# Publishes an optimized Release build of WinFinder (ReadyToRun, self-contained) and adds a
# Start Menu shortcut, so day-to-day launches skip `dotnet run`'s multi-second build check.
#
#   powershell -ExecutionPolicy Bypass -File tools\install.ps1
#   powershell -ExecutionPolicy Bypass -File tools\install.ps1 -NoShortcut
#
# Re-run it after pulling changes to update the installed copy.
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA "Programs\WinFinder"),
    [switch]$NoShortcut
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$running = Get-Process WinFinder -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($Destination, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "WinFinder is running from $Destination. Close it, then run this again." -ForegroundColor Yellow
    exit 1
}

$arm = $env:PROCESSOR_ARCHITECTURE -eq "ARM64"
$rid = if ($arm) { "win-arm64" } else { "win-x64" }
$platform = if ($arm) { "ARM64" } else { "x64" }

Write-Host "Publishing Release ($rid) to $Destination ..."
& dotnet publish (Join-Path $root "src\WinFinder.App") -c Release -r $rid -p:Platform=$platform `
    -p:PublishReadyToRun=true -o $Destination --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $Destination "WinFinder.exe"
if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "WinFinder.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $Destination
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = "WinFinder - a Finder-style file manager"
    $shortcut.Save()
    Write-Host "Start Menu shortcut: $lnk"
}
Write-Host "Installed: $exe" -ForegroundColor Green
