# Builds the SourceVault editor DEMO as a portable folder and .zip for showing the editor on any Windows PC, with no
# service, TPM key, install or administrator rights. The demo runs the full vault editor on a simulated vault in the
# page's memory. NOTHING IS ENCRYPTED: the editor is red and says so, and what people type can stay on that PC.
#   powershell -ExecutionPolicy Bypass -File scripts\Build-EditorDemo.ps1
# Output: artifacts\demo\SourceVault-Demo\ and artifacts\demo\SourceVault-Demo-<date>.zip
# The target PC needs Windows 10 or 11 (x64) with the Microsoft Edge WebView2 Runtime, which Windows 11 includes.
param([string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$DotnetPath) { $DotnetPath = [IO.Path]::GetFullPath((Join-Path $repoRoot 'local/toolchain/dotnet/dotnet.exe')) }
if (!(Test-Path -LiteralPath $DotnetPath)) { throw "dotnet not found: $DotnetPath" }
$sdkRoot = Split-Path -Parent $DotnetPath
$outRoot = Join-Path $repoRoot 'artifacts\demo'
$demo = Join-Path $outRoot 'SourceVault-Demo'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/packages'
$oldAppData = $env:APPDATA
$env:APPDATA = Join-Path $repoRoot 'artifacts/tooling-appdata'
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
try {
    # 1. The editor, pinned to the private runtime beside it (..\runtime), like the installed editor.
    $project = Join-Path $repoRoot 'src/SourceVault.Editor.Host/SourceVault.Editor.Host.csproj'
    $publish = Join-Path $repoRoot 'artifacts/editor-demo-publish'
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    & $DotnetPath restore $project --configfile (Join-Path $repoRoot 'NuGet.Config') -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Editor restore failed' }
    & $DotnetPath publish $project --no-restore -c Release -o $publish -p:SourceVaultInstalled=true -p:UseSharedCompilation=false -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Editor publish failed' }
} finally { $env:APPDATA = $oldAppData }

if (Test-Path -LiteralPath $demo) { Remove-Item -LiteralPath $demo -Recurse -Force }
New-Item -ItemType Directory -Path $demo | Out-Null
Copy-Item -LiteralPath $publish -Destination (Join-Path $demo 'editor') -Recurse

# 2. Private runtime: the base and Windows desktop runtimes of the same version, from the verified workspace SDK.
$runtime = Join-Path $demo 'runtime'
$base = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'shared\Microsoft.NETCore.App') -Directory | Where-Object Name -like '10.*' | Sort-Object { [version]$_.Name } | Select-Object -Last 1
$desktop = Join-Path $sdkRoot "shared\Microsoft.WindowsDesktop.App\$($base.Name)"
if (!(Test-Path -LiteralPath $desktop)) { throw "Windows desktop runtime $($base.Name) is missing from $sdkRoot." }
New-Item -ItemType Directory -Path (Join-Path $runtime 'shared\Microsoft.NETCore.App'), (Join-Path $runtime 'shared\Microsoft.WindowsDesktop.App') | Out-Null
foreach ($file in 'dotnet.exe', 'LICENSE.txt', 'ThirdPartyNotices.txt') { Copy-Item -LiteralPath (Join-Path $sdkRoot $file) -Destination $runtime }
Copy-Item -LiteralPath (Join-Path $sdkRoot 'host') -Destination $runtime -Recurse
Copy-Item -LiteralPath $base.FullName -Destination (Join-Path $runtime 'shared\Microsoft.NETCore.App') -Recurse
Copy-Item -LiteralPath $desktop -Destination (Join-Path $runtime 'shared\Microsoft.WindowsDesktop.App') -Recurse

# 3. A launcher, the notes and the licences.
$crlf = "`r`n"
[IO.File]::WriteAllText((Join-Path $demo 'Start SourceVault Demo.cmd'), ('@echo off' + $crlf + 'rem SourceVault DEMO: a simulated vault. Nothing is encrypted; use made-up code only.' + $crlf + 'start "" "%~dp0editor\SourceVault.Editor.exe" --demo' + $crlf), (New-Object Text.UTF8Encoding $false))
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\Demo-README.txt') -Destination (Join-Path $demo 'README.txt')
$licenses = Join-Path $demo 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $licenses 'SourceVault-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'vendor\packages\WebView2-LICENSE.txt') -Destination (Join-Path $licenses 'Microsoft.Web.WebView2-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\SourceVault.Editor.Web\vendor\monaco\LICENSE') -Destination (Join-Path $licenses 'monaco-editor-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $sdkRoot 'LICENSE.txt') -Destination (Join-Path $licenses 'dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $sdkRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $licenses 'dotnet-ThirdPartyNotices.txt')

# 4. A hash of every file, then the zip.
$files = Get-ChildItem -LiteralPath $demo -Recurse -File | Sort-Object FullName
$lines = foreach ($f in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash, $f.FullName.Substring($demo.Length + 1) }
[IO.File]::WriteAllLines((Join-Path $demo 'demo-manifest.sha256'), [string[]]$lines, (New-Object Text.UTF8Encoding $false))
$zip = Join-Path $outRoot ('SourceVault-Demo-{0:yyyyMMdd}.zip' -f (Get-Date))
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $demo -DestinationPath $zip -CompressionLevel Optimal
Write-Output ("Demo:   {0} ({1} files, runtime {2})" -f $demo, $files.Count, $base.Name)
Write-Output ("Zip:    {0} ({1:N0} MB), SHA-256 {2}" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB), (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)
Write-Output 'Unzip anywhere and run "Start SourceVault Demo.cmd". Nothing in the demo is encrypted.'
