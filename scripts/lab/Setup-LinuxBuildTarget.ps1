#Requires -RunAsAdministrator
# Lab helper (2026-10-20; owner-run, see README.md): the whole Linux build target in one run (Install-WslBuildEngine, New-BuildContainer, Add-LinuxBuildTarget). Stops at the first error.
# Safe to run again: a container that already exists is kept (-ReplaceContainer recreates it), and L4 runs only while
# build.json has no linux target (-ReconnectTls makes new certificates and runs it again).
# Close SourceVault editor windows first. Run as yourself, elevated:
#   powershell -ExecutionPolicy Bypass -File .\Setup-LinuxBuildTarget.ps1
param([switch]$ReplaceContainer, [switch]$ReconnectTls)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$pkg = Join-Path $here '..\..\artifacts\installer\SourceVault-Setup' # scripts\Build-Installer.ps1 output
$distro = 'SourceVault-Ubuntu'
function Step([string]$m) { Write-Host "`n== $m" -ForegroundColor Cyan }
if (Get-Process -Name 'SourceVault.Editor', 'SourceVault.LabClient' -ErrorAction SilentlyContinue) { throw 'Close all SourceVault editor and lab client windows first (setup needs them closed).' }

Step 'L2. WSL distro and Docker engine'
& (Join-Path $here 'Install-WslBuildEngine.ps1') -Distro $distro

Step 'L3. Linux build container (pulls about 335 MB)'
$names = (& wsl.exe -d $distro -u root --exec docker ps -a --format '{{.Names}}') -join ' '
if ($names -match '\bsourcevault-build-linux\b' -and !$ReplaceContainer) { Write-Host '   OK   sourcevault-build-linux already exists (kept; -ReplaceContainer recreates it)' -ForegroundColor Green }
else { & (Join-Path $pkg 'New-BuildContainer.ps1') -Distro $distro -Replace:$ReplaceContainer } # throws on any failure

Step 'L4. TLS, build.json and the build service'
$hasLinux = @((Get-Content -LiteralPath "$env:ProgramData\SourceVault\config\build.json" -Raw | ConvertFrom-Json).targets | Where-Object name -eq 'linux').Count -gt 0
if ($hasLinux -and !$ReconnectTls) {
    Write-Host '   OK   build.json already has a linux target (kept; -ReconnectTls makes new certificates); applying it' -ForegroundColor Green
    & (Join-Path $pkg 'Setup-SourceVault.ps1') -Update -WithBuilds
    if ($LASTEXITCODE -ne 0) { throw "Setup-SourceVault.ps1 -Update -WithBuilds failed (exit $LASTEXITCODE)." }
} else { & (Join-Path $here 'Add-LinuxBuildTarget.ps1') -Distro $distro -Package $pkg }

Write-Host "`nDone. In the editor: Builds -> target linux -> Build saved files." -ForegroundColor Green
