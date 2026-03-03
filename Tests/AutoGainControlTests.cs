using TalkTranscript.Audio;

namespace TalkTranscript.Tests;

public class AutoGainControlTests
{
    // ── 基本動作 ──

    [Fact]
    public void Process_Disabled_DoesNotModifyBuffer()
    {
        var agc = new AutoGainControl { Enabled = false };

        var buffer = MakePcm16(new short[] { 100, -200, 300 });
        var original = buffer.ToArray();

        agc.Process(buffer, buffer.Length);

        Assert.Equal(original, buffer);
    }

    [Fact]
    public void Process_SilentBuffer_DoesNotModify()
    {
        var agc = new AutoGainControl();

        // Very quiet signal (RMS < 10)
        var buffer = MakePcm16(new short[] { 1, -1, 0, 2 });
        var original = buffer.ToArray();

        agc.Process(buffer, buffer.Length);

        Assert.Equal(original, buffer);
    }

    [Fact]
    public void Process_QuietSignal_IncreasesAmplitude()
    {
        // Target RMS = 3000, quiet signal ≈ 100
        var agc = new AutoGainControl(targetRms: 3000f, maxGain: 10f, attackRate: 1.0f);

        // Create a quiet signal
        var samples = new short[100];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(100 * Math.Sin(2 * Math.PI * i / 20));

        var buffer = MakePcm16(samples);
        short peakBefore = AudioProcessing.CalcPeak(buffer, buffer.Length);

        // Process multiple times to let AGC adapt
        for (int pass = 0; pass < 10; pass++)
        {
            buffer = MakePcm16(samples);
            agc.Process(buffer, buffer.Length);
        }

        short peakAfter = AudioProcessing.CalcPeak(buffer, buffer.Length);

        // After AGC, peak should be higher
        Assert.True(peakAfter > peakBefore,
            $"Expected amplified peak > original peak, but {peakAfter} <= {peakBefore}");
    }

    [Fact]
    public void Process_LoudSignal_DoesNotExceedClipping()
    {
        var agc = new AutoGainControl(targetRms: 3000f, maxGain: 10f, attackRate: 1.0f);

        // Signal near maximum amplitude
        var samples = new short[] { 30000, -30000, 25000, -25000 };
        var buffer = MakePcm16(samples);

        agc.Process(buffer, buffer.Length);

        short peak = AudioProcessing.CalcPeak(buffer, buffer.Length);
        Assert.True(peak <= short.MaxValue);
    }

    // ── ApplyGain ──

    [Fact]
    public void ApplyGain_DoublesAmplitude()
    {
        var buffer = MakePcm16(new short[] { 1000, -2000, 500 });

        AutoGainControl.ApplyGain(buffer, buffer.Length, 2.0f);

        Assert.Equal(2000, BitConverter.ToInt16(buffer, 0));
        Assert.Equal(-4000, BitConverter.ToInt16(buffer, 2));
        Assert.Equal(1000, BitConverter.ToInt16(buffer, 4));
    }

    [Fact]
    public void ApplyGain_ClipsAtMaxShort()
    {
        var buffer = MakePcm16(new short[] { 20000, -20000 });

        AutoGainControl.ApplyGain(buffer, buffer.Length, 3.0f);

        // 20000 * 3 = 60000 → clamped to 32767
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(buffer, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(buffer, 2));
    }

    [Fact]
    public void ApplyGain_ReturnsPeakValue()
    {
        var buffer = MakePcm16(new short[] { 100, -300, 200 });

        short peak = AutoGainControl.ApplyGain(buffer, buffer.Length, 5.0f);

        // 300 * 5 = 1500
        Assert.Equal(1500, peak);
    }

    [Fact]
    public void ApplyGain_Unity_NoChange()
    {
        var original = new short[] { 1234, -5678, 0 };
        var buffer = MakePcm16(original);
        var expected = buffer.ToArray();

        AutoGainControl.ApplyGain(buffer, buffer.Length, 1.0f);

        Assert.Equal(expected, buffer);
    }

    // ── CurrentGain / Reset ──

    [Fact]
    public void CurrentGain_InitiallyOne()
    {
        var agc = new AutoGainControl();
        Assert.Equal(1.0f, agc.CurrentGain);
    }

    [Fact]
    public void Reset_RestoresGainToOne()
    {
        var agc = new AutoGainControl(attackRate: 1.0f);

        // Process a quiet signal to change gain
        var samples = new short[50];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(50 * Math.Sin(2 * Math.PI * i / 10));
        var buffer = MakePcm16(samples);
        agc.Process(buffer, buffer.Length);

        // Gain should have changed
        Assert.NotEqual(1.0f, agc.CurrentGain);

        agc.Reset();
        Assert.Equal(1.0f, agc.CurrentGain);
    }

    // ── Smoothing ──

    [Fact]
    public void Process_GradualGainIncrease_WithLowAttackRate()
    {
        var agc = new AutoGainControl(targetRms: 3000f, maxGain: 10f, attackRate: 0.05f);

        var samples = new short[100];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(100 * Math.Sin(2 * Math.PI * i / 20));

        // Single pass should only partially adjust gain
        var buffer = MakePcm16(samples);
        agc.Process(buffer, buffer.Length);

        float gainAfterOnePass = agc.CurrentGain;

        // Gain should be > 1 but not at maximum yet (smoothing)
        Assert.True(gainAfterOnePass > 1.0f);
        Assert.True(gainAfterOnePass < 10.0f);
    }

    [Fact]
    public void Process_MaxGainRespected()
    {
        var agc = new AutoGainControl(targetRms: 30000f, maxGain: 5f, attackRate: 1.0f);

        // Very quiet signal
        var samples = new short[100];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(10 * Math.Sin(2 * Math.PI * i / 20));

        var buffer = MakePcm16(samples);
        agc.Process(buffer, buffer.Length);

        Assert.True(agc.CurrentGain <= 5.0f,
            $"Expected gain <= 5.0, but got {agc.CurrentGain}");
    }

    // ── Helper ──

    private static byte[] MakePcm16(short[] samples)
    {
        var buffer = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.GetBytes(samples[i]).CopyTo(buffer, i * 2);
        return buffer;
    }
}
