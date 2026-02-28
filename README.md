# TalkTranscript

Windows 上でマイクとスピーカーの音声をリアルタイムに文字起こしするコンソールアプリケーションです。通話や会議の内容を自動でテキストファイルに記録します。

## 主な機能

- **リアルタイム文字起こし** — マイク (自分) とスピーカー (相手) を同時にキャプチャし、話者を区別して記録
- **複数エンジン対応** — 環境に応じて最適なエンジンを選択可能
  - **Vosk** — 軽量・リアルタイム (日本語モデル ~48MB)
  - **Whisper** (tiny / base / small / medium / large) — OpenAI Whisper ベースの高精度認識
  - **SAPI** — Windows 標準の音声認識 (追加ダウンロード不要)
- **CUDA GPU アクセラレーション** — NVIDIA GPU 搭載環境で Whisper の推論を高速化
- **ハードウェア自動検出** — GPU / CPU / RAM を検出し、最適なエンジンとモデルサイズを自動推奨
- **複数出力フォーマット** — テキスト (デフォルト) に加え SRT / JSON / Markdown で出力可能
- **対話型設定メニュー** — エンジン・デバイス・言語・出力先などを GUI ライクに設定
- **ブックマーク機能** — 録音中にキー操作で重要箇所をマーク
- **音量メーター** — マイク / スピーカーの入力レベルをリアルタイム表示
- **録音保存** — 音声データを WAV ファイルとして保存するオプション
- **Whisper 後処理** — Vosk / SAPI で録音した音声を Whisper で再認識し精度向上

## 動作環境

- **OS:** Windows 10 / 11 (x64)
- **ランタイム:** .NET 8.0
- **GPU (任意):** NVIDIA GPU + CUDA Toolkit 13 (Whisper GPU モード使用時)

## インストール

### ビルド済みバイナリ

`publish-x64/` または `publish-arm64/` ディレクトリのファイルを任意の場所にコピーして実行してください。

### ソースからビルド

```powershell
dotnet build -c Release
```

初回実行時に選択したエンジンの音声認識モデルが自動的にダウンロードされます。  
モデルは `%APPDATA%\TalkTranscript\Models\` に保存されます。

## 使い方

### 基本的な実行

```powershell
# デフォルト設定で起動 (環境に応じてエンジンを自動選択)
TalkTranscript.exe

# エンジンを指定して起動
TalkTranscript.exe --engine whisper-base

# CPU モードを強制 (GPU を使わない)
TalkTranscript.exe --engine whisper-base --cpu

# 認識言語を指定
TalkTranscript.exe --lang en
```

### コマンドライン引数

| 引数 | 説明 |
|------|------|
| `--engine <name>` | 認識エンジンを指定 (`vosk` / `whisper-tiny` / `whisper-base` / `whisper-small` / `whisper-medium` / `whisper-large` / `sapi`) |
| `--cpu` | GPU を使わず CPU モードで動作 |
| `--lang <code>` | 認識言語 (`ja` / `en` / `auto` など) |
| `--config` | 設定メニューを開く |
| `--whisper-only [size]` | Whisper モデルのダウンロードのみ実行 (デフォルト: `base`) |
| `--diag` | 診断モードで環境情報を表示 |
| `--test` | テストモード (短時間で自動停止) |

### 録音中のキー操作

録音中は以下のキー操作が使用できます:

- **Ctrl+Q** — 録音を停止して終了
- **Ctrl+D / Ctrl+E** — 録音を一時停止し設定変更画面へ
- **ブックマーク** — 重要な箇所にマークを挿入

### 設定メニュー

`--config` オプションまたは録音中の設定変更で以下の項目を変更できます:

1. エンジン選択
2. GPU (CUDA) の有効化 / 無効化
3. 認識言語
4. 出力ディレクトリ
5. 出力フォーマット (SRT / JSON / Markdown)
6. 入出力デバイス
7. 録音保存の有無
8. リソース制御 (CPU スレッド数 / プロセス優先度)

設定は `%APPDATA%\TalkTranscript\settings.json` に保存されます。

## 出力

文字起こし結果は `Transcripts/` ディレクトリ (または設定で指定したディレクトリ) に保存されます。

```
Transcripts/
  transcript_20260228_143000.txt     # テキスト (常に出力)
  transcript_20260228_143000.srt     # SRT 字幕 (オプション)
  transcript_20260228_143000.json    # JSON (オプション)
  transcript_20260228_143000.md      # Markdown (オプション)
```

### テキスト出力例

```
通話記録 - 2026/02/28 14:30
============================================================

[14:30:05] 自分: お疲れ様です。
[14:30:08] 相手: お疲れ様です。本日の議題ですが...

  ★ [14:35:12] ブックマーク: ブックマーク

[14:35:15] 自分: 了解しました。

============================================================
通話時間: 00:10:23  |  自分: 15件  |  相手: 12件
```

## プロジェクト構成

```
├── Program.cs                # エントリポイント・メインループ
├── SpectreUI.cs              # Spectre.Console による Live UI
├── ConfigMenu.cs             # 対話型設定メニュー
├── EngineSelector.cs         # エンジン選択 UI
├── HardwareInfo.cs           # GPU/CPU 環境検出
├── CudaHelper.cs             # CUDA ランタイム設定
├── TranscriptWriter.cs       # テキスト出力 (リアルタイム追記)
├── Audio/
│   ├── AudioProcessing.cs    # 音声前処理 (リサンプリング等)
│   ├── AdaptiveNoiseGate.cs  # 適応型ノイズゲート
│   ├── DeviceSelector.cs     # オーディオデバイス選択
│   ├── RecordingBuffer.cs    # 録音バッファ
│   └── SpeechAudioStream.cs  # 音声ストリーム
├── Models/
│   ├── AppSettings.cs        # アプリケーション設定
│   ├── ModelManager.cs       # モデルダウンロード管理
│   └── TranscriptEntry.cs    # 文字起こしエントリ
├── Output/
│   ├── OutputFormat.cs       # 出力フォーマット定義
│   ├── TranscriptExporter.cs # エクスポートオーケストレータ
│   ├── SrtWriter.cs          # SRT 出力
│   ├── JsonTranscriptWriter.cs # JSON 出力
│   └── MarkdownWriter.cs     # Markdown 出力
├── Transcribers/
│   ├── ICallTranscriber.cs   # トランスクライバ共通インターフェース
│   ├── VoskCallTranscriber.cs    # Vosk エンジン実装
│   ├── WhisperCallTranscriber.cs # Whisper エンジン実装
│   ├── SapiCallTranscriber.cs    # SAPI エンジン実装
│   ├── WhisperPostProcessor.cs   # Whisper 後処理
│   └── WhisperTextFilter.cs      # Whisper 出力フィルタ
└── Tests/                    # ユニットテスト
```

## 使用ライブラリ

| パッケージ | 用途 |
|-----------|------|
| [NAudio](https://github.com/naudio/NAudio) | オーディオキャプチャ (マイク / スピーカー) |
| [Vosk](https://alphacephei.com/vosk/) | オフライン音声認識エンジン |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | OpenAI Whisper の .NET バインディング |
| [Spectre.Console](https://spectreconsole.net/) | リッチなコンソール UI |
| System.Speech | Windows SAPI 音声認識 |
| System.Management | ハードウェア情報取得 |

## テスト

```powershell
cd Tests
dotnet test
```

## ライセンス

このプロジェクトのライセンスについてはリポジトリのライセンスファイルを参照してください。
