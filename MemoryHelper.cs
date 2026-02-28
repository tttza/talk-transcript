using System.Runtime.InteropServices;
using TalkTranscript.Logging;

namespace TalkTranscript;

/// <summary>
/// システムの利用可能メモリを監視し、バッファサイズの上限を動的に計算する。
///
/// 処理遅延でバッファが膨張しても、物理メモリを使い切らないよう制御する。
/// RecordingBuffer / BackpressureMonitor の双方から利用される。
/// </summary>
internal static class MemoryHelper
{
    /// <summary>バッファに割り当て可能な利用可能メモリの割合</summary>
    private const double MaxBufferRatio = 0.30;

    /// <summary>最低限確保すべき空きメモリ (MB)</summary>
    private const long MinFreeMemoryMB = 512;

    /// <summary>バッファの絶対的最小サイズ (30MB ≈ 15分 @ 32KB/s)</summary>
    private const long AbsoluteMinBufferBytes = 30L * 1024 * 1024;

    /// <summary>バッファの絶対的最大サイズ (500MB)</summary>
    private const long AbsoluteMaxBufferBytes = 500L * 1024 * 1024;

    /// <summary>ログ出力の抑制用 (同じ警告を短時間に連発しない)</summary>
    private static DateTime _lastWarnTime = DateTime.MinValue;

    // ── Win32 API ──
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>
    /// 利用可能な物理メモリ (バイト) を返す。
    /// </summary>
    public static long GetAvailablePhysicalMemory()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return (long)status.ullAvailPhys;
        }
        catch { }

        // フォールバック: GC 情報から推定
        var gcInfo = GC.GetGCMemoryInfo();
        return Math.Max(0, gcInfo.TotalAvailableMemoryBytes - gcInfo.MemoryLoadBytes);
    }

    /// <summary>
    /// メモリ使用率 (0–100) を返す。
    /// </summary>
    public static int GetMemoryLoadPercent()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return (int)status.dwMemoryLoad;
        }
        catch { }
        return 50; // 推定
    }

    /// <summary>
    /// 現在の利用可能メモリに基づき、バッファ 1 つあたりの推奨最大バイト数を返す。
    /// RecordingBuffer の初期化時に使用。
    /// </summary>
    /// <param name="bufferCount">同時使用バッファ数 (通常: mic + speaker = 2)</param>
    /// <param name="desiredMaxBytes">希望する最大バイト数 (上限キャップ)</param>
    public static long CalculateBufferMaxBytes(int bufferCount = 2, long desiredMaxBytes = 240L * 1024 * 1024)
    {
        long available = GetAvailablePhysicalMemory();
        long minFreeBytes = MinFreeMemoryMB * 1024 * 1024;

        // 利用可能メモリから最低限の空きを確保した残りの MaxBufferRatio を割り当て
        long usableForBuffers = (long)((available - minFreeBytes) * MaxBufferRatio);
        if (usableForBuffers <= 0)
            usableForBuffers = AbsoluteMinBufferBytes * bufferCount;

        long perBuffer = usableForBuffers / Math.Max(1, bufferCount);

        // 上下限でクランプ
        perBuffer = Math.Clamp(perBuffer, AbsoluteMinBufferBytes, Math.Min(desiredMaxBytes, AbsoluteMaxBufferBytes));

        long availMB = available / (1024 * 1024);
        long perBufMB = perBuffer / (1024 * 1024);
        AppLogger.Info($"[MemoryHelper] 空きメモリ: {availMB}MB → バッファ上限: {perBufMB}MB/個 (×{bufferCount})");

        return perBuffer;
    }

    /// <summary>
    /// 処理遅延バッファの上限を、現在の空きメモリに基づいてクランプする。
    /// BackpressureMonitor から呼ばれる。
    /// </summary>
    /// <param name="delayBasedCapacity">処理遅延から算出したバッファ上限 (バイト)</param>
    /// <returns>メモリ制約を適用した上限 (バイト)</returns>
    public static long ClampToAvailableMemory(long delayBasedCapacity)
    {
        long available = GetAvailablePhysicalMemory();
        long minFreeBytes = MinFreeMemoryMB * 1024 * 1024;

        if (available < minFreeBytes)
        {
            // メモリ逼迫: バッファを大幅縮小 (1/4 に絞る)
            long reduced = Math.Max(AbsoluteMinBufferBytes, delayBasedCapacity / 4);
            WarnMemoryLow(available, reduced);
            return reduced;
        }

        // 利用可能メモリの MaxBufferRatio を上限とする
        long maxAllowable = (long)((available - minFreeBytes) * MaxBufferRatio);
        long result = Math.Min(delayBasedCapacity, Math.Max(AbsoluteMinBufferBytes, maxAllowable));

        return result;
    }

    private static void WarnMemoryLow(long availableBytes, long reducedBytes)
    {
        if ((DateTime.UtcNow - _lastWarnTime).TotalSeconds < 30) return;
        _lastWarnTime = DateTime.UtcNow;

        long availMB = availableBytes / (1024 * 1024);
        long reducedKB = reducedBytes / 1024;
        AppLogger.Warn($"[MemoryHelper] メモリ逼迫 (空き: {availMB}MB) — バッファ上限を {reducedKB}KB に縮小");
    }
}
