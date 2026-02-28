namespace TalkTranscript.Audio;

/// <summary>
/// チャンク分割方式の録音バッファ。
///
/// 2 つの動作モードを持つ:
///
/// <b>メモリモード (既定)</b>:
/// 固定サイズ (64KB) のチャンクを List で管理し、メモリに蓄積する。
/// Whisper 後処理で ToArray() が必要な場合に使用。
///
/// <b>ストリーミングモード</b>:
/// <see cref="StartStreaming"/> を呼ぶと WAV ファイルを開き、
/// 以降の Write() はメモリに蓄積せず直接ディスクに書き出す。
/// 長時間録音でもメモリ消費を抑制できる。
///
/// - <see cref="Write"/>: モードに応じてメモリまたはディスクに書き込む
/// - <see cref="SaveAsWav"/>: メモリモード時のみチャンクをファイルに書き出す
/// - <see cref="ToArray"/>: Whisper 後処理用に全体を byte[] に結合 (メモリモードのみ)
/// - <see cref="StopStreaming"/>: WAV ヘッダーを更新してファイルを閉じる
/// - <see cref="Dispose"/>: 全リソースを解放する
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

    // ── ストリーミングモード ──
    private FileStream? _streamingFile;
    private string? _streamingPath;
    private bool _streaming;

    /// <summary>書き込み済みバイト数</summary>
    public long Length
    {
        get { lock (_lock) return _totalBytes; }
    }

    /// <summary>録音が有効かどうか</summary>
    public bool IsEnabled => _chunks != null;

    /// <summary>ストリーミングモードが有効かどうか</summary>
    public bool IsStreaming { get { lock (_lock) return _streaming; } }

    /// <summary>ストリーミング先ファイルパス (ストリーミング中のみ非 null)</summary>
    public string? StreamingPath { get { lock (_lock) return _streamingPath; } }

    /// <param name="enabled">true で録音バッファを有効化</param>
    /// <param name="maxBytes">最大バイト数 (0 = 空きメモリから自動計算, 超過分は無視)</param>
    public RecordingBuffer(bool enabled, long maxBytes = 0)
    {
        _maxBytes = maxBytes > 0
            ? maxBytes
            : MemoryHelper.CalculateBufferMaxBytes(bufferCount: 2);
        if (!enabled) return;

        // 初期容量 = 想定チャンク数の 1/4 (リスト再確保を減らす)
        int estimatedChunks = (int)(_maxBytes / ChunkSize / 4) + 1;
        _chunks = new List<byte[]>(estimatedChunks);
    }

    /// <summary>
    /// オーディオデータを書き込む。
    /// ストリーミングモードではディスクに直接書き出し、
    /// メモリモードでは 64KB 固定チャンクに分割して蓄積する。
    /// </summary>
    public void Write(byte[] buffer, int offset, int count)
    {
        if (_chunks == null || _disposed) return;
        lock (_lock)
        {
            int remaining = count;
            int srcOffset = offset;

            if (_streaming && _streamingFile != null)
            {
                // ストリーミングモード: ディスクに直接書き出す (メモリ上限なし)
                _streamingFile.Write(buffer, srcOffset, remaining);
                _totalBytes += remaining;
                return;
            }

            // メモリモード: チャンクに蓄積 (_maxBytes まで)
            while (remaining > 0 && _totalBytes < _maxBytes)
            {
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
    /// ストリーミングモードを開始する。
    /// WAV ファイルを開き、以降の Write() はメモリに蓄積せずディスクに直接書き出す。
    /// 既にメモリに蓄積されたチャンクがあればディスクにフラッシュして解放する。
    /// </summary>
    /// <param name="wavPath">出力 WAV ファイルパス</param>
    /// <param name="sampleRate">サンプルレート (既定: 16000)</param>
    public void StartStreaming(string wavPath, int sampleRate = 16000)
    {
        if (_chunks == null || _disposed) return;
        lock (_lock)
        {
            if (_streaming) return;

            Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);
            _streamingFile = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
            _streamingPath = wavPath;
            _streaming = true;

            // プレースホルダーサイズで WAV ヘッダーを書き込む (後で更新)
            WriteWavPcm16Header(_streamingFile, 0, sampleRate);

            // 既存のメモリチャンクをディスクにフラッシュ
            for (int i = 0; i < _chunks.Count; i++)
            {
                int writeLen = (i < _chunks.Count - 1) ? ChunkSize : _tailOffset;
                _streamingFile.Write(_chunks[i], 0, writeLen);
            }
            _chunks.Clear();
            _chunks.TrimExcess();
            _tailOffset = 0;
            // _totalBytes はそのまま維持 (ヘッダー更新時に必要)
        }
    }

    /// <summary>
    /// ストリーミングモードを停止する。
    /// WAV ヘッダーのデータサイズを正しい値に更新してファイルを閉じる。
    /// </summary>
    public void StopStreaming()
    {
        if (!_streaming || _streamingFile == null) return;
        lock (_lock)
        {
            if (!_streaming || _streamingFile == null) return;

            if (_totalBytes > 0 && _totalBytes <= int.MaxValue)
            {
                int dataSize = (int)_totalBytes;
                _streamingFile.Flush();

                // RIFF チャンクサイズ (オフセット 4): 36 + dataSize
                _streamingFile.Seek(4, SeekOrigin.Begin);
                var riffSize = BitConverter.GetBytes(36 + dataSize);
                _streamingFile.Write(riffSize, 0, 4);

                // data チャンクサイズ (オフセット 40): dataSize
                _streamingFile.Seek(40, SeekOrigin.Begin);
                var dataSizeBytes = BitConverter.GetBytes(dataSize);
                _streamingFile.Write(dataSizeBytes, 0, 4);
            }

            _streamingFile.Flush();
            _streamingFile.Dispose();
            _streamingFile = null;
            _streaming = false;
        }
    }

    /// <summary>
    /// 録音データ全体をバイト配列として返す (Whisper 後処理用)。
    /// メモリモードでのみ使用可能。ストリーミングモードでは空配列を返す。
    /// </summary>
    public byte[] ToArray()
    {
        if (_chunks == null || _disposed) return Array.Empty<byte>();
        lock (_lock)
        {
            // ストリーミング済み or データなし → メモリ上にデータがない
            if (_totalBytes == 0 || _totalBytes > int.MaxValue || _chunks.Count == 0)
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
    /// 録音データを WAV ファイルとして保存する (メモリモード用)。
    /// チャンクを順次書き出すため、全体を byte[] に結合しない。
    /// ストリーミングモードでは既にディスクに書き出し済みのため no-op。
    /// </summary>
    /// <param name="wavPath">出力 WAV ファイルパス</param>
    /// <param name="sampleRate">サンプルレート (既定: 16000)</param>
    public void SaveAsWav(string wavPath, int sampleRate = 16000)
    {
        if (_chunks == null || _disposed) return;
        lock (_lock)
        {
            // ストリーミング中 or 完了済みの場合は既にディスク上にある
            if (_streaming || _streamingPath != null) return;

            if (_totalBytes == 0) return;

            // WAV (RIFF) フォーマットはデータサイズが 32bit のため 2GB 超を書けない
            if (_totalBytes > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"録音データが WAV 上限 (2GB) を超えています ({_totalBytes / (1024 * 1024)}MB)。");
            }

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
    /// チャンクデータをクリアしてメモリを解放する (Dispose はしない)。
    /// SaveAsWav() 後に後処理が不要な場合、早期にメモリを回収するために使用する。
    /// </summary>
    public void Clear()
    {
        if (_chunks == null || _disposed) return;
        lock (_lock)
        {
            _chunks.Clear();
            _chunks.TrimExcess();
            _totalBytes = 0;
            _tailOffset = 0;
        }
    }

    /// <summary>
    /// 全リソースを解放する。
    /// ストリーミング中の場合はファイルを確定してから閉じる。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // ストリーミング中のファイルを確定して閉じる
        try { StopStreaming(); } catch { }

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
