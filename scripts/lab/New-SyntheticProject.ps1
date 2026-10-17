#Requires -RunAsAdministrator
# LAB ONLY. Starts the service once with `create-synthetic-project`. Running as NT SERVICE\SourceVault, it verifies
# the whole vault, creates a project named "Synthetic demo N" holding three fixed fictional files (encrypted with
# its TPM-backed key), then re-verifies everything and stops. Result is read from the exit codes.
$ErrorActionPreference = 'Stop'
$serviceName = 'SourceVault'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (!$service) { throw 'Service not installed. Run Install-ServiceLab.ps1 first.' }
if ($service.Status -ne 'Stopped') { throw "Service is $($service.Status); stop it first." }

$output = & sc.exe start $serviceName create-synthetic-project
if ($LASTEXITCODE -ne 0) { $output | Write-Output; throw "sc.exe start failed with exit $LASTEXITCODE." }
$deadline = (Get-Date).AddSeconds(120)
do { Start-Sleep -Milliseconds 500; $service.Refresh() } while ($service.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
if ($service.Status -ne 'Stopped') { throw 'Did not finish within 120 seconds.' }

$state = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
$reasons = @{ 10 = 'vault root missing or untrusted'; 11 = 'unexpected entry in the vault or the project index failed'; 12 = 'an existing project failed authentication' }
if ($state.ExitCode -eq 0) {
    $projects = @(Get-ChildItem -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SourceVault\vault\projects') -Directory).Count
    Write-Output "Created: synthetic project encrypted under NT SERVICE\SourceVault; vault re-verified ($projects project folder(s))."
    Write-Output 'Next: scripts\lab\Test-ServiceLab.ps1 (startup now authenticates every project).'
    exit 0
}
if ($state.ExitCode -eq 1066 -and $reasons.ContainsKey([int]$state.ServiceSpecificExitCode)) { Write-Output "Refused: $($reasons[[int]$state.ServiceSpecificExitCode]) (1066/$($state.ServiceSpecificExitCode)). Nothing was created." }
else { Write-Output "Failed with Win32 exit $($state.ExitCode) (1064 = unexpected failure, 87 = invalid parameter)." }
exit 1
