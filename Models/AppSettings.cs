using System.Text.Json;

namespace TalkTranscript.Models;

/// <summary>
/// アプリケーション設定 (選択したデバイス情報を永続化する)。
/// </summary>
public class AppSettings
{
    /// <summary>マイクデバイスの ID</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>マイクデバイスの表示名 (確認用)</summary>
    public string? MicrophoneDeviceName { get; set; }

    /// <summary>スピーカーデバイスの ID</summary>
    public string? SpeakerDeviceId { get; set; }

    /// <summary>スピーカーデバイスの表示名 (確認用)</summary>
    public string? SpeakerDeviceName { get; set; }

    /// <summary>認識エンジン名 (vosk / sapi / whisper-tiny / whisper-base など)</summary>
    public string? EngineName { get; set; }

    /// <summary>Whisper で GPU を使用するか (CUDA ランタイム導入時のみ有効)</summary>
    public bool UseGpu { get; set; } = true;

    // ── 永続化 ──

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>設定ファイルのパス (%APPDATA%\TalkTranscript\settings.json)</summary>
    public static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TalkTranscript");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    /// <summary>設定を JSON ファイルから読み込む。ファイルがなければ新規インスタンスを返す。</summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                       ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"設定ファイルの読み込みに失敗しました: {ex.Message}");
        }

        return new AppSettings();
    }

    /// <summary>設定を JSON ファイルに保存する。</summary>
    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"設定ファイルの保存に失敗しました: {ex.Message}");
        }
    }
}
