using System.Text;
using TalkTranscript.Models;

namespace TalkTranscript.Output;

/// <summary>
/// Markdown 形式で文字起こし結果を出力する。
/// ドキュメント共有や wiki に最適。
/// </summary>
public static class MarkdownWriter
{
    /// <summary>
    /// エントリ一覧を Markdown 形式でファイルに書き出す。
    /// </summary>
    public static void Write(string filePath, IReadOnlyList<TranscriptEntry> entries,
                             DateTime sessionStart, TimeSpan duration,
                             string engine, string? language)
    {
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);

        // ── ヘッダー ──
        writer.WriteLine($"# 通話記録");
        writer.WriteLine();
        writer.WriteLine($"| 項目 | 値 |");
        writer.WriteLine($"|------|------|");
        writer.WriteLine($"| 日時 | {sessionStart:yyyy/MM/dd HH:mm} |");
        writer.WriteLine($"| 通話時間 | {duration:hh\\:mm\\:ss} |");
        writer.WriteLine($"| エンジン | {engine} |");
        writer.WriteLine($"| 言語 | {language ?? "ja"} |");
        writer.WriteLine($"| 合計発言数 | {entries.Count(e => !e.IsBookmark)} |");

        int selfCount = entries.Count(e => e.Speaker == "自分" && !e.IsBookmark);
        int otherCount = entries.Count(e => e.Speaker == "相手" && !e.IsBookmark);
        writer.WriteLine($"| 自分 | {selfCount} 件 |");
        writer.WriteLine($"| 相手 | {otherCount} 件 |");
        writer.WriteLine();

        // ── ブックマーク一覧 ──
        var bookmarks = entries.Where(e => e.IsBookmark).ToList();
        if (bookmarks.Count > 0)
        {
            writer.WriteLine("## ブックマーク");
            writer.WriteLine();
            foreach (var bm in bookmarks)
            {
                writer.WriteLine($"- **{bm.Timestamp:HH:mm:ss}** {bm.Text}");
            }
            writer.WriteLine();
        }

        // ── 会話内容 ──
        writer.WriteLine("## 会話");
        writer.WriteLine();

        string? lastSpeaker = null;

        foreach (var entry in entries)
        {
            if (entry.IsBookmark)
            {
                writer.WriteLine($"> 📌 **ブックマーク** ({entry.Timestamp:HH:mm:ss}) {entry.Text}");
                writer.WriteLine();
                continue;
            }

            // 話者が変わったら空行
            if (lastSpeaker != null && lastSpeaker != entry.Speaker)
                writer.WriteLine();

            string icon = entry.Speaker == "自分" ? "🎤" : "🔊";
            writer.WriteLine($"**{icon} {entry.Speaker}** `{entry.Timestamp:HH:mm:ss}`  ");
            writer.WriteLine($"{entry.Text}");

            lastSpeaker = entry.Speaker;
        }

        writer.WriteLine();
        writer.WriteLine("---");
        writer.WriteLine($"*生成: TalkTranscript ({engine})*");
    }

    /// <summary>Markdown ファイルの拡張子</summary>
    public static string Extension => ".md";
}
