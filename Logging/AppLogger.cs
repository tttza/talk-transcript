using System.Collections.Concurrent;
using System.Text;

namespace TalkTranscript.Logging;

/// <summary>
/// ファイルとコンソールへのログ出力を統合するアプリケーションロガー。
/// スレッドセーフ。ログファイルは %APPDATA%\TalkTranscript\Logs\ に保存される。
/// </summary>
public static class AppLogger
{
    public enum LogLevel { Debug, Info, Warn, Error }

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TalkTranscript", "Logs");

    private static StreamWriter? _writer;
    private static readonly object _lock = new();
    private static LogLevel _minLevel = LogLevel.Info;
    private static bool _consoleOutput = false;
    private static readonly ConcurrentQueue<string> _recentErrors = new();
    private const int MaxRecentErrors = 50;

    /// <summary>最近のエラーメッセージ一覧 (UI 通知用)</summary>
    public static IReadOnlyCollection<string> RecentErrors => _recentErrors.ToArray();

    /// <summary>新しいエラーが追加されたときに発火するイベント</summary>
    public static event Action<string>? OnError;

    /// <summary>
    /// ロガーを初期化しファイルを開く。
    /// </summary>
    /// <param name="minLevel">出力する最小ログレベル</param>
    /// <param name="consoleOutput">コンソールにも出力するか</param>
    public static void Initialize(LogLevel minLevel = LogLevel.Info, bool consoleOutput = false)
    {
        _minLevel = minLevel;
        _consoleOutput = consoleOutput;

        try
        {
            Directory.CreateDirectory(LogDir);
            var logFile = Path.Combine(LogDir, $"app_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            // 既存のライターを閉じる (2回目以降の呼び出しでリークを防止)
            lock (_lock)
            {
                _writer?.Dispose();
            }

            var newWriter = new StreamWriter(logFile, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };
            newWriter.WriteLine($"=== TalkTranscript ログ開始 {DateTime.Now:yyyy/MM/dd HH:mm:ss} ===");
            newWriter.WriteLine($"OS: {Environment.OSVersion}");
            newWriter.WriteLine($".NET: {Environment.Version}");
            newWriter.WriteLine();

            lock (_lock)
            {
                _writer = newWriter;
            }

            // 古いログを削除 (7日以上前)
            CleanOldLogs();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ログファイルの初期化に失敗: {ex.Message}");
        }
    }

    public static void Debug(string message) => Log(LogLevel.Debug, message);
    public static void Info(string message) => Log(LogLevel.Info, message);
    public static void Warn(string message) => Log(LogLevel.Warn, message);
    public static void Error(string message, Exception? ex = null)
    {
        string full = ex != null ? $"{message}: {ex.Message}" : message;
        Log(LogLevel.Error, full);

        _recentErrors.Enqueue($"[{DateTime.Now:HH:mm:ss}] {full}");
        while (_recentErrors.Count > MaxRecentErrors)
            _recentErrors.TryDequeue(out _);

        try { OnError?.Invoke(full); } catch { /* イベントハンドラの例外が呼び出し元に伝播しないようにする */ }
    }

    private static void Log(LogLevel level, string message)
    {
        if (level < _minLevel) return;

        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-5}] {message}";

        lock (_lock)
        {
            _writer?.WriteLine(line);

            if (_consoleOutput && level >= LogLevel.Warn)
            {
                var prevColor = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    _ => ConsoleColor.Gray
                };
                Console.Error.WriteLine(line);
                Console.ForegroundColor = prevColor;
            }
        }
    }

    /// <summary>ログファイルを閉じる</summary>
    public static void Close()
    {
        lock (_lock)
        {
            _writer?.WriteLine();
            _writer?.WriteLine($"=== ログ終了 {DateTime.Now:yyyy/MM/dd HH:mm:ss} ===");
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>ログディレクトリのパスを返す</summary>
    public static string GetLogDirectory() => LogDir;

    private static void CleanOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.GetFiles(LogDir, "app_*.log"))
            {
                if (File.GetCreationTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* ログクリーンアップの失敗は無視 */ }
    }
}
