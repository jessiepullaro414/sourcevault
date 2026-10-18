#Requires -RunAsAdministrator
# SourceVault uninstall. By default removes the program, service and Start menu shortcut, and KEEPS the encrypted vault
# and the operators group, so a later setup on this same computer can open the vault again.
#
#   Uninstall-SourceVault.ps1                         remove program files, service, shortcut
#   Uninstall-SourceVault.ps1 -RemoveOperatorsGroup   also delete the local group
#   Uninstall-SourceVault.ps1 -DeleteVaultData        also PERMANENTLY delete every project in the vault (asks to confirm)
#
# The service's TPM key is not deleted by any option. Without that key on this computer's TPM, a kept vault cannot be
# decrypted anywhere else.
param([switch]$RemoveOperatorsGroup, [switch]$DeleteVaultData)
$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:ProgramFiles 'SourceVault'
$vaultParent = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SourceVault'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SourceVault Editor.lnk'

if (Get-Process -Name 'SourceVault.Editor', 'SourceVault.LabClient' -ErrorAction SilentlyContinue) { throw 'Close all SourceVault editor and lab client windows first.' }
if ($DeleteVaultData) {
    Write-Host "This permanently deletes every project in $vaultParent. It cannot be undone." -ForegroundColor Red
    if ((Read-Host 'Type DELETE VAULT to continue') -ne 'DELETE VAULT') { throw 'Not confirmed; nothing was removed.' }
}

$service = Get-Service -Name 'SourceVault' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        & sc.exe stop SourceVault | Out-Null
        $deadline = (Get-Date).AddSeconds(30)
        do { Start-Sleep -Milliseconds 300; $service.Refresh() } while ($service.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
        if ($service.Status -ne 'Stopped') { throw 'The service did not stop; nothing was removed.' }
    }
    & sc.exe delete SourceVault | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit $LASTEXITCODE." }
    Write-Output 'Removed the SourceVault service.'
}
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force; Write-Output 'Removed the Start menu shortcut.' }
if (Test-Path -LiteralPath $installRoot) {
    if (!(Test-Path -LiteralPath (Join-Path $installRoot 'install-manifest.sha256'))) { throw "$installRoot does not look like a SourceVault installation; not removed." }
    Remove-Item -LiteralPath $installRoot -Recurse -Force
    Write-Output "Removed $installRoot."
}
if ($DeleteVaultData -and (Test-Path -LiteralPath $vaultParent)) {
    Remove-Item -LiteralPath $vaultParent -Recurse -Force
    Write-Output "Deleted the vault ($vaultParent)."
} elseif (Test-Path -LiteralPath $vaultParent) { Write-Output "Kept the encrypted vault in $vaultParent." }
if ($RemoveOperatorsGroup -and (Get-LocalGroup -Name 'SourceVault Operators' -ErrorAction SilentlyContinue)) {
    Remove-LocalGroup -Name 'SourceVault Operators'
    Write-Output 'Removed the local group SourceVault Operators.'
}
Write-Output 'The service TPM key stays in the NT SERVICE\SourceVault profile.'
