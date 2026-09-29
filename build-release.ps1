<#
.SYNOPSIS
    Publishes AirSend as an unpackaged, self-contained folder and zips it.

.DESCRIPTION
    The app ships as a .zip archive (no MSIX, no package identity, no
    sideloading, no Developer Mode). Everything it needs — the .NET runtime and
    the Windows App SDK — is copied next to AirSend.exe, so the archive runs on a
    clean Windows 10 1809+ machine after unzipping.

.PARAMETER RestoreSources
    Optional NuGet source list (folder or feed URL) forwarded to restore. Use it
    on machines without nuget.org access, for example
    -RestoreSources C:\Users\me\.nuget\packages
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x86', 'win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$RestoreSources,

    [switch]$FrameworkDependent,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = (Select-String -Path (Join-Path $root 'src\AirSend.App\AirSend.App.csproj') -Pattern '<Version>(.*)</Version>').Matches.Groups[1].Value
if (-not $version) { $version = '0.2.0' }

$artifacts = Join-Path $root 'artifacts'
$flavor = if ($FrameworkDependent) { 'frameworkdependent' } else { 'selfcontained' }
$publishDirectory = Join-Path $artifacts "AirSend-$version-$RuntimeIdentifier-$flavor"
$zipPath = Join-Path $artifacts "AirSend-$version-$RuntimeIdentifier-$flavor.zip"

$platform = switch ($RuntimeIdentifier) {
    'win-arm64' { 'ARM64' }
    'win-x86' { 'x86' }
    default { 'x64' }
}

$restoreProperties = @()
if ($RestoreSources) {
    $restoreProperties += "-p:RestoreSources=$RestoreSources"
    $restoreProperties += '-p:NuGetAudit=false'
}

Write-Host "AirSend $version — $Configuration / $RuntimeIdentifier" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host '→ tests' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\AirSend.Core.Tests\AirSend.Core.Tests.csproj') `
        -c $Configuration --nologo @restoreProperties
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}

Write-Host '→ publish' -ForegroundColor Cyan
if (Test-Path $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

$selfContained = -not $FrameworkDependent

dotnet publish (Join-Path $root 'src\AirSend.App\AirSend.App.csproj') `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained $(if ($selfContained) { 'true' } else { 'false' }) `
    -p:WindowsAppSDKSelfContained=$(if ($selfContained) { 'true' } else { 'false' }) `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -p:Platform=$platform `
    -o $publishDirectory `
    --nologo @restoreProperties
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Copy-Item (Join-Path $root 'LICENSE') $publishDirectory -Force
Copy-Item (Join-Path $root 'README.md') $publishDirectory -Force

Write-Host '→ zip' -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal

$size = [Math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "✔ $zipPath ($size MB)" -ForegroundColor Green
Write-Host '  Unzip anywhere and run AirSend.exe — no installer, no Developer Mode.' -ForegroundColor DarkGray
