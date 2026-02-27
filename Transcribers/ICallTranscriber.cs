using TalkTranscript.Models;

namespace TalkTranscript.Transcribers;

/// <summary>
/// 通話文字起こしエンジンの共通インターフェース。
/// SAPI / Vosk など異なるエンジンを切り替えて使用するために定義。
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

    /// <summary>キャプチャと音声認識を開始する</summary>
    void Start();

    /// <summary>キャプチャと音声認識を停止する</summary>
    void Stop();

    /// <summary>マイクの録音済み PCM (16kHz/16bit/mono) を返す (Whisper 後処理用)</summary>
    byte[] GetMicRecording();

    /// <summary>スピーカーの録音済み PCM (16kHz/16bit/mono) を返す (Whisper 後処理用)</summary>
    byte[] GetSpeakerRecording();
}
