# Compression Recorder

画面を録画し、必要な部分だけ切り取って、指定した容量に収まるよう圧縮して書き出す Windows 用アプリです。Discord などへすぐ共有できます。

- 画面・ウィンドウを最大 4096×4096、最長 30 秒で録画（F8 で開始／停止、音声も録れます）
- 時間と画面の範囲を切り取り
- MP4／GIF／WebP（アニメ）で書き出し。容量上限（整数 MB）に収まるよう、上限ぎりぎりまで細かく圧縮
- 圧縮の優先順位: 画質（基準の約半分まで）→ 24fps → さらに画質・大きさ
- 書き出し後の解像度の予想を表示。圧縮中はクリックで中止

## 使い方

1. [Releases](../../releases) から `CompressionRecorder.exe` をダウンロードして起動します（**exe 1 つで動きます**。インストールや FFmpeg の用意は不要です）。
2. 「録画を開始」または F8 で録画します。
3. 切り取り・形式・容量を決めて「Discord用に保存」を押し、できたファイルをドラッグして共有します。

Windows 10/11 x64 向けです。設定は `%LOCALAPPDATA%\CompressionRecorder` に保存されます。

## ビルド

.NET 10 SDK が必要です。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\prepare-ffmpeg.ps1   # FFmpeg を取得して埋め込み用ファイルを作る
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1          # publish\CompressionRecorder.exe ができる
```

## ライセンス・同梱物

exe には FFmpeg（GPL v3 のビルド、改変なし）と NAudio（MIT）が含まれます。詳細は `THIRD_PARTY_NOTICES.txt`、`FFMPEG_LICENSE.txt`、`FFMPEG_SOURCE.txt` を見てください。
