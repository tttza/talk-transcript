using System.Collections.Concurrent;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Audio;
using TalkTranscript.Logging;
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
    private readonly bool _ownsModel;
    private VoskRecognizer? _micRecognizer;
    private VoskRecognizer? _speakerRecognizer;

    // ── キャプチャ ──
    private readonly WaveInEvent _micCapture;
    private readonly WasapiLoopbackCapture _speakerCapture;

    // ── 録音バッファ (Whisper 後処理用) ──
    private readonly RecordingBuffer _micRecording;
    private readonly RecordingBuffer _speakerRecording;

    // ── 結果格納 ──
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _lock = new();

    // ── 制御 ──
    private bool _disposed;
    private volatile bool _stopping;
    private WaveFormat? _loopbackFormat;

    // ── Producer-Consumer キュー (コールバック内でのロックコンテンション解消) ──
    private readonly ConcurrentQueue<byte[]> _micQueue = new();
    private readonly ConcurrentQueue<byte[]> _spkQueue = new();
    private Thread? _micProcessThread;
    private Thread? _spkProcessThread;
    /// <summary>キューサイズ上限 (16kHz/16bit/100ms ≒ 3.2KB × 200 ≒ 20秒分)</summary>
    private const int MaxQueueSize = 200;



    // ── 統計 ──
    private int _micChunks;
    private int _speakerChunks;

    // ── 音量通知 (#2) ──
    private volatile float _lastMicPeak;
    private volatile float _lastSpkPeak;

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

    /// <summary>音量レベルが更新されたときに発火 (micPeak, speakerPeak)</summary>
    public event Action<float, float>? OnVolumeUpdated;

    /// <param name="ownsModel">true のとき Dispose 時に Model も破棄する</param>
    /// <param name="enableRecording">true で録音バッファを保持する (後処理用)</param>
    public VoskCallTranscriber(Model voskModel, MMDevice micDevice, MMDevice speakerDevice,
        bool ownsModel = false, bool enableRecording = false)
    {
        _model = voskModel;
        _ownsModel = ownsModel;
        _micRecording = new RecordingBuffer(enableRecording);
        _speakerRecording = new RecordingBuffer(enableRecording);

        // ── マイク: WaveInEvent (MME API) ──
        int deviceNumber = DeviceHelper.FindWaveInDevice(micDevice, "Vosk");
        _micCapture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = TargetFormat,
            BufferMilliseconds = 100
        };

        // ── スピーカー: WASAPI ループバック ──
        _speakerCapture = new WasapiLoopbackCapture(speakerDevice);
    }

    // FindWaveInDevice は DeviceHelper に統合済み

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

        // ── Producer-Consumer スレッドを起動 ──
        _micProcessThread = new Thread(MicVoskLoop) { IsBackground = true, Name = "VoskMicProcess" };
        _spkProcessThread = new Thread(SpkVoskLoop) { IsBackground = true, Name = "VoskSpkProcess" };
        _micProcessThread.Start();
        _spkProcessThread.Start();
    }

    // ────────────────────────────────────────────────
    //  マイク音声受信
    // ────────────────────────────────────────────────
    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _micRecognizer == null || _stopping) return;

        _micChunks++;

        // 音量通知 (#2)
        short micPeak = AudioProcessing.CalcPeak(e.Buffer, e.BytesRecorded);
        _lastMicPeak = micPeak;
        OnVolumeUpdated?.Invoke(_lastMicPeak, _lastSpkPeak);

        // 録音バッファに保存 (Whisper 後処理用)
        _micRecording.Write(e.Buffer, 0, e.BytesRecorded);

        // 初回ログ
        if (_micChunks <= 1)
        {
            AppLogger.Debug($"[Vosk] マイク ピーク={micPeak}");
        }

        // キューに追加 (コールバックを即座に返す)
        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
        while (_micQueue.Count > MaxQueueSize) _micQueue.TryDequeue(out _);
        _micQueue.Enqueue(copy);
    }

    // ────────────────────────────────────────────────
    //  スピーカー音声受信
    // ────────────────────────────────────────────────
    private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _speakerRecognizer == null || _stopping) return;

        _speakerChunks++;

        // フォーマット変換 (48kHz/32bit/2ch → 16kHz/16bit/mono)
        byte[] converted = AudioProcessing.ConvertLoopbackToTarget(
            e.Buffer, e.BytesRecorded, _loopbackFormat!, TargetFormat);

        if (converted.Length == 0) return;

        // 録音バッファに保存 (Whisper 後処理用)
        _speakerRecording.Write(converted, 0, converted.Length);

        // 初回ログ
        if (_speakerChunks <= 1)
        {
            short maxSample2 = AudioProcessing.CalcPeak(converted, converted.Length);
            AppLogger.Debug($"[Vosk] スピーカー ピーク={maxSample2}");
        }

        // 無音フィルタ: ピークが閾値以下ならスキップ
        short peak = AudioProcessing.CalcPeak(converted, converted.Length);
        _lastSpkPeak = peak;
        OnVolumeUpdated?.Invoke(_lastMicPeak, _lastSpkPeak);
        if (peak < 50) return;

        // キューに追加 (コールバックを即座に返す)
        while (_spkQueue.Count > MaxQueueSize) _spkQueue.TryDequeue(out _);
        _spkQueue.Enqueue(converted);
    }

    // ────────────────────────────────────────────────
    //  Producer-Consumer ループ (コールバックとのロック競合解消)
    // ────────────────────────────────────────────────
    private void MicVoskLoop()
    {
        try
        {
            while (!_stopping)
            {
                if (_micQueue.TryDequeue(out var data))
                {
                    if (_micRecognizer != null && _micRecognizer.AcceptWaveform(data, data.Length))
                        ProcessResult(_micRecognizer.Result(), "自分");
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            // 停止時: キューに残ったデータを処理
            while (_micQueue.TryDequeue(out var remaining))
            {
                if (_micRecognizer != null)
                    _micRecognizer.AcceptWaveform(remaining, remaining.Length);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Vosk] マイク処理スレッドで例外が発生", ex);
        }
    }

    private void SpkVoskLoop()
    {
        try
        {
            while (!_stopping)
            {
                if (_spkQueue.TryDequeue(out var data))
                {
                    if (_speakerRecognizer != null && _speakerRecognizer.AcceptWaveform(data, data.Length))
                        ProcessResult(_speakerRecognizer.Result(), "相手");
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            while (_spkQueue.TryDequeue(out var remaining))
            {
                if (_speakerRecognizer != null)
                    _speakerRecognizer.AcceptWaveform(remaining, remaining.Length);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[Vosk] スピーカー処理スレッドで例外が発生", ex);
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
    //  停止
    // ────────────────────────────────────────────────
    public void Stop()
    {
        if (_stopping || _disposed) return;
        _stopping = true;

        try { _micCapture.StopRecording(); } catch { }
        try { _speakerCapture.StopRecording(); } catch { }

        // Producer-Consumer スレッドの完了を待つ
        bool micJoined = _micProcessThread?.Join(TimeSpan.FromSeconds(10)) ?? true;
        bool spkJoined = _spkProcessThread?.Join(TimeSpan.FromSeconds(10)) ?? true;

        // スレッドが正常終了した場合のみ FinalResult を呼ぶ
        // (タイムアウトした場合はスレッドがまだ認識器にアクセス中のためスキップ)
        if (micJoined)
        {
            try
            {
                if (_micRecognizer != null)
                    ProcessResult(_micRecognizer.FinalResult(), "自分");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Vosk] マイク最終結果エラー (無視): {ex.Message}");
            }
        }
        else
        {
            AppLogger.Warn("[Vosk] マイク処理スレッドがタイムアウト — FinalResult をスキップ");
        }

        if (spkJoined)
        {
            try
            {
                if (_speakerRecognizer != null)
                    ProcessResult(_speakerRecognizer.FinalResult(), "相手");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Vosk] スピーカー最終結果エラー (無視): {ex.Message}");
            }
        }
        else
        {
            AppLogger.Warn("[Vosk] スピーカー処理スレッドがタイムアウト — FinalResult をスキップ");
        }
    }

    // ────────────────────────────────────────────────
    //  録音データ取得 (Whisper 後処理用)
    // ────────────────────────────────────────────────
    public byte[] GetMicRecording() => _micRecording.ToArray();

    public byte[] GetSpeakerRecording() => _speakerRecording.ToArray();

    public void SaveMicRecordingAsWav(string path) => _micRecording.SaveAsWav(path);

    public void SaveSpeakerRecordingAsWav(string path) => _speakerRecording.SaveAsWav(path);

    public void ClearRecordings()
    {
        _micRecording.Clear();
        _speakerRecording.Clear();
    }

    public void StartStreamingRecording(string? micWavPath, string? spkWavPath)
    {
        if (micWavPath != null) _micRecording.StartStreaming(micWavPath);
        if (spkWavPath != null) _speakerRecording.StartStreaming(spkWavPath);
    }

    public void StopStreamingRecording()
    {
        _micRecording.StopStreaming();
        _speakerRecording.StopStreaming();
    }

    public bool IsStreamingRecording => _micRecording.IsStreaming || _speakerRecording.IsStreaming;

    public long MicRecordingLength => _micRecording.Length;

    public long SpeakerRecordingLength => _speakerRecording.Length;

    // ────────────────────────────────────────────────
    //  破棄
    // ────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;

        // Stop() が未呼出の場合に安全に停止 (処理スレッドが認識器にアクセス中に破棄しない)
        if (!_stopping) Stop();

        _disposed = true;

        _micCapture.DataAvailable -= OnMicDataAvailable;
        _speakerCapture.DataAvailable -= OnSpeakerDataAvailable;

        _micCapture.Dispose();
        _speakerCapture.Dispose();
        _micRecognizer?.Dispose();
        _speakerRecognizer?.Dispose();

        // 録音バッファを確実に解放
        _micRecording.Dispose();
        _speakerRecording.Dispose();

        // Producer-Consumer キューをクリア
        while (_micQueue.TryDequeue(out _)) { }
        while (_spkQueue.TryDequeue(out _)) { }

        // 所有権がある場合のみ Model を破棄
        if (_ownsModel)
            _model.Dispose();

        GC.SuppressFinalize(this);
    }
}
