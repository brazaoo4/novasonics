<#
.SYNOPSIS
    NovaSonics Standalone Portable Updater
.DESCRIPTION
    Checks GitHub for the latest release of NovaSonics, downloads the portable ZIP archive,
    and updates the installation in the current directory.
#>

[CmdletBinding()]
param(
    [string]$TargetDir = $PSScriptRoot,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Get-Location
}

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "          NovaSonics Portable Updater                " -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "Target Directory: $TargetDir" -ForegroundColor Gray

# 1. Detect current local version
$exePath = Join-Path $TargetDir "NovaSonics.exe"
$currentVersion = [Version]"0.0.0"

if (Test-Path $exePath) {
    try {
        $fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
        if ($fileInfo.ProductVersion) {
            $rawVer = $fileInfo.ProductVersion.Split('+')[0].Trim().TrimStart('v', 'V')
            [Version]::TryParse($rawVer, [ref]$currentVersion) | Out-Null
        } elseif ($fileInfo.FileVersion) {
            $rawVer = $fileInfo.FileVersion.Split('+')[0].Trim().TrimStart('v', 'V')
            [Version]::TryParse($rawVer, [ref]$currentVersion) | Out-Null
        }
    } catch {
        Write-Warning "Could not read local version from $exePath. Proceeding with update check..."
    }
} else {
    Write-Host "NovaSonics.exe not found in $TargetDir. Will perform fresh extraction." -ForegroundColor Yellow
}

Write-Host "Local Version:  v$($currentVersion.ToString(3))" -ForegroundColor Cyan

# 2. Query GitHub Releases API
$apiUrl = "https://api.github.com/repos/brazaoo4/novasonics/releases/latest"
Write-Host "Checking GitHub for latest release..." -ForegroundColor Gray

try {
    $headers = @{ "User-Agent" = "NovaSonics-Updater" }
    $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Get -TimeoutSec 10
} catch {
    Write-Error "Failed to connect to GitHub Releases API: $_"
    exit 1
}

$remoteTag = $release.tag_name
$remoteVerStr = $remoteTag.Trim().TrimStart('v', 'V')
$remoteVersion = [Version]"0.0.0"
if (![Version]::TryParse($remoteVerStr, [ref]$remoteVersion)) {
    Write-Error "Could not parse remote version '$remoteTag'."
    exit 1
}

Write-Host "Latest Release: v$($remoteVersion.ToString(3)) ($($release.html_url))" -ForegroundColor Green

if (!$Force -and ($remoteVersion -le $currentVersion)) {
    Write-Host "`nNovaSonics is already up to date!" -ForegroundColor Green
    exit 0
}

# 3. Locate Portable ZIP Asset
$zipAsset = $release.assets | Where-Object { $_.name -like "*Portable*.zip" } | Select-Object -First 1
if (-not $zipAsset) {
    $zipAsset = $release.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
}

if (-not $zipAsset) {
    Write-Error "Could not find a portable ZIP release asset in $remoteTag."
    exit 1
}

$downloadUrl = $zipAsset.browser_download_url
$zipFileName = $zipAsset.name
$tempZipPath = Join-Path $env:TEMP $zipFileName

Write-Host "`nDownloading: $zipFileName" -ForegroundColor Cyan
Write-Host "URL: $downloadUrl" -ForegroundColor Gray

# Download file
Invoke-WebRequest -Uri $downloadUrl -OutFile $tempZipPath -UserAgent "NovaSonics-Updater"

# Remove Zone.Identifier
try {
    Remove-Item -Path "$tempZipPath:Zone.Identifier" -Force -ErrorAction SilentlyContinue
} catch {}

# 4. Stop running NovaSonics process
Get-Process -Name NovaSonics -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running NovaSonics process (PID: $($_.Id))..." -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

# 5. Extract to Staging
$stagingDir = Join-Path $env:TEMP "NovaSonics_CLI_Staging"
if (Test-Path $stagingDir) {
    Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

Write-Host "Extracting payload..." -ForegroundColor Gray
Expand-Archive -Path $tempZipPath -DestinationPath $stagingDir -Force

$payloadExe = Get-ChildItem -Path $stagingDir -Filter "NovaSonics.exe" -Recurse | Select-Object -First 1
if (-not $payloadExe) {
    Write-Error "NovaSonics.exe was not found inside the downloaded archive."
    exit 1
}
$sourcePayloadDir = $payloadExe.DirectoryName

# 6. Copy to Target Directory
Write-Host "Applying update to $TargetDir..." -ForegroundColor Green
Copy-Item -Path "$sourcePayloadDir\*" -Destination $TargetDir -Recurse -Force

# 7. Cleanup
try { Remove-Item -Path $tempZipPath -Force -ErrorAction SilentlyContinue } catch {}
try { Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}

Write-Host "`nUpdate completed successfully to v$($remoteVersion.ToString(3))!" -ForegroundColor Green

# 8. Restart NovaSonics
$runExe = Join-Path $TargetDir "NovaSonics.exe"
if (Test-Path $runExe) {
    Write-Host "Launching NovaSonics..." -ForegroundColor Cyan
    Start-Process -FilePath $runExe -WorkingDirectory $TargetDir
}
