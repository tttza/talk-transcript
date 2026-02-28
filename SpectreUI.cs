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

    // ── 音量メーター (#2) ──
    private volatile float _micVolume;
    private volatile float _speakerVolume;

    // ── ブックマーク (#3) ──
    private readonly List<DateTime> _bookmarks = new();
    public event Action? OnBookmarkRequested;

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

    /// <summary>音量レベルを更新する (スレッドセーフ)</summary>
    public void UpdateVolume(float micPeak, float speakerPeak)
    {
        _micVolume = micPeak;
        _speakerVolume = speakerPeak;
    }

    /// <summary>ブックマークを追加する</summary>
    public void AddBookmark()
    {
        lock (_lock) _bookmarks.Add(DateTime.Now);
    }

    /// <summary>
    /// Live 表示の録音セッションを実行する。
    /// キー入力またはテスト終了まで表示をブロックし、アクション名を返す。
    /// コンソールがリダイレクトされている場合は簡易ループにフォールバック。
    /// </summary>
    public string RunSession(DateTime startTime)
    {
        _startTime = startTime;

        // コンソールがリダイレクトされている場合は Live 表示を使わない
        // (Spectre.Console の LiveDisplay は CursorVisible を操作するため IOException になる)
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            return RunFallbackSession();

        try
        {
            return RunLiveSession();
        }
        catch (IOException)
        {
            // CursorVisible 操作が失敗した場合のフォールバック
            return RunFallbackSession();
        }
    }

    /// <summary>Spectre.Console Live 表示を使った通常セッション</summary>
    private string RunLiveSession()
    {
        string? action = "quit";
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
                    var input = CheckKeyInput();
                    if (input != null) { action = input; break; }

                    // ── テストモード自動停止 ──
                    if (_isTest && (DateTime.Now - _startTime).TotalSeconds >= _testSeconds)
                    { action = "quit"; break; }

                    // ── 表示更新 ──
                    ctx.UpdateTarget(BuildDisplay(spinnerFrames[frame % spinnerFrames.Length]));
                    frame++;
                    Thread.Sleep(100);
                }
            });

        return action ?? "quit";
    }

    /// <summary>コンソールがリダイレクトされている場合の簡易セッション</summary>
    private string RunFallbackSession()
    {
        Console.WriteLine("  ● 録音中...");
        int lastCount = 0;

        while (true)
        {
            // ── キー入力チェック (リダイレクト時はスキップ) ──
            if (!Console.IsInputRedirected)
            {
                var result = CheckKeyInput();
                if (result != null) return result;
            }

            // ── テストモード自動停止 ──
            if (_isTest && (DateTime.Now - _startTime).TotalSeconds >= _testSeconds)
                return "quit";

            // ── 新しい認識結果を表示 ──
            lock (_lock)
            {
                while (lastCount < _entries.Count)
                {
                    var entry = _entries[lastCount];
                    Console.WriteLine($"  [{entry.Timestamp:HH:mm:ss}] {entry.Speaker}: {entry.Text}");
                    lastCount++;
                }
            }

            Thread.Sleep(200);
        }
    }

    /// <summary>キー入力をチェックし、アクション文字列を返す。入力なしなら null。</summary>
    private string? CheckKeyInput()
    {
        try
        {
            if (_isTest || Console.IsInputRedirected || !Console.KeyAvailable)
                return null;
        }
        catch (InvalidOperationException) { return null; }

        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Q && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            return "quit";
        if (key.Key == ConsoleKey.F2)
            return "config";
        if (key.Key == ConsoleKey.B && key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            AddBookmark();
            OnBookmarkRequested?.Invoke();
        }
        return null;
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

        // ── 音量メーター (#2) ──
        rows.Add(new Markup("  " + BuildVolumeBar("🎤", _micVolume, "cyan") + "   " +
                                   BuildVolumeBar("🔊", _speakerVolume, "yellow")));

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
                "[white bold]F2[/] [dim]設定[/]  │  " +
                "[white bold]Ctrl+B[/] [dim]ブックマーク[/]"));
        }

        return new Rows(rows);
    }

    /// <summary>音量バーを Spectre.Console マークアップ文字列で生成する</summary>
    private static string BuildVolumeBar(string icon, float peak, string color)
    {
        const int barWidth = 15;
        // ピーク値を 0-1 に正規化 (16bit PCM の最大値 = 32767)
        float normalized = Math.Clamp(peak / 32767f, 0f, 1f);
        int filled = (int)(normalized * barWidth);

        // dB 表示 (固定幅 5 文字に揃えてチラつき防止)
        float db = peak > 0 ? 20f * MathF.Log10(peak / 32767f) : -60f;
        string dbStr = db > -60f ? $"{db:F0}dB".PadLeft(5) : " --- ";

        string bar = new string('█', filled) + new string('░', barWidth - filled);
        return $"{icon} [{color}]{bar}[/] [dim]{dbStr}[/]";
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
