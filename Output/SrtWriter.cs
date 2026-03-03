using System.Text;
using TalkTranscript.Models;

namespace TalkTranscript.Output;

/// <summary>
/// SRT (SubRip) 字幕形式で文字起こし結果を出力する。
/// 動画編集ソフトや字幕表示ツールに直接読み込み可能。
/// </summary>
public static class SrtWriter
{
    /// <summary>
    /// エントリ一覧を SRT 形式でファイルに書き出す。
    /// </summary>
    public static void Write(string filePath, IReadOnlyList<TranscriptEntry> entries, DateTime sessionStart)
    {
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

        int seq = 1;
        foreach (var entry in entries)
        {
            if (entry.IsBookmark) continue; // ブックマークは SRT に含めない

            var start = entry.Timestamp - sessionStart;
            var end = start + (entry.Duration ?? TimeSpan.FromSeconds(3));

            // 負のタイムスタンプを補正
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;

            writer.WriteLine(seq++);
            writer.WriteLine($"{FormatSrtTime(start)} --> {FormatSrtTime(end)}");
            writer.WriteLine($"[{entry.Speaker}] {entry.Text}");
            if (!string.IsNullOrEmpty(entry.TranslatedText))
                writer.WriteLine(entry.TranslatedText);
            writer.WriteLine();
        }
    }

    /// <summary>SRT タイムスタンプ形式 (HH:MM:SS,mmm)</summary>
    private static string FormatSrtTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }

    /// <summary>SRT ファイルの拡張子</summary>
    public static string Extension => ".srt";
}
