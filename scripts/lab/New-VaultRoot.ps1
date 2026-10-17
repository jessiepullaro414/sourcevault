#Requires -RunAsAdministrator
# LAB ONLY. Creates the service's fixed vault root, C:\ProgramData\SourceVault\vault, with the exact ACL the
# service's validator requires (taken from the installed service itself), then runs the validator.
#   C:\ProgramData\SourceVault        owner Administrators; protected; SYSTEM + Administrators full control,
#                                     NT SERVICE\SourceVault read/traverse on this folder only
#   C:\ProgramData\SourceVault\vault  owner Administrators; protected DACL from `vault-root-sddl`
# Creates no store and no files. Refuses to run if C:\ProgramData\SourceVault already exists.
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

$programData = [Environment]::GetFolderPath('CommonApplicationData')
$parent = Join-Path $programData 'SourceVault'
$vault = Join-Path $parent 'vault'
if (Test-Path -LiteralPath $parent) { throw "$parent already exists; not modified. Inspect it, or remove it if it is a leftover lab folder." }

function Get-SddlShape([string]$sddl) {
    if ($sddl -notmatch '^O:(?<owner>[^D]+)D:(?<flags>[A-Z]*)(?<aces>\(.*\))$') { return "unparsed:$sddl" }
    $aces = [regex]::Matches($Matches.aces, '\([^)]*\)') | ForEach-Object Value | Sort-Object
    "$($Matches.owner)|protected=$($Matches.flags.Contains('P'))|$($aces -join '')"
}
function Set-ExactSecurity([string]$path, [string]$sddl) {
    # Apply only owner + DACL; group and SACL are left untouched.
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetSecurityDescriptorSddlForm($sddl, $sections)
    (Get-Item -LiteralPath $path).SetAccessControl($security)
    # Compare owner, protection and the ACE set. Windows adds the auto-inherited flag (D:PAI) and may reorder ACEs.
    $applied = (Get-Item -LiteralPath $path).GetAccessControl($sections).GetSecurityDescriptorSddlForm($sections)
    if ((Get-SddlShape $applied) -ne (Get-SddlShape $sddl)) { throw "ACL on $path did not apply exactly." }
}
New-Item -ItemType Directory -Path $parent | Out-Null
# 0x1200a9 = ReadAndExecute + Synchronize, this folder only (no inheritance flags).
Set-ExactSecurity $parent "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1200a9;;;$serviceSid)"
New-Item -ItemType Directory -Path $vault | Out-Null
Set-ExactSecurity $vault ("O:BA" + $rootSddl)
Write-Output "Created $vault"

& $dotnet $dll vault-root-check
if ($LASTEXITCODE -eq 0) { Write-Output 'Vault root is trusted by the validator. Next: scripts\lab\Test-ServiceLab.ps1' }
else { Write-Output 'Validator rejected the new vault root; see findings above. Nothing else was changed.'; exit 1 }
