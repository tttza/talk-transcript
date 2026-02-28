using System.Text.Json;
using System.Text.Json.Serialization;
using TalkTranscript.Models;

namespace TalkTranscript.Output;

/// <summary>
/// JSON 形式で文字起こし結果を出力する。
/// プログラムからの再利用・分析に適したフォーマット。
/// </summary>
public static class JsonTranscriptWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// エントリ一覧を JSON 形式でファイルに書き出す。
    /// </summary>
    public static void Write(string filePath, IReadOnlyList<TranscriptEntry> entries,
                             DateTime sessionStart, TimeSpan duration,
                             string engine, string? language)
    {
        var doc = new JsonTranscriptDocument
        {
            Metadata = new JsonMetadata
            {
                SessionStart = sessionStart,
                Duration = duration.ToString(@"hh\:mm\:ss"),
                Engine = engine,
                Language = language ?? "ja",
                TotalEntries = entries.Count,
                SelfCount = entries.Count(e => e.Speaker == "自分" && !e.IsBookmark),
                OtherCount = entries.Count(e => e.Speaker == "相手" && !e.IsBookmark),
                BookmarkCount = entries.Count(e => e.IsBookmark)
            },
            Entries = entries.Select(e => new JsonEntry
            {
                Timestamp = e.Timestamp,
                Speaker = e.Speaker,
                Text = e.Text,
                DurationMs = e.Duration.HasValue ? (int)e.Duration.Value.TotalMilliseconds : null,
                SpeakerId = e.SpeakerId,
                IsBookmark = e.IsBookmark ? true : null
            }).ToList()
        };

        var json = JsonSerializer.Serialize(doc, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>JSON ファイルの拡張子</summary>
    public static string Extension => ".json";

    // ── 内部モデル ──

    private class JsonTranscriptDocument
    {
        [JsonPropertyName("metadata")]
        public JsonMetadata Metadata { get; set; } = new();

        [JsonPropertyName("entries")]
        public List<JsonEntry> Entries { get; set; } = new();
    }

    private class JsonMetadata
    {
        [JsonPropertyName("sessionStart")]
        public DateTime SessionStart { get; set; }

        [JsonPropertyName("duration")]
        public string Duration { get; set; } = "";

        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "";

        [JsonPropertyName("language")]
        public string Language { get; set; } = "ja";

        [JsonPropertyName("totalEntries")]
        public int TotalEntries { get; set; }

        [JsonPropertyName("selfCount")]
        public int SelfCount { get; set; }

        [JsonPropertyName("otherCount")]
        public int OtherCount { get; set; }

        [JsonPropertyName("bookmarkCount")]
        public int? BookmarkCount { get; set; }
    }

    private class JsonEntry
    {
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonPropertyName("speaker")]
        public string Speaker { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("durationMs")]
        public int? DurationMs { get; set; }

        [JsonPropertyName("speakerId")]
        public int? SpeakerId { get; set; }

        [JsonPropertyName("isBookmark")]
        public bool? IsBookmark { get; set; }
    }
}
