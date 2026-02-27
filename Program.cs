using TalkTranscript;
using TalkTranscript.Audio;
using TalkTranscript.Models;
using TalkTranscript.Transcribers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using Spectre.Console;
using Vosk;

// ── 初期化 ──
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;
ConsoleHelper.SetFont("BIZ UDゴシック", 18);

bool testMode = args.Contains("--test");
bool diagMode = args.Contains("--diag");
bool whisperOnly = args.Contains("--whisper-only");  // Whisper後処理のみテスト
int testSeconds = 12;

// エンジン選択: --engine 引数 > 設定ファイル > 環境推奨
var hwProfile = HardwareInfo.Detect();
string engineName;
bool useGpu;
int engineIdx = Array.IndexOf(args, "--engine");
if (engineIdx >= 0 && engineIdx + 1 < args.Length)
{
    engineName = args[engineIdx + 1].ToLowerInvariant();
    useGpu = !args.Contains("--cpu");
}
else
{
    var savedSettings = AppSettings.Load();
    if (savedSettings.EngineName != null)
    {
        // 保存済み設定を使用
        engineName = savedSettings.EngineName.ToLowerInvariant();
        useGpu = savedSettings.UseGpu;
    }
    else
    {
        // 初回起動: 環境に最適なエンジンを自動選択
        engineName = hwProfile.RecommendedEngine;
        useGpu = hwProfile.RecommendedUseGpu;
    }
}

// Whisper モデルサイズの解析
string? whisperModelSize = null;
if (engineName.StartsWith("whisper-"))
    whisperModelSize = engineName.Substring("whisper-".Length); // "tiny", "base" etc.

// ── CUDA 利用可否チェック & ランタイムセットアップ ──
bool cudaAvailable = IsCudaAvailable();
if (useGpu && cudaAvailable)
{
    SetupCudaRuntime();
}
else if (useGpu && !cudaAvailable)
{
    useGpu = false;
    AnsiConsole.MarkupLine("  [yellow]⚠ CUDA Toolkit (cublas64_13.dll) が見つかりません。CPU モードで動作します。[/]");
    AnsiConsole.MarkupLine("    GPU を有効化するには CUDA Toolkit 13 をインストールしてください:");
    AnsiConsole.MarkupLine("    [link]https://developer.nvidia.com/cuda-downloads[/]");
    AnsiConsole.WriteLine();
}

// ── 診断モード ──
if (diagMode)
{
    RunDiagnostics();
    return;
}

// ── Whisper モデルのみダウンロード ──
if (whisperOnly)
{
    // --whisper-only [size] でサイズ指定可能 (デフォルト: base)
    string dlSize = "base";
    int woIdx = Array.IndexOf(args, "--whisper-only");
    if (woIdx >= 0 && woIdx + 1 < args.Length && !args[woIdx + 1].StartsWith("--"))
        dlSize = args[woIdx + 1].ToLowerInvariant();
    Console.WriteLine($"Whisper {dlSize} モデルをダウンロードします...");
    await ModelManager.EnsureWhisperModelAsync(dlSize);
    Console.WriteLine("完了しました。");
    return;
}

if (!testMode)
{
    Console.TreatControlCAsInput = true;
}

// ── デバイス読み込み ──
var settings = AppSettings.Load();
bool hasSavedDevices = !string.IsNullOrEmpty(settings.MicrophoneDeviceId)
                    && !string.IsNullOrEmpty(settings.SpeakerDeviceId);

NAudio.CoreAudioApi.MMDevice micDevice;
NAudio.CoreAudioApi.MMDevice speakerDevice;

if (hasSavedDevices)
{
    try
    {
        (micDevice, speakerDevice) = DeviceSelector.LoadSavedDevices(settings);
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

// ── ファイル出力準備 (セッション全体で1ファイル) ──
var outputDir = Path.Combine(AppContext.BaseDirectory, "Transcripts");
Directory.CreateDirectory(outputDir);
var fileName = $"transcript_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
var filePath = Path.Combine(outputDir, fileName);
using var writer = new TranscriptWriter(filePath);

var overallStart = DateTime.Now;
int totalMicCount = 0;
int totalSpkCount = 0;

// ── セッションループ ──
// Ctrl+D/E で停止→設定変更→再開、Ctrl+Q で終了
bool quit = false;
ICallTranscriber? lastTranscriber = null;

// ── ×ボタン / プロセス終了時の安全なシャットダウン ──
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try
    {
        lastTranscriber?.Stop();
        lastTranscriber?.Dispose();
        writer.Close();
    }
    catch { }
};
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // プロセスを即死させない
    try
    {
        lastTranscriber?.Stop();
        lastTranscriber?.Dispose();
        writer.Close();
    }
    catch { }
    Environment.Exit(0);
};

while (!quit)
{
    // ── バナー表示 ──
    SpectreUI.PrintBanner(engineName, useGpu, micDevice.FriendlyName,
                          speakerDevice.FriendlyName, Path.GetFileName(filePath),
                          testMode, testSeconds);

    // ── Whisper モデルサイズ解析 ──
    whisperModelSize = engineName.StartsWith("whisper-")
        ? engineName.Substring("whisper-".Length)
        : null;

    // ── トランスクライバ作成 ──
    // 後処理 (Whisper 再認識) が可能な場合のみ録音バッファを有効化
    bool canPostProcess = whisperModelSize == null
        && (ModelManager.GetWhisperModelPath("base") != null || ModelManager.GetWhisperModelPath("tiny") != null);

    ICallTranscriber callTranscriber;
    if (whisperModelSize != null)
    {
        string wModelPath = await ModelManager.EnsureWhisperModelAsync(whisperModelSize);
        callTranscriber = new WhisperCallTranscriber(wModelPath, whisperModelSize, micDevice, speakerDevice, useGpu);
    }
    else if (engineName == "vosk")
    {
        string voskModelPath = await ModelManager.EnsureVoskModelAsync();
        var voskModel = new Model(voskModelPath);
        callTranscriber = new VoskCallTranscriber(voskModel, micDevice, speakerDevice,
            ownsModel: true, enableRecording: canPostProcess);
    }
    else
    {
        callTranscriber = new SapiCallTranscriber(micDevice, speakerDevice, "ja-JP");
    }

    lastTranscriber = callTranscriber;
    int sessionMic = 0, sessionSpk = 0;

    // ── SpectreUI セットアップ ──
    var ui = new SpectreUI();
    ui.Configure(engineName, useGpu, micDevice.FriendlyName, speakerDevice.FriendlyName,
                 Path.GetFileName(filePath), testMode, testSeconds);

    void OnTranscribed(TranscriptEntry entry)
    {
        ui.AddEntry(entry);
        writer.Append(entry);
        if (entry.Speaker == "自分") { sessionMic++; totalMicCount++; }
        else { sessionSpk++; totalSpkCount++; }
    }

    callTranscriber.OnTranscribed += OnTranscribed;

    // ── Whisper 処理中通知 ──
    if (callTranscriber is WhisperCallTranscriber wt)
    {
        wt.OnProcessingStarted += (speaker, dur) => ui.SetProcessing(speaker, dur);
        wt.OnProcessingCompleted += speaker => ui.ClearProcessing(speaker);
    }

    try
    {
        callTranscriber.Start();
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"  [red]音声認識の開始に失敗しました: {Markup.Escape(ex.Message)}[/]");
        callTranscriber.Dispose();
        return;
    }

    // ── タイトルバー ──
    if (!testMode)
        Console.Title = "通話文字起こし ● 録音中";

    // ── Live 録音セッション ──
    string action = ui.RunSession(overallStart);

    // ── 停止 ──
    AnsiConsole.MarkupLine("[dim]  停止中...[/]");
    callTranscriber.Stop();

    // ── セッション統計 ──
    var sessionElapsed = DateTime.Now - overallStart;
    AnsiConsole.MarkupLine($"[dim]  ─── セッション: 自分 {sessionMic}件 / 相手 {sessionSpk}件 ───[/]");
    AnsiConsole.WriteLine();

    // ── アクション処理 ──
    switch (action)
    {
        case "quit":
            // Whisper 後処理
            RunPostProcessing(callTranscriber, whisperModelSize, writer, filePath, overallStart);
            quit = true;
            break;

        case "device":
            callTranscriber.Dispose();
            SpectreUI.PrintSectionHeader("デバイス変更");
            (micDevice, speakerDevice) = DeviceSelector.SelectDevices(settings);
            Console.WriteLine();
            break;

        case "engine":
            callTranscriber.Dispose();
            SpectreUI.PrintSectionHeader("エンジン変更");
            var newEngine = SelectEngineMenu(engineName);
            if (newEngine != null)
            {
                engineName = newEngine;
                var es = AppSettings.Load();
                es.EngineName = engineName;
                es.Save();
            }
            Console.WriteLine();
            break;

        case "gpu":
            callTranscriber.Dispose();
            if (!useGpu && !cudaAvailable)
            {
                AnsiConsole.MarkupLine("  [yellow]⚠ CUDA Toolkit (cublas64_13.dll) が未インストールのため GPU に切り替えできません[/]");
            }
            else
            {
                useGpu = !useGpu;
                var gs = AppSettings.Load();
                gs.UseGpu = useGpu;
                gs.Save();
                if (useGpu)
                    AnsiConsole.MarkupLine("  [green]→ GPU (CUDA) モードに切り替えました[/]");
                else
                    AnsiConsole.MarkupLine("  [yellow]→ CPU モードに切り替えました[/]");
            }
            Console.WriteLine();
            break;
    }

    if (action == "quit")
        callTranscriber.Dispose();
}

// ── 最終出力 ──
writer.Close();
SpectreUI.PrintSummary(filePath, totalMicCount, totalSpkCount, DateTime.Now - overallStart);

// ══════════════════════════════════════════════════
//  ヘルパー関数
// ══════════════════════════════════════════════════

string? SelectEngineMenu(string currentEngine)
{
    var engines = new[]
    {
        ("vosk",           "Vosk",           "リアルタイム・オフライン"),
        ("whisper-tiny",   "Whisper tiny",   "~39MB  準リアルタイム・高速"),
        ("whisper-base",   "Whisper base",   "~142MB 準リアルタイム・標準"),
        ("whisper-small",  "Whisper small",  "~466MB 準リアルタイム・高精度"),
        ("whisper-medium", "Whisper medium", "~1.5GB 高精度"),
        ("whisper-large",  "Whisper large",  "~3.1GB 最高精度"),
        ("sapi",           "SAPI",           "Windows 標準")
    };

    // 環境情報表示
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write("  環境: ");
    Console.ForegroundColor = ConsoleColor.White;
    if (hwProfile.HasNvidiaGpu)
        Console.Write($"{hwProfile.GpuName} ({hwProfile.GpuVramMB / 1024}GB)");
    else
        Console.Write($"CPU {hwProfile.CpuCores}コア / RAM {hwProfile.SystemRamMB / 1024}GB");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine();

    Console.WriteLine("  エンジンを選択:");
    Console.WriteLine();

    for (int i = 0; i < engines.Length; i++)
    {
        var (id, label, desc) = engines[i];
        bool isCurrent = id == currentEngine;
        bool isRecommended = id == hwProfile.RecommendedEngine;
        string rating = HardwareInfo.GetRecommendation(id, hwProfile);

        // 番号
        Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.Gray;
        Console.Write($"  {i + 1}. ");

        // 推奨マーク
        if (rating == "★")
            Console.ForegroundColor = ConsoleColor.Green;
        else if (rating == "△")
            Console.ForegroundColor = ConsoleColor.Yellow;
        else if (rating == "✕")
            Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"{rating,-2}");

        // ラベル
        Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.White;
        Console.Write($"{label,-16}");

        // 説明
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(desc);

        // 現在 / 推奨タグ
        if (isCurrent)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(" ← 現在");
        }
        else if (isRecommended)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(" ← 推奨");
        }

        Console.ResetColor();
        Console.WriteLine();
    }

    Console.WriteLine();
    Console.Write($"  番号 [1-{engines.Length}] (Enter でキャンセル): ");

    string? input = Console.ReadLine();
    if (int.TryParse(input, out int choice) && choice >= 1 && choice <= engines.Length)
    {
        var (selId, selLabel, _) = engines[choice - 1];
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  → {selLabel}");
        Console.ResetColor();
        return selId;
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  キャンセルしました");
        Console.ResetColor();
        return null;
    }
}

void RunPostProcessing(ICallTranscriber transcriber, string? whisperSize,
                       TranscriptWriter w, string fp, DateTime callStart)
{
    var micPcm = transcriber.GetMicRecording();
    var spkPcm = transcriber.GetSpeakerRecording();

    string? whisperPostModelPath = whisperSize != null
        ? null
        : (ModelManager.GetWhisperModelPath("base") ?? ModelManager.GetWhisperModelPath("tiny"));
    bool hasRecording = micPcm.Length > 0 || spkPcm.Length > 0;

    if (whisperPostModelPath != null && hasRecording)
    {
        // 録音が短すぎる場合はスキップ (16kHz/16bit/mono = 32KB/秒)
        long minBytes = 32_000 * 2; // 最低2秒
        if (micPcm.Length < minBytes && spkPcm.Length < minBytes)
        {
            AnsiConsole.MarkupLine("  [dim](録音が短すぎるため後処理スキップ)[/]");
            return;
        }

        AnsiConsole.MarkupLine("  [dim]Whisper 後処理を実行中...[/]");
        try
        {
            var whisperEntries = WhisperPostProcessor.ProcessAsync(
                whisperPostModelPath, micPcm, spkPcm, callStart, useGpu).GetAwaiter().GetResult();
            if (whisperEntries.Count > 0)
            {
                w.Close();
                w.Dispose();
                using var ww = new TranscriptWriter(fp);
                foreach (var entry in whisperEntries) ww.Append(entry);
                ww.Close();
                AnsiConsole.MarkupLine("  [green]Whisper 後処理で更新しました。[/]");
                return;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [yellow]Whisper 後処理エラー: {Markup.Escape(ex.Message)}[/]");
        }
    }
    else if (whisperPostModelPath == null && hasRecording && whisperSize == null)
    {
        AnsiConsole.MarkupLine("  [dim](Whisper モデルなし → 後処理スキップ。dotnet run -- --whisper-only でダウンロード可能)[/]");
    }
}

/// <summary>
/// CUDA DLL (runtimes/cuda/win-x64/) と CUDA Toolkit (cublas64_13.dll) が
/// 利用可能かチェック。コピー不要 — ランタイムで PATH + プリロードで解決。
/// </summary>
bool IsCudaAvailable()
{
    var exeDir = AppContext.BaseDirectory;
    var cudaDir = Path.Combine(exeDir, "runtimes", "cuda", "win-x64");
    if (!File.Exists(Path.Combine(cudaDir, "ggml-cuda-whisper.dll")))
        return false;
    return FindCudaToolkitBinDir() != null;
}

/// <summary>CUDA Toolkit の bin ディレクトリ (cublas64_13.dll が存在する場所) を検索</summary>
string? FindCudaToolkitBinDir()
{
    var candidates = new List<string>();
    var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
    if (!string.IsNullOrEmpty(cudaPath))
    {
        candidates.Add(Path.Combine(cudaPath, "bin", "x64"));
        candidates.Add(Path.Combine(cudaPath, "bin"));
    }
    // 既知のインストールパスを幅広く検索
    foreach (var ver in new[] { "v13.1", "v13.0", "v12.6", "v12.5", "v12.4" })
    {
        candidates.Add($@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\{ver}\bin\x64");
        candidates.Add($@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\{ver}\bin");
    }
    foreach (var dir in candidates)
    {
        if (File.Exists(Path.Combine(dir, "cublas64_13.dll")))
            return dir;
    }
    return null;
}

/// <summary>
/// CUDA DLL をプリロードして CPU 版より先にロードさせる。
/// PATH に CUDA Toolkit ディレクトリを追加し、依存解決も可能にする。
/// </summary>
void SetupCudaRuntime()
{
    var exeDir = AppContext.BaseDirectory;
    var cudaDir = Path.Combine(exeDir, "runtimes", "cuda", "win-x64");
    var toolkitDir = FindCudaToolkitBinDir();

    // PATH に CUDA ディレクトリを追加 (cublas64_13.dll, cublasLt64_13.dll 等の依存解決用)
    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    var newPaths = new List<string>();
    if (Directory.Exists(cudaDir)) newPaths.Add(cudaDir);
    if (toolkitDir != null) newPaths.Add(toolkitDir);
    if (newPaths.Count > 0)
        Environment.SetEnvironmentVariable("PATH", string.Join(";", newPaths) + ";" + path);

    // CUDA 版 DLL をプリロード (依存順: base → cuda → whisper)
    // Windows はモジュール名が既にロード済みなら同名の別 DLL をロードしない
    foreach (var dll in new[] { "ggml-base-whisper.dll", "ggml-cpu-whisper.dll",
                                "ggml-cuda-whisper.dll", "ggml-whisper.dll", "whisper.dll" })
    {
        var fullPath = Path.Combine(cudaDir, dll);
        if (File.Exists(fullPath))
        {
            NativeLibrary.TryLoad(fullPath, out _);
        }
    }
}

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