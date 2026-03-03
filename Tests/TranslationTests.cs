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
    [InlineData("en", "ja", true)]
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

    // ── マージ翻訳テスト ──

    [Fact]
    public void TranslationWorker_MergeWindow_MergesConsecutiveSameSpeaker()
    {
        // マージ翻訳: 同一話者の連続フラグメントを結合して翻訳
        var translator = new ConcatMockTranslator(); // 入力テキストをそのまま返す
        using var worker = new TranslationWorker(translator, "両方", mergeWindowMs: 500);

        var results = new System.Collections.Concurrent.ConcurrentBag<TranscriptEntry>();
        var signal = new CountdownEvent(3);

        worker.OnTranslated += entry =>
        {
            results.Add(entry);
            signal.Signal();
        };

        var ts = new DateTime(2026, 3, 1, 18, 9, 9);
        // 同一話者の3フラグメントを素早く投入
        worker.Enqueue(new TranscriptEntry(ts, "相手", "Hello world."));
        worker.Enqueue(new TranscriptEntry(ts, "相手", "How are you?"));
        worker.Enqueue(new TranscriptEntry(ts, "相手", "Nice day."));

        bool signaled = signal.Wait(TimeSpan.FromSeconds(10));
        Assert.True(signaled, "マージ翻訳コールバックがタイムアウトしました");
        Assert.Equal(3, results.Count);

        // 結合翻訳が使われたことを確認: translator が受け取ったテキストが結合されている
        Assert.True(translator.LastInput?.Contains("Hello world.") == true);
        Assert.True(translator.LastInput?.Contains("How are you?") == true);
        Assert.True(translator.LastInput?.Contains("Nice day.") == true);
    }

    [Fact]
    public void TranslationWorker_MergeWindow_FlushesOnSpeakerChange()
    {
        var translator = new ConcatMockTranslator();
        using var worker = new TranslationWorker(translator, "両方", mergeWindowMs: 500);

        var results = new System.Collections.Concurrent.ConcurrentBag<TranscriptEntry>();
        var signal = new CountdownEvent(2);

        worker.OnTranslated += entry =>
        {
            results.Add(entry);
            signal.Signal();
        };

        var ts = new DateTime(2026, 3, 1, 18, 9, 9);
        worker.Enqueue(new TranscriptEntry(ts, "相手", "Hello."));
        // 話者変更 → 前のバッファがフラッシュされる
        worker.Enqueue(new TranscriptEntry(ts, "自分", "Hi."));

        bool signaled = signal.Wait(TimeSpan.FromSeconds(10));
        Assert.True(signaled, "話者変更コールバックがタイムアウトしました");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void TranslationWorker_MergeWindow_Zero_DisablesMerge()
    {
        // mergeWindowMs=0 → 従来動作 (即時翻訳)
        using var translator = new MockTranslator("訳");
        using var worker = new TranslationWorker(translator, "両方", mergeWindowMs: 0);

        TranscriptEntry? result = null;
        var signal = new ManualResetEventSlim(false);

        worker.OnTranslated += entry =>
        {
            result = entry;
            signal.Set();
        };

        worker.Enqueue(new TranscriptEntry(DateTime.Now, "相手", "Hello"));

        bool signaled = signal.Wait(TimeSpan.FromSeconds(5));
        Assert.True(signaled, "即時翻訳コールバックがタイムアウトしました");
        Assert.Equal("訳", result?.TranslatedText);
    }

    // ── SplitTranslation テスト ──

    [Fact]
    public void SplitTranslation_SingleEntry_ReturnsWholeText()
    {
        var entries = new List<TranscriptEntry>
        {
            new(DateTime.Now, "相手", "Hello world")
        };
        var result = TranslationWorker.SplitTranslation("こんにちは世界", entries);
        Assert.Single(result);
        Assert.Equal("こんにちは世界", result[0]);
    }

    [Fact]
    public void SplitTranslation_MultipleSentences_DistributesProportionally()
    {
        var entries = new List<TranscriptEntry>
        {
            new(DateTime.Now, "相手", "Hello."),         // 短い
            new(DateTime.Now, "相手", "How are you doing today? Fine thanks.") // 長い
        };
        var result = TranslationWorker.SplitTranslation(
            "こんにちは。今日の調子はどう？元気だよ。", entries);

        Assert.Equal(2, result.Count);
        // 両方が空でないこと
        Assert.False(string.IsNullOrWhiteSpace(result[0]));
        Assert.False(string.IsNullOrWhiteSpace(result[1]));
    }

    [Fact]
    public void SplitIntoSentences_SplitsOnPunctuation()
    {
        var sentences = TranslationWorker.SplitIntoSentences("こんにちは。元気ですか？はい！");
        Assert.Equal(3, sentences.Count);
        Assert.Equal("こんにちは。", sentences[0]);
        Assert.Equal("元気ですか？", sentences[1]);
        Assert.Equal("はい！", sentences[2]);
    }

    [Fact]
    public void SplitIntoSentences_HandlesNoPunctuation()
    {
        var sentences = TranslationWorker.SplitIntoSentences("こんにちは世界");
        Assert.Single(sentences);
        Assert.Equal("こんにちは世界", sentences[0]);
    }

    // ── LooksLikeSourceLanguage テスト ──

    [Theory]
    [InlineData("Hello world", "en", true)]           // 英語テキスト → en ソースOK
    [InlineData("こんにちは世界", "en", false)]        // 日本語テキスト → en ソースNG
    [InlineData("次の動画でお会いしましょう", "en", false)] // 日本語テキスト → en ソースNG
    [InlineData("こんにちは世界", "ja", true)]         // 日本語テキスト → ja ソースOK
    [InlineData("Hello world", "ja", false)]          // 英語テキスト → ja ソースNG
    [InlineData("テスト123", "ja", true)]              // 日本語主体 → ja ソースOK
    [InlineData("한국어 텍스트", "ko", true)]           // 韓国語テキスト → ko ソースOK
    [InlineData("한국어 텍스트", "en", false)]          // 韓国語テキスト → en ソースNG
    [InlineData("中文测试", "zh", true)]               // 中国語テキスト → zh ソースOK
    [InlineData("Bonjour le monde", "fr", true)]      // フランス語テキスト → fr ソースOK
    [InlineData("", "en", false)]                     // 空文字列 → false
    [InlineData("   ", "ja", false)]                  // 空白のみ → false
    public void LooksLikeSourceLanguage_DetectsCorrectly(string text, string sourceLang, bool expected)
    {
        Assert.Equal(expected, TranslationWorker.LooksLikeSourceLanguage(text, sourceLang));
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

    /// <summary>入力テキストをそのまま返すモック翻訳エンジン (結合テスト用)</summary>
    private class ConcatMockTranslator : ITranslator
    {
        public bool IsReady => true;
        public string SourceLanguage => "en";
        public string TargetLanguage => "ja";
        public string? LastInput { get; private set; }

        public string? Translate(string text)
        {
            LastInput = text;
            return text; // 入力をそのまま返す
        }
        public void Dispose() { }
    }
}
