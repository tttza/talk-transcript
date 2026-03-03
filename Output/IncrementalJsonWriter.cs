using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TalkTranscript.Models;

namespace TalkTranscript.Output;

/// <summary>
/// JSON 形式で文字起こし結果を逐次書き出す。
///
/// 各エントリを即座にディスクに書き出すため、
/// クラッシュ時も最後に書き込まれたエントリまでデータを保持する。
///
/// <b>中間ファイル形式</b>:
/// 各行に 1 つの JSON オブジェクトを改行区切りで書き出す (NDJSON 互換)。
/// これにより追記のみで高速に書き込める。
///
/// <b>Close() 時</b>:
/// <see cref="JsonTranscriptWriter.Write"/> を使用して
/// メタデータを含む完全な JSON ファイルに仕上げる。
/// </summary>
public sealed class IncrementalJsonWriter : IDisposable
{
    private static readonly JsonSerializerOptions EntryJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private int _entryCount;
    private bool _closed;
    private bool _disposed;
    private readonly Timer _flushTimer;

    /// <summary>出力先ファイルパス</summary>
    public string FilePath => _filePath;

    public IncrementalJsonWriter(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: false, Encoding.UTF8)
        {
            AutoFlush = false  // 定期 Flush で I/O オーバーヘッドを削減
        };
        // 5秒ごとにディスクにフラッシュ (クラッシュ耐性と性能のバランス)
        _flushTimer = new Timer(_ => PeriodicFlush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void PeriodicFlush()
    {
        lock (_lock)
        {
            if (_closed || _disposed) return;
            try { _writer.Flush(); } catch { }
        }
    }

    /// <summary>
    /// 認識結果を 1 件追記する。スレッドセーフ。
    /// NDJSON 形式 (1 行 1 JSON オブジェクト) で書き出す。
    /// </summary>
    public void Append(TranscriptEntry entry)
    {
        if (_closed || _disposed) return;

        lock (_lock)
        {
            if (_closed || _disposed) return;

            var obj = new IncrementalEntry
            {
                Timestamp = entry.Timestamp,
                Speaker = entry.Speaker,
                Text = entry.Text,
                DurationMs = entry.Duration.HasValue ? (int)entry.Duration.Value.TotalMilliseconds : null,
                SpeakerId = entry.SpeakerId,
                IsBookmark = entry.IsBookmark ? true : null,
                TranslatedText = entry.TranslatedText
            };

            string json = JsonSerializer.Serialize(obj, EntryJsonOptions);
            _writer.WriteLine(json);
            _entryCount++;
        }
    }

    /// <summary>
    /// 中間ファイルを閉じ、メタデータを含む完全な JSON ファイルに仕上げる。
    /// </summary>
    public void Close(IReadOnlyList<TranscriptEntry> allEntries,
                      DateTime sessionStart, TimeSpan duration,
                      string engine, string? language)
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            _flushTimer.Dispose();
            _writer.Flush();
            _writer.Dispose();

            // 完全な JSON 形式で一時ファイルに書き出し、成功後にアトミックに置換
            string tempPath = _filePath + ".tmp";
            try
            {
                JsonTranscriptWriter.Write(tempPath, allEntries, sessionStart, duration, engine, language);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch
            {
                // 完全な JSON への変換に失敗した場合、NDJSON 中間ファイルをそのまま残す
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer.Dispose();
            try
            {
                if (!_closed)
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch { }
        }
    }

    // ── 内部モデル (NDJSON 行用) ──

    private class IncrementalEntry
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

        [JsonPropertyName("translatedText")]
        public string? TranslatedText { get; set; }
    }
}
