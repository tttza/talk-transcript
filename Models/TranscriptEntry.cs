namespace TalkTranscript.Models;

/// <summary>
/// 文字起こしの1発言を表すレコード。
/// </summary>
/// <param name="Timestamp">発言が認識された日時</param>
/// <param name="Speaker">話者 ("自分" or "相手")</param>
/// <param name="Text">認識されたテキスト</param>
/// <param name="Duration">発話の長さ (取得可能な場合)</param>
/// <param name="SpeakerId">話者識別 ID (ダイアライゼーション用。null で未識別)</param>
/// <param name="IsBookmark">ブックマークエントリかどうか</param>
public record TranscriptEntry(
    DateTime Timestamp,
    string Speaker,
    string Text,
    TimeSpan? Duration = null,
    int? SpeakerId = null,
    bool IsBookmark = false);
