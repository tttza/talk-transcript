using System.Collections.Concurrent;

namespace TalkTranscript.Audio;

/// <summary>
/// WASAPI ループバックキャプチャと System.Speech.Recognition.SpeechRecognitionEngine を
/// 橋渡しするスレッドセーフなストリーム。
/// ループバック側が Write() でオーディオデータを書き込み、
/// SpeechRecognitionEngine が Read() で読み取る。
/// </summary>
public sealed class SpeechAudioStream : Stream
{
    private readonly BlockingCollection<byte[]> _buffer = new();
    private byte[] _currentChunk = Array.Empty<byte>();
    private int _currentOffset;
    private long _position;
    private volatile bool _done;

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
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_done) return;

        var data = new byte[count];
        Buffer.BlockCopy(buffer, offset, data, 0, count);
        _buffer.Add(data);
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
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }
}
