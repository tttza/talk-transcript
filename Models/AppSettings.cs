using System.Text.Json;
using System.Text.Json.Serialization;
using TalkTranscript.Output;

namespace TalkTranscript.Models;

/// <summary>
/// アプリケーション設定 (選択したデバイス情報を永続化する)。
/// </summary>
public class AppSettings
{
    /// <summary>マイクデバイスの ID</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>マイクデバイスの表示名 (確認用)</summary>
    public string? MicrophoneDeviceName { get; set; }

    /// <summary>スピーカーデバイスの ID</summary>
    public string? SpeakerDeviceId { get; set; }

    /// <summary>スピーカーデバイスの表示名 (確認用)</summary>
    public string? SpeakerDeviceName { get; set; }

    /// <summary>認識エンジン名 (vosk / sapi / whisper-tiny / whisper-base など)</summary>
    public string? EngineName { get; set; }

    /// <summary>Whisper で GPU を使用するか (CUDA ランタイム導入時のみ有効)</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>認識言語 (ja / en / auto など。Whisper の言語パラメータ)</summary>
    public string? Language { get; set; }

    /// <summary>出力ディレクトリ (null の場合は既定の Transcripts/ を使用)</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>録音データを WAV ファイルとして保存するか</summary>
    public bool SaveRecording { get; set; } = false;

    /// <summary>追加の出力フォーマット (テキスト出力は常に行われる)</summary>
    [JsonConverter(typeof(OutputFormatsConverter))]
    public List<OutputFormat>? OutputFormats { get; set; }

    // ── 永続化 ──

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>設定ファイルのパス (%APPDATA%\TalkTranscript\settings.json)</summary>
    public static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TalkTranscript");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    /// <summary>設定を JSON ファイルから読み込む。ファイルがなければ新規インスタンスを返す。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                       ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"設定ファイルの読み込みに失敗しました: {ex.Message}");
        }

        return new AppSettings();
    }

    /// <summary>設定を JSON ファイルに保存する。</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"設定ファイルの保存に失敗しました: {ex.Message}");
        }
    }

    /// <summary>OutputFormats の JSON 変換</summary>
    private class OutputFormatsConverter : JsonConverter<List<OutputFormat>?>
    {
        public override List<OutputFormat>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            var list = new List<OutputFormat>();
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var str = reader.GetString();
                    if (str != null && Enum.TryParse<OutputFormat>(str, true, out var fmt))
                        list.Add(fmt);
                }
            }
            return list.Count > 0 ? list : null;
        }

        public override void Write(Utf8JsonWriter writer, List<OutputFormat>? value, JsonSerializerOptions options)
        {
            if (value == null || value.Count == 0) { writer.WriteNullValue(); return; }
            writer.WriteStartArray();
            foreach (var f in value) writer.WriteStringValue(f.ToString());
            writer.WriteEndArray();
        }
    }
}
