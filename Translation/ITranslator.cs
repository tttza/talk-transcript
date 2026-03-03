namespace TalkTranscript.Translation;

/// <summary>
/// テキスト翻訳エンジンのインターフェース。
/// </summary>
public interface ITranslator : IDisposable
{
    /// <summary>テキストを翻訳する。翻訳不要または失敗時は null を返す。</summary>
    string? Translate(string text);

    /// <summary>モデルのロードが完了しているか</summary>
    bool IsReady { get; }

    /// <summary>翻訳元言語コード</summary>
    string SourceLanguage { get; }

    /// <summary>翻訳先言語コード</summary>
    string TargetLanguage { get; }
}
