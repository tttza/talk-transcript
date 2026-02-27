using System.Text;
using TalkTranscript.Models;

namespace TalkTranscript;

/// <summary>
/// 文字起こし結果をリアルタイムにファイルへ追記する。
/// 通話開始時に Open → 認識の都度 Append → 通話終了時に Close してフッターを書く。
/// </summary>
public sealed class TranscriptWriter : IDisposable
{
    private readonly string _filePath;
    private readonly StreamWriter _writer;
    private string? _lastSpeaker;
    private int _selfCount;
    private int _otherCount;
    private DateTime? _firstTimestamp;
    private DateTime? _lastTimestamp;
    private bool _disposed;

    /// <summary>出力先ファイルパス</summary>
    public string FilePath => _filePath;

    /// <summary>合計発言数</summary>
    public int TotalCount => _selfCount + _otherCount;

    public TranscriptWriter(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: false, Encoding.UTF8)
        {
            AutoFlush = true  // 各行を即座にディスクへ
        };

        // ── ヘッダー ──
        _writer.WriteLine($"通話記録 - {DateTime.Now:yyyy/MM/dd HH:mm}");
        _writer.WriteLine(new string('=', 60));
        _writer.WriteLine();
    }

    /// <summary>
    /// 認識結果を 1 件追記する。スレッドセーフ。
    /// </summary>
    public void Append(TranscriptEntry entry)
    {
        lock (_writer)
        {
            _firstTimestamp ??= entry.Timestamp;
            _lastTimestamp = entry.Timestamp;

            // 話者が変わったら空行を挿入して見やすくする
            if (_lastSpeaker != null && _lastSpeaker != entry.Speaker)
            {
                _writer.WriteLine();
            }

            _writer.WriteLine($"[{entry.Timestamp:HH:mm:ss}] {entry.Speaker}: {entry.Text}");
            _lastSpeaker = entry.Speaker;

            if (entry.Speaker == "自分") _selfCount++;
            else _otherCount++;
        }
    }

    /// <summary>
    /// フッターを書き込んでファイルを閉じる。
    /// </summary>
    public void Close()
    {
        if (_disposed) return;

        lock (_writer)
        {
            _writer.WriteLine();
            _writer.WriteLine(new string('=', 60));

            var duration = (_lastTimestamp ?? DateTime.Now) - (_firstTimestamp ?? DateTime.Now);

            _writer.WriteLine($"通話時間 (概算): {duration:hh\\:mm\\:ss}");
            _writer.WriteLine($"合計発言数: {_selfCount + _otherCount}");
            _writer.WriteLine($"  自分: {_selfCount} 件");
            _writer.WriteLine($"  相手: {_otherCount} 件");

            _writer.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
    }
}
