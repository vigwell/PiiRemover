#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Ensures the Hebrew (he-IL) OCR language pack is installed for Windows.Media.Ocr.
.DESCRIPTION
    Checks whether he-IL is available to the Windows OCR engine.
    If not, installs the Hebrew language pack (including OCR capability) silently.
    Run this script once on any machine that will run the PiiRemover API.
#>

$ErrorActionPreference = 'Stop'

function Write-Step  { param($msg) Write-Host "  $msg" -ForegroundColor Cyan   }
function Write-Ok    { param($msg) Write-Host "  [OK]  $msg" -ForegroundColor Green  }
function Write-Fail  { param($msg) Write-Host "  [ERR] $msg" -ForegroundColor Red    }
function Write-Info  { param($msg) Write-Host "  [..] $msg"  -ForegroundColor Gray   }

Write-Host ""
Write-Host "  PiiRemover — Hebrew OCR Language Pack Setup" -ForegroundColor White
Write-Host "  ============================================" -ForegroundColor DarkGray
Write-Host ""

# ── 1. Load WinRT type ───────────────────────────────────────────────────────
Write-Step "Loading Windows.Media.Ocr runtime type..."
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
    Write-Ok "Windows.Media.Ocr loaded."
} catch {
    Write-Fail "Cannot load Windows.Media.Ocr. This script requires Windows 10 or later."
    exit 1
}

# ── 2. Check current available OCR languages ─────────────────────────────────
Write-Step "Checking installed OCR recogniser languages..."
$available = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages
$tags = $available | ForEach-Object { $_.LanguageTag }

Write-Info "Currently available OCR languages: $($tags -join ', ')"

if ($tags -contains 'he-IL') {
    Write-Ok "Hebrew (he-IL) OCR is already installed. Nothing to do."
    Write-Host ""
    exit 0
}

# ── 3. Install Hebrew language pack ──────────────────────────────────────────
Write-Host ""
Write-Step "Hebrew (he-IL) not found. Installing language pack..."

# Try Install-Language cmdlet (Windows 11 / Server 2022+)
$installLangCmd = Get-Command Install-Language -ErrorAction SilentlyContinue
if ($installLangCmd) {
    Write-Info "Using Install-Language cmdlet..."
    try {
        Install-Language -Language he-IL -CopyToSettings
        Write-Ok "Install-Language completed."
    } catch {
        Write-Fail "Install-Language failed: $_"
        Write-Info "Falling back to DISM..."
        goto UseDism
    }
} else {
    :UseDism
    Write-Info "Install-Language not available. Using DISM..."
    try {
        $dismResult = dism.exe /Online /Add-Capability /CapabilityName:Language.OCR~~~he-IL~0.0.1.0 /NoRestart 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "DISM exited with code $LASTEXITCODE`n$dismResult"
        }
        Write-Ok "DISM install completed."
    } catch {
        Write-Fail "DISM failed: $_"
        Write-Host ""
        Write-Host "  Manual steps:" -ForegroundColor Yellow
        Write-Host "  1. Settings → Time & Language → Language & region" -ForegroundColor Yellow
        Write-Host "  2. Add a language → Hebrew (עברית)" -ForegroundColor Yellow
        Write-Host "  3. Make sure 'Optical character recognition' is checked" -ForegroundColor Yellow
        Write-Host ""
        exit 1
    }
}

# ── 4. Verify installation ───────────────────────────────────────────────────
Write-Host ""
Write-Step "Verifying installation..."

# Re-query — WinRT caches, so reload in a fresh runspace
$verify = Start-Job -ScriptBlock {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
    [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag }
} | Wait-Job | Receive-Job

if ($verify -contains 'he-IL') {
    Write-Ok "Hebrew (he-IL) OCR is now installed and verified."
} else {
    Write-Info "Language installed but not yet visible to OCR engine."
    Write-Info "A system restart or sign-out may be required."
}

Write-Host ""
Write-Host "  Done. Restart the PiiRemover API for the change to take effect." -ForegroundColor Green
Write-Host ""
exit 0
