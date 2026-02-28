using TalkTranscript.Models;

namespace TalkTranscript.Tests;

public class TranscriptWriterTests : IDisposable
{
    private readonly string _tempDir;

    public TranscriptWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TalkTranscriptTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Constructor_CreatesFileWithHeader()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        var writer = new TranscriptWriter(path);
        writer.Close();
        writer.Dispose();

        Assert.True(File.Exists(path));
        var content = File.ReadAllText(path);
        Assert.Contains("通話記録", content);
    }

    [Fact]
    public void Append_WritesEntry()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        var writer = new TranscriptWriter(path);

        writer.Append(new TranscriptEntry(
            Timestamp: new DateTime(2026, 1, 1, 10, 30, 0),
            Speaker: "自分",
            Text: "テスト発言"));

        writer.Close();
        writer.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("10:30:00", content);
        Assert.Contains("自分", content);
        Assert.Contains("テスト発言", content);
    }

    [Fact]
    public void Append_MultipleEntries_WritesAll()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        var writer = new TranscriptWriter(path);

        writer.Append(new TranscriptEntry(DateTime.Now, "自分", "発言1"));
        writer.Append(new TranscriptEntry(DateTime.Now, "相手", "発言2"));
        writer.Append(new TranscriptEntry(DateTime.Now, "自分", "発言3"));

        writer.Close();
        writer.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("発言1", content);
        Assert.Contains("発言2", content);
        Assert.Contains("発言3", content);
    }

    [Fact]
    public void Append_Bookmark_WritesBookmarkMarker()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        var writer = new TranscriptWriter(path);

        writer.Append(new TranscriptEntry(
            Timestamp: DateTime.Now,
            Speaker: "📌",
            Text: "重要ポイント",
            IsBookmark: true));

        writer.Close();
        writer.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("★", content);
        Assert.Contains("ブックマーク", content);
    }

    [Fact]
    public void Close_WritesFooterWithStats()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        var writer = new TranscriptWriter(path);

        writer.Append(new TranscriptEntry(DateTime.Now, "自分", "A"));
        writer.Append(new TranscriptEntry(DateTime.Now, "自分", "B"));
        writer.Append(new TranscriptEntry(DateTime.Now, "相手", "C"));

        writer.Close();
        writer.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("自分: 2 件", content);
        Assert.Contains("相手: 1 件", content);
        Assert.Contains("合計発言数: 3", content);
    }

    [Fact]
    public void Close_WithBookmarks_ShowsBookmarkCount()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        var writer = new TranscriptWriter(path);

        writer.Append(new TranscriptEntry(DateTime.Now, "📌", "BM", IsBookmark: true));
        writer.Append(new TranscriptEntry(DateTime.Now, "📌", "BM2", IsBookmark: true));

        writer.Close();
        writer.Dispose();

        var content = File.ReadAllText(path);
        Assert.Contains("ブックマーク: 2 件", content);
    }

    [Fact]
    public void TotalCount_ReturnsCorrectSum()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        using var writer = new TranscriptWriter(path);

        writer.Append(new TranscriptEntry(DateTime.Now, "自分", "A"));
        writer.Append(new TranscriptEntry(DateTime.Now, "相手", "B"));

        Assert.Equal(2, writer.TotalCount);
    }

    [Fact]
    public void FilePath_ReturnsConstructorPath()
    {
        var path = Path.Combine(_tempDir, "output.txt");
        using var writer = new TranscriptWriter(path);
        Assert.Equal(path, writer.FilePath);
        writer.Close();
    }
}
