namespace TalkTranscript.Output;

/// <summary>
/// 文字起こし結果の出力フォーマット。
/// </summary>
public enum OutputFormat
{
    /// <summary>プレーンテキスト (従来のデフォルト)</summary>
    Text,

    /// <summary>SRT 字幕形式 (動画編集ソフトで使用可能)</summary>
    Srt,

    /// <summary>JSON 形式 (プログラムからの再利用・分析)</summary>
    Json,

    /// <summary>Markdown 形式 (ドキュメント共有)</summary>
    Markdown
}
