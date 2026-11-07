<#
.SYNOPSIS
    Builds and packages a self-contained, portable Windows x64 release of Immersive Reading.

.DESCRIPTION
    Compiles the .NET 10 desktop application with native Photino, SQLite, MeCab,
    frontend assets (wwwroot), and runtime libraries into a single portable directory
    and generates an ready-to-distribute ZIP archive.

.PARAMETER Configuration
    Build configuration (default: "Release").

.PARAMETER Runtime
    Target runtime identifier (default: "win-x64").

.PARAMETER SkipVerify
    Skip the automated verification check of the compiled binary.

.EXAMPLE
    .\scripts\package_windows.ps1
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..")
$ProjectFile = Join-Path $RepoRoot "immersive-reader\src\ImmersiveReading.App\ImmersiveReading.App.csproj"
$DistDir = Join-Path $RepoRoot "dist\ImmersiveReading-$Runtime"
$ZipFile = Join-Path $RepoRoot "dist\ImmersiveReading-$Runtime.zip"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Immersive Reading - Standalone Release Packager ($Runtime)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Clean previous build if it exists
if (Test-Path $DistDir) {
    Write-Host "[1/5] Cleaning existing build directory: $DistDir..." -ForegroundColor DarkGray
    Remove-Item -Recurse -Force $DistDir
}
if (Test-Path $ZipFile) {
    Remove-Item -Force $ZipFile
}

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

# 2. Run dotnet publish self-contained
Write-Host "[2/5] Publishing self-contained $Configuration binary ($Runtime)..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    $ProjectFile,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $DistDir,
    "-p:PublishSingleFile=false",
    "-p:DebugType=none"
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}

# 3. Add 1-click launcher and portable readme
Write-Host "[3/5] Adding 1-click launcher and portable instructions..." -ForegroundColor Yellow

$launcherBat = @"
@echo off
cd /d "%~dp0"
start "" "%~dp0ImmersiveReading.App.exe" %*
"@
Set-Content -Path (Join-Path $DistDir "START-READER.bat") -Value $launcherBat -Encoding ASCII

$portableReadme = @"
================================================================================
  IMMERSIVE READING — PORTABLE DESKTOP EDITION (Windows x64)
================================================================================

Welcome to Immersive Reading! This package is completely self-contained.
No installation, administrative privileges, or .NET runtime installation required.

HOW TO RUN:
--------------------------------------------------------------------------------
1. Double-click "START-READER.bat" or "ImmersiveReading.App.exe" to launch.
2. Drag and drop any Japanese .epub file onto the library window to import.
3. Click "Read" (or press Enter) to open the reader.

KEY SHORTCUTS & FEATURES:
--------------------------------------------------------------------------------
- Reader Keyboard Shortcuts: Press '?' anytime while reading to see the Vim motions cheat sheet.
- Instant Dictionary: Hover over any word token or press 'e' / 'Enter'.
- Furigana Toggle: Press 'f' to cycle All Furigana / Unlearned Only / Off.
- Vertical/Horizontal Layout: Press 'v' to switch between vertical and horizontal.
- Global Settings: Press 'Ctrl + ,' to open Settings (fonts, themes, directories, LLM).
- Data Privacy: All library data, reading progress, and vocabulary states are kept
  locally in the ".library-data" folder.

SYSTEM REQUIREMENTS:
--------------------------------------------------------------------------------
- Windows 10 (version 1809 or higher) or Windows 11 (64-bit).
- Microsoft Edge WebView2 Runtime (pre-installed on virtually all modern Windows systems).

Enjoy your reading!
"@
Set-Content -Path (Join-Path $DistDir "README.txt") -Value $portableReadme -Encoding UTF8

# 4. Verify the published binary
if (-not $SkipVerify) {
    Write-Host "[4/5] Verifying published standalone binary (--verify-reader)..." -ForegroundColor Yellow
    $exePath = Join-Path $DistDir "ImmersiveReading.App.exe"
    & $exePath --verify-reader
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Standalone binary verification failed with exit code $LASTEXITCODE"
    }
    Write-Host "  Verification OK!" -ForegroundColor Green
} else {
    Write-Host "[4/5] Verification skipped by flag." -ForegroundColor DarkGray
}

# 5. Create ZIP Archive
Write-Host "[5/5] Compressing release package to ZIP archive..." -ForegroundColor Yellow
Add-Type -AssemblyName "System.IO.Compression.FileSystem"
[System.IO.Compression.ZipFile]::CreateFromDirectory($DistDir, $ZipFile, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipItem = Get-Item $ZipFile
$sizeMb = [math]::Round($zipItem.Length / 1MB, 2)

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  BUILD & PACKAGING COMPLETE!" -ForegroundColor Green
Write-Host "  Folder: $DistDir" -ForegroundColor Green
Write-Host "  Zip:    $ZipFile ($sizeMb MB)" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

