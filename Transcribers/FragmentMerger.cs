using System.Collections.Concurrent;
using System.Text;
using TalkTranscript.Logging;
using TalkTranscript.Models;

namespace TalkTranscript.Transcribers;

/// <summary>
/// フラグメント結合ワーカー。
///
/// インターバル処理で細切れになった同一話者のフラグメントをバッファリングし、
/// 文の切れ目（句読点）やタイムアウトで結合してから通知する。
/// 翻訳の有無にかかわらず常時動作する。
///
/// プロデューサー/コンシューマーパターン:
/// - 認識スレッドが Enqueue() でエントリを投入
/// - ワーカースレッドがバッファリング → 結合 → コールバックで結果を返却
/// </summary>
public sealed class FragmentMerger : IDisposable
{
    private readonly BlockingCollection<TranscriptEntry> _queue = new(128);
    private readonly Thread _workerThread;
    private volatile bool _stopping;
    private bool _disposed;

    /// <summary>マージバッファに蓄積する最大エントリ数</summary>
    private const int MaxMergeCount = 10;

    /// <summary>
    /// 未完文待機タイムアウト (ms)。
    /// インターバル処理 (3秒) + Whisper 推論時間を考慮。
    /// 文が途中で終わっている場合に次のフラグメントを待つ最大時間。
    /// </summary>
    private const int MinIncompleteSentenceTimeoutMs = 20_000;

    /// <summary>
    /// 完結文フラッシュタイムアウト (ms)。
    /// 文末記号で終わっているバッファを、次のフラグメントが来なかった場合に
    /// フラッシュするまでの待機時間。通常は文末検出で即時フラッシュされるため
    /// 安全策として機能する。
    /// </summary>
    private const int DefaultFlushTimeoutMs = 1_500;

    /// <summary>
    /// 複数フラグメントが逐次結合されたときに発火されるイベント。
    /// 元のエントリリストと、結合後の新エントリを引数に取る。
    /// UI 側で既存の行にテキストを追記するために使用。
    /// </summary>
    public event Action<IReadOnlyList<TranscriptEntry>, TranscriptEntry>? OnMerged;

    /// <summary>
    /// バッファがフラッシュされたときに発火されるイベント。
    /// 結合済み (count > 1) / 単一 (count == 1) を問わず、
    /// 確定したエントリを通知する。翻訳キューへの投入に使用。
    /// </summary>
    public event Action<TranscriptEntry>? OnFlushed;

    /// <summary>
    /// フラグメント結合ワーカーを初期化して開始する。
    /// </summary>
    public FragmentMerger()
    {
        _workerThread = new Thread(WorkerLoop)
        {
            Name = "FragmentMerger",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _workerThread.Start();

        AppLogger.Info("FragmentMerger 開始");
    }

    /// <summary>結合キューにエントリを投入する (スレッドセーフ)</summary>
    public void Enqueue(TranscriptEntry entry)
    {
        if (_stopping || _disposed) return;
        if (entry.IsBookmark) return;

        try
        {
            if (!_queue.TryAdd(entry, TimeSpan.FromMilliseconds(50)))
            {
                AppLogger.Warn("FragmentMerger: キューが満杯のためスキップ");
            }
        }
        catch (InvalidOperationException)
        {
            // キューが完了済み
        }
    }

    /// <summary>ワーカーを停止する</summary>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        _queue.CompleteAdding();

        if (!_workerThread.Join(5_000))
        {
            AppLogger.Warn("FragmentMerger: 停止タイムアウト (5秒)");
        }
        AppLogger.Info("FragmentMerger 停止");
    }

    private void WorkerLoop()
    {
        var buffer = new List<TranscriptEntry>();
        string? currentSpeaker = null;
        TranscriptEntry? _runningMerged = null; // 逐次マージの現在結果 (UI 表示中)

        try
        {
            while (!_stopping)
            {
                bool gotItem;
                TranscriptEntry? entry = null;

                try
                {
                    int timeout;
                    if (buffer.Count > 0)
                    {
                        // マージ済みテキストまたは最後のフラグメントで完結度を判定
                        string checkText = _runningMerged?.Text ?? buffer[^1].Text;
                        bool incomplete = !EndsWithSentenceBoundary(checkText);
                        timeout = incomplete
                            ? MinIncompleteSentenceTimeoutMs
                            : DefaultFlushTimeoutMs;
                    }
                    else
                    {
                        timeout = Timeout.Infinite;
                    }
                    gotItem = _queue.TryTake(out entry, timeout);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (gotItem && entry != null)
                {
                    // 話者が変わった or バッファが満杯 → 既存バッファをフラッシュ
                    if (buffer.Count > 0 &&
                        (entry.Speaker != currentSpeaker
                         || buffer.Count >= MaxMergeCount))
                    {
                        DoFlush(buffer, _runningMerged);
                        buffer.Clear();
                        _runningMerged = null;
                    }

                    buffer.Add(entry);
                    currentSpeaker = entry.Speaker;

                    // ── 逐次マージ: 2件以上なら即座にマージして UI を更新 ──
                    if (buffer.Count > 1)
                    {
                        var mergedEntry = BuildMergedEntry(buffer);

                        // UI 置換対象: [前回マージ結果 or 最初のエントリ, 今回追加エントリ]
                        var toReplace = new List<TranscriptEntry>(2)
                        {
                            _runningMerged ?? buffer[0],
                            entry
                        };
                        OnMerged?.Invoke(toReplace.AsReadOnly(), mergedEntry);
                        _runningMerged = mergedEntry;
                    }

                    // ── マージ済みテキストが文末で完結 → フラッシュ (翻訳に渡す) ──
                    string currentText = _runningMerged?.Text ?? entry.Text;
                    if (EndsWithSentenceBoundary(currentText))
                    {
                        DoFlush(buffer, _runningMerged);
                        buffer.Clear();
                        _runningMerged = null;
                        // currentSpeaker は維持: 次エントリの話者比較用
                    }
                }
                else
                {
                    // タイムアウト → フラッシュ
                    if (buffer.Count > 0)
                    {
                        DoFlush(buffer, _runningMerged);
                        buffer.Clear();
                        _runningMerged = null;
                        currentSpeaker = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }

        // 残りをフラッシュ
        if (buffer.Count > 0)
        {
            DoFlush(buffer, _runningMerged);
        }
    }

    /// <summary>バッファ内の全フラグメントから結合エントリを作成する</summary>
    private TranscriptEntry BuildMergedEntry(List<TranscriptEntry> buffer)
    {
        string merged = JoinTexts(buffer);
        TimeSpan? totalDuration = null;
        foreach (var e in buffer)
        {
            if (e.Duration.HasValue)
                totalDuration = (totalDuration ?? TimeSpan.Zero) + e.Duration.Value;
        }
        AppLogger.Debug($"FragmentMerger: {buffer.Count}件を逐次結合 ({merged.Length}文字)");
        return buffer[^1] with { Text = merged, Duration = totalDuration };
    }

    /// <summary>
    /// バッファをフラッシュし、確定エントリを翻訳用に通知する。
    /// 逐次マージで UI は既に更新済みなので OnMerged は発火しない。
    /// </summary>
    private void DoFlush(List<TranscriptEntry> buffer, TranscriptEntry? runningMerged)
    {
        if (buffer.Count == 0) return;
        var entry = runningMerged ?? buffer[0];
        OnFlushed?.Invoke(entry);
    }

    // ────────────────────────────────────────────────
    //  テキスト完結度判定 (static ユーティリティ)
    // ────────────────────────────────────────────────

    /// <summary>
    /// バッファ内テキストが「十分な完結度」を持つかを判定する。
    /// 文末記号で終わっている場合だけでなく、文中に完結した文を含む場合も
    /// 完結とみなす (Whisper が末尾の句読点を省略するケースに対応)。
    /// </summary>
    internal static bool IsBufferTextComplete(string text)
    {
        // フラグメント結合では「末尾が文末記号か」だけで判定する。
        // 内部に句読点があっても末尾が文途中なら未完扱いとし、
        // 次のフラグメントとの結合を待つ。
        return EndsWithSentenceBoundary(text);
    }

    /// <summary>
    /// テキスト中に文末記号の後にさらにテキストが続くパターンがあるかを判定する。
    /// 例: "Sure. I can talk about anything" → true ("." の後にテキストがある)
    /// 例: "and you'll be able to see exactly" → false (句読点がない)
    /// </summary>
    internal static bool HasInternalSentenceBoundary(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (int i = 0; i < text.Length - 1; i++)
        {
            if ("。！？.!?".IndexOf(text[i]) >= 0)
            {
                // 句読点の後に空白以外の文字があるか
                for (int j = i + 1; j < text.Length; j++)
                {
                    if (!char.IsWhiteSpace(text[j]))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// テキストが文末記号で終わっているかを判定する。
    /// 文の途中で分割されたフラグメントを検出し、マージ待機を延長するために使用。
    /// 英語 (.!?)、日本語 (。！？)、閉じ括弧 (」』）) などに対応。
    /// </summary>
    internal static bool EndsWithSentenceBoundary(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true; // 空テキストは完了扱い

        // 末尾の空白を除いた最後の文字を取得
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) continue;

            // 文末記号: 英語/日本語/中国語の句読点、閉じ括弧類
            return "。！？.!?」』）)…♪".IndexOf(c) >= 0;
        }

        return true;
    }

    /// <summary>
    /// フラグメントのテキストをスマートに結合する。
    /// ラテン文字系テキストの境界にはスペースを補完し、CJK テキストはそのまま結合する。
    /// </summary>
    internal static string JoinTexts(List<TranscriptEntry> entries)
    {
        if (entries.Count == 0) return "";
        if (entries.Count == 1) return entries[0].Text;

        var sb = new StringBuilder(entries[0].Text);
        for (int i = 1; i < entries.Count; i++)
        {
            string prev = entries[i - 1].Text;
            string curr = entries[i].Text;

            if (prev.Length > 0 && curr.Length > 0)
            {
                char lastChar = prev[^1];
                char firstChar = curr[0];

                // 前後どちらかが空白ならスペース不要
                // 前後どちらかが CJK ならスペース不要 (日本語・中国語・韓国語)
                if (!char.IsWhiteSpace(lastChar) && !char.IsWhiteSpace(firstChar)
                    && !IsCjkChar(lastChar) && !IsCjkChar(firstChar))
                {
                    sb.Append(' ');
                }
            }

            sb.Append(curr);
        }

        return sb.ToString();
    }

    /// <summary>CJK 文字かどうかを判定する (スペース補完不要な文字)</summary>
    private static bool IsCjkChar(char c)
    {
        return (c >= '\u3040' && c <= '\u309F')   // ひらがな
            || (c >= '\u30A0' && c <= '\u30FF')   // カタカナ
            || (c >= '\u4E00' && c <= '\u9FFF')   // CJK統合漢字
            || (c >= '\u3400' && c <= '\u4DBF')   // CJK拡張A
            || (c >= '\uF900' && c <= '\uFAFF')   // CJK互換漢字
            || (c >= '\uAC00' && c <= '\uD7A3')   // ハングル
            || (c >= '\u3000' && c <= '\u303F');   // CJK句読点・記号
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _queue.Dispose();
    }
}
