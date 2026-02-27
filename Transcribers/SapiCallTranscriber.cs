using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TalkTranscript.Audio;
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

    // ── 音量トラッキングとソース切り替え ──
    /// <summary>現在アクティブなソース (true = マイク, false = スピーカー)</summary>
    private volatile bool _micActive = true;
    private volatile float _micPeak;
    private volatile float _speakerPeak;
    /// <summary>アクティブソースが切り替わった最後のタイムスタンプ</summary>
    private long _lastSwitchTicks = Environment.TickCount64;

    // ── 定数 ──
    /// <summary>認識エンジンが期待するフォーマット: 16 kHz / 16 bit / mono</summary>
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    /// <summary>発話と判定するピーク閾値 (16bit PCM の絶対値)</summary>
    private const float SilenceThreshold = 300f;

    /// <summary>ソース切り替えの最小間隔 (ミリ秒)。チャタリング防止。</summary>
    private const long SwitchCooldownMs = 400;

    // ── 統計 ──
    private int _micChunks;
    private int _speakerChunks;
    private int _micWritten;
    private int _speakerWritten;
    private WaveFormat? _loopbackFormat;

    /// <summary>認識済みの全エントリ (スレッドセーフなコピーを返す)</summary>
    public IReadOnlyList<TranscriptEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    /// <summary>マイク認識結果のみ</summary>
    public IReadOnlyList<TranscriptEntry> MicEntries
    {
        get { lock (_lock) return _entries.Where(e => e.Speaker == "自分").ToList(); }
    }

    /// <summary>スピーカー認識結果のみ</summary>
    public IReadOnlyList<TranscriptEntry> SpeakerEntries
    {
        get { lock (_lock) return _entries.Where(e => e.Speaker == "相手").ToList(); }
    }

    /// <summary>認識結果が得られたときに発火するイベント</summary>
    public event Action<TranscriptEntry>? OnTranscribed;

    public SapiCallTranscriber(MMDevice micDevice, MMDevice speakerDevice, string culture = "ja-JP")
    {
        _engine = new SpeechRecognitionEngine(new CultureInfo(culture));
        _audioStream = new SpeechAudioStream();

        // ── マイク: WaveInEvent (MME API) ──
        int deviceNumber = FindWaveInDevice(micDevice);
        _micCapture = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = TargetFormat,       // 16kHz/16bit/mono — MME が自動変換
            BufferMilliseconds = 100
        };

        // ── スピーカー: WASAPI ループバック ──
        _speakerCapture = new WasapiLoopbackCapture(speakerDevice);
    }

    /// <summary>
    /// MMDevice の FriendlyName から WaveIn デバイス番号を検索する。
    /// WaveIn の ProductName は最大31文字に切り詰められるため、前方一致で比較する。
    /// </summary>
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
                Console.WriteLine($"[通話] WaveIn デバイス #{i}: {prodName} (マッチ)");
                return i;
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[通話] WaveIn デバイスが名前で一致しません。デフォルトを使用します。");
        Console.WriteLine($"[通話] 検索名: {targetName}");
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            Console.WriteLine($"[通話]   #{i}: {caps.ProductName}");
        }
        Console.ResetColor();
        return 0;
    }

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
        // Bluetooth HFP デバイスでは、ループバックとマイクを同時に開始すると
        // 干渉する場合がある。ループバックを先に開始して安定させてからマイクを開始する。
        _speakerCapture.StartRecording();
        Console.WriteLine("[通話] スピーカーループバックを開始 (安定待ち...)");
        Thread.Sleep(1500);
        _micCapture.StartRecording();
        Console.WriteLine("[通話] マイクキャプチャを開始しました");
    }

    // ────────────────────────────────────────────────
    //  マイク音声受信
    // ────────────────────────────────────────────────
    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        _micChunks++;

        // ピーク計算 (16bit PCM)
        short maxSample = 0;
        for (int j = 0; j + 1 < e.BytesRecorded; j += 2)
        {
            short s = BitConverter.ToInt16(e.Buffer, j);
            short abs = Math.Abs(s);
            if (abs > maxSample) maxSample = abs;
        }
        _micPeak = maxSample;

        // 初回ログ
        if (_micChunks <= 3)
            Console.WriteLine($"[通話] マイク chunk#{_micChunks}: {e.BytesRecorded} bytes, ピーク={maxSample}");

        // ソース切り替え判定
        UpdateActiveSource();

        // アクティブソースならストリームに書き込む
        if (_micActive)
        {
            _audioStream.Write(e.Buffer, 0, e.BytesRecorded);
            _micWritten += e.BytesRecorded;
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
        byte[] converted = ConvertAudio(
            e.Buffer, e.BytesRecorded, _loopbackFormat!, TargetFormat);

        if (converted.Length == 0) return;

        // ピーク計算 (変換後の16bit PCM)
        short maxSample = 0;
        for (int j = 0; j + 1 < converted.Length; j += 2)
        {
            short s = BitConverter.ToInt16(converted, j);
            short abs = Math.Abs(s);
            if (abs > maxSample) maxSample = abs;
        }
        _speakerPeak = maxSample;

        // 初回ログ
        if (_speakerChunks <= 3)
            Console.WriteLine($"[通話] スピーカー chunk#{_speakerChunks}: {converted.Length} bytes, ピーク={maxSample}");

        // ソース切り替え判定
        UpdateActiveSource();

        // アクティブソースならストリームに書き込む
        if (!_micActive)
        {
            _audioStream.Write(converted, 0, converted.Length);
            _speakerWritten += converted.Length;
        }
    }

    // ────────────────────────────────────────────────
    //  ソース切り替えロジック
    // ────────────────────────────────────────────────
    /// <summary>
    /// 音量レベルに基づいてアクティブソースを切り替える。
    /// チャタリング防止のため、最小切り替え間隔を設ける。
    /// </summary>
    private void UpdateActiveSource()
    {
        long now = Environment.TickCount64;
        if (now - _lastSwitchTicks < SwitchCooldownMs) return;

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
    //  音声フォーマット変換
    // ────────────────────────────────────────────────
    /// <summary>
    /// ループバック音声 (通常 IEEE Float 32bit) を 16kHz/16bit/mono に変換する。
    /// サンプルレート変換は単純間引き (decimation) で行う。
    /// </summary>
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

        byte[] result = new byte[outputSamples * 2]; // 16bit = 2 bytes

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

    // ────────────────────────────────────────────────
    //  認識イベントハンドラ
    // ────────────────────────────────────────────────
    private void OnAudioStateChanged(object? sender, AudioStateChangedEventArgs e)
    {
        Console.WriteLine($"[通話] AudioState: {e.AudioState}");
    }

    private void OnSpeechDetected(object? sender, SpeechDetectedEventArgs e)
    {
        string src = _micActive ? "自分" : "相手";
        Console.WriteLine($"[通話] 音声検出 ({src}, position: {e.AudioPosition})");
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        string speaker = _micActive ? "自分" : "相手";
        Console.WriteLine($"[通話] 認識 ({speaker}): \"{e.Result.Text}\" (信頼度: {e.Result.Confidence:F2})");

        if (e.Result.Confidence < 0.1f) return;

        var entry = new TranscriptEntry(
            Timestamp: DateTime.Now,
            Speaker: speaker,
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
        Console.WriteLine($"[通話] 棄却 (信頼度: {e.Result.Confidence:F2}, 候補: \"{altText}\")");
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
        Console.WriteLine($"[通話] 停止中... (マイク書込: {_micWritten:N0}, スピーカー書込: {_speakerWritten:N0})");

        _audioStream.Complete();
        _micCapture.StopRecording();
        _speakerCapture.StopRecording();

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

    /// <summary>SAPI 版では録音バッファを保持しないため空を返す</summary>
    public byte[] GetMicRecording() => Array.Empty<byte>();

    /// <summary>SAPI 版では録音バッファを保持しないため空を返す</summary>
    public byte[] GetSpeakerRecording() => Array.Empty<byte>();

    // ────────────────────────────────────────────────
    //  破棄
    // ────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
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

        GC.SuppressFinalize(this);
    }
}
