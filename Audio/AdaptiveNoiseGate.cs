namespace TalkTranscript.Audio;

/// <summary>
/// 環境ノイズに基づいて無音閾値を動的に調整するアダプティブノイズゲート。
/// 直近の RMS エネルギー履歴からノイズフロアを推定し、
/// 閾値を自動的に引き上げ/引き下げする。
/// </summary>
internal sealed class AdaptiveNoiseGate
{
    private readonly Queue<float> _rmsHistory = new();
    private readonly int _historySize;
    private readonly float _minThreshold;
    private readonly float _maxThreshold;
    private readonly float _multiplier;

    private float _currentThreshold;
    private float _noiseFloor;

    /// <summary>現在の動的閾値 (ピーク判定用)</summary>
    public float CurrentThreshold => _currentThreshold;

    /// <summary>推定されたノイズフロア (RMS)</summary>
    public float NoiseFloor => _noiseFloor;

    /// <param name="initialThreshold">初期閾値</param>
    /// <param name="historySize">ノイズフロア推定に使うサンプル数</param>
    /// <param name="multiplier">ノイズフロアに対する閾値の倍率 (通常 2.5〜4.0)</param>
    /// <param name="minThreshold">閾値の下限</param>
    /// <param name="maxThreshold">閾値の上限</param>
    public AdaptiveNoiseGate(
        float initialThreshold = 250f,
        int historySize = 50,
        float multiplier = 3.0f,
        float minThreshold = 100f,
        float maxThreshold = 2000f)
    {
        _historySize = historySize;
        _multiplier = multiplier;
        _minThreshold = minThreshold;
        _maxThreshold = maxThreshold;
        _currentThreshold = initialThreshold;
        _noiseFloor = initialThreshold / multiplier;
    }

    /// <summary>
    /// 新しい RMS 値を供給し、ノイズフロアと閾値を更新する。
    /// </summary>
    /// <param name="rms">チャンクの RMS エネルギー</param>
    public void Update(float rms)
    {
        _rmsHistory.Enqueue(rms);
        while (_rmsHistory.Count > _historySize)
            _rmsHistory.Dequeue();

        if (_rmsHistory.Count < 5) return;

        // ノイズフロア = 下位25%の中央値 (外れ値に頑健)
        var sorted = _rmsHistory.OrderBy(x => x).ToArray();
        int q1Index = sorted.Length / 4;
        _noiseFloor = sorted[Math.Max(0, q1Index)];

        // 閾値 = ノイズフロア × 倍率 (範囲制限)
        _currentThreshold = Math.Clamp(_noiseFloor * _multiplier, _minThreshold, _maxThreshold);
    }

    /// <summary>
    /// 指定されたピーク値が音声と判定されるかどうかを返す。
    /// </summary>
    public bool IsVoice(short peak, float rms)
    {
        return peak >= _currentThreshold || rms >= _currentThreshold * 0.7f;
    }
}
