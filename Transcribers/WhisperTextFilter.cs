using System.Text.RegularExpressions;

namespace TalkTranscript.Transcribers;

/// <summary>
/// Whisper 認識結果のハルシネーション除去、重複検出、テキスト正規化を行うフィルター。
///
/// Whisper は無音・小音量区間で以下のようなハルシネーションを起こしやすい:
///   - "ご視聴ありがとうございました"
///   - "字幕作成: ○○" 
///   - 同じフレーズの無限繰り返し
///   - "(音楽)" などの非発話テキスト
///
/// このフィルターで認識精度を大幅に向上させる。
/// </summary>
internal static class WhisperTextFilter
{
    // ── ハルシネーションとして既知のパターン (正規表現) ──
    private static readonly Regex[] HallucinationPatterns = new[]
    {
        // YouTube 字幕系
        new Regex(@"^ご視聴ありがとうございま(した|す)", RegexOptions.Compiled),
        new Regex(@"^チャンネル登録", RegexOptions.Compiled),
        new Regex(@"^(字幕|翻訳|テロップ).*(作成|提供|協力)", RegexOptions.Compiled),
        new Regex(@"^お疲れ様でした\s*$", RegexOptions.Compiled),
        new Regex(@"^ありがとうございました\s*$", RegexOptions.Compiled),

        // 音響イベント (非発話)
        new Regex(@"^\(.*\)\s*$", RegexOptions.Compiled),          // (音楽), (拍手) 等
        new Regex(@"^\[.*\]\s*$", RegexOptions.Compiled),          // [音楽], [拍手] 等
        new Regex(@"^♪+\s*$", RegexOptions.Compiled),              // ♪♪♪

        // 句読点・記号のみ
        new Regex(@"^[\s。、．，！？!?…・\-ー～~]+$", RegexOptions.Compiled),

        // 英語のハルシネーション
        new Regex(@"^(Thank you|Thanks for watching|Subscribe)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"^(you|\.\.\.)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// 同一フレーズの繰り返しを検出する正規表現。
    /// 例: "はい はい はい はい" → 3回以上繰り返しは異常
    /// </summary>
    private static readonly Regex RepetitionPattern =
        new(@"(.{2,15}?)\1{2,}", RegexOptions.Compiled);

    /// <summary>
    /// テキストがハルシネーションかどうかを判定する。
    /// true を返した場合は結果を破棄すべき。
    /// </summary>
    public static bool IsHallucination(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        string trimmed = text.Trim();

        // 極端に短い (1文字以下)
        if (trimmed.Length <= 1)
            return true;

        // 既知のハルシネーションパターン
        foreach (var pattern in HallucinationPatterns)
        {
            if (pattern.IsMatch(trimmed))
                return true;
        }

        // 同一フレーズの過剰な繰り返し
        if (RepetitionPattern.IsMatch(trimmed))
        {
            // 繰り返し部分がテキスト全体の 70% 以上を占める場合はハルシネーション
            var match = RepetitionPattern.Match(trimmed);
            if (match.Length > trimmed.Length * 0.7)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 前回の認識結果と比較して重複かどうかを判定する。
    /// Whisper は同じ音声区間を複数回処理すると同一テキストを返すことがある。
    /// </summary>
    /// <param name="newText">新しく認識されたテキスト</param>
    /// <param name="previousText">前回認識されたテキスト</param>
    /// <param name="threshold">類似度閾値 (0.0-1.0、デフォルト 0.8)</param>
    public static bool IsDuplicate(string newText, string previousText, double threshold = 0.8)
    {
        if (string.IsNullOrEmpty(previousText))
            return false;

        string a = NormalizeForComparison(newText);
        string b = NormalizeForComparison(previousText);

        // 完全一致
        if (a == b)
            return true;

        // 一方が他方を含む (部分的な重複)
        if (a.Length > 3 && b.Length > 3)
        {
            if (b.Contains(a) || a.Contains(b))
                return true;
        }

        // レーベンシュタイン距離ベースの類似度
        if (a.Length > 0 && b.Length > 0)
        {
            double similarity = 1.0 - (double)LevenshteinDistance(a, b) / Math.Max(a.Length, b.Length);
            if (similarity >= threshold)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 認識テキストを正規化・クリーンアップする。
    /// </summary>
    public static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        string result = text.Trim();

        // 全角スペースを半角に
        result = result.Replace('　', ' ');

        // 連続する空白を1つに
        result = Regex.Replace(result, @"\s{2,}", " ");

        // 先頭末尾の不要な句読点を除去
        result = result.TrimStart('、', ',', '。', '.', '　', ' ');

        return result;
    }

    /// <summary>
    /// 比較用にテキストを正規化する (空白・句読点を除去)。
    /// </summary>
    private static string NormalizeForComparison(string text)
    {
        string s = text.Trim();
        // 空白・句読点を除去して比較
        s = Regex.Replace(s, @"[\s。、，．！？!?,.\-ー]", "");
        return s;
    }

    /// <summary>
    /// レーベンシュタイン距離を計算する (2行DP)。
    /// </summary>
    private static int LevenshteinDistance(string s, string t)
    {
        int n = s.Length, m = t.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        var prev = new int[m + 1];
        var curr = new int[m + 1];

        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }
}
