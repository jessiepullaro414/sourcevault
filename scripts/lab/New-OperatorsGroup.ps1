#Requires -RunAsAdministrator
# LAB ONLY. Creates the local group "SourceVault Operators" (if missing) and adds one member (default: you).
# The service grants pipe access only to this group, and refuses to start (1066/13) if the group does not exist.
# Windows adds group membership to a user's token at sign-in: the member must sign out and back in afterwards.
param([string]$Member = "$env:USERDOMAIN\$env:USERNAME")
$ErrorActionPreference = 'Stop'
$group = 'SourceVault Operators'
if (!(Get-LocalGroup -Name $group -ErrorAction SilentlyContinue)) {
    New-LocalGroup -Name $group -Description 'SourceVault lab pipe access (synthetic only)' | Out-Null # 48-char limit
    Write-Output "Created local group '$group'."
} else { Write-Output "Local group '$group' already exists." }
$sid = (New-Object Security.Principal.NTAccount($Member)).Translate([Security.Principal.SecurityIdentifier]).Value
if (Get-LocalGroupMember -Group $group | Where-Object { $_.SID.Value -eq $sid }) { Write-Output "$Member is already a member." }
else { Add-LocalGroupMember -Group $group -Member $Member; Write-Output "Added $Member." }
Write-Output "Sign out and back in before $Member's programs can use the pipe."
