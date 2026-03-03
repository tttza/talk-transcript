namespace TalkTranscript.Translation;

/// <summary>
/// Helsinki-NLP Opus-MT モデルの言語ペア定義。
/// Hugging Face 上の ONNX 変換モデルの URL を管理する。
/// </summary>
public static class LanguagePairs
{
    /// <summary>サポートする言語ペア一覧</summary>
    /// <remarks>
    /// onnx-community の ONNX 変換済みモデルを使用。
    /// Helsinki-NLP のオリジナルリポには ONNX ファイルが含まれないため使用不可。
    /// en→ja / en→ko は ONNX 版が公開されていないため現在未サポート。
    /// </remarks>
    private static readonly Dictionary<string, LanguagePairInfo> Pairs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja-en"] = new("onnx-community/opus-mt-ja-en", "日本語 → 英語"),
        ["en-zh"] = new("onnx-community/opus-mt-en-zh", "英語 → 中国語"),
        ["zh-en"] = new("onnx-community/opus-mt-zh-en", "中国語 → 英語"),
        ["ko-en"] = new("onnx-community/opus-mt-ko-en", "韓国語 → 英語"),
        ["en-fr"] = new("onnx-community/opus-mt-en-fr", "英語 → フランス語"),
        ["fr-en"] = new("onnx-community/opus-mt-fr-en", "フランス語 → 英語"),
        ["en-de"] = new("onnx-community/opus-mt-en-de", "英語 → ドイツ語"),
        ["de-en"] = new("onnx-community/opus-mt-de-en", "ドイツ語 → 英語"),
        ["en-es"] = new("onnx-community/opus-mt-en-es", "英語 → スペイン語"),
        ["es-en"] = new("onnx-community/opus-mt-es-en", "スペイン語 → 英語"),
    };

    /// <summary>
    /// ONNX モデルのダウンロードに必要なファイル一覧。
    /// Hugging Face の ONNX 変換済みリポジトリからダウンロードする。
    /// </summary>
    public static readonly string[] RequiredFiles =
    {
        "encoder_model.onnx",
        "decoder_model.onnx",
        "decoder_with_past_model.onnx",
        "source.spm",
        "target.spm",
        "config.json",
        "tokenizer_config.json",
        "vocab.json",
    };

    /// <summary>指定した言語ペアがサポートされているか</summary>
    public static bool IsSupported(string sourceLang, string targetLang)
        => Pairs.ContainsKey($"{sourceLang}-{targetLang}");

    /// <summary>言語ペア情報を取得する</summary>
    public static LanguagePairInfo? GetInfo(string sourceLang, string targetLang)
        => Pairs.TryGetValue($"{sourceLang}-{targetLang}", out var info) ? info : null;

    /// <summary>指定した言語ペアの Hugging Face モデル URL のベースを返す</summary>
    public static string? GetModelBaseUrl(string sourceLang, string targetLang)
    {
        var info = GetInfo(sourceLang, targetLang);
        if (info == null) return null;
        // Optimum による ONNX 変換済みモデルの URL パターン
        return $"https://huggingface.co/{info.HuggingFaceRepo}/resolve/main/onnx/";
    }

    /// <summary>サポートされている全言語ペアを返す</summary>
    public static IReadOnlyList<(string Key, string Description)> GetAllPairs()
        => Pairs.Select(p => (p.Key, p.Value.Description)).ToList();

    /// <summary>翻訳先として選択可能な言語一覧</summary>
    public static IReadOnlyList<(string Code, string Name)> TargetLanguages { get; } = new[]
    {
        ("ja", "日本語"),
        ("en", "English"),
        ("zh", "中文"),
        ("ko", "한국어"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("es", "Español"),
    };
}

/// <summary>言語ペアの情報</summary>
public record LanguagePairInfo(string HuggingFaceRepo, string Description);
