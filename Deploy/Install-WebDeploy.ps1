#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs Web Deploy 4.0 and starts Web Management Service (WMSvc)
    Run this on the Azure VM via RDP.
#>

$ErrorActionPreference = "Stop"
function Write-Step { param($m) Write-Host "`n==  $m" -ForegroundColor Cyan }
function Write-OK   { param($m) Write-Host "  [OK]  $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  [WARN] $m" -ForegroundColor Yellow }

# ── 1. Download Web Deploy 4.0 ────────────────────────────────────────────────
Write-Step "1 / 4  Downloading Web Deploy 4.0"
$url  = "https://download.microsoft.com/download/0/1/D/01DC28EA-638C-4A22-A57B-4CEF97755C6C/WebDeploy_amd64_en-US.msi"
$msi  = "C:\PiiRemover-Setup\WebDeploy_amd64.msi"
New-Item -ItemType Directory -Force "C:\PiiRemover-Setup" | Out-Null

if (Test-Path $msi) {
    Write-OK "Already downloaded"
} else {
    Write-Host "  Downloading (~5 MB)..." -NoNewline
    Invoke-WebRequest -Uri $url -OutFile $msi -UseBasicParsing
    Write-Host " done" -ForegroundColor Green
}

# ── 2. Install Web Deploy (full, including WMSvc integration) ─────────────────
Write-Step "2 / 4  Installing Web Deploy 4.0 (all features)"
$proc = Start-Process msiexec -ArgumentList `
    "/i `"$msi`" /quiet /norestart ADDLOCAL=ALL" `
    -Wait -PassThru
if ($proc.ExitCode -in 0, 3010) {
    Write-OK "Web Deploy installed (exit=$($proc.ExitCode))"
} else {
    Write-Host "  [FAIL] Installer exit code: $($proc.ExitCode)" -ForegroundColor Red
    exit 1
}

# ── 3. Start and auto-start Web Management Service (WMSvc) ───────────────────
Write-Step "3 / 4  Configuring Web Management Service (WMSvc) on port 8172"

Set-Service -Name "WMSvc" -StartupType Automatic
Start-Service -Name "WMSvc" -ErrorAction SilentlyContinue

$svc = Get-Service "WMSvc"
if ($svc.Status -eq "Running") {
    Write-OK "WMSvc is running"
} else {
    Write-Host "  [FAIL] WMSvc failed to start" -ForegroundColor Red
    exit 1
}

# ── 4. Open Windows Firewall port 8172 ───────────────────────────────────────
Write-Step "4 / 4  Opening Windows Firewall port 8172 (Web Deploy)"

$existing = Get-NetFirewallRule -DisplayName "WebDeploy 8172" -ErrorAction SilentlyContinue
if ($existing) {
    Write-OK "Firewall rule already exists"
} else {
    New-NetFirewallRule `
        -DisplayName "WebDeploy 8172" `
        -Direction Inbound `
        -Protocol TCP `
        -LocalPort 8172 `
        -Action Allow | Out-Null
    Write-OK "Firewall rule created for port 8172"
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Web Deploy ready!" -ForegroundColor Green
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "  IMPORTANT: Also add port 8172 in Azure NSG!" -ForegroundColor Yellow
Write-Host "  Azure Portal -> VM -> Networking -> Add inbound rule:" -ForegroundColor Yellow
Write-Host "    Port: 8172  Protocol: TCP  Action: Allow" -ForegroundColor White
Write-Host ""
Write-Host "  Then retry Publish from Visual Studio." -ForegroundColor White
Write-Host ""
