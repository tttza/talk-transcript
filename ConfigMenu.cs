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
            ("設定をリセット",    () => ResetSettings(settings)),
        };

        while (true)
        {
            // 現在の設定を表示
            PrintCurrentSettings(settings, hwProfile);

            // 番号付きメニューを表示
            AnsiConsole.MarkupLine("[cyan]変更する項目を選択してください (番号を入力 / 0 で戻る):[/]");
            AnsiConsole.WriteLine();
            for (int i = 0; i < menuItems.Length; i++)
            {
                AnsiConsole.MarkupLine($"  [white]{i + 1}.[/] {menuItems[i].Label}");
            }
            AnsiConsole.MarkupLine($"  [dim]0. ← 戻る[/]");
            AnsiConsole.WriteLine();

            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("  [cyan]>[/]")
                    .ValidationErrorMessage("[red]  有効な番号を入力してください[/]")
                    .Validate(v =>
                    {
                        if (int.TryParse(v.Trim(), out int n) && n >= 0 && n <= menuItems.Length)
                            return ValidationResult.Success();
                        return ValidationResult.Error();
                    }));

            int selected = int.Parse(input.Trim());
            AnsiConsole.WriteLine();

            if (selected == 0)
                break;

            menuItems[selected - 1].Action?.Invoke();
        }
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

        table.AddRow("録音保存", settings.SaveRecording ? "[green]有効[/]" : "[dim]無効[/]");
        table.AddRow("マイク", Markup.Escape(settings.MicrophoneDeviceName ?? "(未設定)"));
        table.AddRow("スピーカー", Markup.Escape(settings.SpeakerDeviceName ?? "(未設定)"));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void ConfigureEngine(AppSettings settings, HardwareInfo.EnvironmentProfile hwProfile)
    {
        SpectreUI.PrintSectionHeader("エンジン変更");
        var newEngine = EngineSelector.SelectEngine(settings.EngineName ?? "vosk", hwProfile);
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
        settings.Save();
        AnsiConsole.MarkupLine($"  [green]→ 録音保存: {(settings.SaveRecording ? "有効" : "無効")}[/]");
        AnsiConsole.WriteLine();
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

            AnsiConsole.MarkupLine("  [green]設定をリセットしました。[/]");
        }
        AnsiConsole.WriteLine();
    }
}
