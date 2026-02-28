using TalkTranscript.Models;

namespace TalkTranscript.Output;

/// <summary>
/// 指定されたフォーマットで文字起こし結果をエクスポートするオーケストレータ。
/// 複数の出力形式に対応し、フォーマットに応じた Writer を呼び出す。
/// </summary>
public static class TranscriptExporter
{
    /// <summary>
    /// 既存のテキスト出力に加えて、指定フォーマットのファイルを追加出力する。
    /// </summary>
    /// <param name="baseFilePath">ベースとなるファイルパス (拡張子は自動変更)</param>
    /// <param name="entries">文字起こしエントリ一覧</param>
    /// <param name="formats">出力するフォーマット一覧</param>
    /// <param name="sessionStart">セッション開始時刻</param>
    /// <param name="duration">通話時間</param>
    /// <param name="engine">使用したエンジン名</param>
    /// <param name="language">言語</param>
    /// <returns>出力されたファイルパスの一覧</returns>
    public static List<string> Export(
        string baseFilePath,
        IReadOnlyList<TranscriptEntry> entries,
        IEnumerable<OutputFormat> formats,
        DateTime sessionStart,
        TimeSpan duration,
        string engine,
        string? language)
    {
        var exportedFiles = new List<string>();
        string dir = Path.GetDirectoryName(baseFilePath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(baseFilePath);

        foreach (var format in formats)
        {
            try
            {
                string ext = GetExtension(format);
                string path = Path.Combine(dir, baseName + ext);

                switch (format)
                {
                    case OutputFormat.Srt:
                        SrtWriter.Write(path, entries, sessionStart);
                        break;

                    case OutputFormat.Json:
                        JsonTranscriptWriter.Write(path, entries, sessionStart, duration, engine, language);
                        break;

                    case OutputFormat.Markdown:
                        MarkdownWriter.Write(path, entries, sessionStart, duration, engine, language);
                        break;

                    case OutputFormat.Text:
                        // テキスト形式は TranscriptWriter が既に処理しているのでスキップ
                        continue;
                }

                exportedFiles.Add(path);
                Logging.AppLogger.Info($"エクスポート完了: {path}");
            }
            catch (Exception ex)
            {
                Logging.AppLogger.Error($"エクスポート失敗 ({format})", ex);
            }
        }

        return exportedFiles;
    }

    /// <summary>フォーマットに対応するファイル拡張子を返す</summary>
    public static string GetExtension(OutputFormat format) => format switch
    {
        OutputFormat.Srt => ".srt",
        OutputFormat.Json => ".json",
        OutputFormat.Markdown => ".md",
        OutputFormat.Text => ".txt",
        _ => ".txt"
    };

    /// <summary>フォーマット名の一覧を表示用文字列で返す</summary>
    public static string FormatDescription(OutputFormat format) => format switch
    {
        OutputFormat.Text => "テキスト (.txt)",
        OutputFormat.Srt => "SRT 字幕 (.srt)",
        OutputFormat.Json => "JSON (.json)",
        OutputFormat.Markdown => "Markdown (.md)",
        _ => format.ToString()
    };

    /// <summary>文字列からフォーマットをパースする</summary>
    public static OutputFormat? ParseFormat(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "text" or "txt" => OutputFormat.Text,
            "srt" => OutputFormat.Srt,
            "json" => OutputFormat.Json,
            "markdown" or "md" => OutputFormat.Markdown,
            _ => null
        };
    }
}
