using System.Management;
using System.Runtime.InteropServices;

namespace TalkTranscript;

/// <summary>
/// GPU / CPU の環境情報を検出し、推奨 Whisper モデルを判定する。
/// </summary>
internal static class HardwareInfo
{
    public record GpuDevice(string Name, long VramMB, string Vendor);

    /// <summary>検出結果</summary>
    public record EnvironmentProfile
    {
        public bool HasNvidiaGpu { get; init; }
        public bool HasAmdGpu { get; init; }
        public bool HasIntelGpu { get; init; }
        public long GpuVramMB { get; init; }
        public string GpuName { get; init; } = "";
        public int CpuCores { get; init; }
        public long SystemRamMB { get; init; }

        /// <summary>この環境に最適な既定エンジン名</summary>
        public string RecommendedEngine { get; init; } = "vosk";
        /// <summary>GPU を使うべきか</summary>
        public bool RecommendedUseGpu { get; init; }
        /// <summary>推奨 GPU バックエンド (NVIDIA → CUDA、AMD/Intel → Vulkan)</summary>
        public GpuBackend RecommendedGpuBackend { get; init; } = GpuBackend.None;
    }

    /// <summary>環境を検出し推奨モデルを判定する</summary>
    public static EnvironmentProfile Detect()
    {
        var gpus = DetectGpus();
        int cpuCores = Environment.ProcessorCount;
        long ramMB = GetSystemRamMB();

        var nvidia = gpus.FirstOrDefault(g => g.Vendor == "NVIDIA");
        var amd = gpus.FirstOrDefault(g => g.Vendor == "AMD");
        var intel = gpus.FirstOrDefault(g => g.Vendor == "Intel");

        bool hasNvidia = nvidia != null;
        bool hasAmd = amd != null;
        bool hasIntel = intel != null;
        long vram = nvidia?.VramMB ?? amd?.VramMB ?? intel?.VramMB ?? 0;
        string gpuName = nvidia?.Name ?? amd?.Name ?? intel?.Name ?? "";

        // 推奨エンジン判定
        string engine;
        bool useGpu;
        GpuBackend gpuBackend;

        if (hasNvidia && vram >= 8000)
        {
            // 8GB+ VRAM (RTX 3060以上): large が動く
            engine = vram >= 12000 ? "whisper-large" : "whisper-medium";
            useGpu = true;
            gpuBackend = GpuBackend.Cuda;
        }
        else if (hasNvidia && vram >= 4000)
        {
            // 4-8GB VRAM: small まで
            engine = "whisper-small";
            useGpu = true;
            gpuBackend = GpuBackend.Cuda;
        }
        else if (hasNvidia && vram >= 2000)
        {
            // 2-4GB VRAM: base まで
            engine = "whisper-base";
            useGpu = true;
            gpuBackend = GpuBackend.Cuda;
        }
        else if (hasNvidia)
        {
            engine = "whisper-tiny";
            useGpu = true;
            gpuBackend = GpuBackend.Cuda;
        }
        else if (hasAmd && vram >= 8000)
        {
            // AMD GPU 8GB+: Vulkan で medium/large
            engine = vram >= 12000 ? "whisper-large" : "whisper-medium";
            useGpu = true;
            gpuBackend = GpuBackend.Vulkan;
        }
        else if (hasAmd && vram >= 4000)
        {
            // AMD GPU 4-8GB: Vulkan で small
            engine = "whisper-small";
            useGpu = true;
            gpuBackend = GpuBackend.Vulkan;
        }
        else if (hasAmd && vram >= 2000)
        {
            // AMD GPU 2-4GB: Vulkan で base
            engine = "whisper-base";
            useGpu = true;
            gpuBackend = GpuBackend.Vulkan;
        }
        else if (cpuCores >= 8 && ramMB >= 8000)
        {
            // 高性能 CPU (Ryzen 等): CPU で small まで実用的
            engine = "whisper-small";
            useGpu = false;
            gpuBackend = GpuBackend.None;
        }
        else if (cpuCores >= 4 && ramMB >= 4000)
        {
            // 標準的 CPU: base が実用的
            engine = "whisper-base";
            useGpu = false;
            gpuBackend = GpuBackend.None;
        }
        else
        {
            // 低スペック: vosk が最適
            engine = "vosk";
            useGpu = false;
            gpuBackend = GpuBackend.None;
        }

        return new EnvironmentProfile
        {
            HasNvidiaGpu = hasNvidia,
            HasAmdGpu = hasAmd,
            HasIntelGpu = hasIntel,
            GpuVramMB = vram,
            GpuName = gpuName,
            CpuCores = cpuCores,
            SystemRamMB = ramMB,
            RecommendedEngine = engine,
            RecommendedUseGpu = useGpu,
            RecommendedGpuBackend = gpuBackend
        };
    }

    /// <summary>エンジン名に対する推奨度ラベルを返す</summary>
    public static string GetRecommendation(string engineId, EnvironmentProfile env)
    {
        return engineId switch
        {
            "vosk" => "★",  // 常に使える
            "whisper-tiny" => env.CpuCores >= 2 ? "★" : "",
            "whisper-base" => GetWhisperRating(env, requiredVramMB: 1000, requiredCores: 4, requiredRamMB: 4000),
            "whisper-small" => GetWhisperRating(env, requiredVramMB: 2000, requiredCores: 8, requiredRamMB: 8000),
            "whisper-medium" => GetWhisperRating(env, requiredVramMB: 4000, requiredCores: 12, requiredRamMB: 16000),
            "whisper-large" => GetWhisperRating(env, requiredVramMB: 8000, requiredCores: 16, requiredRamMB: 16000),
            "sapi" => "",
            _ => ""
        };
    }

    private static string GetWhisperRating(EnvironmentProfile env, long requiredVramMB, int requiredCores, long requiredRamMB)
    {
        bool hasGpu = env.HasNvidiaGpu || env.HasAmdGpu;
        if (hasGpu && env.GpuVramMB >= requiredVramMB)
            return "★";  // GPU で快適
        if (!hasGpu && env.CpuCores >= requiredCores && env.SystemRamMB >= requiredRamMB)
            return "★";  // CPU でも実用可
        if (hasGpu && env.GpuVramMB >= requiredVramMB * 0.7)
            return "△";  // ギリギリ
        if (env.CpuCores >= requiredCores * 0.6 && env.SystemRamMB >= requiredRamMB * 0.7)
            return "△";  // CPU でやや重い
        return "✕";      // 非推奨
    }

    // ── GPU 検出 ──

    private static List<GpuDevice> DetectGpus()
    {
        var gpus = new List<GpuDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                // AdapterRAM は uint32 (最大4GB) なので VRAM が大きい場合は WMI の制限
                long vram = Convert.ToInt64(obj["AdapterRAM"] ?? 0);
                long vramMB = vram / (1024 * 1024);

                // 4GB 上限の WMI 制限を回避: 名前から推定
                if (vramMB <= 0 || vramMB >= 4095)
                    vramMB = EstimateVramFromName(name);

                string vendor = name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                    : name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                    : name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                    : "Unknown";

                gpus.Add(new GpuDevice(name, vramMB, vendor));
            }
        }
        catch
        {
            // WMI が使えない環境
        }
        return gpus;
    }

    /// <summary>GPU 名から VRAM を推定 (WMI の 4GB 上限回避用)</summary>
    private static long EstimateVramFromName(string name)
    {
        var n = name.ToUpperInvariant();
        // RTX 40 series
        if (n.Contains("4090")) return 24576;
        if (n.Contains("4080")) return 16384;
        if (n.Contains("4070 TI SUPER")) return 16384;
        if (n.Contains("4070 TI")) return 12288;
        if (n.Contains("4070 SUPER")) return 12288;
        if (n.Contains("4070")) return 12288;
        if (n.Contains("4060 TI")) return 8192; // 8GB が標準 (16GB 版は少数派)
        if (n.Contains("4060")) return 8192;
        // RTX 30 series
        if (n.Contains("3090")) return 24576;
        if (n.Contains("3080 TI")) return 12288;
        if (n.Contains("3080")) return 10240;
        if (n.Contains("3070 TI")) return 8192;
        if (n.Contains("3070")) return 8192;
        if (n.Contains("3060 TI")) return 8192;
        if (n.Contains("3060")) return 12288;
        if (n.Contains("3050")) return 8192;
        // RTX 20 series
        if (n.Contains("2080 TI")) return 11264;
        if (n.Contains("2080")) return 8192;
        if (n.Contains("2070")) return 8192;
        if (n.Contains("2060")) return 6144;
        // GTX 16 series
        if (n.Contains("1660")) return 6144;
        if (n.Contains("1650")) return 4096;
        // Fallback for NVIDIA
        if (n.Contains("NVIDIA")) return 4096;
        // Intel
        if (n.Contains("INTEL")) return 0; // 共有メモリ
        return 2048; // 安全な最小値
    }

    private static long GetSystemRamMB()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                long bytes = Convert.ToInt64(obj["TotalPhysicalMemory"] ?? 0);
                return bytes / (1024 * 1024);
            }
        }
        catch { }
        return 8192; // デフォルト推定
    }
}
