using TalkTranscript.Logging;
using TalkTranscript.Output;

namespace TalkTranscript;

/// <summary>
/// エンジン選択メニューを表示し、ユーザーにエンジンを選ばせる。
/// Program.cs から抽出。
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
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("  環境: ");
        Console.ForegroundColor = ConsoleColor.White;
        if (hwProfile.HasNvidiaGpu)
            Console.Write($"{hwProfile.GpuName} ({hwProfile.GpuVramMB / 1024}GB)");
        else
            Console.Write($"CPU {hwProfile.CpuCores}コア / RAM {hwProfile.SystemRamMB / 1024}GB");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("  エンジンを選択:");
        Console.WriteLine();

        for (int i = 0; i < Engines.Length; i++)
        {
            var (id, label, desc) = Engines[i];
            bool isCurrent = id == currentEngine;
            bool isRecommended = id == hwProfile.RecommendedEngine;
            string rating = HardwareInfo.GetRecommendation(id, hwProfile);

            Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.Write($"  {i + 1}. ");

            if (rating == "★")
                Console.ForegroundColor = ConsoleColor.Green;
            else if (rating == "△")
                Console.ForegroundColor = ConsoleColor.Yellow;
            else if (rating == "✕")
                Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{rating,-2}");

            Console.ForegroundColor = isCurrent ? ConsoleColor.Green : ConsoleColor.White;
            Console.Write($"{label,-16}");

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(desc);

            if (isCurrent)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(" ← 現在");
            }
            else if (isRecommended)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write(" ← 推奨");
            }

            Console.ResetColor();
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.Write($"  番号 [1-{Engines.Length}] (Enter でキャンセル): ");

        string? input = Console.ReadLine();
        if (int.TryParse(input, out int choice) && choice >= 1 && choice <= Engines.Length)
        {
            var (selId, selLabel, _) = Engines[choice - 1];
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  → {selLabel}");
            Console.ResetColor();
            AppLogger.Info($"エンジン変更: {selId}");
            return selId;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  キャンセルしました");
            Console.ResetColor();
            return null;
        }
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
