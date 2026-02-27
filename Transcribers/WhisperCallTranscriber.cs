using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Audio;
using TalkTranscript.Models;
using Whisper.net;

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

    // ── キャプチャ ──
    private readonly WaveInEvent _micCapture;
    private readonly WasapiLoopbackCapture _speakerCapture;

    // ── 音声バッファ ──
    private readonly MemoryStream _micBuffer = new();
    private readonly MemoryStream _speakerBuffer = new();
    private readonly object _micBufLock = new();
    private readonly object _spkBufLock = new();

    // ── 録音全体 (Whisper 後処理用) ──
    private readonly MemoryStream _micRecording = new();
    private readonly MemoryStream _speakerRecording = new();
    private readonly object _micRecLock = new();
    private readonly object _spkRecLock = new();

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
    private const int EnergyHistorySize = 10; // 直近10チャンク分

    // ── Prompt 引き継ぎ (文脈連続性) ──
    private string _micLastPrompt = "";
    private string _spkLastPrompt = "";
    /// <summary>Prompt の最大長 (Whisper のトークン上限を考慮)</summary>
    private const int MaxPromptLength = 200;

    // ── 重複検出用: 直近の認識テキスト ──
    private string _micLastText = "";
    private string _spkLastText = "";

    // ── 設定 ──
    /// <summary>無音と判定するピーク閾値 (チャンク単位)</summary>
    private const short SilenceThreshold = 250;
    /// <summary>発話終了とみなす無音持続時間 (ms)</summary>
    private const int SilenceTimeoutMs = 700;
    /// <summary>バッファ全体の RMS がこの値未満なら実質無音とみなしスキップ (Whisper ハルシネーション防止)</summary>
    private const float MinRmsEnergy = 200f;
    /// <summary>最小バッファ秒数 (短すぎるチャンクは精度が低い)</summary>
    private const double MinBufferSec = 1.2;
    /// <summary>最大バッファ秒数 (長い発話でもここで強制処理)</summary>
    private const double MaxBufferSec = 12.0;
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

    /// <summary>Whisper 処理開始時に発火 (speaker, durationSec)</summary>
    public event Action<string, double>? OnProcessingStarted;

    /// <summary>Whisper 処理完了時に発火 (speaker)</summary>
    public event Action<string>? OnProcessingCompleted;

    /// <param name="whisperModelPath">Whisper GGML モデルファイルへのパス</param>
    /// <param name="modelSize">モデル名 (ログ表示用: "tiny", "base" など)</param>
    /// <param name="useGpu">true で GPU (CUDA) を使用</param>
    public WhisperCallTranscriber(
        string whisperModelPath,
        string modelSize,
        MMDevice micDevice,
        MMDevice speakerDevice,
        bool useGpu = true)
    {
        _factory = WhisperFactory.FromPath(whisperModelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        _modelSize = modelSize;

        int deviceNumber = FindWaveInDevice(micDevice);
        _micCapture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = TargetFormat,
            BufferMilliseconds = 100
        };

        _speakerCapture = new WasapiLoopbackCapture(speakerDevice);
    }

    private static int FindWaveInDevice(MMDevice mmDevice)
    {
        string targetName = mmDevice.FriendlyName;

        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            string prodName = caps.ProductName;

            if (targetName.StartsWith(prodName, StringComparison.OrdinalIgnoreCase) ||
                prodName.StartsWith(targetName[..Math.Min(targetName.Length, prodName.Length)],
                                    StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[Whisper] WaveIn デバイス #{i}: {prodName} (マッチ)");
                return i;
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Whisper] WaveIn デバイスが名前で一致しません。デフォルトを使用します。");
        Console.ResetColor();
        return 0;
    }

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

        // エネルギー履歴を更新 (スライディングウィンドウ VAD)
        lock (_micBufLock)
        {
            _micEnergyHistory.Enqueue(rms);
            while (_micEnergyHistory.Count > EnergyHistorySize)
                _micEnergyHistory.Dequeue();
        }

        // 録音全体を保存 (後処理用)
        lock (_micRecLock)
        {
            _micRecording.Write(e.Buffer, 0, e.BytesRecorded);
        }

        // 無音スキップ: ピーク + エネルギー の二重判定
        if (peak < SilenceThreshold && rms < VadEnergyThreshold) return;

        // VAD: 音声がある時刻を記録
        _micLastVoiceTime = DateTime.UtcNow;

        // 認識用バッファに追加
        lock (_micBufLock)
        {
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

        // 無音スキップ: ピーク + エネルギー の二重判定
        short peak = AudioProcessing.CalcPeak(converted, converted.Length);
        float rms = AudioProcessing.CalcRms(converted, converted.Length);

        // エネルギー履歴を更新
        lock (_spkBufLock)
        {
            _spkEnergyHistory.Enqueue(rms);
            while (_spkEnergyHistory.Count > EnergyHistorySize)
                _spkEnergyHistory.Dequeue();
        }

        if (peak < SilenceThreshold && rms < VadEnergyThreshold) return;

        // VAD: 音声がある時刻を記録
        _spkLastVoiceTime = DateTime.UtcNow;

        // 録音全体を保存
        lock (_spkRecLock)
        {
            _speakerRecording.Write(converted, 0, converted.Length);
        }

        // 認識用バッファに追加
        lock (_spkBufLock)
        {
            _speakerBuffer.Write(converted, 0, converted.Length);
        }
    }

    // ────────────────────────────────────────────────
    //  VAD ベース認識処理ループ (バックグラウンドスレッド)
    // ────────────────────────────────────────────────
    private void MicProcessLoop() => VadProcessLoop("自分", _micBufLock, _micBuffer,
        () => _micLastVoiceTime, () => _micLastPrompt, p => _micLastPrompt = p,
        () => _micLastText, t => _micLastText = t, _micEnergyHistory);

    private void SpkProcessLoop() => VadProcessLoop("相手", _spkBufLock, _speakerBuffer,
        () => _spkLastVoiceTime, () => _spkLastPrompt, p => _spkLastPrompt = p,
        () => _spkLastText, t => _spkLastText = t, _spkEnergyHistory);

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
        Queue<float> energyHistory)
    {
        int minBytes = (int)(TargetFormat.SampleRate * 2 * MinBufferSec);
        int maxBytes = (int)(TargetFormat.SampleRate * 2 * MaxBufferSec);

        while (!_stopping)
        {
            Thread.Sleep(200); // ポーリング間隔

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
                    pcmData = buffer.ToArray();
                    buffer.SetLength(0);
                }
            }

            if (pcmData != null)
            {
                string prompt = getPrompt();
                string lastText = getLastText();
                string result = ProcessChunk(pcmData, speaker, prompt, lastText);
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
            remaining = buffer.Length > 0 ? buffer.ToArray() : null;
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
                var builder = _factory.CreateBuilder()
                    .WithLanguage("ja")
                    .WithThreads(Math.Max(1, Environment.ProcessorCount / 2));

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
        catch
        {
            return "";
        }
    }

    // ────────────────────────────────────────────────
    //  音声変換ユーティリティ (共通関数へ委譲)
    // ────────────────────────────────────────────────

    private static byte[] ConvertAudio(
        byte[] source, int length, WaveFormat sourceFormat, WaveFormat targetFormat)
        => AudioProcessing.ConvertLoopbackToTarget(source, length, sourceFormat, targetFormat);

    // ────────────────────────────────────────────────
    //  停止
    // ────────────────────────────────────────────────
    public void Stop()
    {
        _stopping = true;

        _micCapture.StopRecording();
        _speakerCapture.StopRecording();

        // 認識スレッドの完了を待つ
        _micProcessThread?.Join(TimeSpan.FromSeconds(15));
        _spkProcessThread?.Join(TimeSpan.FromSeconds(15));
    }

    public byte[] GetMicRecording()
    {
        lock (_micRecLock) return _micRecording.ToArray();
    }

    public byte[] GetSpeakerRecording()
    {
        lock (_spkRecLock) return _speakerRecording.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _micCapture.DataAvailable -= OnMicDataAvailable;
        _speakerCapture.DataAvailable -= OnSpeakerDataAvailable;

        _micCapture.Dispose();
        _speakerCapture.Dispose();
        _factory.Dispose();
        _micBuffer.Dispose();
        _speakerBuffer.Dispose();
        _micRecording.Dispose();
        _speakerRecording.Dispose();

        GC.SuppressFinalize(this);
    }
}
