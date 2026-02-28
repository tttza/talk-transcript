using System.Runtime.InteropServices;
using TalkTranscript.Logging;

namespace TalkTranscript;

/// <summary>
/// CUDA ランタイムの検出・セットアップを行うヘルパー。
/// Program.cs から抽出。
/// </summary>
internal static class CudaHelper
{
    /// <summary>
    /// CUDA DLL (runtimes/cuda/win-x64/) と CUDA Toolkit (cublas64_13.dll) が
    /// 利用可能かチェック。
    /// </summary>
    public static bool IsCudaAvailable()
    {
        var exeDir = AppContext.BaseDirectory;
        var cudaDir = Path.Combine(exeDir, "runtimes", "cuda", "win-x64");
        if (!File.Exists(Path.Combine(cudaDir, "ggml-cuda-whisper.dll")))
            return false;
        return FindCudaToolkitBinDir() != null;
    }

    /// <summary>CUDA Toolkit の bin ディレクトリ (cublas64_13.dll が存在する場所) を検索</summary>
    public static string? FindCudaToolkitBinDir()
    {
        var candidates = new List<string>();
        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(cudaPath))
        {
            candidates.Add(Path.Combine(cudaPath, "bin", "x64"));
            candidates.Add(Path.Combine(cudaPath, "bin"));
        }
        foreach (var ver in new[] { "v13.1", "v13.0", "v12.6", "v12.5", "v12.4" })
        {
            candidates.Add($@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\{ver}\bin\x64");
            candidates.Add($@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\{ver}\bin");
        }
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "cublas64_13.dll")))
                return dir;
        }
        return null;
    }

    /// <summary>
    /// CUDA DLL をプリロードして CPU 版より先にロードさせる。
    /// PATH に CUDA Toolkit ディレクトリを追加し、依存解決も可能にする。
    /// </summary>
    public static void SetupRuntime()
    {
        var exeDir = AppContext.BaseDirectory;
        var cudaDir = Path.Combine(exeDir, "runtimes", "cuda", "win-x64");
        var toolkitDir = FindCudaToolkitBinDir();

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var newPaths = new List<string>();
        if (Directory.Exists(cudaDir)) newPaths.Add(cudaDir);
        if (toolkitDir != null) newPaths.Add(toolkitDir);
        if (newPaths.Count > 0)
            Environment.SetEnvironmentVariable("PATH", string.Join(";", newPaths) + ";" + path);

        foreach (var dll in new[] { "ggml-base-whisper.dll", "ggml-cpu-whisper.dll",
                                    "ggml-cuda-whisper.dll", "ggml-whisper.dll", "whisper.dll" })
        {
            var fullPath = Path.Combine(cudaDir, dll);
            if (File.Exists(fullPath))
            {
                NativeLibrary.TryLoad(fullPath, out _);
                AppLogger.Debug($"CUDA DLL ロード: {dll}");
            }
        }

        AppLogger.Info("CUDA ランタイムセットアップ完了");
    }
}
