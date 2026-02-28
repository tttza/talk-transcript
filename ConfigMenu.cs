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

        while (true)
        {
            // 現在の設定を表示
            PrintCurrentSettings(settings, hwProfile);

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]変更する項目を選択してください:[/]")
                    .AddChoices(
                        "1. エンジン",
                        "2. GPU (CUDA)",
                        "3. 言語",
                        "4. 出力ディレクトリ",
                        "5. 出力フォーマット",
                        "6. デバイス",
                        "7. 設定をリセット",
                        "← 戻る"));

            if (choice.StartsWith("←"))
                break;

            switch (choice[0])
            {
                case '1':
                    ConfigureEngine(settings, hwProfile);
                    break;
                case '2':
                    ConfigureGpu(settings);
                    break;
                case '3':
                    ConfigureLanguage(settings);
                    break;
                case '4':
                    ConfigureOutputDirectory(settings);
                    break;
                case '5':
                    ConfigureOutputFormats(settings);
                    break;
                case '6':
                    ConfigureDevices(settings);
                    break;
                case '7':
                    ResetSettings(settings);
                    break;
            }
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
        table.AddRow("言語", Markup.Escape(settings.Language ?? "ja (日本語)"));
        table.AddRow("出力ディレクトリ", Markup.Escape(settings.OutputDirectory ?? "(デフォルト)"));

        string formats = settings.OutputFormats?.Count > 0
            ? string.Join(", ", settings.OutputFormats.Select(f => TranscriptExporter.FormatDescription(f)))
            : "テキスト (.txt) のみ";
        table.AddRow("出力フォーマット", Markup.Escape(formats));

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

    private static void ConfigureLanguage(AppSettings settings)
    {
        var languages = new[]
        {
            ("ja", "日本語"),
            ("en", "English"),
            ("zh", "中文"),
            ("ko", "한국어"),
            ("auto", "自動検出 (Whisper のみ)")
        };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  言語を選択:[/]")
                .AddChoices(languages.Select(l => $"{l.Item1} - {l.Item2}")));

        string langCode = choice.Split(' ')[0];
        settings.Language = langCode;
        settings.Save();
        AnsiConsole.MarkupLine($"  [green]→ {choice}[/]");
        AnsiConsole.WriteLine();
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

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[cyan]  出力フォーマットを選択 (スペースで切替, Enter で確定):[/]")
                .AddChoices(allFormats.Select(f => TranscriptExporter.FormatDescription(f)))
                .InstructionsText("[dim](スペースで選択/解除)[/]"));

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

            AnsiConsole.MarkupLine("  [green]設定をリセットしました。[/]");
        }
        AnsiConsole.WriteLine();
    }
}
