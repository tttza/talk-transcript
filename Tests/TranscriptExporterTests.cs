using TalkTranscript.Models;
using TalkTranscript.Output;

namespace TalkTranscript.Tests;

public class TranscriptExporterTests : IDisposable
{
    private readonly string _tempDir;

    public TranscriptExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TalkTranscriptTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private List<TranscriptEntry> CreateSampleEntries()
    {
        var start = new DateTime(2026, 3, 1, 10, 0, 0);
        return new List<TranscriptEntry>
        {
            new(start, "自分", "こんにちは"),
            new(start.AddSeconds(5), "相手", "はい、こんにちは"),
            new(start.AddSeconds(10), "自分", "本日の議題は..."),
        };
    }

    [Fact]
    public void Export_Srt_CreatesFile()
    {
        var basePath = Path.Combine(_tempDir, "transcript.txt");
        var entries = CreateSampleEntries();
        var start = new DateTime(2026, 3, 1, 10, 0, 0);

        var files = TranscriptExporter.Export(basePath, entries,
            new[] { OutputFormat.Srt }, start, TimeSpan.FromMinutes(5), "whisper-base", "ja");

        Assert.Single(files);
        Assert.EndsWith(".srt", files[0]);
        Assert.True(File.Exists(files[0]));

        var content = File.ReadAllText(files[0]);
        Assert.Contains("こんにちは", content);
        Assert.Contains("-->", content);
    }

    [Fact]
    public void Export_Json_CreatesValidJson()
    {
        var basePath = Path.Combine(_tempDir, "transcript.txt");
        var entries = CreateSampleEntries();
        var start = new DateTime(2026, 3, 1, 10, 0, 0);

        var files = TranscriptExporter.Export(basePath, entries,
            new[] { OutputFormat.Json }, start, TimeSpan.FromMinutes(5), "whisper-base", "ja");

        Assert.Single(files);
        Assert.EndsWith(".json", files[0]);

        var content = File.ReadAllText(files[0]);
        Assert.Contains("\"entries\"", content);
        Assert.Contains("\"engine\"", content);
        Assert.Contains("こんにちは", content);
    }

    [Fact]
    public void Export_Markdown_CreatesFile()
    {
        var basePath = Path.Combine(_tempDir, "transcript.txt");
        var entries = CreateSampleEntries();
        var start = new DateTime(2026, 3, 1, 10, 0, 0);

        var files = TranscriptExporter.Export(basePath, entries,
            new[] { OutputFormat.Markdown }, start, TimeSpan.FromMinutes(5), "whisper-base", "ja");

        Assert.Single(files);
        Assert.EndsWith(".md", files[0]);

        var content = File.ReadAllText(files[0]);
        Assert.Contains("# 通話記録", content);
        Assert.Contains("こんにちは", content);
    }

    [Fact]
    public void Export_Text_SkipsExport()
    {
        var basePath = Path.Combine(_tempDir, "transcript.txt");
        var entries = CreateSampleEntries();
        var start = new DateTime(2026, 3, 1, 10, 0, 0);

        var files = TranscriptExporter.Export(basePath, entries,
            new[] { OutputFormat.Text }, start, TimeSpan.FromMinutes(5), "sapi", "ja");

        Assert.Empty(files);
    }

    [Fact]
    public void Export_MultipleFormats_CreatesAllFiles()
    {
        var basePath = Path.Combine(_tempDir, "transcript.txt");
        var entries = CreateSampleEntries();
        var start = new DateTime(2026, 3, 1, 10, 0, 0);

        var files = TranscriptExporter.Export(basePath, entries,
            new[] { OutputFormat.Srt, OutputFormat.Json, OutputFormat.Markdown },
            start, TimeSpan.FromMinutes(5), "whisper-base", "ja");

        Assert.Equal(3, files.Count);
    }

    [Theory]
    [InlineData(OutputFormat.Srt, ".srt")]
    [InlineData(OutputFormat.Json, ".json")]
    [InlineData(OutputFormat.Markdown, ".md")]
    [InlineData(OutputFormat.Text, ".txt")]
    public void GetExtension_ReturnsCorrect(OutputFormat format, string expected)
    {
        Assert.Equal(expected, TranscriptExporter.GetExtension(format));
    }

    [Fact]
    public void Export_WithBookmark_IncludedInOutput()
    {
        var basePath = Path.Combine(_tempDir, "transcript.txt");
        var start = new DateTime(2026, 3, 1, 10, 0, 0);
        var entries = new List<TranscriptEntry>
        {
            new(start, "自分", "テスト発言"),
            new(start.AddSeconds(5), "📌", "注意", IsBookmark: true),
            new(start.AddSeconds(10), "相手", "了解"),
        };

        var files = TranscriptExporter.Export(basePath, entries,
            new[] { OutputFormat.Json }, start, TimeSpan.FromMinutes(1), "sapi", "ja");

        var content = File.ReadAllText(files[0]);
        Assert.Contains("isBookmark", content);
    }
}
