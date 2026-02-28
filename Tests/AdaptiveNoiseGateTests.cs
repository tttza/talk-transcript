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
}
