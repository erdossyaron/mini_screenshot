# Telepítő varázsló készítése: release\mini_screenshot-Setup-<verzió>.exe
# Előtte futtasd a tools\publish.ps1-et, hogy a dist\Screen.exe friss legyen
# (vagy add meg a -Publish kapcsolót, és ez a szkript futtatja).
# Kell hozzá az Inno Setup 6:  winget install --id JRSoftware.InnoSetup -e --scope user
# Futtatás: powershell -ExecutionPolicy Bypass -File tools\make-setup.ps1 [-Publish]
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

if ($Publish) {
    & (Join-Path $PSScriptRoot 'publish.ps1')
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Nincs Inno Setup 6. Telepítés: winget install --id JRSoftware.InnoSetup -e --scope user' }
if (-not (Test-Path "$root\dist\Screen.exe")) { throw 'Nincs dist\Screen.exe, előbb futtasd a tools\publish.ps1-et.' }

& $iscc /Q "$root\installer\mini_screenshot.iss"
if ($LASTEXITCODE -ne 0) { throw 'A telepítő fordítása nem sikerült.' }

Get-ChildItem "$root\release" -Filter 'mini_screenshot-Setup-*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { Write-Output "Kész: $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
