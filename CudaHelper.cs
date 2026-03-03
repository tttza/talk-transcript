using System.Runtime.InteropServices;
using TalkTranscript.Logging;

namespace TalkTranscript;

/// <summary>
/// CUDA ランタイムの検出・セットアップを行うヘルパー。
/// CUDA 12 (cublas64_12.dll) と CUDA 13 (cublas64_13.dll) の両方に対応。
/// </summary>
internal static class CudaHelper
{
    /// <summary>検出された CUDA バージョン (12 or 13、未検出は 0)</summary>
    public static int DetectedCudaMajor { get; private set; }

    /// <summary>
    /// CUDA DLL (runtimes/cuda/win-x64/) と CUDA Toolkit (cublas64_12/13.dll) が
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

    /// <summary>
    /// CUDA Toolkit の bin ディレクトリを検索。
    /// cublas64_12.dll (CUDA 12) と cublas64_13.dll (CUDA 13) の両方を探す。
    /// 新しいバージョンを優先する。
    /// </summary>
    public static string? FindCudaToolkitBinDir()
    {
        var candidates = new List<string>();
        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        if (!string.IsNullOrEmpty(cudaPath))
        {
            candidates.Add(Path.Combine(cudaPath, "bin", "x64"));
            candidates.Add(Path.Combine(cudaPath, "bin"));
        }
        foreach (var ver in new[] { "v13.1", "v13.0", "v12.8", "v12.6", "v12.5", "v12.4", "v12.3", "v12.2", "v12.1", "v12.0" })
        {
            candidates.Add($@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\{ver}\bin\x64");
            candidates.Add($@"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\{ver}\bin");
        }

        // CUDA 13 を優先して検索
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "cublas64_13.dll")))
            {
                DetectedCudaMajor = 13;
                return dir;
            }
        }

        // CUDA 12 にフォールバック
        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "cublas64_12.dll")))
            {
                DetectedCudaMajor = 12;
                return dir;
            }
        }

        DetectedCudaMajor = 0;
        return null;
    }

    /// <summary>検出された CUDA バージョンの表示文字列を返す</summary>
    public static string GetCudaVersionLabel()
    {
        return DetectedCudaMajor > 0 ? $"CUDA {DetectedCudaMajor}" : "CUDA";
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

        AppLogger.Info($"CUDA ランタイムセットアップ完了 ({GetCudaVersionLabel()})");
    }
}
