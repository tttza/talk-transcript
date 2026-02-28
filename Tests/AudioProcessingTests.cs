using TalkTranscript.Audio;

namespace TalkTranscript.Tests;

public class AudioProcessingTests
{
    // ── CalcPeak ──

    [Fact]
    public void CalcPeak_AllZero_ReturnsZero()
    {
        var buffer = new byte[100];
        Assert.Equal(0, AudioProcessing.CalcPeak(buffer, buffer.Length));
    }

    [Fact]
    public void CalcPeak_KnownValues_ReturnsMax()
    {
        // 16bit PCM: short values at specific offsets
        var buffer = new byte[8];
        BitConverter.GetBytes((short)100).CopyTo(buffer, 0);
        BitConverter.GetBytes((short)-500).CopyTo(buffer, 2);
        BitConverter.GetBytes((short)300).CopyTo(buffer, 4);
        BitConverter.GetBytes((short)50).CopyTo(buffer, 6);

        Assert.Equal(500, AudioProcessing.CalcPeak(buffer, buffer.Length));
    }

    [Fact]
    public void CalcPeak_NegativeValues_ReturnsAbsoluteMax()
    {
        var buffer = new byte[4];
        BitConverter.GetBytes((short)-1000).CopyTo(buffer, 0);
        BitConverter.GetBytes((short)200).CopyTo(buffer, 2);

        Assert.Equal(1000, AudioProcessing.CalcPeak(buffer, buffer.Length));
    }

    // ── CalcRms ──

    [Fact]
    public void CalcRms_AllZero_ReturnsZero()
    {
        var buffer = new byte[100];
        Assert.Equal(0f, AudioProcessing.CalcRms(buffer, buffer.Length));
    }

    [Fact]
    public void CalcRms_KnownValues_ReturnsCorrectRms()
    {
        // Two samples: 300, 400 → RMS = sqrt((90000 + 160000) / 2) = sqrt(125000) ≈ 353.55
        var buffer = new byte[4];
        BitConverter.GetBytes((short)300).CopyTo(buffer, 0);
        BitConverter.GetBytes((short)400).CopyTo(buffer, 2);

        float rms = AudioProcessing.CalcRms(buffer, buffer.Length);
        Assert.InRange(rms, 353f, 354f);
    }

    // ── ConvertPcm16ToFloat ──

    [Fact]
    public void ConvertPcm16ToFloat_EmptyInput_ReturnsEmpty()
    {
        var result = AudioProcessing.ConvertPcm16ToFloat(Array.Empty<byte>());
        Assert.Empty(result);
    }

    [Fact]
    public void ConvertPcm16ToFloat_ZeroSamples_ReturnsZeros()
    {
        var pcm = new byte[4]; // 2 zero samples
        var result = AudioProcessing.ConvertPcm16ToFloat(pcm);

        Assert.Equal(2, result.Length);
        Assert.Equal(0f, result[0]);
        Assert.Equal(0f, result[1]);
    }

    [Fact]
    public void ConvertPcm16ToFloat_MaxPositive_ReturnsNearOne()
    {
        var pcm = new byte[2];
        BitConverter.GetBytes((short)32767).CopyTo(pcm, 0);

        var result = AudioProcessing.ConvertPcm16ToFloat(pcm);
        Assert.Single(result);
        Assert.InRange(result[0], 0.99f, 1.0f);
    }

    [Fact]
    public void ConvertPcm16ToFloat_MaxNegative_ReturnsNearNegOne()
    {
        var pcm = new byte[2];
        BitConverter.GetBytes((short)-32768).CopyTo(pcm, 0);

        var result = AudioProcessing.ConvertPcm16ToFloat(pcm);
        Assert.Single(result);
        Assert.Equal(-1.0f, result[0]);
    }

    // ── WriteWavPcm16 / WriteWavFloat ──

    [Fact]
    public void WriteWavPcm16_ProducesValidWavHeader()
    {
        var pcm = new byte[100];
        using var ms = new MemoryStream();
        AudioProcessing.WriteWavPcm16(ms, pcm, 16000);

        ms.Position = 0;
        var data = ms.ToArray();

        // RIFF header check
        Assert.Equal((byte)'R', data[0]);
        Assert.Equal((byte)'I', data[1]);
        Assert.Equal((byte)'F', data[2]);
        Assert.Equal((byte)'F', data[3]);

        // WAVE marker
        Assert.Equal((byte)'W', data[8]);
        Assert.Equal((byte)'A', data[9]);
        Assert.Equal((byte)'V', data[10]);
        Assert.Equal((byte)'E', data[11]);

        // fmt chunk: PCM = 1
        short format = BitConverter.ToInt16(data, 20);
        Assert.Equal(1, format);

        // sample rate
        int sampleRate = BitConverter.ToInt32(data, 24);
        Assert.Equal(16000, sampleRate);

        // data size
        int dataSize = BitConverter.ToInt32(data, 40);
        Assert.Equal(100, dataSize);
    }

    [Fact]
    public void WriteWavFloat_ProducesValidWavHeader()
    {
        var samples = new float[] { 0.5f, -0.5f, 0.0f };
        using var ms = new MemoryStream();
        AudioProcessing.WriteWavFloat(ms, samples, 16000);

        ms.Position = 0;
        var data = ms.ToArray();

        // RIFF
        Assert.Equal((byte)'R', data[0]);

        // fmt chunk: IEEE float = 3
        short format = BitConverter.ToInt16(data, 20);
        Assert.Equal(3, format);

        // bits per sample = 32
        short bps = BitConverter.ToInt16(data, 34);
        Assert.Equal(32, bps);

        // data size = 3 samples × 4 bytes
        int dataSize = BitConverter.ToInt32(data, 40);
        Assert.Equal(12, dataSize);
    }

    // ── ConvertLoopbackToTarget ──

    [Fact]
    public void ConvertLoopbackToTarget_EmptyInput_ReturnsEmpty()
    {
        var sourceFormat = new NAudio.Wave.WaveFormat(48000, 32, 2); // IEEE float will fail with this
        // Use WaveFormat.CreateIeeeFloatWaveFormat for proper test
        var source = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var target = new NAudio.Wave.WaveFormat(16000, 16, 1);
        var result = AudioProcessing.ConvertLoopbackToTarget(Array.Empty<byte>(), 0, source, target);
        Assert.Empty(result);
    }
}
