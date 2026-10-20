#Requires -RunAsAdministrator
# Creates the Windows build container for SourceVault builds: Microsoft's .NET SDK image (Windows Server Core),
# pinned by digest, on the Windows Docker engine (docs/files-history-builds-design.md, section 4; owner choice
# 2026-10-18). Needs Windows 11 Pro/Enterprise/Education with the Containers and Hyper-V features, and a Docker engine
# in Windows-container mode.
#
#   New-WindowsBuildContainer.ps1 [-Name sourcevault-build-windows] [-MemoryGB 4] [-Isolation auto|process|hyperv] [-Replace] [-ImageArchive <images\sdk-windows.tar>]
#   New-WindowsBuildContainer.ps1 -OwnerImageId sha256:<64 hex> [-ImageArchive <your-image.tar>] [-Name sourcevault-build-java] [-TargetName java-windows]
#
# -ImageArchive (offline, the deployment drive): loads the image from that archive instead of pulling, and accepts it
# only if its image ID is the pinned configuration digest below.
#
# -OwnerImageId (docs/cpp-build-design.md, section 1): YOUR build image instead of Microsoft's .NET SDK image (MSVC,
# a JDK for Ant...). It is never pulled: it must already be in this engine (docker load / docker build), or come from
# -ImageArchive, and its image ID must equal -OwnerImageId. It needs PowerShell (a Server Core base) for the keep-alive
# and a base OS this host can run. Instead of the .NET checks, the script lists the build tools it finds. The copy
# check still decides whether the build service can work with the container.
#
# What it does, with the docker CLI on this PC:
#   1. Checks that the engine is running Windows containers.
#   2. Pulls mcr.microsoft.com/dotnet/sdk by digest (about 2.7 GB). Docker refuses content that doesn't hash to it.
#   3. Creates the container with no network, a memory limit and restart with the engine. Isolation "auto" tries
#      process isolation first, then Hyper-V isolation (Windows 11 often needs Hyper-V when the host build differs
#      from the image's).
#   4. Checks it: network mode none, the pinned SDK version, and an offline test build of a fictional console app.
#   5. THE KEY CHECK: copies a fictional file into the running container and back out, the way the build service
#      does. Copying into a running Hyper-V-isolated container may be refused by Docker; if so, this target cannot
#      build yet and the script says so.
#   6. Prints the build.json target.
#
# BE AWARE: the container holds plaintext source while a build runs; whatever a build copies elsewhere inside it can
# stay there. Builds run as the image's default user (ContainerAdministrator) inside the container; the container
# boundary, not that account, is what separates it from this PC. Offline means NuGet packages can't be downloaded.
# LAB SCRIPT: not run during development; run it yourself and record the result in docs/lab-checklist.md.
param(
    [string]$Name = 'sourcevault-build-windows',
    [ValidateRange(1, 64)][int]$MemoryGB = 4,
    [ValidateSet('auto', 'process', 'hyperv')][string]$Isolation = 'auto',
    [ValidatePattern('^npipe:////\./pipe/[A-Za-z0-9._-]{1,128}$')][string]$Engine = 'npipe:////./pipe/docker_engine',
    [switch]$Replace,
    [string]$ImageArchive,
    [ValidatePattern('^sha256:[0-9a-f]{64}$')][string]$OwnerImageId,
    [ValidatePattern('^[A-Za-z0-9_-]{1,32}$')][string]$TargetName = 'windows'
)
$ErrorActionPreference = 'Stop'
# Talk to exactly the engine build.json names (default: the standard Windows engine pipe, served by Microsoft's
# standalone Docker engine), whatever docker context or Docker Desktop setting this PC's CLI would otherwise pick.
$env:DOCKER_HOST = $Engine
Remove-Item Env:DOCKER_CONTEXT -ErrorAction SilentlyContinue
# Pinned 2026-10-18: tag 10.0.402-windowsservercore-ltsc2025 (the SDK this repository builds with), manifest digest
# from mcr.microsoft.com. To move to a newer SDK, look up the new tag's digest and change both here, in one reviewed commit.
$imageRepo = 'mcr.microsoft.com/dotnet/sdk'
$imageTag = '10.0.402-windowsservercore-ltsc2025'
$imageDigest = 'sha256:c3fad2bdfc6637eef231ed3acb6718737643b88ca4877233ef29053ae3b329e5'
$sdkVersion = '10.0.402'
$image = "$imageRepo@$imageDigest"
# Its configuration digest: the image ID after `docker load` (kit-sources.json).
$imageId = 'sha256:22585ab055b5a2dfee1cc9311445592a1000e5dfbd92c16828fa4721c9a33854'
if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$') { throw 'Invalid container name.' }

function Invoke-Docker {
    # Native call; output lines returned. Throws on a non-zero exit unless -AllowFailure is the first argument.
    $allow = $args.Count -gt 0 -and $args[0] -eq '-AllowFailure'
    # @() keeps a one-item list a list (PowerShell would unroll it to a plain string).
    $rest = @(if ($allow) { if ($args.Count -gt 1) { $args[1..($args.Count - 1)] } } else { $args })
    if ($rest.Count -eq 0) { throw 'Invoke-Docker needs a docker command.' }
    $ErrorActionPreference = 'Continue' # Windows PowerShell turns native stderr lines into terminating errors under Stop
    $output = & docker.exe @rest 2>&1 # docker.exe, never a PowerShell function or alias | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0 -and !$allow) { throw "docker $($rest[0]) failed (exit $LASTEXITCODE): $($output -join ' ')" }
    return $output
}
function Ok([string]$text) { Write-Host "   OK   $text" -ForegroundColor Green }
function Encoded([string]$script) { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script)) }

if ($OwnerImageId) { Write-Host "SourceVault Windows build container from your image $OwnerImageId" -ForegroundColor White }
else { Write-Host "SourceVault Windows build container: $imageRepo`:$imageTag pinned to $imageDigest" -ForegroundColor White }
if (!(Get-Command docker.exe -CommandType Application -ErrorAction SilentlyContinue)) { throw 'The docker CLI (docker.exe) is not installed on this PC.' }
$serverOs = (Invoke-Docker -AllowFailure version --format '{{.Server.Os}}') -join ''
if ($LASTEXITCODE -ne 0 -or !$serverOs) { throw "No Docker engine answers on $Engine. Install or start the Windows engine (Microsoft's standalone Docker engine runs as the 'docker' service: Start-Service docker)." }
if ($serverOs -ne 'windows') { throw "The engine on $Engine runs $serverOs containers, not Windows containers. If Docker Desktop is also installed, quit or uninstall it: it can take over that pipe." }
Ok 'Docker engine running Windows containers'

if ((Invoke-Docker -AllowFailure container inspect --format '{{.Name}}' $Name) -and $LASTEXITCODE -eq 0) {
    if (!$Replace) { throw "A container named '$Name' already exists. Rerun with -Replace to delete and recreate it (nothing in it is kept)." }
    Invoke-Docker rm -f $Name | Out-Null
    Ok "removed the old '$Name'"
}

if ($OwnerImageId) { $imageId = $OwnerImageId } # the ID every check below must match
if ($ImageArchive) {
    $archive = (Resolve-Path -LiteralPath $ImageArchive).Path
    Write-Host "Loading the image from $archive (no download; a few minutes)..."
    Invoke-Docker load -i $archive | Out-Null
    $loaded = (Invoke-Docker -AllowFailure image inspect --format '{{.Id}}' $imageId) -join ''
    if ($loaded -ne $imageId) { throw "The archive did not load the pinned image $imageId; not creating the container." }
    $image = $imageId # create the container from the verified image ID
    Ok 'image loaded from the archive and matches the pinned image ID'
} elseif ($OwnerImageId) {
    $found = (Invoke-Docker -AllowFailure image inspect --format '{{.Id}}' $imageId) -join ''
    if ($found -ne $imageId) { throw "Image $imageId is not in the Windows Docker engine. Load it first (docker load -i <archive>) or pass -ImageArchive; SourceVault never pulls your images." }
    $image = $imageId
    Ok "your image $imageId is in the engine"
} else {
    Write-Host "Pulling $image (about 2.7 GB; Docker verifies every layer against the digest)..."
    Invoke-Docker pull $image | Out-Null
    $digests = Invoke-Docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' $image
    if (!(@($digests) -contains $image)) { throw 'The pulled image does not carry the pinned digest; not creating the container.' }
    Ok 'image pulled and matches the pinned digest'
}

$keepAlive = 'while ($true) { Start-Sleep -Seconds 3600 }'
$common = @('--network', 'none', '--memory', "${MemoryGB}g", '--restart', 'unless-stopped',
    '--label', 'sourcevault.role=build', '--label', "sourcevault.image=$image",
    '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1', '-e', 'DOTNET_NOLOGO=1', '-e', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1',
    '-e', 'DOTNET_GENERATE_ASPNET_CERTIFICATE=false', '-e', 'MSBUILDDISABLENODEREUSE=1', '-e', 'DOTNET_CLI_UI_LANGUAGE=en')
$modes = if ($Isolation -eq 'auto') { 'process', 'hyperv' } else { @($Isolation) }
$created = $null
foreach ($mode in $modes) {
    # --entrypoint: SourceVault's keep-alive, whatever ENTRYPOINT an image defines.
    $result = Invoke-Docker -AllowFailure run -d --name $Name --isolation $mode @common --entrypoint powershell.exe $image -NoProfile -Command $keepAlive
    if ($LASTEXITCODE -eq 0) { $created = $mode; break }
    Write-Host "   NOTE $mode isolation was refused: $($result -join ' ')" -ForegroundColor Yellow
    Invoke-Docker -AllowFailure rm -f $Name | Out-Null
}
if (!$created) { throw 'The container could not be started with any isolation mode (see the notes above).' }
Invoke-Docker exec $Name cmd /d /c 'mkdir C:\sourcevault\src C:\sourcevault\out' | Out-Null
Ok "container '$Name' created with $created isolation (no network, restarts with the engine)"

$network = (Invoke-Docker inspect --format '{{.HostConfig.NetworkMode}}' $Name) -join ''
if ($network -ne 'none') { throw "The container's network mode is '$network', not 'none'." }
if ($OwnerImageId) {
    Ok 'network mode none'
    # The tools your image carries, for the lab record (name: path; on PATH as the container starts).
    $tools = @'
foreach ($t in 'msbuild', 'cl', 'dotnet', 'java', 'javac', 'ant', 'mvn', 'gradle', 'cmake', 'ninja', 'python') {
    $c = Get-Command $t -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) { '{0}: {1}' -f $t, $c.Source }
}
'@
    $found = Invoke-Docker -AllowFailure exec $Name powershell -NoProfile -EncodedCommand (Encoded $tools)
    if ($found) { Write-Host '   Build tools on PATH in your image:'; $found | ForEach-Object { Write-Host "     $_" } }
    else { Write-Host '   NOTE none of the usual build tools is on PATH in your image. A build script can call tools by full path, or build.json''s command can run an environment script first (e.g. VsDevCmd.bat).' -ForegroundColor Yellow }
} else {
$version = (Invoke-Docker exec $Name dotnet --version) -join ''
if ($version -ne $sdkVersion) { throw "The container reports SDK $version, expected $sdkVersion." }
Ok "network mode none; SDK $version"
}

# The key check: copy a fictional file into the RUNNING container and back out, as the build service does.
$local = Join-Path ([IO.Path]::GetTempPath()) ('sourcevault-copycheck-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $local | Out-Null
try {
    $marker = 'SourceVault copy check (fictional) ' + [guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $local 'copy-check.txt'), $marker)
    $in = Invoke-Docker -AllowFailure cp (Join-Path $local 'copy-check.txt') "${Name}:C:\sourcevault\src\copy-check.txt"
    $copyIn = $LASTEXITCODE -eq 0
    $out = Invoke-Docker -AllowFailure cp "${Name}:C:\sourcevault\src\copy-check.txt" (Join-Path $local 'back.txt')
    $copyOut = $LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath (Join-Path $local 'back.txt')) -and ([IO.File]::ReadAllText((Join-Path $local 'back.txt')) -eq $marker)
    Invoke-Docker -AllowFailure exec $Name cmd /d /c 'del /q C:\sourcevault\src\copy-check.txt' | Out-Null
    if (!$copyIn -or !$copyOut) {
        Write-Host "   FAIL copying into/out of the running container: in=$copyIn out=$copyOut" -ForegroundColor Red
        Write-Host "        $(($in + $out) -join ' ')" -ForegroundColor Red
        Write-Host "        With $created isolation, Docker does not support this copy, so the build service cannot use this container yet." -ForegroundColor Red
        Write-Host '        Record this output in docs/lab-checklist.md; the container is left in place for inspection.' -ForegroundColor Red
        throw 'The copy check failed (see above).'
    }
    Ok "a fictional file copied into the running container and back out ($created isolation)"
}
finally { Remove-Item -LiteralPath $local -Recurse -Force -ErrorAction SilentlyContinue }

# A fictional console app, built and run offline, then removed (the .NET image only).
if (!$OwnerImageId) {
$build = @'
$ErrorActionPreference = 'Stop'
$d = 'C:\svcheck'; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $d | Out-Null
Set-Content "$d\SvCheck.csproj" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
Set-Content "$d\Program.cs" 'System.Console.WriteLine("sourcevault offline build ok");'
dotnet build "$d\SvCheck.csproj" -c Release -o "$d\out" --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet "$d\out\SvCheck.dll"
Remove-Item $d -Recurse -Force
'@
$check = Invoke-Docker -AllowFailure exec $Name powershell -NoProfile -EncodedCommand (Encoded $build)
if (@($check) -notcontains 'sourcevault offline build ok') { throw "The offline test build did not run: $($check -join ' ')" }
Ok 'a fictional console app built and ran with no network'
}

Write-Host ''
Write-Host 'Done. Use this target in C:\ProgramData\SourceVault\config\build.json:' -ForegroundColor White
@"
    {
      "name": "$TargetName",
      "engine": "$($Engine -replace '^npipe:////', 'npipe://')",
      "container": "$Name",
      "platform": "windows",
      "sourcePath": "C:\\sourcevault\\src",
      "command": ["cmd.exe", "/d", "/c", "build.cmd"],
      "artifactPath": "C:\\sourcevault\\out",
      "timeoutSeconds": 900,
      "maxArtifactMegabytes": 512,
      "allowNetwork": false
    }
"@ | Write-Output
Write-Host 'Each project needs a build.cmd at its top level that writes its output to C:\sourcevault\out (see samples\dotnet-hello).'
Write-Host 'Then give the build service access to this engine: Grant-BuildDockerAccess.ps1 (see the user guide).'
