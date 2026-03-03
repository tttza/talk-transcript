using Spectre.Console;
using TalkTranscript.Audio;
using TalkTranscript.Logging;
using TalkTranscript.Models;
using TalkTranscript.Output;
using TalkTranscript.Translation;

namespace TalkTranscript;

/// <summary>
/// 対話型の設定メニュー (--config オプション)。
/// Spectre.Console を使ったリッチな設定画面を提供する。
/// </summary>
internal static class ConfigMenu
{
    /// <summary>
    /// 対話型の設定メニューを表示し、設定を変更する。
    /// </summary>
    public static void Show(HardwareInfo.EnvironmentProfile hwProfile)
    {
        var settings = AppSettings.Load();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel("[bold]設定メニュー[/]")
            .Border(BoxBorder.Rounded)
            .BorderStyle(Style.Parse("cyan"))
            .Padding(2, 0));
        AnsiConsole.WriteLine();

        // 初回セットアップの案内
        if (!File.Exists(AppSettings.FilePath))
        {
            if (AnsiConsole.Confirm("[cyan]  初回セットアップを開始しますか? (主要設定を順に案内します)[/]", true))
            {
                RunQuickSetup(settings, hwProfile);
                return;
            }
        }

        while (true)
        {
            // メニュー項目を毎回再構築 (変更後の値を反映)
            var menuItems = BuildMenuItems(settings, hwProfile);

            AnsiConsole.MarkupLine("[cyan]変更する項目を選択してください (↑↓ / 番号 / Enter):[/]");
            AnsiConsole.WriteLine();

            int selected = ShowInteractiveMenu(menuItems);
            AnsiConsole.WriteLine();

            if (selected == 0)
                break;

            // selected は 1-based の actionable item 番号
            int actionCount = 0;
            for (int i = 0; i < menuItems.Length; i++)
            {
                if (!menuItems[i].IsSeparator)
                {
                    actionCount++;
                    if (actionCount == selected)
                    {
                        menuItems[i].Action?.Invoke();
                        break;
                    }
                }
            }
        }
    }

    // ── ANSI エスケープ定数 ──
    private const string AnsiReset     = "\x1b[0m";
    private const string AnsiBoldCyan  = "\x1b[1;36m";
    private const string AnsiWhite     = "\x1b[37m";
    private const string AnsiDim       = "\x1b[2m";
    private const string AnsiClearLine = "\x1b[2K";
    private const string AnsiGreen     = "\x1b[32m";
    private const string AnsiDarkCyan  = "\x1b[36m";   // 値用 (dim より視認性が高い)
    private const string AnsiYellow    = "\x1b[33m";   // カテゴリ区切り用

    /// <summary>メニュー項目 (セパレータと選択可能項目を統一的に扱う)</summary>
    private readonly record struct MenuItem(
        string Label,
        string? CurrentValue,
        Action? Action,
        bool IsSeparator = false)
    {
        public static MenuItem Separator(string category) => new(category, null, null, true);
    }

    /// <summary>
    /// 現在の設定からメニュー項目一覧を構築する。
    /// カテゴリ区切り付きで、各項目に現在値を表示する。
    /// </summary>
    private static MenuItem[] BuildMenuItems(AppSettings settings, HardwareInfo.EnvironmentProfile hwProfile)
    {
        string engineVal = settings.EngineName ?? "(自動)";

        string gpuVal = settings.EffectiveGpuBackend switch
        {
            GpuBackend.Auto   => "Auto (自動)",
            GpuBackend.Cuda   => "CUDA",
            GpuBackend.Vulkan => "Vulkan",
            GpuBackend.None   => "無効 (CPU)",
            _ => "不明"
        };

        string langVal = FormatLanguageDisplay(settings.Language);

        string transVal = settings.EnableTranslation
            ? $"{settings.Language ?? "auto"} → {settings.TranslationTargetLang} ({settings.TranslationTarget})"
            : "無効";

        string outputDir = settings.OutputDirectory ?? "Transcripts/";

        string formatsVal = settings.OutputFormats?.Count > 0
            ? string.Join(", ", settings.OutputFormats.Select(f => TranscriptExporter.FormatDescription(f)))
            : "テキスト (.txt) のみ";

        string micName = SimplifyDeviceName(settings.MicrophoneDeviceName);
        string spkName = SimplifyDeviceName(settings.SpeakerDeviceName);
        string devicesVal = $"{micName} / {spkName}";

        string recVal = settings.SaveRecording
            ? (settings.SaveMicOnly ? "有効 (マイクのみ)" : "有効")
            : "無効";

        string threadVal = settings.MaxCpuThreads > 0
            ? $"{settings.MaxCpuThreads} スレッド"
            : "自動";
        string priorityVal = FormatPriorityDisplay(settings.ProcessPriority);
        string resourceVal = $"{threadVal} / {priorityVal}";

        string boostVal = settings.AudioBoostEnabled
            ? $"有効 (上限{settings.AudioBoostMaxGain}倍)"
            : "無効";

        return new MenuItem[]
        {
            MenuItem.Separator("エンジン・GPU"),
            new("エンジン",           engineVal,   () => ConfigureEngine(settings, hwProfile)),
            new("GPU (CUDA/Vulkan)", gpuVal,      () => ConfigureGpu(settings, hwProfile)),

            MenuItem.Separator("認識・翻訳"),
            new("言語",              langVal,     () => ConfigureLanguage(settings)),
            new("翻訳",              transVal,    () => ConfigureTranslation(settings)),
            new("音声ブースト",      boostVal,    () => ConfigureAudioBoost(settings)),

            MenuItem.Separator("入出力"),
            new("デバイス",          devicesVal,  () => ConfigureDevices(settings)),
            new("出力ディレクトリ",  outputDir,   () => ConfigureOutputDirectory(settings)),
            new("出力フォーマット",  formatsVal,  () => ConfigureOutputFormats(settings)),
            new("録音保存",          recVal,      () => ConfigureRecording(settings)),

            MenuItem.Separator("システム"),
            new("リソース制御",      resourceVal, () => ConfigureResources(settings)),
            new("設定をリセット",    null,        () => ResetSettings(settings)),
        };
    }

    /// <summary>デバイス名から括弧内の識別名のみを抽出する</summary>
    private static string SimplifyDeviceName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "(未設定)";
        // "ヘッドセット マイク (Logicool PRO X)" → "Logicool PRO X"
        int open = name.IndexOf('(');
        int close = name.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            string inner = name[(open + 1)..close].Trim();
            if (!string.IsNullOrEmpty(inner)) return inner;
        }
        return name;
    }

    /// <summary>
    /// 初回起動時のクイックセットアップウィザード。
    /// 主要な設定 4 項目を順番に案内する。
    /// </summary>
    private static void RunQuickSetup(AppSettings settings, HardwareInfo.EnvironmentProfile hwProfile)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]クイック セットアップ[/]").RuleStyle("dim"));
        AnsiConsole.WriteLine();

        // Step 1: エンジン
        AnsiConsole.MarkupLine("[bold cyan]Step 1/4:[/] エンジン選択");
        ConfigureEngine(settings, hwProfile);

        // Step 2: GPU
        AnsiConsole.MarkupLine("[bold cyan]Step 2/4:[/] GPU バックエンド");
        ConfigureGpu(settings, hwProfile);

        // Step 3: 言語
        AnsiConsole.MarkupLine("[bold cyan]Step 3/4:[/] 認識言語");
        ConfigureLanguage(settings);

        // Step 4: デバイス
        AnsiConsole.MarkupLine("[bold cyan]Step 4/4:[/] 入出力デバイス");
        ConfigureDevices(settings);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]セットアップ完了[/]").RuleStyle("dim"));
        AnsiConsole.MarkupLine("[dim]  他の設定を変更するには --config で設定メニューを開いてください。[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// 番号キーと矢印キーの両方で操作できるインタラクティブメニュー。
    /// カテゴリ区切りに対応し、各項目に現在値をインライン表示する。
    /// 戻り値: 1〜N (選択した項目), 0 = 戻る。
    /// </summary>
    private static int ShowInteractiveMenu(MenuItem[] items)
    {
        // コンソール入力がリダイレクトされている場合はフォールバック
        if (Console.IsInputRedirected)
            return ShowFallbackMenu(items);

        // actionable (非セパレータ) 項目のインデックスを収集
        var actionIndices = new List<int>();
        for (int i = 0; i < items.Length; i++)
            if (!items[i].IsSeparator) actionIndices.Add(i);

        int actionCount = actionIndices.Count;

        // カーソルを非表示にして描画のちらつきを抑制
        Console.CursorVisible = false;

        int cursor = 0; // 0..actionCount (actionCount = "戻る")
        int totalChoices = actionCount + 1;

        // 描画行数: 全items行 + セパレータ前の空行 (先頭除く) + 戻るの前の空行
        int separatorCount = items.Count(it => it.IsSeparator);
        int blankLines = Math.Max(0, separatorCount - 1) + 1; // 先頭以外のセパレータ前 + 戻る前
        int lineCount = items.Length + blankLines + 1; // items行 + 空行 + "戻る"行

        try
        {
            // 初回描画
            WriteMenuLines(items, actionIndices, cursor);

            while (true)
            {
                var key = Console.ReadKey(true);

                // ── 数字キー (メインキーボード & テンキー) ──
                int num = -1;
                if (key.Key >= ConsoleKey.D0 && key.Key <= ConsoleKey.D9)
                    num = key.Key - ConsoleKey.D0;
                else if (key.Key >= ConsoleKey.NumPad0 && key.Key <= ConsoleKey.NumPad9)
                    num = key.Key - ConsoleKey.NumPad0;

                if (num == 0)
                {
                    MoveUpAndRewrite(items, actionIndices, actionCount, lineCount);
                    return 0;
                }
                if (num >= 1 && num <= actionCount)
                {
                    MoveUpAndRewrite(items, actionIndices, num - 1, lineCount);
                    return num;
                }

                // ── 矢印キー ──
                bool moved = false;
                if (key.Key == ConsoleKey.UpArrow)   { cursor = (cursor - 1 + totalChoices) % totalChoices; moved = true; }
                if (key.Key == ConsoleKey.DownArrow) { cursor = (cursor + 1) % totalChoices; moved = true; }

                if (moved)
                    MoveUpAndRewrite(items, actionIndices, cursor, lineCount);

                // ── Enter / Escape ──
                if (key.Key == ConsoleKey.Enter)
                    return cursor == actionCount ? 0 : cursor + 1;

                if (key.Key == ConsoleKey.Escape)
                {
                    MoveUpAndRewrite(items, actionIndices, actionCount, lineCount);
                    return 0;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
            Console.WriteLine(); // 最終行の後に改行を入れて次の出力が重ならないようにする
        }
    }

    /// <summary>相対カーソル移動で描画開始位置に戻ってから再描画する。</summary>
    private static void MoveUpAndRewrite(MenuItem[] items, List<int> actionIndices, int cursor, int lineCount)
    {
        // カーソルを描画開始位置まで戻す (最終行は改行なしなので lineCount - 1 行上へ)
        Console.Write($"\x1b[{lineCount - 1}A\r");
        WriteMenuLines(items, actionIndices, cursor);
    }

    /// <summary>全角文字を考慮した表示幅を返す。全角は 2、半角は 1。</summary>
    private static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (char c in s)
        {
            // CJK統合漢字, ひらがな, カタカナ, CJK記号, 全角英数, 半角カナ以外のやや広い文字
            if (c >= 0x1100 && (
                (c <= 0x115F) ||                           // Hangul Jamo
                (c >= 0x2E80 && c <= 0x303E) ||            // CJK Radicals, Kangxi, CJK Symbols
                (c >= 0x3041 && c <= 0x33BF) ||            // Hiragana, Katakana, Bopomofo, etc.
                (c >= 0x3400 && c <= 0x4DBF) ||            // CJK Unified Ext A
                (c >= 0x4E00 && c <= 0xA4CF) ||            // CJK Unified, Yi
                (c >= 0xAC00 && c <= 0xD7AF) ||            // Hangul Syllables
                (c >= 0xF900 && c <= 0xFAFF) ||            // CJK Compat Ideographs
                (c >= 0xFE30 && c <= 0xFE6F) ||            // CJK Compat Forms
                (c >= 0xFF01 && c <= 0xFF60) ||            // Fullwidth Forms
                (c >= 0xFFE0 && c <= 0xFFE6)))             // Fullwidth Signs
                w += 2;
            else
                w += 1;
        }
        return w;
    }

    /// <summary>表示幅を考慮して右パディングする。</summary>
    private static string PadRightDisplay(string s, int totalWidth)
    {
        int pad = totalWidth - DisplayWidth(s);
        return pad > 0 ? s + new string(' ', pad) : s;
    }

    /// <summary>
    /// メニュー項目を Console.Write で直接描画する。
    /// カテゴリ区切り・インライン現在値に対応。最終行は改行しない。
    /// </summary>
    private static void WriteMenuLines(MenuItem[] items, List<int> actionIndices, int cursor)
    {
        var sb = new System.Text.StringBuilder();

        // ラベルの最大表示幅を算出
        int maxDisplayWidth = 0;
        foreach (var item in items)
        {
            if (!item.IsSeparator)
            {
                int dw = DisplayWidth(item.Label);
                if (dw > maxDisplayWidth) maxDisplayWidth = dw;
            }
        }

        int numWidth = actionIndices.Count >= 10 ? 2 : 1;
        int itemNum = 0;

        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].IsSeparator)
            {
                // カテゴリ区切りの前に空行 (先頭カテゴリは除く)
                if (i > 0)
                    sb.Append($"{AnsiClearLine}\n");
                sb.Append($"{AnsiClearLine}  {AnsiYellow}■ {items[i].Label}{AnsiReset}\n");
                continue;
            }

            int cursorIdx = actionIndices.IndexOf(i);
            bool selected = cursorIdx == cursor;
            string prefix = selected ? "❯" : " ";
            string labelColor = selected ? AnsiBoldCyan : AnsiWhite;
            string valColor   = selected ? AnsiGreen : AnsiDarkCyan;

            itemNum++;
            string numStr = itemNum.ToString().PadLeft(numWidth);
            string paddedLabel = PadRightDisplay(items[i].Label, maxDisplayWidth);
            string value = items[i].CurrentValue != null
                ? $" {AnsiDim}:{AnsiReset} {valColor}{items[i].CurrentValue}{AnsiReset}"
                : "";

            sb.Append($"{AnsiClearLine}  {labelColor}{prefix} {numStr}. {paddedLabel}{AnsiReset}{value}\n");
        }

        sb.Append($"{AnsiClearLine}\n"); // 最後のグループと戻るの間
        bool isBack = cursor == actionIndices.Count;
        string backColor = isBack ? AnsiBoldCyan : AnsiDim;
        string backPrefix = isBack ? "❯" : " ";
        string backNumStr = "0".PadLeft(numWidth);
        sb.Append($"{AnsiClearLine}  {backColor}{backPrefix} {backNumStr}. ← 戻る{AnsiReset}");

        Console.Write(sb.ToString());
    }

    /// <summary>入力リダイレクト時のフォールバックメニュー。</summary>
    private static int ShowFallbackMenu(MenuItem[] items)
    {
        // ラベルの最大表示幅を算出
        int maxDW = 0;
        foreach (var item in items)
            if (!item.IsSeparator && DisplayWidth(item.Label) > maxDW)
                maxDW = DisplayWidth(item.Label);

        int itemNum = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].IsSeparator)
            {
                if (i > 0) AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"  [yellow]■ {Markup.Escape(items[i].Label)}[/]");
                continue;
            }
            itemNum++;
            string padded = PadRightDisplay(items[i].Label, maxDW);
            string val = items[i].CurrentValue != null
                ? $" [dim]:[/] [darkcyan]{Markup.Escape(items[i].CurrentValue!)}[/]"
                : "";
            AnsiConsole.MarkupLine($"  [white]  {itemNum,2}. {Markup.Escape(padded)}[/]{val}");
        }
        AnsiConsole.MarkupLine($"  [dim]   0. ← 戻る[/]");
        AnsiConsole.WriteLine();

        int totalItems = itemNum;
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("  [cyan]>[/]")
                .ValidationErrorMessage("[red]  有効な番号を入力してください[/]")
                .Validate(v =>
                {
                    if (int.TryParse(v.Trim(), out int n) && n >= 0 && n <= totalItems)
                        return ValidationResult.Success();
                    return ValidationResult.Error();
                }));
        return int.Parse(input.Trim());
    }

    /// <summary>サブメニューの統一ヘッダー。タイトルと現在値を表示する。</summary>
    private static void PrintSubHeader(string title, string? currentValue = null)
    {
        AnsiConsole.WriteLine();
        string val = currentValue != null
            ? $"  [dim]現在: {Markup.Escape(currentValue)}[/]"
            : "";
        AnsiConsole.MarkupLine($"  [bold cyan]{Markup.Escape(title)}[/]{val}");
        AnsiConsole.WriteLine();
    }

    /// <summary>サブメニューの統一確認メッセージ。</summary>
    private static void PrintSaved(string label, string value)
    {
        AnsiConsole.MarkupLine($"  [green]✓ {Markup.Escape(label)}: {Markup.Escape(value)}[/]");
        AnsiConsole.WriteLine();
    }

    private static void ConfigureEngine(AppSettings settings, HardwareInfo.EnvironmentProfile hwProfile)
    {
        PrintSubHeader("エンジン", settings.EngineName ?? "(自動)");
        var newEngine = EngineSelector.SelectEngine(settings.EngineName ?? hwProfile.RecommendedEngine, hwProfile);
        if (newEngine != null)
        {
            settings.EngineName = newEngine;
            settings.Save();
            PrintSaved("エンジン", newEngine);
        }
    }

    private static void ConfigureGpu(AppSettings settings, HardwareInfo.EnvironmentProfile hwProfile)
    {
        string currentGpu = settings.EffectiveGpuBackend switch
        {
            GpuBackend.Auto => "Auto (自動)",
            GpuBackend.Cuda => "CUDA",
            GpuBackend.Vulkan => "Vulkan",
            GpuBackend.None => "無効 (CPU)",
            _ => "不明"
        };
        PrintSubHeader("GPU バックエンド", currentGpu);

        bool cudaAvailable = CudaHelper.IsCudaAvailable();
        bool vulkanAvailable = VulkanHelper.IsVulkanAvailable();

        // GPU 情報 + 利用可能状況をコンパクトに表示
        var gpuInfo = new Table().Border(TableBorder.None).HideHeaders().AddColumn("").AddColumn("");
        string gpuLabel = hwProfile.HasAnyGpu
            ? $"{Markup.Escape(hwProfile.GpuName)} ({hwProfile.GpuVramMB / 1024}GB)"
            : "(未検出)";
        gpuInfo.AddRow("[dim]GPU[/]", gpuLabel);
        gpuInfo.AddRow("[dim]CUDA[/]", cudaAvailable ? $"[green]✓ {CudaHelper.GetCudaVersionLabel()}[/]" : "[dim]✗[/]");
        gpuInfo.AddRow("[dim]Vulkan[/]", vulkanAvailable ? "[green]✓[/]" : "[dim]✗[/]");
        AnsiConsole.Write(gpuInfo);
        AnsiConsole.WriteLine();

        // 選択肢を構築
        var choices = new List<string> { "Auto (自動検出)" };
        if (cudaAvailable)
            choices.Add($"CUDA ({CudaHelper.GetCudaVersionLabel()})");
        if (vulkanAvailable)
            choices.Add("Vulkan");
        choices.Add("CPU (無効)");

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  選択:[/]")
                .HighlightStyle(Style.Parse("bold cyan"))
                .AddChoices(choices));

        if (selected.StartsWith("Auto"))
            settings.EffectiveGpuBackend = GpuBackend.Auto;
        else if (selected.StartsWith("CUDA"))
            settings.EffectiveGpuBackend = GpuBackend.Cuda;
        else if (selected.StartsWith("Vulkan"))
            settings.EffectiveGpuBackend = GpuBackend.Vulkan;
        else
            settings.EffectiveGpuBackend = GpuBackend.None;

        settings.Save();
        string newGpu = settings.EffectiveGpuBackend switch
        {
            GpuBackend.Auto => "Auto (自動)",
            GpuBackend.Cuda => "CUDA",
            GpuBackend.Vulkan => "Vulkan",
            _ => "無効 (CPU)"
        };
        PrintSaved("GPU", newGpu);
    }

    /// <summary>GPU 設定の表示用文字列を生成する</summary>
    private static string FormatGpuDisplay(AppSettings settings)
    {
        return settings.EffectiveGpuBackend switch
        {
            GpuBackend.Auto => "[green]Auto (自動)[/]",
            GpuBackend.Cuda => $"[green]CUDA[/]",
            GpuBackend.Vulkan => "[green]Vulkan[/]",
            GpuBackend.None => "[yellow]無効 (CPU)[/]",
            _ => "[dim]不明[/]"
        };
    }

    /// <summary>言語コードと表示名の対応表</summary>
    private static readonly (string Code, string Name)[] LanguageList =
    {
        ("ja", "日本語"),
        ("en", "English"),
        ("zh", "中文"),
        ("ko", "한국어"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("es", "Español"),
    };

    private static void ConfigureLanguage(AppSettings settings)
    {
        PrintSubHeader("認識言語", FormatLanguageDisplay(settings.Language));

        var choices = LanguageList.Select(l => $"{l.Code} - {l.Name}").ToList();
        choices.Add("auto - 自動検出 (Whisper のみ)");

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  選択:[/]")
                .HighlightStyle(Style.Parse("bold cyan"))
                .AddChoices(choices));

        string langCode = choice.Split(' ')[0];
        settings.Language = langCode;
        settings.Save();
        PrintSaved("言語", FormatLanguageDisplay(langCode));
    }

    /// <summary>言語設定の表示用文字列を生成する。</summary>
    internal static string FormatLanguageDisplay(string? language)
    {
        if (string.IsNullOrEmpty(language)) return "ja (日本語)";
        if (language == "auto") return "auto (自動検出)";

        var codes = language.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dict = LanguageList.ToDictionary(l => l.Code, l => l.Name, StringComparer.OrdinalIgnoreCase);

        var parts = codes.Select(c => dict.TryGetValue(c, out var name) ? $"{c} ({name})" : c);
        return string.Join(", ", parts);
    }

    private static void ConfigureOutputDirectory(AppSettings settings)
    {
        PrintSubHeader("出力ディレクトリ", settings.OutputDirectory ?? "Transcripts/ (デフォルト)");

        string input = AnsiConsole.Prompt(
            new TextPrompt<string>("  [cyan]新しいパス (Enter でデフォルトに戻す):[/]")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(input))
        {
            settings.OutputDirectory = null;
            PrintSaved("出力ディレクトリ", "Transcripts/ (デフォルト)");
        }
        else
        {
            settings.OutputDirectory = input.Trim();
            PrintSaved("出力ディレクトリ", settings.OutputDirectory);
        }
        settings.Save();
    }

    private static void ConfigureOutputFormats(AppSettings settings)
    {
        string currentFmt = settings.OutputFormats?.Count > 0
            ? string.Join(", ", settings.OutputFormats.Select(f => TranscriptExporter.FormatDescription(f)))
            : "テキスト (.txt) のみ";
        PrintSubHeader("出力フォーマット", currentFmt);

        var allFormats = Enum.GetValues<OutputFormat>();
        var currentFormats = settings.OutputFormats ?? new List<OutputFormat>();

        var prompt = new MultiSelectionPrompt<string>()
            .Title("[cyan]  スペースで切替, Enter で確定:[/]")
            .HighlightStyle(Style.Parse("bold cyan"))
            .AddChoices(allFormats.Select(f => TranscriptExporter.FormatDescription(f)))
            .InstructionsText("[dim](スペースで選択/解除)[/]");

        foreach (var f in currentFormats)
            prompt.Select(TranscriptExporter.FormatDescription(f));

        var selected = AnsiConsole.Prompt(prompt);

        settings.OutputFormats = new List<OutputFormat>();
        foreach (var sel in selected)
            foreach (var f in allFormats)
                if (TranscriptExporter.FormatDescription(f) == sel)
                    settings.OutputFormats.Add(f);

        settings.Save();
        string newFmt = settings.OutputFormats.Count > 0
            ? string.Join(", ", settings.OutputFormats.Select(f => TranscriptExporter.FormatDescription(f)))
            : "テキスト (.txt) のみ";
        PrintSaved("出力フォーマット", newFmt);
    }

    private static void ConfigureDevices(AppSettings settings)
    {
        string micCur = settings.MicrophoneDeviceName ?? "(未設定)";
        string spkCur = settings.SpeakerDeviceName ?? "(未設定)";
        PrintSubHeader("デバイス", $"{micCur} / {spkCur}");
        try
        {
            DeviceSelector.SelectDevices(settings);
            PrintSaved("マイク", settings.MicrophoneDeviceName ?? "(未設定)");
            AnsiConsole.MarkupLine($"  [green]✓ スピーカー: {Markup.Escape(settings.SpeakerDeviceName ?? "(未設定)")}[/]");
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]✗ デバイスの選択に失敗: {Markup.Escape(ex.Message)}[/]");
            AnsiConsole.WriteLine();
        }
    }

    private static void ConfigureRecording(AppSettings settings)
    {
        string curRec = settings.SaveRecording
            ? (settings.SaveMicOnly ? "有効 (マイクのみ)" : "有効")
            : "無効";
        PrintSubHeader("録音保存", curRec);

        settings.SaveRecording = AnsiConsole.Confirm("  WAV ファイルとして保存しますか?", settings.SaveRecording);
        if (settings.SaveRecording)
            settings.SaveMicOnly = AnsiConsole.Confirm("  マイクのみ保存? (スピーカー録音を除外)", settings.SaveMicOnly);
        else
            settings.SaveMicOnly = false;

        settings.Save();
        string detail = settings.SaveRecording
            ? (settings.SaveMicOnly ? "有効 (マイクのみ)" : "有効 (マイク+スピーカー)")
            : "無効";
        PrintSaved("録音保存", detail);
    }

    private static void ConfigureTranslation(AppSettings settings)
    {
        string curTrans = settings.EnableTranslation
            ? $"{settings.Language ?? "auto"} → {settings.TranslationTargetLang} ({settings.TranslationTarget})"
            : "無効";
        PrintSubHeader("翻訳", curTrans);

        settings.EnableTranslation = AnsiConsole.Confirm("  リアルタイム翻訳を有効にしますか?", settings.EnableTranslation);

        if (settings.EnableTranslation)
        {
            // 翻訳元言語 = 認識言語 (自動決定)
            settings.TranslationSourceLang = null;
            string srcDisplay = !string.IsNullOrEmpty(settings.Language) && settings.Language != "auto"
                ? settings.Language : "auto";
            AnsiConsole.MarkupLine($"  [dim]翻訳元: {srcDisplay} (認識言語から自動決定)[/]");

            if (settings.Language == "auto")
            {
                AnsiConsole.MarkupLine("  [yellow]⚠ 認識言語が auto の場合、翻訳は無効化されます。言語を固定してください。[/]");
            }
            AnsiConsole.WriteLine();

            // 翻訳先言語
            string currentLang = settings.Language ?? "auto";
            var tgtChoices = LanguagePairs.TargetLanguages
                .Where(l => !string.Equals(l.Code, currentLang, StringComparison.OrdinalIgnoreCase))
                .Select(l => $"{l.Code} - {l.Name}").ToList();

            var tgtChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]  翻訳先言語:[/]")
                    .HighlightStyle(Style.Parse("bold cyan"))
                    .AddChoices(tgtChoices));
            settings.TranslationTargetLang = tgtChoice.Split(' ')[0];

            // 翻訳対象
            var targetChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]  翻訳対象:[/]")
                    .HighlightStyle(Style.Parse("bold cyan"))
                    .AddChoices("相手 — 相手の発言のみ翻訳", "自分 — 自分の発言のみ翻訳", "両方 — 全ての発言を翻訳"));
            settings.TranslationTarget = targetChoice.Split(' ')[0];

            // GPU
            settings.TranslationUseGpu = AnsiConsole.Confirm("  翻訳で GPU を使用しますか?", settings.TranslationUseGpu);


            // 言語ペアの有効性を確認
            string effectiveSrc = !string.IsNullOrEmpty(settings.Language) && settings.Language != "auto"
                ? settings.Language : "en";
            string effectiveTgt = settings.TranslationTargetLang;

            if (string.Equals(effectiveSrc, effectiveTgt, StringComparison.OrdinalIgnoreCase))
            {
                effectiveTgt = string.Equals(effectiveSrc, "ja", StringComparison.OrdinalIgnoreCase) ? "en" : "ja";
                AnsiConsole.MarkupLine($"  [dim]同一言語のため方向を入れ替え: {effectiveSrc}→{effectiveTgt}[/]");
            }

            if (!LanguagePairs.IsSupported(effectiveSrc, effectiveTgt)
                && LanguagePairs.IsSupported(effectiveTgt, effectiveSrc))
            {
                AnsiConsole.MarkupLine($"  [dim]{effectiveSrc}→{effectiveTgt} 未サポート → 逆方向に切替[/]");
                (effectiveSrc, effectiveTgt) = (effectiveTgt, effectiveSrc);
            }

            if (!LanguagePairs.IsSupported(effectiveSrc, effectiveTgt))
            {
                AnsiConsole.MarkupLine($"  [yellow]⚠ {effectiveSrc}→{effectiveTgt} は未サポート[/]");
                AnsiConsole.MarkupLine("  [dim]サポート言語ペア:[/]");
                foreach (var (key, desc) in LanguagePairs.GetAllPairs())
                    AnsiConsole.MarkupLine($"    [dim]{key}: {desc}[/]");
            }

            PrintSaved("翻訳", $"{effectiveSrc}→{effectiveTgt} (対象:{settings.TranslationTarget}, {(settings.TranslationUseGpu ? "GPU" : "CPU")})");
        }
        else
        {
            PrintSaved("翻訳", "無効");
        }

        settings.Save();
    }

    private static void ConfigureResources(AppSettings settings)
    {
        int totalCores = Environment.ProcessorCount;
        int currentThreads = settings.MaxCpuThreads > 0
            ? settings.MaxCpuThreads
            : Math.Max(1, totalCores - 4);
        string curRes = $"{(settings.MaxCpuThreads > 0 ? $"{settings.MaxCpuThreads} スレッド" : "自動")} / {FormatPriorityDisplay(settings.ProcessPriority)}";
        PrintSubHeader("リソース制御", curRes);

        AnsiConsole.MarkupLine($"  [dim]CPU {totalCores} コア / 現在 {currentThreads} スレッド[/]");
        AnsiConsole.WriteLine();

        // ── CPU スレッド数 ──
        var threadChoices = new List<string>();
        for (int t = 1; t <= Math.Max(1, totalCores - 2); t++)
        {
            string label = t == 1 ? "1 (最小 — 他のアプリ優先)"
                         : t <= totalCores / 4 ? $"{t} (軽量)"
                         : t <= totalCores / 2 ? $"{t} (バランス)"
                         : $"{t} (高速 — PC が重くなる可能性あり)";
            threadChoices.Add(label);
        }
        threadChoices.Insert(0, $"自動 ({Math.Max(1, totalCores - 4)} スレッド)");

        var threadChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  CPU スレッド数:[/]")
                .PageSize(12)
                .HighlightStyle(Style.Parse("bold cyan"))
                .AddChoices(threadChoices));

        if (threadChoice.StartsWith("自動"))
            settings.MaxCpuThreads = 0;
        else
        {
            var numStr = threadChoice.Split(' ')[0];
            if (int.TryParse(numStr, out int threads))
                settings.MaxCpuThreads = threads;
        }

        // ── プロセス優先度 ──
        var priorityChoices = new[]
        {
            "Normal — 通常 (デフォルト)",
            "BelowNormal — やや低い (他アプリと共存しやすい)",
            "Idle — 最低 (他アプリが完全に優先される)",
        };

        var priorityChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  プロセス優先度:[/]")
                .HighlightStyle(Style.Parse("bold cyan"))
                .AddChoices(priorityChoices));
        settings.ProcessPriority = priorityChoice.Split(' ')[0];

        settings.Save();
        ApplyProcessPriority(settings.ProcessPriority);

        string threadLabel = settings.MaxCpuThreads > 0 ? $"{settings.MaxCpuThreads} スレッド" : "自動";
        PrintSaved("リソース", $"{threadLabel} / {FormatPriorityDisplay(settings.ProcessPriority)}");
        AnsiConsole.MarkupLine("  [dim]スレッド数は次のセッションから適用されます。[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>プロセス優先度を適用する</summary>
    internal static void ApplyProcessPriority(string? priority)
    {
        try
        {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            p.PriorityClass = priority?.ToLowerInvariant() switch
            {
                "belownormal" => System.Diagnostics.ProcessPriorityClass.BelowNormal,
                "idle"        => System.Diagnostics.ProcessPriorityClass.Idle,
                _             => System.Diagnostics.ProcessPriorityClass.Normal,
            };
            AppLogger.Info($"プロセス優先度を {priority ?? "Normal"} に設定");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"プロセス優先度の変更に失敗: {ex.Message}");
        }
    }

    /// <summary>優先度設定の表示用文字列</summary>
    internal static string FormatPriorityDisplay(string? priority)
    {
        return priority?.ToLowerInvariant() switch
        {
            "belownormal" => "BelowNormal (やや低い)",
            "idle"        => "Idle (最低)",
            _             => "Normal (通常)",
        };
    }

    private static void ConfigureAudioBoost(AppSettings settings)
    {
        string curBoost = settings.AudioBoostEnabled
            ? $"有効 (上限{settings.AudioBoostMaxGain}倍)"
            : "無効";
        PrintSubHeader("音声ブースト (AGC)", curBoost);

        AnsiConsole.MarkupLine("  [dim]声が小さい場合に自動的に音量を増幅し、認識精度を向上させます。[/]");
        AnsiConsole.MarkupLine("  [dim]通常の声量では増幅されず、小さい声のときだけ必要な分だけ適用されます。[/]");
        AnsiConsole.MarkupLine("  [dim]ノイズも増幅されるため、静かな環境での使用を推奨します。[/]");
        AnsiConsole.WriteLine();

        settings.AudioBoostEnabled = AnsiConsole.Confirm("  音声ブースト (AGC) を有効にしますか?", settings.AudioBoostEnabled);

        if (settings.AudioBoostEnabled)
        {
            var gainChoices = new[]
            {
                "5 — 控えめ (ノイズ少)",
                "8 — バランス",
                "10 — 標準 (推奨)",
                "15 — 強め (声が非常に小さい場合)",
                "20 — 最大 (ノイズ注意)",
            };

            var gainChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]  ゲイン上限倍率 (実際の倍率は声量に応じて自動調整):[/]")
                    .HighlightStyle(Style.Parse("bold cyan"))
                    .AddChoices(gainChoices));
            settings.AudioBoostMaxGain = int.Parse(gainChoice.Split(' ')[0]);
        }

        settings.Save();
        string detail = settings.AudioBoostEnabled
            ? $"有効 (上限{settings.AudioBoostMaxGain}倍)"
            : "無効";
        PrintSaved("音声ブースト", detail);
        if (settings.AudioBoostEnabled)
        {
            AnsiConsole.MarkupLine("  [dim]次のセッションから適用されます。[/]");
            AnsiConsole.WriteLine();
        }
    }

    private static void ResetSettings(AppSettings settings)
    {
        PrintSubHeader("設定リセット");
        if (AnsiConsole.Confirm("  [yellow]すべての設定を初期値に戻しますか?[/]", false))
        {
            var fresh = new AppSettings();
            try
            {
                File.Delete(AppSettings.FilePath);
            }
            catch { }

            // 現在の参照を更新
            settings.EngineName = fresh.EngineName;
            settings.UseGpu = fresh.UseGpu;
            settings.Language = fresh.Language;
            settings.OutputDirectory = fresh.OutputDirectory;
            settings.OutputFormats = fresh.OutputFormats;
            settings.MicrophoneDeviceId = fresh.MicrophoneDeviceId;
            settings.MicrophoneDeviceName = fresh.MicrophoneDeviceName;
            settings.SpeakerDeviceId = fresh.SpeakerDeviceId;
            settings.SpeakerDeviceName = fresh.SpeakerDeviceName;
            settings.SaveRecording = fresh.SaveRecording;
            settings.SaveMicOnly = fresh.SaveMicOnly;
            settings.MaxCpuThreads = fresh.MaxCpuThreads;
            settings.ProcessPriority = fresh.ProcessPriority;
            settings.EnableTranslation = fresh.EnableTranslation;
            settings.TranslationSourceLang = fresh.TranslationSourceLang;
            settings.TranslationTargetLang = fresh.TranslationTargetLang;
            settings.TranslationTarget = fresh.TranslationTarget;
            settings.TranslationUseGpu = fresh.TranslationUseGpu;
            settings.GpuBackendName = fresh.GpuBackendName;
            settings.AudioBoostEnabled = fresh.AudioBoostEnabled;
            settings.AudioBoostMaxGain = fresh.AudioBoostMaxGain;

            PrintSaved("リセット", "すべての設定を初期値に戻しました");
        }
    }
}
