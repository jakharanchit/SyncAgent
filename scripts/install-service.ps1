#Requires -RunAsAdministrator
# Installs SyncAgent as a Windows Service.
# Copy this file to the same folder as SyncAgent.exe before running.
# Run from that folder as Administrator.

$serviceName = "SyncAgent"
$displayName = "SyncAgent"
$description = "Syncs local SQLite data to central PostgreSQL."
$exePath     = Join-Path $PSScriptRoot "SyncAgent.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "SyncAgent.exe not found at: $exePath`nRun this script from the folder containing SyncAgent.exe."
    exit 1
}

$existing = sc.exe query $serviceName 2>$null
if ($existing -match "RUNNING|STOPPED|PAUSED") {
    Write-Host "Service '$serviceName' already exists. Stopping and removing first..."
    sc.exe stop   $serviceName | Out-Null
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $serviceName binPath= "`"$exePath`"" start= auto obj= LocalSystem DisplayName= $displayName
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create service."; exit 1 }

sc.exe description $serviceName $description

# Auto-restart on failure: 3 attempts, 60 s delay each, reset failure count after 24 h
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000

sc.exe start $serviceName
if ($LASTEXITCODE -ne 0) {
    Write-Error "Service registered but failed to start. Check syncagent.json (especially SQLitePath and Postgres:ConnectionString)."
    exit 1
}

Start-Sleep -Seconds 3
sc.exe query $serviceName

Write-Host ""
Write-Host "SyncAgent service installed and started."
Write-Host "Log location is configured via Logging:LogPath in syncagent.json (default: ./logs next to the exe)."
