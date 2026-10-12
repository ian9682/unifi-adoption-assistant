<#
.SYNOPSIS
  Tests, publishes and packages UniFi Adoption Assistant. Run on Windows with the .NET 10 SDK.

.EXAMPLE
  ./build/publish.ps1
  ./build/publish.ps1 -Version 1.2.0 -SettingsFile C:\it\unifi-settings.json
  ./build/publish.ps1 -CertificatePath C:\certs\codesign.pfx -CertificatePassword (Read-Host -AsSecureString)

.OUTPUTS
  artifacts\UniFiAdoptionAssistant-win-x86.zip     portable copy (exe + settings files)
  artifacts\UniFiAdoptionAssistant-Setup.exe       per-user installer (if Inno Setup 6 is installed)
  artifacts\SHA256SUMS.txt                         checksums for the files above
#>
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',
    # win-x86 runs on 32-bit and 64-bit Windows 10/11 (and on ARM64 through emulation), so one download fits every PC.
    [string] $Runtime = 'win-x86',
    [string] $Configuration = 'Release',
    # Optional pre-configured settings.json to ship inside the package and installer.
    [string] $SettingsFile,
    [string] $CertificatePath,
    [SecureString] $CertificatePassword,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts "publish\$Runtime"
$exe = Join-Path $publishDir 'UniFiAdoptionAssistant.exe'

function Invoke-Checked([scriptblock] $Command, [string] $What) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

function Invoke-Sign([string] $File) {
    if (-not $CertificatePath) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool.exe was not found. Install the Windows SDK.' }
    $plain = [System.Net.NetworkCredential]::new('', $CertificatePassword).Password
    Invoke-Checked { & $signtool.FullName sign /fd SHA256 /f $CertificatePath /p $plain /tr $TimestampUrl /td SHA256 $File } "Signing $File"
}

Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item $publishDir -ItemType Directory -Force | Out-Null

if (-not $SkipTests) {
    Write-Host '== Running tests'
    Invoke-Checked { dotnet run --project (Join-Path $root 'tests\UniFiAdoption.Tests') -c $Configuration } 'Tests'
}

Write-Host "== Publishing $Version for $Runtime"
Invoke-Checked {
    dotnet publish (Join-Path $root 'src\UniFiAdoption.App\UniFiAdoption.App.csproj') `
        -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -p:Version=$Version `
        -o $publishDir
} 'Publish'

Get-ChildItem $publishDir -Exclude 'UniFiAdoptionAssistant.exe' | Remove-Item -Recurse -Force
Copy-Item (Join-Path $root 'src\UniFiAdoption.App\settings.example.json') $publishDir
if ($SettingsFile) { Copy-Item $SettingsFile (Join-Path $publishDir 'settings.json') }

Invoke-Sign $exe

Write-Host '== Creating portable zip'
$zip = Join-Path $artifacts "UniFiAdoptionAssistant-$Runtime.zip"
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip -Force

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
$setup = Join-Path $artifacts 'UniFiAdoptionAssistant-Setup.exe'
if ($iscc) {
    Write-Host '== Building installer'
    Invoke-Checked { & $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/O$artifacts" (Join-Path $root 'installer\UniFiAdoptionAssistant.iss') } 'Inno Setup'
    Invoke-Sign $setup
} else {
    Write-Warning 'Inno Setup 6 not found; skipping the installer. The portable zip was still created.'
}

$files = @($zip) + @(if (Test-Path $setup) { $setup })
$files | ForEach-Object {
    $hash = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $_ -Leaf)"
} | Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii

Write-Host '== Done'
Get-ChildItem $artifacts -File | Format-Table Name, Length
