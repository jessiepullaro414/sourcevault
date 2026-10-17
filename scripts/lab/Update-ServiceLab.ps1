#Requires -RunAsAdministrator
# LAB ONLY. Replaces the installed service binaries with a fresh artifacts\service-publish build, keeping the
# installed config, runtime, service registration and TPM key. Re-records the hash manifest.
param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'SourceVault'),
    [string]$PublishPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (!$PublishPath) { $PublishPath = Join-Path $repoRoot 'artifacts\service-publish' }
$serviceName = 'SourceVault'
$serviceDir = Join-Path $InstallRoot 'service'
$configName = 'sourcevault.service.json'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (!$service -or !(Test-Path -LiteralPath (Join-Path $InstallRoot 'install-manifest.sha256'))) { throw 'No lab installation found. Use Install-ServiceLab.ps1.' }
if (!(Test-Path -LiteralPath (Join-Path $PublishPath 'SourceVault.Service.dll'))) { throw "No build in $PublishPath. Run Publish-ServiceLab.ps1 (as your normal user) first." }
if ($service.Status -ne 'Stopped') {
    & sc.exe stop $serviceName | Out-Null
    $deadline = (Get-Date).AddSeconds(30)
    do { Start-Sleep -Milliseconds 250; $service.Refresh() } while ($service.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
    if ($service.Status -ne 'Stopped') { throw 'Service did not stop; nothing changed.' }
}

Get-ChildItem -LiteralPath $serviceDir | Where-Object Name -ne $configName | Remove-Item -Recurse -Force
Get-ChildItem -LiteralPath $PublishPath | Where-Object Name -ne $configName | Copy-Item -Destination $serviceDir -Recurse
$manifest = Get-ChildItem -LiteralPath $InstallRoot -Recurse -File | Where-Object Name -ne 'install-manifest.sha256' | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($InstallRoot.Length + 1)
}
[IO.File]::WriteAllLines((Join-Path $InstallRoot 'install-manifest.sha256'), [string[]]$manifest, (New-Object Text.UTF8Encoding $false))
Write-Output "Updated service binaries in $serviceDir; config kept; $($manifest.Count) files re-hashed."
