using Spectre.Console;
using TalkTranscript.Audio;
using TalkTranscript.Logging;
using TalkTranscript.Models;
using TalkTranscript.Output;

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

        var menuItems = new (string Label, Action? Action)[]
        {
            ("エンジン",         () => ConfigureEngine(settings, hwProfile)),
            ("GPU (CUDA)",      () => ConfigureGpu(settings)),
            ("言語",             () => ConfigureLanguage(settings)),
            ("出力ディレクトリ",   () => ConfigureOutputDirectory(settings)),
            ("出力フォーマット",   () => ConfigureOutputFormats(settings)),
            ("デバイス",         () => ConfigureDevices(settings)),
            ("録音保存",         () => ConfigureRecording(settings)),
            ("リソース制御",     () => ConfigureResources(settings)),
            ("設定をリセット",    () => ResetSettings(settings)),
        };

        while (true)
        {
            // 現在の設定を表示
            PrintCurrentSettings(settings, hwProfile);

            AnsiConsole.MarkupLine("[cyan]変更する項目を選択してください (↑↓ / 番号 / Enter):[/]");
            AnsiConsole.WriteLine();

            int selected = ShowInteractiveMenu(menuItems);
            AnsiConsole.WriteLine();

            if (selected == 0)
                break;

            menuItems[selected - 1].Action?.Invoke();
        }
    }

    // ── ANSI エスケープ定数 ──
    private const string AnsiReset     = "\x1b[0m";
    private const string AnsiBoldCyan  = "\x1b[1;36m";
    private const string AnsiWhite     = "\x1b[37m";
    private const string AnsiDim       = "\x1b[2m";
    private const string AnsiClearLine = "\x1b[2K";

    /// <summary>
    /// 番号キーと矢印キーの両方で操作できるインタラクティブメニュー。
    /// 戻り値: 1〜N (選択した項目), 0 = 戻る。
    /// </summary>
    private static int ShowInteractiveMenu((string Label, Action? Action)[] items)
    {
        // コンソール入力がリダイレクトされている場合はフォールバック
        if (Console.IsInputRedirected)
            return ShowFallbackMenu(items);

        // カーソルを非表示にして描画のちらつきを抑制
        Console.CursorVisible = false;

        int cursor = 0; // 0..items.Length (items.Length = "戻る")
        int count = items.Length + 1;
        int lineCount = count; // 描画する行数

        try
        {
            // 初回描画
            WriteMenuLines(items, cursor);

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
                    MoveUpAndRewrite(items, items.Length, lineCount);
                    return 0;
                }
                if (num >= 1 && num <= items.Length)
                {
                    MoveUpAndRewrite(items, num - 1, lineCount);
                    return num;
                }

                // ── 矢印キー ──
                bool moved = false;
                if (key.Key == ConsoleKey.UpArrow)   { cursor = (cursor - 1 + count) % count; moved = true; }
                if (key.Key == ConsoleKey.DownArrow) { cursor = (cursor + 1) % count; moved = true; }

                if (moved)
                    MoveUpAndRewrite(items, cursor, lineCount);

                // ── Enter / Escape ──
                if (key.Key == ConsoleKey.Enter)
                    return cursor == items.Length ? 0 : cursor + 1;

                if (key.Key == ConsoleKey.Escape)
                {
                    MoveUpAndRewrite(items, items.Length, lineCount);
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
    private static void MoveUpAndRewrite((string Label, Action? Action)[] items, int cursor, int lineCount)
    {
        // カーソルを描画開始位置まで戻す (最終行は改行なしなので lineCount - 1 行上へ)
        Console.Write($"\x1b[{lineCount - 1}A\r");
        WriteMenuLines(items, cursor);
    }

    /// <summary>
    /// メニュー項目を Console.Write で直接描画する。
    /// 最終行は改行しない。
    /// </summary>
    private static void WriteMenuLines((string Label, Action? Action)[] items, int cursor)
    {
        var sb = new System.Text.StringBuilder();

        for (int i = 0; i < items.Length; i++)
        {
            bool selected = (i == cursor);
            string prefix = selected ? "❯" : " ";
            string color  = selected ? AnsiBoldCyan : AnsiWhite;
            sb.Append($"{AnsiClearLine}  {color}{prefix} {i + 1}. {items[i].Label}{AnsiReset}\n");
        }

        bool isBack = cursor == items.Length;
        string backColor = isBack ? AnsiBoldCyan : AnsiDim;
        string backPrefix = isBack ? "❯" : " ";
        sb.Append($"{AnsiClearLine}  {backColor}{backPrefix} 0. ← 戻る{AnsiReset}");

        Console.Write(sb.ToString());
    }

    /// <summary>入力リダイレクト時のフォールバックメニュー。</summary>
    private static int ShowFallbackMenu((string Label, Action? Action)[] items)
    {
        for (int i = 0; i < items.Length; i++)
            AnsiConsole.MarkupLine($"  [white]  {i + 1}. {items[i].Label}[/]");
        AnsiConsole.MarkupLine($"  [dim]  0. ← 戻る[/]");
        AnsiConsole.WriteLine();

        var input = AnsiConsole.Prompt(
            new TextPrompt<string>("  [cyan]>[/]")
                .ValidationErrorMessage("[red]  有効な番号を入力してください[/]")
                .Validate(v =>
                {
                    if (int.TryParse(v.Trim(), out int n) && n >= 0 && n <= items.Length)
                        return ValidationResult.Success();
                    return ValidationResult.Error();
                }));
        return int.Parse(input.Trim());
    }

    private static void PrintCurrentSettings(AppSettings settings, HardwareInfo.EnvironmentProfile hwProfile)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(Style.Parse("dim"))
            .AddColumn("[dim]設定[/]")
            .AddColumn("[white]値[/]");

        table.AddRow("エンジン", Markup.Escape(settings.EngineName ?? "(自動)"));
        table.AddRow("GPU", settings.UseGpu ? "[green]有効[/]" : "[yellow]無効[/]");
        table.AddRow("言語", Markup.Escape(FormatLanguageDisplay(settings.Language)));
        table.AddRow("出力ディレクトリ", Markup.Escape(settings.OutputDirectory ?? "(デフォルト)"));

        string formats = settings.OutputFormats?.Count > 0
            ? string.Join(", ", settings.OutputFormats.Select(f => TranscriptExporter.FormatDescription(f)))
            : "テキスト (.txt) のみ";
        table.AddRow("出力フォーマット", Markup.Escape(formats));

        string recLabel = settings.SaveRecording
            ? (settings.SaveMicOnly ? "[green]有効 (マイクのみ)[/]" : "[green]有効[/]")
            : "[dim]無効[/]";
        table.AddRow("録音保存", recLabel);

        string threadDisplay = settings.MaxCpuThreads > 0
            ? $"{settings.MaxCpuThreads} スレッド"
            : $"自動 ({Math.Max(1, Environment.ProcessorCount - 4)} / {Environment.ProcessorCount} コア)";
        table.AddRow("CPU スレッド数", Markup.Escape(threadDisplay));
        table.AddRow("プロセス優先度", Markup.Escape(FormatPriorityDisplay(settings.ProcessPriority)));

        table.AddRow("マイク", Markup.Escape(settings.MicrophoneDeviceName ?? "(未設定)"));
        table.AddRow("スピーカー", Markup.Escape(settings.SpeakerDeviceName ?? "(未設定)"));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void ConfigureEngine(AppSettings settings, HardwareInfo.EnvironmentProfile hwProfile)
    {
        SpectreUI.PrintSectionHeader("エンジン変更");
        var newEngine = EngineSelector.SelectEngine(settings.EngineName ?? hwProfile.RecommendedEngine, hwProfile);
        if (newEngine != null)
        {
            settings.EngineName = newEngine;
            settings.Save();
            AnsiConsole.MarkupLine("[green]  エンジンを保存しました。[/]");
        }
        AnsiConsole.WriteLine();
    }

    private static void ConfigureGpu(AppSettings settings)
    {
        bool cudaAvailable = CudaHelper.IsCudaAvailable();
        if (!cudaAvailable)
        {
            AnsiConsole.MarkupLine("  [yellow]⚠ CUDA Toolkit が検出されません。GPU モードは使用できません。[/]");
            settings.UseGpu = false;
        }
        else
        {
            settings.UseGpu = AnsiConsole.Confirm("  GPU (CUDA) を使用しますか?", settings.UseGpu);
        }
        settings.Save();
        AnsiConsole.MarkupLine($"  [green]→ {(settings.UseGpu ? "GPU" : "CPU")} モード[/]");
        AnsiConsole.WriteLine();
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
        var choices = LanguageList.Select(l => $"{l.Code} - {l.Name}").ToList();
        choices.Add("auto - 自動検出 (Whisper のみ)");

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  言語を選択:[/]")
                .AddChoices(choices));

        string langCode = choice.Split(' ')[0];
        settings.Language = langCode;
        settings.Save();
        AnsiConsole.MarkupLine($"  [green]→ {choice}[/]");
        AnsiConsole.WriteLine();
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
        AnsiConsole.MarkupLine($"  [dim]現在: {Markup.Escape(settings.OutputDirectory ?? "(デフォルト: Transcripts/)")}[/]");
        string input = AnsiConsole.Prompt(
            new TextPrompt<string>("  [cyan]出力ディレクトリ (Enter で既定値):[/]")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(input))
        {
            settings.OutputDirectory = null;
            AnsiConsole.MarkupLine("  [green]→ デフォルト (Transcripts/) に戻しました[/]");
        }
        else
        {
            settings.OutputDirectory = input.Trim();
            AnsiConsole.MarkupLine($"  [green]→ {Markup.Escape(settings.OutputDirectory)}[/]");
        }
        settings.Save();
        AnsiConsole.WriteLine();
    }

    private static void ConfigureOutputFormats(AppSettings settings)
    {
        var allFormats = Enum.GetValues<OutputFormat>();
        var currentFormats = settings.OutputFormats ?? new List<OutputFormat>();

        var prompt = new MultiSelectionPrompt<string>()
            .Title("[cyan]  出力フォーマットを選択 (スペースで切替, Enter で確定):[/]")
            .AddChoices(allFormats.Select(f => TranscriptExporter.FormatDescription(f)))
            .InstructionsText("[dim](スペースで選択/解除)[/]");

        // 現在の選択を事前選択
        foreach (var f in currentFormats)
        {
            prompt.Select(TranscriptExporter.FormatDescription(f));
        }

        var selected = AnsiConsole.Prompt(prompt);

        settings.OutputFormats = new List<OutputFormat>();
        foreach (var sel in selected)
        {
            foreach (var f in allFormats)
            {
                if (TranscriptExporter.FormatDescription(f) == sel)
                    settings.OutputFormats.Add(f);
            }
        }

        settings.Save();
        AnsiConsole.MarkupLine($"  [green]→ {string.Join(", ", settings.OutputFormats)}[/]");
        AnsiConsole.WriteLine();
    }

    private static void ConfigureDevices(AppSettings settings)
    {
        SpectreUI.PrintSectionHeader("デバイス変更");
        try
        {
            DeviceSelector.SelectDevices(settings);
            AnsiConsole.MarkupLine("  [green]デバイスを保存しました。[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]デバイスの選択に失敗: {Markup.Escape(ex.Message)}[/]");
        }
        AnsiConsole.WriteLine();
    }

    private static void ConfigureRecording(AppSettings settings)
    {
        settings.SaveRecording = AnsiConsole.Confirm("  録音データを WAV ファイルとして保存しますか?", settings.SaveRecording);
        if (settings.SaveRecording)
        {
            settings.SaveMicOnly = AnsiConsole.Confirm("  マイクのみ保存しますか? (スピーカー録音を除外)", settings.SaveMicOnly);
        }
        else
        {
            settings.SaveMicOnly = false;
        }
        settings.Save();
        string detail = settings.SaveRecording
            ? (settings.SaveMicOnly ? "有効 (マイクのみ)" : "有効 (マイク+スピーカー)")
            : "無効";
        AnsiConsole.MarkupLine($"  [green]→ 録音保存: {detail}[/]");
        AnsiConsole.WriteLine();
    }

    private static void ConfigureResources(AppSettings settings)
    {
        SpectreUI.PrintSectionHeader("リソース制御");

        int totalCores = Environment.ProcessorCount;
        int currentThreads = settings.MaxCpuThreads > 0
            ? settings.MaxCpuThreads
            : Math.Max(1, totalCores - 4);

        AnsiConsole.MarkupLine($"  [dim]CPU コア数: {totalCores}[/]");
        AnsiConsole.MarkupLine($"  [dim]現在のスレッド数: {currentThreads}[/]");
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
                .Title("[cyan]  Whisper の CPU スレッド数を選択:[/]")
                .PageSize(12)
                .HighlightStyle(Style.Parse("bold cyan"))
                .AddChoices(threadChoices));

        if (threadChoice.StartsWith("自動"))
        {
            settings.MaxCpuThreads = 0;
        }
        else
        {
            // "N (...)" からNを抽出
            var numStr = threadChoice.Split(' ')[0];
            if (int.TryParse(numStr, out int threads))
                settings.MaxCpuThreads = threads;
        }
        AnsiConsole.MarkupLine($"  [green]→ スレッド数: {(settings.MaxCpuThreads > 0 ? settings.MaxCpuThreads.ToString() : "自動")}[/]");
        AnsiConsole.WriteLine();

        // ── プロセス優先度 ──
        var priorityChoices = new[]
        {
            "Normal — 通常 (デフォルト)",
            "BelowNormal — やや低い (他アプリと共存しやすい)",
            "Idle — 最低 (他アプリが完全に優先される)",
        };

        var priorityChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  プロセス優先度を選択:[/]")
                .HighlightStyle(Style.Parse("bold cyan"))
                .AddChoices(priorityChoices));

        settings.ProcessPriority = priorityChoice.Split(' ')[0];
        AnsiConsole.MarkupLine($"  [green]→ 優先度: {FormatPriorityDisplay(settings.ProcessPriority)}[/]");

        settings.Save();
        AnsiConsole.WriteLine();

        // 即時反映
        ApplyProcessPriority(settings.ProcessPriority);
        AnsiConsole.MarkupLine("  [dim]設定を反映しました。スレッド数は次のセッションから適用されます。[/]");
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

    private static void ResetSettings(AppSettings settings)
    {
        if (AnsiConsole.Confirm("  [yellow]すべての設定をリセットしますか?[/]", false))
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
            settings.MaxCpuThreads = fresh.MaxCpuThreads;
            settings.ProcessPriority = fresh.ProcessPriority;

            AnsiConsole.MarkupLine("  [green]設定をリセットしました。[/]");
        }
        AnsiConsole.WriteLine();
    }
}
