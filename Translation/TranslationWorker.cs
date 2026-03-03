using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TalkTranscript.Logging;
using TalkTranscript.Models;

namespace TalkTranscript.Translation;

/// <summary>
/// 非同期翻訳ワーカー。
/// 認識スレッドをブロックせずにバックグラウンドで翻訳を実行する。
///
/// プロデューサー/コンシューマーパターン:
/// - 認識スレッドが Enqueue() でエントリを投入
/// - ワーカースレッドが翻訳を実行し、コールバックで結果を返却
/// </summary>
public sealed class TranslationWorker : IDisposable
{
    private readonly ITranslator _translator;
    private readonly BlockingCollection<TranslationRequest> _queue = new(128);
    private readonly Thread _workerThread;
    private readonly string _translationTarget; // "自分" / "相手" / "両方"
    private readonly int _mergeWindowMs;        // マージウィンドウ (ms), 0=無効
    private volatile bool _stopping;
    private bool _disposed;

    /// <summary>マージバッファに蓄積する最大エントリ数</summary>
    private const int MaxMergeCount = 10;

    /// <summary>文が未完の場合のタイムアウト倍率 (通常マージウィンドウの N 倍)</summary>
    private const int IncompleteSentenceTimeoutMultiplier = 8;

    /// <summary>未完文待機の最低保証時間 (ms)。Whisper の処理が長い場合に対応。</summary>
    private const int MinIncompleteSentenceTimeoutMs = 10_000;

    /// <summary>翻訳完了時に呼ばれるイベント (翻訳済み TranscriptEntry を引数に取る)</summary>
    public event Action<TranscriptEntry>? OnTranslated;

    /// <summary>
    /// 複数フラグメントが結合されたときに発火されるイベント。
    /// 元のエントリリストと、結合後の新エントリを引数に取る。
    /// UI やデータ側でフラグメントを統合するために使用。
    /// </summary>
    public event Action<IReadOnlyList<TranscriptEntry>, TranscriptEntry>? OnEntriesMerged;

    /// <summary>
    /// 翻訳ワーカーを初期化して開始する。
    /// </summary>
    /// <param name="translator">翻訳エンジン</param>
    /// <param name="translationTarget">翻訳対象 ("自分" / "相手" / "両方")</param>
    /// <param name="mergeWindowMs">
    /// マージウィンドウ (ミリ秒)。同一話者の連続フラグメントをこの時間内に
    /// バッファリングし結合翻訳する。0 で無効 (従来動作)。
    /// </param>
    public TranslationWorker(ITranslator translator, string translationTarget = "相手",
                             int mergeWindowMs = 0)
    {
        _translator = translator;
        _translationTarget = translationTarget;
        _mergeWindowMs = Math.Max(0, mergeWindowMs);

        _workerThread = new Thread(WorkerLoop)
        {
            Name = "TranslationWorker",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _workerThread.Start();

        AppLogger.Info($"TranslationWorker 開始 (対象: {translationTarget}, マージ: {_mergeWindowMs}ms)");
    }

    /// <summary>翻訳キューにエントリを投入する (スレッドセーフ)</summary>
    public void Enqueue(TranscriptEntry entry)
    {
        if (_stopping || _disposed) return;
        if (entry.IsBookmark) return;

        // 翻訳対象フィルタ
        if (_translationTarget != "両方")
        {
            if (entry.Speaker != _translationTarget) return;
        }

        // テキストが翻訳元言語らしくない場合はスキップ
        // (例: en→ja モデルに日本語テキストを渡すと <unk> が大量発生する)
        if (!LooksLikeSourceLanguage(entry.Text, _translator.SourceLanguage))
        {
            AppLogger.Debug($"テキストが翻訳元言語 ({_translator.SourceLanguage}) と異なるためスキップ: \"{entry.Text}\"");
            return;
        }

        try
        {
            if (!_queue.TryAdd(new TranslationRequest(entry)))
            {
                AppLogger.Warn("翻訳キューが満杯のため翻訳をスキップしました");
            }
        }
        catch (InvalidOperationException)
        {
            // キューが完了済み
        }
    }

    /// <summary>ワーカーを停止する</summary>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _queue.CompleteAdding();

        // ワーカースレッドの終了を待つ (翻訳中のタスクが完了するまで)
        if (!_workerThread.Join(10_000))
        {
            AppLogger.Warn("TranslationWorker: 停止タイムアウト (10秒)。翻訳を中断します。");
        }
        AppLogger.Info("TranslationWorker 停止");
    }

    private void WorkerLoop()
    {
        if (_mergeWindowMs <= 0)
        {
            // マージ無効: 従来の即時翻訳
            WorkerLoopImmediate();
        }
        else
        {
            // マージ有効: 同一話者の連続フラグメントをバッファリング
            WorkerLoopMerge();
        }
    }

    /// <summary>従来の即時翻訳ループ (マージ無効時)</summary>
    private void WorkerLoopImmediate()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable())
            {
                if (_stopping) break;
                TranslateSingle(request.Entry);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>マージバッファリング翻訳ループ</summary>
    private void WorkerLoopMerge()
    {
        var buffer = new List<TranscriptEntry>();
        string? currentSpeaker = null;

        try
        {
            while (!_stopping)
            {
                bool gotItem;
                TranslationRequest? request = null;

                try
                {
                    int timeout;
                    if (buffer.Count > 0)
                    {
                        // バッファ内のテキストが完結していない = 文の途中 → Whisper の処理時間を考慮して長めに待機
                        bool incomplete = !IsBufferTextComplete(buffer[^1].Text);
                        timeout = incomplete
                            ? Math.Max(_mergeWindowMs * IncompleteSentenceTimeoutMultiplier,
                                       MinIncompleteSentenceTimeoutMs)
                            : _mergeWindowMs;
                    }
                    else
                    {
                        timeout = Timeout.Infinite;
                    }
                    gotItem = _queue.TryTake(out request, timeout);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    // CompleteAdding 呼び出し後に発生
                    break;
                }

                if (gotItem && request != null)
                {
                    var entry = request.Entry;

                    // 話者が変わった or バッファが満杯 or 前の文が完結済み → フラッシュ
                    if (buffer.Count > 0 &&
                        (entry.Speaker != currentSpeaker
                         || buffer.Count >= MaxMergeCount
                         || IsBufferTextComplete(buffer[^1].Text)))
                    {
                        FlushBuffer(buffer);
                        buffer.Clear();
                    }

                    buffer.Add(entry);
                    currentSpeaker = entry.Speaker;
                }
                else
                {
                    // タイムアウト → フラッシュ
                    if (buffer.Count > 0)
                    {
                        FlushBuffer(buffer);
                        buffer.Clear();
                        currentSpeaker = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }

        // 残りをフラッシュ
        if (buffer.Count > 0)
        {
            FlushBuffer(buffer);
        }
    }

    /// <summary>単一エントリを翻訳して結果を通知する</summary>
    private void TranslateSingle(TranscriptEntry entry)
    {
        try
        {
            string? translated = _translator.Translate(entry.Text);
            if (!string.IsNullOrEmpty(translated))
            {
                var translatedEntry = entry with { TranslatedText = translated };
                OnTranslated?.Invoke(translatedEntry);
            }
            else
            {
                AppLogger.Debug($"翻訳結果が空: \"{entry.Text}\"");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"翻訳ワーカーエラー: {ex.Message}");
        }
    }

    /// <summary>バッファ内のエントリを結合翻訳して各エントリに分配する</summary>
    private void FlushBuffer(List<TranscriptEntry> buffer)
    {
        if (buffer.Count == 0) return;

        if (buffer.Count == 1)
        {
            TranslateSingle(buffer[0]);
            return;
        }

        // テキストを結合して一括翻訳
        string merged = string.Join(" ", buffer.Select(e => e.Text));
        string? translated = null;

        try
        {
            translated = _translator.Translate(merged);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"結合翻訳エラー (個別にフォールバック): {ex.Message}");
        }

        if (string.IsNullOrEmpty(translated))
        {
            // フォールバック: 個別翻訳
            foreach (var entry in buffer)
                TranslateSingle(entry);
            return;
        }

        AppLogger.Debug($"結合翻訳: {buffer.Count}件のフラグメントを結合 " +
                        $"({merged.Length}文字 → {translated.Length}文字)");

        // 結合エントリを作成: 最後のタイムスタンプを保持し、テキストを結合
        var mergedEntry = buffer[^1] with { Text = merged, TranslatedText = translated };

        if (OnEntriesMerged != null)
        {
            // 認識エントリ自体も統合する
            var originals = buffer.ToList().AsReadOnly();
            OnEntriesMerged.Invoke(originals, mergedEntry);
        }
        else
        {
            // イベントハンドラが未登録なら従来動作 (翻訳のみ通知)
            OnTranslated?.Invoke(mergedEntry);
        }
    }

    /// <summary>
    /// 翻訳テキストを文レベルで分割し、各エントリに比例配分する。
    /// ソーステキストの長さの比率に基づいて翻訳文を分配する。
    /// </summary>
    internal static List<string> SplitTranslation(string translatedText, List<TranscriptEntry> entries)
    {
        if (entries.Count <= 1)
            return new List<string> { translatedText };

        // 文ごとに分割 (日本語・英語・中国語の句読点に対応)
        var sentences = SplitIntoSentences(translatedText);

        if (sentences.Count >= entries.Count)
        {
            // 文数 >= エントリ数 → ソーステキスト長さの比率で文を分配
            return DistributeSentences(sentences, entries);
        }

        // 文数 < エントリ数 → ソーステキスト長さの比率で文字レベル分割
        return SplitByCharProportion(translatedText, entries);
    }

    /// <summary>テキストを文単位で分割する</summary>
    internal static List<string> SplitIntoSentences(string text)
    {
        // 文末記号で分割 (記号は直前の文に含める)
        var sentences = new List<string>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if ("。！？.!?".IndexOf(text[i]) >= 0)
            {
                sentences.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        // 残りのテキスト
        if (start < text.Length)
        {
            string remainder = text[start..].Trim();
            if (!string.IsNullOrEmpty(remainder))
                sentences.Add(remainder);
        }

        return sentences;
    }

    /// <summary>文のリストをソーステキスト長さの比率に基づいてエントリに分配する</summary>
    private static List<string> DistributeSentences(List<string> sentences, List<TranscriptEntry> entries)
    {
        int totalSourceLen = entries.Sum(e => e.Text.Length);
        var result = new List<string>();
        int sentenceIdx = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            if (i == entries.Count - 1)
            {
                // 最後のエントリに残り全ての文を割り当て
                result.Add(string.Join("", sentences.Skip(sentenceIdx)));
            }
            else
            {
                // ソーステキスト長さの比率から、このエントリに割り当てる文数を算出
                double ratio = (double)entries[i].Text.Length / totalSourceLen;
                int targetSentences = Math.Max(1,
                    (int)Math.Round(ratio * sentences.Count));

                // 残りのエントリに少なくとも 1 文ずつ残す
                int remaining = entries.Count - i - 1;
                int available = sentences.Count - sentenceIdx - remaining;
                targetSentences = Math.Min(targetSentences, available);
                targetSentences = Math.Max(1, targetSentences);

                result.Add(string.Join("",
                    sentences.Skip(sentenceIdx).Take(targetSentences)));
                sentenceIdx += targetSentences;
            }
        }

        return result;
    }

    /// <summary>文字レベルで比例分割する (文分割できない場合のフォールバック)</summary>
    private static List<string> SplitByCharProportion(string text, List<TranscriptEntry> entries)
    {
        int totalSourceLen = entries.Sum(e => e.Text.Length);
        var result = new List<string>();
        int charIdx = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            if (i == entries.Count - 1)
            {
                // 最後のエントリに残り全てを割り当て
                result.Add(text[charIdx..]);
            }
            else
            {
                double ratio = (double)entries[i].Text.Length / totalSourceLen;
                int charCount = (int)Math.Round(ratio * text.Length);
                charCount = Math.Max(1, Math.Min(charCount, text.Length - charIdx - 1));

                // 空白境界にスナップ (前後 5 文字以内)
                int end = charIdx + charCount;
                int bestEnd = end;
                for (int d = 0; d <= 5 && end + d < text.Length; d++)
                {
                    if (char.IsWhiteSpace(text[end + d]) || "、，。".IndexOf(text[end + d]) >= 0)
                    { bestEnd = end + d + 1; break; }
                }
                if (bestEnd == end)
                {
                    for (int d = 1; d <= 5 && end - d > charIdx; d++)
                    {
                        if (char.IsWhiteSpace(text[end - d]) || "、，。".IndexOf(text[end - d]) >= 0)
                        { bestEnd = end - d + 1; break; }
                    }
                }

                result.Add(text[charIdx..bestEnd]);
                charIdx = bestEnd;
            }
        }

        return result;
    }

    /// <summary>
    /// テキストが指定した翻訳元言語らしいかを簡易判定する。
    /// CJK 文字 (ひらがな/カタカナ/漢字/ハングル) の比率で判定し、
    /// 翻訳元言語と明らかに異なるテキストをフィルタする。
    /// </summary>
    internal static bool LooksLikeSourceLanguage(string text, string sourceLang)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 句読点・空白・記号を除いた文字数でカウント
        int total = 0;
        int cjkCount = 0;     // 漢字・ひらがな・カタカナ
        int hangulCount = 0;  // ハングル

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c))
                continue;
            total++;

            // ひらがな U+3040-309F, カタカナ U+30A0-30FF, CJK統合漢字 U+4E00-9FFF,
            // CJK互換漢字 U+F900-FAFF, CJK拡張A U+3400-4DBF
            if ((c >= '\u3040' && c <= '\u309F') ||
                (c >= '\u30A0' && c <= '\u30FF') ||
                (c >= '\u4E00' && c <= '\u9FFF') ||
                (c >= '\uF900' && c <= '\uFAFF') ||
                (c >= '\u3400' && c <= '\u4DBF'))
            {
                cjkCount++;
            }
            // ハングル U+AC00-D7A3, ハングル字母 U+1100-11FF, U+3130-318F
            else if ((c >= '\uAC00' && c <= '\uD7A3') ||
                     (c >= '\u1100' && c <= '\u11FF') ||
                     (c >= '\u3130' && c <= '\u318F'))
            {
                hangulCount++;
            }
        }

        if (total == 0) return false;

        double cjkRatio = (double)cjkCount / total;
        double hangulRatio = (double)hangulCount / total;

        return sourceLang.ToLowerInvariant() switch
        {
            // 日本語ソース: テキストに CJK 文字が 30% 以上含まれるべき
            "ja" => cjkRatio >= 0.3,
            // 中国語ソース: テキストに CJK 文字 (漢字) が 30% 以上含まれるべき
            "zh" => cjkRatio >= 0.3,
            // 韓国語ソース: テキストにハングルが 30% 以上含まれるべき
            "ko" => hangulRatio >= 0.3,
            // ラテン文字系言語 (en, fr, de, es 等): CJK/ハングルが 30% 未満であるべき
            _ => (cjkRatio + hangulRatio) < 0.3,
        };
    }

    /// <summary>
    /// バッファ内テキストが「翻訳に十分な完結度」を持つかを判定する。
    /// 文末記号で終わっている場合だけでなく、文中に完結した文を含む場合も
    /// 完結とみなす (Whisper が末尾の句読点を省略するケースに対応)。
    /// </summary>
    internal static bool IsBufferTextComplete(string text)
    {
        if (EndsWithSentenceBoundary(text)) return true;
        return HasInternalSentenceBoundary(text);
    }

    /// <summary>
    /// テキスト中に文末記号の後にさらにテキストが続くパターンがあるかを判定する。
    /// 例: "Sure. I can talk about anything" → true ("." の後にテキストがある)
    /// 例: "and you'll be able to see exactly" → false (句読点がない)
    /// </summary>
    internal static bool HasInternalSentenceBoundary(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (int i = 0; i < text.Length - 1; i++)
        {
            if ("。！？.!?".IndexOf(text[i]) >= 0)
            {
                // 句読点の後に空白以外の文字があるか
                for (int j = i + 1; j < text.Length; j++)
                {
                    if (!char.IsWhiteSpace(text[j]))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// テキストが文末記号で終わっているかを判定する。
    /// 文の途中で分割されたフラグメントを検出し、マージ待機を延長するために使用。
    /// 英語 (.!?)、日本語 (。！？)、閉じ括弧 (」』）) などに対応。
    /// </summary>
    internal static bool EndsWithSentenceBoundary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true; // 空テキストは完了扱い

        // 末尾の空白を除いた最後の文字を取得
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) continue;

            // 文末記号: 英語/日本語/中国語の句読点、閉じ括弧類
            return "。！？.!?」』）)…♪".IndexOf(c) >= 0;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _queue.Dispose();
    }

    private record TranslationRequest(TranscriptEntry Entry);
}
