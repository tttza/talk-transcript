using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace TalkTranscript.Audio;

/// <summary>
/// MMDevice → WaveIn デバイス番号のマッピングなど、
/// 複数のトランスクライバで共有するデバイスユーティリティ。
/// </summary>
internal static class DeviceHelper
{
    /// <summary>
    /// MMDevice の FriendlyName から WaveIn デバイス番号を検索する。
    /// WaveIn の ProductName は最大31文字に切り詰められるため、前方一致で比較する。
    /// </summary>
    /// <param name="mmDevice">対象の MMDevice</param>
    /// <param name="tag">ログ出力用のタグ (例: "Whisper", "Vosk")</param>
    /// <returns>WaveIn デバイス番号 (見つからない場合は 0)</returns>
    public static int FindWaveInDevice(MMDevice mmDevice, string tag = "Audio")
    {
        string targetName = mmDevice.FriendlyName;

        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            string prodName = caps.ProductName;

            if (string.IsNullOrEmpty(prodName)) continue;

            if (targetName.StartsWith(prodName, StringComparison.OrdinalIgnoreCase) ||
                prodName.StartsWith(targetName[..Math.Min(targetName.Length, prodName.Length)],
                                    StringComparison.OrdinalIgnoreCase))
            {
                Logging.AppLogger.Info($"[{tag}] WaveIn デバイス #{i}: {prodName} (マッチ)");
                return i;
            }
        }

        Logging.AppLogger.Warn($"[{tag}] WaveIn デバイスが名前で一致しません。デフォルトを使用します。");
        return 0;
    }
}
