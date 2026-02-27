using System.IO.Compression;
using System.Net.Http;

namespace TalkTranscript.Models;

/// <summary>
/// Vosk / Whisper の音声認識モデルのダウンロードと管理を行う。
/// モデルは %APPDATA%\TalkTranscript\Models\ に保存する。
/// </summary>
public static class ModelManager
{
    private static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TalkTranscript", "Models");

    // ── Vosk ──
    private const string VoskModelName = "vosk-model-small-ja-0.22";
    private const string VoskModelUrl =
        "https://alphacephei.com/vosk/models/vosk-model-small-ja-0.22.zip";

    // ── Whisper ──
    private const string WhisperModelFileName = "ggml-base.bin";

    /// <summary>Vosk モデルのディレクトリパスを返す。未ダウンロードなら null。</summary>
    public static string? GetVoskModelPath()
    {
        string modelPath = Path.Combine(ModelsDir, VoskModelName);
        return Directory.Exists(modelPath) ? modelPath : null;
    }

    /// <summary>Whisper モデルのファイルパスを返す。未ダウンロードなら null。</summary>
    public static string? GetWhisperModelPath()
    {
        string modelPath = Path.Combine(ModelsDir, WhisperModelFileName);
        return File.Exists(modelPath) ? modelPath : null;
    }

    /// <summary>
    /// Vosk 日本語モデルをダウンロードして展開する (~48MB)。
    /// </summary>
    public static async Task<string> EnsureVoskModelAsync()
    {
        string modelPath = Path.Combine(ModelsDir, VoskModelName);
        if (Directory.Exists(modelPath))
        {
            Console.WriteLine($"[モデル] Vosk モデル: {modelPath} (キャッシュ済み)");
            return modelPath;
        }

        Directory.CreateDirectory(ModelsDir);
        string zipPath = Path.Combine(ModelsDir, $"{VoskModelName}.zip");

        Console.WriteLine($"[モデル] Vosk 日本語モデルをダウンロード中...");
        Console.WriteLine($"[モデル]   URL: {VoskModelUrl}");
        Console.WriteLine($"[モデル]   保存先: {ModelsDir}");

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        // ダウンロード (進捗表示付き)
        using (var response = await http.GetAsync(VoskModelUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long? totalBytes = response.Content.Headers.ContentLength;

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(zipPath);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;

            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                totalRead += read;

                if (totalBytes.HasValue)
                {
                    int pct = (int)(totalRead * 100 / totalBytes.Value);
                    Console.Write($"\r[モデル]   ダウンロード: {totalRead / 1024 / 1024}MB / {totalBytes.Value / 1024 / 1024}MB ({pct}%)   ");
                }
                else
                {
                    Console.Write($"\r[モデル]   ダウンロード: {totalRead / 1024 / 1024}MB   ");
                }
            }
            Console.WriteLine();
        }

        // 展開
        Console.WriteLine("[モデル]   展開中...");
        ZipFile.ExtractToDirectory(zipPath, ModelsDir, overwriteFiles: true);
        File.Delete(zipPath);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[モデル] Vosk モデルの準備完了: {modelPath}");
        Console.ResetColor();

        return modelPath;
    }

    /// <summary>
    /// Whisper GGML モデルをダウンロードする (~142MB for base)。
    /// Whisper.net の GgmlType を使用してダウンロードする。
    /// </summary>
    public static async Task<string> EnsureWhisperModelAsync()
    {
        string modelPath = Path.Combine(ModelsDir, WhisperModelFileName);
        if (File.Exists(modelPath))
        {
            Console.WriteLine($"[モデル] Whisper モデル: {modelPath} (キャッシュ済み)");
            return modelPath;
        }

        Directory.CreateDirectory(ModelsDir);

        Console.WriteLine("[モデル] Whisper base モデルをダウンロード中 (~142MB)...");

        // Whisper.net の組み込みダウンローダーを使用
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        var downloader = new Whisper.net.Ggml.WhisperGgmlDownloader(http);
        using var modelStream = await downloader
            .GetGgmlModelAsync(Whisper.net.Ggml.GgmlType.Base);

        using var fileStream = File.Create(modelPath);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await modelStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            totalRead += read;
            Console.Write($"\r[モデル]   ダウンロード: {totalRead / 1024 / 1024}MB   ");
        }
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[モデル] Whisper モデルの準備完了: {modelPath}");
        Console.ResetColor();

        return modelPath;
    }
}
