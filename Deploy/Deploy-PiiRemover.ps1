#Requires -RunAsAdministrator
<#
.SYNOPSIS
    PiiRemover — one-click publish + deploy to Azure VM over WinRM / file share.

.PARAMETER VmIp
    Public IP of the Azure VM (e.g. 20.123.45.67)

.PARAMETER VmUser
    VM administrator username (default: azureuser)

.PARAMETER VmPassword
    VM administrator password (will prompt if not supplied)

.PARAMETER LicensePath
    Path to your license.lic file to copy to the VM (optional)

.EXAMPLE
    .\Deploy-PiiRemover.ps1 -VmIp 20.123.45.67 -VmUser azureuser
#>
param(
    [Parameter(Mandatory)][string]$VmIp,
    [string]$VmUser      = "azureuser",
    [string]$VmPassword  = "",
    [string]$LicensePath = ""
)

$ErrorActionPreference = "Stop"
$SolutionRoot = Split-Path $PSScriptRoot -Parent
$PublishOutput = "$SolutionRoot\publish\api"
$RemoteSitePath = "C:\inetpub\piiremover"

function Write-Step { param($msg) Write-Host "`n==  $msg" -ForegroundColor Cyan }
function Write-OK   { param($msg) Write-Host "  [OK]  $msg" -ForegroundColor Green }
function Write-Fail { param($msg) Write-Host "  [FAIL] $msg" -ForegroundColor Red; exit 1 }

# ── Credentials ───────────────────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($VmPassword)) {
    $secPwd = Read-Host "Password for $VmUser@$VmIp" -AsSecureString
} else {
    $secPwd = ConvertTo-SecureString $VmPassword -AsPlainText -Force
}
$cred = New-Object PSCredential($VmUser, $secPwd)

# ── Step 1: dotnet publish ─────────────────────────────────────────────────────
Write-Step "1 / 4  Publishing PiiRemover.Api (Release)"
if (Test-Path $PublishOutput) { Remove-Item $PublishOutput -Recurse -Force }
Push-Location $SolutionRoot
dotnet publish PiiRemover.Api -c Release -o $PublishOutput --nologo
if ($LASTEXITCODE -ne 0) { Write-Fail "dotnet publish failed" }
Pop-Location
Write-OK "Published to: $PublishOutput"

# ── Step 2: Copy tessdata if present ──────────────────────────────────────────
$tessdata = "$SolutionRoot\PiiRemover.Api\tessdata"
if (Test-Path $tessdata) {
    Copy-Item $tessdata "$PublishOutput\tessdata" -Recurse -Force
    Write-OK "tessdata folder included in publish output"
}

# ── Step 3: Copy files to VM via WinRM (PowerShell remoting) ──────────────────
Write-Step "2 / 4  Connecting to VM at $VmIp via WinRM"

# Trust the VM host
$trustedHosts = (Get-Item WSMan:\localhost\Client\TrustedHosts).Value
if ($trustedHosts -notmatch [regex]::Escape($VmIp)) {
    Set-Item WSMan:\localhost\Client\TrustedHosts -Value "$trustedHosts,$VmIp" -Force
    Write-OK "Added $VmIp to WinRM TrustedHosts"
}

$session = New-PSSession -ComputerName $VmIp -Credential $cred -ErrorAction Stop
Write-OK "Connected to VM"

Write-Step "3 / 4  Stopping IIS, copying files, restarting IIS"

# Stop IIS on remote
Invoke-Command -Session $session -ScriptBlock {
    iisreset /stop | Out-Null
    Write-Host "  [VM] IIS stopped"
}

# Copy published files
Write-Host "  Copying published files to VM..." -NoNewline
Copy-Item "$PublishOutput\*" -Destination $RemoteSitePath `
          -ToSession $session -Recurse -Force
Write-Host " done" -ForegroundColor Green
Write-OK "Files copied to $RemoteSitePath"

# Copy license if provided
if ($LicensePath -and (Test-Path $LicensePath)) {
    Copy-Item $LicensePath -Destination "$RemoteSitePath\license.lic" -ToSession $session -Force
    Write-OK "license.lic deployed"
} else {
    Write-Host "  [WARN] No license.lic copied — place it manually in $RemoteSitePath" -ForegroundColor Yellow
}

# Start IIS on remote
Invoke-Command -Session $session -ScriptBlock {
    iisreset /start | Out-Null
    Write-Host "  [VM] IIS started"
}

Remove-PSSession $session

# ── Step 4: Smoke test ────────────────────────────────────────────────────────
Write-Step "4 / 4  Smoke test — GET http://$VmIp/api/v1/health"
Start-Sleep -Seconds 3
try {
    $resp = Invoke-WebRequest -Uri "http://$VmIp/api/v1/health" -UseBasicParsing -TimeoutSec 10
    if ($resp.StatusCode -in 200, 503) {
        Write-OK "Health endpoint responded HTTP $($resp.StatusCode)"
        Write-Host "  $($resp.Content)" -ForegroundColor Gray
    } else {
        Write-Host "  [WARN] Unexpected status: $($resp.StatusCode)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "  [WARN] Health check failed: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "  The site may still be starting — check http://$VmIp/swagger" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Deployment complete!" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Swagger UI : http://$VmIp/swagger" -ForegroundColor White
Write-Host "  Health     : http://$VmIp/api/v1/health" -ForegroundColor White
Write-Host "  Admin      : http://$VmIp/admin" -ForegroundColor White
Write-Host ""
