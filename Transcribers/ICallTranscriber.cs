using TalkTranscript.Models;

namespace TalkTranscript.Transcribers;

/// <summary>
/// 通話文字起こしエンジンの共通インターフェース。
/// SAPI / Vosk / Whisper など異なるエンジンを切り替えて使用するために定義。
/// </summary>
public interface ICallTranscriber : IDisposable
{
    /// <summary>認識済みの全エントリ</summary>
    IReadOnlyList<TranscriptEntry> Entries { get; }

    /// <summary>マイク認識結果のみ</summary>
    IReadOnlyList<TranscriptEntry> MicEntries { get; }

    /// <summary>スピーカー認識結果のみ</summary>
    IReadOnlyList<TranscriptEntry> SpeakerEntries { get; }

    /// <summary>認識結果が得られたときに発火するイベント</summary>
    event Action<TranscriptEntry>? OnTranscribed;

    /// <summary>音量レベルが更新されたときに発火するイベント (micPeak, speakerPeak: 0-32767)</summary>
    event Action<float, float>? OnVolumeUpdated;

    /// <summary>キャプチャと音声認識を開始する</summary>
    void Start();

    /// <summary>キャプチャと音声認識を停止する</summary>
    void Stop();

    /// <summary>マイクの録音済み PCM (16kHz/16bit/mono) を返す (Whisper 後処理用)</summary>
    byte[] GetMicRecording();

    /// <summary>スピーカーの録音済み PCM (16kHz/16bit/mono) を返す (Whisper 後処理用)</summary>
    byte[] GetSpeakerRecording();

    /// <summary>マイク録音を WAV ファイルとして保存する (ストリーミング方式 — 全体を byte[] にしない)</summary>
    void SaveMicRecordingAsWav(string path);

    /// <summary>スピーカー録音を WAV ファイルとして保存する (ストリーミング方式)</summary>
    void SaveSpeakerRecordingAsWav(string path);

    /// <summary>録音バッファをクリアしてメモリを早期に解放する (後処理不要時に呼ぶ)</summary>
    void ClearRecordings();

    /// <summary>ストリーミング録音を開始する (録音データをメモリに蓄積せずディスクに直接書き出す)</summary>
    /// <param name="micWavPath">マイク録音の WAV パス (null で無効)</param>
    /// <param name="spkWavPath">スピーカー録音の WAV パス (null で無効)</param>
    void StartStreamingRecording(string? micWavPath, string? spkWavPath);

    /// <summary>ストリーミング録音を停止し WAV ヘッダーを確定する</summary>
    void StopStreamingRecording();

    /// <summary>ストリーミング録音が有効かどうか</summary>
    bool IsStreamingRecording { get; }

    /// <summary>マイク録音のバイト数</summary>
    long MicRecordingLength { get; }

    /// <summary>スピーカー録音のバイト数</summary>
    long SpeakerRecordingLength { get; }
}
