#Requires -RunAsAdministrator
# Stops and removes the SyncAgent Windows Service.
# Run as Administrator.

$serviceName = "SyncAgent"

$existing = sc.exe query $serviceName 2>$null
if ($existing -notmatch "RUNNING|STOPPED|PAUSED") {
    Write-Host "Service '$serviceName' is not installed. Nothing to do."
    exit 0
}

Write-Host "Stopping '$serviceName'..."
sc.exe stop $serviceName | Out-Null
Start-Sleep -Seconds 3

Write-Host "Removing '$serviceName'..."
sc.exe delete $serviceName
if ($LASTEXITCODE -eq 0) {
    Write-Host "SyncAgent service removed successfully."
} else {
    Write-Error "Failed to remove service. Try again or remove via services.msc."
    exit 1
}
