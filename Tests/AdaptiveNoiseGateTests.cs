using TalkTranscript.Audio;

namespace TalkTranscript.Tests;

public class AdaptiveNoiseGateTests
{
    [Fact]
    public void Constructor_SetsDefaults()
    {
        var gate = new AdaptiveNoiseGate();
        Assert.True(gate.CurrentThreshold > 0);
        Assert.True(gate.NoiseFloor > 0);
    }

    [Fact]
    public void Constructor_CustomParameters_Applied()
    {
        var gate = new AdaptiveNoiseGate(
            initialThreshold: 500f,
            historySize: 100,
            multiplier: 4.0f,
            minThreshold: 200f,
            maxThreshold: 5000f);

        Assert.Equal(500f, gate.CurrentThreshold);
    }

    [Fact]
    public void Update_LowRms_LowersThreshold()
    {
        var gate = new AdaptiveNoiseGate(initialThreshold: 1000f, historySize: 10);

        // Supply many low RMS values to bring threshold down
        for (int i = 0; i < 15; i++)
        {
            gate.Update(50f);
        }

        // Threshold should be lower than initial (but clamped to minThreshold)
        Assert.True(gate.CurrentThreshold <= 1000f);
    }

    [Fact]
    public void Update_HighRms_RaisesThreshold()
    {
        var gate = new AdaptiveNoiseGate(initialThreshold: 100f, historySize: 10, minThreshold: 50f);

        // Supply high RMS values
        for (int i = 0; i < 15; i++)
        {
            gate.Update(500f);
        }

        Assert.True(gate.CurrentThreshold > 100f);
    }

    [Fact]
    public void IsVoice_PeakAboveThreshold_ReturnsTrue()
    {
        var gate = new AdaptiveNoiseGate(initialThreshold: 200f);
        Assert.True(gate.IsVoice(300, 100f));
    }

    [Fact]
    public void IsVoice_PeakBelowThreshold_ReturnsFalse()
    {
        var gate = new AdaptiveNoiseGate(initialThreshold: 500f);
        Assert.False(gate.IsVoice(100, 50f));
    }

    [Fact]
    public void IsVoice_HighRms_ReturnsTrue()
    {
        // Even if peak is below threshold, high RMS should trigger
        var gate = new AdaptiveNoiseGate(initialThreshold: 200f);
        // RMS threshold is CurrentThreshold * 0.7 = 140
        Assert.True(gate.IsVoice(100, 200f));
    }

    [Fact]
    public void Update_InsufficientSamples_DoesNotChangeThreshold()
    {
        var gate = new AdaptiveNoiseGate(initialThreshold: 500f);
        float initial = gate.CurrentThreshold;

        // Only 3 updates (less than 5 minimum)
        gate.Update(100f);
        gate.Update(100f);
        gate.Update(100f);

        Assert.Equal(initial, gate.CurrentThreshold);
    }

    [Fact]
    public void NoiseFloor_TracksLowerQuartile()
    {
        var gate = new AdaptiveNoiseGate(historySize: 20);

        // Feed mixed values: low noise floor samples + occasional peaks
        for (int i = 0; i < 20; i++)
        {
            float rms = (i % 5 == 0) ? 500f : 50f; // mostly quiet with occasional spikes
            gate.Update(rms);
        }

        // Noise floor should be based on the quiet parts
        Assert.True(gate.NoiseFloor < 200f);
    }

    [Fact]
    public void Threshold_ClampedToMinMax()
    {
        var gate = new AdaptiveNoiseGate(minThreshold: 100f, maxThreshold: 2000f, historySize: 10);

        // Very low noise → threshold should be clamped to minThreshold
        for (int i = 0; i < 15; i++) gate.Update(1f);
        Assert.True(gate.CurrentThreshold >= 100f);

        // Very high noise → threshold should be clamped to maxThreshold
        var gate2 = new AdaptiveNoiseGate(minThreshold: 100f, maxThreshold: 2000f, historySize: 10);
        for (int i = 0; i < 15; i++) gate2.Update(10000f);
        Assert.True(gate2.CurrentThreshold <= 2000f);
    }

    [Fact]
    public void CorrectCallingPattern_OnlyNoiseUpdatesHistory()
    {
        // 正しい呼び出しパターンをシミュレート:
        // IsVoice=false (ノイズ) のときだけ Update を呼ぶ
        var gate = new AdaptiveNoiseGate(initialThreshold: 250f, historySize: 10);

        // ── 起動直後: ユーザーが話し続けている ──
        for (int i = 0; i < 15; i++)
        {
            bool isVoice = gate.IsVoice(400, 600f); // peak=400 >= threshold=250 → true
            Assert.True(isVoice, "音声は通過するはず");
            // 音声なので Update は呼ばない
        }

        // 閾値は初期値 250 のまま (ノイズをまだ供給していないため)
        Assert.Equal(250f, gate.CurrentThreshold);

        // ── ユーザーが話し止む: ノイズのチャンクが来る ──
        for (int i = 0; i < 10; i++)
        {
            bool isVoice = gate.IsVoice(30, 40f); // peak=30 < 250 → false
            Assert.False(isVoice);
            gate.Update(40f); // ノイズなので Update する
        }

        // 閾値がノイズレベルに適応: ノイズフロア ≈ 40, 閾値 ≈ 120 (clamped to min 100)
        Assert.True(gate.CurrentThreshold < 250f,
            $"閾値がノイズに適応すべき: {gate.CurrentThreshold}");

        // ── ユーザーが再び話し始める ──
        Assert.True(gate.IsVoice(400, 600f), "閾値が下がったので音声は通過するはず");
    }

    [Fact]
    public void CorrectCallingPattern_ContinuousSpeech_ThresholdStable()
    {
        // ユーザーが起動直後からずっと話し続けるシナリオ
        // Update が一度も呼ばれないため、閾値は初期値 250 のまま安定
        var gate = new AdaptiveNoiseGate(initialThreshold: 250f, historySize: 10);

        // 50 チャンク連続で音声
        for (int i = 0; i < 50; i++)
        {
            bool isVoice = gate.IsVoice(400, 600f);
            Assert.True(isVoice, $"チャンク {i}: 音声は通過するはず");
            // 音声なので Update は呼ばない
        }

        // 閾値が跳ね上がっていないこと
        Assert.Equal(250f, gate.CurrentThreshold);
    }
}
