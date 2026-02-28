using Spectre.Console;
using TalkTranscript.Logging;
using TalkTranscript.Output;

namespace TalkTranscript;

/// <summary>
/// エンジン選択メニューを表示し、ユーザーにエンジンを選ばせる。
/// Spectre.Console の SelectionPrompt で ↑↓ カーソル選択に対応。
/// </summary>
internal static class EngineSelector
{
    private static readonly (string Id, string Label, string Desc)[] Engines = new[]
    {
        ("vosk",           "Vosk",           "リアルタイム"),
        ("whisper-tiny",   "Whisper tiny",   "~39MB  準リアルタイム・高速"),
        ("whisper-base",   "Whisper base",   "~142MB 準リアルタイム・バランス"),
        ("whisper-small",  "Whisper small",  "~466MB 準リアルタイム・高精度"),
        ("whisper-medium", "Whisper medium", "~1.5GB 高精度 (GPU 推奨)"),
        ("whisper-large",  "Whisper large",  "~3.1GB 最高精度 (GPU 推奨)"),
        ("sapi",           "SAPI",           "Windows 標準 (マイク/スピーカー同時書き起こし不可)")
    };

    /// <summary>
    /// エンジン選択メニューを表示し、選択されたエンジン名を返す。
    /// キャンセルされた場合は null を返す。
    /// </summary>
    public static string? SelectEngine(string currentEngine, HardwareInfo.EnvironmentProfile hwProfile)
    {
        // 環境情報表示
        if (hwProfile.HasNvidiaGpu)
            AnsiConsole.MarkupLine($"  [dim]環境:[/] [white]{Markup.Escape(hwProfile.GpuName ?? "GPU")} ({hwProfile.GpuVramMB / 1024}GB)[/]");
        else
            AnsiConsole.MarkupLine($"  [dim]環境:[/] [white]CPU {hwProfile.CpuCores}コア / RAM {hwProfile.SystemRamMB / 1024}GB[/]");
        AnsiConsole.WriteLine();

        // 選択肢を構築 (レーティング + ラベル + 説明 + 現在/推奨マーカー)
        const string cancelLabel = "← キャンセル";
        var choices = new List<string>();
        int defaultIndex = -1;

        for (int i = 0; i < Engines.Length; i++)
        {
            var (id, label, desc) = Engines[i];
            string rating = HardwareInfo.GetRecommendation(id, hwProfile);
            bool isCurrent = id == currentEngine;
            bool isRecommended = id == hwProfile.RecommendedEngine;

            string marker = isCurrent ? " ← 現在" : isRecommended ? " ← 推奨" : "";
            string display = $"{rating} {label,-16} {desc}{marker}";
            choices.Add(display);

            if (isCurrent) defaultIndex = i;
        }

        choices.Add(cancelLabel);

        // 現在選択中のエンジンが先頭に来るように並び替え
        var orderedChoices = new List<string>(choices);
        if (defaultIndex >= 0)
        {
            var current = orderedChoices[defaultIndex];
            orderedChoices.RemoveAt(defaultIndex);
            orderedChoices.Insert(0, current);
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  エンジンを選択 (↑↓で移動, Enterで確定):[/]")
                .PageSize(10)
                .HighlightStyle(Style.Parse("bold cyan"))
                .AddChoices(orderedChoices));

        if (selected == cancelLabel)
        {
            AnsiConsole.MarkupLine("  [dim]キャンセルしました[/]");
            return null;
        }

        // 選択されたインデックスからエンジンIDを逆引き
        int idx = choices.IndexOf(selected);
        if (idx >= 0 && idx < Engines.Length)
        {
            var (selId, selLabel, _) = Engines[idx];
            AnsiConsole.MarkupLine($"  [green]→ {Markup.Escape(selLabel)}[/]");
            AppLogger.Info($"エンジン変更: {selId}");
            return selId;
        }

        return null;
    }

    /// <summary>コマンドライン引数からフォーマットをパースする</summary>
    public static List<OutputFormat> ParseFormats(string[] args)
    {
        var formats = new List<OutputFormat>();
        int idx = Array.IndexOf(args, "--format");
        if (idx >= 0 && idx + 1 < args.Length)
        {
            foreach (var part in args[idx + 1].Split(','))
            {
                var f = TranscriptExporter.ParseFormat(part.Trim());
                if (f.HasValue) formats.Add(f.Value);
            }
        }
        return formats;
    }
}
