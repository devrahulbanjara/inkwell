<#
  Builds Inkwell for Windows: a self-contained Inkwell.exe (no .NET install needed) and Inkwell-Setup.exe.

    ./scripts/build.ps1                  # version from the latest git tag (v0.1.2 -> 0.1.2)
    ./scripts/build.ps1 -Version 0.2.0
    ./scripts/build.ps1 -SkipInstaller   # just the exe

  Output: windows/publish/win-x64/Inkwell.exe, windows/publish/Inkwell-Setup.exe and Inkwell-<version>-Setup.exe
  Needs the .NET 8 SDK, and Inno Setup 6 for the installer (winget install JRSoftware.InnoSetup).
#>
param([string]$Version = "", [switch]$SkipInstaller)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
  $tag = (git describe --tags --abbrev=0 2>$null)
  $Version = if ($tag) { $tag.TrimStart('v') } else { "0.1.0" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')" }
Write-Host "==> Building Inkwell $Version" -ForegroundColor Cyan

dotnet test Inkwell.sln -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

Remove-Item publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish Inkwell\Inkwell.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -p:Version=$Version -p:AssemblyVersion=$Version.0 -p:FileVersion=$Version.0 -p:InformationalVersion=$Version `
  -o publish\win-x64 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
Write-Host "OK  publish\win-x64\Inkwell.exe" -ForegroundColor Green

if ($SkipInstaller) { return }
$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }
& $iscc /Q "/DAppVersion=$Version" installer\Inkwell.iss
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
Copy-Item publish\Inkwell-Setup.exe "publish\Inkwell-$Version-Setup.exe" -Force
$sha = (Get-FileHash publish\Inkwell-Setup.exe -Algorithm SHA256).Hash.ToLower()
Write-Host "OK  publish\Inkwell-Setup.exe  ($([math]::Round((Get-Item publish\Inkwell-Setup.exe).Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "  SHA-256 $sha"
