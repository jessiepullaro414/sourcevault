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
$solution = Join-Path $repoRoot 'SourceVault.sln'
& $DotnetPath restore $solution --configfile (Join-Path $repoRoot 'NuGet.Config') --disable-parallel --verbosity minimal -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed' }
& $DotnetPath build $solution --no-restore --configuration Release --nologo --verbosity minimal -p:UseSharedCompilation=false -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed' }
$checks = @(
    @{ Assembly='tests/SourceVault.Contracts.Tests/bin/Release/net10.0-windows/SourceVault.Contracts.Tests.dll'; Args=@() },
    @{ Assembly='spikes/tpm/bin/Release/net10.0-windows/SourceVault.TpmSpike.dll'; Args=@('self-test') },
    @{ Assembly='spikes/ipc/bin/Release/net10.0-windows/SourceVault.IpcSpike.dll'; Args=@('self-test') },
    @{ Assembly='spikes/builder/bin/Release/net10.0/SourceVault.BuilderSpike.dll'; Args=@('self-test',(Join-Path $repoRoot 'artifacts/builder-tests')) },
    @{ Assembly='tests/SourceVault.Storage.Tests/bin/Release/net10.0/SourceVault.Storage.Tests.dll'; Args=@() },
    @{ Assembly='tests/SourceVault.Revisions.Tests/bin/Release/net10.0/SourceVault.Revisions.Tests.dll'; Args=@((Join-Path $repoRoot 'artifacts/revision-tests')) },
    @{ Assembly='tests/SourceVault.Service.Tests/bin/Release/net10.0-windows/SourceVault.Service.Tests.dll'; Args=@() },
    @{ Assembly='tests/SourceVault.Integration.Tests/bin/Release/net10.0/SourceVault.Integration.Tests.dll'; Args=@((Join-Path $repoRoot 'artifacts/integration-tests')) }
)
foreach ($check in $checks) {
    $testAssembly = Join-Path $repoRoot $check.Assembly
    $testArgs = $check.Args
    & $DotnetPath $testAssembly @testArgs
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($check.Assembly)" }
}
Write-Output 'Verification passed. These synthetic tests do not validate service identities, TPM operations, or VM isolation.'
} finally {
    $env:APPDATA = $originalTaskAppData
}
