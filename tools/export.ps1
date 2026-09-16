# Exports a versioned Windows build of BS VR Manager.
# Usage:  powershell -File tools\export.ps1
# - reads the version from src\App\AppInfo.cs
# - exports with the "Windows Desktop" preset (embedded pck)
# - renames the exe to build\BSVRManager-v<version>.exe
# - syncs ui\*.json into build\ui\ (a seeded ui folder overrides the embedded pck)
# - repoints desktop shortcuts that target the old unversioned exe
param()
$ErrorActionPreference = "Stop"
$root  = Split-Path $PSScriptRoot -Parent
$godot = "$root\tools\godot\godot_console.exe"

$m = Select-String -Path "$root\src\App\AppInfo.cs" -Pattern 'Version = "([^"]+)"'
if (-not $m) { Write-Host "cannot read version from AppInfo.cs"; exit 1 }
$ver = $m.Matches[0].Groups[1].Value
Write-Host "=== BS VR Manager v$ver ==="

Get-Process BSVRManager, DepotDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$log = & $godot --headless --path $root --export-release "Windows Desktop" "$root\build\BSVRManager.exe" 2>&1
$log | Out-File "$root\out\export.log"
if ($LASTEXITCODE -ne 0) { Write-Host "EXPORT FAILED"; exit 1 }
if (($log | Select-String "Failed to build project|error CS|MSB[0-9]{4}:")) {
    Write-Host "EXPORT FAILED: .NET build errors inside export (see out\export.log)"; exit 1
}

$versioned = "$root\build\BSVRManager-v$ver.exe"
Move-Item "$root\build\BSVRManager.exe" $versioned -Force
Copy-Item "$root\ui\*.json" "$root\build\ui\" -Force

$shell = New-Object -ComObject WScript.Shell
Get-ChildItem "$env:USERPROFILE\Desktop\*.lnk" -ErrorAction SilentlyContinue | ForEach-Object {
    $lnk = $shell.CreateShortcut($_.FullName)
    $t = $lnk.TargetPath
    # repoint any shortcut at an older BSVRManager exe in build\ (versioned or not)
    if (($t -like "$root\build\BSVRManager*.exe") -and (Split-Path $t -Parent -ErrorAction SilentlyContinue) -ieq "$root\build" -and $t -ne $versioned) {
        $lnk.TargetPath = $versioned
        $lnk.Save()
        Write-Host "shortcut updated: $($_.Name)"
    }
}

Write-Host "Done: $versioned"
