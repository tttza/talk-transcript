using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Models;
using Whisper.net;
using TalkTranscript;

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

    // ── Prompt 引き継ぎ (文脈連続性) ──
    private string _micLastPrompt = "";
    private string _spkLastPrompt = "";

    // ── 設定 ──
    /// <summary>無音と判定するピーク閾値 (チャンク単位)</summary>
    private const short SilenceThreshold = 200;
    /// <summary>発話終了とみなす無音持続時間 (ms)</summary>
    private const int SilenceTimeoutMs = 800;
    /// <summary>バッファ全体の RMS がこの値未満なら実質無音とみなしスキップ (Whisper ハルシネーション防止)</summary>
    private const float MinRmsEnergy = 150f;
    /// <summary>最小バッファ秒数 (短すぎるチャンクは精度が低い)</summary>
    private const double MinBufferSec = 1.0;
    /// <summary>最大バッファ秒数 (長い発話でもここで強制処理)</summary>
    private const double MaxBufferSec = 15.0;
    /// <summary>短い発話を救済する長い無音判定時間 (ms)。バッファが最小長未満でもこの時間無音が続けば処理する</summary>
    private const int LongSilenceMs = 2000;

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
        _micCapture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  マイクエラー: {e.Exception.Message}");
                Console.ResetColor();
            }
        };

        _loopbackFormat = _speakerCapture.WaveFormat;

        _speakerCapture.DataAvailable += OnSpeakerDataAvailable;
        _speakerCapture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  スピーカーエラー: {e.Exception.Message}");
                Console.ResetColor();
            }
        };

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

        short peak = CalcPeak(e.Buffer, e.BytesRecorded);

        // 録音全体を保存 (後処理用)
        lock (_micRecLock)
        {
            _micRecording.Write(e.Buffer, 0, e.BytesRecorded);
        }

        // 無音スキップ (Whisper は無音でハルシネーションするため)
        if (peak < SilenceThreshold) return;

        // VAD: 音声がある時刻を記録
        _micLastVoiceTime = DateTime.UtcNow;

        // 認識用バッファに追加
        lock (_micBufLock)
        {
            _micBuffer.Write(e.Buffer, 0, e.BytesRecorded);
        }

        if (_micChunks <= 1)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [Whisper] マイク ピーク={peak}");
            Console.ResetColor();
        }
    }

    // ────────────────────────────────────────────────
    //  スピーカー音声受信
    // ────────────────────────────────────────────────
    private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _stopping) return;

        _speakerChunks++;

        byte[] converted = ConvertAudio(
            e.Buffer, e.BytesRecorded, _loopbackFormat!, TargetFormat);
        if (converted.Length == 0) return;

        // 無音スキップ
        short peak = CalcPeak(converted, converted.Length);
        if (peak < SilenceThreshold) return;

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

        if (_speakerChunks <= 1)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [Whisper] スピーカー ピーク={peak}");
            Console.ResetColor();
        }
    }

    // ────────────────────────────────────────────────
    //  VAD ベース認識処理ループ (バックグラウンドスレッド)
    // ────────────────────────────────────────────────
    private void MicProcessLoop() => VadProcessLoop("自分", _micBufLock, _micBuffer,
        () => _micLastVoiceTime, () => _micLastPrompt, p => _micLastPrompt = p);

    private void SpkProcessLoop() => VadProcessLoop("相手", _spkBufLock, _speakerBuffer,
        () => _spkLastVoiceTime, () => _spkLastPrompt, p => _spkLastPrompt = p);

    /// <summary>
    /// VAD ベースの認識ループ。
    /// 発話→無音(800ms以上)を検知したとき、またはバッファが最大長(15秒)に
    /// 達したときに Whisper で処理する。最小長(2秒)未満はスキップ。
    /// </summary>
    private void VadProcessLoop(
        string speaker,
        object bufLock,
        MemoryStream buffer,
        Func<DateTime> getLastVoiceTime,
        Func<string> getPrompt,
        Action<string> setPrompt)
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

                // 通常: バッファ >= 最小長 かつ (無音検出 or 最大到達)
                // 短い発話救済: バッファ < 最小長 でも長い無音が続けば処理
                bool normalProcess = buffer.Length >= minBytes && (silenceDetected || maxReached);
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
                string result = ProcessChunk(pcmData, speaker, prompt);
                if (!string.IsNullOrEmpty(result))
                    setPrompt(result);
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
            ProcessChunk(remaining, speaker, prompt);
        }
    }

    /// <summary>PCM 16bit チャンクを Whisper で認識する。認識テキストを返す。</summary>
    private string ProcessChunk(byte[] pcm16, string speaker, string prompt)
    {
        // バッファ全体の RMS エネルギーを確認—実質無音ならスキップ
        float rms = CalcRms(pcm16, pcm16.Length);
        if (rms < MinRmsEnergy)
            return "";

        try
        {
            double durationSec = pcm16.Length / (double)(TargetFormat.SampleRate * 2);

            List<(TimeSpan Start, TimeSpan End, string Text)> results;
            using (var spinner = new ConsoleSpinner($"[{speaker}] {durationSec:F1}秒の音声を処理中..."))
            {
                var builder = _factory.CreateBuilder()
                    .WithLanguage("ja")
                    .WithThreads(Math.Max(1, Environment.ProcessorCount / 2));

                // 前回の認識結果を prompt として渡し、文脈を維持
                if (!string.IsNullOrEmpty(prompt))
                    builder = builder.WithPrompt(prompt);

                using var processor = builder.Build();

                using var wavStream = new MemoryStream();
                WriteWavPcm16(wavStream, pcm16, TargetFormat.SampleRate);
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

            var sb = new System.Text.StringBuilder();

            foreach (var (start, end, text) in results)
            {
                sb.Append(text);

                var entry = new TranscriptEntry(
                    Timestamp: DateTime.Now,
                    Speaker: speaker,
                    Text: text,
                    Duration: end - start);

                lock (_lock)
                {
                    _entries.Add(entry);
                }

                OnTranscribed?.Invoke(entry);
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [{speaker}] 認識エラー: {ex.Message}");
            Console.ResetColor();
            return "";
        }
    }

    // ────────────────────────────────────────────────
    //  音声変換ユーティリティ
    // ────────────────────────────────────────────────
    /// <summary>PCM 16bit/mono データを WAV ファイルとしてストリームに書き込む</summary>
    private static void WriteWavPcm16(Stream stream, byte[] pcm16, int sampleRate)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        int channels = 1;
        int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataSize = pcm16.Length;

        // RIFF header
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk (PCM)
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                       // chunk size
        w.Write((short)1);                 // audio format: PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        // data chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        w.Write(pcm16);
    }

    private static byte[] ConvertAudio(
        byte[] source, int length, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        int bytesPerSample = sourceFormat.BitsPerSample / 8;
        int channels = sourceFormat.Channels;
        int sampleCount = length / (bytesPerSample * channels);

        int ratio = sourceFormat.SampleRate / targetFormat.SampleRate;
        if (ratio < 1) ratio = 1;

        int outputSamples = sampleCount / ratio;
        if (outputSamples == 0) return Array.Empty<byte>();

        byte[] result = new byte[outputSamples * 2];

        for (int i = 0; i < outputSamples; i++)
        {
            int srcIndex = i * ratio;
            float sum = 0f;
            for (int ch = 0; ch < channels; ch++)
            {
                int offset = (srcIndex * channels + ch) * bytesPerSample;
                if (offset + 4 <= length)
                    sum += BitConverter.ToSingle(source, offset);
            }
            float mono = Math.Clamp(sum / channels, -1.0f, 1.0f);
            short pcm = (short)(mono * 32767);
            result[i * 2] = (byte)(pcm & 0xFF);
            result[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return result;
    }

    private static short CalcPeak(byte[] buffer, int length)
    {
        short max = 0;
        for (int j = 0; j + 1 < length; j += 2)
        {
            short s = BitConverter.ToInt16(buffer, j);
            short abs = Math.Abs(s);
            if (abs > max) max = abs;
        }
        return max;
    }

    /// <summary>PCM 16bit バッファの RMS (二乗平均平方根) を計算する</summary>
    private static float CalcRms(byte[] buffer, int length)
    {
        long sumSq = 0;
        int count = 0;
        for (int j = 0; j + 1 < length; j += 2)
        {
            short s = BitConverter.ToInt16(buffer, j);
            sumSq += (long)s * s;
            count++;
        }
        return count > 0 ? MathF.Sqrt((float)sumSq / count) : 0f;
    }

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
