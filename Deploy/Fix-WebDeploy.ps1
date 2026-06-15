#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Fixes Web Deploy 1603 install error.
    Run on the Azure VM as Administrator.
#>

$ErrorActionPreference = "SilentlyContinue"
function Write-Step { param($m) Write-Host "`n==  $m" -ForegroundColor Cyan }
function Write-OK   { param($m) Write-Host "  [OK]  $m" -ForegroundColor Green }
function Write-Warn { param($m) Write-Host "  [WARN] $m" -ForegroundColor Yellow }

$TempDir = "C:\PiiRemover-Setup"
New-Item -ItemType Directory -Force $TempDir | Out-Null

# ── 1. Install IIS Management Service Windows Feature (required for WMSvc) ────
Write-Step "1 / 5  Installing IIS Web Management Service Windows feature"
$ErrorActionPreference = "Stop"
$result = Install-WindowsFeature -Name Web-Mgmt-Service -IncludeManagementTools
Write-OK "Web-Mgmt-Service: $($result.Success) (restart=$($result.RestartNeeded))"

# Also ensure these are present
$extras = @("Web-Mgmt-Console","Web-Mgmt-Compat","Web-Metabase","Web-Lgcy-Mgmt-Console")
foreach ($f in $extras) {
    Install-WindowsFeature -Name $f -ErrorAction SilentlyContinue | Out-Null
}
Write-OK "IIS management features ensured"
$ErrorActionPreference = "SilentlyContinue"

# ── 2. Remove any broken Web Deploy remnants ──────────────────────────────────
Write-Step "2 / 5  Removing any existing/broken Web Deploy installation"
$products = Get-WmiObject -Class Win32_Product | Where-Object { $_.Name -like "*Web Deploy*" -or $_.Name -like "*MSDeploy*" }
foreach ($p in $products) {
    Write-Warn "Removing: $($p.Name)"
    $p.Uninstall() | Out-Null
}
# Clean up leftover service
$svc = Get-Service "MsDepSvc" -ErrorAction SilentlyContinue
if ($svc) {
    Stop-Service "MsDepSvc" -Force -ErrorAction SilentlyContinue
    sc.exe delete MsDepSvc | Out-Null
    Write-OK "Removed leftover MsDepSvc"
}
Start-Sleep -Seconds 2

# ── 3. Download Web Deploy 4.0 ────────────────────────────────────────────────
Write-Step "3 / 5  Downloading Web Deploy 4.0"
$msi = "$TempDir\WebDeploy_amd64.msi"
if (-not (Test-Path $msi)) {
    $url = "https://download.microsoft.com/download/0/1/D/01DC28EA-638C-4A22-A57B-4CEF97755C6C/WebDeploy_amd64_en-US.msi"
    Write-Host "  Downloading..." -NoNewline
    Invoke-WebRequest -Uri $url -OutFile $msi -UseBasicParsing -ErrorAction Stop
    Write-Host " done" -ForegroundColor Green
} else {
    Write-OK "Already downloaded"
}

# ── 4. Install Web Deploy with verbose log ────────────────────────────────────
Write-Step "4 / 5  Installing Web Deploy 4.0"
$log = "$TempDir\webdeploy-install.log"
$ErrorActionPreference = "Stop"
$proc = Start-Process msiexec -ArgumentList @(
    "/i", "`"$msi`"",
    "/quiet",
    "/norestart",
    "ADDLOCAL=ALL",
    "LicenseAccepted=0",
    "/l*v", "`"$log`""
) -Wait -PassThru

Write-Host "  Exit code: $($proc.ExitCode)"

if ($proc.ExitCode -in 0, 3010) {
    Write-OK "Web Deploy installed successfully"
} else {
    Write-Warn "Exit code $($proc.ExitCode) — checking log for cause..."
    Write-Host ""
    # Show the last 30 relevant lines from the MSI log
    if (Test-Path $log) {
        Get-Content $log | Select-String "error|fail|return value 3" -CaseSensitive:$false |
            Select-Object -Last 20 |
            ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }
    Write-Host ""
    Write-Warn "Trying alternative: install only core + WMSvc components"
    $proc2 = Start-Process msiexec -ArgumentList @(
        "/i", "`"$msi`"",
        "/quiet",
        "/norestart",
        "ADDLOCAL=MSDeployFeature,MSDeployUIFeature,MSDeployWMSVCHandlerFeature",
        "/l*v", "`"$log`""
    ) -Wait -PassThru
    Write-Host "  Alternative exit code: $($proc2.ExitCode)"
    if ($proc2.ExitCode -notin 0, 3010) {
        Write-Host ""
        Write-Host "  [FAIL] Web Deploy install failed. Log saved to:" -ForegroundColor Red
        Write-Host "  $log" -ForegroundColor Yellow
        Write-Host "  Please share this file for further diagnosis." -ForegroundColor Yellow
        exit 1
    }
}

# ── 5. Start WMSvc and open firewall ─────────────────────────────────────────
Write-Step "5 / 5  Starting Web Management Service and opening firewall"

# Enable remote connections in WMSvc registry
$regPath = "HKLM:\SOFTWARE\Microsoft\WebManagement\Server"
if (Test-Path $regPath) {
    Set-ItemProperty $regPath -Name "EnableRemoteManagement" -Value 1
    Write-OK "WMSvc remote management enabled"
}

Set-Service -Name "WMSvc" -StartupType Automatic -ErrorAction SilentlyContinue
Start-Service -Name "WMSvc" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$svc = Get-Service "WMSvc" -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") {
    Write-OK "WMSvc is running on port 8172"
} else {
    Write-Warn "WMSvc not running — trying sc start"
    sc.exe start WMSvc | Out-Null
    Start-Sleep -Seconds 3
    $svc = Get-Service "WMSvc" -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq "Running") {
        Write-OK "WMSvc started"
    } else {
        Write-Host "  [FAIL] WMSvc could not start. Check Windows Event Log." -ForegroundColor Red
    }
}

# Firewall
$existing = Get-NetFirewallRule -DisplayName "WebDeploy 8172" -ErrorAction SilentlyContinue
if (-not $existing) {
    New-NetFirewallRule -DisplayName "WebDeploy 8172" -Direction Inbound `
        -Protocol TCP -LocalPort 8172 -Action Allow | Out-Null
    Write-OK "Firewall rule added for port 8172"
} else {
    Write-OK "Firewall rule already exists"
}

# ── Done ──────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Done! Verify WMSvc is running:" -ForegroundColor Green
Write-Host "    Get-Service WMSvc" -ForegroundColor White
Write-Host ""
Write-Host "  Also make sure port 8172 is open in Azure NSG." -ForegroundColor Yellow
Write-Host "  Then retry Publish in Visual Studio." -ForegroundColor White
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Cyan
