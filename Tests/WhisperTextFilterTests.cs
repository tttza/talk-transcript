using TalkTranscript.Transcribers;

namespace TalkTranscript.Tests;

public class WhisperTextFilterTests
{
    // ── IsHallucination ──

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void IsHallucination_EmptyOrWhitespace_ReturnsTrue(string? text)
    {
        Assert.True(WhisperTextFilter.IsHallucination(text!));
    }

    [Theory]
    [InlineData("あ")]     // 1文字
    [InlineData("A")]
    public void IsHallucination_SingleChar_ReturnsTrue(string text)
    {
        Assert.True(WhisperTextFilter.IsHallucination(text));
    }

    [Theory]
    [InlineData("ご視聴ありがとうございました")]
    [InlineData("ご視聴ありがとうございます")]
    [InlineData("チャンネル登録お願いします")]
    [InlineData("字幕作成: ○○")]
    [InlineData("お疲れ様でした")]
    [InlineData("ありがとうございました")]
    [InlineData("(音楽)")]
    [InlineData("[拍手]")]
    [InlineData("♪♪♪")]
    [InlineData("Thank you for watching")]
    [InlineData("Subscribe")]
    public void IsHallucination_KnownPatterns_ReturnsTrue(string text)
    {
        Assert.True(WhisperTextFilter.IsHallucination(text));
    }

    [Theory]
    [InlineData("。。。")]
    [InlineData("、、")]
    [InlineData("！？")]
    [InlineData("...")]
    public void IsHallucination_PunctuationOnly_ReturnsTrue(string text)
    {
        Assert.True(WhisperTextFilter.IsHallucination(text));
    }

    [Theory]
    [InlineData("今日の会議について話しましょう")]
    [InlineData("はい、分かりました")]
    [InlineData("Hello, how are you?")]
    [InlineData("報告書を送ってください")]
    public void IsHallucination_NormalText_ReturnsFalse(string text)
    {
        Assert.False(WhisperTextFilter.IsHallucination(text));
    }

    // ── IsDuplicate ──

    [Fact]
    public void IsDuplicate_ExactMatch_ReturnsTrue()
    {
        Assert.True(WhisperTextFilter.IsDuplicate("こんにちは", "こんにちは"));
    }

    [Fact]
    public void IsDuplicate_EmptyPrevious_ReturnsFalse()
    {
        Assert.False(WhisperTextFilter.IsDuplicate("こんにちは", ""));
    }

    [Fact]
    public void IsDuplicate_CompletelyDifferent_ReturnsFalse()
    {
        Assert.False(WhisperTextFilter.IsDuplicate("今日は天気がいい", "クラウド基盤の設計"));
    }

    [Fact]
    public void IsDuplicate_ContainedText_ReturnsTrue()
    {
        Assert.True(WhisperTextFilter.IsDuplicate("今日の会議について", "今日の会議について話しましょう"));
    }

    // ── NormalizeText ──

    [Fact]
    public void NormalizeText_TrimsWhitespace()
    {
        Assert.Equal("テスト", WhisperTextFilter.NormalizeText("  テスト  "));
    }

    [Fact]
    public void NormalizeText_CollapsesSpaces()
    {
        Assert.Equal("今日は天気がいい", WhisperTextFilter.NormalizeText("今日は　 天気が  いい"));
    }

    [Fact]
    public void NormalizeText_TrimsLeadingPunctuation()
    {
        Assert.Equal("テスト", WhisperTextFilter.NormalizeText("、。テスト"));
    }

    [Fact]
    public void NormalizeText_EmptyReturnsEmpty()
    {
        Assert.Equal("", WhisperTextFilter.NormalizeText(""));
        Assert.Equal("", WhisperTextFilter.NormalizeText("  "));
    }

    [Fact]
    public void NormalizeText_RemovesSpacesBetweenJapaneseChars()
    {
        // ひらがな間
        Assert.Equal("こんにちは", WhisperTextFilter.NormalizeText("こん にち は"));
        // カタカナ間
        Assert.Equal("テスト", WhisperTextFilter.NormalizeText("テ ス ト"));
        // 漢字間
        Assert.Equal("東京都", WhisperTextFilter.NormalizeText("東京 都"));
        // 混合
        Assert.Equal("今日はテストです", WhisperTextFilter.NormalizeText("今日 は テスト です"));
    }

    [Fact]
    public void NormalizeText_PreservesSpacesAroundEnglish()
    {
        // 英語の単語間スペースは保持
        Assert.Equal("hello world", WhisperTextFilter.NormalizeText("hello world"));
        // 日本語と英語の間のスペースも保持
        Assert.Equal("これは test です", WhisperTextFilter.NormalizeText("これは test です"));
    }
}
