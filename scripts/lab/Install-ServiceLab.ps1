#Requires -RunAsAdministrator
# LAB ONLY. Installs the metadata-only SourceVault service as virtual account NT SERVICE\SourceVault, manual start.
# Copies a private .NET 10 runtime (from the verified workspace SDK) and the published service into an
# administrator-owned folder under Program Files. Creates no key, vault folder or document endpoint.
param(
    [string]$KeyName = 'SourceVault.Lab.Service01',
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'SourceVault'),
    [string]$PublishPath,
    [string]$RuntimePath,
    [string]$VaultRoot # optional, e.g. K:\SourceVault\vault; omitted = the default C:\ProgramData\SourceVault\vault
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (!$PublishPath) { $PublishPath = Join-Path $repoRoot 'artifacts\service-publish' }
if (!$RuntimePath) { $RuntimePath = [IO.Path]::GetFullPath((Join-Path $repoRoot '..\..\work\toolchain\dotnet')) }
$serviceName = 'SourceVault'
$account = 'NT SERVICE\SourceVault'

# Refuse to touch an existing installation.
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw "Service '$serviceName' already exists. Run Uninstall-ServiceLab.ps1 first." }
if (Test-Path -LiteralPath $InstallRoot) { throw "$InstallRoot already exists. Remove it or choose -InstallRoot." }
if ($KeyName -notmatch '^SourceVault\.Lab\.[A-Za-z0-9._-]{1,64}$') { throw 'Key name must be SourceVault.Lab.<letters, digits, . _ ->' }
if ($VaultRoot -and ($VaultRoot.Length -gt 120 -or $VaultRoot -notmatch '^[A-Za-z]:\\[^\\]+(\\[^\\]+)+$' -or [IO.Path]::GetFullPath($VaultRoot) -cne $VaultRoot)) {
    throw '-VaultRoot must be a full path at least two folders below a drive root, such as K:\SourceVault\vault (the service checks the rest).'
}
if (!(Test-Path -LiteralPath (Join-Path $PublishPath 'SourceVault.Service.dll'))) { throw "Published service not found in $PublishPath. Run scripts\lab\Publish-ServiceLab.ps1 first (as your normal user)." }
$framework = Get-ChildItem -LiteralPath (Join-Path $RuntimePath 'shared\Microsoft.NETCore.App') -Directory -ErrorAction SilentlyContinue | Where-Object Name -like '10.*' | Sort-Object Name | Select-Object -Last 1
if (!(Test-Path -LiteralPath (Join-Path $RuntimePath 'dotnet.exe')) -or !$framework) { throw "No .NET 10 runtime found under $RuntimePath" }

Write-Output "Installing to $InstallRoot (runtime $($framework.Name), key $KeyName)"
$runtimeDir = Join-Path $InstallRoot 'runtime'
$serviceDir = Join-Path $InstallRoot 'service'
New-Item -ItemType Directory -Path $runtimeDir, $serviceDir, (Join-Path $runtimeDir 'shared\Microsoft.NETCore.App') | Out-Null
foreach ($file in 'dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    $source = Join-Path $RuntimePath $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $runtimeDir }
}
Copy-Item -LiteralPath (Join-Path $RuntimePath 'host') -Destination $runtimeDir -Recurse
Copy-Item -LiteralPath $framework.FullName -Destination (Join-Path $runtimeDir 'shared\Microsoft.NETCore.App') -Recurse
Copy-Item -Path (Join-Path $PublishPath '*') -Destination $serviceDir -Recurse
$config = if ($VaultRoot) { '{"schemaVersion":2,"mode":"MetadataOnly","keyName":"' + $KeyName + '","vaultRoot":' + ($VaultRoot | ConvertTo-Json) + '}' }
          else { '{"schemaVersion":1,"mode":"MetadataOnly","keyName":"' + $KeyName + '"}' }
[IO.File]::WriteAllText((Join-Path $serviceDir 'sourcevault.service.json'), $config, (New-Object Text.UTF8Encoding $false))

# The folder inherits the Program Files ACL. Refuse if ordinary users could modify anything in it.
$untrusted = 'BUILTIN\Users', 'NT AUTHORITY\Authenticated Users', 'Everyone', 'NT AUTHORITY\INTERACTIVE'
# Write-type bits only (FullControl/Modify also contain read bits): WriteData 0x2, AppendData 0x4, WriteEA 0x10,
# DeleteChild 0x40, WriteAttributes 0x100, Delete 0x10000, WriteDAC 0x40000, WriteOwner 0x80000, GENERIC_ALL/WRITE.
$writeMask = 0x500D0156
foreach ($path in @($InstallRoot) + @(Get-ChildItem -LiteralPath $InstallRoot -Recurse | ForEach-Object FullName)) {
    foreach ($rule in (Get-Acl -LiteralPath $path).Access) {
        $isWrite = (([int]$rule.FileSystemRights) -band $writeMask) -ne 0
        $applies = ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0
        if ($rule.AccessControlType -eq 'Allow' -and $isWrite -and $applies -and $untrusted -contains $rule.IdentityReference.Value) {
            Remove-Item -LiteralPath $InstallRoot -Recurse -Force
            throw "Ordinary users could modify $path ($($rule.IdentityReference)). Installation removed."
        }
    }
}

# Record hashes of everything installed.
$manifest = Get-ChildItem -LiteralPath $InstallRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($InstallRoot.Length + 1)
}
[IO.File]::WriteAllLines((Join-Path $InstallRoot 'install-manifest.sha256'), [string[]]$manifest, (New-Object Text.UTF8Encoding $false))

# Register the service. New-Service handles the quoted path; sc.exe then sets the virtual account.
$binPath = '"{0}" "{1}" service' -f (Join-Path $runtimeDir 'dotnet.exe'), (Join-Path $serviceDir 'SourceVault.Service.dll')
New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName 'SourceVault (lab)' -StartupType Manual `
    -Description 'SourceVault lab service: metadata-only, synthetic data only. Opens its own TPM key; no document endpoints.' | Out-Null
& sc.exe config $serviceName obj= $account | Out-Null
if ($LASTEXITCODE -ne 0) { & sc.exe delete $serviceName | Out-Null; throw "Could not set service account (sc.exe exit $LASTEXITCODE). Service removed; files left in $InstallRoot." }
& sc.exe sidtype $serviceName unrestricted | Out-Null
$installed = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if ($installed.StartName -ne $account -or $installed.PathName -ne $binPath -or $installed.StartMode -ne 'Manual') {
    & sc.exe delete $serviceName | Out-Null
    throw 'Service registration did not match the expected account, path or start mode. Service removed.'
}
Write-Output "Installed service '$serviceName' as $account, manual start, $($manifest.Count) files hashed."
Write-Output 'Next: scripts\lab\Provision-ServiceKey.ps1'
