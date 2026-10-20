#Requires -RunAsAdministrator
# Lab helper (2026-10-20; owner-run, see README.md): a "java" build target for Ant projects (Ant kept in the project), on the Linux engine in WSL.
#   1. Pulls a JDK image into the SourceVault distro's engine (eclipse-temurin, about 200 MB) and reads its image ID.
#   2. New-BuildContainer.ps1 -OwnerImageId: container 'sourcevault-build-java' from exactly that ID (no network,
#      all capabilities dropped), and lists the tools it finds.
#   3. build.json gets a "java" target using the linux target's engine and TLS settings (other targets are kept).
#   4. Setup -Update -WithBuilds applies it (and installs the package's editor, with Build steps), then -Status.
# Run after the Linux target works (the package's Setup-Builds.ps1). Close SourceVault editor windows first.
#   powershell -ExecutionPolicy Bypass -File .\Add-JavaBuildTarget.ps1 [-JdkTag 21-jdk] [-Replace]
# -JdkTag: the eclipse-temurin tag for the JDK your project needs, e.g. 8-jdk, 11-jdk, 17-jdk, 21-jdk.
param(
    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')][string]$JdkTag = '21-jdk',
    [string]$Distro = 'SourceVault-Ubuntu',
    [string]$Package = (Join-Path $PSScriptRoot '..\..\artifacts\installer\SourceVault-Setup'),
    [switch]$Replace
)
$ErrorActionPreference = 'Stop'
$buildJson = Join-Path $env:ProgramData 'SourceVault\config\build.json'
$container = 'sourcevault-build-java'
function Ok([string]$m) { Write-Host "   OK   $m" -ForegroundColor Green }
function Step([string]$m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Wsl { $out = & wsl.exe -d $Distro -u root --exec @args; if ($LASTEXITCODE -ne 0) { throw "In '$Distro': $($args -join ' ') failed (exit $LASTEXITCODE)." }; $out }
if (Get-Process -Name 'SourceVault.Editor', 'SourceVault.LabClient' -ErrorAction SilentlyContinue) { throw 'Close all SourceVault editor and lab client windows first (setup needs them closed).' }
$config = Get-Content -LiteralPath $buildJson -Raw | ConvertFrom-Json
$linux = @($config.targets | Where-Object name -eq 'linux')[0]
if (!$linux) { throw 'build.json has no linux target yet: set up the Linux target first (guided setup option 6, or Setup-Builds.ps1).' }

Step "1. JDK image eclipse-temurin:$JdkTag"
Wsl docker pull "eclipse-temurin:$JdkTag" | Out-Null
$imageId = (Wsl docker image inspect --format '{{.Id}}' "eclipse-temurin:$JdkTag") -join ''
if ($imageId -notmatch '^sha256:[0-9a-f]{64}$') { throw "Could not read the image ID ($imageId)." }
Ok "pulled; image ID $imageId (record it: the container is created from exactly this ID)"

Step "2. Container $container"
$names = (Wsl docker ps -a --format '{{.Names}}') -join ' '
if ($names -match "\b$container\b" -and !$Replace) { Ok "$container already exists (kept; -Replace recreates it from this image)" }
else { & (Join-Path $Package 'New-BuildContainer.ps1') -Distro $Distro -OwnerImageId $imageId -Name $container -TargetName java -Replace:$Replace }

Step '3. The java target in build.json'
$java = [ordered]@{
    name = 'java'; engine = $linux.engine
    tls = [ordered]@{ caFile = $linux.tls.caFile; clientCertificateThumbprint = $linux.tls.clientCertificateThumbprint }
    container = $container; platform = 'linux'; sourcePath = '/sourcevault/src'
    command = @('/bin/sh', '-c', 'sed -i ''s/\r$//'' build.sh && exec /bin/sh build.sh')
    artifactPath = '/sourcevault/out'; timeoutSeconds = 1800; maxArtifactMegabytes = 1024; allowNetwork = $false
}
$config.targets = @(@($config.targets | Where-Object { $_.name -ne 'java' }) + (New-Object PSObject -Property $java))
[IO.File]::WriteAllText($buildJson, ($config | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
Ok "build.json targets: $(($config.targets | ForEach-Object name) -join ', ')"

Step '4. Apply'
& (Join-Path $Package 'Setup-SourceVault.ps1') -Update -WithBuilds
if ($LASTEXITCODE -ne 0) { throw "Setup-SourceVault.ps1 -Update -WithBuilds failed (exit $LASTEXITCODE); fix the FAIL line above and run this again." }
& (Join-Path $Package 'Setup-SourceVault.ps1') -Status
Write-Host "`nDone. In the editor: your Ant project -> Builds -> target java -> Build steps (Ant template) -> edit, save -> Build saved files." -ForegroundColor Green
