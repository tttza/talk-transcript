using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Audio;
using TalkTranscript.Models;

namespace TalkTranscript.Transcribers;

/// <summary>
/// スピーカー出力 (ループバック) を NAudio の WasapiLoopbackCapture でキャプチャし、
/// System.Speech.Recognition.SpeechRecognitionEngine で文字起こしする。
///
/// Windows.Media.SpeechRecognition.SpeechRecognizer はマイク入力専用で
/// 任意のオーディオストリームを受け付けないため、スピーカー側は
/// System.Speech を使用する。
/// </summary>
public sealed class SpeakerTranscriber : IDisposable
{
    private readonly SpeechRecognitionEngine _engine;
    private readonly WasapiLoopbackCapture _loopback;
    private readonly SpeechAudioStream _audioStream;
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _recognizeCompleted = new(false);
    private WaveFormat? _captureFormat;
    private bool _disposed;
    private bool _recognizerStarted;
    private volatile bool _stopping;
    private int _dataChunksReceived;
    private long _totalBytesWritten;

    /// <summary>認識エンジンが期待するフォーマット: 16 kHz / 16 bit / mono</summary>
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    // ── Entries キャッシュ (ToList() の毎回アロケーションを回避) ──
    private IReadOnlyList<TranscriptEntry>? _cachedEntries;

    /// <summary>認識済みの全エントリ (スレッドセーフなコピーを返す)</summary>
    public IReadOnlyList<TranscriptEntry> Entries
    {
        get { lock (_lock) { if (_cachedEntries == null) _cachedEntries = _entries.ToList(); return _cachedEntries; } }
    }

    /// <summary>認識結果が得られたときに発火するイベント</summary>
    public event Action<TranscriptEntry>? OnTranscribed;

    public SpeakerTranscriber(MMDevice speakerDevice, string culture = "ja-JP")
    {
        _engine = new SpeechRecognitionEngine(new CultureInfo(culture));
        _loopback = new WasapiLoopbackCapture(speakerDevice);
        _audioStream = new SpeechAudioStream();
    }

    /// <summary>
    /// ループバックキャプチャと音声認識を開始する。
    /// </summary>
    public void Start()
    {
        // ── WASAPI ループバック設定 ──
        _captureFormat = _loopback.WaveFormat;
        Console.WriteLine($"[スピーカー] キャプチャフォーマット: " +
                          $"{_captureFormat.SampleRate} Hz, " +
                          $"{_captureFormat.BitsPerSample} bit, " +
                          $"{_captureFormat.Channels} ch");

        _loopback.DataAvailable += OnLoopbackDataAvailable;
        _loopback.RecordingStopped += OnRecordingStopped;

        // ── System.Speech 認識エンジン設定 ──
        _engine.SetInputToAudioStream(
            _audioStream,
            new SpeechAudioFormatInfo(
                TargetFormat.SampleRate,
                AudioBitsPerSample.Sixteen,
                AudioChannel.Mono));

        // 自由発話 (ディクテーション) 文法を読み込む
        _engine.LoadGrammar(new DictationGrammar());

        // タイムアウト設定: ストリーム入力では無制限にする
        _engine.InitialSilenceTimeout = TimeSpan.Zero;
        _engine.BabbleTimeout = TimeSpan.Zero;
        _engine.EndSilenceTimeout = TimeSpan.FromSeconds(1.5);
        _engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(2.0);

        // イベントハンドラ
        _engine.SpeechRecognized += OnSpeechRecognized;
        _engine.SpeechRecognitionRejected += OnSpeechRejected;
        _engine.SpeechDetected += OnSpeechDetected;
        _engine.RecognizeCompleted += OnRecognizeCompleted;
        _engine.AudioStateChanged += OnAudioStateChanged;
        _engine.AudioLevelUpdated += OnAudioLevelUpdated;

        // ループバックキャプチャ開始 (データが流れ始めてから認識を開始)
        _loopback.StartRecording();
        Console.WriteLine("[スピーカー] ループバックキャプチャを開始しました");
    }

    private void OnAudioStateChanged(object? sender, AudioStateChangedEventArgs e)
    {
        Console.WriteLine($"[スピーカー] AudioState: {e.AudioState}");
    }

    private void OnSpeechDetected(object? sender, SpeechDetectedEventArgs e)
    {
        Console.WriteLine($"[スピーカー] 音声検出 (position: {e.AudioPosition})");
    }

    private void OnAudioLevelUpdated(object? sender, AudioLevelUpdatedEventArgs e)
    {
        if (_dataChunksReceived > 0 && _dataChunksReceived % 50 == 0 && _entries.Count == 0)
        {
            Console.Write($"\r[スピーカー] AudioLevel: {e.AudioLevel}   ");
        }
    }

    /// <summary>
    /// WASAPI ループバックからオーディオデータを受信した際の処理。
    /// 音声フォーマットを変換して SpeechAudioStream に書き込む。
    /// </summary>
    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        try
        {
            byte[] converted = AudioProcessing.ConvertLoopbackToTarget(
                e.Buffer, e.BytesRecorded, _captureFormat!, TargetFormat);

            if (converted.Length > 0)
            {
                _audioStream.Write(converted, 0, converted.Length);
                Interlocked.Add(ref _totalBytesWritten, converted.Length);

                _dataChunksReceived++;

                // データが実際に流れ始めてから認識エンジンを開始
                if (!_recognizerStarted)
                {
                    _recognizerStarted = true;
                    _engine.RecognizeAsync(RecognizeMode.Multiple);
                    Console.WriteLine("[スピーカー] 音声認識エンジンを開始しました");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[スピーカー] 音声変換エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// 音声が認識されたときの処理。
    /// </summary>
    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        Console.WriteLine($"[スピーカー] 認識: ({e.Result.Text.Length}文字) (信頼度: {e.Result.Confidence:F2})");
        if (e.Result.Confidence < 0.1f) return;

        var entry = new TranscriptEntry(
            Timestamp: DateTime.Now,
            Speaker: "相手",
            Text: e.Result.Text,
            Duration: e.Result.Audio?.Duration);

        lock (_lock)
        {
            _entries.Add(entry);
            _cachedEntries = null;
        }

        OnTranscribed?.Invoke(entry);
    }

    /// <summary>
    /// 認識できなかった音声の処理 (現在は無視)。
    /// </summary>
    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        var altText = e.Result.Alternates.Count > 0 ? e.Result.Alternates[0].Text : "(なし)";
        Console.WriteLine($"[スピーカー] 棄却 (信頼度: {e.Result.Confidence:F2}, 候補: \"{altText}\")");
    }

    /// <summary>
    /// 認識エンジンが終了したときの処理。
    /// </summary>
    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error != null)
        {
            Console.WriteLine($"[スピーカー] 認識エラー: {e.Error.Message}");
        }
        _recognizeCompleted.Set();
    }

    /// <summary>
    /// ループバックが停止したときの処理。
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Console.WriteLine("[スピーカー] ループバックキャプチャが停止しました");
        if (e.Exception != null)
        {
            Console.WriteLine($"[スピーカー] エラー: {e.Exception.Message}");
        }
    }

    /// <summary>
    /// ループバックキャプチャと音声認識を停止する。
    /// </summary>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;

        Console.WriteLine($"[スピーカー] 停止中... (変換済みバイト: {Interlocked.Read(ref _totalBytesWritten):N0})");

        _audioStream.Complete();
        _loopback.StopRecording();

        if (_recognizerStarted)
        {
            try
            {
                _engine.RecognizeAsyncStop();
                if (!_recognizeCompleted.Wait(TimeSpan.FromSeconds(5)))
                {
                    Console.WriteLine("[スピーカー] 認識完了待ちタイムアウト、キャンセルします");
                    _engine.RecognizeAsyncCancel();
                }
            }
            catch { /* 無視 */ }
        }
        Console.WriteLine("[スピーカー] 音声認識を停止しました");
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Stop() が未呼出の場合に安全に停止
        Stop();

        _disposed = true;

        _loopback.DataAvailable -= OnLoopbackDataAvailable;
        _loopback.RecordingStopped -= OnRecordingStopped;
        _engine.SpeechRecognized -= OnSpeechRecognized;
        _engine.SpeechRecognitionRejected -= OnSpeechRejected;
        _engine.SpeechDetected -= OnSpeechDetected;
        _engine.RecognizeCompleted -= OnRecognizeCompleted;
        _engine.AudioStateChanged -= OnAudioStateChanged;
        _engine.AudioLevelUpdated -= OnAudioLevelUpdated;

        _loopback.Dispose();
        _engine.Dispose();
        _audioStream.Dispose();
        _recognizeCompleted.Dispose();

        GC.SuppressFinalize(this);
    }
}
