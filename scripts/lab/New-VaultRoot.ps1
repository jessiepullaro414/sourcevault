#Requires -RunAsAdministrator
# LAB ONLY. Creates the installed service's configured vault root (default C:\ProgramData\SourceVault\vault, or the
# -VaultRoot given to Install-ServiceLab.ps1, e.g. K:\SourceVault\vault) with the exact ACL the service's validator
# requires (taken from the installed service itself), then runs the validator.
#   <parent>, e.g. C:\ProgramData\SourceVault   owner Administrators; protected; SYSTEM + Administrators full control,
#                                                NT SERVICE\SourceVault read/traverse on this folder only
#   <vault root>                                 owner Administrators; protected DACL from `vault-root-sddl`
# Creates no store and no files. Refuses to run if the parent already exists (unless it is this script's own empty
# leftover), and never changes the drive root or any folder above the parent: those must already pass the check.
param([string]$InstallRoot = (Join-Path $env:ProgramFiles 'SourceVault'))
$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $InstallRoot 'runtime\dotnet.exe'
$dll = Join-Path $InstallRoot 'service\SourceVault.Service.dll'
if (!(Test-Path -LiteralPath $dotnet) -or !(Test-Path -LiteralPath $dll)) { throw 'Service not installed. Run Install-ServiceLab.ps1 first.' }

$rootSddl = (& $dotnet $dll vault-root-sddl | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $rootSddl -notmatch '^D:P(\(A;OICI;FA;;;S-1-5-[0-9-]+\))+$') {
    throw 'The installed service does not support vault-root-sddl. Run Publish-ServiceLab.ps1 and Update-ServiceLab.ps1 first.'
}
$serviceSid = ([Security.Principal.NTAccount]'NT SERVICE\SourceVault').Translate([Security.Principal.SecurityIdentifier]).Value
if ($rootSddl -notmatch [regex]::Escape($serviceSid)) { throw 'Expected ACL does not name the installed service account.' }

# The root the installed service is configured for, as its own read-only check reports it.
$check = & $dotnet $dll vault-root-check | Out-String
if ($check -notmatch 'vaultRoot=(?<root>[A-Za-z]:\\[^;\r\n]+); trusted=') { throw "The installed service did not report its vault root:`n$check" }
$vault = $Matches.root
$parent = Split-Path -Parent $vault
if ($check -match 'volume_unavailable') { throw "Drive $($vault.Substring(0, 2)) is not available (missing, offline or locked); nothing was created." }
$above = @($check -split "`r?`n" | Where-Object { $_ -match '^error \S+ at (volume|ancestor:([2-9]|[1-9][0-9]+))' })
if ($above) { Write-Output $check; throw "The drive or folders above $parent do not pass the service's check, and this script never changes them. See 'Vault on another drive' in docs/user-guide.md." }
# A rerun may continue only over this script's own empty leftovers (an earlier interrupted run); anything else is refused.
if (Test-Path -LiteralPath $parent) {
    $contents = @(Get-ChildItem -LiteralPath $parent -Recurse -Force)
    $onlyEmptyVault = $contents.Count -eq 0 -or ($contents.Count -eq 1 -and $contents[0].PSIsContainer -and $contents[0].Name -eq (Split-Path -Leaf $vault))
    if (!$onlyEmptyVault) { throw "$parent already exists and is not an empty leftover of this script; not modified." }
    Write-Output "$parent exists from an earlier run and is empty; re-applying the expected ACLs."
}

$sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
function Get-SddlShape([string]$sddl) {
    # Round-trip through the security API so raw SIDs and their aliases (S-1-5-18 vs SY) compare equal.
    $canonical = New-Object Security.AccessControl.DirectorySecurity
    $canonical.SetSecurityDescriptorSddlForm($sddl, $sections)
    $sddl = $canonical.GetSecurityDescriptorSddlForm($sections)
    if ($sddl -notmatch '^O:(?<owner>[^D]+)D:(?<flags>[A-Z]*)(?<aces>\(.*\))$') { return "unparsed:$sddl" }
    $aces = [regex]::Matches($Matches.aces, '\([^)]*\)') | ForEach-Object Value | Sort-Object
    "$($Matches.owner)|protected=$($Matches.flags.Contains('P'))|$($aces -join '')"
}
function Set-ExactSecurity([string]$path, [string]$sddl) {
    # Apply only owner + DACL; group and SACL are left untouched.
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetSecurityDescriptorSddlForm($sddl, $sections)
    (Get-Item -LiteralPath $path).SetAccessControl($security)
    # Compare owner, protection and the ACE set. Windows adds the auto-inherited flag (D:PAI) and may reorder ACEs.
    $applied = (Get-Item -LiteralPath $path).GetAccessControl($sections).GetSecurityDescriptorSddlForm($sections)
    if ((Get-SddlShape $applied) -ne (Get-SddlShape $sddl)) { throw "ACL on $path did not apply exactly." }
}
if (!(Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
# 0x1200a9 = ReadAndExecute + Synchronize, this folder only (no inheritance flags).
Set-ExactSecurity $parent "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1200a9;;;$serviceSid)"
if (!(Test-Path -LiteralPath $vault)) { New-Item -ItemType Directory -Path $vault | Out-Null }
Set-ExactSecurity $vault ("O:BA" + $rootSddl)
Write-Output "Created $vault"

& $dotnet $dll vault-root-check
if ($LASTEXITCODE -eq 0) { Write-Output 'Vault root is trusted by the validator. Next: scripts\lab\Test-ServiceLab.ps1' }
else { Write-Output 'Validator rejected the new vault root; see findings above. Nothing else was changed.'; exit 1 }
