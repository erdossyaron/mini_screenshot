# Önálló, egyfájlos Screen.exe készítése a dist mappába (.NET sem kell hozzá a célgépen).
# Futtatás: powershell -ExecutionPolicy Bypass -File tools\publish.ps1
# A verziószámot a Screen.csproj <Version> eleme adja, kiadás előtt azt léptesd.
# A telepített példány (%LOCALAPPDATA%\Programs\mini_screenshot) futhat közben, ehhez nem nyúl.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$dist = Join-Path $root 'dist'
$staging = Join-Path $root 'obj\publish-staging'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

dotnet publish "$root\Screen.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $staging
if ($LASTEXITCODE -ne 0) { throw "A publikálás nem sikerült." }

New-Item -ItemType Directory -Force $dist | Out-Null

# Korábban félretett régi példányok törlése, ha már nem futnak
Get-ChildItem $dist -Filter 'Screen.old-*.exe' -ErrorAction SilentlyContinue |
    ForEach-Object { try { Remove-Item $_.FullName -Force } catch { } }

# Futó exe nem írható felül, de átnevezhető
$target = Join-Path $dist 'Screen.exe'
if (Test-Path $target) {
    try {
        Remove-Item $target -Force
    }
    catch {
        $old = "Screen.old-$(Get-Date -Format 'yyyyMMdd-HHmmss').exe"
        Rename-Item $target $old
        Write-Output "A dist\Screen.exe fut, a régi átnevezve: $old (következő publikáláskor törlődik)"
    }
}

# Csak az exe kell: a mellé kerülő natív WPF DLL-eket a WinForms app nem használja
Copy-Item (Join-Path $staging 'Screen.exe') $target
Remove-Item $staging -Recurse -Force

$ver = (Get-Item $target).VersionInfo.FileVersion
Write-Output "Kész: $target (verzió: $ver, $([math]::Round((Get-Item $target).Length / 1MB, 1)) MB)"
