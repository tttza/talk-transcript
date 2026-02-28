using TalkTranscript.Audio;
using TalkTranscript.Models;
using Whisper.net;

namespace TalkTranscript.Transcribers;

/// <summary>
/// 通話終了後に、メモリ上の録音 PCM データを Whisper で高精度に文字起こしする。
/// リアルタイムではなくバッチ処理。CPU のみで動作する。
///
/// 入力: 16kHz / 16bit / mono の PCM バイト配列
/// Whisper.net は 16kHz / float32 / mono を期待するため変換する。
///
/// 改善点:
///   - ハルシネーションフィルタリング
///   - 重複セグメント検出
///   - 短いセグメントの統合
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
        DateTime callStartTime,
        bool useGpu = true,
        string language = "ja",
        int maxCpuThreads = 0)
    {
        var entries = new List<TranscriptEntry>();

        Console.WriteLine("[Whisper] 後処理を開始します...");

        using var factory = WhisperFactory.FromPath(whisperModelPath, new WhisperFactoryOptions { UseGpu = useGpu });

        // マイク音声の処理
        if (micPcm.Length > 0)
        {
            Console.WriteLine($"[Whisper] マイク音声を処理中 ({micPcm.Length / 1024}KB)...");
            var micEntries = await ProcessSingleAsync(factory, micPcm, "自分", callStartTime, language, maxCpuThreads);
            entries.AddRange(micEntries);
            Console.WriteLine($"[Whisper] マイク: {micEntries.Count} セグメント認識");
        }

        // スピーカー音声の処理
        if (speakerPcm.Length > 0)
        {
            Console.WriteLine($"[Whisper] スピーカー音声を処理中 ({speakerPcm.Length / 1024}KB)...");
            var spkEntries = await ProcessSingleAsync(factory, speakerPcm, "相手", callStartTime, language, maxCpuThreads);
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
        DateTime callStartTime,
        string language = "ja",
        int maxCpuThreads = 0)
    {
        var entries = new List<TranscriptEntry>();

        // スレッド数: maxCpuThreads > 0 ならユーザー指定値、0 なら CPU コア数の半分
        int threads = maxCpuThreads > 0
            ? Math.Min(maxCpuThreads, Math.Max(1, Environment.ProcessorCount - 1))
            : Math.Max(1, Environment.ProcessorCount / 2);

        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(threads)
            .Build();

        using var stream = new MemoryStream();
        // Whisper.net は PCM16 WAV を期待する (IEEE float WAV は "Unsupported wave file" になる)
        AudioProcessing.WriteWavPcm16(stream, pcm16bit, 16000);
        stream.Position = 0;

        string lastText = "";

        await foreach (var segment in processor.ProcessAsync(stream))
        {
            string text = segment.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;

            // テキスト正規化
            text = WhisperTextFilter.NormalizeText(text);

            // ハルシネーションフィルター
            if (WhisperTextFilter.IsHallucination(text))
                continue;

            // 重複テキスト検出
            if (WhisperTextFilter.IsDuplicate(text, lastText))
                continue;

            // Whisper のタイムスタンプから実際の時刻を算出
            var timestamp = callStartTime + segment.Start;
            var duration = segment.End - segment.Start;

            entries.Add(new TranscriptEntry(
                Timestamp: timestamp,
                Speaker: speaker,
                Text: text,
                Duration: duration));

            Console.WriteLine($"[Whisper] [{segment.Start:mm\\:ss} → {segment.End:mm\\:ss}] {speaker}: {text}");
            lastText = text;
        }

        // 短いセグメントを統合 (同じ話者で 1秒未満のセグメントが連続する場合)
        entries = MergeShortSegments(entries, speaker);

        return entries;
    }

    /// <summary>
    /// 同じ話者の短いセグメントを統合する。
    /// 2秒以内の間隔で連続する短いセグメントを 1 つにまとめる。
    /// </summary>
    private static List<TranscriptEntry> MergeShortSegments(List<TranscriptEntry> entries, string speaker)
    {
        if (entries.Count <= 1) return entries;

        var merged = new List<TranscriptEntry>();
        var current = entries[0];

        for (int i = 1; i < entries.Count; i++)
        {
            var next = entries[i];
            var gap = next.Timestamp - current.Timestamp - (current.Duration ?? TimeSpan.Zero);
            bool isShort = (current.Duration ?? TimeSpan.FromSeconds(5)) < TimeSpan.FromSeconds(1.5);

            // 短いセグメントが 2秒以内の間隔で続く場合は結合
            if (isShort && gap < TimeSpan.FromSeconds(2))
            {
                var combinedDuration = (next.Timestamp + (next.Duration ?? TimeSpan.Zero)) - current.Timestamp;
                current = current with
                {
                    Text = current.Text + next.Text,
                    Duration = combinedDuration
                };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }
        merged.Add(current);

        return merged;
    }

}
