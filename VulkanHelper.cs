using System.Runtime.InteropServices;
using TalkTranscript.Logging;

namespace TalkTranscript;

/// <summary>
/// Vulkan ランタイムの検出・セットアップを行うヘルパー。
/// Whisper.net.Runtime.Vulkan パッケージが提供する ggml-vulkan-whisper.dll を使用。
/// AMD / Intel / NVIDIA GPU で動作する汎用 GPU バックエンド。
/// </summary>
internal static class VulkanHelper
{
    /// <summary>成功結果のみキャッシュ (失敗は一時的な可能性があるためキャッシュしない)</summary>
    private static bool _confirmedAvailable;

    /// <summary>リトライ回数</summary>
    private const int MaxRetries = 3;

    /// <summary>リトライ間隔 (ms)</summary>
    private const int RetryDelayMs = 200;

    /// <summary>
    /// Vulkan ランタイム DLL (runtimes/vulkan/win-x64/ または直下) が
    /// 利用可能かチェック。成功結果はプロセス存続中キャッシュされる。
    /// 失敗時はキャッシュせず、次回呼び出し時にリトライする。
    /// </summary>
    public static bool IsVulkanAvailable()
    {
        if (_confirmedAvailable)
            return true;

        var dir = FindVulkanDll();
        if (dir != null)
        {
            _confirmedAvailable = true;
            return true;
        }
        return false; // キャッシュしない → 次回リトライ
    }

    /// <summary>ggml-vulkan-whisper.dll のパスを検索する (リトライ付き)</summary>
    private static string? FindVulkanDll()
    {
        var exeDir = AppContext.BaseDirectory;

        // Whisper.net.Runtime.Vulkan パッケージの配置先候補
        var candidates = new[]
        {
            Path.Combine(exeDir, "runtimes", "vulkan", "win-x64"),
            Path.Combine(exeDir, "runtimes", "win-x64", "native"),
            exeDir
        };

        foreach (var dir in candidates)
        {
            var path = Path.Combine(dir, "ggml-vulkan-whisper.dll");
            if (FileExistsWithRetry(path))
                return dir;
        }

        return null;
    }

    /// <summary>
    /// Vulkan DLL をプリロードして CPU 版より先にロードさせる。
    /// </summary>
    public static void SetupRuntime()
    {
        var vulkanDir = FindVulkanDll();
        if (vulkanDir == null)
        {
            AppLogger.Warn("Vulkan ランタイム DLL が見つかりません");
            return;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Contains(vulkanDir))
            Environment.SetEnvironmentVariable("PATH", vulkanDir + ";" + path);

        foreach (var dll in new[] { "ggml-base-whisper.dll", "ggml-cpu-whisper.dll",
                                    "ggml-vulkan-whisper.dll", "ggml-whisper.dll", "whisper.dll" })
        {
            var fullPath = Path.Combine(vulkanDir, dll);
            if (File.Exists(fullPath))
            {
                NativeLibrary.TryLoad(fullPath, out _);
                AppLogger.Debug($"Vulkan DLL ロード: {dll}");
            }
        }

        AppLogger.Info("Vulkan ランタイムセットアップ完了");
    }

    /// <summary>
    /// File.Exists をリトライ付きで実行する。
    /// </summary>
    private static bool FileExistsWithRetry(string path)
    {
        for (int i = 0; i < MaxRetries; i++)
        {
            try
            {
                if (File.Exists(path))
                    return true;
            }
            catch { }

            if (i < MaxRetries - 1)
                Thread.Sleep(RetryDelayMs);
        }
        return false;
    }
}
