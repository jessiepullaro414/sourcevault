param([string]$DotnetPath, [switch]$Rebuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$DotnetPath) {
    $workspaceSdk = Join-Path $repoRoot 'local/toolchain/dotnet/dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $workspaceSdk) { [System.IO.Path]::GetFullPath($workspaceSdk) } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path
$runtimeList = & $DotnetPath --list-runtimes
if ($LASTEXITCODE -ne 0 -or !($runtimeList -match '^Microsoft.WindowsDesktop.App 10\.')) {
    throw 'Use a .NET 10 SDK or Windows Desktop Runtime. No runtime is downloaded by this script.'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/packages'
$oldAppData = $env:APPDATA
$env:APPDATA = Join-Path $repoRoot 'artifacts/tooling-appdata'
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
$publishFolder = Join-Path $repoRoot 'artifacts/editor-preview'
$exe = Join-Path $publishFolder 'SourceVault.Editor.exe'
try {
    if ($Rebuild -or !(Test-Path -LiteralPath $exe)) {
        $project = Join-Path $repoRoot 'src/SourceVault.Editor.Host/SourceVault.Editor.Host.csproj'
        & $DotnetPath restore $project --configfile (Join-Path $repoRoot 'NuGet.Config') --disable-parallel -m:1 -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { throw 'Editor restore failed' }
        & $DotnetPath publish $project --configuration Release --no-restore --output $publishFolder -p:UseSharedCompilation=false -m:1 -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { throw 'Editor publish failed. Close an older preview before rebuilding.' }
    }
    # Select the explicit workspace runtime for this child, without changing machine settings.
    $start = [System.Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $publishFolder
    $start.Arguments = '--synthetic-preview' # constant; ArgumentList is unavailable in Windows PowerShell 5.1
    $start.Environment['DOTNET_ROOT'] = Split-Path -Parent $DotnetPath
    $start.Environment['DOTNET_ROOT_X64'] = Split-Path -Parent $DotnetPath
    [System.Diagnostics.Process]::Start($start) | Out-Null
    Write-Output 'Opened SourceVault synthetic editor. Revisions are session-only; no vault is connected.'
} finally { $env:APPDATA = $oldAppData }
