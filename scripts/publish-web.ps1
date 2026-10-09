param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts/web-win-x64'
$zip = Join-Path $root 'artifacts/SysBot-SV-Local-Web-win-x64.zip'
Push-Location $root
try {
    & $Dotnet publish SysBot.Pokemon.Web/SysBot.Pokemon.Web.csproj -c Release -r win-x64 --self-contained true -o $output
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    Copy-Item docs/SV-LOCAL-WEB.zh-CN.md "$output/README.zh-CN.md"
    Copy-Item docs/SV-LOCAL-WEB.zh-CN.md "$output/SV-LOCAL-WEB.zh-CN.md"
    Copy-Item docs/SV-LOCAL-TRADE.zh-CN.md "$output/SV-LOCAL-TRADE.zh-CN.md"
    Copy-Item flow-report.html "$output/flow-report.html"
    Copy-Item LICENSE "$output/LICENSE"
    Copy-Item SysBot.Pokemon.Web/native/win-x64/README.md "$output/libusb-README.md"
    @'
@echo off
cd /d "%~dp0"
title SysBot Local Web
SysBot.Web.exe
if errorlevel 1 (
  echo.
  echo SysBot stopped. Check the error above. Close any other running copy first.
  pause
)
'@ | Set-Content "$output/start-web.cmd" -Encoding Ascii
    # Never distribute local device settings, queues or logs from a previous local launch.
    Get-ChildItem $output -Exclude 'data','logs' | Compress-Archive -DestinationPath $zip -Force -CompressionLevel Optimal
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($zip)
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
    "$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding Ascii
    Write-Host "Package: $zip"
    Write-Host "SHA256: $hash"
} finally { Pop-Location }
