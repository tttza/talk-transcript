using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Audio;
using TalkTranscript.Logging;
using TalkTranscript.Models;
using Whisper.net;
using Whisper.net.SamplingStrategy;

namespace TalkTranscript.Transcribers;

/// <summary>
/// Whisper.net でマイクとスピーカーを「準リアルタイム」に文字起こしする。
///
/// Whisper はストリーミング API を持たないため、VAD (音声区間検出) で
/// 発話の切れ目を検知し、そのタイミングでバッファを処理する。
/// また前回の認識結果を initial_prompt として渡し、
/// チャンク間の文脈連続性を保つ。
///
/// マイクとスピーカーで独立した認識スレッドを持つため同時発話も対応。
/// CPU だけで動作する。tiny モデルなら十分リアルタイムに近い速度が出る。
/// </summary>
public sealed class WhisperCallTranscriber : ICallTranscriber
{
    // ── Whisper ──
    private readonly WhisperFactory _factory;
    private readonly string _modelSize;
    private readonly string _language;

    // ── キャプチャ ──
    private readonly WaveInEvent _micCapture;
    private readonly WasapiLoopbackCapture _speakerCapture;

    // ── 音声バッファ ──
    private MemoryStream _micBuffer = new();
    private MemoryStream _speakerBuffer = new();
    private readonly object _micBufLock = new();
    private readonly object _spkBufLock = new();

    // ── 録音全体 (Whisper 後処理用) ──
    private readonly RecordingBuffer _micRecording;
    private readonly RecordingBuffer _speakerRecording;

    // ── 結果格納 ──
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _lock = new();

    // ── 制御 ──
    private bool _disposed;
    private volatile bool _stopping;
    private WaveFormat? _loopbackFormat;
    private Thread? _micProcessThread;
    private Thread? _spkProcessThread;

    // ── VAD (音声区間検出) ──
    private DateTime _micLastVoiceTime = DateTime.MinValue;
    private DateTime _spkLastVoiceTime = DateTime.MinValue;

    // ── VAD エネルギー履歴 (スライディングウィンドウ) ──
    private readonly Queue<float> _micEnergyHistory = new();
    private readonly Queue<float> _spkEnergyHistory = new();
    private const int EnergyHistorySize = 10;

    // ── Prompt 引き継ぎ (文脈連続性) ──
    private string _micLastPrompt = "";
    private string _spkLastPrompt = "";
    private const int MaxPromptLength = 200;

    /// <summary>
    /// 言語に応じた初期プロンプト。
    /// Whisper の initial_prompt に目的のドメインを示す短いテキストを入れると
    /// 最初のチャンクから適切な言語・文体で認識される。
    /// </summary>
    private static string GetInitialPrompt(string language) => language switch
    {
        "ja" => "会議の文字起こしです。",
        "en" => "This is a meeting transcript.",
        _ => ""
    };

    // ── 重複検出用 ──
    private string _micLastText = "";
    private string _spkLastText = "";

    // ── チャンク間オーバーラップ ──
    private byte[]? _micOverlap;
    private byte[]? _spkOverlap;
    /// <summary>次のチャンクに引き継ぐオーバーラップ秒数</summary>
    private const double OverlapSec = 0.5;

    // ── アダプティブノイズゲート (#8) ──
    private readonly AdaptiveNoiseGate _micNoiseGate = new();
    private readonly AdaptiveNoiseGate _spkNoiseGate = new();

    // ── 音量通知 (#2) ──
    private volatile float _lastMicPeak;
    private volatile float _lastSpkPeak;

    // ── バックプレッシャー制御 ──
    private readonly BackpressureMonitor _micBackpressure;
    private readonly BackpressureMonitor _spkBackpressure;

    // ── 設定 ──
    /// <summary>無音と判定するピーク閾値 (チャンク単位)</summary>
    private const short SilenceThreshold = 250;
    /// <summary>発話終了とみなす無音持続時間 (ms)</summary>
    private const int SilenceTimeoutMs = 700;
    /// <summary>バッファ全体の RMS がこの値未満なら実質無音とみなしスキップ (Whisper ハルシネーション防止)</summary>
    private const float MinRmsEnergy = 200f;
    /// <summary>最小バッファ秒数 (短すぎるチャンクは精度が低い) — BackpressureMonitor で動的に調整される</summary>
    private const double DefaultMinBufferSec = 2.0;
    /// <summary>最大バッファ秒数 (長い発話でもここで強制処理) — BackpressureMonitor で動的に調整される</summary>
    private const double DefaultMaxBufferSec = 12.0;
    /// <summary>短い発話を救済する長い無音判定時間 (ms)。バッファが最小長未満でもこの時間無音が続けば処理する</summary>
    private const int LongSilenceMs = 2000;
    /// <summary>エネルギーベースの VAD 閾値 (平均 RMS がこの値を超えたら音声あり)</summary>
    private const float VadEnergyThreshold = 180f;

    // ── 統計 ──
    private int _micChunks;
    private int _speakerChunks;

    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    public IReadOnlyList<TranscriptEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public IReadOnlyList<TranscriptEntry> MicEntries
    {
        get { lock (_lock) return _entries.Where(e => e.Speaker == "自分").ToList(); }
    }

    public IReadOnlyList<TranscriptEntry> SpeakerEntries
    {
        get { lock (_lock) return _entries.Where(e => e.Speaker == "相手").ToList(); }
    }

    public event Action<TranscriptEntry>? OnTranscribed;

    /// <summary>音量レベルが更新されたときに発火 (micPeak, speakerPeak)</summary>
    public event Action<float, float>? OnVolumeUpdated;

    /// <summary>Whisper 処理開始時に発火 (speaker, durationSec)</summary>
    public event Action<string, double>? OnProcessingStarted;

    /// <summary>Whisper 処理完了時に発火 (speaker)</summary>
    public event Action<string>? OnProcessingCompleted;

    /// <summary>Whisper 推論スレッド数 (0 = 自動)</summary>
    private readonly int _maxCpuThreads;

    /// <param name="whisperModelPath">Whisper GGML モデルファイルへのパス</param>
    /// <param name="modelSize">モデル名 (ログ表示用: "tiny", "base" など)</param>
    /// <param name="useGpu">true で GPU (CUDA) を使用</param>
    /// <param name="language">認識言語 ("ja", "en", "auto" など)</param>
    /// <param name="enableRecording">true で録音バッファを保持する (後処理用)</param>
    /// <param name="maxCpuThreads">Whisper 推論スレッド数 (0 = 自動)</param>
    public WhisperCallTranscriber(
        string whisperModelPath,
        string modelSize,
        MMDevice micDevice,
        MMDevice speakerDevice,
        bool useGpu = true,
        string language = "ja",
        bool enableRecording = false,
        int maxCpuThreads = 0)
    {
        _factory = WhisperFactory.FromPath(whisperModelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        _modelSize = modelSize;
        _language = language;
        _maxCpuThreads = maxCpuThreads;
        _micRecording = new RecordingBuffer(enableRecording);
        _speakerRecording = new RecordingBuffer(enableRecording);

        // 初期プロンプト: 言語に応じたドメインヒントを設定
        string initialPrompt = GetInitialPrompt(language);
        _micLastPrompt = initialPrompt;
        _spkLastPrompt = initialPrompt;

        // バックプレッシャー制御を初期化
        _micBackpressure = new BackpressureMonitor(DefaultMinBufferSec, DefaultMaxBufferSec);
        _spkBackpressure = new BackpressureMonitor(DefaultMinBufferSec, DefaultMaxBufferSec);

        int deviceNumber = DeviceHelper.FindWaveInDevice(micDevice, "Whisper");
        _micCapture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = TargetFormat,
            BufferMilliseconds = 100
        };

        _speakerCapture = new WasapiLoopbackCapture(speakerDevice);
    }

    // FindWaveInDevice は DeviceHelper に統合済み

    public void Start()
    {
        _micCapture.DataAvailable += OnMicDataAvailable;
        _micCapture.RecordingStopped += (_, _) => { };

        _loopbackFormat = _speakerCapture.WaveFormat;

        _speakerCapture.DataAvailable += OnSpeakerDataAvailable;
        _speakerCapture.RecordingStopped += (_, _) => { };

        // 認識処理スレッドを起動
        _micProcessThread = new Thread(MicProcessLoop)
        {
            IsBackground = true,
            Name = "WhisperMicProcess"
        };
        _spkProcessThread = new Thread(SpkProcessLoop)
        {
            IsBackground = true,
            Name = "WhisperSpkProcess"
        };
        _micProcessThread.Start();
        _spkProcessThread.Start();

        // キャプチャ開始
        _speakerCapture.StartRecording();
        Thread.Sleep(1500);
        _micCapture.StartRecording();
    }

    // ────────────────────────────────────────────────
    //  マイク音声受信
    // ────────────────────────────────────────────────
    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _stopping) return;

        _micChunks++;

        short peak = AudioProcessing.CalcPeak(e.Buffer, e.BytesRecorded);
        float rms = AudioProcessing.CalcRms(e.Buffer, e.BytesRecorded);

        // 音量通知 (#2)
        _lastMicPeak = peak;
        OnVolumeUpdated?.Invoke(_lastMicPeak, _lastSpkPeak);

        // アダプティブノイズゲート (#8)
        _micNoiseGate.Update(rms);

        // エネルギー履歴を更新 (スライディングウィンドウ VAD)
        lock (_micBufLock)
        {
            _micEnergyHistory.Enqueue(rms);
            while (_micEnergyHistory.Count > EnergyHistorySize)
                _micEnergyHistory.Dequeue();
        }

        // 録音全体を保存 (後処理用)
        _micRecording.Write(e.Buffer, 0, e.BytesRecorded);

        // 無音スキップ: アダプティブノイズゲート判定
        if (!_micNoiseGate.IsVoice(peak, rms)) return;

        // VAD: 音声がある時刻を記録
        _micLastVoiceTime = DateTime.UtcNow;

        // 認識用バッファに追加 (バックプレッシャー: 上限超過時は古いデータを破棄)
        lock (_micBufLock)
        {
            long capacity = _micBackpressure.GetBufferCapacityBytes(TargetFormat.SampleRate);
            if (_micBuffer.Length > capacity)
            {
                long dropped = _micBuffer.Length;
                int keep = (int)(capacity / 2);
                keep = keep - (keep % 2); // サンプル境界 (16bit=2bytes) にアライン
                byte[] tail = _micBuffer.ToArray()[(int)(_micBuffer.Length - keep)..];
                _micBuffer.SetLength(0);
                _micBuffer.Write(tail, 0, tail.Length);
                _micBackpressure.ReportDropped(dropped - keep);
                AppLogger.Warn($"[Whisper] マイクバッファ溢れ: {(dropped - keep) / 1024}KB 破棄");
            }
            _micBuffer.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    // ────────────────────────────────────────────────
    //  スピーカー音声受信
    // ────────────────────────────────────────────────
    private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _stopping) return;

        _speakerChunks++;

        byte[] converted = AudioProcessing.ConvertLoopbackToTarget(
            e.Buffer, e.BytesRecorded, _loopbackFormat!, TargetFormat);
        if (converted.Length == 0) return;

        // 無音スキップ: アダプティブノイズゲート判定
        short peak = AudioProcessing.CalcPeak(converted, converted.Length);
        float rms = AudioProcessing.CalcRms(converted, converted.Length);

        // 音量通知 (#2)
        _lastSpkPeak = peak;
        OnVolumeUpdated?.Invoke(_lastMicPeak, _lastSpkPeak);

        // アダプティブノイズゲート (#8)
        _spkNoiseGate.Update(rms);

        // エネルギー履歴を更新
        lock (_spkBufLock)
        {
            _spkEnergyHistory.Enqueue(rms);
            while (_spkEnergyHistory.Count > EnergyHistorySize)
                _spkEnergyHistory.Dequeue();
        }

        // 録音全体を保存 (ノイズゲート前に記録)
        _speakerRecording.Write(converted, 0, converted.Length);

        if (!_spkNoiseGate.IsVoice(peak, rms)) return;

        // VAD: 音声がある時刻を記録
        _spkLastVoiceTime = DateTime.UtcNow;

        // 認識用バッファに追加 (バックプレッシャー: 上限超過時は古いデータを破棄)
        lock (_spkBufLock)
        {
            long capacity = _spkBackpressure.GetBufferCapacityBytes(TargetFormat.SampleRate);
            if (_speakerBuffer.Length > capacity)
            {
                long dropped = _speakerBuffer.Length;
                int keep = (int)(capacity / 2);
                keep = keep - (keep % 2); // サンプル境界 (16bit=2bytes) にアライン
                byte[] tail = _speakerBuffer.ToArray()[(int)(_speakerBuffer.Length - keep)..];
                _speakerBuffer.SetLength(0);
                _speakerBuffer.Write(tail, 0, tail.Length);
                _spkBackpressure.ReportDropped(dropped - keep);
                AppLogger.Warn($"[Whisper] スピーカーバッファ溢れ: {(dropped - keep) / 1024}KB 破棄");
            }
            _speakerBuffer.Write(converted, 0, converted.Length);
        }
    }

    // ────────────────────────────────────────────────
    //  VAD ベース認識処理ループ (バックグラウンドスレッド)
    // ────────────────────────────────────────────────
    private void MicProcessLoop() => VadProcessLoop("自分", _micBufLock, _micBuffer,
        () => _micLastVoiceTime, () => _micLastPrompt, p => _micLastPrompt = p,
        () => _micLastText, t => _micLastText = t, _micEnergyHistory, _micBackpressure,
        () => _micOverlap, o => _micOverlap = o);

    private void SpkProcessLoop() => VadProcessLoop("相手", _spkBufLock, _speakerBuffer,
        () => _spkLastVoiceTime, () => _spkLastPrompt, p => _spkLastPrompt = p,
        () => _spkLastText, t => _spkLastText = t, _spkEnergyHistory, _spkBackpressure,
        () => _spkOverlap, o => _spkOverlap = o);

    /// <summary>
    /// VAD ベースの認識ループ。
    /// 発話→無音(700ms以上)を検知したとき、またはバッファが最大長(12秒)に
    /// 達したときに Whisper で処理する。最小長(1.2秒)未満はスキップ。
    /// エネルギー履歴を参照してより精密な無音判定を行う。
    /// </summary>
    private void VadProcessLoop(
        string speaker,
        object bufLock,
        MemoryStream buffer,
        Func<DateTime> getLastVoiceTime,
        Func<string> getPrompt,
        Action<string> setPrompt,
        Func<string> getLastText,
        Action<string> setLastText,
        Queue<float> energyHistory,
        BackpressureMonitor backpressure,
        Func<byte[]?> getOverlap,
        Action<byte[]?> setOverlap)
    {
        while (!_stopping)
        {
            Thread.Sleep(200); // ポーリング間隔

            // バックプレッシャーで動的に調整されるバッファサイズ
            int minBytes = (int)(TargetFormat.SampleRate * 2 * backpressure.CurrentMinBufferSec);
            int maxBytes = (int)(TargetFormat.SampleRate * 2 * backpressure.CurrentMaxBufferSec);

            byte[]? pcmData = null;

            lock (bufLock)
            {
                if (buffer.Length == 0)
                    continue;

                var lastVoice = getLastVoiceTime();
                double silenceMs = (DateTime.UtcNow - lastVoice).TotalMilliseconds;
                bool silenceDetected = lastVoice != DateTime.MinValue && silenceMs >= SilenceTimeoutMs;
                bool maxReached = buffer.Length >= maxBytes;

                // エネルギー履歴で無音判定を補強
                float avgEnergy = 0f;
                if (energyHistory.Count > 0)
                    avgEnergy = energyHistory.Average();
                bool energySilence = avgEnergy < VadEnergyThreshold && energyHistory.Count >= 3;

                // 通常: バッファ >= 最小長 かつ (無音検出 or 最大到達)
                // エネルギーベースは追加の無音判定
                bool normalProcess = buffer.Length >= minBytes && 
                    ((silenceDetected && energySilence) || silenceDetected || maxReached);
                bool shortUtterance = buffer.Length > 0 && buffer.Length < minBytes
                                   && lastVoice != DateTime.MinValue && silenceMs >= LongSilenceMs;

                if (normalProcess || shortUtterance)
                {
                    // ToArray のメモリ倍増を最小化: Position ベースで読み出す
                    int len = (int)buffer.Length;
                    byte[] rawData = new byte[len];
                    buffer.Position = 0;
                    buffer.Read(rawData, 0, len);
                    buffer.SetLength(0);

                    // 前チャンク末尾のオーバーラップを先頭に結合
                    byte[]? overlap = getOverlap();
                    if (overlap != null && overlap.Length > 0)
                    {
                        pcmData = new byte[overlap.Length + rawData.Length];
                        Buffer.BlockCopy(overlap, 0, pcmData, 0, overlap.Length);
                        Buffer.BlockCopy(rawData, 0, pcmData, overlap.Length, rawData.Length);
                    }
                    else
                    {
                        pcmData = rawData;
                    }

                    // 次のチャンクのために末尾を保存
                    int overlapBytes = (int)(OverlapSec * TargetFormat.SampleRate * 2);
                    overlapBytes = overlapBytes - (overlapBytes % 2); // サンプル境界にアライン
                    if (rawData.Length > overlapBytes)
                    {
                        byte[] newOverlap = new byte[overlapBytes];
                        Buffer.BlockCopy(rawData, rawData.Length - overlapBytes, newOverlap, 0, overlapBytes);
                        setOverlap(newOverlap);
                    }
                    else
                    {
                        setOverlap(null);
                    }
                }
            }

            if (pcmData != null)
            {
                string prompt = getPrompt();
                string lastText = getLastText();

                // 処理時間を計測してバックプレッシャーに報告
                double audioSec = pcmData.Length / (double)(TargetFormat.SampleRate * 2);
                var sw = Stopwatch.StartNew();
                string result = ProcessChunk(pcmData, speaker, prompt, lastText);
                sw.Stop();
                backpressure.ReportChunkProcessed(audioSec, sw.Elapsed.TotalSeconds);

                if (!string.IsNullOrEmpty(result))
                {
                    // Prompt を適切な長さに管理
                    string newPrompt = prompt + result;
                    if (newPrompt.Length > MaxPromptLength)
                        newPrompt = newPrompt[^MaxPromptLength..];
                    setPrompt(newPrompt);
                    setLastText(result);
                }
            }
        }

        // 停止時: 残りのバッファをすべて処理
        byte[]? remaining;
        lock (bufLock)
        {
            int len = (int)buffer.Length;
            if (len > 0)
            {
                byte[] rawData = new byte[len];
                buffer.Position = 0;
                buffer.Read(rawData, 0, len);

                // 前チャンク末尾のオーバーラップを先頭に結合
                byte[]? overlap = getOverlap();
                if (overlap != null && overlap.Length > 0)
                {
                    remaining = new byte[overlap.Length + rawData.Length];
                    Buffer.BlockCopy(overlap, 0, remaining, 0, overlap.Length);
                    Buffer.BlockCopy(rawData, 0, remaining, overlap.Length, rawData.Length);
                }
                else
                {
                    remaining = rawData;
                }
                setOverlap(null);
            }
            else
            {
                remaining = null;
            }
            buffer.SetLength(0);
        }

        if (remaining != null)
        {
            string prompt = getPrompt();
            string lastText = getLastText();
            ProcessChunk(remaining, speaker, prompt, lastText);
        }
    }

    /// <summary>PCM 16bit チャンクを Whisper で認識する。認識テキストを返す。</summary>
    private string ProcessChunk(byte[] pcm16, string speaker, string prompt, string lastText)
    {
        // バッファ全体の RMS エネルギーを確認—実質無音ならスキップ
        float rms = AudioProcessing.CalcRms(pcm16, pcm16.Length);
        if (rms < MinRmsEnergy)
            return "";

        try
        {
            double durationSec = pcm16.Length / (double)(TargetFormat.SampleRate * 2);

            OnProcessingStarted?.Invoke(speaker, durationSec);

            List<(TimeSpan Start, TimeSpan End, string Text)> results;
            try
            {
                // Whisper 推論スレッド数を決定:
                // _maxCpuThreads > 0 ならユーザー指定値を使用、
                // 0 ならオーディオスレッド用に4コアを確保し残りを割り当て
                int whisperThreads = _maxCpuThreads > 0
                    ? Math.Min(_maxCpuThreads, Math.Max(1, Environment.ProcessorCount - 2))
                    : Math.Max(1, Environment.ProcessorCount - 4);

                // ── ビームサーチデコーディング (greedy → beam search で精度向上) ──
                var beamStrategy = (BeamSearchSamplingStrategyBuilder)_factory.CreateBuilder()
                    .WithBeamSearchSamplingStrategy();
                var builder = beamStrategy
                    .WithBeamSize(5)
                    .WithPatience(1.0f)
                    .ParentBuilder
                    .WithLanguage(_language)
                    .WithThreads(whisperThreads)
                    // ── Temperature 0 (決定論的) + フォールバック ──
                    .WithTemperature(0f)
                    .WithTemperatureInc(0.2f)
                    // ── 品質フィルタ閾値 ──
                    .WithEntropyThreshold(2.4f)
                    .WithLogProbThreshold(-1.0f)
                    .WithNoSpeechThreshold(0.6f)
                    // ── チャンク内セグメント間のプロンプト引き継ぎ ──
                    .WithCarryInitialPrompt(true);

                // 前回の認識結果を prompt として渡し、文脈を維持
                if (!string.IsNullOrEmpty(prompt))
                    builder = builder.WithPrompt(prompt);

                using var processor = builder.Build();

                using var wavStream = new MemoryStream();
                AudioProcessing.WriteWavPcm16(wavStream, pcm16, TargetFormat.SampleRate);
                wavStream.Position = 0;

                var task = Task.Run(async () =>
                {
                    var segments = new List<(TimeSpan Start, TimeSpan End, string Text)>();
                    await foreach (var seg in processor.ProcessAsync(wavStream))
                    {
                        string text = seg.Text?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(text))
                            segments.Add((seg.Start, seg.End, text));
                    }
                    return segments;
                });

                results = task.GetAwaiter().GetResult();
            }
            finally
            {
                OnProcessingCompleted?.Invoke(speaker);
            }

            var sb = new System.Text.StringBuilder();

            foreach (var (start, end, text) in results)
            {
                // ── ハルシネーションフィルター ──
                string normalized = WhisperTextFilter.NormalizeText(text);
                if (WhisperTextFilter.IsHallucination(normalized))
                    continue;

                // ── 重複テキスト検出 ──
                if (WhisperTextFilter.IsDuplicate(normalized, lastText))
                    continue;

                sb.Append(normalized);

                var entry = new TranscriptEntry(
                    Timestamp: DateTime.Now,
                    Speaker: speaker,
                    Text: normalized,
                    Duration: end - start);

                lock (_lock)
                {
                    _entries.Add(entry);
                }

                OnTranscribed?.Invoke(entry);
                lastText = normalized;
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            Logging.AppLogger.Error($"[Whisper] {speaker} 認識エラー", ex);
            return "";
        }
    }

    // ────────────────────────────────────────────────
    //  停止
    // ────────────────────────────────────────────────
    public void Stop()
    {
        if (_stopping || _disposed) return;
        _stopping = true;

        try { _micCapture.StopRecording(); } catch { }
        try { _speakerCapture.StopRecording(); } catch { }

        // 認識スレッドの完了を待つ
        _micProcessThread?.Join(TimeSpan.FromSeconds(15));
        _spkProcessThread?.Join(TimeSpan.FromSeconds(15));
    }

    public byte[] GetMicRecording() => _micRecording.ToArray();

    public byte[] GetSpeakerRecording() => _speakerRecording.ToArray();

    public void SaveMicRecordingAsWav(string path) => _micRecording.SaveAsWav(path);

    public void SaveSpeakerRecordingAsWav(string path) => _speakerRecording.SaveAsWav(path);

    public long MicRecordingLength => _micRecording.Length;

    public long SpeakerRecordingLength => _speakerRecording.Length;

    public void Dispose()
    {
        if (_disposed) return;

        // Stop() が未呼出の場合に安全に停止 (処理スレッドが走行中にリソースを破棄しない)
        if (!_stopping) Stop();

        _disposed = true;

        _micCapture.DataAvailable -= OnMicDataAvailable;
        _speakerCapture.DataAvailable -= OnSpeakerDataAvailable;

        _micCapture.Dispose();
        _speakerCapture.Dispose();
        _factory.Dispose();

        // 認識バッファを確実に解放
        _micBuffer.SetLength(0);
        _micBuffer.Capacity = 0;
        _micBuffer.Dispose();
        _speakerBuffer.SetLength(0);
        _speakerBuffer.Capacity = 0;
        _speakerBuffer.Dispose();

        // 録音バッファを確実に解放
        _micRecording.Dispose();
        _speakerRecording.Dispose();

        GC.SuppressFinalize(this);
    }
}
