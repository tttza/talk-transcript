using System.Buffers;
using NAudio.Wave;

namespace TalkTranscript.Audio;

/// <summary>
/// オーディオ信号処理のユーティリティ。
/// リサンプリング、ピーク計算、RMS 計算など、
/// 複数のトランスクライバで共有する処理を集約する。
/// </summary>
internal static class AudioProcessing
{
    /// <summary>認識エンジンが期待するフォーマット: 16 kHz / 16 bit / mono</summary>
    public static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    /// <summary>
    /// WASAPI ループバック (IEEE Float 32bit/マルチチャンネル) を
    /// 16kHz / 16bit / mono に変換する。
    ///
    /// 単純デシメーション (間引き) ではなくリニア補間を使用し、
    /// エイリアシングアーティファクトを低減する。
    /// </summary>
    public static byte[] ConvertLoopbackToTarget(
        byte[] source, int length, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        int bytesPerSample = sourceFormat.BitsPerSample / 8;
        int channels = sourceFormat.Channels;
        int sampleCount = length / (bytesPerSample * channels);

        if (sampleCount == 0) return Array.Empty<byte>();

        // ソースのモノラル float サンプルを生成 (ArrayPool で再利用)
        float[] monoSamples = ArrayPool<float>.Shared.Rent(sampleCount);
        try
        {
            for (int i = 0; i < sampleCount; i++)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    int offset = (i * channels + ch) * bytesPerSample;
                    if (offset + 4 <= length)
                        sum += BitConverter.ToSingle(source, offset);
                }
                monoSamples[i] = Math.Clamp(sum / channels, -1.0f, 1.0f);
            }

            // リサンプリング比率
            double ratio = (double)sourceFormat.SampleRate / targetFormat.SampleRate;
            int outputSamples = (int)(sampleCount / ratio);
            if (outputSamples == 0) return Array.Empty<byte>();

            byte[] result = new byte[outputSamples * 2]; // 16bit = 2 bytes

            for (int i = 0; i < outputSamples; i++)
            {
                // リニア補間で滑らかにリサンプル
                double srcPos = i * ratio;
                int idx0 = (int)srcPos;
                int idx1 = Math.Min(idx0 + 1, sampleCount - 1);
                float frac = (float)(srcPos - idx0);

                float sample = monoSamples[idx0] * (1f - frac) + monoSamples[idx1] * frac;
                short pcm = (short)(Math.Clamp(sample, -1.0f, 1.0f) * 32767);

                result[i * 2] = (byte)(pcm & 0xFF);
                result[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
            }

            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(monoSamples);
        }
    }

    /// <summary>PCM 16bit バッファ内のピーク振幅を返す</summary>
    public static short CalcPeak(byte[] buffer, int length)
    {
        short max = 0;
        for (int j = 0; j + 1 < length; j += 2)
        {
            short s = BitConverter.ToInt16(buffer, j);
            short abs = Math.Abs(s);
            if (abs > max) max = abs;
        }
        return max;
    }

    /// <summary>PCM 16bit バッファの RMS (二乗平均平方根) を計算する</summary>
    public static float CalcRms(byte[] buffer, int length)
    {
        long sumSq = 0;
        int count = 0;
        for (int j = 0; j + 1 < length; j += 2)
        {
            short s = BitConverter.ToInt16(buffer, j);
            sumSq += (long)s * s;
            count++;
        }
        return count > 0 ? MathF.Sqrt((float)sumSq / count) : 0f;
    }

    /// <summary>PCM 16bit/mono データを WAV ファイルとしてストリームに書き込む</summary>
    public static void WriteWavPcm16(Stream stream, byte[] pcm16, int sampleRate)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        int channels = 1;
        int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataSize = pcm16.Length;

        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);    // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        w.Write(pcm16);
    }

    /// <summary>float32 サンプルを WAV ファイル形式 (IEEE float) でストリームに書き込む</summary>
    public static void WriteWavFloat(Stream stream, float[] samples, int sampleRate)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        int dataSize = samples.Length * 4;
        int channels = 1;
        int bitsPerSample = 32;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)3);   // IEEE float
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write("data"u8);
        writer.Write(dataSize);

        foreach (var sample in samples)
            writer.Write(sample);
    }

    /// <summary>16bit PCM バイト配列を float32 配列に変換する (-1.0 〜 1.0)</summary>
    public static float[] ConvertPcm16ToFloat(byte[] pcm)
    {
        int sampleCount = pcm.Length / 2;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short s = BitConverter.ToInt16(pcm, i * 2);
            samples[i] = s / 32768f;
        }

        return samples;
    }
}
