# exe単体(.NET同梱・FFmpeg同梱)を publish\CompressionRecorder.exe に作る。
# 先に scripts\prepare-ffmpeg.ps1 を実行して third_party\ffmpeg.exe.gz を用意しておく。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'third_party\ffmpeg.exe.gz'))) { throw 'third_party\ffmpeg.exe.gz がありません。scripts\prepare-ffmpeg.ps1 を先に実行してください。' }
dotnet publish (Join-Path $root 'CompressionRecorder.csproj') -c Release -o (Join-Path $root 'publish')
