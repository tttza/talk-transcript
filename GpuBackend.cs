namespace TalkTranscript;

/// <summary>
/// GPU アクセラレーションのバックエンド種別。
/// Whisper 推論で使用する GPU ランタイムを指定する。
/// </summary>
public enum GpuBackend
{
    /// <summary>CPU のみ (GPU を使用しない)</summary>
    None,

    /// <summary>NVIDIA CUDA (CUDA 12/13 対応)</summary>
    Cuda,

    /// <summary>Vulkan (NVIDIA / AMD / Intel GPU で動作)</summary>
    Vulkan,

    /// <summary>自動検出 (NVIDIA → CUDA、AMD/Intel → Vulkan、GPU なし → CPU)</summary>
    Auto
}
