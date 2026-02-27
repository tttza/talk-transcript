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
    // モデルサイズに応じたファイル名を生成する
    private static string WhisperModelFileName(string size) =>
        size == "large" ? "ggml-large-v3.bin" : $"ggml-{size}.bin";

    /// <summary>Vosk モデルのディレクトリパスを返す。未ダウンロードなら null。</summary>
    public static string? GetVoskModelPath()
    {
        string modelPath = Path.Combine(ModelsDir, VoskModelName);
        return Directory.Exists(modelPath) ? modelPath : null;
    }

    /// <summary>Whisper モデルのファイルパスを返す (既定: base)。未ダウンロードなら null。</summary>
    public static string? GetWhisperModelPath(string size = "base")
    {
        string modelPath = Path.Combine(ModelsDir, WhisperModelFileName(size));
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
    /// Whisper GGML モデルをダウンロードする。
    /// tiny (~39MB), base (~142MB), small (~466MB), medium (~1.5GB), large (~3.1GB)
    /// </summary>
    public static async Task<string> EnsureWhisperModelAsync(string size = "base")
    {
        string fileName = WhisperModelFileName(size);
        string modelPath = Path.Combine(ModelsDir, fileName);
        if (File.Exists(modelPath))
        {
            Console.WriteLine($"[モデル] Whisper {size} モデル: {modelPath} (キャッシュ済み)");
            return modelPath;
        }

        Directory.CreateDirectory(ModelsDir);

        // Hugging Face から直接ダウンロード (進捗表示付き)
        string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{fileName}";

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[モデル] Whisper {size} モデルをダウンロード中...");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  URL: {url}");
        Console.ResetColor();

        string tempPath = modelPath + ".tmp";

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromHours(1); // large モデルは時間がかかる

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = File.Create(tempPath);

        var buffer = new byte[131072]; // 128KB バッファ
        long totalRead = 0;
        int read;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while ((read = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            totalRead += read;

            // 進捗表示 (500ms ごと)
            if (sw.ElapsedMilliseconds > 500 || read == 0)
            {
                sw.Restart();
                double mbRead = totalRead / (1024.0 * 1024.0);
                if (totalBytes.HasValue && totalBytes > 0)
                {
                    double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                    int pct = (int)(totalRead * 100 / totalBytes.Value);
                    Console.Write($"\r  [{pct,3}%] {mbRead:F1} / {mbTotal:F0} MB   ");
                }
                else
                {
                    Console.Write($"\r  {mbRead:F1} MB   ");
                }
            }
        }
        Console.WriteLine();

        fileStream.Close();
        File.Move(tempPath, modelPath, overwrite: true);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[モデル] Whisper モデルの準備完了: {modelPath}");
        Console.ResetColor();

        return modelPath;
    }
}
