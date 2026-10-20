#Requires -RunAsAdministrator
# Creates the local 'docker' group and sets the Windows Docker engine to allow it (besides administrators).
# The group starts empty; Grant-BuildDockerAccess.ps1 -Group 'docker' later adds only the SourceVault build service.
# Safe to run again. Keeps any other settings already in daemon.json.
#   powershell -ExecutionPolicy Bypass -File .\Set-DockerGroup.ps1
$ErrorActionPreference = 'Stop'
$group = 'docker'
$daemon = Join-Path $env:ProgramData 'docker\config\daemon.json'

if (!(Get-Service -Name docker -ErrorAction SilentlyContinue)) { throw 'The docker service is not installed (guide step 2: install-docker-ce.ps1).' }

if (Get-LocalGroup -Name $group -ErrorAction SilentlyContinue) { Write-Host "[ok] group '$group' already exists" }
else {
    New-LocalGroup -Name $group -Description 'May use Docker (SourceVault build service only)' | Out-Null
    Write-Host "[ok] created group '$group'"
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $daemon) | Out-Null
$settings = if (Test-Path -LiteralPath $daemon) { Get-Content -LiteralPath $daemon -Raw | ConvertFrom-Json } else { New-Object PSObject }
if ($null -eq $settings) { $settings = New-Object PSObject }
$settings | Add-Member -NotePropertyName hosts -NotePropertyValue @('npipe://') -Force
$settings | Add-Member -NotePropertyName group -NotePropertyValue $group -Force
[IO.File]::WriteAllText($daemon, ($settings | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
Write-Host "[ok] wrote $daemon"

Write-Host '[..] restarting docker'
Restart-Service docker
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Seconds 2; $os = (& docker version --format '{{.Server.Os}}' 2>$null) -join '' } until ($os -eq 'windows' -or (Get-Date) -gt $deadline)
if ($os -ne 'windows') { throw 'The docker engine did not come back within 60 seconds. Check: Get-EventLog -LogName Application -Source docker -Newest 10' }

Write-Host ''
Get-Content -LiteralPath $daemon
Write-Host ("[ok] engine running: server {0} windows; administrators and '{1}' may use \\.\pipe\docker_engine" -f ((& docker version --format '{{.Server.Version}}') -join ''), $group) -ForegroundColor Green
Write-Host "Next: guide step 3 (Setup-SourceVault.ps1 -Update -WithBuilds)."
