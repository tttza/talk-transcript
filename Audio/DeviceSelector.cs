using NAudio.CoreAudioApi;
using Spectre.Console;
using TalkTranscript.Models;

namespace TalkTranscript.Audio;

/// <summary>
/// オーディオデバイスを列挙し、ユーザーにカーソルで選ばせるヘルパー。
/// Spectre.Console の SelectionPrompt で ↑↓ カーソル選択に対応。
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
    /// 前回の設定がありデバイスが存在すればそれをデフォルトにする。
    /// </summary>
    /// <returns>(マイクデバイス, スピーカーデバイス)</returns>
    public static (MMDevice Microphone, MMDevice Speaker) SelectDevices(AppSettings settings)
    {
        using var enumerator = new MMDeviceEnumerator();

        // ── マイク選択 ──
        AnsiConsole.MarkupLine("  [cyan]── マイクデバイスを選択 ──[/]");
        var mic = SelectDevice(
            enumerator,
            DataFlow.Capture,
            settings.MicrophoneDeviceId,
            settings.MicrophoneDeviceName);

        settings.MicrophoneDeviceId = mic.ID;
        settings.MicrophoneDeviceName = mic.FriendlyName;
        AnsiConsole.WriteLine();

        // ── スピーカー選択 ──
        AnsiConsole.MarkupLine("  [cyan]── スピーカーデバイスを選択 ──[/]");
        var speaker = SelectDevice(
            enumerator,
            DataFlow.Render,
            settings.SpeakerDeviceId,
            settings.SpeakerDeviceName);

        settings.SpeakerDeviceId = speaker.ID;
        settings.SpeakerDeviceName = speaker.FriendlyName;
        AnsiConsole.WriteLine();

        // 設定を保存
        settings.Save();
        AnsiConsole.MarkupLine($"  [dim]設定を保存しました: {Markup.Escape(AppSettings.FilePath)}[/]");

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

        // デバイスが1つしかない場合は自動選択
        if (devices.Count == 1)
        {
            AnsiConsole.MarkupLine($"  [green]→ {Markup.Escape(devices[0].FriendlyName)} (自動選択)[/]");
            return devices[0];
        }

        // 前回選択のインデックスを探す
        int? savedIndex = null;
        if (!string.IsNullOrEmpty(savedDeviceId))
        {
            savedIndex = devices.FindIndex(d => d.ID == savedDeviceId);
            if (savedIndex < 0) savedIndex = null;
        }

        // 選択肢を構築
        var choices = devices.Select((d, i) =>
        {
            string marker = (savedIndex == i) ? " ★前回" : "";
            return $"{Markup.Remove(d.FriendlyName)}{marker}";
        }).ToList();

        // 前回選択が先頭に来るように並び替え
        var orderedChoices = new List<string>(choices);
        if (savedIndex.HasValue)
        {
            var prev = orderedChoices[savedIndex.Value];
            orderedChoices.RemoveAt(savedIndex.Value);
            orderedChoices.Insert(0, prev);
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]  ↑↓で移動, Enterで確定:[/]")
                .PageSize(10)
                .HighlightStyle(Style.Parse("bold cyan"))
                .AddChoices(orderedChoices));
        int idx = choices.IndexOf(selected);
        if (idx >= 0 && idx < devices.Count)
        {
            AnsiConsole.MarkupLine($"  [green]→ {Markup.Escape(devices[idx].FriendlyName)}[/]");
            return devices[idx];
        }

        // フォールバック: 前回 or 最初
        if (savedIndex.HasValue)
            return devices[savedIndex.Value];

        return devices[0];
    }
}
