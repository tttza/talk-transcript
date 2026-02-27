using TalkTranscript;
using TalkTranscript.Audio;
using TalkTranscript.Models;
using TalkTranscript.Transcribers;
using System.Globalization;
using System.Speech.Recognition;
using Vosk;

// ── 初期化 ──
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

bool testMode = args.Contains("--test");
bool diagMode = args.Contains("--diag");
bool whisperOnly = args.Contains("--whisper-only");  // Whisper後処理のみテスト
int testSeconds = 12;

// エンジン選択: --engine sapi / --engine vosk (デフォルト: vosk)
string engineName = "vosk";
int engineIdx = Array.IndexOf(args, "--engine");
if (engineIdx >= 0 && engineIdx + 1 < args.Length)
    engineName = args[engineIdx + 1].ToLowerInvariant();

// ── 診断モード ──
if (diagMode)
{
    RunDiagnostics();
    return;
}

// ── Whisper モデルのみダウンロード ──
if (whisperOnly)
{
    Console.WriteLine("Whisper モデルをダウンロードします...");
    await ModelManager.EnsureWhisperModelAsync();
    Console.WriteLine("完了しました。次回の通話から Whisper 後処理が有効になります。");
    return;
}

if (!testMode)
{
    Console.TreatControlCAsInput = true;
}

Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║    通話文字起こしツール               ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.WriteLine();
if (testMode) Console.WriteLine($"[テストモード] 自動で開始し、{testSeconds}秒後に停止します。");
Console.WriteLine($"認識エンジン: {engineName.ToUpperInvariant()}");
Console.WriteLine();

// ── デバイス選択 ──
var settings = AppSettings.Load();

// 前回の設定がある場合はデバイス選択をスキップして即開始
bool hasSavedDevices = !string.IsNullOrEmpty(settings.MicrophoneDeviceId)
                    && !string.IsNullOrEmpty(settings.SpeakerDeviceId);

NAudio.CoreAudioApi.MMDevice micDevice;
NAudio.CoreAudioApi.MMDevice speakerDevice;

if (hasSavedDevices)
{
    try
    {
        (micDevice, speakerDevice) = DeviceSelector.LoadSavedDevices(settings);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"マイク: {micDevice.FriendlyName}");
        Console.WriteLine($"スピーカー: {speakerDevice.FriendlyName}");
        Console.ResetColor();
        Console.WriteLine("(デバイスを変更するには Ctrl+D を押してください)");
    }
    catch
    {
        Console.WriteLine("前回のデバイスが見つかりません。選択してください。");
        (micDevice, speakerDevice) = DeviceSelector.SelectDevices(settings);
    }
}
else
{
    (micDevice, speakerDevice) = DeviceSelector.SelectDevices(settings);
}
Console.WriteLine();

// ── リアルタイム表示用コールバック ──
// ファイル出力は逐次追記で行う
var outputDir = Path.Combine(AppContext.BaseDirectory, "Transcripts");
Directory.CreateDirectory(outputDir);
var fileName = $"transcript_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
var filePath = Path.Combine(outputDir, fileName);
using var writer = new TranscriptWriter(filePath);

void OnTranscribed(TranscriptEntry entry)
{
    // コンソール表示
    var color = entry.Speaker == "自分" ? ConsoleColor.Cyan : ConsoleColor.Yellow;
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine($"  [{entry.Timestamp:HH:mm:ss}] {entry.Speaker}: {entry.Text}");
    Console.ForegroundColor = prev;

    // ファイルへ即座に追記
    writer.Append(entry);
}

// ── トランスクライバ初期化・開始 ──
ICallTranscriber callTranscriber;
DateTime callStartTime = DateTime.Now;

if (engineName == "vosk")
{
    // Vosk モデルのダウンロード/準備
    string voskModelPath = await ModelManager.EnsureVoskModelAsync();
    var voskModel = new Model(voskModelPath);
    callTranscriber = new VoskCallTranscriber(voskModel, micDevice, speakerDevice);
}
else
{
    // 旧SAPI エンジン
    callTranscriber = new SapiCallTranscriber(micDevice, speakerDevice, "ja-JP");
}

try
{
    callTranscriber.OnTranscribed += OnTranscribed;
    callTranscriber.Start();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"音声認識の開始に失敗しました: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    Console.ResetColor();
    return;
}

Console.WriteLine();
Console.WriteLine("────────────────────────────────────────");
Console.WriteLine(" 文字起こしを開始しました。");
if (testMode)
{
    Console.WriteLine($" {testSeconds}秒後に自動停止します。");
}
else
{
    Console.WriteLine(" Ctrl+Q : 録音停止して保存");
    Console.WriteLine(" Ctrl+D : デバイス変更 (次回から反映)");
}
Console.WriteLine("────────────────────────────────────────");
Console.WriteLine();

// ── 終了待ち ──
if (testMode)
{
    Thread.Sleep(testSeconds * 1000);
}
else
{
    bool running = true;
    while (running)
    {
        if (!Console.KeyAvailable)
        {
            Thread.Sleep(100);
            continue;
        }

        var key = Console.ReadKey(intercept: true);

        // Ctrl+Q で録音停止
        if (key.Key == ConsoleKey.Q && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            running = false;
        }
        // Ctrl+D でデバイス変更 (設定のみ変更、次回起動から反映)
        else if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            Console.WriteLine();
            Console.WriteLine("デバイス設定を変更します (次回起動から反映):");
            var newSettings = AppSettings.Load();
            DeviceSelector.SelectDevices(newSettings);
            Console.WriteLine();
        }
    }
}

Console.WriteLine();
Console.WriteLine("停止中...");

// ── 停止 ──
callTranscriber.Stop();

// ── Whisper 後処理判定 ──
var micPcm = callTranscriber.GetMicRecording();
var spkPcm = callTranscriber.GetSpeakerRecording();
string? whisperModelPath = ModelManager.GetWhisperModelPath();
bool hasRecording = micPcm.Length > 0 || spkPcm.Length > 0;

Console.WriteLine($"マイク認識数: {callTranscriber.MicEntries.Count}");
Console.WriteLine($"スピーカー認識数: {callTranscriber.SpeakerEntries.Count}");

// ── Whisper 後処理 (モデルがある場合) ──
// Whisper で再認識した場合はファイルを上書きする
if (whisperModelPath != null && hasRecording)
{
    Console.WriteLine();
    Console.WriteLine("Whisper による高精度後処理を実行中...");
    try
    {
        var whisperEntries = await WhisperPostProcessor.ProcessAsync(
            whisperModelPath, micPcm, spkPcm, callStartTime);
        if (whisperEntries.Count > 0)
        {
            // Whisper 結果で既存ファイルを上書き
            writer.Close();
            writer.Dispose();

            // 新しい Writer で Whisper 結果を書き直す
            using var whisperWriter = new TranscriptWriter(filePath);
            foreach (var entry in whisperEntries)
                whisperWriter.Append(entry);
            whisperWriter.Close();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Whisper による高精度テキストでファイルを更新しました。");
            Console.ResetColor();
        }
        else
        {
            writer.Close();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Whisper 後処理エラー (Vosk の結果を使用します): {ex.Message}");
        Console.ResetColor();
        writer.Close();
    }
}
else
{
    if (whisperModelPath == null && hasRecording)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("(Whisper モデルが未ダウンロードのため後処理をスキップ)");
        Console.WriteLine("(高精度な文字起こしには: dotnet run -- --whisper-only を一度実行してモデルをダウンロード)");
        Console.ResetColor();
    }
    writer.Close();
}

callTranscriber.Dispose();

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"文字起こし結果を保存しました:");
Console.WriteLine($"  {filePath}");
Console.ResetColor();

Console.WriteLine();
Console.WriteLine("完了しました。");

// ── 診断モード関数 ──
void RunDiagnostics()
{
    Console.WriteLine("=== 音声認識 診断モード ===");
    Console.WriteLine();

    // 1. インストール済み認識エンジン
    var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
    Console.WriteLine($"[1] インストール済み認識エンジン: {recognizers.Count} 件");
    foreach (var r in recognizers)
        Console.WriteLine($"    - {r.Id} | {r.Culture} | {r.Description}");
    Console.WriteLine();

    if (recognizers.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("音声認識エンジンがインストールされていません！");
        Console.ResetColor();
        return;
    }

    var jaRec = recognizers.FirstOrDefault(r => r.Culture.Name == "ja-JP");
    var culture = jaRec != null ? new CultureInfo("ja-JP") : recognizers[0].Culture;

    // 2. デバイス一覧表示
    Console.WriteLine("[2] オーディオデバイス一覧");
    using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
    var defaultCapture = enumerator.GetDefaultAudioEndpoint(
        NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Communications);
    Console.WriteLine($"    デフォルト録音デバイス: {defaultCapture.FriendlyName}");
    Console.WriteLine();

    // 3. WaveIn (MME API) で音量チェック
    Console.WriteLine("[3] WaveIn (MME) マイク音量チェック (5秒) — マイクに向かって話してください");
    Console.WriteLine();

    var settings2 = AppSettings.Load();
    NAudio.CoreAudioApi.MMDevice mic;
    try
    {
        (mic, _) = DeviceSelector.LoadSavedDevices(settings2);
        Console.WriteLine($"    使用マイク: {mic.FriendlyName}");
    }
    catch
    {
        mic = defaultCapture;
        Console.WriteLine($"    使用マイク (デフォルト): {mic.FriendlyName}");
    }

    // WaveIn デバイス検索
    int waveInDeviceNum = -1;
    Console.WriteLine("    WaveIn デバイス一覧:");
    for (int i = 0; i < NAudio.Wave.WaveIn.DeviceCount; i++)
    {
        var caps = NAudio.Wave.WaveIn.GetCapabilities(i);
        bool match = mic.FriendlyName.StartsWith(caps.ProductName, StringComparison.OrdinalIgnoreCase);
        string marker = match ? " ★" : "";
        Console.WriteLine($"      #{i}: {caps.ProductName}{marker}");
        if (match && waveInDeviceNum < 0) waveInDeviceNum = i;
    }
    if (waveInDeviceNum < 0) waveInDeviceNum = 0;
    Console.WriteLine($"    使用 WaveIn デバイス: #{waveInDeviceNum}");
    Console.WriteLine();

    float maxPeak = 0f;
    int totalBytes = 0;
    int nonZeroSamples = 0;
    int totalSamples = 0;

    using (var waveIn = new NAudio.Wave.WaveInEvent
    {
        DeviceNumber = waveInDeviceNum,
        WaveFormat = new NAudio.Wave.WaveFormat(16000, 16, 1),
        BufferMilliseconds = 100
    })
    {
        Console.WriteLine($"    要求フォーマット: {waveIn.WaveFormat}");
        waveIn.DataAvailable += (s, e) =>
        {
            totalBytes += e.BytesRecorded;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                short abs = Math.Abs(sample);
                totalSamples++;
                if (abs > maxPeak) maxPeak = abs;
                if (abs > 100) nonZeroSamples++;
            }
        };
        waveIn.StartRecording();
        Thread.Sleep(5000);
        waveIn.StopRecording();
    }

    float peakDb = maxPeak > 0 ? 20f * MathF.Log10(maxPeak / 32767f) : -100f;
    Console.WriteLine($"    合計バイト: {totalBytes:N0}");
    Console.WriteLine($"    合計サンプル: {totalSamples:N0}, 非ゼロ (>100): {nonZeroSamples:N0}");
    Console.WriteLine($"    ピーク: {maxPeak:F0} ({peakDb:F1} dBFS)");
    Console.WriteLine();

    if (maxPeak < 100)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("    ★ マイクから音声が検出されません！");
        Console.WriteLine("      マイクの接続/ミュート/Windows のプライバシー設定を確認してください。");
        Console.ResetColor();
        Console.WriteLine();
    }
    else if (peakDb < -40f)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"    ★ 音量が非常に小さいです ({peakDb:F1} dBFS)。マイクのゲインを上げてください。");
        Console.ResetColor();
        Console.WriteLine();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"    ★ 音声レベル OK ({peakDb:F1} dBFS)");
        Console.ResetColor();
        Console.WriteLine();
    }

    // 4. フルパイプラインテスト: WaveIn → SpeechAudioStream → SpeechRecognitionEngine
    Console.WriteLine("[4] WaveIn → ストリーム → System.Speech パイプラインテスト (10秒)");
    Console.WriteLine("    何か話してください...");
    Console.WriteLine();

    using var micTranscriber = new MicTranscriber(mic, culture.Name);
    int pipelineRecognized = 0;
    micTranscriber.OnTranscribed += entry =>
    {
        pipelineRecognized++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"    [認識] \"{entry.Text}\"");
        Console.ResetColor();
    };

    try
    {
        micTranscriber.Start();
        Thread.Sleep(10000);
        micTranscriber.Stop();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"    エラー: {ex.Message}");
        Console.ResetColor();
    }

    Console.WriteLine();
    Console.WriteLine($"    結果: パイプライン認識={pipelineRecognized}");
    Console.WriteLine();

    // 5. まとめ
    Console.WriteLine("=== 診断結果 ===");
    if (maxPeak < 100)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("マイクから音声が検出されません。マイクの接続を確認してください。");
        Console.ResetColor();
    }
    else if (pipelineRecognized > 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("パイプライン正常動作！音声認識が機能しています。");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("WaveIn でマイク音声は取得できましたが、認識結果がありません。");
        Console.WriteLine("音声が小さい・ノイズが多い可能性があります。");
        Console.ResetColor();
    }
}