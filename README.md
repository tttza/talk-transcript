# TalkTranscript

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/OS-Windows%2010%2F11-0078D4.svg)]()

Windows 上でマイクとスピーカーの音声をリアルタイムに文字起こしするコンソールアプリです。  
通話や会議の内容を話者別に自動記録します。

## 特長

- **話者分離つき文字起こし** — マイク (自分) とスピーカー (相手) を同時にキャプチャし、発言者を区別して記録
- **3 種類の認識エンジン** — Vosk (軽量) / Whisper (高精度・5 サイズ) / SAPI (OS 標準) を環境に応じて自動選択
- **ローカル翻訳** — MarianMT による 11 言語ペアのリアルタイム翻訳 (クラウド不要)
- **GPU 自動検出** — CUDA / Vulkan を検出し、最適なバックエンドで高速化
- **複数出力** — テキスト・SRT・JSON・Markdown に対応
- **クラッシュリカバリ** — インクリメンタル書き出しにより異常終了時もデータを保持

## クイックスタート

```powershell
# そのまま実行 — 環境を自動検出してエンジン・GPU を選択
TalkTranscript.exe

# エンジンを指定
TalkTranscript.exe --engine whisper-base

# GPU を使わない
TalkTranscript.exe --cpu
```

初回起動時に音声認識モデルが自動ダウンロードされます。  
録音中は **Ctrl+Q** で終了、**F2** で設定メニューを開けます。

> 詳しい使い方・設定・トラブルシューティングは **[USAGE.md](USAGE.md)** を参照してください。

## 動作環境

| 項目 | 要件 |
|------|------|
| OS | Windows 10 / 11 (x64) |
| ランタイム | .NET 8.0 |
| GPU (任意) | NVIDIA (CUDA 13) / NVIDIA・AMD・Intel (Vulkan) |

## インストール

**ビルド済みバイナリ** — [Releases](https://github.com/tttza/talk_transcript/releases) から最新版をダウンロードして展開・実行。

**ソースからビルド:**

```powershell
dotnet build -c Release
```

## 出力例

```
通話記録 - 2026/02/28 14:30
============================================================

[14:30:05] 自分: お疲れ様です。
[14:30:08] 相手: お疲れ様です。本日の議題ですが...

  ★ [14:35:12] ブックマーク

[14:35:15] 自分: 了解しました。

============================================================
通話時間: 00:10:23  |  自分: 15件  |  相手: 12件
```

## 使用ライブラリ

[NAudio](https://github.com/naudio/NAudio) · [Vosk](https://alphacephei.com/vosk/) · [Whisper.net](https://github.com/sandrohanea/whisper.net) · [Spectre.Console](https://spectreconsole.net/) · [ONNX Runtime](https://onnxruntime.ai/)

## テスト

```powershell
cd Tests
dotnet test
```

## ライセンス

[MIT License](LICENSE) © 2026 tttza
