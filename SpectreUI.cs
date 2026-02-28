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

    // ── スクロール ──
    private int _scrollOffset;          // 0 = 最新表示, 正の値 = 過去方向へのオフセット
    private DateTime _lastScrollTime;   // 最後にスクロール操作した時刻
    private const int ScrollHoldSeconds = 5; // スクロール操作後に自動ジャンプを抑制する秒数

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
        lock (_lock)
        {
            _entries.Add(entry);
            // スクロール操作から一定時間経過していれば最新へジャンプ
            if (_scrollOffset == 0 || (DateTime.Now - _lastScrollTime).TotalSeconds >= ScrollHoldSeconds)
                _scrollOffset = 0;
        }
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

        // ── スクロール操作 ──
        if (key.Key == ConsoleKey.UpArrow || key.Key == ConsoleKey.PageUp)
        {
            int step = key.Key == ConsoleKey.PageUp ? 10 : 1;
            lock (_lock)
            {
                int maxEntries = EstimateMaxEntries();
                int maxOffset = Math.Max(0, _entries.Count - maxEntries);
                _scrollOffset = Math.Min(_scrollOffset + step, maxOffset);
                _lastScrollTime = DateTime.Now;
            }
        }
        if (key.Key == ConsoleKey.DownArrow || key.Key == ConsoleKey.PageDown)
        {
            int step = key.Key == ConsoleKey.PageDown ? 10 : 1;
            lock (_lock)
            {
                _scrollOffset = Math.Max(0, _scrollOffset - step);
                if (_scrollOffset > 0) _lastScrollTime = DateTime.Now;
            }
        }
        if (key.Key == ConsoleKey.Home)
        {
            lock (_lock)
            {
                int maxEntries = EstimateMaxEntries();
                _scrollOffset = Math.Max(0, _entries.Count - maxEntries);
                _lastScrollTime = DateTime.Now;
            }
        }
        if (key.Key == ConsoleKey.End)
        {
            lock (_lock) _scrollOffset = 0;
        }
        return null;
    }

    private IRenderable BuildDisplay(string spinnerFrame)
    {
        var elapsed = DateTime.Now - _startTime;
        var rows = new List<IRenderable>();

        // ── 文字起こし結果 (上部: スクロール可能領域) ──
        List<TranscriptEntry> visible;
        Dictionary<string, double> proc;
        int totalEntries, scrollOff, hiddenAbove, hiddenBelow;
        lock (_lock)
        {
            int maxEntries = EstimateMaxEntries();
            totalEntries = _entries.Count;
            scrollOff = _scrollOffset;
            int endIdx = totalEntries - scrollOff;
            int startIdx = Math.Max(0, endIdx - maxEntries);
            endIdx = Math.Max(startIdx, endIdx);
            visible = _entries.GetRange(startIdx, endIdx - startIdx);
            hiddenAbove = startIdx;
            hiddenBelow = totalEntries - endIdx;
            proc = new Dictionary<string, double>(_processing);
        }

        if (visible.Count == 0 && proc.Count == 0)
        {
            rows.Add(new Markup("  [dim]音声を待っています...[/]"));
        }

        // ── スクロール位置インジケーター (上方向) ──
        if (hiddenAbove > 0)
            rows.Add(new Markup($"  [dim]  ↑ さらに {hiddenAbove} 件[/]"));

        foreach (var entry in visible)
        {
            var icon = entry.Speaker == "自分" ? "▶" : "◀";
            var color = entry.Speaker == "自分" ? "cyan" : "yellow";
            rows.Add(new Markup(
                $"  [dim]{entry.Timestamp:HH:mm:ss}[/]  " +
                $"[{color}]{icon} {Markup.Escape(entry.Speaker)}:[/] " +
                Markup.Escape(entry.Text)));
        }

        // ── スクロール位置インジケーター (下方向) ──
        if (hiddenBelow > 0)
            rows.Add(new Markup($"  [dim]  ↓ さらに {hiddenBelow} 件 (最新)[/]"));

        // ── 処理中インジケーター (スピナー) ──
        foreach (var (speaker, dur) in proc)
        {
            rows.Add(new Markup(
                $"  [yellow]{spinnerFrame}[/] [dim][[{Markup.Escape(speaker)}]] {dur:F1}秒の音声を処理中...[/]"));
        }

        // ── 下部固定領域を画面最下部に押し下げるパディング ──
        // 下部固定行数: 区切り線(1) + バナー(1-2) + ステータス(1) + 区切り線(1) + キーガイド(1) = 5-6
        int bannerLines = _isTest ? 2 : 1;
        int footerLines = 1 + bannerLines + 1 + 1 + (_isTest ? 0 : 1);
        int usedLines = rows.Count + proc.Count; // 上部で使った行数 (proc は rows に含まれている)
        int windowHeight;
        try { windowHeight = Console.WindowHeight; }
        catch { windowHeight = 30; }
        int padding = Math.Max(0, windowHeight - (rows.Count + footerLines));
        for (int i = 0; i < padding; i++)
            rows.Add(new Text(""));

        // ═══════════════════════════════════════════════
        //  下部固定領域 (常に最下部に表示される)
        // ═══════════════════════════════════════════════
        rows.Add(new Rule().RuleStyle("dim"));

        // ── バナー (エンジン・デバイス・ファイル) ──
        var gpuTag = _engine.StartsWith("whisper")
            ? (_useGpu ? "[green]GPU[/]" : "[dim]CPU[/]")
            : "";
        if (_isTest)
            rows.Add(new Markup($"  [magenta][[テストモード]] {_testSeconds}秒後に自動停止[/]"));
        rows.Add(new Markup(
            $"  [white bold]{Markup.Escape(_engine.ToUpperInvariant())}[/]" +
            (gpuTag.Length > 0 ? $" ({gpuTag})" : "") +
            $"  [dim]│[/]  [cyan]🎤 {Markup.Escape(TruncateDevice(_micName))}[/]" +
            $"  [dim]│[/]  [yellow]🔊 {Markup.Escape(TruncateDevice(_speakerName))}[/]" +
            $"  [dim]│[/]  [dim]→[/] {Markup.Escape(_fileName)}"));

        // ── ステータス + 音量バー ──
        var statusParts = new List<string> { "[green]●[/]" };
        statusParts.Add($"{elapsed:hh\\:mm\\:ss}");
        if (_isTest)
            statusParts.Add($"[magenta]テスト({_testSeconds}秒)[/]");
        statusParts.Add(BuildVolumeBar("🎤", _micVolume, "cyan"));
        statusParts.Add(BuildVolumeBar("🔊", _speakerVolume, "yellow"));
        rows.Add(new Markup("  " + string.Join("  ", statusParts)));

        // ── キー操作ガイド ──
        rows.Add(new Rule().RuleStyle("dim"));
        if (!_isTest)
        {
            var guide = "  [white bold]Ctrl+Q[/] [dim]停止[/]  │  " +
                "[white bold]F2[/] [dim]設定[/]  │  " +
                "[white bold]Ctrl+B[/] [dim]ブックマーク[/]  │  " +
                "[white bold]↑↓[/][dim]/[/][white bold]PgUp PgDn[/] [dim]スクロール[/]";
            if (scrollOff > 0)
                guide += "  │  [white bold]End[/] [dim]最新へ[/]";
            rows.Add(new Markup(guide));
        }

        return new Rows(rows);
    }

    /// <summary>音量バーを Spectre.Console マークアップ文字列で生成する</summary>
    private static string BuildVolumeBar(string icon, float peak, string color)
    {
        const int barWidth = 15;
        const float dbMin = -48f;  // バー下限 (これ以下は空表示)
        const float dbMax = 0f;    // バー上限 (フルスケール)

        // dB 計算 (対数スケール — 人の聴覚特性に合致)
        float db = peak > 0 ? 20f * MathF.Log10(peak / 32767f) : -60f;

        // dB を 0-1 に正規化してバー表示 (対数スケール)
        float normalized = Math.Clamp((db - dbMin) / (dbMax - dbMin), 0f, 1f);
        int filled = (int)(normalized * barWidth);

        // dB 表示 (固定幅 5 文字に揃えてチラつき防止)
        string dbStr = db > -60f ? $"{db:F0}dB".PadLeft(5) : " --- ";

        string bar = new string('█', filled) + new string('░', barWidth - filled);
        return $"{icon} [{color}]{bar}[/] [dim]{dbStr}[/]";
    }

    /// <summary>現在の端末サイズと状態から表示可能なエントリ数を見積もる</summary>
    private int EstimateMaxEntries()
    {
        // 下部固定領域: 区切り線(1) + バナー(1-2) + ステータス+音量(1) + 区切り線(1) + キーガイド(1) = 5-6
        // + スピナー行 + スクロールインジケーター(最大2)
        int bannerLines = _isTest ? 2 : 1;
        int overhead = 1 + bannerLines + 1 + 1 + 1 + _processing.Count + 2;
        try { return Math.Max(3, Console.WindowHeight - overhead); }
        catch { return 20; }
    }

    // ══════════════════════════════════════════════════
    //  静的ヘルパー (Live 表示の前後で使用)
    // ══════════════════════════════════════════════════

    /// <summary>バナーを表示する (Live セッション外で使用)</summary>
    public static void PrintBanner(string engine, bool useGpu, string mic,
                                   string speaker, string fileName,
                                   bool test, int sec)
    {
        // Live セッション内ではバナーは BuildDisplay() が描画するため、
        // ここではセッション開始前の一瞬だけ表示される。
        // (Live 表示が開始されると自動的にクリアされる)
    }

    /// <summary>デバイス名が長い場合に短縮する</summary>
    private static string TruncateDevice(string name)
    {
        const int max = 30;
        return name.Length <= max ? name : name[..(max - 1)] + "…";
    }

    /// <summary>最終サマリーを表示する</summary>
    public static void PrintSummary(string? filePath, int micCount, int spkCount, TimeSpan elapsed)
    {
        AnsiConsole.WriteLine();
        if (filePath != null)
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
