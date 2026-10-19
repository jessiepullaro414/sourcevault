#Requires -RunAsAdministrator
# LAB ONLY. Normal start/stop check: the service must reach RUNNING only after opening its own TPM key as
# NT SERVICE\SourceVault, then stop cleanly. Run again after a reboot to check the key survives restarts.
# -LeaveRunning skips the stop, e.g. to run Test-PipeAccess.ps1 from a normal window; stop later with: sc.exe stop SourceVault
param([switch]$LeaveRunning)
$ErrorActionPreference = 'Stop'
$serviceName = 'SourceVault'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (!$service) { throw 'Service not installed. Run Install-ServiceLab.ps1 first.' }
if ($service.Status -ne 'Stopped') { throw "Service is $($service.Status); stop it first." }
function Wait-State([string[]]$states, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { Start-Sleep -Milliseconds 250; $service.Refresh() } while ($states -notcontains [string]$service.Status -and (Get-Date) -lt $deadline)
}

$started = Get-Date
$output = & sc.exe start $serviceName
if ($LASTEXITCODE -ne 0) { $output | Write-Output; throw "sc.exe start failed with exit $LASTEXITCODE." }
Wait-State 'Running', 'Stopped' 60
$state = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if ($service.Status -ne 'Running') {
    $reason = if ($state.ExitCode -eq 1066 -and $state.ServiceSpecificExitCode -eq 10) { 'vault root missing or untrusted; run New-VaultRoot.ps1 or the service''s vault-root-check' }
        elseif ($state.ExitCode -eq 1066 -and $state.ServiceSpecificExitCode -eq 17) { 'the vault drive is missing, offline or locked (e.g. BitLocker); unlock or attach it' }
        elseif ($state.ExitCode -eq 1066 -and $state.ServiceSpecificExitCode -eq 11) { 'unexpected entry in the vault, or the project index failed authentication' }
        elseif ($state.ExitCode -eq 1066 -and $state.ServiceSpecificExitCode -eq 12) { 'a project failed authentication' }
        elseif ($state.ExitCode -eq 1066 -and $state.ServiceSpecificExitCode -eq 13) { 'local group SourceVault Operators is missing; run New-OperatorsGroup.ps1' }
        elseif ($state.ExitCode -eq 1066 -and $state.ServiceSpecificExitCode -eq 14) { 'the pipe could not be created (name already taken?)' }
        elseif ($state.ExitCode -eq 1064) { 'key, config or identity check failed' } else { 'see exit code' }
    Write-Output "FAIL: service did not reach RUNNING (state $($service.Status), Win32 exit $($state.ExitCode)/$($state.ServiceSpecificExitCode): $reason)."
    exit 1
}
$processId = $state.ProcessId
$owner = Invoke-CimMethod -InputObject (Get-CimInstance Win32_Process -Filter "ProcessId=$processId") -MethodName GetOwner
$identityOk = $owner.Domain -eq 'NT SERVICE' -and $owner.User -eq 'SourceVault'
Write-Output ("{0}: RUNNING after {1:N1}s; process {2} runs as {3}\{4}" -f ($(if ($identityOk) { 'PASS' } else { 'FAIL' })), ((Get-Date) - $started).TotalSeconds, $processId, $owner.Domain, $owner.User)
# Listing pipe names does not connect to them.
$pipeOk = [bool]([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*\SourceVault.v1' })
Write-Output ("{0}: pipe \\.\pipe\SourceVault.v1 {1}" -f ($(if ($pipeOk) { 'PASS' } else { 'FAIL' })), ($(if ($pipeOk) { 'is listening' } else { 'not found' })))
if ($LeaveRunning) {
    Write-Output 'Left RUNNING. Now run scripts\lab\Test-PipeAccess.ps1 from a normal window, then: sc.exe stop SourceVault'
    if ($identityOk -and $pipeOk) { exit 0 } else { exit 1 }
}

$stopping = Get-Date
& sc.exe stop $serviceName | Out-Null
Wait-State 'Stopped' 30
$state = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
$stopOk = $service.Status -eq 'Stopped' -and $state.ExitCode -eq 0
Write-Output ("{0}: stop ended in state {1} after {2:N1}s, Win32 exit {3}" -f ($(if ($stopOk) { 'PASS' } else { 'FAIL' })), $service.Status, ((Get-Date) - $stopping).TotalSeconds, $state.ExitCode)
Start-Sleep -Seconds 1
$orphan = Get-Process -Id $processId -ErrorAction SilentlyContinue
Write-Output ("{0}: service process {1} after stop" -f ($(if ($orphan) { 'FAIL' } else { 'PASS' })), ($(if ($orphan) { 'still running' } else { 'exited' })))
if ($identityOk -and $pipeOk -and $stopOk -and !$orphan) { exit 0 } else { exit 1 }
