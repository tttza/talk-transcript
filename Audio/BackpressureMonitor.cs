using TalkTranscript.Logging;

namespace TalkTranscript.Audio;

/// <summary>
/// 処理遅延を監視し、バッファパラメータを動的に調整するバックプレッシャー制御。
///
/// Whisper 等の重い推論処理がリアルタイムに追いつかない場合、
/// チャンク長を伸ばして処理回数を減らし、メモリ蓄積を抑制する。
/// 処理が回復傾向になればパラメータを徐々に元に戻す。
/// </summary>
internal sealed class BackpressureMonitor
{
    private readonly double _defaultMinBufferSec;
    private readonly double _defaultMaxBufferSec;
    private int _consecutiveSlowChunks;
    private long _totalDroppedBytes;

    /// <summary>現在の最小バッファ秒数 (遅延時は引き上げる)</summary>
    public double CurrentMinBufferSec { get; private set; }

    /// <summary>現在の最大バッファ秒数 (遅延時は引き上げる)</summary>
    public double CurrentMaxBufferSec { get; private set; }

    /// <summary>連続して処理が遅延しているチャンク数</summary>
    public int ConsecutiveSlowChunks => _consecutiveSlowChunks;

    /// <summary>バッファオーバーフローにより破棄した総バイト数</summary>
    public long TotalDroppedBytes => Interlocked.Read(ref _totalDroppedBytes);

    /// <summary>古いオーディオを破棄すべき状態か (連続3チャンク以上遅延)</summary>
    public bool ShouldDropOldAudio => _consecutiveSlowChunks >= 3;

    /// <param name="defaultMinBufferSec">デフォルト最小バッファ秒数</param>
    /// <param name="defaultMaxBufferSec">デフォルト最大バッファ秒数</param>
    public BackpressureMonitor(double defaultMinBufferSec = 1.2, double defaultMaxBufferSec = 12.0)
    {
        _defaultMinBufferSec = defaultMinBufferSec;
        _defaultMaxBufferSec = defaultMaxBufferSec;
        CurrentMinBufferSec = defaultMinBufferSec;
        CurrentMaxBufferSec = defaultMaxBufferSec;
    }

    /// <summary>
    /// チャンク処理結果を報告し、パラメータを動的に調整する。
    /// </summary>
    /// <param name="audioSec">処理した音声の長さ (秒)</param>
    /// <param name="processingTimeSec">実際の処理時間 (秒)</param>
    public void ReportChunkProcessed(double audioSec, double processingTimeSec)
    {
        if (processingTimeSec > audioSec * 1.2)
        {
            // 処理が 1.2 倍以上かかっている → 遅延
            _consecutiveSlowChunks++;

            // チャンク長を延ばして処理回数を減らす
            CurrentMinBufferSec = Math.Min(5.0, CurrentMinBufferSec + 0.5);
            CurrentMaxBufferSec = Math.Min(30.0, CurrentMaxBufferSec + 2.0);

            if (_consecutiveSlowChunks == 1)
            {
                AppLogger.Warn($"[Backpressure] 処理遅延検出: 音声 {audioSec:F1}s に対し {processingTimeSec:F1}s " +
                               $"(バッファ: {CurrentMinBufferSec:F1}-{CurrentMaxBufferSec:F1}s)");
            }
            else if (_consecutiveSlowChunks % 5 == 0)
            {
                AppLogger.Warn($"[Backpressure] 連続遅延 {_consecutiveSlowChunks} チャンク " +
                               $"(バッファ: {CurrentMinBufferSec:F1}-{CurrentMaxBufferSec:F1}s, " +
                               $"破棄済: {_totalDroppedBytes / 1024}KB)");
            }
        }
        else
        {
            // 処理が追いついている → 徐々に回復
            _consecutiveSlowChunks = Math.Max(0, _consecutiveSlowChunks - 1);

            CurrentMinBufferSec = Math.Max(_defaultMinBufferSec, CurrentMinBufferSec - 0.2);
            CurrentMaxBufferSec = Math.Max(_defaultMaxBufferSec, CurrentMaxBufferSec - 1.0);
        }
    }

    /// <summary>
    /// バッファオーバーフローにより破棄したバイト数を記録する。
    /// </summary>
    public void ReportDropped(long droppedBytes)
    {
        Interlocked.Add(ref _totalDroppedBytes, droppedBytes);
    }

    /// <summary>
    /// 現在のバッファ長上限 (バイト単位) を取得する。
    /// 処理遅延ベースの値を算出したうえで、利用可能メモリに基づいてクランプする。
    /// </summary>
    /// <param name="sampleRate">サンプルレート (Hz)</param>
    /// <param name="bytesPerSample">1サンプルのバイト数 (通常2 = 16bit)</param>
    public long GetBufferCapacityBytes(int sampleRate, int bytesPerSample = 2)
    {
        long delayBased = (long)(CurrentMaxBufferSec * sampleRate * bytesPerSample);
        return MemoryHelper.ClampToAvailableMemory(delayBased);
    }
}
