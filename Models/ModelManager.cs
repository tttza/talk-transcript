using System.IO.Compression;
using System.Net.Http;
using TalkTranscript.Translation;

namespace TalkTranscript.Models;

/// <summary>
/// Vosk / Whisper の音声認識モデルのダウンロードと管理を行う。
/// モデルは %LOCALAPPDATA%\TalkTranscript\Models\ に保存する。
/// </summary>
public static class ModelManager
{
    private static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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
            Logging.AppLogger.Info($"Vosk モデル: {modelPath} (キャッシュ済み)");
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
            Logging.AppLogger.Info($"Whisper {size} モデル: {modelPath} (キャッシュ済み)");
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

    // ── 翻訳モデル ──

    /// <summary>翻訳モデルのディレクトリ名を返す</summary>
    private static string TranslationModelDirName(string sourceLang, string targetLang) =>
        $"translation-{sourceLang}-{targetLang}";

    /// <summary>翻訳モデルのディレクトリパスを返す。未ダウンロードなら null。</summary>
    public static string? GetTranslationModelPath(string sourceLang, string targetLang)
    {
        string dir = Path.Combine(ModelsDir, TranslationModelDirName(sourceLang, targetLang));
        if (!Directory.Exists(dir)) return null;
        if (!File.Exists(Path.Combine(dir, "encoder_model.onnx"))) return null;
        if (!File.Exists(Path.Combine(dir, "decoder_model.onnx"))) return null;
        if (!File.Exists(Path.Combine(dir, "decoder_with_past_model.onnx"))) return null;
        if (!File.Exists(Path.Combine(dir, "vocab.json"))) return null;

        // モデルソースの整合性チェック (別リポのキャッシュが残っている場合に再取得)
        var info = LanguagePairs.GetInfo(sourceLang, targetLang);
        if (info != null)
        {
            string markerPath = Path.Combine(dir, ".model_source");
            if (File.Exists(markerPath))
            {
                string cached = File.ReadAllText(markerPath).Trim();
                if (!string.Equals(cached, info.HuggingFaceRepo, StringComparison.OrdinalIgnoreCase))
                {
                    Logging.AppLogger.Info($"翻訳モデルソース変更検出: {cached} → {info.HuggingFaceRepo}。再取得します。");
                    try { Directory.Delete(dir, true); } catch { }
                    return null;
                }
            }
        }

        return dir;
    }

    /// <summary>
    /// 翻訳モデル (Helsinki-NLP Opus-MT ONNX) をダウンロードする。
    /// ONNX 変換済みリポからはファイルを直接取得する。
    /// PyTorch のみのモデルは Optimum CLI で ONNX に変換する。
    /// </summary>
    public static async Task<string> EnsureTranslationModelAsync(string sourceLang, string targetLang)
    {
        string dirName = TranslationModelDirName(sourceLang, targetLang);
        string modelDir = Path.Combine(ModelsDir, dirName);

        // キャッシュ確認
        if (GetTranslationModelPath(sourceLang, targetLang) != null)
        {
            Logging.AppLogger.Info($"翻訳モデル: {modelDir} (キャッシュ済み)");
            return modelDir;
        }

        // 言語ペア確認
        var info = LanguagePairs.GetInfo(sourceLang, targetLang);
        if (info == null)
            throw new NotSupportedException($"未サポートの言語ペア: {sourceLang} → {targetLang}");

        Directory.CreateDirectory(modelDir);

        if (info.NeedsOnnxConversion)
        {
            // PyTorch → ONNX 変換が必要
            await ConvertModelToOnnxAsync(info, sourceLang, targetLang, modelDir);
        }
        else
        {
            // ONNX 変換済みリポから直接ダウンロード
            await DownloadOnnxModelAsync(info, sourceLang, targetLang, modelDir);
        }

        return modelDir;
    }

    /// <summary>ONNX 変換済みリポから翻訳モデルファイルをダウンロードする</summary>
    private static async Task DownloadOnnxModelAsync(
        LanguagePairInfo info, string sourceLang, string targetLang, string modelDir)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[翻訳モデル] {sourceLang}→{targetLang} モデルをダウンロード中...");
        Console.ResetColor();

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TalkTranscript/1.0");

        foreach (string file in LanguagePairs.RequiredFiles)
        {
            string filePath = Path.Combine(modelDir, file);
            if (File.Exists(filePath))
            {
                Console.WriteLine($"  ✓ {file} (既存)");
                continue;
            }

            // ファイル URL を構築
            // onnx-community リポは onnx/ サブディレクトリに ONNX ファイルがある
            // それ以外のリポ (tttza/* 等) は全ファイルがルートにある
            string url;
            bool isOnnxCommunity = info.HuggingFaceRepo.StartsWith("onnx-community/", StringComparison.OrdinalIgnoreCase);
            if (file.EndsWith(".onnx") && isOnnxCommunity)
                url = $"https://huggingface.co/{info.HuggingFaceRepo}/resolve/main/onnx/{file}";
            else
                url = $"https://huggingface.co/{info.HuggingFaceRepo}/resolve/main/{file}";

            string tempPath = filePath + ".tmp";
            Console.Write($"  ↓ {file}...");

            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (!response.IsSuccessStatusCode)
                {
                    // ONNX ファイルが onnx/ にない場合、ルートを試す
                    if (file.EndsWith(".onnx"))
                    {
                        url = $"https://huggingface.co/{info.HuggingFaceRepo}/resolve/main/{file}";
                        response.Dispose();
                        using var retryResponse = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        retryResponse.EnsureSuccessStatusCode();
                        await DownloadFile(retryResponse, tempPath);
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                    }
                }
                else
                {
                    await DownloadFile(response, tempPath);
                }

                File.Move(tempPath, filePath, overwrite: true);
                Console.WriteLine(" OK");
            }
            catch (HttpRequestException ex)
            {
                // 一時ファイルのクリーンアップ
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                Console.WriteLine($" スキップ ({ex.StatusCode})");
                Logging.AppLogger.Warn($"翻訳モデルファイル取得失敗: {file} - {ex.Message}");
                // tokenizer_config.json は必須ではない
                if (file != "tokenizer_config.json")
                {
                    // 必須ファイルがダウンロードできない場合はエラー
                    try { Directory.Delete(modelDir, true); } catch { }
                    throw new InvalidOperationException($"翻訳モデルのダウンロードに失敗: {file}", ex);
                }
            }
            catch (Exception ex)
            {
                // 一時ファイルのクリーンアップ
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[翻訳モデル] 準備完了: {modelDir}");
        Console.ResetColor();

        // モデルソースを記録 (キャッシュ整合性チェック用)
        WriteModelSourceMarker(modelDir, info.HuggingFaceRepo);
    }

    /// <summary>
    /// PyTorch モデルを Python Optimum CLI で ONNX に変換する。
    /// 初回のみ変換が必要で、結果はキャッシュされる。
    /// </summary>
    private static async Task ConvertModelToOnnxAsync(
        LanguagePairInfo info, string sourceLang, string targetLang, string modelDir)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[翻訳モデル] {sourceLang}→{targetLang} モデルを ONNX に変換します (初回のみ)...");
        Console.ResetColor();

        // Python の存在確認
        string? pythonCmd = FindPython();
        if (pythonCmd == null)
        {
            try { Directory.Delete(modelDir, true); } catch { }
            throw new InvalidOperationException(
                $"{sourceLang}→{targetLang} の翻訳モデル ({info.HuggingFaceRepo}) は ONNX 変換が必要です。\n" +
                "  Python がインストールされていないため変換できません。\n" +
                "  Python 3.9 以降をインストールして PATH に追加してください。\n" +
                "  https://www.python.org/downloads/");
        }

        // optimum[exporters] のインストール確認 & インストール
        Console.WriteLine("  Python パッケージを確認中...");
        if (!await IsOptimumInstalled(pythonCmd))
        {
            Console.WriteLine("  optimum[exporters] をインストール中 (初回のみ)...");
            var installResult = await RunProcessAsync(pythonCmd,
                "-m pip install --quiet optimum[exporters] transformers sentencepiece protobuf",
                timeoutSeconds: 300);
            if (installResult.ExitCode != 0)
            {
                try { Directory.Delete(modelDir, true); } catch { }
                throw new InvalidOperationException(
                    $"optimum のインストールに失敗しました:\n{installResult.StdErr}");
            }
            Console.WriteLine("  ✓ optimum[exporters] インストール完了");
        }

        // Optimum CLI で ONNX 変換
        Console.WriteLine($"  {info.HuggingFaceRepo} → ONNX 変換中 (数分かかる場合があります)...");
        var convertResult = await RunProcessAsync(pythonCmd,
            $"-m optimum.exporters.onnx --model {info.HuggingFaceRepo} " +
            $"--task text2text-generation-with-past \"{modelDir}\"",
            timeoutSeconds: 600);

        if (convertResult.ExitCode != 0)
        {
            Logging.AppLogger.Error($"ONNX 変換失敗:\n{convertResult.StdErr}");
            try { Directory.Delete(modelDir, true); } catch { }
            throw new InvalidOperationException(
                $"ONNX 変換に失敗しました。詳細はログを確認してください。\n{convertResult.StdErr}");
        }

        // Optimum は config.json 等もコピーするが、
        // encoder/decoder の ONNX ファイルがサブフォルダに出る場合に対応
        MoveOnnxFilesToRoot(modelDir);

        // 必須ファイルの確認
        foreach (string file in LanguagePairs.RequiredFiles)
        {
            if (file == "tokenizer_config.json") continue; // オプショナル
            if (!File.Exists(Path.Combine(modelDir, file)))
            {
                Logging.AppLogger.Error($"ONNX 変換後に {file} が見つかりません (dir: {modelDir})");
                try { Directory.Delete(modelDir, true); } catch { }
                throw new InvalidOperationException(
                    $"ONNX 変換後に必要なファイル {file} が生成されませんでした。");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[翻訳モデル] ONNX 変換完了: {modelDir}");
        Console.ResetColor();

        // モデルソースを記録 (キャッシュ整合性チェック用)
        WriteModelSourceMarker(modelDir, info.HuggingFaceRepo);
    }

    /// <summary>ONNX ファイルが onnx/ サブフォルダにある場合はルートに移動する</summary>
    private static void MoveOnnxFilesToRoot(string modelDir)
    {
        string onnxSubDir = Path.Combine(modelDir, "onnx");
        if (!Directory.Exists(onnxSubDir)) return;

        foreach (var file in Directory.GetFiles(onnxSubDir, "*.onnx"))
        {
            string dest = Path.Combine(modelDir, Path.GetFileName(file));
            if (!File.Exists(dest))
                File.Move(file, dest);
        }
        // サブフォルダが空なら削除
        try
        {
            if (Directory.GetFiles(onnxSubDir).Length == 0)
                Directory.Delete(onnxSubDir, true);
        }
        catch { }
    }

    /// <summary>Python コマンドを探す (python / python3 / py)</summary>
    private static string? FindPython()
    {
        foreach (var cmd in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(cmd, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit(5000);
                if (proc?.ExitCode == 0) return cmd;
            }
            catch { }
        }
        return null;
    }

    /// <summary>optimum がインストール済みか確認する</summary>
    private static async Task<bool> IsOptimumInstalled(string pythonCmd)
    {
        var result = await RunProcessAsync(pythonCmd,
            "-c \"import optimum; import transformers\"", timeoutSeconds: 15);
        return result.ExitCode == 0;
    }

    /// <summary>外部プロセスを実行して結果を返す</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string command, string arguments, int timeoutSeconds = 60)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(command, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // pip install 時の文字化けを防ぐ
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var proc = System.Diagnostics.Process.Start(psi)
                         ?? throw new InvalidOperationException($"プロセス起動失敗: {command}");

        var stdOutTask = proc.StandardOutput.ReadToEndAsync();
        var stdErrTask = proc.StandardError.ReadToEndAsync();

        bool exited = proc.WaitForExit(timeoutSeconds * 1000);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "", $"タイムアウト ({timeoutSeconds}秒)");
        }

        return (proc.ExitCode, await stdOutTask, await stdErrTask);
    }


    private static async Task DownloadFile(HttpResponseMessage response, string path)
    {
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = File.Create(path);
        await contentStream.CopyToAsync(fileStream);
    }

    /// <summary>モデルソースを記録するマーカーファイルを書き込む</summary>
    private static void WriteModelSourceMarker(string modelDir, string repoName)
    {
        try
        {
            File.WriteAllText(Path.Combine(modelDir, ".model_source"), repoName);
        }
        catch (Exception ex)
        {
            Logging.AppLogger.Warn($"モデルソースマーカー書き込み失敗: {ex.Message}");
        }
    }
}
