#Requires -RunAsAdministrator
<#
.SYNOPSIS
    PiiRemover VM Prerequisites Installer
    Run this script ONCE on a fresh Windows Server 2022 Azure VM via RDP.
    It installs: IIS, ASP.NET Core 10 Hosting Bundle, URL Rewrite, Windows Auth,
                 Hebrew OCR language pack, and Event Log source registration.
#>

$ErrorActionPreference = "Stop"

function Write-Step  { param($msg) Write-Host "`n==  $msg" -ForegroundColor Cyan }
function Write-OK    { param($msg) Write-Host "  [OK]  $msg" -ForegroundColor Green }
function Write-Warn  { param($msg) Write-Host "  [WARN] $msg" -ForegroundColor Yellow }
function Write-Fail  { param($msg) Write-Host "  [FAIL] $msg" -ForegroundColor Red }

$TempDir = "C:\PiiRemover-Setup"
New-Item -ItemType Directory -Force $TempDir | Out-Null

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "1 / 7  Installing IIS and required Windows features"
# ─────────────────────────────────────────────────────────────────────────────
$features = @(
    "Web-Server",                    # IIS core
    "Web-WebServer",
    "Web-Common-Http",               # Default doc, static content, etc.
    "Web-Default-Doc",
    "Web-Static-Content",
    "Web-Http-Errors",
    "Web-Http-Redirect",
    "Web-Health",
    "Web-Http-Logging",
    "Web-Performance",
    "Web-Stat-Compression",
    "Web-Security",
    "Web-Filtering",
    "Web-Windows-Auth",              # Windows Authentication
    "Web-Basic-Auth",
    "Web-App-Dev",
    "Web-Net-Ext45",
    "Web-Asp-Net45",
    "Web-ISAPI-Ext",
    "Web-ISAPI-Filter",
    "Web-Mgmt-Tools",                # IIS Manager
    "Web-Mgmt-Console",
    "Web-Mgmt-Compat",
    "Web-Metabase",
    "NET-Framework-45-ASPNET",       # .NET Framework 4.5 ASP.NET
    "NET-WCF-HTTP-Activation45"
)

foreach ($f in $features) {
    $state = (Get-WindowsFeature -Name $f).InstallState
    if ($state -eq "Installed") {
        Write-OK "$f (already installed)"
    } else {
        Install-WindowsFeature -Name $f -IncludeManagementTools | Out-Null
        Write-OK "$f installed"
    }
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "2 / 7  Downloading .NET 10 ASP.NET Core Hosting Bundle"
# ─────────────────────────────────────────────────────────────────────────────
$hostingBundleUrl = "https://download.visualstudio.microsoft.com/download/pr/97f57a73-8a25-4e76-8f21-d1462ed21b18/3b3ede39e50a680534e40fce2a33614c/dotnet-hosting-10.0.0-win.exe"
$hostingBundlePath = "$TempDir\dotnet-hosting-10-win.exe"

if (Test-Path $hostingBundlePath) {
    Write-OK "Hosting bundle already downloaded"
} else {
    Write-Host "  Downloading .NET 10 Hosting Bundle (~70 MB)..." -NoNewline
    try {
        Invoke-WebRequest -Uri $hostingBundleUrl -OutFile $hostingBundlePath -UseBasicParsing
        Write-Host " done" -ForegroundColor Green
    } catch {
        Write-Warn "Direct download failed. Trying alternative..."
        # Fallback: use dotnet-install script approach
        Write-Fail "Cannot download automatically. Please download manually from:"
        Write-Host "  https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
        Write-Host "  -> Look for 'Hosting Bundle' under Windows" -ForegroundColor Yellow
        Write-Host "  -> Place in: $hostingBundlePath" -ForegroundColor Yellow
        Write-Host "  Then re-run this script." -ForegroundColor Yellow
        exit 1
    }
}

Write-Step "2b / 7  Installing .NET 10 Hosting Bundle (silent)"
$proc = Start-Process -FilePath $hostingBundlePath `
    -ArgumentList "/install", "/quiet", "/norestart", "OPT_NO_SHAREDFX=0" `
    -Wait -PassThru
if ($proc.ExitCode -eq 0 -or $proc.ExitCode -eq 3010) {
    Write-OK ".NET 10 Hosting Bundle installed (exit=$($proc.ExitCode))"
    if ($proc.ExitCode -eq 3010) { Write-Warn "Reboot required — will reboot at end of script" }
} else {
    Write-Fail ".NET 10 Hosting Bundle installer exited with code $($proc.ExitCode)"
    exit 1
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "3 / 7  Downloading and installing IIS URL Rewrite Module"
# ─────────────────────────────────────────────────────────────────────────────
$urlRewriteUrl  = "https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi"
$urlRewritePath = "$TempDir\urlrewrite2.msi"

$rewriteInstalled = Get-WmiObject -Class Win32_Product | Where-Object { $_.Name -like "*URL Rewrite*" }
if ($rewriteInstalled) {
    Write-OK "URL Rewrite Module already installed"
} else {
    Write-Host "  Downloading URL Rewrite Module..." -NoNewline
    Invoke-WebRequest -Uri $urlRewriteUrl -OutFile $urlRewritePath -UseBasicParsing
    Write-Host " done" -ForegroundColor Green
    $proc = Start-Process msiexec -ArgumentList "/i `"$urlRewritePath`" /quiet /norestart" -Wait -PassThru
    if ($proc.ExitCode -eq 0) { Write-OK "URL Rewrite Module installed" }
    else { Write-Warn "URL Rewrite installer exited with code $($proc.ExitCode) (may be OK)" }
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "4 / 7  Configuring IIS — resetting to pick up new modules"
# ─────────────────────────────────────────────────────────────────────────────
& "$env:SystemRoot\System32\inetsrv\appcmd.exe" list module /name:AspNetCoreModuleV2 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-OK "AspNetCoreModuleV2 registered in IIS"
} else {
    Write-Warn "AspNetCoreModuleV2 not found yet — running iisreset"
}
iisreset /restart | Out-Null
Write-OK "IIS restarted"

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "5 / 7  Creating IIS site structure for PiiRemover"
# ─────────────────────────────────────────────────────────────────────────────
$sitePath = "C:\inetpub\piiremover"
New-Item -ItemType Directory -Force $sitePath | Out-Null
New-Item -ItemType Directory -Force "$sitePath\logs" | Out-Null
Write-OK "Site folder: $sitePath"

Import-Module WebAdministration -ErrorAction SilentlyContinue

# App pool — No Managed Code (required for .NET Core / in-process)
if (Test-Path "IIS:\AppPools\PiiRemover") {
    Write-OK "App pool 'PiiRemover' already exists"
} else {
    New-WebAppPool -Name "PiiRemover" | Out-Null
    Set-ItemProperty "IIS:\AppPools\PiiRemover" managedRuntimeVersion ""   # No Managed Code
    Set-ItemProperty "IIS:\AppPools\PiiRemover" startMode "AlwaysRunning"
    Set-ItemProperty "IIS:\AppPools\PiiRemover" autoStart $true
    Set-ItemProperty "IIS:\AppPools\PiiRemover" processModel.idleTimeout "00:00:00"  # Never idle
    Write-OK "App pool 'PiiRemover' created (No Managed Code, AlwaysRunning)"
}

# Website
if (Get-Website -Name "PiiRemover" -ErrorAction SilentlyContinue) {
    Write-OK "Website 'PiiRemover' already exists"
} else {
    New-Website -Name "PiiRemover" `
                -PhysicalPath $sitePath `
                -ApplicationPool "PiiRemover" `
                -Port 80 `
                -Force | Out-Null
    Write-OK "Website 'PiiRemover' created on port 80"
}

# Folder permissions — IIS_IUSRS needs write (for SQLite DB and logs)
$acl = Get-Acl $sitePath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "IIS_IUSRS", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($rule)
Set-Acl $sitePath $acl
Write-OK "IIS_IUSRS granted Modify on $sitePath"

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "6 / 7  Registering Windows Event Log source for PiiRemover"
# ─────────────────────────────────────────────────────────────────────────────
$logName    = "PiiRemover"
$sourceName = "PiiRemover"

# Remove stale registration if pointing to wrong log
if ([System.Diagnostics.EventLog]::SourceExists($sourceName)) {
    $existingLog = [System.Diagnostics.EventLog]::LogNameFromSourceName($sourceName, ".")
    if ($existingLog -ne $logName) {
        Write-Warn "Source '$sourceName' registered under '$existingLog' — re-registering under '$logName'"
        [System.Diagnostics.EventLog]::DeleteEventSource($sourceName)
    } else {
        Write-OK "Event Log source '$sourceName' already registered under '$logName'"
    }
}

if (-not [System.Diagnostics.EventLog]::SourceExists($sourceName)) {
    $data = New-Object System.Diagnostics.EventSourceCreationData($sourceName, $logName)
    [System.Diagnostics.EventLog]::CreateEventSource($data)
    Write-OK "Event Log source '$sourceName' created under log '$logName'"
}

# Write a test entry
$el = New-Object System.Diagnostics.EventLog($logName)
$el.Source = $sourceName
$el.WriteEntry("PiiRemover prerequisites installed successfully.", "Information", 1000)
Write-OK "Test entry written to '$logName' event log"

# ─────────────────────────────────────────────────────────────────────────────
Write-Step "7 / 7  Installing Hebrew OCR language pack"
# ─────────────────────────────────────────────────────────────────────────────
$heLang = Get-InstalledLanguage | Where-Object { $_.LanguageId -eq "he-IL" }
if ($heLang) {
    Write-OK "Hebrew (he-IL) language already installed"
} else {
    Write-Host "  Installing Hebrew language pack (he-IL)..." -NoNewline
    try {
        Install-Language -Language "he-IL" -CopyToSettings -ExcludeFeatures 2>&1 | Out-Null
        Write-Host " done" -ForegroundColor Green
        Write-OK "Hebrew (he-IL) installed"
    } catch {
        Write-Warn "Install-Language failed — trying DISM fallback"
        try {
            DISM /Online /Add-Capability /CapabilityName:Language.OCR~~~he-IL~0.0.1.0 /NoRestart 2>&1 | Out-Null
            Write-OK "Hebrew OCR capability installed via DISM"
        } catch {
            Write-Warn "Could not install Hebrew OCR automatically."
            Write-Host "  Manual step: Settings -> Time & Language -> Language & Region" -ForegroundColor Yellow
            Write-Host "  -> Add Hebrew (he-IL) -> install OCR pack" -ForegroundColor Yellow
        }
    }
}

# ─────────────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Prerequisites installation complete!" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "  IIS site  : http://<your-vm-ip>/" -ForegroundColor White
Write-Host "  Site path : $sitePath" -ForegroundColor White
Write-Host "  App pool  : PiiRemover (No Managed Code)" -ForegroundColor White
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run Deploy-PiiRemover.ps1 from your DEV machine to copy the app" -ForegroundColor Yellow
Write-Host "  2. Place license.lic in $sitePath" -ForegroundColor Yellow
Write-Host "  3. Restart IIS:  iisreset" -ForegroundColor Yellow
Write-Host ""

# Prompt reboot if hosting bundle required it (exit code 3010)
$needReboot = $proc.ExitCode -eq 3010
if ($needReboot) {
    Write-Warn "A reboot is required to complete the .NET Hosting Bundle installation."
    $ans = Read-Host "  Reboot now? (y/n)"
    if ($ans -eq "y") { Restart-Computer -Force }
}
