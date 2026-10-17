#Requires -RunAsAdministrator
# LAB ONLY. Starts the service once with the fixed `provision-key` parameter. Running as NT SERVICE\SourceVault,
# it creates its decrypt-only TPM key (never overwriting one), checks policy, export refusal and a synthetic
# wrap/unwrap, then stops. The result is read from the service exit codes; nothing is logged.
# Anything later wrapped by this key is unrecoverable if the TPM is cleared or the service profile is lost.
$ErrorActionPreference = 'Stop'
$serviceName = 'SourceVault'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (!$service) { throw 'Service not installed. Run Install-ServiceLab.ps1 first.' }
if ($service.Status -ne 'Stopped') { throw "Service is $($service.Status); stop it first." }

$output = & sc.exe start $serviceName provision-key
if ($LASTEXITCODE -ne 0) { $output | Write-Output; throw "sc.exe start failed with exit $LASTEXITCODE (1069 = logon failure, 1053 = no response)." }
$deadline = (Get-Date).AddSeconds(120)
do { Start-Sleep -Milliseconds 500; $service.Refresh() } while ($service.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
if ($service.Status -ne 'Stopped') { throw 'Provisioning did not finish within 120 seconds.' }

$state = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
$outcomes = @{
    0 = 'Created: decrypt-only TPM key created under NT SERVICE\SourceVault; export refused; synthetic roundtrip passed.'
    1 = 'AlreadyExists: the configured key already exists. Nothing was changed.'
    2 = 'Unavailable: wrong identity, missing config/provider, or key creation failed.'
    3 = 'PolicyRejected: a key was created but failed the startup policy checks. It was left in place.'
    4 = 'ExportNotRefused: a key was created but its private key could be exported. It was left in place. Do not use it.'
    5 = 'RoundtripFailed: a key was created and opened but wrap/unwrap did not match. It was left in place.'
}
if ($state.ExitCode -eq 0) { Write-Output $outcomes[0]; Write-Output 'Next: scripts\lab\Test-ServiceLab.ps1'; exit 0 }
if ($state.ExitCode -eq 1066 -and $outcomes.ContainsKey([int]$state.ServiceSpecificExitCode)) { Write-Output $outcomes[[int]$state.ServiceSpecificExitCode]; exit 1 }
Write-Output "Service stopped with Win32 exit code $($state.ExitCode) (87 = invalid start parameter, 1064 = unexpected failure)."
exit 1
