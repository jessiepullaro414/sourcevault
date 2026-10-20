# Creates the Linux build container for SourceVault builds: Microsoft's .NET SDK image, pinned by digest, on the
# Docker engine inside a WSL distro (docs/files-history-builds-design.md, section 4; owner choice 2026-10-18).
#
#   New-BuildContainer.ps1 -Distro Ubuntu-24.04 [-Name sourcevault-build-linux] [-MemoryGB 4] [-Replace] [-ImageArchive <images\sdk-linux.tar>]
#
# -ImageArchive (offline, the deployment drive): loads the image from that archive instead of pulling, and accepts it
# only if its image ID is the pinned configuration digest below.
#
# What it does, all inside the distro (as root):
#   1. Checks that Docker is installed and its engine is running.
#   2. Pulls mcr.microsoft.com/dotnet/sdk by digest (about 335 MB for amd64). Pulling by digest means Docker refuses
#      any content that doesn't hash to it, so the image cannot change under the same name.
#   3. Creates the container: no network, all Linux capabilities dropped, no privilege escalation, an init process,
#      a process and memory limit, /tmp in memory, and restart with the engine. It just waits (sleep); the build
#      service copies a project in, runs build.json's command, and copies the output out.
#   4. Checks it: the network mode is none, the SDK version is the pinned one, and a fictional console app builds and
#      runs offline.
#   5. Prints the build.json target. The engine still needs TLS for the build service: run New-WslDockerTls.ps1.
#
# BE AWARE: the container holds plaintext source while a build runs, and whatever a build copies elsewhere inside it
# (caches, /root) can stay there; emptying the source folder is best effort, not erasure. Offline means NuGet
# packages can't be downloaded: projects that reference packages need them provided another way (not built yet).
# LAB SCRIPT: not run during development; run it yourself and record the result in docs/lab-checklist.md.
param(
    [Parameter(Mandatory)][string]$Distro,
    [string]$Name = 'sourcevault-build-linux',
    [ValidateRange(1, 64)][int]$MemoryGB = 4,
    [switch]$Replace,
    [string]$ImageArchive
)
$ErrorActionPreference = 'Stop'
# Pinned 2026-10-18: tag 10.0.402 (the SDK this repository builds with), multi-platform index digest from
# mcr.microsoft.com. To move to a newer SDK, look up the new tag's digest and change both here, in one reviewed commit.
$imageRepo = 'mcr.microsoft.com/dotnet/sdk'
$imageTag = '10.0.402'
$imageDigest = 'sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa'
$image = "$imageRepo@$imageDigest"
# The linux/amd64 image of that index: its configuration digest is the image ID after `docker load` (kit-sources.json).
$imageId = 'sha256:9cd0d6fa04def99891bf4d5a29eb9a3878b6041f9cc25cb389ea0df2177d23fd'

if ($Distro -notmatch '^[A-Za-z0-9._-]{1,64}$') { throw 'Invalid distro name.' }
if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$') { throw 'Invalid container name.' }

function Invoke-Wsl([string]$script, [switch]$AllowFailure) {
    # The script goes in on stdin (LF line endings) so nothing is interpolated into a Windows command line.
    $output = ($script -replace "`r", '') | & wsl.exe -d $Distro -u root --exec sh -c 'sed -e ''1s/^\xEF\xBB\xBF//'' -e ''s/\r$//'' | sh' # strip the CR/BOM PowerShell adds to piped text
    if ($LASTEXITCODE -ne 0 -and !$AllowFailure) { throw "The command in WSL distro '$Distro' failed (exit $LASTEXITCODE)." }
    return $output
}
function Ok([string]$text) { Write-Host "   OK   $text" -ForegroundColor Green }

Write-Host "SourceVault build container: $imageRepo`:$imageTag pinned to $imageDigest" -ForegroundColor White
& wsl.exe -d $Distro -u root -- true
if ($LASTEXITCODE -ne 0) { throw "WSL distro '$Distro' could not be started (see 'wsl -l -v')." }
Invoke-Wsl 'command -v docker >/dev/null || { echo "docker is not installed in the distro" >&2; exit 3; }; docker version --format "{{.Server.Version}}" >/dev/null || { echo "the Docker engine is not running in the distro" >&2; exit 4; }' | Out-Null
Ok "Docker engine running in '$Distro'"

$exists = (Invoke-Wsl "docker container inspect --format '{{.Name}}' '$Name' 2>/dev/null" -AllowFailure)
if ($exists) {
    if (!$Replace) { throw "A container named '$Name' already exists. Rerun with -Replace to delete and recreate it (nothing in it is kept)." }
    Invoke-Wsl "docker rm -f '$Name'" | Out-Null
    Ok "removed the old '$Name'"
}

if ($ImageArchive) {
    $archive = (Resolve-Path -LiteralPath $ImageArchive).Path
    $linuxPath = (& wsl.exe -d $Distro -u root --exec wslpath -u $archive) -join ''
    if ($LASTEXITCODE -ne 0 -or $linuxPath -notmatch '^/') { throw "WSL could not see $archive." }
    Write-Host "Loading the image from $archive (no download)..."
    Invoke-Wsl "docker load -i '$($linuxPath -replace "'", "'\''")'" | Out-Null
    $loaded = (Invoke-Wsl "docker image inspect --format '{{.Id}}' '$imageId' 2>/dev/null" -AllowFailure) -join ''
    if ($loaded -ne $imageId) { throw "The archive did not load the pinned image $imageId; not creating the container." }
    $image = $imageId # create the container from the verified image ID
    Ok "image loaded from the archive and matches the pinned image ID"
} else {
    Write-Host "Pulling $image (about 335 MB for amd64; Docker verifies every layer against the digest)..."
    Invoke-Wsl "docker pull '$image'" | Out-Null
    $digests = Invoke-Wsl "docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' '$image'"
    if (!(@($digests) -contains $image)) { throw "The pulled image does not carry the pinned digest; not creating the container." }
    Ok "image pulled and matches the pinned digest"
}

$limitGB = $MemoryGB
Invoke-Wsl @"
set -eu
docker run -d --name '$Name' \
  --network none --cap-drop ALL --security-opt no-new-privileges --init \
  --pids-limit 512 --memory ${limitGB}g --tmpfs /tmp:rw,size=1g \
  --restart unless-stopped \
  --label sourcevault.role=build --label sourcevault.image='$image' \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  -e DOTNET_GENERATE_ASPNET_CERTIFICATE=false -e MSBUILDDISABLENODEREUSE=1 -e DOTNET_CLI_UI_LANGUAGE=en \
  -w /sourcevault '$image' sleep infinity >/dev/null
docker exec '$Name' mkdir -p /sourcevault/src /sourcevault/out
"@ | Out-Null
Ok "container '$Name' created (no network, capabilities dropped, restarts with the engine)"

$network = (Invoke-Wsl "docker inspect --format '{{.HostConfig.NetworkMode}}' '$Name'") -join ''
if ($network -ne 'none') { throw "The container's network mode is '$network', not 'none'." }
$version = (Invoke-Wsl "docker exec '$Name' dotnet --version") -join ''
if ($version -ne $imageTag) { throw "The container reports SDK $version, expected $imageTag." }
Ok "network mode none; SDK $version"

# A fictional console app, built and run offline in /tmp (memory), then removed.
$check = Invoke-Wsl @"
set -eu
docker exec -w /tmp '$Name' sh -c 'rm -rf svcheck && mkdir svcheck && cd svcheck \
  && printf "%s\n" "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>" > SvCheck.csproj \
  && printf "%s\n" "System.Console.WriteLine(\"sourcevault offline build ok\");" > Program.cs \
  && dotnet build -c Release -o out --nologo -v quiet >/dev/null && dotnet out/SvCheck.dll; cd /tmp && rm -rf svcheck'
"@
if (@($check) -notcontains 'sourcevault offline build ok') { throw 'The offline test build did not run.' }
Ok 'a fictional console app built and ran with no network'

Write-Host ''
Write-Host 'Done. Use this target in C:\ProgramData\SourceVault\config\build.json (the tls block comes from New-WslDockerTls.ps1):' -ForegroundColor White
@"
    {
      "name": "linux",
      "engine": "tcp://127.0.0.1:2376",
      "tls": { "caFile": "C:\\ProgramData\\SourceVault\\config\\wsl-docker-ca.pem", "clientCertificateThumbprint": "<from New-WslDockerTls.ps1>" },
      "container": "$Name",
      "platform": "linux",
      "sourcePath": "/sourcevault/src",
      "command": ["/bin/sh", "-c", "sed -i 's/\\r`$//' build.sh && exec /bin/sh build.sh"],
      "artifactPath": "/sourcevault/out",
      "timeoutSeconds": 900,
      "maxArtifactMegabytes": 512,
      "allowNetwork": false
    }
"@ | Write-Output
Write-Host 'Each project needs a build.sh at its top level, for example: dotnet publish -c Release -o /sourcevault/out (see samples\dotnet-hello).'
