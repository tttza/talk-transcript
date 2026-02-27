using NAudio.CoreAudioApi;
using TalkTranscript.Models;

namespace TalkTranscript.Audio;

/// <summary>
/// オーディオデバイスを列挙し、ユーザーに番号で選ばせるヘルパー。
/// 前回選択を AppSettings から復元し、変更があれば保存する。
/// </summary>
public static class DeviceSelector
{
    /// <summary>
    /// 前回保存されたデバイスを直接読み込む (選択UIをスキップ)。
    /// デバイスが見つからない場合は例外を投げる。
    /// </summary>
    public static (MMDevice Microphone, MMDevice Speaker) LoadSavedDevices(AppSettings settings)
    {
        using var enumerator = new MMDeviceEnumerator();

        var mics = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var speakers = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

        var mic = mics.FirstOrDefault(d => d.ID == settings.MicrophoneDeviceId)
                  ?? throw new InvalidOperationException("保存されたマイクデバイスが見つかりません。");
        var speaker = speakers.FirstOrDefault(d => d.ID == settings.SpeakerDeviceId)
                      ?? throw new InvalidOperationException("保存されたスピーカーデバイスが見つかりません。");

        return (mic, speaker);
    }

    /// <summary>
    /// マイクとスピーカーのデバイスを選択させる。
    /// 前回の設定がありデバイスが存在すればそれを提示し、Enter で再利用できる。
    /// </summary>
    /// <returns>(マイクデバイス, スピーカーデバイス)</returns>
    public static (MMDevice Microphone, MMDevice Speaker) SelectDevices(AppSettings settings)
    {
        using var enumerator = new MMDeviceEnumerator();

        // ── マイク選択 ──
        Console.WriteLine("── マイクデバイスを選択してください ──");
        var mic = SelectDevice(
            enumerator,
            DataFlow.Capture,
            settings.MicrophoneDeviceId,
            settings.MicrophoneDeviceName);

        settings.MicrophoneDeviceId = mic.ID;
        settings.MicrophoneDeviceName = mic.FriendlyName;
        Console.WriteLine();

        // ── スピーカー選択 ──
        Console.WriteLine("── スピーカーデバイスを選択してください ──");
        var speaker = SelectDevice(
            enumerator,
            DataFlow.Render,
            settings.SpeakerDeviceId,
            settings.SpeakerDeviceName);

        settings.SpeakerDeviceId = speaker.ID;
        settings.SpeakerDeviceName = speaker.FriendlyName;
        Console.WriteLine();

        // 設定を保存
        settings.Save();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"設定を保存しました: {AppSettings.FilePath}");
        Console.ResetColor();

        return (mic, speaker);
    }

    private static MMDevice SelectDevice(
        MMDeviceEnumerator enumerator,
        DataFlow dataFlow,
        string? savedDeviceId,
        string? savedDeviceName)
    {
        var devices = enumerator
            .EnumerateAudioEndPoints(dataFlow, DeviceState.Active)
            .ToList();

        if (devices.Count == 0)
        {
            var kind = dataFlow == DataFlow.Capture ? "マイク" : "スピーカー";
            throw new InvalidOperationException(
                $"利用可能な{kind}デバイスが見つかりませんでした。");
        }

        // 前回選択のインデックスを探す
        int? savedIndex = null;
        if (!string.IsNullOrEmpty(savedDeviceId))
        {
            savedIndex = devices.FindIndex(d => d.ID == savedDeviceId);
            if (savedIndex < 0) savedIndex = null;
        }

        // デバイス一覧を表示
        for (int i = 0; i < devices.Count; i++)
        {
            var marker = (savedIndex == i) ? " ★前回" : "";
            Console.WriteLine($"  {i + 1}. {devices[i].FriendlyName}{marker}");
        }

        // 前回の選択がある場合のプロンプト
        if (savedIndex.HasValue)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"番号を入力 (Enter で前回のまま [{savedIndex.Value + 1}. {savedDeviceName}]): ");
            Console.ResetColor();
        }
        else
        {
            Console.Write("番号を入力: ");
        }

        var input = Console.ReadLine()?.Trim();

        // Enter のみ → 前回の選択を使用
        if (string.IsNullOrEmpty(input))
        {
            if (savedIndex.HasValue)
            {
                var device = devices[savedIndex.Value];
                Console.WriteLine($"  → {device.FriendlyName}");
                return device;
            }

            // 前回なし & 入力なし → デバイスが1つなら自動選択
            if (devices.Count == 1)
            {
                Console.WriteLine($"  → {devices[0].FriendlyName} (自動選択)");
                return devices[0];
            }
        }

        // 番号で選択
        if (int.TryParse(input, out int num) && num >= 1 && num <= devices.Count)
        {
            var device = devices[num - 1];
            Console.WriteLine($"  → {device.FriendlyName}");
            return device;
        }

        // 不正入力 → 前回があればそれ、なければ最初のデバイス
        if (savedIndex.HasValue)
        {
            var device = devices[savedIndex.Value];
            Console.WriteLine($"  → {device.FriendlyName} (前回の選択)");
            return device;
        }

        Console.WriteLine($"  → {devices[0].FriendlyName} (デフォルト)");
        return devices[0];
    }
}
