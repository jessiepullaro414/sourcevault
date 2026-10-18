#Requires -RunAsAdministrator
# LAB ONLY. Installs (or replaces) the vault editor at <install root>\editor\SourceVault.Editor.exe, the exact path the
# service's executable check allows, and adds the Windows desktop runtime (WPF) to the private runtime at
# <install root>\runtime, at the same version as the base runtime already there. Uses the system WebView2 Runtime.
param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'SourceVault'),
    [string]$PublishPath,
    [string]$RuntimePath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (!$PublishPath) { $PublishPath = Join-Path $repoRoot 'artifacts\editor-publish' }
if (!$RuntimePath) { $RuntimePath = [IO.Path]::GetFullPath((Join-Path $repoRoot '..\..\work\toolchain\dotnet')) }
$runtimeDir = Join-Path $InstallRoot 'runtime'
if (!(Test-Path -LiteralPath (Join-Path $InstallRoot 'install-manifest.sha256')) -or !(Test-Path -LiteralPath (Join-Path $runtimeDir 'dotnet.exe'))) {
    throw 'No lab service installation found. Run Install-ServiceLab.ps1 first.'
}
if (!(Test-Path -LiteralPath (Join-Path $PublishPath 'SourceVault.Editor.exe')) -or !(Test-Path -LiteralPath (Join-Path $PublishPath 'assets\index.html'))) {
    throw "No editor build in $PublishPath. Run Publish-ServiceLab.ps1 (as your normal user) first."
}

# Desktop runtime: must match the installed base runtime version exactly.
$base = Get-ChildItem -LiteralPath (Join-Path $runtimeDir 'shared\Microsoft.NETCore.App') -Directory | Select-Object -First 1
$desktopTarget = Join-Path $runtimeDir "shared\Microsoft.WindowsDesktop.App\$($base.Name)"
if (!(Test-Path -LiteralPath $desktopTarget)) {
    $desktopSource = Join-Path $RuntimePath "shared\Microsoft.WindowsDesktop.App\$($base.Name)"
    if (!(Test-Path -LiteralPath $desktopSource)) { throw "Windows desktop runtime $($base.Name) not found under $RuntimePath." }
    New-Item -ItemType Directory -Path (Split-Path -Parent $desktopTarget) -Force | Out-Null
    Copy-Item -LiteralPath $desktopSource -Destination (Split-Path -Parent $desktopTarget) -Recurse
    Write-Output "Added Windows desktop runtime $($base.Name) to $runtimeDir."
} else { Write-Output "Windows desktop runtime $($base.Name) already present." }

$editorDir = Join-Path $InstallRoot 'editor'
if (Test-Path -LiteralPath $editorDir) { Get-ChildItem -LiteralPath $editorDir | Remove-Item -Recurse -Force }
else { New-Item -ItemType Directory -Path $editorDir | Out-Null }
Copy-Item -Path (Join-Path $PublishPath '*') -Destination $editorDir -Recurse

# Ordinary users must not be able to modify the editor or its runtime, or the executable check would be meaningless.
$untrusted = 'BUILTIN\Users', 'NT AUTHORITY\Authenticated Users', 'Everyone', 'NT AUTHORITY\INTERACTIVE'
$writeMask = 0x500D0156
foreach ($path in @($editorDir, $desktopTarget) + @(Get-ChildItem -LiteralPath $editorDir, $desktopTarget -Recurse | ForEach-Object FullName)) {
    foreach ($rule in (Get-Acl -LiteralPath $path).Access) {
        $applies = ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0
        if ($rule.AccessControlType -eq 'Allow' -and (([int]$rule.FileSystemRights) -band $writeMask) -ne 0 -and $applies -and $untrusted -contains $rule.IdentityReference.Value) {
            Remove-Item -LiteralPath $editorDir -Recurse -Force
            throw "Ordinary users could modify $path ($($rule.IdentityReference)). Editor removed."
        }
    }
}

$manifest = Get-ChildItem -LiteralPath $InstallRoot -Recurse -File | Where-Object Name -ne 'install-manifest.sha256' | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($InstallRoot.Length + 1)
}
[IO.File]::WriteAllLines((Join-Path $InstallRoot 'install-manifest.sha256'), [string[]]$manifest, (New-Object Text.UTF8Encoding $false))
$exe = Join-Path $editorDir 'SourceVault.Editor.exe'
Write-Output "Installed editor: $exe ($($manifest.Count) files hashed under $InstallRoot)."
Write-Output 'Start it from a NORMAL window, signed in as a SourceVault Operators member, with the service running:'
Write-Output "  & '$exe' --vault"
