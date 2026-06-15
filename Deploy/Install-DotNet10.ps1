#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Downloads and installs .NET 10 ASP.NET Core Hosting Bundle on the VM.
    Run this on the Azure VM as Administrator.
#>

$ErrorActionPreference = "Stop"
function Write-Step { param($m) Write-Host "`n==  $m" -ForegroundColor Cyan }
function Write-OK   { param($m) Write-Host "  [OK]  $m" -ForegroundColor Green }

$TempDir = "C:\PiiRemover-Setup"
New-Item -ItemType Directory -Force $TempDir | Out-Null

# ── Check if already installed ────────────────────────────────────────────────
Write-Step "Checking existing .NET installations"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (Test-Path $dotnet) {
    $versions = & $dotnet --list-runtimes 2>&1
    Write-Host $versions
    if ($versions -match "Microsoft.AspNetCore.App 10\.") {
        Write-OK ".NET 10 ASP.NET Core runtime already installed"
        exit 0
    }
}

# ── Download .NET 10 Hosting Bundle ──────────────────────────────────────────
Write-Step "Downloading .NET 10 ASP.NET Core Hosting Bundle"

# Hosting Bundle includes: .NET Runtime + ASP.NET Core Runtime + IIS integration
$url  = "https://download.visualstudio.microsoft.com/download/pr/97f57a73-8a25-4e76-8f21-d1462ed21b18/3b3ede39e50a680534e40fce2a33614c/dotnet-hosting-10.0.0-win.exe"
$dest = "$TempDir\dotnet-hosting-10-win.exe"

if (Test-Path $dest) {
    Write-OK "Already downloaded: $dest"
} else {
    Write-Host "  Downloading (~70 MB)..." -NoNewline
    # Use BITS for more reliable large file download
    try {
        Start-BitsTransfer -Source $url -Destination $dest
        Write-Host " done (BITS)" -ForegroundColor Green
    } catch {
        # Fallback to WebRequest
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
        Write-Host " done (WebRequest)" -ForegroundColor Green
    }
}

# ── Install silently ──────────────────────────────────────────────────────────
Write-Step "Installing .NET 10 Hosting Bundle (silent)"
$proc = Start-Process -FilePath $dest `
    -ArgumentList "/install", "/quiet", "/norestart" `
    -Wait -PassThru

Write-Host "  Exit code: $($proc.ExitCode)"
if ($proc.ExitCode -notin 0, 3010) {
    Write-Host "  [FAIL] Installer failed with exit code $($proc.ExitCode)" -ForegroundColor Red
    exit 1
}
Write-OK ".NET 10 Hosting Bundle installed"

# ── Restart IIS to pick up new ASP.NET Core module ───────────────────────────
Write-Step "Restarting IIS"
iisreset /restart | Out-Null
Write-OK "IIS restarted"

# ── Verify ────────────────────────────────────────────────────────────────────
Write-Step "Verifying installation"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (Test-Path $dotnet) {
    Write-Host "  Installed runtimes:" -ForegroundColor White
    & $dotnet --list-runtimes | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
    Write-Host "  Installed SDKs:" -ForegroundColor White
    & $dotnet --list-sdks | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
} else {
    Write-Host "  [WARN] dotnet.exe not found in default location" -ForegroundColor Yellow
    Write-Host "  Try opening a new PowerShell window and running: dotnet --version" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Done! Now retry: PiiRemover.Api.exe" -ForegroundColor Green
Write-Host "  or browse: http://48.199.16.164/swagger" -ForegroundColor White
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Cyan

if ($proc.ExitCode -eq 3010) {
    Write-Host ""
    Write-Host "  NOTE: A reboot is recommended to complete installation." -ForegroundColor Yellow
    $ans = Read-Host "  Reboot now? (y/n)"
    if ($ans -eq "y") { Restart-Computer -Force }
}
