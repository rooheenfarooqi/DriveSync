<#
.SYNOPSIS
    Builds the DriveSync installer (DriveSync-win-Setup.exe) and update packages with Velopack.

.DESCRIPTION
    Used both on your PC (to test an installer) and by .github/workflows/release.yml (for real releases),
    so both make exactly the same thing. Output goes to artifacts/releases.

    Code signing is off until you set it up. When one of these is set, every .exe and .dll is signed:
      DRIVESYNC_SIGN_PFX, DRIVESYNC_SIGN_PFX_PASSWORD   a .pfx code-signing certificate and its password
      DRIVESYNC_AZURE_SIGN_METADATA                     Azure Trusted Signing metadata.json (credentials come
                                                        from AZURE_CLIENT_ID, AZURE_CLIENT_SECRET, AZURE_TENANT_ID)

.PARAMETER DeltaFromGitHub
    Downloads the latest published release first, so the update package can be a small delta. Needs
    GITHUB_TOKEN. The release workflow uses this; a local test build doesn't need it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
#>
param([switch]$DeltaFromGitHub)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$PackId = 'RooheenFarooqi.DriveSync'   # never change: installed copies find their updates by this id
$RepoUrl = 'https://github.com/rooheenfarooqi/DriveSync'
$project = 'src/DriveSync.App/DriveSync.App.csproj'
$publish = Join-Path $root 'artifacts/publish'
$releases = Join-Path $root 'artifacts/releases'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

[xml]$xml = Get-Content $project
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw "No <Version> in $project." }
$prerelease = $version.Contains('-')
Write-Host "Building DriveSync $version$(if ($prerelease) { ' (pre-release)' })"

foreach ($dir in $publish, $releases) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory $dir | Out-Null
}

Invoke-Checked 'Restoring tools' { dotnet tool restore }

if ($DeltaFromGitHub) {
    if (-not $env:GITHUB_TOKEN) { throw 'DeltaFromGitHub needs GITHUB_TOKEN.' }
    $download = @('vpk', 'download', 'github', '--repoUrl', $RepoUrl, '--token', $env:GITHUB_TOKEN, '--outputDir', $releases)
    if ($prerelease) { $download += '--pre' }
    # The first release has nothing to download; that only means no delta package.
    & dotnet @download
    if ($LASTEXITCODE -ne 0) { Write-Warning 'No earlier release downloaded, so this release has no delta package.' }
}

# Locked restore: fails if any package differs from packages.lock.json.
Invoke-Checked 'Restoring packages' { dotnet restore $project -r win-x64 --locked-mode }
Invoke-Checked 'Publishing' {
    dotnet publish $project -c Release -r win-x64 --self-contained --no-restore `
        -p:ContinuousIntegrationBuild=true -p:DebugType=none -o $publish
}

$pack = @(
    'vpk', 'pack',
    '--packId', $PackId,
    '--packVersion', $version,
    '--packDir', $publish,
    '--mainExe', 'DriveSync.exe',
    '--packTitle', 'DriveSync',
    '--packAuthors', 'Rooheen Farooqi',
    '--icon', 'src/DriveSync.App/Assets/DriveSync.ico',
    '--splashImage', 'src/DriveSync.App/Assets/Splash.png',
    '--outputDir', $releases
)
if (Test-Path 'RELEASE_NOTES.md') { $pack += @('--releaseNotes', 'RELEASE_NOTES.md') }

if ($env:DRIVESYNC_SIGN_PFX) {
    if (-not (Test-Path $env:DRIVESYNC_SIGN_PFX)) { throw "Signing certificate not found: $env:DRIVESYNC_SIGN_PFX" }
    Write-Host 'Signing with the .pfx certificate'
    $pack += @('--signParams', "/f `"$env:DRIVESYNC_SIGN_PFX`" /p `"$env:DRIVESYNC_SIGN_PFX_PASSWORD`" /fd sha256 /tr http://timestamp.digicert.com /td sha256")
}
elseif ($env:DRIVESYNC_AZURE_SIGN_METADATA) {
    Write-Host 'Signing with Azure Trusted Signing'
    $pack += @('--azureTrustedSignFile', $env:DRIVESYNC_AZURE_SIGN_METADATA)
}
else {
    Write-Warning 'Not code-signed: Windows SmartScreen will show "Unknown publisher" when people run the installer.'
}

Invoke-Checked 'Packaging' { dotnet @pack }

Write-Host ''
Write-Host "Done. Installer: $(Join-Path $releases "$PackId-win-Setup.exe")"
Get-ChildItem $releases | Format-Table Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
