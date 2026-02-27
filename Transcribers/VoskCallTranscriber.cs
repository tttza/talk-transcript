using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Models;
using Vosk;

namespace TalkTranscript.Transcribers;

/// <summary>
/// Vosk (オフライン音声認識) でマイクとスピーカーを独立して文字起こしする。
///
/// Vosk は SAPI と異なり、複数の VoskRecognizer を同時に実行可能。
/// マイクとスピーカーそれぞれに専用の認識器を持ち、
/// 同時発話でも両方を正しく認識できる。
///
/// 録音データはメモリに保持し、通話終了後に Whisper で高精度な
/// 再認識 (後処理) を行うことも可能。
/// </summary>
public sealed class VoskCallTranscriber : ICallTranscriber
{
    // ── Vosk モデル & 認識器 ──
    private readonly Model _model;
    private VoskRecognizer? _micRecognizer;
    private VoskRecognizer? _speakerRecognizer;

    // ── キャプチャ ──
    private readonly WaveInEvent _micCapture;
    private readonly WasapiLoopbackCapture _speakerCapture;

    // ── 録音バッファ (Whisper 後処理用) ──
    private readonly MemoryStream _micRecording = new();
    private readonly MemoryStream _speakerRecording = new();
    private readonly object _micRecLock = new();
    private readonly object _speakerRecLock = new();

    // ── 結果格納 ──
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _lock = new();

    // ── 制御 ──
    private bool _disposed;
    private volatile bool _stopping;
    private WaveFormat? _loopbackFormat;

    // ── 統計 ──
    private int _micChunks;
    private int _speakerChunks;

    /// <summary>認識エンジンが期待するフォーマット: 16 kHz / 16 bit / mono</summary>
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

    public VoskCallTranscriber(Model voskModel, MMDevice micDevice, MMDevice speakerDevice)
    {
        _model = voskModel;

        // ── マイク: WaveInEvent (MME API) ──
        int deviceNumber = FindWaveInDevice(micDevice);
        _micCapture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = TargetFormat,
            BufferMilliseconds = 100
        };

        // ── スピーカー: WASAPI ループバック ──
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
                Console.WriteLine($"[Vosk] WaveIn デバイス #{i}: {prodName} (マッチ)");
                return i;
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Vosk] WaveIn デバイスが名前で一致しません。デフォルトを使用します。");
        Console.ResetColor();
        return 0;
    }

    public void Start()
    {
        // ── Vosk 認識器を作成 (マイク用・スピーカー用の 2 つ) ──
        _micRecognizer = new VoskRecognizer(_model, TargetFormat.SampleRate);
        _micRecognizer.SetMaxAlternatives(0);
        _micRecognizer.SetWords(true);

        _speakerRecognizer = new VoskRecognizer(_model, TargetFormat.SampleRate);
        _speakerRecognizer.SetMaxAlternatives(0);
        _speakerRecognizer.SetWords(true);

        // ── マイク ──
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

        // ── スピーカー ──
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

        // ── キャプチャ開始 ──
        _speakerCapture.StartRecording();
        Thread.Sleep(1500);
        _micCapture.StartRecording();
    }

    // ────────────────────────────────────────────────
    //  マイク音声受信
    // ────────────────────────────────────────────────
    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _micRecognizer == null || _stopping) return;

        _micChunks++;

        // 録音バッファに保存 (Whisper 後処理用)
        lock (_micRecLock)
        {
            _micRecording.Write(e.Buffer, 0, e.BytesRecorded);
        }

        // 初回ログ
        if (_micChunks <= 1)
        {
            short maxSample = CalcPeak(e.Buffer, e.BytesRecorded);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [Vosk] マイク ピーク={maxSample}");
            Console.ResetColor();
        }

        // Vosk に音声データを供給
        if (_micRecognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
        {
            ProcessResult(_micRecognizer.Result(), "自分");
        }
    }

    // ────────────────────────────────────────────────
    //  スピーカー音声受信
    // ────────────────────────────────────────────────
    private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _speakerRecognizer == null) return;

        _speakerChunks++;

        // フォーマット変換 (48kHz/32bit/2ch → 16kHz/16bit/mono)
        byte[] converted = ConvertAudio(
            e.Buffer, e.BytesRecorded, _loopbackFormat!, TargetFormat);

        if (converted.Length == 0) return;

        // 録音バッファに保存 (Whisper 後処理用)
        lock (_speakerRecLock)
        {
            _speakerRecording.Write(converted, 0, converted.Length);
        }

        // 初回ログ
        if (_speakerChunks <= 1)
        {
            short maxSample = CalcPeak(converted, converted.Length);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [Vosk] スピーカー ピーク={maxSample}");
            Console.ResetColor();
        }

        // 無音フィルタ: ピークが閾値以下ならVoskへの供給をスキップ
        // (無音データを大量に流し続けるとVoskが不安定になる)
        short peak = CalcPeak(converted, converted.Length);
        if (peak < 50) return;

        // Vosk に音声データを供給
        if (!_stopping && _speakerRecognizer.AcceptWaveform(converted, converted.Length))
        {
            ProcessResult(_speakerRecognizer.Result(), "相手");
        }
    }

    // ────────────────────────────────────────────────
    //  認識結果の処理
    // ────────────────────────────────────────────────
    private void ProcessResult(string json, string speaker)
    {
        string text = ParseText(json);
        if (string.IsNullOrWhiteSpace(text)) return;

        // Vosk の日本語モデルは形態素ごとにスペースを入れるので除去する
        text = text.Replace(" ", "").Replace("\u3000", "");
        if (string.IsNullOrWhiteSpace(text)) return;

        var entry = new TranscriptEntry(
            Timestamp: DateTime.Now,
            Speaker: speaker,
            Text: text,
            Duration: null);

        lock (_lock)
        {
            _entries.Add(entry);
        }

        OnTranscribed?.Invoke(entry);
    }

    /// <summary>Vosk JSON 結果から "text" フィールドを抽出する</summary>
    private static string ParseText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("text", out var textElem))
                return textElem.GetString() ?? "";
        }
        catch { }
        return "";
    }

    /// <summary>Vosk JSON 部分結果から "partial" フィールドを抽出する</summary>
    private static string ParsePartial(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("partial", out var textElem))
                return textElem.GetString() ?? "";
        }
        catch { }
        return "";
    }

    // ────────────────────────────────────────────────
    //  音声フォーマット変換
    // ────────────────────────────────────────────────
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
                {
                    sum += BitConverter.ToSingle(source, offset);
                }
            }

            float mono = sum / channels;
            mono = Math.Clamp(mono, -1.0f, 1.0f);

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

    // ────────────────────────────────────────────────
    //  停止
    // ────────────────────────────────────────────────
    public void Stop()
    {
        _stopping = true;

        _micCapture.StopRecording();
        _speakerCapture.StopRecording();

        // 残りの音声を最終認識
        try
        {
            if (_micRecognizer != null)
                ProcessResult(_micRecognizer.FinalResult(), "自分");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Vosk] マイク最終結果エラー (無視): {ex.Message}");
        }
        try
        {
            if (_speakerRecognizer != null)
                ProcessResult(_speakerRecognizer.FinalResult(), "相手");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Vosk] スピーカー最終結果エラー (無視): {ex.Message}");
        }

        long micBytes, spkBytes;
        lock (_micRecLock) micBytes = _micRecording.Length;
        lock (_speakerRecLock) spkBytes = _speakerRecording.Length;


    }

    // ────────────────────────────────────────────────
    //  録音データ取得 (Whisper 後処理用)
    // ────────────────────────────────────────────────
    public byte[] GetMicRecording()
    {
        lock (_micRecLock) return _micRecording.ToArray();
    }

    public byte[] GetSpeakerRecording()
    {
        lock (_speakerRecLock) return _speakerRecording.ToArray();
    }

    // ────────────────────────────────────────────────
    //  破棄
    // ────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _micCapture.DataAvailable -= OnMicDataAvailable;
        _speakerCapture.DataAvailable -= OnSpeakerDataAvailable;

        _micCapture.Dispose();
        _speakerCapture.Dispose();
        _micRecognizer?.Dispose();
        _speakerRecognizer?.Dispose();
        _micRecording.Dispose();
        _speakerRecording.Dispose();

        GC.SuppressFinalize(this);
    }
}
