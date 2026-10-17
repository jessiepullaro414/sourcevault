#Requires -RunAsAdministrator
# LAB ONLY. Stops and deletes the SourceVault service and removes its Program Files folder.
# The service's TPM key stays in the NT SERVICE\SourceVault profile; this script does not delete keys.
param([string]$InstallRoot = (Join-Path $env:ProgramFiles 'SourceVault'))
$ErrorActionPreference = 'Stop'
$serviceName = 'SourceVault'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        & sc.exe stop $serviceName | Out-Null
        $deadline = (Get-Date).AddSeconds(30)
        do { Start-Sleep -Milliseconds 250; $service.Refresh() } while ($service.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
        if ($service.Status -ne 'Stopped') { throw 'Service did not stop; nothing removed.' }
    }
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit $LASTEXITCODE." }
    Write-Output "Deleted service '$serviceName'."
} else { Write-Output "Service '$serviceName' is not installed." }

# Only remove a folder that looks like this installer's output.
if (Test-Path -LiteralPath $InstallRoot) {
    $looksInstalled = (Test-Path -LiteralPath (Join-Path $InstallRoot 'install-manifest.sha256')) -and
        (Test-Path -LiteralPath (Join-Path $InstallRoot 'service\SourceVault.Service.dll'))
    if (!$looksInstalled) { throw "$InstallRoot does not look like a SourceVault lab install; not removed." }
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    Write-Output "Removed $InstallRoot."
}
Write-Output 'The service TPM key (if provisioned) remains in the NT SERVICE\SourceVault profile.'
