# FFmpeg(gyan.dev の essentials 版)を取得し、exeへ埋め込むための third_party\ffmpeg.exe.gz を作る。
# 使い方: powershell -ExecutionPolicy Bypass -File scripts\prepare-ffmpeg.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $env:TEMP 'compression-recorder-ffmpeg'
$out = Join-Path $root 'third_party'
New-Item -ItemType Directory -Force -Path $work, $out | Out-Null
$zip = Join-Path $work 'ffmpeg.zip'
Invoke-WebRequest -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $work -Force
$exe = Get-ChildItem -Path $work -Recurse -Filter ffmpeg.exe | Select-Object -First 1
$gz = Join-Path $out 'ffmpeg.exe.gz'
$in = [IO.File]::OpenRead($exe.FullName)
$dst = [IO.File]::Create($gz)
$gzip = New-Object IO.Compression.GZipStream($dst, [IO.Compression.CompressionLevel]::SmallestSize)
$in.CopyTo($gzip); $gzip.Dispose(); $dst.Dispose(); $in.Dispose()
Write-Host "created $gz"
