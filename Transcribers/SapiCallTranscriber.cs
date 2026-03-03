using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Audio;
using TalkTranscript.Logging;
using TalkTranscript.Models;

namespace TalkTranscript.Transcribers;

/// <summary>
/// マイクとスピーカーの両方の音声を 1 つの SpeechRecognitionEngine で文字起こしする。
///
/// SAPI (System.Speech) は同一プロセスで複数の SpeechRecognitionEngine を
/// 同時に RecognizeAsync(Multiple) で走らせると一方が停止する既知の制約がある。
/// この問題を回避するため、単一エンジンに対して音量レベルに基づいて
/// マイク/スピーカーの音声を切り替えて供給する。
///
/// 切り替えロジック:
///   - マイクが発話中 (ピーク &gt; 閾値) → マイク音声を供給 → "自分"
///   - マイクが無音でスピーカーが発話中 → スピーカー音声を供給 → "相手"
///   - 両方無音 → マイク音声を供給 (既定)
/// </summary>
public sealed class SapiCallTranscriber : ICallTranscriber
{
    // ── 音声認識 ──
    private readonly SpeechRecognitionEngine _engine;
    private readonly SpeechAudioStream _audioStream;

    // ── キャプチャ ──
    private readonly WaveInEvent _micCapture;
    private readonly WasapiLoopbackCapture _speakerCapture;

    // ── 結果格納 ──
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _lock = new();

    // ── 制御 ──
    private readonly ManualResetEventSlim _recognizeCompleted = new(false);
    private bool _disposed;
    private bool _recognizerStarted;

    // ── 録音バッファ ──
    private readonly RecordingBuffer _micRecording;
    private readonly RecordingBuffer _speakerRecording;

    // ── 音量トラッキングとソース切り替え ──
    /// <summary>現在アクティブなソース (true = マイク, false = スピーカー)</summary>
    private volatile bool _micActive = true;
    private volatile float _micPeak;
    private volatile float _speakerPeak;
    /// <summary>アクティブソースが切り替わった最後のタイムスタンプ</summary>
    private long _lastSwitchTicks = Environment.TickCount64;

    /// <summary>認識エンジンが音声を処理中かどうか (AudioState.Speech)</summary>
    private volatile bool _engineInSpeech;

    /// <summary>SpeechDetected 時点でのアクティブソース (認識結果の話者判定に使用)</summary>
    /// <remarks>連続発話での上書きを防ぐためキューで管理する</remarks>
    private readonly System.Collections.Concurrent.ConcurrentQueue<bool> _speechDetectedQueue = new();

    /// <summary>ソース切替のロック</summary>
    private readonly object _sourceSwitchLock = new();

    private volatile bool _stopping;

    // ── 定数 ──
    /// <summary>認識エンジンが期待するフォーマット: 16 kHz / 16 bit / mono</summary>
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    /// <summary>発話と判定するピーク閾値 (16bit PCM の絶対値)</summary>
    private const float SilenceThreshold = 300f;

    /// <summary>ソース切り替えの最小間隔 (ミリ秒)。チャタリング防止。</summary>
    private const long SwitchCooldownMs = 800;

    // ── 統計 ──
    private int _micChunks;
    private int _speakerChunks;
    private long _micWritten;
    private long _speakerWritten;
    private WaveFormat? _loopbackFormat;

    // ── Entries キャッシュ (ToList() の毎回アロケーションを回避) ──
    private IReadOnlyList<TranscriptEntry>? _cachedEntries;
    private IReadOnlyList<TranscriptEntry>? _cachedMicEntries;
    private IReadOnlyList<TranscriptEntry>? _cachedSpkEntries;

    /// <summary>認識済みの全エントリ (スレッドセーフなコピーを返す)</summary>
    public IReadOnlyList<TranscriptEntry> Entries
    {
        get { lock (_lock) { if (_cachedEntries == null) _cachedEntries = _entries.ToList(); return _cachedEntries; } }
    }

    /// <summary>マイク認識結果のみ</summary>
    public IReadOnlyList<TranscriptEntry> MicEntries
    {
        get { lock (_lock) { if (_cachedMicEntries == null) _cachedMicEntries = _entries.Where(e => e.Speaker == "自分").ToList(); return _cachedMicEntries; } }
    }

    /// <summary>スピーカー認識結果のみ</summary>
    public IReadOnlyList<TranscriptEntry> SpeakerEntries
    {
        get { lock (_lock) { if (_cachedSpkEntries == null) _cachedSpkEntries = _entries.Where(e => e.Speaker == "相手").ToList(); return _cachedSpkEntries; } }
    }

    public event Action<TranscriptEntry>? OnTranscribed;

    /// <summary>音量レベルが更新されたときに発火 (micPeak, speakerPeak)</summary>
    public event Action<float, float>? OnVolumeUpdated;

    public SapiCallTranscriber(MMDevice micDevice, MMDevice speakerDevice, string culture = "ja-JP",
        bool enableRecording = false)
    {
        _engine = new SpeechRecognitionEngine(new CultureInfo(culture));
        _audioStream = new SpeechAudioStream();

        _micRecording = new RecordingBuffer(enableRecording);
        _speakerRecording = new RecordingBuffer(enableRecording);

        // ── マイク: WaveInEvent (MME API) ──
        int deviceNumber = DeviceHelper.FindWaveInDevice(micDevice, "通話");
        _micCapture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = TargetFormat,       // 16kHz/16bit/mono — MME が自動変換
            BufferMilliseconds = 100
        };

        // ── スピーカー: WASAPI ループバック ──
        _speakerCapture = new WasapiLoopbackCapture(speakerDevice);
    }

    // FindWaveInDevice は DeviceHelper に統合済み

    /// <summary>
    /// マイク + スピーカーのキャプチャと音声認識を開始する。
    /// </summary>
    public void Start()
    {
        // ── マイク設定 ──
        Console.WriteLine($"[通話] マイク フォーマット: " +
                          $"{_micCapture.WaveFormat.SampleRate} Hz, " +
                          $"{_micCapture.WaveFormat.BitsPerSample} bit, " +
                          $"{_micCapture.WaveFormat.Channels} ch");

        _micCapture.DataAvailable += OnMicDataAvailable;
        _micCapture.RecordingStopped += (_, e) =>
        {
            Console.WriteLine("[通話] マイクキャプチャが停止しました");
            if (e.Exception != null)
                Console.WriteLine($"[通話] マイクエラー: {e.Exception.Message}");
        };

        // ── スピーカー設定 ──
        _loopbackFormat = _speakerCapture.WaveFormat;
        Console.WriteLine($"[通話] スピーカー フォーマット: " +
                          $"{_loopbackFormat.SampleRate} Hz, " +
                          $"{_loopbackFormat.BitsPerSample} bit, " +
                          $"{_loopbackFormat.Channels} ch");

        _speakerCapture.DataAvailable += OnSpeakerDataAvailable;
        _speakerCapture.RecordingStopped += (_, e) =>
        {
            Console.WriteLine("[通話] スピーカーキャプチャが停止しました");
            if (e.Exception != null)
                Console.WriteLine($"[通話] スピーカーエラー: {e.Exception.Message}");
        };

        // ── 認識エンジン設定 ──
        _engine.SetInputToAudioStream(
            _audioStream,
            new SpeechAudioFormatInfo(
                TargetFormat.SampleRate,
                AudioBitsPerSample.Sixteen,
                AudioChannel.Mono));

        _engine.LoadGrammar(new DictationGrammar());

        _engine.InitialSilenceTimeout = TimeSpan.Zero;
        _engine.BabbleTimeout = TimeSpan.Zero;
        _engine.EndSilenceTimeout = TimeSpan.FromSeconds(1.5);
        _engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(2.0);

        _engine.SpeechRecognized += OnSpeechRecognized;
        _engine.SpeechRecognitionRejected += OnSpeechRejected;
        _engine.SpeechDetected += OnSpeechDetected;
        _engine.RecognizeCompleted += OnRecognizeCompleted;
        _engine.AudioStateChanged += OnAudioStateChanged;

        // ── キャプチャ開始 ──
        // Bluetooth HFP 干渉防止のためループバックを先に安定させてからマイクを開始。
        _speakerCapture.StartRecording();
        Thread.Sleep(500);
        _micCapture.StartRecording();
    }

    // ────────────────────────────────────────────────
    //  マイク音声受信
    // ────────────────────────────────────────────────
    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        _micChunks++;

        // ピーク計算 (16bit PCM)
        short maxSample = AudioProcessing.CalcPeak(e.Buffer, e.BytesRecorded);
        _micPeak = maxSample;

        // 音量通知 (#2)
        OnVolumeUpdated?.Invoke(_micPeak, _speakerPeak);

        // 録音バッファに保存
        _micRecording.Write(e.Buffer, 0, e.BytesRecorded);

        // 初回ログ
        if (_micChunks <= 3)
            Console.WriteLine($"[通話] マイク chunk#{_micChunks}: {e.BytesRecorded} bytes, ピーク={maxSample}");

        // ソース切り替え判定
        UpdateActiveSource();

        // アクティブソースならストリームに書き込む
        if (_micActive)
        {
            _audioStream.Write(e.Buffer, 0, e.BytesRecorded);
            Interlocked.Add(ref _micWritten, e.BytesRecorded);
        }

        // エンジン開始 (初回のみ)
        if (!_recognizerStarted)
        {
            _recognizerStarted = true;
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            Console.WriteLine("[通話] 音声認識エンジンを開始しました");
        }
    }

    // ────────────────────────────────────────────────
    //  スピーカー音声受信
    // ────────────────────────────────────────────────
    private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        _speakerChunks++;

        // フォーマット変換 (48kHz/32bit/2ch → 16kHz/16bit/mono)
        byte[] converted = AudioProcessing.ConvertLoopbackToTarget(
            e.Buffer, e.BytesRecorded, _loopbackFormat!, TargetFormat);

        if (converted.Length == 0) return;

        short maxSample = AudioProcessing.CalcPeak(converted, converted.Length);
        _speakerPeak = maxSample;

        // 音量通知 (#2)
        OnVolumeUpdated?.Invoke(_micPeak, _speakerPeak);

        // 録音バッファに保存
        _speakerRecording.Write(converted, 0, converted.Length);

        // 初回ログ
        if (_speakerChunks <= 3)
            Console.WriteLine($"[通話] スピーカー chunk#{_speakerChunks}: {converted.Length} bytes, ピーク={maxSample}");

        // ソース切り替え判定
        UpdateActiveSource();

        // アクティブソースならストリームに書き込む
        if (!_micActive)
        {
            _audioStream.Write(converted, 0, converted.Length);
            Interlocked.Add(ref _speakerWritten, converted.Length);
        }
    }

    // ────────────────────────────────────────────────
    //  ソース切り替えロジック
    // ────────────────────────────────────────────────
    /// <summary>
    /// 音量レベルに基づいてアクティブソースを切り替える。
    /// チャタリング防止のため、最小切り替え間隔を設ける。
    /// 認識エンジンが音声を処理中 (AudioState.Speech) の場合は
    /// ソース切り替えを抑制し、発話の途中で音声が途切れるのを防ぐ。
    /// </summary>
    private void UpdateActiveSource()
    {
        long now = Environment.TickCount64;
        if (now - _lastSwitchTicks < SwitchCooldownMs) return;

        // 認識エンジンが音声処理中はソース切り替えを抑制
        // (自分の発話が認識途中で途切れるのを防止)
        if (_engineInSpeech) return;

        bool newMicActive;

        if (_micPeak > SilenceThreshold)
        {
            // マイクに音声あり → 自分が話している
            newMicActive = true;
        }
        else if (_speakerPeak > SilenceThreshold && _micPeak <= SilenceThreshold)
        {
            // スピーカーに音声ありかつマイク無音 → 相手が話している
            newMicActive = false;
        }
        else
        {
            // 両方無音 → 現状維持
            return;
        }

        if (newMicActive != _micActive)
        {
            _micActive = newMicActive;
            _lastSwitchTicks = now;
            string src = newMicActive ? "マイク (自分)" : "スピーカー (相手)";
            Console.WriteLine($"[通話] ソース切替: {src}");
        }
    }

    // ────────────────────────────────────────────────
    //  認識イベントハンドラ
    // ────────────────────────────────────────────────
    private void OnAudioStateChanged(object? sender, AudioStateChangedEventArgs e)
    {
        Console.WriteLine($"[通話] AudioState: {e.AudioState}");
        _engineInSpeech = (e.AudioState == AudioState.Speech);
    }

    private void OnSpeechDetected(object? sender, SpeechDetectedEventArgs e)
    {
        // 音声検出時点のソースをキューに記録し、認識完了まで 1:1 で対応付ける
        _speechDetectedQueue.Enqueue(_micActive);
        string src = _micActive ? "自分" : "相手";
        Console.WriteLine($"[通話] 音声検出 ({src}, position: {e.AudioPosition})");
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        // SpeechDetected 時点でキューに記録したソースを使う (連続発話での上書きを防止)
        bool detectedMicActive = _speechDetectedQueue.TryDequeue(out bool queued) ? queued : _micActive;
        string speaker = detectedMicActive ? "自分" : "相手";
        Console.WriteLine($"[通話] 認識 ({speaker}): ({e.Result.Text.Length}文字) (信頼度: {e.Result.Confidence:F2})");

        if (e.Result.Confidence < 0.1f) return;

        var entry = new TranscriptEntry(
            Timestamp: DateTime.Now,
            Speaker: speaker,
            Text: e.Result.Text,
            Duration: e.Result.Audio?.Duration);

        lock (_lock)
        {
            _entries.Add(entry);
            _cachedEntries = null;
            _cachedMicEntries = null;
            _cachedSpkEntries = null;
        }

        OnTranscribed?.Invoke(entry);
    }

    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        Console.WriteLine($"[通話] 棄却 (信頼度: {e.Result.Confidence:F2})");
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error != null)
            Console.WriteLine($"[通話] 認識エラー: {e.Error.Message}");
        _recognizeCompleted.Set();
    }

    // ────────────────────────────────────────────────
    //  停止
    // ────────────────────────────────────────────────
    public void Stop()
    {
        if (_stopping || _disposed) return;
        _stopping = true;

        Console.WriteLine($"[通話] 停止中... (マイク書込: {Interlocked.Read(ref _micWritten):N0}, スピーカー書込: {Interlocked.Read(ref _speakerWritten):N0})");

        try { _audioStream.Complete(); } catch { }
        try { _micCapture.StopRecording(); } catch { }
        try { _speakerCapture.StopRecording(); } catch { }

        if (_recognizerStarted)
        {
            try
            {
                _engine.RecognizeAsyncStop();
                if (!_recognizeCompleted.Wait(TimeSpan.FromSeconds(5)))
                {
                    Console.WriteLine("[通話] 認識完了待ちタイムアウト、キャンセルします");
                    _engine.RecognizeAsyncCancel();
                }
            }
            catch { /* 無視 */ }
        }

        Console.WriteLine($"[通話] マイクチャンク: {_micChunks}, スピーカーチャンク: {_speakerChunks}");
        Console.WriteLine("[通話] 音声認識を停止しました");
    }

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

        // Stop() が未呼出の場合に安全に停止 (処理中にリソースを破棄しない)
        if (!_stopping) Stop();

        _disposed = true;

        _micCapture.DataAvailable -= OnMicDataAvailable;
        _speakerCapture.DataAvailable -= OnSpeakerDataAvailable;

        _engine.SpeechRecognized -= OnSpeechRecognized;
        _engine.SpeechRecognitionRejected -= OnSpeechRejected;
        _engine.SpeechDetected -= OnSpeechDetected;
        _engine.RecognizeCompleted -= OnRecognizeCompleted;
        _engine.AudioStateChanged -= OnAudioStateChanged;

        _micCapture.Dispose();
        _speakerCapture.Dispose();
        _engine.Dispose();
        _audioStream.Dispose();
        _recognizeCompleted.Dispose();

        // 録音バッファを確実に解放
        _micRecording.Dispose();
        _speakerRecording.Dispose();

        GC.SuppressFinalize(this);
    }
}
