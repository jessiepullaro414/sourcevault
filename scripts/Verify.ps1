param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot 'artifacts/cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot 'artifacts/packages'
# NuGet/SDK bootstrap can read user-level settings even with an explicit config.
# Use a process-local tooling profile so verification does not touch the user's profile.
$originalTaskAppData = $env:APPDATA
$env:APPDATA = Join-Path $repoRoot 'artifacts/tooling-appdata'
New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
try {
$projects = @(
    'tests/SourceVault.Contracts.Tests/SourceVault.Contracts.Tests.csproj',
    'spikes/tpm/SourceVault.TpmSpike.csproj',
    'spikes/ipc/SourceVault.IpcSpike.csproj',
    'spikes/builder/SourceVault.BuilderSpike.csproj'
)
foreach ($relativeProject in $projects) {
    $projectPath = Join-Path $repoRoot $relativeProject
    if (!(Test-Path -LiteralPath $projectPath)) { throw "Required milestone project missing: $relativeProject" }
    & $DotnetPath restore $projectPath --configfile (Join-Path $repoRoot 'NuGet.Config') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $relativeProject" }
    & $DotnetPath build $projectPath --no-restore --configuration Release --nologo --verbosity minimal -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $relativeProject" }
}
& $DotnetPath (Join-Path $repoRoot 'tests/SourceVault.Contracts.Tests/bin/Release/net10.0-windows/SourceVault.Contracts.Tests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed' }
& $DotnetPath (Join-Path $repoRoot 'spikes/tpm/bin/Release/net10.0-windows/SourceVault.TpmSpike.dll') self-test
if ($LASTEXITCODE -ne 0) { throw 'TPM policy tests failed' }
& $DotnetPath (Join-Path $repoRoot 'spikes/ipc/bin/Release/net10.0-windows/SourceVault.IpcSpike.dll') self-test
if ($LASTEXITCODE -ne 0) { throw 'IPC loopback tests failed (restricted tokens may deny the deliberately narrow pipe DACL; do not broaden it)' }
& $DotnetPath (Join-Path $repoRoot 'spikes/builder/bin/Release/net10.0/SourceVault.BuilderSpike.dll') self-test (Join-Path $repoRoot 'artifacts/builder-tests')
if ($LASTEXITCODE -ne 0) { throw 'Builder tests failed' }
Write-Output 'Verification passed. These synthetic tests do not validate service identities, TPM operations, or VM isolation.'
} finally {
    $env:APPDATA = $originalTaskAppData
}
