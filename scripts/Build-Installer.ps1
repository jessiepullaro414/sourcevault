# Builds the SourceVault setup package: a folder and a .zip that install SourceVault on another Windows computer with
# no repository, SDK or download. Run as your normal user from this repository.
#   powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1
# Output: artifacts\installer\SourceVault-Setup\ and artifacts\installer\SourceVault-Setup-<date>.zip
param([string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$DotnetPath) { $DotnetPath = [IO.Path]::GetFullPath((Join-Path $repoRoot 'local/toolchain/dotnet/dotnet.exe')) }
$sdkRoot = Split-Path -Parent $DotnetPath
$outRoot = Join-Path $repoRoot 'artifacts\installer'
$package = Join-Path $outRoot 'SourceVault-Setup'
$payload = Join-Path $package 'payload'

# 1. Fresh builds of the service, lab client and editor (installed variants, pinned to the private runtime).
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\lab\Publish-ServiceLab.ps1') -DotnetPath $DotnetPath
if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }

if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Recurse -Force }
New-Item -ItemType Directory -Path $payload | Out-Null

# 2. Private runtime: the base and Windows desktop runtimes of the same version, from the verified workspace SDK.
$runtime = Join-Path $payload 'runtime'
$base = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'shared\Microsoft.NETCore.App') -Directory | Where-Object Name -like '10.*' | Sort-Object { [version]$_.Name } | Select-Object -Last 1
$desktop = Join-Path $sdkRoot "shared\Microsoft.WindowsDesktop.App\$($base.Name)"
if (!(Test-Path -LiteralPath $desktop)) { throw "Windows desktop runtime $($base.Name) is missing from $sdkRoot." }
New-Item -ItemType Directory -Path (Join-Path $runtime 'shared\Microsoft.NETCore.App'), (Join-Path $runtime 'shared\Microsoft.WindowsDesktop.App') | Out-Null
foreach ($file in 'dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt') { Copy-Item -LiteralPath (Join-Path $sdkRoot $file) -Destination $runtime }
Copy-Item -LiteralPath (Join-Path $sdkRoot 'host') -Destination $runtime -Recurse
Copy-Item -LiteralPath $base.FullName -Destination (Join-Path $runtime 'shared\Microsoft.NETCore.App') -Recurse
Copy-Item -LiteralPath $desktop -Destination (Join-Path $runtime 'shared\Microsoft.WindowsDesktop.App') -Recurse

# 3. Programs.
Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts\service-publish') -Destination (Join-Path $payload 'service') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts\client-publish') -Destination (Join-Path $payload 'client') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts\editor-publish') -Destination (Join-Path $payload 'editor') -Recurse
if (Test-Path -LiteralPath (Join-Path $payload 'editor\assets\dev')) { throw 'The editor build contains the development fake host.' }
Get-ChildItem -LiteralPath (Join-Path $payload 'service') -Filter 'sourcevault.service.json' -Recurse | Remove-Item -Force # setup writes its own

# 3b. SourceVault Pack for the other computer: its own folder with the base and Windows desktop runtimes (the window).
$packTool = Join-Path $package 'tools\SourceVault-Pack'
New-Item -ItemType Directory -Path (Join-Path $packTool 'runtime\shared\Microsoft.NETCore.App'), (Join-Path $packTool 'runtime\shared\Microsoft.WindowsDesktop.App') | Out-Null
foreach ($file in 'dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt') { Copy-Item -LiteralPath (Join-Path $sdkRoot $file) -Destination (Join-Path $packTool 'runtime') }
Copy-Item -LiteralPath (Join-Path $sdkRoot 'host') -Destination (Join-Path $packTool 'runtime') -Recurse
Copy-Item -LiteralPath $base.FullName -Destination (Join-Path $packTool 'runtime\shared\Microsoft.NETCore.App') -Recurse
Copy-Item -LiteralPath $desktop -Destination (Join-Path $packTool 'runtime\shared\Microsoft.WindowsDesktop.App') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'artifacts\pack-publish') -Destination (Join-Path $packTool 'pack') -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Pack-README.txt') -Destination (Join-Path $packTool 'README.txt')

# 4. Scripts, guide and third-party licenses.
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Setup-SourceVault.ps1'), (Join-Path $repoRoot 'installer\Uninstall-SourceVault.ps1'),
    (Join-Path $repoRoot 'installer\Grant-BuildDockerAccess.ps1'), (Join-Path $repoRoot 'installer\New-WslDockerTls.ps1'), (Join-Path $repoRoot 'installer\New-BuildContainer.ps1'), (Join-Path $repoRoot 'installer\New-WindowsBuildContainer.ps1'), (Join-Path $repoRoot 'installer\build.example.json'), (Join-Path $repoRoot 'installer\Setup-Builds.ps1'), (Join-Path $repoRoot 'installer\kit-sources.json'), (Join-Path $repoRoot 'installer\SourceVault Setup.cmd') -Destination $package
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\samples') -Destination $package -Recurse # synthetic projects for import and build checks
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\user-guide.md') -Destination (Join-Path $package 'README.md')
$licenses = Join-Path $package 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'vendor\packages\WebView2-LICENSE.txt') -Destination (Join-Path $licenses 'Microsoft.Web.WebView2-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\SourceVault.Editor.Web\vendor\monaco\LICENSE') -Destination (Join-Path $licenses 'monaco-editor-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $sdkRoot 'LICENSE.txt') -Destination (Join-Path $licenses 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $sdkRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $licenses 'dotnet-ThirdPartyNotices.txt')

# 5. Integrity manifest checked by setup before it changes anything.
$files = Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName
$lines = foreach ($f in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash, $f.FullName.Substring($package.Length + 1) }
[IO.File]::WriteAllLines((Join-Path $package 'package-manifest.sha256'), [string[]]$lines, (New-Object Text.UTF8Encoding $false))

# 6. Zips for transfer: the setup package, and SourceVault Pack on its own for the other computer.
$packZip = Join-Path $outRoot ('SourceVault-Pack-{0:yyyyMMdd}.zip' -f (Get-Date))
if (Test-Path -LiteralPath $packZip) { Remove-Item -LiteralPath $packZip -Force }
Compress-Archive -Path $packTool -DestinationPath $packZip -CompressionLevel Optimal
$zip = Join-Path $outRoot ('SourceVault-Setup-{0:yyyyMMdd}.zip' -f (Get-Date))
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $package -DestinationPath $zip -CompressionLevel Optimal
$size = (Get-Item -LiteralPath $zip).Length / 1MB
Write-Output ("Package: {0} ({1} files, runtime {2})" -f $package, $files.Count, $base.Name)
Write-Output ("Zip:     {0} ({1:N0} MB)" -f $zip, $size)
Write-Output ("SHA-256: {0}" -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)
Write-Output ("Pack:    {0} ({1:N0} MB), SHA-256 {2}" -f $packZip, ((Get-Item -LiteralPath $packZip).Length / 1MB), (Get-FileHash -LiteralPath $packZip -Algorithm SHA256).Hash)
