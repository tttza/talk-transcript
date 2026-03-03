using System.Runtime.InteropServices;
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
    private GpuBackend _gpuBackend;
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
                          string fileName, bool test, int testSec,
                          GpuBackend gpuBackend = GpuBackend.None)
    {
        _engine = engine;
        _useGpu = useGpu;
        _gpuBackend = gpuBackend;
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

    /// <summary>翻訳結果でエントリを更新する (スレッドセーフ)</summary>
    public void UpdateTranslation(TranscriptEntry translatedEntry)
    {
        lock (_lock)
        {
            // タイムスタンプ + Speaker + Text で照合して更新
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.Timestamp == translatedEntry.Timestamp
                    && e.Speaker == translatedEntry.Speaker
                    && e.Text == translatedEntry.Text)
                {
                    _entries[i] = translatedEntry;
                    break;
                }
            }
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

        // ── ネイティブライブラリ (ONNX Runtime, whisper.cpp) の stderr 出力を抑制 ──
        // Live 表示中に stderr へ書き込まれると赤文字で一瞬表示されてしまうため、
        // セッション中は stderr を NUL にリダイレクトする。
        var savedError = Console.Error;
        int savedFd = -1;
        try { savedFd = NativeStderr.Suppress(); } catch { /* P/Invoke 失敗時は無視 */ }
        Console.SetError(TextWriter.Null);

        try
        {
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
                        try
                        {
                            ctx.UpdateTarget(BuildDisplay(spinnerFrames[frame % spinnerFrames.Length]));
                        }
                        catch (Exception ex)
                        {
                            // レンダリングエラーは握りつぶして次フレームでリトライ
                            Logging.AppLogger.Warn($"Live 表示更新エラー: {ex.Message}");
                        }
                        frame++;
                        Thread.Sleep(100);
                    }
                });
        }
        finally
        {
            // ── stderr を復帰 ──
            Console.SetError(savedError);
            try { NativeStderr.Restore(savedFd); } catch { }
        }

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
                    if (!string.IsNullOrEmpty(entry.TranslatedText))
                        Console.WriteLine($"             ↳ {entry.TranslatedText}");
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

        // ══════════════════════════════════════════
        //  ヘッダー (固定 3 行: バナー / ステータス+音量 / 区切り線)
        // ══════════════════════════════════════════
        // 1行目: エンジン + デバイス + ファイル名
        var gpuTag = _engine.StartsWith("whisper")
            ? (_useGpu ? $"[green]{FormatGpuBackendTag(_gpuBackend)}[/]" : "[dim]CPU[/]")
            : "";
        var bannerLine =
            $"  [white bold]{Markup.Escape(_engine.ToUpperInvariant())}[/]" +
            (gpuTag.Length > 0 ? $" ({gpuTag})" : "") +
            $"  [dim]│[/]  [cyan]🎤 {Markup.Escape(TruncateDevice(_micName))}[/]" +
            $"  [dim]│[/]  [yellow]🔊 {Markup.Escape(TruncateDevice(_speakerName))}[/]" +
            $"  [dim]│[/]  [dim]→[/] {Markup.Escape(_fileName)}";

        // 2行目: ステータス + 音量メーター
        var statusParts = new List<string> { "[green]●[/]" };
        statusParts.Add($"{elapsed:hh\\:mm\\:ss}");
        if (_isTest)
            statusParts.Add($"[magenta]テスト({_testSeconds}秒)[/]");
        statusParts.Add(BuildVolumeBar("🎤", _micVolume, "cyan"));
        statusParts.Add(BuildVolumeBar("🔊", _speakerVolume, "yellow"));

        // バナーの表示テキスト (Markup タグを除いた実テキスト) の表示幅で折り返し行数を計算
        var bannerPlain =
            "  " + _engine.ToUpperInvariant() +
            (_engine.StartsWith("whisper") ? (_useGpu ? $" ({FormatGpuBackendTag(_gpuBackend)})" : " (CPU)") : "") +
            "  │  🎤 " + TruncateDevice(_micName) +
            "  │  🔊 " + TruncateDevice(_speakerName) +
            "  │  → " + _fileName;

        int consoleWidth;
        try { consoleWidth = Console.WindowWidth; } catch { consoleWidth = 80; }
        int bannerLines = Math.Max(1, (int)Math.Ceiling(
            (double)EstimateDisplayWidth(bannerPlain) / Math.Max(1, consoleWidth)));

        var header = new Rows(
            new Markup(bannerLine),
            new Markup("  " + string.Join("  ", statusParts)),
            new Rule().RuleStyle("dim"));

        int headerSize = bannerLines + 2;  // バナー(N行) + 音量(1行) + 区切り線(1行)

        // ══════════════════════════════════════════
        //  フッター (固定 3 行: 空行 / 区切り線 / キーガイド)
        // ══════════════════════════════════════════
        int scrollOff;
        lock (_lock) scrollOff = _scrollOffset;

        var footerRows = new List<IRenderable>
        {
            new Text(""),
            new Rule().RuleStyle("dim")
        };
        if (!_isTest)
        {
            var guide = "  [white bold]Ctrl+Q[/] [dim]停止[/]  │  " +
                "[white bold]F2[/] [dim]設定[/]  │  " +
                "[white bold]Ctrl+B[/] [dim]ブックマーク[/]  │  " +
                "[white bold]↑↓[/][dim]/[/][white bold]PgUp PgDn[/] [dim]スクロール[/]";
            if (scrollOff > 0)
                guide += "  │  [white bold]End[/] [dim]最新へ[/]";
            footerRows.Add(new Markup(guide));
        }
        var footer = new Rows(footerRows);
        int footerSize = footerRows.Count;   // 2 (テスト) or 3 (通常)

        // ══════════════════════════════════════════
        //  ボディ (残り全部 — Layout が自動クリップ)
        // ══════════════════════════════════════════
        int bodyHeight;
        try { bodyHeight = Math.Max(3, Console.WindowHeight - headerSize - footerSize); }
        catch { bodyHeight = 20; }

        List<TranscriptEntry> visible;
        Dictionary<string, double> proc;
        int totalEntries, hiddenAbove, hiddenBelow;
        lock (_lock)
        {
            totalEntries = _entries.Count;
            proc = new Dictionary<string, double>(_processing);

            int endIdx = Math.Max(0, totalEntries - scrollOff);

            // ボディ内のオーバーヘッド行 (インジケーター・スピナー)
            // 最悪ケースで 2 (↑↓) + proc.Count を差し引いてエントリに使える行数を計算
            int bodyOverhead = 2 + proc.Count;
            int linesForEntries = Math.Max(1, bodyHeight - bodyOverhead);

            // 末尾から遡り、折り返しを考慮して表示可能な行数に収まるエントリだけ選択
            int usedLines = 0;
            int startIdx = endIdx;
            for (int i = endIdx - 1; i >= 0; i--)
            {
                int lines = EstimateEntryLines(_entries[i], consoleWidth);
                if (usedLines + lines > linesForEntries) break;
                usedLines += lines;
                startIdx = i;
            }

            visible = _entries.GetRange(startIdx, endIdx - startIdx);
            hiddenAbove = startIdx;
            hiddenBelow = totalEntries - endIdx;
        }

        var bodyRows = new List<IRenderable>();

        if (visible.Count == 0 && proc.Count == 0)
        {
            bodyRows.Add(new Markup("  [dim italic]音声を待っています...[/]"));
        }

        if (hiddenAbove > 0)
            bodyRows.Add(new Markup($"  [dim]  ↑ さらに {hiddenAbove} 件[/]"));

        foreach (var entry in visible)
        {
            var icon = entry.Speaker == "自分" ? "▶" : "◀";
            var color = entry.Speaker == "自分" ? "cyan" : "yellow";
            bodyRows.Add(new Markup(
                $"  [dim]{entry.Timestamp:HH:mm:ss}[/]  " +
                $"[{color}]{icon} {Markup.Escape(entry.Speaker)}:[/] " +
                Markup.Escape(entry.Text)));

            // 翻訳テキストがある場合は直下に表示
            if (!string.IsNullOrEmpty(entry.TranslatedText))
            {
                bodyRows.Add(new Markup(
                    $"             [green]↳ {Markup.Escape(entry.TranslatedText)}[/]"));
            }
        }

        if (hiddenBelow > 0)
            bodyRows.Add(new Markup($"  [dim]  ↓ さらに {hiddenBelow} 件 (最新)[/]"));

        foreach (var (speaker, dur) in proc)
        {
            bodyRows.Add(new Markup(
                $"  [yellow]{spinnerFrame}[/] [dim][[{Markup.Escape(speaker)}]] {dur:F1}秒の音声を処理中...[/]"));
        }

        var body = new Rows(bodyRows);

        // ══════════════════════════════════════════
        //  Layout: ヘッダー/フッターを固定サイズで確保し
        //  ボディは残り全部。はみ出しは構造的にクリップ。
        // ══════════════════════════════════════════
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(headerSize),
                new Layout("Body"),
                new Layout("Footer").Size(footerSize));

        layout["Header"].Update(header);
        layout["Body"].Update(body);
        layout["Footer"].Update(footer);

        return layout;
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

    /// <summary>スクロール用: 大まかな最大表示件数 (キー入力でのオフセット上限計算に使用)</summary>
    private int EstimateMaxEntries()
    {
        // ヘッダー(2) + フッター(3) + ボディオーバーヘッド(2) = 7
        int overhead = 7 + _processing.Count;
        try { return Math.Max(3, Console.WindowHeight - overhead); }
        catch { return 20; }
    }

    /// <summary>エントリが端末上で何行を消費するかを見積もる (折り返し考慮)</summary>
    private static int EstimateEntryLines(TranscriptEntry entry, int consoleWidth)
    {
        // 表示プレフィクス: "  HH:mm:ss  ▶ Speaker: " (Markup タグは幅 0)
        int prefixWidth = 2 + 8 + 2 + 2 + EstimateDisplayWidth(entry.Speaker) + 2;
        int totalWidth = prefixWidth + EstimateDisplayWidth(entry.Text);
        int lines = Math.Max(1, (int)Math.Ceiling((double)totalWidth / Math.Max(1, consoleWidth)));

        // 翻訳行がある場合は追加行を計算
        if (!string.IsNullOrEmpty(entry.TranslatedText))
        {
            int transWidth = 13 + 2 + EstimateDisplayWidth(entry.TranslatedText); // "             ↳ "
            lines += Math.Max(1, (int)Math.Ceiling((double)transWidth / Math.Max(1, consoleWidth)));
        }

        return lines;
    }

    /// <summary>
    /// 文字列の端末上の表示幅を見積もる。
    /// CJK (日本語・中国語・韓国語) や全角文字は 2 カラム、それ以外は 1 カラム。
    /// </summary>
    private static int EstimateDisplayWidth(string text)
    {
        int width = 0;
        foreach (char c in text)
        {
            if (IsWideChar(c))
                width += 2;
            else
                width += 1;
        }
        return width;
    }

    /// <summary>端末で 2 カラム幅を占めるワイド文字かどうかを判定する</summary>
    private static bool IsWideChar(char c)
    {
        // CJK Unified Ideographs, Hiragana, Katakana, Hangul, Fullwidth Forms, etc.
        return c >= 0x1100 && (
            (c <= 0x115F) ||                           // Hangul Jamo
            (c >= 0x2E80 && c <= 0x303E) ||            // CJK Radicals, Kangxi, CJK Symbols
            (c >= 0x3041 && c <= 0x33BF) ||            // Hiragana, Katakana, Bopomofo, CJK Compat
            (c >= 0x3400 && c <= 0x4DBF) ||            // CJK Unified Ext A
            (c >= 0x4E00 && c <= 0xA4CF) ||            // CJK Unified, Yi
            (c >= 0xAC00 && c <= 0xD7AF) ||            // Hangul Syllables
            (c >= 0xF900 && c <= 0xFAFF) ||            // CJK Compat Ideographs
            (c >= 0xFE30 && c <= 0xFE6F) ||            // CJK Compat Forms
            (c >= 0xFF01 && c <= 0xFF60) ||            // Fullwidth Forms
            (c >= 0xFFE0 && c <= 0xFFE6));             // Fullwidth Signs
    }

    // ══════════════════════════════════════════════════
    //  静的ヘルパー (Live 表示の前後で使用)
    // ══════════════════════════════════════════════════

    /// <summary>バナーを表示する</summary>
    public static void PrintBanner(string engine, bool useGpu, string mic,
                                   string speaker, string fileName,
                                   bool test, int sec,
                                   GpuBackend gpuBackend = GpuBackend.None)
    {
        AnsiConsole.WriteLine();

        if (test)
            AnsiConsole.MarkupLine($"  [magenta][[テストモード]] {sec}秒後に自動停止[/]");

        var gpuTag = engine.StartsWith("whisper")
            ? (useGpu ? $"[green]{FormatGpuBackendTag(gpuBackend)}[/]" : "[dim]CPU[/]")
            : "";

        // 1行目: エンジン + デバイス
        AnsiConsole.MarkupLine(
            $"  [white bold]{Markup.Escape(engine.ToUpperInvariant())}[/]" +
            (gpuTag.Length > 0 ? $" ({gpuTag})" : "") +
            $"  [dim]│[/]  [cyan]🎤 {Markup.Escape(TruncateDevice(mic))}[/]" +
            $"  [dim]│[/]  [yellow]🔊 {Markup.Escape(TruncateDevice(speaker))}[/]" +
            $"  [dim]│[/]  [dim]→[/] {Markup.Escape(fileName)}");
    }

    /// <summary>GPU バックエンドの表示タグを生成する</summary>
    private static string FormatGpuBackendTag(GpuBackend backend) => backend switch
    {
        GpuBackend.Cuda => CudaHelper.DetectedCudaMajor > 0
            ? $"GPU/{CudaHelper.GetCudaVersionLabel()}"
            : "GPU/CUDA",
        GpuBackend.Vulkan => "GPU/Vulkan",
        _ => "GPU"
    };

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

/// <summary>
/// ネイティブライブラリ (ONNX Runtime, whisper.cpp) の stderr 出力を
/// Live 表示中に抑制するためのヘルパー。
/// C ランタイムの _dup/_dup2 を使ってファイルディスクリプタ 2 (stderr) を
/// NUL にリダイレクトし、セッション終了後に復帰する。
/// </summary>
internal static class NativeStderr
{
    private const int StderrFd = 2;

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int _dup(int fd);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int _dup2(int fd1, int fd2);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int _close(int fd);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int _open(string filename, int oflag);

    private const int O_WRONLY = 1;

    /// <summary>
    /// stderr を NUL にリダイレクトし、復帰用のファイルディスクリプタを返す。
    /// 失敗した場合は -1 を返す。
    /// </summary>
    public static int Suppress()
    {
        int saved = _dup(StderrFd);
        if (saved < 0) return -1;

        int nulFd = _open("NUL", O_WRONLY);
        if (nulFd < 0) { _close(saved); return -1; }

        _dup2(nulFd, StderrFd);
        _close(nulFd);
        return saved;
    }

    /// <summary>
    /// Suppress で保存したファイルディスクリプタから stderr を復帰する。
    /// </summary>
    public static void Restore(int savedFd)
    {
        if (savedFd < 0) return;
        _dup2(savedFd, StderrFd);
        _close(savedFd);
    }
}
