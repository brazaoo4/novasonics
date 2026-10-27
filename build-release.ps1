<#
.SYNOPSIS
    NovaSonics Automated Release Build Script
.DESCRIPTION
    1. Reads application version from NovaSonics.csproj.
    2. Builds and publishes win-x64 Release binaries into publish/win-x64.
    3. Packages a portable archive dist/NovaSonics-v{Version}-Portable-win-x64.zip.
    4. Locates or installs Inno Setup 6 and compiles installer/NovaSonics.iss into dist/NovaSonics-Setup-v{Version}.exe.
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipLocalUpdate
)

$ErrorActionPreference = "Stop"
$ScriptRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ScriptRoot)) {
    $ScriptRoot = Get-Location
}

Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host "         NovaSonics Release Build Pipeline           " -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan

# 1. Environment & SDK Discovery
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

$dotnetCmd = "dotnet"
try {
    & $dotnetCmd --version | Out-Null
} catch {
    if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
        $dotnetCmd = "$env:USERPROFILE\.dotnet\dotnet.exe"
    } else {
        throw "Could not locate .NET SDK. Please install .NET 8 SDK or ensure it is in PATH."
    }
}

# 2. Read Version Metadata
$csprojPath = Join-Path $ScriptRoot "NovaSonics\NovaSonics.csproj"
if (-not (Test-Path $csprojPath)) {
    throw "NovaSonics.csproj not found at: $csprojPath"
}

[xml]$csprojXml = Get-Content $csprojPath
$version = $csprojXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    $versionMatch = [regex]::Match((Get-Content $csprojPath -Raw), '<Version>(.*?)</Version>')
    if ($versionMatch.Success) {
        $version = $versionMatch.Groups[1].Value.Trim()
    } else {
        $version = "1.0.0"
    }
}

Write-Host "[1/4] Target Version: v$version ($Configuration, $Runtime)" -ForegroundColor Green

# 3. Terminate running instances before publish (unless skipping local update)
if (-not $SkipLocalUpdate) {
    Get-Process -Name NovaSonics -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Terminating running NovaSonics process (PID: $($_.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 300
}

# 4. Clean & Prepare Directories
$publishDir = Join-Path $ScriptRoot "publish\win-x64"
$distDir = Join-Path $ScriptRoot "dist"

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

# 5. Dotnet Publish
Write-Host "[2/4] Publishing application binaries..." -ForegroundColor Green
$publishArgs = @(
    "publish",
    $csprojPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "false",
    "-o", $publishDir,
    "/p:Version=$version",
    "/p:PublishSingleFile=false"
)

& $dotnetCmd @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# Clean any stray temp files
Get-ChildItem -Path $publishDir -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# Copy standalone portable update script into publish directory
$cliUpdateScript = Join-Path $ScriptRoot "Scripts\update-portable.ps1"
if (Test-Path $cliUpdateScript) {
    Copy-Item -Path $cliUpdateScript -Destination (Join-Path $publishDir "update-portable.ps1") -Force
    Copy-Item -Path $cliUpdateScript -Destination (Join-Path $publishDir "update.ps1") -Force
}

# 6. Package Portable ZIP Archive
Write-Host "[3/4] Creating portable ZIP archive..." -ForegroundColor Green
$zipFilename = "NovaSonics-v$version-Portable-$Runtime.zip"
$zipPath = Join-Path $distDir $zipFilename

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
$zipItem = Get-Item $zipPath
$zipSizeMb = [math]::Round($zipItem.Length / 1MB, 2)
Write-Host "  -> Portable ZIP created: $zipFilename ($zipSizeMb MB)" -ForegroundColor Cyan

# 7. Locate or Install Inno Setup (ISCC.exe)
Write-Host "[4/4] Locating Inno Setup 6 compiler (ISCC.exe)..." -ForegroundColor Green

function Find-Iscc {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )

    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    foreach ($path in $candidates) {
        if (Test-Path $path) {
            return $path
        }
    }

    # Search Program Files
    $searched = Get-ChildItem -Path "C:\Program Files*", "$env:LOCALAPPDATA\Programs" -Filter "ISCC.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($searched) {
        return $searched.FullName
    }

    return $null
}

$isccPath = Find-Iscc
if (-not $isccPath) {
    Write-Host "Inno Setup compiler not found. Attempting installation via winget..." -ForegroundColor Yellow
    try {
        & winget install --id JrSoftware.InnoSetup --exact --accept-source-agreements --accept-package-agreements --silent
        Start-Sleep -Seconds 3
        $isccPath = Find-Iscc
    } catch {
        Write-Warning "Winget installation failed: $_"
    }
}

if (-not $isccPath) {
    throw "ISCC.exe (Inno Setup 6) could not be located. Please install Inno Setup 6 from https://jrsoftware.org/isinfo.php"
}

Write-Host "  Found Inno Setup at: $isccPath" -ForegroundColor Gray

# 8. Compile Inno Setup Script
$issScriptPath = Join-Path $ScriptRoot "installer\NovaSonics.iss"
if (-not (Test-Path $issScriptPath)) {
    throw "Installer script not found at: $issScriptPath"
}

Write-Host "Compiling installer script with ISCC..." -ForegroundColor Green
$setupFilename = "NovaSonics-Setup-v$version.exe"
$setupPath = Join-Path $distDir $setupFilename

& $isccPath "/DMyAppVersion=$version" $issScriptPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

if (Test-Path $setupPath) {
    $setupItem = Get-Item $setupPath
    $setupSizeMb = [math]::Round($setupItem.Length / 1MB, 2)
    Write-Host "  -> Installer created: $setupFilename ($setupSizeMb MB)" -ForegroundColor Cyan
} else {
    throw "Expected setup artifact $setupPath was not created."
}

# 9. Local Installation Sync
if ($SkipLocalUpdate) {
    Write-Host "`n[5/5] Skipping local installation update (-SkipLocalUpdate is set)." -ForegroundColor Yellow
} else {
    Write-Host "`n[5/5] Updating local installed version..." -ForegroundColor Green
    $targetDirs = @(
        "C:\Program Files\NovaSonics",
        "$env:LOCALAPPDATA\Programs\NovaSonics"
    )

    $localUpdated = $false
    foreach ($targetDir in $targetDirs) {
        if (Test-Path $targetDir) {
            try {
                Write-Host "  Attempting direct update to: $targetDir" -ForegroundColor Cyan
                Copy-Item -Path "$publishDir\*" -Destination $targetDir -Recurse -Force -ErrorAction Stop
                Write-Host "  -> Successfully updated local binaries in: $targetDir" -ForegroundColor Green
                $localUpdated = $true
            } catch {
                Write-Warning "  Direct update to $targetDir requires administrator elevation."
            }
        }
    }

    if ($localUpdated) {
        $installedExe = "C:\Program Files\NovaSonics\NovaSonics.exe"
        if (Test-Path $installedExe) {
            Write-Host "  Starting updated NovaSonics..." -ForegroundColor Cyan
            Start-Process -FilePath $installedExe -WorkingDirectory (Split-Path $installedExe -Parent)
        }
    } else {
        Write-Host "  Launching setup installer to apply updates locally..." -ForegroundColor Cyan
        cmd.exe /c start "" $setupPath
    }
}

Write-Host "`n=====================================================" -ForegroundColor Green
Write-Host "             BUILD COMPLETED SUCCESSFULLY!           " -ForegroundColor Green
Write-Host "=====================================================" -ForegroundColor Green
Write-Host "Artifacts in dist/:" -ForegroundColor Green
Get-ChildItem -Path $distDir | ForEach-Object {
    $size = [math]::Round($_.Length / 1MB, 2)
    Write-Host "  $($_.Name) ($size MB)" -ForegroundColor White
}
