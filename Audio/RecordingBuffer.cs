namespace TalkTranscript.Audio;

/// <summary>
/// チャンク分割方式の録音バッファ (メモリ上のみ / ディスク書き出しなし)。
///
/// MemoryStream の倍々確保 + ToArray コピー問題を解消するため、
/// 固定サイズ (64KB) のチャンクを List で管理する。
///
/// - <see cref="Write"/>: チャンク単位でメモリに蓄積 (LOH 断片化を抑制)
/// - <see cref="SaveAsWav"/>: チャンクを順次ファイルに書き出し (全体を byte[] にしない)
/// - <see cref="ToArray"/>: Whisper 後処理用に全体を byte[] に結合 (一時的にメモリ消費)
/// - <see cref="Dispose"/>: 全チャンク参照をクリアし GC 回収可能にする
///
/// 無効状態 (enabled=false) では全操作が no-op となる。
/// スレッドセーフ (内部ロック)。
/// </summary>
internal sealed class RecordingBuffer : IDisposable
{
    private const int ChunkSize = 65536; // 64KB — LOH 閾値 (85KB) 未満

    private readonly List<byte[]>? _chunks;
    private readonly object _lock = new();
    private readonly long _maxBytes;
    private int _tailOffset; // 末尾チャンク内の書き込み済みオフセット
    private long _totalBytes;
    private bool _disposed;

    /// <summary>書き込み済みバイト数</summary>
    public long Length
    {
        get { lock (_lock) return _totalBytes; }
    }

    /// <summary>録音が有効かどうか</summary>
    public bool IsEnabled => _chunks != null;

    /// <param name="enabled">true で録音バッファを有効化</param>
    /// <param name="maxBytes">最大バイト数 (0 = 空きメモリから自動計算, 超過分は無視)</param>
    public RecordingBuffer(bool enabled, long maxBytes = 0)
    {
        _maxBytes = maxBytes > 0
            ? maxBytes
            : MemoryHelper.CalculateBufferMaxBytes(bufferCount: 2);
        if (!enabled) return;

        // 初期容量 = 想定チャンク数の 1/4 (リスト再確保を減らす)
        int estimatedChunks = (int)(maxBytes / ChunkSize / 4) + 1;
        _chunks = new List<byte[]>(estimatedChunks);
    }

    /// <summary>
    /// オーディオデータを書き込む。
    /// 64KB 固定チャンクに分割して蓄積するため、
    /// MemoryStream のような倍々確保が発生しない。
    /// </summary>
    public void Write(byte[] buffer, int offset, int count)
    {
        if (_chunks == null || _disposed) return;
        lock (_lock)
        {
            int remaining = count;
            int srcOffset = offset;

            while (remaining > 0 && _totalBytes < _maxBytes)
            {
                // 末尾チャンクに空きがない場合は新規チャンク追加
                if (_chunks.Count == 0 || _tailOffset >= ChunkSize)
                {
                    _chunks.Add(new byte[ChunkSize]);
                    _tailOffset = 0;
                }

                int space = ChunkSize - _tailOffset;
                int maxWrite = (int)Math.Min(remaining, _maxBytes - _totalBytes);
                int toWrite = Math.Min(space, maxWrite);

                Buffer.BlockCopy(buffer, srcOffset, _chunks[^1], _tailOffset, toWrite);
                _tailOffset += toWrite;
                _totalBytes += toWrite;
                srcOffset += toWrite;
                remaining -= toWrite;
            }
        }
    }

    /// <summary>
    /// 録音データ全体をバイト配列として返す (Whisper 後処理用)。
    /// 一時的にメモリを消費するが、呼び出し側で使用後すぐに GC 回収される。
    /// </summary>
    public byte[] ToArray()
    {
        if (_chunks == null || _disposed) return Array.Empty<byte>();
        lock (_lock)
        {
            if (_totalBytes == 0 || _totalBytes > int.MaxValue)
                return Array.Empty<byte>();

            int total = (int)_totalBytes;
            byte[] result = new byte[total];
            int destOffset = 0;

            for (int i = 0; i < _chunks.Count; i++)
            {
                int copyLen = (i < _chunks.Count - 1) ? ChunkSize : _tailOffset;
                Buffer.BlockCopy(_chunks[i], 0, result, destOffset, copyLen);
                destOffset += copyLen;
            }
            return result;
        }
    }

    /// <summary>
    /// 録音データを WAV ファイルとして保存する。
    /// チャンクを順次書き出すため、全体を byte[] に結合しない。
    /// </summary>
    /// <param name="wavPath">出力 WAV ファイルパス</param>
    /// <param name="sampleRate">サンプルレート (既定: 16000)</param>
    public void SaveAsWav(string wavPath, int sampleRate = 16000)
    {
        if (_chunks == null || _disposed) return;
        lock (_lock)
        {
            if (_totalBytes == 0) return;

            using var output = File.Create(wavPath);
            WriteWavPcm16Header(output, (int)_totalBytes, sampleRate);

            for (int i = 0; i < _chunks.Count; i++)
            {
                int writeLen = (i < _chunks.Count - 1) ? ChunkSize : _tailOffset;
                output.Write(_chunks[i], 0, writeLen);
            }
        }
    }

    /// <summary>WAV ファイルの PCM16 ヘッダーを書き込む</summary>
    private static void WriteWavPcm16Header(Stream stream, int dataSize, int sampleRate)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        int channels = 1;
        int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);    // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);

        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
    }

    /// <summary>
    /// 全チャンクの参照をクリアし、GC 回収可能にする。
    /// ディスクに書き出していないため、ファイル削除は不要。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_chunks != null)
        {
            lock (_lock)
            {
                _chunks.Clear();
                _totalBytes = 0;
                _tailOffset = 0;
            }
        }
    }
}
