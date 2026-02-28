using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Audio;
using TalkTranscript.Models;

namespace TalkTranscript.Transcribers;

/// <summary>
/// 指定したマイクデバイスから NAudio の WaveInEvent (MME API) で音声を取得し、
/// System.Speech.Recognition.SpeechRecognitionEngine で文字起こしする。
///
/// WaveInEvent は WASAPI と異なり、Bluetooth HFP (既定の通信デバイス) を
/// 正しくハンドリングし、フォーマット変換も自動で行う。
/// </summary>
public sealed class MicTranscriber : IDisposable
{
    private readonly SpeechRecognitionEngine _engine;
    private readonly WaveInEvent _capture;
    private readonly SpeechAudioStream _audioStream;
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _recognizeCompleted = new(false);
    private bool _disposed;
    private bool _recognizerStarted;
    private int _dataChunksReceived;
    private int _totalBytesWritten;

    /// <summary>認識エンジンが期待するフォーマット: 16 kHz / 16 bit / mono</summary>
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    /// <summary>認識済みの全エントリ (スレッドセーフなコピーを返す)</summary>
    public IReadOnlyList<TranscriptEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    /// <summary>認識結果が得られたときに発火するイベント</summary>
    public event Action<TranscriptEntry>? OnTranscribed;

    /// <summary>
    /// 指定したマイクデバイスで初期化する。
    /// MMDevice → WaveIn デバイス番号のマッピングを行う。
    /// </summary>
    public MicTranscriber(MMDevice micDevice, string culture = "ja-JP")
    {
        _engine = new SpeechRecognitionEngine(new CultureInfo(culture));
        _audioStream = new SpeechAudioStream();

        int deviceNumber = DeviceHelper.FindWaveInDevice(micDevice, "マイク");
        _capture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = TargetFormat,       // 16kHz/16bit/mono — MMEが自動変換
            BufferMilliseconds = 100
        };
    }

    // FindWaveInDevice は DeviceHelper に統合済み

    /// <summary>
    /// マイクキャプチャと音声認識を開始する。
    /// </summary>
    public void Start()
    {
        Console.WriteLine($"[マイク] WaveIn フォーマット: " +
                          $"{_capture.WaveFormat.SampleRate} Hz, " +
                          $"{_capture.WaveFormat.BitsPerSample} bit, " +
                          $"{_capture.WaveFormat.Channels} ch");

        _capture.DataAvailable += OnCaptureDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

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

        // マイクキャプチャ開始
        _capture.StartRecording();
        Console.WriteLine("[マイク] マイクキャプチャを開始しました");
    }

    private void OnAudioStateChanged(object? sender, AudioStateChangedEventArgs e)
    {
        Console.WriteLine($"[マイク] AudioState: {e.AudioState}");
    }

    private void OnSpeechDetected(object? sender, SpeechDetectedEventArgs e)
    {
        Console.WriteLine($"[マイク] 音声検出 (position: {e.AudioPosition})");
    }

    /// <summary>
    /// マイクからオーディオデータを受信した際の処理。
    /// WaveInEvent は要求したフォーマット (16kHz/16bit/mono) でデータを返すため
    /// 変換は不要。そのまま SpeechAudioStream に書き込む。
    /// </summary>
    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        _audioStream.Write(e.Buffer, 0, e.BytesRecorded);
        _totalBytesWritten += e.BytesRecorded;
        _dataChunksReceived++;

        // 最初の数チャンクの音量をログ出力
        if (_dataChunksReceived <= 3 || _dataChunksReceived % 100 == 0)
        {
            short maxSample = 0;
            for (int j = 0; j + 1 < e.BytesRecorded; j += 2)
            {
                short s = BitConverter.ToInt16(e.Buffer, j);
                short abs = Math.Abs(s);
                if (abs > maxSample) maxSample = abs;
            }
            Console.WriteLine($"[マイク] chunk#{_dataChunksReceived}: {e.BytesRecorded} bytes, ピーク={maxSample}");
        }

        // データが実際に流れ始めてから認識エンジンを開始
        if (!_recognizerStarted)
        {
            _recognizerStarted = true;
            _engine.RecognizeAsync(RecognizeMode.Multiple);
            Console.WriteLine("[マイク] 音声認識エンジンを開始しました");
        }
    }

    /// <summary>
    /// 音声が認識されたときの処理。
    /// </summary>
    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        Console.WriteLine($"[マイク] 認識: \"{e.Result.Text}\" (信頼度: {e.Result.Confidence:F2})");
        if (e.Result.Confidence < 0.1f) return;

        var entry = new TranscriptEntry(
            Timestamp: DateTime.Now,
            Speaker: "自分",
            Text: e.Result.Text,
            Duration: e.Result.Audio?.Duration);

        lock (_lock)
        {
            _entries.Add(entry);
        }

        OnTranscribed?.Invoke(entry);
    }

    private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        var altText = e.Result.Alternates.Count > 0 ? e.Result.Alternates[0].Text : "(なし)";
        Console.WriteLine($"[マイク] 棄却 (信頼度: {e.Result.Confidence:F2}, 候補: \"{altText}\")");
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (e.Error != null)
        {
            Console.WriteLine($"[マイク] 認識エラー: {e.Error.Message}");
        }
        _recognizeCompleted.Set();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Console.WriteLine("[マイク] マイクキャプチャが停止しました");
        if (e.Exception != null)
        {
            Console.WriteLine($"[マイク] エラー: {e.Exception.Message}");
        }
    }

    /// <summary>
    /// マイクキャプチャと音声認識を停止する。
    /// </summary>
    public void Stop()
    {
        Console.WriteLine($"[マイク] 停止中... (書き込みバイト: {_totalBytesWritten:N0})");

        // 先にストリームを完了させて Read がブロックしないようにする
        _audioStream.Complete();
        _capture.StopRecording();

        if (_recognizerStarted)
        {
            try
            {
                // Stop = 認識中の音声を処理してから停止
                _engine.RecognizeAsyncStop();
                if (!_recognizeCompleted.Wait(TimeSpan.FromSeconds(5)))
                {
                    Console.WriteLine("[マイク] 認識完了待ちタイムアウト、キャンセルします");
                    _engine.RecognizeAsyncCancel();
                }
            }
            catch { /* 無視 */ }
        }
        Console.WriteLine("[マイク] 音声認識を停止しました");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _capture.DataAvailable -= OnCaptureDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _engine.SpeechRecognized -= OnSpeechRecognized;
        _engine.SpeechRecognitionRejected -= OnSpeechRejected;
        _engine.SpeechDetected -= OnSpeechDetected;
        _engine.RecognizeCompleted -= OnRecognizeCompleted;
        _engine.AudioStateChanged -= OnAudioStateChanged;

        _capture.Dispose();
        _engine.Dispose();
        _audioStream.Dispose();
        _recognizeCompleted.Dispose();

        GC.SuppressFinalize(this);
    }
}
