using Spectre.Console;
using Spectre.Console.Rendering;
using TalkTranscript.Models;

namespace TalkTranscript;

/// <summary>
/// Spectre.Console を使った Live 表示 UI。
/// 録音中の文字起こし結果、処理中スピナー、キー操作ガイドを
/// 常に正しい位置に表示する。
///
/// - ヘッダー/フッターは常時表示 (スクロールで消えない)
/// - スピナーは Whisper 処理中のみ表示、完了で自動消去
/// - スレッドセーフ (バックグラウンド認識スレッドから安全に更新可能)
/// </summary>
internal sealed class SpectreUI
{
    private readonly List<TranscriptEntry> _entries = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, double> _processing = new();

    private string _engine = "";
    private bool _useGpu;
    private string _micName = "";
    private string _speakerName = "";
    private string _fileName = "";
    private bool _isTest;
    private int _testSeconds;
    private DateTime _startTime;

    public void Configure(string engine, bool useGpu, string mic, string speaker,
                          string fileName, bool test, int testSec)
    {
        _engine = engine;
        _useGpu = useGpu;
        _micName = mic;
        _speakerName = speaker;
        _fileName = fileName;
        _isTest = test;
        _testSeconds = testSec;
    }

    /// <summary>認識結果を追加する (スレッドセーフ)</summary>
    public void AddEntry(TranscriptEntry entry)
    {
        lock (_lock) _entries.Add(entry);
    }

    /// <summary>処理中状態を設定する (スレッドセーフ)</summary>
    public void SetProcessing(string speaker, double durationSec)
    {
        lock (_lock) _processing[speaker] = durationSec;
    }

    /// <summary>処理完了を通知する (スレッドセーフ)</summary>
    public void ClearProcessing(string speaker)
    {
        lock (_lock) _processing.Remove(speaker);
    }

    /// <summary>
    /// Live 表示の録音セッションを実行する。
    /// キー入力またはテスト終了まで表示をブロックし、アクション名を返す。
    /// </summary>
    public string RunSession(DateTime startTime)
    {
        _startTime = startTime;
        string action = "quit";
        var spinnerFrames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        int frame = 0;

        AnsiConsole.Live(new Text(""))
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Start(ctx =>
            {
                while (true)
                {
                    // ── キー入力チェック ──
                    if (!_isTest && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Q && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { action = "quit"; break; }
                        if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { action = "device"; break; }
                        if (key.Key == ConsoleKey.E && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { action = "engine"; break; }
                        if (key.Key == ConsoleKey.G && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                        { action = "gpu"; break; }
                    }

                    // ── テストモード自動停止 ──
                    if (_isTest && (DateTime.Now - _startTime).TotalSeconds >= _testSeconds)
                    { action = "quit"; break; }

                    // ── 表示更新 ──
                    ctx.UpdateTarget(BuildDisplay(spinnerFrames[frame % spinnerFrames.Length]));
                    frame++;
                    Thread.Sleep(100);
                }
            });

        return action;
    }

    private IRenderable BuildDisplay(string spinnerFrame)
    {
        var elapsed = DateTime.Now - _startTime;
        var rows = new List<IRenderable>();

        // ── ステータス行 ──
        var statusParts = new List<string> { "[green]● 録音中[/]" };
        if (_isTest)
            statusParts.Add($"[magenta]テスト ({_testSeconds}秒)[/]");
        statusParts.Add($"[dim]経過[/] {elapsed:hh\\:mm\\:ss}");
        rows.Add(new Markup("  " + string.Join("  ", statusParts)));
        rows.Add(new Rule().RuleStyle("dim"));

        // ── 文字起こし結果 ──
        int maxEntries;
        try { maxEntries = Math.Max(3, Console.WindowHeight - 10); }
        catch { maxEntries = 20; }

        List<TranscriptEntry> visible;
        Dictionary<string, double> proc;
        lock (_lock)
        {
            int skip = Math.Max(0, _entries.Count - maxEntries);
            visible = _entries.Skip(skip).ToList();
            proc = new Dictionary<string, double>(_processing);
        }

        if (visible.Count == 0 && proc.Count == 0)
        {
            rows.Add(new Markup("  [dim]音声を待っています...[/]"));
        }

        foreach (var entry in visible)
        {
            var icon = entry.Speaker == "自分" ? "▶" : "◀";
            var color = entry.Speaker == "自分" ? "cyan" : "yellow";
            rows.Add(new Markup(
                $"  [dim]{entry.Timestamp:HH:mm:ss}[/]  " +
                $"[{color}]{icon} {Markup.Escape(entry.Speaker)}:[/] " +
                Markup.Escape(entry.Text)));
        }

        // ── 処理中インジケーター (スピナー) ──
        foreach (var (speaker, dur) in proc)
        {
            rows.Add(new Markup(
                $"  [yellow]{spinnerFrame}[/] [dim][[{Markup.Escape(speaker)}]] {dur:F1}秒の音声を処理中...[/]"));
        }

        // ── フッター (キー操作ガイド: 常に表示) ──
        rows.Add(new Text(""));
        rows.Add(new Rule().RuleStyle("dim"));
        if (!_isTest)
        {
            rows.Add(new Markup(
                "  [white bold]Ctrl+Q[/] [dim]停止[/]  │  " +
                "[white bold]Ctrl+D[/] [dim]デバイス[/]  │  " +
                "[white bold]Ctrl+E[/] [dim]エンジン[/]  │  " +
                "[white bold]Ctrl+G[/] [dim]GPU切替[/]"));
        }

        return new Rows(rows);
    }

    // ══════════════════════════════════════════════════
    //  静的ヘルパー (Live 表示の前後で使用)
    // ══════════════════════════════════════════════════

    /// <summary>バナーを表示する</summary>
    public static void PrintBanner(string engine, bool useGpu, string mic,
                                   string speaker, string fileName,
                                   bool test, int sec)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel("[bold]通話文字起こしツール[/]")
            .Border(BoxBorder.Double)
            .BorderStyle(Style.Parse("white"))
            .Padding(2, 0));
        AnsiConsole.WriteLine();

        if (test)
        {
            AnsiConsole.MarkupLine($"  [magenta][[テストモード]] {sec}秒後に自動停止[/]");
            AnsiConsole.WriteLine();
        }

        var gpuTag = engine.StartsWith("whisper")
            ? (useGpu ? " [green][[GPU]][/]" : " [dim][[CPU]][/]")
            : "";

        AnsiConsole.MarkupLine($"  [dim]エンジン    :[/] [white]{Markup.Escape(engine.ToUpperInvariant())}[/]{gpuTag}");
        AnsiConsole.MarkupLine($"  [dim]マイク      :[/] [cyan]{Markup.Escape(mic)}[/]");
        AnsiConsole.MarkupLine($"  [dim]スピーカー  :[/] [yellow]{Markup.Escape(speaker)}[/]");
        AnsiConsole.MarkupLine($"  [dim]出力先      :[/] [white]{Markup.Escape(fileName)}[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>最終サマリーを表示する</summary>
    public static void PrintSummary(string filePath, int micCount, int spkCount, TimeSpan elapsed)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [green]✓[/] 保存先: [white]{Markup.Escape(filePath)}[/]");
        AnsiConsole.MarkupLine($"  [dim]合計: 自分 {micCount}件 / 相手 {spkCount}件 / 経過 {elapsed:hh\\:mm\\:ss}[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>セクションヘッダーを表示する</summary>
    public static void PrintSectionHeader(string title)
    {
        AnsiConsole.Write(new Rule($"[dim]{Markup.Escape(title)}[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();
    }
}
