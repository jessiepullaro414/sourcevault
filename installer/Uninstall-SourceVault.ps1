#Requires -RunAsAdministrator
# SourceVault uninstall. By default removes the program, service and Start menu shortcut, and KEEPS the encrypted vault
# and the operators group, so a later setup on this same computer can open the vault again.
#
#   Uninstall-SourceVault.ps1                         remove program files, service, shortcut
#   Uninstall-SourceVault.ps1 -RemoveOperatorsGroup   also delete the local group
#   Uninstall-SourceVault.ps1 -DeleteVaultData        also PERMANENTLY delete every project in the vault (asks to confirm)
#
# The vault location is read from the installed service configuration (default C:\ProgramData\SourceVault\vault, or the
# -VaultRoot chosen at install). A kept vault on another drive is reopened by installing again with the same -VaultRoot.
#
# The service's TPM key is not deleted by any option. Without that key on this computer's TPM, a kept vault cannot be
# decrypted anywhere else.
param([switch]$RemoveOperatorsGroup, [switch]$DeleteVaultData)
$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:ProgramFiles 'SourceVault'
$dataDir = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SourceVault'
$defaultVaultRoot = Join-Path $dataDir 'vault'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SourceVault Editor.lnk'

if (Get-Process -Name 'SourceVault.Editor', 'SourceVault.LabClient' -ErrorAction SilentlyContinue) { throw 'Close all SourceVault editor and lab client windows first.' }
# Read the configured vault location before the program folder (which holds the configuration) is removed.
$vaultRoot = $defaultVaultRoot
$serviceConfig = Join-Path $installRoot 'service\sourcevault.service.json'
if (Test-Path -LiteralPath $serviceConfig) {
    try { $config = Get-Content -LiteralPath $serviceConfig -Raw | ConvertFrom-Json } catch { throw "$serviceConfig could not be read; nothing was removed." }
    if ($config.schemaVersion -eq 2) { $vaultRoot = [string]$config.vaultRoot }
}
if ($vaultRoot -notmatch '^[A-Za-z]:\\[^\\]+(\\[^\\]+)+$') { throw "The configured vault location '$vaultRoot' is not a valid vault path; nothing was removed." }
$vaultParent = Split-Path -Parent $vaultRoot
$custom = $vaultRoot -ne $defaultVaultRoot
$driveReady = (New-Object IO.DriveInfo ($vaultRoot.Substring(0, 3))).IsReady
if ($DeleteVaultData) {
    if (!$driveReady) { throw "Drive $($vaultRoot.Substring(0, 2)) of the vault $vaultRoot is not available (missing, offline or locked); nothing was removed." }
    # Only a SourceVault vault layout is deleted from an administrator-chosen location.
    if ($custom -and (Test-Path -LiteralPath $vaultRoot) -and @(Get-ChildItem -LiteralPath $vaultRoot -Force | Where-Object { @('index', 'projects', 'builds') -notcontains $_.Name }).Count -gt 0) {
        throw "$vaultRoot holds items that are not part of a SourceVault vault; nothing was removed."
    }
    Write-Host "This permanently deletes every project in $vaultRoot$(if ($custom) { " and the configuration in $dataDir" }). It cannot be undone." -ForegroundColor Red
    if ((Read-Host 'Type DELETE VAULT to continue') -ne 'DELETE VAULT') { throw 'Not confirmed; nothing was removed.' }
}

$build = Get-Service -Name 'SourceVaultBuild' -ErrorAction SilentlyContinue
if ($build) {
    if ($build.Status -ne 'Stopped') {
        & sc.exe stop SourceVaultBuild | Out-Null
        $deadline = (Get-Date).AddSeconds(60)
        do { Start-Sleep -Milliseconds 300; $build.Refresh() } while ($build.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
        if ($build.Status -ne 'Stopped') { throw 'The build service did not stop (a build may be running); nothing was removed.' }
    }
    & sc.exe delete SourceVaultBuild | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete SourceVaultBuild failed with exit $LASTEXITCODE." }
    Write-Output 'Removed the SourceVault build service. Build artifact folders are kept; remove them yourself if wanted.'
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
if ($DeleteVaultData) {
    if (Test-Path -LiteralPath $vaultRoot) { Remove-Item -LiteralPath $vaultRoot -Recurse -Force; Write-Output "Deleted the vault ($vaultRoot)." }
    # The parent setup created for a vault elsewhere is removed only when nothing else is in it; the drive root never.
    if ($custom -and (Test-Path -LiteralPath $vaultParent)) {
        if (@(Get-ChildItem -LiteralPath $vaultParent -Force).Count -eq 0) { Remove-Item -LiteralPath $vaultParent -Force; Write-Output "Removed the empty folder $vaultParent." }
        else { Write-Output "Kept $($vaultParent): it holds other items." }
    }
    if (Test-Path -LiteralPath $dataDir) { Remove-Item -LiteralPath $dataDir -Recurse -Force; Write-Output "Deleted $dataDir." }
} elseif (!$driveReady) {
    Write-Output "Drive $($vaultRoot.Substring(0, 2)) is not available, so the vault in $vaultRoot was not checked. Nothing on it was changed."
} elseif (Test-Path -LiteralPath $vaultRoot) {
    Write-Output "Kept the encrypted vault in $vaultRoot."
    if ($custom) { Write-Output "To open it again on this computer, install with: Setup-SourceVault.ps1 -VaultRoot $vaultRoot" }
}
if ($RemoveOperatorsGroup -and (Get-LocalGroup -Name 'SourceVault Operators' -ErrorAction SilentlyContinue)) {
    Remove-LocalGroup -Name 'SourceVault Operators'
    Write-Output 'Removed the local group SourceVault Operators.'
}
Write-Output 'The service TPM key stays in the NT SERVICE\SourceVault profile.'
