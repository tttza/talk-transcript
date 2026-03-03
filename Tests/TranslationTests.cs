using TalkTranscript.Models;
using TalkTranscript.Transcribers;
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
    public void FragmentMerger_MergesConsecutiveSameSpeaker()
    {
        // 逐次マージ: 同一話者の未完フラグメントを結合
        // OnMerged はフラグメント追加のたびに発火される (逐次)
        using var merger = new FragmentMerger(mergeWindowMs: 500);

        var mergeHistory = new System.Collections.Concurrent.ConcurrentBag<(IReadOnlyList<TranscriptEntry> originals, TranscriptEntry merged)>();
        var flushedResults = new System.Collections.Concurrent.ConcurrentBag<TranscriptEntry>();
        var flushSignal = new CountdownEvent(1);

        merger.OnMerged += (originals, merged) =>
        {
            mergeHistory.Add((originals, merged));
        };
        merger.OnFlushed += entry =>
        {
            flushedResults.Add(entry);
            flushSignal.Signal();
        };

        var ts = new DateTime(2026, 3, 1, 18, 9, 9);
        // 同一話者の未完フラグメントを素早く投入 (最後が文末記号 → フラッシュ)
        merger.Enqueue(new TranscriptEntry(ts, "相手", "and you'll be able to see exactly"));
        merger.Enqueue(new TranscriptEntry(ts, "相手", "how it all transcribes and"));
        merger.Enqueue(new TranscriptEntry(ts, "相手", "works in real time."));

        bool signaled = flushSignal.Wait(TimeSpan.FromSeconds(15));
        Assert.True(signaled, "フラッシュコールバックがタイムアウトしました");

        // OnMerged が複数回発火されている (逐次マージ)
        Assert.True(mergeHistory.Count >= 1, "OnMerged が発火されていません");

        // フラッシュされた結合テキストが全フラグメントを含む
        var flushed = flushedResults.First();
        Assert.Contains("and you'll be able to see exactly", flushed.Text);
        Assert.Contains("how it all transcribes and", flushed.Text);
        Assert.Contains("works in real time.", flushed.Text);
    }

    [Fact]
    public void FragmentMerger_FlushesOnSpeakerChange()
    {
        using var merger = new FragmentMerger(mergeWindowMs: 500);

        var flushedResults = new System.Collections.Concurrent.ConcurrentBag<TranscriptEntry>();
        var signal = new CountdownEvent(2);

        merger.OnFlushed += entry =>
        {
            flushedResults.Add(entry);
            signal.Signal();
        };

        var ts = new DateTime(2026, 3, 1, 18, 9, 9);
        merger.Enqueue(new TranscriptEntry(ts, "相手", "Hello."));
        // 話者変更 → 前のバッファがフラッシュされる
        merger.Enqueue(new TranscriptEntry(ts, "自分", "Hi."));

        bool signaled = signal.Wait(TimeSpan.FromSeconds(10));
        Assert.True(signaled, "話者変更コールバックがタイムアウトしました");
        Assert.Equal(2, flushedResults.Count);
    }

    [Fact]
    public void FragmentMerger_FlushesOnCompleteSentence()
    {
        // 文末記号で終わるフラグメントは、次のフラグメント到着時にフラッシュされる
        // → 完結した文が不要に結合されない
        using var merger = new FragmentMerger(mergeWindowMs: 5000);

        var flushedResults = new System.Collections.Concurrent.ConcurrentBag<TranscriptEntry>();
        var signal = new CountdownEvent(3);

        merger.OnFlushed += entry =>
        {
            flushedResults.Add(entry);
            signal.Signal();
        };

        var ts = new DateTime(2026, 3, 1, 19, 55, 27);
        // 3つの完結した文を素早く投入 → 各文が個別にフラッシュされる
        merger.Enqueue(new TranscriptEntry(ts, "相手", "No problem, I'll keep on in English."));
        merger.Enqueue(new TranscriptEntry(ts, "相手", "So, in our chat, you shared you were exploring how the transcript works."));
        merger.Enqueue(new TranscriptEntry(ts, "相手", "Is there anything else you're curious about?"));

        bool signaled = signal.Wait(TimeSpan.FromSeconds(15));
        Assert.True(signaled, "完結文フラッシュコールバックがタイムアウトしました");
        Assert.Equal(3, flushedResults.Count);

        // 各文が個別にフラッシュされている (結合されていない)
        var texts = flushedResults.Select(e => e.Text).OrderBy(t => t.Length).ToList();
        Assert.Contains(texts, t => t.Contains("No problem"));
        Assert.Contains(texts, t => t.Contains("transcript works"));
        Assert.Contains(texts, t => t.Contains("curious about"));
    }

    [Fact]
    public void TranslationWorker_ImmediateTranslation()
    {
        // TranslationWorker は常に即時翻訳 (マージなし)
        using var translator = new MockTranslator("訳");
        using var worker = new TranslationWorker(translator, "両方");

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

    // ── EndsWithSentenceBoundary テスト ──

    [Theory]
    [InlineData("Hello world.", true)]           // ピリオドで終わる
    [InlineData("How are you?", true)]            // クエスチョンマーク
    [InlineData("Great!", true)]                  // エクスクラメーション
    [InlineData("こんにちは。", true)]              // 日本語句点
    [InlineData("元気ですか？", true)]              // 日本語疑問符
    [InlineData("すごい！", true)]                  // 日本語感嘆符
    [InlineData("Hello world. ", true)]            // 末尾に空白
    [InlineData("and you'll be able to see exactly", false)]  // 文の途中 (英語)
    [InlineData("We'll keep it nice and clear in English,", false)]  // カンマで終わる
    [InlineData("I'm looking for", false)]        // 文の途中 (英語)
    [InlineData("それは私の", false)]               // 文の途中 (日本語)
    [InlineData("今日は天気が良くて", false)]        // 文の途中 (日本語・て形)
    [InlineData("次の動画で", false)]               // 文の途中 (日本語・助詞)
    [InlineData("「こんにちは」", true)]             // 閉じ鉤括弧
    [InlineData("ありがとうございます）", true)]     // 閉じ括弧
    [InlineData("素晴らしい…", true)]               // 三点リーダー
    [InlineData("", true)]                        // 空文字列
    [InlineData("   ", true)]                     // 空白のみ
    public void EndsWithSentenceBoundary_DetectsCorrectly(string text, bool expected)
    {
        Assert.Equal(expected, FragmentMerger.EndsWithSentenceBoundary(text));
    }

    // ── HasInternalSentenceBoundary テスト ──

    [Theory]
    [InlineData("Sure. I can talk about anything", true)]        // ピリオド+後続テキスト
    [InlineData("Let's see. Imagine a peaceful morning", true)]  // 内部ピリオド
    [InlineData("元気ですか。はい", true)]                         // 日本語句点+後続
    [InlineData("本当？マジで", true)]                              // 日本語疑問符+後続
    [InlineData("and you'll be able to see exactly", false)]     // 句読点なし
    [InlineData("Hello world.", false)]                          // 末尾のみ (後続なし)
    [InlineData("Nice day", false)]                              // 句読点なし
    [InlineData("", false)]                                      // 空文字列
    [InlineData("Hello.  ", false)]                              // ピリオド後は空白のみ
    public void HasInternalSentenceBoundary_DetectsCorrectly(string text, bool expected)
    {
        Assert.Equal(expected, FragmentMerger.HasInternalSentenceBoundary(text));
    }

    // ── IsBufferTextComplete テスト ──

    [Theory]
    [InlineData("Hello world.", true)]                                              // 末尾ピリオド
    [InlineData("Sure. I can talk about anything", false)]                           // 内部ピリオドのみ (末尾未完)
    [InlineData("and you'll be able to see exactly", false)]                        // 未完
    [InlineData("Sure. I can talk about anything. Let's see. Imagine a peaceful morning", false)] // 内部ピリオドのみ (末尾未完)
    [InlineData("こんにちは。元気ですか", false)]                                      // 日本語内部句点のみ (末尾未完)
    [InlineData("今日は天気が良くて", false)]                                         // 日本語未完
    public void IsBufferTextComplete_DetectsCorrectly(string text, bool expected)
    {
        Assert.Equal(expected, FragmentMerger.IsBufferTextComplete(text));
    }

    [Fact]
    public void FragmentMerger_IncompleteSentence_WaitsLonger()
    {
        // 文が未完のフラグメントを送信し、次のフラグメントが通常のマージウィンドウより
        // 大幅に遅れて到着しても結合されることを確認 (Whisper の処理遅延をシミュレート)
        using var merger = new FragmentMerger(mergeWindowMs: 300);

        var results = new System.Collections.Concurrent.ConcurrentBag<TranscriptEntry>();
        var signal = new CountdownEvent(1);

        merger.OnMerged += (originals, merged) =>
        {
            results.Add(merged);
            signal.Signal();
        };

        var ts = new DateTime(2026, 3, 1, 18, 9, 9);
        // 文が未完のフラグメントを先に送信
        merger.Enqueue(new TranscriptEntry(ts, "相手", "I'll speak at a"));
        // 通常マージウィンドウ (300ms) よりはるかに遅い 3 秒後に到着
        // → 最低保証 20 秒があるため結合される
        Thread.Sleep(3000);
        merger.Enqueue(new TranscriptEntry(ts, "相手", "pace that's easier to follow."));

        bool signaled = signal.Wait(TimeSpan.FromSeconds(15));
        Assert.True(signaled, "未完文マージコールバックがタイムアウトしました");

        // 結合エントリのテキストが結合されている
        var merged = results.First();
        Assert.Contains("I'll speak at a", merged.Text);
        Assert.Contains("pace that's easier to follow.", merged.Text);
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
