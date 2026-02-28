using System.Collections.Concurrent;
using TalkTranscript.Logging;

namespace TalkTranscript.Audio;

/// <summary>
/// WASAPI ループバックキャプチャと System.Speech.Recognition.SpeechRecognitionEngine を
/// 橋渡しするスレッドセーフなストリーム。
/// ループバック側が Write() でオーディオデータを書き込み、
/// SpeechRecognitionEngine が Read() で読み取る。
///
/// 有界バッファを使用し、上限を超えた場合は最も古いデータを破棄して
/// メモリの無制限成長を防止する。
/// </summary>
public sealed class SpeechAudioStream : Stream
{
    /// <summary>16kHz/16bit/mono 約10秒分 ≒ 320KB / 約3.2KB per chunk = 100 チャンク</summary>
    private const int DefaultBoundedCapacity = 100;

    private readonly BlockingCollection<byte[]> _buffer;
    private byte[] _currentChunk = Array.Empty<byte>();
    private int _currentOffset;
    private long _position;
    private volatile bool _done;
    private long _droppedBytes;

    /// <summary>バッファ溢れにより破棄されたバイト数</summary>
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <param name="boundedCapacity">内部キューの最大チャンク数 (0 = 無制限)</param>
    public SpeechAudioStream(int boundedCapacity = DefaultBoundedCapacity)
    {
        _buffer = boundedCapacity > 0
            ? new BlockingCollection<byte[]>(boundedCapacity)
            : new BlockingCollection<byte[]>();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;  // SpeechRecognitionEngine が Seek 可能を期待する
    public override bool CanWrite => true;
    public override long Length => long.MaxValue;  // 無限ストリームとして扱う
    public override long Position
    {
        get => _position;
        set { /* SpeechRecognitionEngine が呼び出すので無視する */ }
    }

    /// <summary>
    /// SpeechRecognitionEngine から呼ばれる。データが届くまでブロックする。
    /// SpeechRecognitionEngine は Read() が 0 を返すとストリーム終了と判断するため、
    /// ストリームが完了 (_done=true) するまでは必ずデータが来るまでブロックする。
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;

        int totalRead = 0;

        while (totalRead < count)
        {
            // 現在のチャンクを使い切ったら次のチャンクを取得
            if (_currentOffset >= _currentChunk.Length)
            {
                if (_done && _buffer.Count == 0)
                    break;

                // タイムアウト付きで待つ (Stop時にデッドロックしないように)
                if (!_buffer.TryTake(out var chunk, TimeSpan.FromMilliseconds(200)))
                {
                    if (_done)
                        break;

                    // データがまだ来ていないが、ストリームは終了していない
                    // 既に一部読んでいればそれを返す、そうでなければ待ち続ける
                    if (totalRead > 0)
                        break;
                    continue;
                }

                _currentChunk = chunk;
                _currentOffset = 0;
            }

            int available = _currentChunk.Length - _currentOffset;
            int toCopy = Math.Min(available, count - totalRead);
            Buffer.BlockCopy(_currentChunk, _currentOffset, buffer, offset + totalRead, toCopy);
            _currentOffset += toCopy;
            totalRead += toCopy;
        }

        _position += totalRead;
        return totalRead;
    }

    /// <summary>
    /// ループバックキャプチャ側からオーディオデータを書き込む。
    /// バッファが上限に達した場合は最も古いチャンクを破棄して新しいデータを追加する。
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_done) return;

        var data = new byte[count];
        Buffer.BlockCopy(buffer, offset, data, 0, count);

        if (!_buffer.TryAdd(data, TimeSpan.FromMilliseconds(50)))
        {
            // バッファが満杯 → 最も古いチャンクを破棄して再試行
            if (_buffer.TryTake(out var dropped))
            {
                Interlocked.Add(ref _droppedBytes, dropped.Length);
                if (Interlocked.Read(ref _droppedBytes) % (dropped.Length * 100) < dropped.Length)
                {
                    AppLogger.Warn($"[SpeechAudioStream] バッファ溢れ: 古いデータを破棄 (累計: {Interlocked.Read(ref _droppedBytes) / 1024}KB)");
                }
            }
            _buffer.TryAdd(data);
        }
    }

    /// <summary>
    /// ストリームの終了を通知する。Read() がこれ以上ブロックしなくなる。
    /// </summary>
    public void Complete()
    {
        _done = true;
        _buffer.CompleteAdding();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _position;
    public override void SetLength(long value) { /* SpeechRecognitionEngine が呼び出すので無視する */ }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _done = true;
            // Read/Write スレッドを安全にアンブロックしてから破棄
            try { _buffer.CompleteAdding(); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }
}
