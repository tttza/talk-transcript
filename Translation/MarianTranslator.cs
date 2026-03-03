using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TalkTranscript.Logging;

namespace TalkTranscript.Translation;

/// <summary>
/// ONNX Runtime + Helsinki-NLP MarianMT モデルによるローカル翻訳エンジン。
///
/// vocab.json による正確なトークン ID マッピングと
/// SentencePiece Viterbi トークナイザーを使用し、
/// ONNX の Encoder/Decoder + KV キャッシュで Beam Search デコーディングを行う。
/// DirectML GPU/CPU 自動切替に対応。
/// </summary>
public sealed class MarianTranslator : ITranslator
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;          // Step 0: decoder_model.onnx (KVキャッシュ生成)
    private readonly InferenceSession _decoderWithPast;  // Step 1+: decoder_with_past_model.onnx (KVキャッシュ使用)
    private readonly SessionOptions _sessionOptions;
    private readonly int _numDecoderLayers;

    // vocab.json から読み込んだ正確な token→id / id→token マッピング
    private readonly Dictionary<string, int> _vocab;        // token string → model id
    private readonly Dictionary<int, string> _revVocab;     // model id → token string

    // SPM Viterbi トークナイゼーション用データ
    private readonly List<string> _spmPieces;               // SPM ピース文字列
    private readonly List<float> _spmScores;                // SPM ピーススコア (log-prob)
    private readonly Dictionary<string, int> _pieceToModelId; // SPM ピース → model id

    private readonly int _eosId;
    private readonly int _unkId;
    private readonly int _padId;
    private readonly int _decoderStartId;
    private readonly int _maxLength;
    private readonly int _numBeams;

    private readonly object _lock = new();
    private volatile bool _disposed;

    public bool IsReady { get; } = true;
    public bool UsingGpu { get; private set; }
    public string SourceLanguage { get; }
    public string TargetLanguage { get; }

    /// <summary>
    /// MarianMT ONNX モデルをロードして翻訳エンジンを初期化する。
    /// </summary>
    /// <param name="modelDir">モデルファイルが格納されたディレクトリ</param>
    /// <param name="sourceLang">翻訳元言語コード</param>
    /// <param name="targetLang">翻訳先言語コード</param>
    /// <param name="useGpu">GPU を使用するか</param>
    /// <param name="maxLength">最大出力トークン数</param>
    /// <param name="numBeams">Beam Search のビーム数 (1=Greedy, 4=MarianMT標準)</param>
    public MarianTranslator(string modelDir, string sourceLang, string targetLang,
                            bool useGpu = false, int maxLength = 128, int numBeams = 4)
    {
        SourceLanguage = sourceLang;
        TargetLanguage = targetLang;
        _maxLength = maxLength;
        _numBeams = numBeams;

        // ── vocab.json のロード (正確な token ↔ model ID マッピング) ──
        string vocabPath = Path.Combine(modelDir, "vocab.json");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException("vocab.json が見つかりません。翻訳モデルを再ダウンロードしてください。", vocabPath);

        var vocabJson = File.ReadAllText(vocabPath, System.Text.Encoding.UTF8);
        _vocab = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(vocabJson)
                 ?? throw new InvalidOperationException("vocab.json のパースに失敗しました。");
        _revVocab = new Dictionary<int, string>();
        foreach (var kv in _vocab)
        {
            _revVocab.TryAdd(kv.Value, kv.Key);
        }

        // 特殊トークン ID
        _eosId = _vocab.GetValueOrDefault("</s>", 0);
        _unkId = _vocab.GetValueOrDefault("<unk>", 1);
        _padId = _vocab.GetValueOrDefault("<pad>", 60715);

        // config.json から decoder_start_token_id, decoder_layers を取得
        _numDecoderLayers = 6;
        LoadModelConfig(modelDir, out _, out int configDecoderStart, out _numDecoderLayers);
        // config.json に decoder_start_token_id がない場合 pad_token_id を使用 (MarianMT 規約)
        _decoderStartId = configDecoderStart >= 0 ? configDecoderStart : _padId;
        AppLogger.Debug($"MarianMT config: eos={_eosId}, unk={_unkId}, pad={_padId}, start={_decoderStartId}, layers={_numDecoderLayers}");

        // ── SPM Viterbi トークナイゼーション用データのロード ──
        (_spmPieces, _spmScores) = ParseSpmFull(Path.Combine(modelDir, "source.spm"));
        _pieceToModelId = new Dictionary<string, int>();
        for (int i = 0; i < _spmPieces.Count; i++)
        {
            if (_vocab.TryGetValue(_spmPieces[i], out int modelId))
                _pieceToModelId.TryAdd(_spmPieces[i], modelId);
        }
        AppLogger.Debug($"SPM pieces mapped to vocab: {_pieceToModelId.Count}/{_spmPieces.Count}");

        // ── ONNX Session の初期化 ──
        _sessionOptions = CreateSessionOptions(useGpu, out bool gpuActivated);
        UsingGpu = gpuActivated;
        string encoderPath = Path.Combine(modelDir, "encoder_model.onnx");
        string decoderPath = Path.Combine(modelDir, "decoder_model.onnx");
        string decoderWithPastPath = Path.Combine(modelDir, "decoder_with_past_model.onnx");

        try
        {
            _encoder = new InferenceSession(encoderPath, _sessionOptions);
            _decoder = new InferenceSession(decoderPath, _sessionOptions);
            _decoderWithPast = new InferenceSession(decoderWithPastPath, _sessionOptions);
        }
        catch
        {
            _encoder?.Dispose();
            _decoder?.Dispose();
            _decoderWithPast?.Dispose();
            _sessionOptions.Dispose();
            throw;
        }

        AppLogger.Info($"MarianTranslator 初期化完了: {sourceLang}→{targetLang} " +
                       $"(GPU: {UsingGpu}, vocab: {_vocab.Count}, layers: {_numDecoderLayers})");
    }

    /// <summary>テキストを翻訳する</summary>
    public string? Translate(string text)
    {
        if (_disposed) return null;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length < 2) return null; // 1文字は翻訳不要

        try
        {
            lock (_lock)
            {
                if (_disposed) return null;
                var result = TranslateInternal(text);
                AppLogger.Debug($"翻訳完了: {text.Length}文字 → {result.Length}文字");
                return result;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"翻訳エラー ({SourceLanguage}→{TargetLanguage}): {ex.Message}", ex);
            return null;
        }
    }

    private string TranslateInternal(string text)
    {
        // ── 1. Viterbi トークナイズ + vocab.json ID マッピング ──
        var tokenIds = TokenizeViterbi(text);
        tokenIds.Add(_eosId); // </s> を末尾に追加

        int seqLen = tokenIds.Count;
        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLen });
        for (int i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = tokenIds[i];
            attentionMask[0, i] = 1;
        }

        // ── 2. Encoder 実行 ──
        var encoderInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };

        using var encoderResults = _encoder.Run(encoderInputs);
        var encoderHiddenStates = encoderResults.First().AsTensor<float>().Clone();

        // ── 3. Step 0: decoder_model.onnx → logits + KV キャッシュ初期化 ──
        var step0Ids = new DenseTensor<long>(new[] { 1, 1 });
        step0Ids[0, 0] = _decoderStartId;

        var step0Inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", step0Ids),
            NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates),
        };

        int numBeams = _numBeams;
        const float lengthPenalty = 1.0f;
        int kvCount = _numDecoderLayers * 2; // key + value per layer

        (int tokenId, float logProb)[] step0Top;
        var encoderKv = new Tensor<float>[kvCount];        // 全ビーム共有 (不変)
        var initialDecoderKv = new Tensor<float>[kvCount]; // Step 0 の decoder KV

        using (var step0Results = _decoder.Run(step0Inputs))
        {
            var logits0 = step0Results.First(o => o.Name == "logits").AsTensor<float>();
            step0Top = TopKLogProbs(logits0, 0, 0, logits0.Dimensions[2], numBeams);

            for (int layer = 0; layer < _numDecoderLayers; layer++)
            {
                encoderKv[layer * 2] = step0Results
                    .First(o => o.Name == $"present.{layer}.encoder.key").AsTensor<float>().Clone();
                encoderKv[layer * 2 + 1] = step0Results
                    .First(o => o.Name == $"present.{layer}.encoder.value").AsTensor<float>().Clone();
                initialDecoderKv[layer * 2] = step0Results
                    .First(o => o.Name == $"present.{layer}.decoder.key").AsTensor<float>().Clone();
                initialDecoderKv[layer * 2 + 1] = step0Results
                    .First(o => o.Name == $"present.{layer}.decoder.value").AsTensor<float>().Clone();
            }
        }

        // ── 4. Step 0 の logits から初期ビームを生成 ──
        var activeBeams = new List<(List<int> tokens, float score, Tensor<float>[] decoderKv)>();
        var finishedBeams = new List<(List<int> tokens, float normalizedScore)>();

        foreach (var (tokenId, logProb) in step0Top)
        {
            if (tokenId < 0) continue;
            if (tokenId == _eosId)
            {
                finishedBeams.Add((new List<int> { _decoderStartId, tokenId }, logProb));
                continue;
            }
            // 全ビームが同じ初期 decoder KV を共有 (ORT は read-only でアクセスするためクローン不要)
            activeBeams.Add((
                new List<int> { _decoderStartId, tokenId },
                logProb,
                initialDecoderKv));
        }

        // ── 5. Steps 1+: decoder_with_past_model.onnx (KV キャッシュ使用) ──
        // 各ステップで 1 トークンのみデコーダに渡し、KV キャッシュで過去のコンテキストを維持。
        // KV キャッシュなしの O(n²) から O(n) に改善。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int timeoutMs = 15_000;

        for (int step = 1; step < _maxLength; step++)
        {
            if (_disposed || activeBeams.Count == 0) break;
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                AppLogger.Warn($"翻訳タイムアウト (step={step}, {sw.ElapsedMilliseconds}ms)");
                break;
            }

            var stepNewKv = new Tensor<float>[activeBeams.Count][];
            var allCandidates = new List<(List<int> tokens, float score, int parentIdx)>();

            for (int bIdx = 0; bIdx < activeBeams.Count; bIdx++)
            {
                var beam = activeBeams[bIdx];
                int lastToken = beam.tokens[^1];

                var decIds = new DenseTensor<long>(new[] { 1, 1 });
                decIds[0, 0] = lastToken;

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", decIds),
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMask),
                };

                // Past KV: decoder (ビームごとに異なる) + encoder (全ビーム共有)
                for (int layer = 0; layer < _numDecoderLayers; layer++)
                {
                    inputs.Add(NamedOnnxValue.CreateFromTensor(
                        $"past_key_values.{layer}.decoder.key", beam.decoderKv[layer * 2]));
                    inputs.Add(NamedOnnxValue.CreateFromTensor(
                        $"past_key_values.{layer}.decoder.value", beam.decoderKv[layer * 2 + 1]));
                    inputs.Add(NamedOnnxValue.CreateFromTensor(
                        $"past_key_values.{layer}.encoder.key", encoderKv[layer * 2]));
                    inputs.Add(NamedOnnxValue.CreateFromTensor(
                        $"past_key_values.{layer}.encoder.value", encoderKv[layer * 2 + 1]));
                }

                using var result = _decoderWithPast.Run(inputs);
                var logits = result.First(o => o.Name == "logits").AsTensor<float>();
                int vSize = logits.Dimensions[2];

                // 更新された decoder KV を保存 (seq_len が +1 成長)
                var newDecKv = new Tensor<float>[kvCount];
                for (int layer = 0; layer < _numDecoderLayers; layer++)
                {
                    newDecKv[layer * 2] = result
                        .First(o => o.Name == $"present.{layer}.decoder.key").AsTensor<float>().Clone();
                    newDecKv[layer * 2 + 1] = result
                        .First(o => o.Name == $"present.{layer}.decoder.value").AsTensor<float>().Clone();
                }
                stepNewKv[bIdx] = newDecKv;

                // Top 2*numBeams 候補
                var topTokens = TopKLogProbs(logits, 0, 0, vSize, 2 * numBeams);

                foreach (var (tokenId, logProb) in topTokens)
                {
                    if (tokenId < 0) continue;
                    float newScore = beam.score + logProb;
                    var newTokens = new List<int>(beam.tokens) { tokenId };

                    if (tokenId == _eosId)
                    {
                        int outLen = newTokens.Count - 1;
                        float normScore = newScore / MathF.Pow(outLen, lengthPenalty);
                        finishedBeams.Add((newTokens, normScore));
                    }
                    else
                    {
                        allCandidates.Add((newTokens, newScore, bIdx));
                    }
                }
            }

            // 上位 numBeams を選択し、対応する decoder KV を引き継ぐ
            allCandidates.Sort((a, b) => b.score.CompareTo(a.score));
            int selectCount = Math.Min(numBeams, allCandidates.Count);
            var newActiveBeams = new List<(List<int> tokens, float score, Tensor<float>[] decoderKv)>(selectCount);
            for (int i = 0; i < selectCount; i++)
            {
                var cand = allCandidates[i];
                newActiveBeams.Add((cand.tokens, cand.score, stepNewKv[cand.parentIdx]));
            }
            activeBeams = newActiveBeams;

            // Early stopping: 完了ビームがアクティブビームの最良スコアを超えている場合
            if (finishedBeams.Count >= numBeams && activeBeams.Count > 0)
            {
                float bestFinished = finishedBeams.Max(f => f.normalizedScore);
                float bestActiveNorm = activeBeams[0].score
                    / MathF.Pow(Math.Max(1, activeBeams[0].tokens.Count - 1), lengthPenalty);
                if (bestFinished >= bestActiveNorm) break;
            }
        }

        // ── 6. 最良ビームを選択 & デトークナイズ ──
        List<int> bestOutputTokens;
        if (finishedBeams.Count > 0)
            bestOutputTokens = finishedBeams.OrderByDescending(f => f.normalizedScore).First().tokens;
        else if (activeBeams.Count > 0)
            bestOutputTokens = activeBeams[0].tokens;
        else
            return "";

        var resultTokens = bestOutputTokens.Skip(1).Where(t => t != _eosId).ToList();
        return Detokenize(resultTokens, _revVocab);
    }

    /// <summary>
    /// SentencePiece Viterbi アルゴリズムでテキストをトークナイズし、
    /// vocab.json の model ID を返す。
    /// </summary>
    private List<int> TokenizeViterbi(string text)
    {
        // SentencePiece の ▁ (U+2581) プレフィクスに変換
        string input = "▁" + text.Replace(" ", "▁");
        int n = input.Length;

        // DP テーブル
        var bestScore = new float[n + 1];
        var bestEdge = new (int start, int modelId)[n + 1];
        Array.Fill(bestScore, float.NegativeInfinity);
        bestScore[0] = 0;

        // SPM ピースルックアップ (特殊トークンをスキップ)
        var pieceLookup = new Dictionary<string, (int spmIdx, float score)>();
        for (int i = 3; i < _spmPieces.Count; i++) // 0=<unk>, 1=<s>, 2=</s> はスキップ
            pieceLookup.TryAdd(_spmPieces[i], (i, _spmScores[i]));

        for (int end = 1; end <= n; end++)
        {
            int maxLen = Math.Min(end, 64);
            for (int len = 1; len <= maxLen; len++)
            {
                int start = end - len;
                string sub = input.Substring(start, len);
                if (pieceLookup.TryGetValue(sub, out var entry)
                    && _pieceToModelId.TryGetValue(sub, out int modelId))
                {
                    float newScore = bestScore[start] + entry.score;
                    if (newScore > bestScore[end])
                    {
                        bestScore[end] = newScore;
                        bestEdge[end] = (start, modelId);
                    }
                }
            }

            // ピースが見つからない場合は <unk> として 1 文字進む
            if (float.IsNegativeInfinity(bestScore[end]))
            {
                bestScore[end] = bestScore[end - 1] - 20f; // 重いペナルティ
                bestEdge[end] = (end - 1, _unkId);
            }
        }

        // バックトラックで結果を構築
        var result = new List<int>();
        int pos = n;
        while (pos > 0)
        {
            result.Add(bestEdge[pos].modelId);
            pos = bestEdge[pos].start;
        }
        result.Reverse();
        return result;
    }

    /// <summary>トークン ID 列をテキストにデコードする</summary>
    private string Detokenize(List<int> tokenIds, Dictionary<int, string> revVocab)
    {
        var sb = new System.Text.StringBuilder();
        foreach (int id in tokenIds)
        {
            if (id == _unkId) continue; // <unk> トークンをスキップ
            if (revVocab.TryGetValue(id, out string? piece))
                sb.Append(piece);
        }
        // ◁ をスペースに戻し、先頭のスペースを除去、残存 <unk> を除去
        return sb.ToString().Replace("▁", " ").Replace("<unk>", "").TrimStart();
    }

    /// <summary>    /// logits の指定位置から Log-Softmax を計算し、Top-K の (tokenId, logProb) を返す。
    /// pad/start トークンは生成対象から除外する (HuggingFace 互換).
    /// </summary>
    private (int tokenId, float logProb)[] TopKLogProbs(
        Tensor<float> logits, int batchIdx, int seqPos, int vocabSize, int k)
    {
        // 数値安定 Log-Softmax
        float maxLogit = float.MinValue;
        for (int v = 0; v < vocabSize; v++)
        {
            if (v == _decoderStartId) continue;
            float l = logits[batchIdx, seqPos, v];
            if (l > maxLogit) maxLogit = l;
        }
        double sumExp = 0;
        for (int v = 0; v < vocabSize; v++)
        {
            if (v == _decoderStartId) continue;
            sumExp += Math.Exp(logits[batchIdx, seqPos, v] - maxLogit);
        }
        float logZ = maxLogit + (float)Math.Log(sumExp);

        // Top-K 抽出 (挿入ソート)
        var topK = new (int id, float logProb)[k];
        for (int i = 0; i < k; i++) topK[i] = (-1, float.MinValue);

        for (int v = 0; v < vocabSize; v++)
        {
            if (v == _decoderStartId) continue;
            if (v == _unkId) continue; // <unk> トークンを生成候補から除外
            float logProb = logits[batchIdx, seqPos, v] - logZ;
            if (logProb > topK[k - 1].logProb)
            {
                topK[k - 1] = (v, logProb);
                for (int i = k - 1; i > 0 && topK[i].logProb > topK[i - 1].logProb; i--)
                    (topK[i], topK[i - 1]) = (topK[i - 1], topK[i]);
            }
        }

        return topK;
    }

    /// <summary>    /// SentencePiece .spm (Protocol Buffers) からピース文字列とスコアを抽出する。
    /// Viterbi トークナイゼーションに必要。
    /// </summary>
    private static (List<string> pieces, List<float> scores) ParseSpmFull(string spmPath)
    {
        var pieces = new List<string>();
        var scores = new List<float>();
        var data = File.ReadAllBytes(spmPath);
        int idx = 0;

        while (idx < data.Length)
        {
            int fieldTag = ReadVarint(data, ref idx);
            int fieldNumber = fieldTag >> 3;
            int wireType = fieldTag & 0x7;

            if (wireType == 2) // length-delimited
            {
                int length = ReadVarint(data, ref idx);
                if (length < 0 || idx + length > data.Length) break;
                if (fieldNumber == 1) // ModelProto.pieces
                {
                    var (piece, score, _) = ParseSentencePieceFull(data, idx, length);
                    pieces.Add(piece);
                    scores.Add(score);
                }
                idx += length;
            }
            else if (wireType == 0) { ReadVarint(data, ref idx); }
            else if (wireType == 1) { idx += 8; }
            else if (wireType == 5) { idx += 4; }
            else { break; }
        }

        return (pieces, scores);
    }

    /// <summary>SentencePiece メッセージからピース文字列とスコアを抽出する</summary>
    private static (string piece, float score, int type) ParseSentencePieceFull(
        byte[] data, int offset, int length)
    {
        int end = offset + length;
        int idx = offset;
        string piece = "";
        float score = 0f;
        int type = 1; // NORMAL

        while (idx < end)
        {
            int fieldTag = ReadVarint(data, ref idx);
            int fieldNumber = fieldTag >> 3;
            int wireType = fieldTag & 0x7;

            if (wireType == 2) // length-delimited
            {
                int len = ReadVarint(data, ref idx);
                if (len < 0 || idx + len > end) break;
                if (fieldNumber == 1) // piece string
                    piece = System.Text.Encoding.UTF8.GetString(data, idx, len);
                idx += len;
            }
            else if (wireType == 0) // varint
            {
                int val = ReadVarint(data, ref idx);
                if (fieldNumber == 3) type = val;
            }
            else if (wireType == 5) // 32-bit (float)
            {
                if (fieldNumber == 2)
                    score = BitConverter.ToSingle(data, idx);
                idx += 4;
            }
            else if (wireType == 1) { idx += 8; }
            else { break; }
        }

        return (piece, score, type);
    }

    /// <summary>Protocol Buffers varint を読み取る</summary>
    private static int ReadVarint(byte[] data, ref int idx)
    {
        int result = 0;
        int shift = 0;
        while (idx < data.Length && shift < 35) // int のビット幅を超えないようガード
        {
            byte b = data[idx++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static SessionOptions CreateSessionOptions(bool useGpu, out bool gpuActivated)
    {
        gpuActivated = false;
        var options = new SessionOptions();
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        // デコーダは呼び出しごとにシーケンス長が変わるため、メモリパターンを無効化
        options.EnableMemoryPattern = false;
        // ネイティブ stderr へのログ出力を抑制 (Live UI 表示と干渉するため)
        options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL;

        if (useGpu)
        {
            try
            {
                options.AppendExecutionProvider_DML(0);
                gpuActivated = true;
                AppLogger.Info("翻訳エンジン: DirectML Execution Provider を使用");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"DirectML EP の初期化に失敗、CPU にフォールバック: {ex.Message}");
                // CPU フォールバック (デフォルトで CPU EP が設定される)
            }
        }

        return options;
    }

    /// <summary>
    /// config.json からモデル設定を読み取る。
    /// ファイルがない場合は MarianMT のデフォルト値を使用。
    /// </summary>
    private static void LoadModelConfig(string modelDir,
        out int eosId, out int decoderStartId, out int numDecoderLayers)
    {
        // デフォルト値 (Helsinki-NLP opus-mt 標準)
        eosId = 0;
        decoderStartId = -1; // -1 = config.json に記載なし (呼び出し元で pad_token_id にフォールバック)
        numDecoderLayers = 6;

        string configPath = Path.Combine(modelDir, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("eos_token_id", out var eid))
                    eosId = eid.GetInt32();
                if (root.TryGetProperty("decoder_start_token_id", out var dsid))
                    decoderStartId = dsid.GetInt32();
                if (root.TryGetProperty("decoder_layers", out var dl))
                    numDecoderLayers = dl.GetInt32();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"config.json の解析に失敗 (デフォルト値を使用): {ex.Message}");
            }
        }
        else
        {
            AppLogger.Warn("​config.json が見つかりません。MarianMT デフォルト値を使用");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _encoder.Dispose();
        _decoder.Dispose();
        _decoderWithPast.Dispose();
        _sessionOptions.Dispose();
    }
}
