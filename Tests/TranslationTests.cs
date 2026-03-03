using TalkTranscript.Models;
using TalkTranscript.Translation;

namespace TalkTranscript.Tests;

public class TranslationTests
{
    // ── TranscriptEntry.TranslatedText テスト ──

    [Fact]
    public void TranscriptEntry_TranslatedText_DefaultIsNull()
    {
        var entry = new TranscriptEntry(DateTime.Now, "自分", "テスト");
        Assert.Null(entry.TranslatedText);
    }

    [Fact]
    public void TranscriptEntry_WithTranslatedText_SetsCorrectly()
    {
        var entry = new TranscriptEntry(DateTime.Now, "相手", "Hello");
        var translated = entry with { TranslatedText = "こんにちは" };

        Assert.Equal("Hello", translated.Text);
        Assert.Equal("こんにちは", translated.TranslatedText);
    }

    [Fact]
    public void TranscriptEntry_WithTranslatedText_PreservesOtherFields()
    {
        var ts = new DateTime(2026, 3, 1, 10, 0, 0);
        var dur = TimeSpan.FromSeconds(3);
        var entry = new TranscriptEntry(ts, "相手", "Hello", Duration: dur, SpeakerId: 2);
        var translated = entry with { TranslatedText = "こんにちは" };

        Assert.Equal(ts, translated.Timestamp);
        Assert.Equal("相手", translated.Speaker);
        Assert.Equal("Hello", translated.Text);
        Assert.Equal(dur, translated.Duration);
        Assert.Equal(2, translated.SpeakerId);
        Assert.False(translated.IsBookmark);
        Assert.Equal("こんにちは", translated.TranslatedText);
    }

    [Fact]
    public void TranscriptEntry_Equality_IncludesTranslatedText()
    {
        var ts = new DateTime(2026, 3, 1, 10, 0, 0);
        var a = new TranscriptEntry(ts, "相手", "Hello", TranslatedText: "こんにちは");
        var b = new TranscriptEntry(ts, "相手", "Hello", TranslatedText: "こんにちは");
        var c = new TranscriptEntry(ts, "相手", "Hello", TranslatedText: "やあ");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ── LanguagePairs テスト ──

    [Theory]
    [InlineData("ja", "en", true)]
    [InlineData("en", "zh", true)]
    [InlineData("en", "ja", false)]
    [InlineData("en", "ko", false)]
    [InlineData("xx", "yy", false)]
    public void LanguagePairs_IsSupported(string src, string tgt, bool expected)
    {
        Assert.Equal(expected, LanguagePairs.IsSupported(src, tgt));
    }

    [Fact]
    public void LanguagePairs_GetInfo_ReturnsInfoForSupported()
    {
        var info = LanguagePairs.GetInfo("ja", "en");
        Assert.NotNull(info);
        Assert.Contains("opus-mt", info.HuggingFaceRepo);
        Assert.Contains("onnx-community", info.HuggingFaceRepo);
    }

    [Fact]
    public void LanguagePairs_GetInfo_ReturnsNullForUnsupported()
    {
        Assert.Null(LanguagePairs.GetInfo("xx", "yy"));
    }

    [Fact]
    public void LanguagePairs_GetModelBaseUrl_ReturnsUrlForSupported()
    {
        var url = LanguagePairs.GetModelBaseUrl("ja", "en");
        Assert.NotNull(url);
        Assert.Contains("huggingface.co", url);
    }

    [Fact]
    public void LanguagePairs_GetAllPairs_ReturnsNonEmpty()
    {
        var pairs = LanguagePairs.GetAllPairs();
        Assert.True(pairs.Count > 0);
    }

    [Fact]
    public void LanguagePairs_TargetLanguages_ContainsJapanese()
    {
        Assert.Contains(LanguagePairs.TargetLanguages, l => l.Code == "ja");
    }

    // ── TranslationWorker テスト (モック翻訳) ──

    [Fact]
    public void TranslationWorker_Enqueue_TranslatesAndCallsBack()
    {
        using var translator = new MockTranslator("テスト翻訳");
        using var worker = new TranslationWorker(translator, "両方");

        TranscriptEntry? result = null;
        var signal = new ManualResetEventSlim(false);

        worker.OnTranslated += entry =>
        {
            result = entry;
            signal.Set();
        };

        var entry = new TranscriptEntry(DateTime.Now, "相手", "Hello");
        worker.Enqueue(entry);

        bool signaled = signal.Wait(TimeSpan.FromSeconds(5));
        Assert.True(signaled, "翻訳コールバックがタイムアウトしました");
        Assert.NotNull(result);
        Assert.Equal("テスト翻訳", result.TranslatedText);
        Assert.Equal("Hello", result.Text);
    }

    [Fact]
    public void TranslationWorker_Enqueue_FiltersTarget_Self()
    {
        using var translator = new MockTranslator("訳");
        using var worker = new TranslationWorker(translator, "自分");

        TranscriptEntry? result = null;
        var signal = new ManualResetEventSlim(false);

        worker.OnTranslated += entry =>
        {
            result = entry;
            signal.Set();
        };

        // 相手のエントリは翻訳されない
        worker.Enqueue(new TranscriptEntry(DateTime.Now, "相手", "Hello"));
        bool signaled1 = signal.Wait(TimeSpan.FromSeconds(1));
        Assert.False(signaled1);

        // 自分のエントリは翻訳される
        worker.Enqueue(new TranscriptEntry(DateTime.Now, "自分", "Hi"));
        bool signaled2 = signal.Wait(TimeSpan.FromSeconds(5));
        Assert.True(signaled2);
        Assert.Equal("訳", result?.TranslatedText);
    }

    [Fact]
    public void TranslationWorker_SkipsBookmarks()
    {
        using var translator = new MockTranslator("訳");
        using var worker = new TranslationWorker(translator, "両方");

        TranscriptEntry? result = null;
        var signal = new ManualResetEventSlim(false);

        worker.OnTranslated += entry =>
        {
            result = entry;
            signal.Set();
        };

        // ブックマークは翻訳されない
        worker.Enqueue(new TranscriptEntry(DateTime.Now, "📌", "メモ", IsBookmark: true));
        bool signaled = signal.Wait(TimeSpan.FromSeconds(1));
        Assert.False(signaled);
    }

    [Fact]
    public void TranslationWorker_Stop_CompletesGracefully()
    {
        using var translator = new MockTranslator("訳");
        var worker = new TranslationWorker(translator, "両方");
        worker.Stop();
        worker.Dispose();
    }

    // ── ModelManager テスト ──

    [Fact]
    public void ModelManager_GetTranslationModelPath_ReturnsNullWhenNoModel()
    {
        // テスト環境にモデルはないはず
        var path = ModelManager.GetTranslationModelPath("xx", "yy");
        Assert.Null(path);
    }

    /// <summary>テスト用のモック翻訳エンジン</summary>
    private class MockTranslator : ITranslator
    {
        private readonly string _translation;
        public bool IsReady => true;
        public string SourceLanguage => "en";
        public string TargetLanguage => "ja";

        public MockTranslator(string translation) => _translation = translation;
        public string? Translate(string text) => _translation;
        public void Dispose() { }
    }
}
