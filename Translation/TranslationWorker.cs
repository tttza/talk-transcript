using System.Collections.Concurrent;
using TalkTranscript.Logging;
using TalkTranscript.Models;

namespace TalkTranscript.Translation;

/// <summary>
/// 非同期翻訳ワーカー。
/// 認識スレッドをブロックせずにバックグラウンドで翻訳を実行する。
///
/// プロデューサー/コンシューマーパターン:
/// - 認識スレッドが Enqueue() でエントリを投入
/// - ワーカースレッドが翻訳を実行し、コールバックで結果を返却
/// </summary>
public sealed class TranslationWorker : IDisposable
{
    private readonly ITranslator _translator;
    private readonly BlockingCollection<TranslationRequest> _queue = new(128);
    private readonly Thread _workerThread;
    private readonly string _translationTarget; // "自分" / "相手" / "両方"
    private volatile bool _stopping;
    private bool _disposed;

    /// <summary>翻訳完了時に呼ばれるイベント (翻訳済み TranscriptEntry を引数に取る)</summary>
    public event Action<TranscriptEntry>? OnTranslated;

    /// <summary>
    /// 翻訳ワーカーを初期化して開始する。
    /// </summary>
    /// <param name="translator">翻訳エンジン</param>
    /// <param name="translationTarget">翻訳対象 ("自分" / "相手" / "両方")</param>
    public TranslationWorker(ITranslator translator, string translationTarget = "相手")
    {
        _translator = translator;
        _translationTarget = translationTarget;

        _workerThread = new Thread(WorkerLoop)
        {
            Name = "TranslationWorker",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _workerThread.Start();

        AppLogger.Info($"TranslationWorker 開始 (対象: {translationTarget})");
    }

    /// <summary>翻訳キューにエントリを投入する (スレッドセーフ)</summary>
    public void Enqueue(TranscriptEntry entry)
    {
        if (_stopping || _disposed) return;
        if (entry.IsBookmark) return;

        // 翻訳対象フィルタ
        if (_translationTarget != "両方")
        {
            if (entry.Speaker != _translationTarget) return;
        }

        try
        {
            if (!_queue.TryAdd(new TranslationRequest(entry)))
            {
                AppLogger.Warn("翻訳キューが満杯のため翻訳をスキップしました");
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

        // ワーカースレッドの終了を待つ (翻訳中のタスクが完了するまで)
        if (!_workerThread.Join(10_000))
        {
            AppLogger.Warn("TranslationWorker: 停止タイムアウト (10秒)。翻訳を中断します。");
        }
        AppLogger.Info("TranslationWorker 停止");
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable())
            {
                if (_stopping) break;

                try
                {
                    string? translated = _translator.Translate(request.Entry.Text);
                    if (!string.IsNullOrEmpty(translated))
                    {
                        var translatedEntry = request.Entry with { TranslatedText = translated };
                        OnTranslated?.Invoke(translatedEntry);
                    }
                    else
                    {
                        AppLogger.Debug($"翻訳結果が空: \"{request.Entry.Text}\"");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"翻訳ワーカーエラー: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _queue.Dispose();
    }

    private record TranslationRequest(TranscriptEntry Entry);
}
