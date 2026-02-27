using TalkTranscript.Models;
using Whisper.net;

namespace TalkTranscript.Transcribers;

/// <summary>
/// 通話終了後に、メモリ上の録音 PCM データを Whisper で高精度に文字起こしする。
/// リアルタイムではなくバッチ処理。CPU のみで動作する。
///
/// 入力: 16kHz / 16bit / mono の PCM バイト配列
/// Whisper.net は 16kHz / float32 / mono を期待するため変換する。
/// </summary>
public static class WhisperPostProcessor
{
    /// <summary>
    /// マイクとスピーカーの録音を Whisper で再認識し、高精度なエントリを生成する。
    /// Vosk のリアルタイム結果を置き換える。
    /// </summary>
    /// <param name="whisperModelPath">Whisper GGML モデルファイルのパス</param>
    /// <param name="micPcm">マイク PCM (16kHz/16bit/mono)</param>
    /// <param name="speakerPcm">スピーカー PCM (16kHz/16bit/mono)</param>
    /// <param name="callStartTime">通話開始時刻 (タイムスタンプ計算用)</param>
    /// <returns>Whisper による高精度な認識結果</returns>
    public static async Task<List<TranscriptEntry>> ProcessAsync(
        string whisperModelPath,
        byte[] micPcm,
        byte[] speakerPcm,
        DateTime callStartTime)
    {
        var entries = new List<TranscriptEntry>();

        Console.WriteLine("[Whisper] 後処理を開始します...");

        using var factory = WhisperFactory.FromPath(whisperModelPath);

        // マイク音声の処理
        if (micPcm.Length > 0)
        {
            Console.WriteLine($"[Whisper] マイク音声を処理中 ({micPcm.Length / 1024}KB)...");
            var micEntries = await ProcessSingleAsync(factory, micPcm, "自分", callStartTime);
            entries.AddRange(micEntries);
            Console.WriteLine($"[Whisper] マイク: {micEntries.Count} セグメント認識");
        }

        // スピーカー音声の処理
        if (speakerPcm.Length > 0)
        {
            Console.WriteLine($"[Whisper] スピーカー音声を処理中 ({speakerPcm.Length / 1024}KB)...");
            var spkEntries = await ProcessSingleAsync(factory, speakerPcm, "相手", callStartTime);
            entries.AddRange(spkEntries);
            Console.WriteLine($"[Whisper] スピーカー: {spkEntries.Count} セグメント認識");
        }

        // タイムスタンプ順にソート
        entries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Whisper] 後処理完了: 合計 {entries.Count} セグメント");
        Console.ResetColor();

        return entries;
    }

    private static async Task<List<TranscriptEntry>> ProcessSingleAsync(
        WhisperFactory factory,
        byte[] pcm16bit,
        string speaker,
        DateTime callStartTime)
    {
        var entries = new List<TranscriptEntry>();

        // 16bit PCM → float32 に変換
        float[] samples = ConvertPcm16ToFloat(pcm16bit);

        using var processor = factory.CreateBuilder()
            .WithLanguage("ja")
            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
            .Build();

        using var stream = new MemoryStream();
        // WAV ヘッダーを書き込んでから float サンプルを書き込む
        WriteWavFloat(stream, samples, 16000);
        stream.Position = 0;

        await foreach (var segment in processor.ProcessAsync(stream))
        {
            string text = segment.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Whisper のタイムスタンプから実際の時刻を算出
            var timestamp = callStartTime + segment.Start;
            var duration = segment.End - segment.Start;

            entries.Add(new TranscriptEntry(
                Timestamp: timestamp,
                Speaker: speaker,
                Text: text,
                Duration: duration));

            Console.WriteLine($"[Whisper] [{segment.Start:mm\\:ss} → {segment.End:mm\\:ss}] {speaker}: {text}");
        }

        return entries;
    }

    /// <summary>16bit PCM バイト配列を float32 配列に変換する (-1.0 ～ 1.0)</summary>
    private static float[] ConvertPcm16ToFloat(byte[] pcm)
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

    /// <summary>float32 サンプルを WAV ファイル形式 (PCM float) でストリームに書き込む</summary>
    private static void WriteWavFloat(Stream stream, float[] samples, int sampleRate)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        int dataSize = samples.Length * 4; // float32 = 4 bytes
        int channels = 1;
        int bitsPerSample = 32;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);

        // fmt chunk (IEEE float)
        writer.Write("fmt "u8);
        writer.Write(16);                       // chunk size
        writer.Write((short)3);                 // audio format: IEEE float
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        // data chunk
        writer.Write("data"u8);
        writer.Write(dataSize);

        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
    }
}
