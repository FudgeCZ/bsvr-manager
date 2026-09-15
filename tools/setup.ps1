# Downloads the runtime helpers BS VR Manager expects next to its executable:
#   tools\steamcmd\steamcmd.exe   (background version downloads)
#   tools\DepotDownloader.exe     (QR-code login + download fallback)
# Usage:  powershell -File tools\setup.ps1  [-AppDir <folder containing BSVRManager.exe>]
param([string]$AppDir = "$PSScriptRoot\..\build")

$ErrorActionPreference = "Stop"
$tools = Join-Path $AppDir "tools"
New-Item -ItemType Directory -Force -Path "$tools\steamcmd" | Out-Null

Write-Host "Downloading steamcmd..."
Invoke-WebRequest -Uri "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip" -OutFile "$tools\steamcmd.zip"
Expand-Archive -Path "$tools\steamcmd.zip" -DestinationPath "$tools\steamcmd" -Force
Remove-Item "$tools\steamcmd.zip"

Write-Host "Downloading DepotDownloader..."
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/SteamRE/DepotDownloader/releases/latest"
$asset = $rel.assets | Where-Object { $_.name -match "windows-x64\.zip$" } | Select-Object -First 1
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile "$tools\dd.zip"
Expand-Archive -Path "$tools\dd.zip" -DestinationPath "$tools\dd" -Force
Copy-Item "$tools\dd\DepotDownloader.exe" "$tools\DepotDownloader.exe" -Force
Remove-Item "$tools\dd.zip"; Remove-Item "$tools\dd" -Recurse -Force

Write-Host "Done. Helpers installed in $tools"
