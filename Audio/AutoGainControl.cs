namespace TalkTranscript.Audio;

/// <summary>
/// 自動ゲイン制御 (AGC)。
/// 入力音声の RMS エネルギーに基づいてゲインを動的に調整し、
/// 小さい声でもノイズゲートを通過して認識エンジンに届くようにする。
///
/// <b>動作原理</b>:
/// 現在の RMS が目標 RMS を下回っている場合にゲインを徐々に上げ、
/// 超えている場合は徐々に下げる (スムージング)。
/// 急激なゲイン変動を防ぐため、攻撃時間 (ゲイン上昇) と
/// リリース時間 (ゲイン低下) を分離している。
///
/// <b>使い方</b>:
/// <see cref="Process"/> で PCM 16bit バッファにインプレースでゲインを適用する。
/// ゲイン係数が 1.0 の場合は何もしない (無効時のオーバーヘッドなし)。
/// </summary>
internal sealed class AutoGainControl
{
    /// <summary>目標 RMS エネルギー (16bit PCM)。この値に近づくようゲインを調整する。</summary>
    private readonly float _targetRms;

    /// <summary>最大ゲイン倍率。ノイズまで増幅しすぎないための安全弁。</summary>
    private readonly float _maxGain;

    /// <summary>最小ゲイン倍率 (通常 1.0 = 減衰しない)。</summary>
    private readonly float _minGain;

    /// <summary>ゲイン上昇速度 (攻撃)。0〜1 の範囲で、1 に近いほど追従が速い。</summary>
    private readonly float _attackRate;

    /// <summary>ゲイン低下速度 (リリース)。0〜1 の範囲で、攻撃より遅くして安定させる。</summary>
    private readonly float _releaseRate;

    /// <summary>現在のスムージングされたゲイン係数</summary>
    private float _currentGain = 1.0f;

    /// <summary>AGC が有効かどうか</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>現在のゲイン倍率 (デバッグ/表示用)</summary>
    public float CurrentGain => _currentGain;

    /// <summary>
    /// AGC を初期化する。
    /// </summary>
    /// <param name="targetRms">目標 RMS (推奨: 3000〜5000)</param>
    /// <param name="maxGain">最大ゲイン倍率 (推奨: 8〜15)</param>
    /// <param name="minGain">最小ゲイン倍率 (通常 1.0)</param>
    /// <param name="attackRate">ゲイン上昇速度 (推奨: 0.03〜0.1)</param>
    /// <param name="releaseRate">ゲイン低下速度 (推奨: 0.005〜0.02)</param>
    public AutoGainControl(
        float targetRms = 3000f,
        float maxGain = 10f,
        float minGain = 1.0f,
        float attackRate = 0.05f,
        float releaseRate = 0.01f)
    {
        _targetRms = targetRms;
        _maxGain = maxGain;
        _minGain = minGain;
        _attackRate = attackRate;
        _releaseRate = releaseRate;
    }

    /// <summary>
    /// PCM 16bit バッファにゲインをインプレースで適用する。
    /// RMS に基づいてゲインを動的に調整し、クリッピング保護を行う。
    /// </summary>
    /// <param name="buffer">PCM 16bit LE バッファ</param>
    /// <param name="length">処理するバイト数</param>
    /// <returns>ゲイン適用後のピーク値 (VAD 判定に使用)</returns>
    public short Process(byte[] buffer, int length)
    {
        if (!Enabled || length < 2)
            return AudioProcessing.CalcPeak(buffer, length);

        // 現在の RMS を計算
        float rms = AudioProcessing.CalcRms(buffer, length);

        // RMS が極端に小さい場合 (実質無音) はゲインを変更しない
        if (rms < 10f)
            return AudioProcessing.CalcPeak(buffer, length);

        // 目標ゲインを算出: targetRms / currentRms
        float desiredGain = _targetRms / rms;
        desiredGain = Math.Clamp(desiredGain, _minGain, _maxGain);

        // スムージング: 攻撃 (ゲイン上昇) はリリース (低下) より速い
        float rate = desiredGain > _currentGain ? _attackRate : _releaseRate;
        _currentGain += (desiredGain - _currentGain) * rate;
        _currentGain = Math.Clamp(_currentGain, _minGain, _maxGain);

        // ゲインが 1.0 に十分近い場合はスキップ (不要な処理を回避)
        if (MathF.Abs(_currentGain - 1.0f) < 0.05f)
            return AudioProcessing.CalcPeak(buffer, length);

        // ゲインを適用 (インプレース、クリッピング保護付き)
        return ApplyGain(buffer, length, _currentGain);
    }

    /// <summary>
    /// 固定ゲインを PCM 16bit バッファにインプレースで適用する。
    /// クリッピングを防止するため short 範囲にクランプする。
    /// </summary>
    /// <param name="buffer">PCM 16bit LE バッファ</param>
    /// <param name="length">処理するバイト数</param>
    /// <param name="gain">適用するゲイン倍率</param>
    /// <returns>ゲイン適用後のピーク値</returns>
    internal static short ApplyGain(byte[] buffer, int length, float gain)
    {
        int peak = 0;

        for (int i = 0; i + 1 < length; i += 2)
        {
            short sample = BitConverter.ToInt16(buffer, i);
            int amplified = (int)(sample * gain);

            // クリッピング保護
            amplified = Math.Clamp(amplified, short.MinValue, short.MaxValue);

            buffer[i] = (byte)(amplified & 0xFF);
            buffer[i + 1] = (byte)((amplified >> 8) & 0xFF);

            int abs = Math.Abs(amplified);
            if (abs > peak) peak = abs;
        }

        return (short)Math.Min(peak, short.MaxValue);
    }

    /// <summary>
    /// ゲイン係数をリセットする (デバイス切替時などに使用)。
    /// </summary>
    public void Reset()
    {
        _currentGain = 1.0f;
    }
}
