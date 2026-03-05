using System.Runtime.InteropServices;
using TalkTranscript.Logging;

namespace TalkTranscript;

/// <summary>
/// CUDA ランタイムの検出・セットアップを行うヘルパー。
/// CUDA 12 (cublas64_12.dll) と CUDA 13 (cublas64_13.dll) の両方に対応。
///
/// プロセス起動直後は Windows Defender 等のスキャンにより File.Exists が
/// 一時的に false を返すことがあるため、リトライと複数検出手段を併用する。
/// </summary>
internal static class CudaHelper
{
    /// <summary>検出された CUDA バージョン (12 or 13、未検出は 0)</summary>
    public static int DetectedCudaMajor { get; private set; }

    /// <summary>成功結果のみキャッシュ (失敗は一時的な可能性があるためキャッシュしない)</summary>
    private static bool _confirmedAvailable;

    /// <summary>検出済みの CUDA Toolkit ディレクトリ (成功時にキャッシュ)</summary>
    private static string? _cachedToolkitDir;

    /// <summary>リトライ回数</summary>
    private const int MaxRetries = 3;

    /// <summary>リトライ間隔 (ms)</summary>
    private const int RetryDelayMs = 200;

    /// <summary>
    /// CUDA DLL (runtimes/cuda/win-x64/) と CUDA Toolkit (cublas64_12/13.dll) が
    /// 利用可能かチェック。成功結果はプロセス存続中キャッシュされる。
    /// 失敗時はキャッシュせず、次回呼び出し時にリトライする。
    /// </summary>
    public static bool IsCudaAvailable()
    {
        if (_confirmedAvailable)
            return true;

        // ── Step 1: ggml-cuda-whisper.dll の存在確認 (リトライ付き) ──
        var exeDir = AppContext.BaseDirectory;
        var cudaDir = Path.Combine(exeDir, "runtimes", "cuda", "win-x64");
        var dllPath = Path.Combine(cudaDir, "ggml-cuda-whisper.dll");

        bool dllFound = FileExistsWithRetry(dllPath);
        if (!dllFound)
        {
            // File.Exists が失敗しても NativeLibrary.TryLoad で検出できる場合がある
            // (アンチウイルスが File.Exists をブロックしてもロード自体は成功するケース)
            dllFound = NativeLibrary.TryLoad(dllPath, out _);
        }

        if (!dllFound)
        {
            AppLogger.Debug($"CUDA 不可: {dllPath} が見つかりません");
            return false; // キャッシュしない → 次回リトライ
        }

        // ── Step 2: CUDA Toolkit の検出 (リトライ付き) ──
        var toolkitDir = FindCudaToolkitBinDir();
        if (toolkitDir == null)
        {
            // PATH 上の cublas DLL も探す (CUDA Toolkit が非標準パスにある場合)
            toolkitDir = FindCublasInPath();
        }

        if (toolkitDir == null)
        {
            AppLogger.Debug("CUDA 不可: CUDA Toolkit (cublas64) が見つかりません");
            return false; // キャッシュしない → 次回リトライ
        }

        AppLogger.Debug($"CUDA 利用可能: toolkit={toolkitDir}, CUDA {DetectedCudaMajor}");
        _confirmedAvailable = true;
        _cachedToolkitDir = toolkitDir;
        return true;
    }

    /// <summary>
    /// CUDA Toolkit の bin ディレクトリを検索。
    /// cublas64_12.dll (CUDA 12) と cublas64_13.dll (CUDA 13) の両方を探す。
    /// 新しいバージョンを優先する。
    /// </summary>
    public static string? FindCudaToolkitBinDir()
    {
        // キャッシュ済みならそのまま返す
        if (_cachedToolkitDir != null)
            return _cachedToolkitDir;

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

        // CUDA 13 を優先して検索 (リトライ付き)
        foreach (var dir in candidates)
        {
            if (FileExistsWithRetry(Path.Combine(dir, "cublas64_13.dll")))
            {
                DetectedCudaMajor = 13;
                _cachedToolkitDir = dir;
                return dir;
            }
        }

        // CUDA 12 にフォールバック (リトライ付き)
        foreach (var dir in candidates)
        {
            if (FileExistsWithRetry(Path.Combine(dir, "cublas64_12.dll")))
            {
                DetectedCudaMajor = 12;
                _cachedToolkitDir = dir;
                return dir;
            }
        }

        DetectedCudaMajor = 0;
        return null;
    }

    /// <summary>
    /// PATH 環境変数から cublas64 DLL を検索する。
    /// CUDA_PATH が未設定でも PATH に CUDA bin が含まれていれば検出できる。
    /// </summary>
    private static string? FindCublasInPath()
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        // CUDA 13 を優先
        foreach (var dir in pathDirs)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "cublas64_13.dll")))
                {
                    DetectedCudaMajor = 13;
                    _cachedToolkitDir = dir;
                    return dir;
                }
            }
            catch { }
        }

        foreach (var dir in pathDirs)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "cublas64_12.dll")))
                {
                    DetectedCudaMajor = 12;
                    _cachedToolkitDir = dir;
                    return dir;
                }
            }
            catch { }
        }

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
        var toolkitDir = _cachedToolkitDir ?? FindCudaToolkitBinDir() ?? FindCublasInPath();

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

    /// <summary>
    /// File.Exists をリトライ付きで実行する。
    /// プロセス起動直後にアンチウイルスのリアルタイムスキャンが
    /// ファイルをロックし、File.Exists が一時的に false を返す問題に対処する。
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
