using TalkTranscript;
using TalkTranscript.Audio;
using TalkTranscript.Logging;
using TalkTranscript.Models;
using TalkTranscript.Output;
using TalkTranscript.Transcribers;
using System.Globalization;
using System.Speech.Recognition;
using Spectre.Console;
using Vosk;

// ── 初期化 ──
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;
ConsoleHelper.SetFont("BIZ UDゴシック", 18);
AppLogger.Initialize(AppLogger.LogLevel.Debug);
Vosk.Vosk.SetLogLevel(-1);

bool testMode = args.Contains("--test");
bool diagMode = args.Contains("--diag");
bool whisperOnly = args.Contains("--whisper-only");
bool configMode = args.Contains("--config");
int testSeconds = 12;

// ── 設定読み込み (1回だけ) ──
var hwProfile = HardwareInfo.Detect();
var settings = AppSettings.Load();

// ── プロセス優先度の適用 ──
ConfigMenu.ApplyProcessPriority(settings.ProcessPriority);

// エンジン選択: --engine 引数 > 設定ファイル > 環境推奨
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
    engineName = (settings.EngineName ?? hwProfile.RecommendedEngine).ToLowerInvariant();
    useGpu = settings.EngineName != null ? settings.UseGpu : hwProfile.RecommendedUseGpu;
}

// Whisper モデルサイズの解析
string? whisperModelSize = null;
if (engineName.StartsWith("whisper-"))
    whisperModelSize = engineName.Substring("whisper-".Length); // "tiny", "base" etc.

// ── CUDA 利用可否チェック & ランタイムセットアップ ──
bool cudaAvailable = CudaHelper.IsCudaAvailable();
if (useGpu && cudaAvailable)
{
    CudaHelper.SetupRuntime();
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
    AppLogger.Close();
    return;
}

// ── 設定メニュー (#9) ──
if (configMode)
{
    ConfigMenu.Show(hwProfile);
    AppLogger.Close();
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

// ── 言語設定 (#7) ──
string language = "ja"; // デフォルト
int langIdx = Array.IndexOf(args, "--lang");
if (langIdx >= 0 && langIdx + 1 < args.Length)
{
    language = args[langIdx + 1].ToLowerInvariant();
}
else if (!string.IsNullOrEmpty(settings.Language))
{
    language = settings.Language;
}
AppLogger.Info($"認識言語: {ConfigMenu.FormatLanguageDisplay(language)}");

// ── 出力フォーマット設定 (#1) ──
var extraFormats = EngineSelector.ParseFormats(args);
if (extraFormats.Count == 0 && settings.OutputFormats?.Count > 0)
{
    extraFormats = settings.OutputFormats;
}

// ── デバイス読み込み ──
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

// ── ファイル出力準備 (セッション全体で1ファイル) (#5) ──
string outputDir;
if (!string.IsNullOrEmpty(settings.OutputDirectory))
{
    outputDir = settings.OutputDirectory;
}
else
{
    outputDir = Path.Combine(AppContext.BaseDirectory, "Transcripts");
}
Directory.CreateDirectory(outputDir);
var fileName = $"transcript_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
var filePath = Path.Combine(outputDir, fileName);
using var writer = new TranscriptWriter(filePath);

var overallStart = DateTime.Now;
int totalMicCount = 0;
int totalSpkCount = 0;
var allEntries = new System.Collections.Concurrent.ConcurrentBag<TranscriptEntry>(); // 全セッションのエントリを蓄積 (エクスポート用・スレッドセーフ)

// ── セッションループ ──
// Ctrl+D/E で停止→設定変更→再開、Ctrl+Q で終了
bool quit = false;
ICallTranscriber? lastTranscriber = null;

// ── ×ボタン / プロセス終了時の安全なシャットダウン ──
int _cleanedUp = 0; // 0=未, 1=済 (Interlockedでアトミックに制御)
void EmergencyCleanup()
{
    if (Interlocked.Exchange(ref _cleanedUp, 1) != 0) return;
    try { lastTranscriber?.Stop(); } catch { }
    try { lastTranscriber?.Dispose(); } catch { }
    try { writer.Close(); } catch { }
}
AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyCleanup();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    EmergencyCleanup();
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
    // 後処理 (Whisper 再認識) が可能な場合、または録音保存が有効な場合に録音バッファを有効化
    bool canPostProcess = whisperModelSize == null
        && (ModelManager.GetWhisperModelPath("base") != null || ModelManager.GetWhisperModelPath("tiny") != null);
    bool enableRecording = canPostProcess || settings.SaveRecording;

    ICallTranscriber callTranscriber;
    if (whisperModelSize != null)
    {
        string wModelPath = await ModelManager.EnsureWhisperModelAsync(whisperModelSize);
        callTranscriber = new WhisperCallTranscriber(wModelPath, whisperModelSize, micDevice, speakerDevice, useGpu, language,
            enableRecording: settings.SaveRecording, maxCpuThreads: settings.MaxCpuThreads);
    }
    else if (engineName == "vosk")
    {
        string voskModelPath = await ModelManager.EnsureVoskModelAsync();
        var voskModel = new Model(voskModelPath);
        callTranscriber = new VoskCallTranscriber(voskModel, micDevice, speakerDevice,
            ownsModel: true, enableRecording: enableRecording);
    }
    else
    {
        string sapiCulture = language switch
        {
            "auto" => "ja-JP",
            var l when l.Contains('-') => l,
            "ja" => "ja-JP",
            "en" => "en-US",
            "zh" => "zh-CN",
            "ko" => "ko-KR",
            "fr" => "fr-FR",
            "de" => "de-DE",
            "es" => "es-ES",
            _ => language + "-" + language.ToUpperInvariant()
        };
        callTranscriber = new SapiCallTranscriber(micDevice, speakerDevice, sapiCulture,
            enableRecording: enableRecording);
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
        allEntries.Add(entry);
        if (entry.Speaker == "自分") { sessionMic++; totalMicCount++; }
        else { sessionSpk++; totalSpkCount++; }
    }

    callTranscriber.OnTranscribed += OnTranscribed;

    // ── 音量通知 (#2) ──
    callTranscriber.OnVolumeUpdated += (mic, spk) => ui.UpdateVolume(mic, spk);

    // ── ブックマーク (#3) ──
    ui.OnBookmarkRequested += () =>
    {
        var bookmark = new TranscriptEntry(
            Timestamp: DateTime.Now,
            Speaker: "📌",
            Text: "ブックマーク",
            IsBookmark: true);
        ui.AddEntry(bookmark);
        writer.Append(bookmark);
        AppLogger.Info("ブックマークを追加しました");
    };

    // ── Whisper 処理中通知 ──
    if (callTranscriber is WhisperCallTranscriber wt)
    {
        wt.OnProcessingStarted += (speaker, dur) => ui.SetProcessing(speaker, dur);
        wt.OnProcessingCompleted += speaker => ui.ClearProcessing(speaker);
    }

    try
    {
        callTranscriber.Start();
        AppLogger.Info($"音声認識開始 (エンジン: {engineName}, GPU: {useGpu})");
    }
    catch (Exception ex)
    {
        AppLogger.Error("音声認識の開始に失敗", ex);
        AnsiConsole.MarkupLine($"  [red]音声認識の開始に失敗しました: {Markup.Escape(ex.Message)}[/]");

        // 自動リトライ (#10)
        AnsiConsole.MarkupLine("  [yellow]2秒後にリトライします...[/]");
        Thread.Sleep(2000);
        try
        {
            callTranscriber.Start();
            AppLogger.Info("リトライ成功");
        }
        catch (Exception retryEx)
        {
            AppLogger.Error("リトライも失敗", retryEx);
            AnsiConsole.MarkupLine($"  [red]リトライも失敗しました: {Markup.Escape(retryEx.Message)}[/]");
            callTranscriber.Dispose();
            AppLogger.Close();
            return;
        }
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
            RunPostProcessing(callTranscriber, whisperModelSize, writer, filePath, overallStart, language);
            quit = true;
            break;

        case "config":
            callTranscriber.Dispose();
            ConfigMenu.Show(hwProfile);
            // 設定画面の残りを消してから録音セッションを再開
            Console.Clear();
            // 設定を再読み込み
            settings = AppSettings.Load();
            // --engine 引数が指定されていればそちらを優先
            if (engineIdx < 0)
            {
                engineName = (settings.EngineName ?? hwProfile.RecommendedEngine).ToLowerInvariant();
                useGpu = settings.EngineName != null ? settings.UseGpu : hwProfile.RecommendedUseGpu;
            }
            // セッション開始時刻をリセット (Whisper 後処理のタイムスタンプが正しくなるよう)
            overallStart = DateTime.Now;
            // プロセス優先度を再適用
            ConfigMenu.ApplyProcessPriority(settings.ProcessPriority);
            // --lang 引数が指定されていればそちらを優先
            if (langIdx < 0)
                language = !string.IsNullOrEmpty(settings.Language) ? settings.Language : "ja";
            // 出力フォーマット再読み込み (--format 引数がなければ設定から)
            var argsFormats = EngineSelector.ParseFormats(args);
            extraFormats = argsFormats.Count > 0
                ? argsFormats
                : (settings.OutputFormats ?? new List<OutputFormat>());
            // デバイス再読み込み
            try
            {
                (micDevice, speakerDevice) = DeviceSelector.LoadSavedDevices(settings);
            }
            catch
            {
                Console.WriteLine("前回のデバイスが見つかりません。選択してください。");
                (micDevice, speakerDevice) = DeviceSelector.SelectDevices(settings);
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

// ── 追加フォーマットでエクスポート (#1) ──
if (extraFormats.Count > 0)
{
    var sortedEntries = allEntries.OrderBy(e => e.Timestamp).ToList();
    var exported = TranscriptExporter.Export(filePath, sortedEntries, extraFormats,
        overallStart, DateTime.Now - overallStart, engineName, language);
    foreach (var ep in exported)
    {
        AnsiConsole.MarkupLine($"  [green]✓[/] 追加出力: [white]{Markup.Escape(ep)}[/]");
    }
}

AppLogger.Close();

// ══════════════════════════════════════════════════
//  ヘルパー関数
// ══════════════════════════════════════════════════

// SelectEngineMenu は EngineSelector.SelectEngine() に移行済み

void RunPostProcessing(ICallTranscriber transcriber, string? whisperSize,
                       TranscriptWriter w, string fp, DateTime callStart, string lang)
{
    // ── 録音保存 (ストリーミング方式: byte[] にせずチャンクを順次書き出す) ──
    if (settings.SaveRecording)
    {
        SaveRecordingToWav(transcriber, fp);
    }

    string? whisperPostModelPath = whisperSize != null
        ? null
        : (ModelManager.GetWhisperModelPath("base") ?? ModelManager.GetWhisperModelPath("tiny"));
    bool hasRecording = transcriber.MicRecordingLength > 0 || transcriber.SpeakerRecordingLength > 0;

    if (whisperPostModelPath != null && hasRecording)
    {
        // 録音が短すぎる場合はスキップ (16kHz/16bit/mono = 32KB/秒)
        long minBytes = 32_000 * 2; // 最低2秒
        if (transcriber.MicRecordingLength < minBytes && transcriber.SpeakerRecordingLength < minBytes)
        {
            AnsiConsole.MarkupLine("  [dim](録音が短すぎるため後処理スキップ)[/]");
            return;
        }

        // Whisper 後処理用にのみ byte[] をロード (一時的なメモリ消費)
        var micPcm = transcriber.GetMicRecording();
        var spkPcm = transcriber.GetSpeakerRecording();

        AnsiConsole.MarkupLine("  [dim]Whisper 後処理を実行中...[/]");
        try
        {
            var whisperEntries = WhisperPostProcessor.ProcessAsync(
                whisperPostModelPath, micPcm, spkPcm, callStart, useGpu, lang, settings.MaxCpuThreads).GetAwaiter().GetResult();
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
            AppLogger.Error("Whisper 後処理エラー", ex);
            AnsiConsole.MarkupLine($"  [yellow]Whisper 後処理エラー: {Markup.Escape(ex.Message)}[/]");
        }
    }
    else if (whisperPostModelPath == null && hasRecording && whisperSize == null)
    {
        AnsiConsole.MarkupLine("  [dim](Whisper モデルなし → 後処理スキップ。dotnet run -- --whisper-only でダウンロード可能)[/]");
    }
}

/// <summary>
/// 録音データを WAV ファイルとして保存する (ストリーミング方式)。
/// RecordingBuffer のチャンクを順次書き出すため、全体を byte[] にしない。
/// </summary>
void SaveRecordingToWav(ICallTranscriber transcriber, string transcriptPath)
{
    if (transcriber.MicRecordingLength == 0 && transcriber.SpeakerRecordingLength == 0) return;

    string dir = Path.GetDirectoryName(transcriptPath) ?? ".";
    string baseName = Path.GetFileNameWithoutExtension(transcriptPath);

    try
    {
        if (transcriber.MicRecordingLength > 0)
        {
            string micPath = Path.Combine(dir, $"{baseName}_mic.wav");
            transcriber.SaveMicRecordingAsWav(micPath);
            AnsiConsole.MarkupLine($"  [green]✓[/] マイク録音: [white]{Markup.Escape(micPath)}[/]");
        }

        if (transcriber.SpeakerRecordingLength > 0)
        {
            string spkPath = Path.Combine(dir, $"{baseName}_speaker.wav");
            transcriber.SaveSpeakerRecordingAsWav(spkPath);
            AnsiConsole.MarkupLine($"  [green]✓[/] スピーカー録音: [white]{Markup.Escape(spkPath)}[/]");
        }
    }
    catch (Exception ex)
    {
        AppLogger.Error("録音データの保存に失敗", ex);
        AnsiConsole.MarkupLine($"  [yellow]録音データの保存に失敗: {Markup.Escape(ex.Message)}[/]");
    }
}

// IsCudaAvailable / FindCudaToolkitBinDir / SetupCudaRuntime は CudaHelper に移行済み

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
                int abs = Math.Abs((int)sample); // short.MinValue (-32768) でも安全
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