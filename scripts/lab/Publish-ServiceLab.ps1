# Builds the service for the lab installer. Run as your normal (non-admin) user.
# Output: artifacts/service-publish (framework-dependent; the installer supplies a private .NET runtime).
param([string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (!$DotnetPath) { $DotnetPath = [IO.Path]::GetFullPath((Join-Path $repoRoot '../../work/toolchain/dotnet/dotnet.exe')) }
if (!(Test-Path -LiteralPath $DotnetPath)) { throw "dotnet not found: $DotnetPath" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/packages'
$oldAppData = $env:APPDATA
$env:APPDATA = Join-Path $repoRoot 'artifacts/tooling-appdata'
try {
    $project = Join-Path $repoRoot 'src/SourceVault.Service/SourceVault.Service.csproj'
    $output = Join-Path $repoRoot 'artifacts/service-publish'
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    & $DotnetPath restore $project --configfile (Join-Path $repoRoot 'NuGet.Config') -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    & $DotnetPath publish $project --no-restore -c Release -o $output -p:UseAppHost=false -p:UseSharedCompilation=false -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Write-Output "Published service to $output"
    # Lab client: framework-dependent apphost .exe that runs only on <install root>\runtime (see its .csproj).
    $clientProject = Join-Path $repoRoot 'src/SourceVault.LabClient/SourceVault.LabClient.csproj'
    $clientOutput = Join-Path $repoRoot 'artifacts/client-publish'
    if (Test-Path -LiteralPath $clientOutput) { Remove-Item -LiteralPath $clientOutput -Recurse -Force }
    & $DotnetPath restore $clientProject --configfile (Join-Path $repoRoot 'NuGet.Config') -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Client restore failed' }
    & $DotnetPath publish $clientProject --no-restore -c Release -o $clientOutput -p:UseSharedCompilation=false -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Client publish failed' }
    Write-Output "Published lab client to $clientOutput"
    # Editor, installed build: apphost pinned to <install root>\runtime (SourceVaultInstalled=true).
    $editorProject = Join-Path $repoRoot 'src/SourceVault.Editor.Host/SourceVault.Editor.Host.csproj'
    $editorOutput = Join-Path $repoRoot 'artifacts/editor-publish'
    if (Test-Path -LiteralPath $editorOutput) { Remove-Item -LiteralPath $editorOutput -Recurse -Force }
    & $DotnetPath restore $editorProject --configfile (Join-Path $repoRoot 'NuGet.Config') -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Editor restore failed' }
    & $DotnetPath publish $editorProject --no-restore -c Release -o $editorOutput -p:SourceVaultInstalled=true -p:UseSharedCompilation=false -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) { throw 'Editor publish failed' }
    Write-Output "Published editor to $editorOutput"
} finally { $env:APPDATA = $oldAppData }
