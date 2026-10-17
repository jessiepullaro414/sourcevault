#Requires -RunAsAdministrator
# LAB ONLY. Installs (or replaces) the lab client at <install root>\client\SourceVault.LabClient.exe, the exact path
# the service's executable check allows. The client runs only on <install root>\runtime. Prints no document content.
param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'SourceVault'),
    [string]$PublishPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (!$PublishPath) { $PublishPath = Join-Path $repoRoot 'artifacts\client-publish' }
if (!(Test-Path -LiteralPath (Join-Path $InstallRoot 'install-manifest.sha256')) -or !(Test-Path -LiteralPath (Join-Path $InstallRoot 'runtime\dotnet.exe'))) {
    throw 'No lab service installation found. Run Install-ServiceLab.ps1 first.'
}
if (!(Test-Path -LiteralPath (Join-Path $PublishPath 'SourceVault.LabClient.exe'))) { throw "No client build in $PublishPath. Run Publish-ServiceLab.ps1 (as your normal user) first." }

$clientDir = Join-Path $InstallRoot 'client'
if (Test-Path -LiteralPath $clientDir) { Get-ChildItem -LiteralPath $clientDir | Remove-Item -Recurse -Force }
else { New-Item -ItemType Directory -Path $clientDir | Out-Null }
Copy-Item -Path (Join-Path $PublishPath '*') -Destination $clientDir -Recurse

# Same rule as the service install: ordinary users must not be able to modify anything here, or the executable
# check would be meaningless.
$untrusted = 'BUILTIN\Users', 'NT AUTHORITY\Authenticated Users', 'Everyone', 'NT AUTHORITY\INTERACTIVE'
$writeMask = 0x500D0156
foreach ($path in @($clientDir) + @(Get-ChildItem -LiteralPath $clientDir -Recurse | ForEach-Object FullName)) {
    foreach ($rule in (Get-Acl -LiteralPath $path).Access) {
        $applies = ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0
        if ($rule.AccessControlType -eq 'Allow' -and (([int]$rule.FileSystemRights) -band $writeMask) -ne 0 -and $applies -and $untrusted -contains $rule.IdentityReference.Value) {
            Remove-Item -LiteralPath $clientDir -Recurse -Force
            throw "Ordinary users could modify $path ($($rule.IdentityReference)). Client removed."
        }
    }
}

$manifest = Get-ChildItem -LiteralPath $InstallRoot -Recurse -File | Where-Object Name -ne 'install-manifest.sha256' | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($InstallRoot.Length + 1)
}
[IO.File]::WriteAllLines((Join-Path $InstallRoot 'install-manifest.sha256'), [string[]]$manifest, (New-Object Text.UTF8Encoding $false))
$exe = Join-Path $clientDir 'SourceVault.LabClient.exe'
Write-Output "Installed lab client: $exe ($($manifest.Count) files hashed under $InstallRoot)."
Write-Output 'Run it from a NORMAL window, signed in as a SourceVault Operators member, with the service running:'
Write-Output "  & '$exe' check"
Write-Output "  & '$exe' list"
