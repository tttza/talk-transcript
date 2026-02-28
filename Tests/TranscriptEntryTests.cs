using TalkTranscript.Models;

namespace TalkTranscript.Tests;

public class TranscriptEntryTests
{
    [Fact]
    public void DefaultValues_SpeakerId_IsNull()
    {
        var entry = new TranscriptEntry(DateTime.Now, "自分", "テスト");
        Assert.Null(entry.SpeakerId);
    }

    [Fact]
    public void DefaultValues_IsBookmark_IsFalse()
    {
        var entry = new TranscriptEntry(DateTime.Now, "自分", "テスト");
        Assert.False(entry.IsBookmark);
    }

    [Fact]
    public void DefaultValues_Duration_IsNull()
    {
        var entry = new TranscriptEntry(DateTime.Now, "自分", "テスト");
        Assert.Null(entry.Duration);
    }

    [Fact]
    public void WithSpeakerId_SetCorrectly()
    {
        var entry = new TranscriptEntry(DateTime.Now, "自分", "テスト", SpeakerId: 1);
        Assert.Equal(1, entry.SpeakerId);
    }

    [Fact]
    public void WithIsBookmark_SetCorrectly()
    {
        var entry = new TranscriptEntry(DateTime.Now, "📌", "メモ", IsBookmark: true);
        Assert.True(entry.IsBookmark);
    }

    [Fact]
    public void WithDuration_SetCorrectly()
    {
        var dur = TimeSpan.FromSeconds(5.5);
        var entry = new TranscriptEntry(DateTime.Now, "相手", "テスト", Duration: dur);
        Assert.Equal(dur, entry.Duration);
    }

    [Fact]
    public void Record_Equality_Works()
    {
        var ts = new DateTime(2026, 1, 1, 12, 0, 0);
        var a = new TranscriptEntry(ts, "自分", "同じ");
        var b = new TranscriptEntry(ts, "自分", "同じ");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Record_Inequality_Works()
    {
        var ts = new DateTime(2026, 1, 1, 12, 0, 0);
        var a = new TranscriptEntry(ts, "自分", "A");
        var b = new TranscriptEntry(ts, "自分", "B");
        Assert.NotEqual(a, b);
    }
}
