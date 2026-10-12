<#
.SYNOPSIS
  Tests, builds and packages UniFi Adoption Assistant. Run on Windows with the .NET 10 SDK
  (it builds the .NET Framework 4.8 app as well as the .NET 10 test target).

.EXAMPLE
  ./build/publish.ps1
  ./build/publish.ps1 -Version 2.0.1 -SettingsFile C:\it\unifi-settings.json
  ./build/publish.ps1 -CertificatePath C:\certs\codesign.pfx -CertificatePassword (Read-Host -AsSecureString)

.OUTPUTS
  artifacts\UniFiAdoptionAssistant.exe             the app: one file, runs on Windows 10 (1903+) and 11 as they are
  artifacts\UniFiAdoptionAssistant-Setup.exe       optional per-user installer (if Inno Setup 6 is installed)
  artifacts\UniFiAdoptionAssistant-portable.zip    only when -SettingsFile is given: exe + settings.json
  artifacts\SHA256SUMS.txt                         checksums for the files above
#>
[CmdletBinding()]
param(
    [string] $Version = '2.0.0',
    [string] $Configuration = 'Release',
    # Optional pre-configured settings.json to ship inside the installer and portable zip.
    [string] $SettingsFile,
    [string] $CertificatePath,
    [SecureString] $CertificatePassword,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish\app'
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
    # The same tests run on .NET Framework 4.8 (what ships) and on .NET 10.
    $tests = Join-Path $root 'tests\UniFiAdoption.Tests'
    Write-Host '== Running tests (.NET Framework 4.8)'
    Invoke-Checked { dotnet run --project $tests -c $Configuration -f net48 } 'Tests on .NET Framework 4.8'
    Write-Host '== Running tests (.NET 10)'
    Invoke-Checked { dotnet run --project $tests -c $Configuration -f net10.0 } 'Tests on .NET 10'
}

Write-Host "== Building $Version (.NET Framework 4.8, single exe)"
Invoke-Checked {
    dotnet publish (Join-Path $root 'src\UniFiAdoption.App\UniFiAdoption.App.csproj') -c $Configuration -p:Version=$Version -o $publishDir
} 'Build'

# Costura has packed SSH.NET and its dependencies into the exe; keep only the exe and its config.
Get-ChildItem $publishDir -Exclude 'UniFiAdoptionAssistant.exe', 'UniFiAdoptionAssistant.exe.config' | Remove-Item -Recurse -Force
Copy-Item (Join-Path $root 'src\UniFiAdoption.App\settings.example.json') $publishDir
if ($SettingsFile) { Copy-Item $SettingsFile (Join-Path $publishDir 'settings.json') }

Invoke-Sign $exe

$portableExe = Join-Path $artifacts 'UniFiAdoptionAssistant.exe'
Copy-Item $exe $portableExe
$zip = Join-Path $artifacts 'UniFiAdoptionAssistant-portable.zip'
if ($SettingsFile) {
    Write-Host '== Creating portable zip with settings.json'
    Compress-Archive -Path $exe, (Join-Path $publishDir 'settings.json') -DestinationPath $zip -Force
}

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
    Write-Warning 'Inno Setup 6 not found; skipping the installer. The standalone exe was still created.'
}

$files = @($portableExe) + @($setup, $zip | Where-Object { Test-Path $_ })
$files | ForEach-Object {
    $hash = (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $_ -Leaf)"
} | Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii

Write-Host '== Done'
Get-ChildItem $artifacts -File | Format-Table Name, @{ n = 'Size (KB)'; e = { [math]::Round($_.Length / 1KB) } }
