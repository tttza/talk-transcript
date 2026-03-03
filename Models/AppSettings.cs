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

    /// <summary>
    /// GPU バックエンド ("Auto" / "Cuda" / "Vulkan" / "None")。
    /// Auto = NVIDIA GPU → CUDA、AMD/Intel GPU → Vulkan、GPU なし → CPU。
    /// 未設定 (null) の場合は UseGpu 設定から自動判定する (後方互換)。
    /// </summary>
    public string? GpuBackendName { get; set; }

    /// <summary>認識言語 (ja / en / auto など。Whisper の言語パラメータ)</summary>
    public string? Language { get; set; }

    /// <summary>出力ディレクトリ (null の場合は既定の Transcripts/ を使用)</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>録音データを WAV ファイルとして保存するか</summary>
    public bool SaveRecording { get; set; } = false;

    /// <summary>マイクのみ録音を保存するか (true の場合スピーカー録音をファイルに残さない)</summary>
    public bool SaveMicOnly { get; set; } = false;

    /// <summary>
    /// Whisper 推論に使用する最大 CPU スレッド数。
    /// 0 = 自動 (CPU コア数 - 4、最低 1)。
    /// 値を小さくすると CPU 負荷が下がり PC が軽くなる。
    /// </summary>
    public int MaxCpuThreads { get; set; } = 0;

    /// <summary>
    /// プロセス優先度。
    /// "Normal" (通常) / "BelowNormal" (やや低い) / "Idle" (最低) から選択。
    /// 低い優先度にすると他のアプリの動作を妨げにくくなる。
    /// </summary>
    public string ProcessPriority { get; set; } = "Normal";

    /// <summary>出力フォーマット (未設定の場合はテキスト出力のみ)</summary>
    [JsonConverter(typeof(OutputFormatsConverter))]
    public List<OutputFormat>? OutputFormats { get; set; }

    // ── 翻訳設定 ──

    /// <summary>リアルタイム翻訳を有効にするか</summary>
    public bool EnableTranslation { get; set; } = false;

    /// <summary>翻訳元言語 (null = 自動検出)</summary>
    public string? TranslationSourceLang { get; set; }

    /// <summary>翻訳先言語</summary>
    public string TranslationTargetLang { get; set; } = "ja";

    /// <summary>翻訳対象 ("相手" / "自分" / "両方")</summary>
    public string TranslationTarget { get; set; } = "相手";

    /// <summary>翻訳で GPU を使用するか</summary>
    public bool TranslationUseGpu { get; set; } = true;

    // ── GPU バックエンド ヘルパー ──

    /// <summary>
    /// GpuBackend 列挙値を取得する。
    /// GpuBackendName が未設定の場合は UseGpu フラグから自動変換する (後方互換)。
    /// </summary>
    [JsonIgnore]
    public GpuBackend EffectiveGpuBackend
    {
        get
        {
            if (!string.IsNullOrEmpty(GpuBackendName)
                && Enum.TryParse<GpuBackend>(GpuBackendName, true, out var parsed))
                return parsed;
            // 後方互換: UseGpu が true なら Auto (自動検出)、false なら None
            return UseGpu ? GpuBackend.Auto : GpuBackend.None;
        }
        set
        {
            GpuBackendName = value.ToString();
            UseGpu = value != GpuBackend.None;
        }
    }

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
