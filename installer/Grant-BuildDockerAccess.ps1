#Requires -RunAsAdministrator
# Gives the SourceVault build service (NT SERVICE\SourceVaultBuild) access to a Windows Docker engine, by adding it to
# the local group that the engine's named pipe allows, then restarts the build service so its token includes it.
#
#   Grant-BuildDockerAccess.ps1                       group 'docker-users' (Docker Desktop's default)
#   Grant-BuildDockerAccess.ps1 -Group 'docker'       a standalone dockerd whose daemon.json has  "group": "docker"
#   Grant-BuildDockerAccess.ps1 -Revoke               remove the build service from the group again
#
# BE AWARE: whoever can use a Docker engine can run containers that mount C:\ — Docker access is administrator-
# equivalent on this computer. That is why only the build service gets it, never the vault service or operators.
# LAB SCRIPT: not run during development; verify on the target machine.
param([string]$Group = 'docker-users', [switch]$Revoke)
$ErrorActionPreference = 'Stop'
$account = 'NT SERVICE\SourceVaultBuild'
if (!(Get-Service -Name 'SourceVaultBuild' -ErrorAction SilentlyContinue)) { throw 'The build service is not installed. Run Setup-SourceVault.ps1 -Update -WithBuilds first.' }
if (!(Get-LocalGroup -Name $Group -ErrorAction SilentlyContinue)) {
    throw "Local group '$Group' does not exist. For a standalone dockerd, create it, add `"group`": `"$Group`" to C:\ProgramData\docker\config\daemon.json and restart the docker service; Docker Desktop creates 'docker-users'."
}
$sid = (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value
$member = Get-LocalGroupMember -Group $Group | Where-Object { $_.SID.Value -eq $sid }
if ($Revoke) {
    if ($member) { Remove-LocalGroupMember -Group $Group -Member $account; Write-Output "Removed $account from '$Group'." } else { Write-Output "$account was not in '$Group'." }
} else {
    if ($member) { Write-Output "$account is already in '$Group'." } else { Add-LocalGroupMember -Group $Group -Member $account; Write-Output "Added $account to '$Group'." }
}
$service = Get-Service -Name 'SourceVaultBuild'
if ($service.Status -eq 'Running') {
    Restart-Service -Name 'SourceVaultBuild' -Force
    Write-Output 'Restarted the build service so the change takes effect.'
}
Write-Output 'Check the engine: the pipe in build.json (default npipe://./pipe/docker_engine) must belong to an engine that allows this group.'
