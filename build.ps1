# Baut PerfPulse und erstellt den Installer.
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Ergebnis: dist\PerfPulse-Setup-<Version>.exe
param(
    [string]$Version = "1.0.0",
    # Adresse der PerfPulse-Website (z. B. https://perfpulse.example), leer = Wert aus der .csproj
    [string]$UpdateServer = "",
    [switch]$SkipAssets
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

if (-not $SkipAssets) {
    Write-Host "==> Grafiken erzeugen" -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\tools\make-assets.ps1"
}

Write-Host "==> App veröffentlichen (self-contained, win-x64)" -ForegroundColor Cyan
if (Test-Path "$root\publish") { Remove-Item "$root\publish" -Recurse -Force }
dotnet publish "$root\PerformanceMonitor\PerformanceMonitor.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$Version `
    $(if ($UpdateServer) { "-p:UpdateServer=$UpdateServer" }) `
    -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen" }

Write-Host "==> Installer erstellen" -ForegroundColor Cyan
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 nicht gefunden. Installieren mit: winget install JRSoftware.InnoSetup"
}
& $iscc "/DAppVersion=$Version" "$root\installer\PerfPulse.iss"
if ($LASTEXITCODE -ne 0) { throw "Installer-Erstellung fehlgeschlagen" }

$setup = Get-Item "$root\dist\PerfPulse-Setup-$Version.exe"
Write-Host ""
Write-Host ("Fertig: {0} ({1:0.0} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
