#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Registers the "PiiRemover" dedicated Windows Event Log source.
.DESCRIPTION
    Creates a custom log "PiiRemover" under Applications and Services Logs,
    and registers "PiiRemover" as its event source.
    If the source was previously registered under a different log (e.g. Application),
    it is removed and re-created under the correct log.
    Run once on each machine that hosts the PiiRemover API.
#>

$SourceName = "PiiRemover"
$LogName    = "PiiRemover"

Write-Host ""
Write-Host "  PiiRemover -- Register Windows Event Log Source" -ForegroundColor White
Write-Host "  ================================================" -ForegroundColor DarkGray
Write-Host ""

# ── Check / clean up existing registration ────────────────────────────────────
if ([System.Diagnostics.EventLog]::SourceExists($SourceName))
{
    $existing = [System.Diagnostics.EventLog]::LogNameFromSourceName($SourceName, ".")

    if ($existing -eq $LogName)
    {
        Write-Host "  [OK]  Source '$SourceName' already registered under log '$LogName'." -ForegroundColor Green
        Write-Host ""

        # Confirm with a test write
        try
        {
            [System.Diagnostics.EventLog]::WriteEntry(
                $SourceName,
                "PiiRemover event log verified.",
                [System.Diagnostics.EventLogEntryType]::Information,
                1000)
            Write-Host "  [OK]  Test entry written to '$LogName'." -ForegroundColor Green
            Write-Host "        Open: Event Viewer > Applications and Services Logs > $LogName" -ForegroundColor DarkGray
        }
        catch
        {
            Write-Host "  [WARN] Write test failed: $_" -ForegroundColor Yellow
        }

        Write-Host ""
        exit 0
    }

    # Wrong log -- remove and re-register
    Write-Host "  [..] Source registered under '$existing' (wrong log). Re-registering under '$LogName'..." -ForegroundColor Yellow
    try
    {
        [System.Diagnostics.EventLog]::DeleteEventSource($SourceName)
        Write-Host "  [OK]  Old registration removed." -ForegroundColor Green
    }
    catch
    {
        Write-Host "  [ERR] Could not remove old registration: $_" -ForegroundColor Red
        Write-Host ""
        exit 1
    }
}

# ── Create event source (also creates the dedicated log) ──────────────────────
Write-Host "  [..] Registering source '$SourceName' under log '$LogName'..." -ForegroundColor Gray
try
{
    $params = New-Object System.Diagnostics.EventSourceCreationData($SourceName, $LogName)
    [System.Diagnostics.EventLog]::CreateEventSource($params)
    Write-Host "  [OK]  Source '$SourceName' registered under log '$LogName'." -ForegroundColor Green
}
catch
{
    Write-Host "  [ERR] Registration failed: $_" -ForegroundColor Red
    Write-Host ""
    exit 1
}

# ── Write test entry ──────────────────────────────────────────────────────────
Write-Host "  [..] Writing test entry..." -ForegroundColor Gray
try
{
    [System.Diagnostics.EventLog]::WriteEntry(
        $SourceName,
        "PiiRemover event log source registered successfully.",
        [System.Diagnostics.EventLogEntryType]::Information,
        1000)
    Write-Host "  [OK]  Test entry written." -ForegroundColor Green
    Write-Host "        Open: Event Viewer > Applications and Services Logs > $LogName" -ForegroundColor DarkGray
}
catch
{
    Write-Host "  [WARN] Source registered but test write failed: $_" -ForegroundColor Yellow
    Write-Host "         A system restart may be required for the new log to become active." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  Done. Restart the PiiRemover API for the change to take effect." -ForegroundColor Green
Write-Host ""
exit 0
