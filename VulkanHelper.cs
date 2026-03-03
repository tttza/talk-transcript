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
    /// <summary>
    /// Vulkan ランタイム DLL (runtimes/vulkan/win-x64/ または直下) が
    /// 利用可能かチェック。
    /// </summary>
    public static bool IsVulkanAvailable()
    {
        return FindVulkanDll() != null;
    }

    /// <summary>ggml-vulkan-whisper.dll のパスを検索する</summary>
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
            if (File.Exists(path))
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
}
