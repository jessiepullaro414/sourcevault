#Requires -RunAsAdministrator
# SourceVault: set up Docker builds on this PC, without the deployment drive. Guided setup's "Set up or refresh Docker
# builds" runs this; it also runs on its own, from the setup package, in an elevated PowerShell:
#   powershell -ExecutionPolicy Bypass -File .\Setup-Builds.ps1 [-Windows yes|no] [-Linux yes|no] [-Downloads <folder>]
#                                                         [-ArtifactRoot <folder>] [-ReconnectTls]
# The same steps as Deploy-SourceVault.ps1 phases 2-6, with downloads instead of the drive:
#   1. Windows target: the Containers feature, Microsoft's standalone Docker engine with a 'docker' group that only the
#      build service joins, and the container 'sourcevault-build-windows' (Microsoft's .NET SDK image, pinned).
#   2. Linux target: WSL, a distro of its own ('SourceVault-Ubuntu'; your own distros are untouched), Docker's static
#      engine in it (no bridge, no iptables), the container 'sourcevault-build-linux', and TLS on 127.0.0.1:2376 so only
#      the build service (holding the client certificate) can use it from Windows.
#   3. build.json with those targets (other targets are kept), Docker access for the build service, setup -Update
#      -WithBuilds, and -Status.
# Each file it needs is looked for in -Downloads first. A missing one is downloaded only after you say yes, and every
# file is checked against kit-sources.json (size, and SHA-256 where one is pinned) before it is used. Finished steps are
# skipped, so after a restart you just run it again. Close SourceVault editor windows first.
# BE AWARE: the WSL distro belongs to the Windows account that runs this and runs only while that account is signed in
# (a sign-in task keeps it up); the Linux target can't build while that account is signed out. Docker access is
# administrator-equivalent on this PC; only administrators and the build service get it.
param(
    [ValidateSet('ask', 'yes', 'no')][string]$Windows = 'ask',
    [ValidateSet('ask', 'yes', 'no')][string]$Linux = 'ask',
    [string]$Downloads = (Join-Path $env:SystemDrive 'SourceVault-Downloads'),
    [string]$ArtifactRoot = (Join-Path $env:SystemDrive 'SourceVault-Builds\artifacts'),
    [switch]$ReconnectTls
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is many times slower with its progress bar
$package = $PSScriptRoot
$setup = Join-Path $package 'Setup-SourceVault.ps1'
$sources = Get-Content -LiteralPath (Join-Path $package 'kit-sources.json') -Raw | ConvertFrom-Json
$distro = 'SourceVault-Ubuntu'
$distroDir = Join-Path $env:ProgramData 'SourceVault-WSL\Ubuntu'
$configDir = Join-Path $env:ProgramData 'SourceVault\config'
$buildJson = Join-Path $configDir 'build.json'
$dockerExe = Join-Path $env:ProgramFiles 'Docker\docker.exe'
$windowsEngine = 'npipe:////./pipe/docker_engine'
$port = 2376
$script:restart = $false

$script:reported = $false
trap {
    if (!$script:reported) { Write-Host "   FAIL $($_.Exception.Message)" -ForegroundColor Red }
    Write-Host "`nBuild setup stopped. Fix the problem above and run it again; finished steps are skipped." -ForegroundColor Red
    exit 1
}
function Step([string]$t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Ok([string]$t) { Write-Host "   OK   $t" -ForegroundColor Green }
function Warn([string]$t) { Write-Host "   NOTE $t" -ForegroundColor Yellow }
function Fail([string]$t) { Write-Host "   FAIL $t" -ForegroundColor Red; $script:reported = $true; throw $t }
function Confirm-Step([string]$question, [bool]$default = $true) {
    $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
    $answer = (Read-Host "   $question $hint").Trim()
    if (!$answer) { return $default }
    return $answer -match '^(y|yes)$'
}
function Want([string]$choice, [string]$question) { if ($choice -eq 'ask') { Confirm-Step $question } else { $choice -eq 'yes' } }
# Runs one of the package's scripts in its own PowerShell; its output goes to the screen and only its exit code is returned.
function Invoke-Script([string]$path, [string[]]$arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $path @arguments | Out-Host
    return $LASTEXITCODE
}
function Invoke-Docker {
    $env:DOCKER_HOST = $windowsEngine; Remove-Item Env:DOCKER_CONTEXT -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Continue' # native stderr lines are not errors here
    $output = & $dockerExe @args 2>&1 | ForEach-Object { "$_" }
    return $output
}
function Invoke-Wsl([string]$script, [switch]$AllowFailure) {
    $output = ($script -replace "`r", '') | & wsl.exe -d $distro -u root --exec sh -c 'sed -e ''1s/^\xEF\xBB\xBF//'' -e ''s/\r$//'' | sh' # strip the CR/BOM PowerShell adds to piped text
    if ($LASTEXITCODE -ne 0 -and !$AllowFailure) { Fail "A command in the WSL distro failed (exit $LASTEXITCODE)." }
    return $output
}
function Enable-Feature([string]$name, [string]$label) {
    $info = Get-WindowsOptionalFeature -Online -FeatureName $name
    if ($info.State -eq 'Enabled') { return }
    $result = Enable-WindowsOptionalFeature -Online -FeatureName $name -All -NoRestart
    Ok "turned on $label"
    if ($result.RestartNeeded) { $script:restart = $true }
}

# A pinned file from kit-sources.json, from -Downloads; downloaded first (after a yes) if it isn't there.
function Get-PinnedFile([string]$name) {
    $entry = $sources.files | Where-Object name -eq $name
    if (!$entry) { Fail "kit-sources.json has no entry '$name'." }
    $leaf = Split-Path -Leaf ($entry.path -replace '/', '\')
    $path = Join-Path $Downloads $leaf
    if (!(Test-Path -LiteralPath $path)) {
        $mb = [math]::Round($entry.size / 1MB)
        if (!(Confirm-Step "Download $leaf ($mb MB) from $($entry.url)?")) { Fail "Put $leaf in $Downloads, then run this again." }
        New-Item -ItemType Directory -Force -Path $Downloads | Out-Null
        $partial = "$path.partial"
        Invoke-WebRequest -Uri $entry.url -OutFile $partial -UseBasicParsing
        Move-Item -LiteralPath $partial -Destination $path -Force
    }
    if ((Get-Item -LiteralPath $path).Length -ne $entry.size) { Fail "$path is not the pinned size ($($entry.size) bytes). Delete it and run this again." }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($entry.sha256) {
        if ($hash -ne $entry.sha256) { Fail "$path does not match its pinned SHA-256. Delete it and run this again." }
        Ok "$leaf matches its pinned SHA-256"
    } else { Warn "$leaf has no pinned SHA-256 (its publisher gives none); it is $hash" }
    return $path
}

# ---- what to set up ---------------------------------------------------------------------------------------------
Write-Host 'SourceVault: set up Docker builds on this PC (development build: synthetic data only)' -ForegroundColor White
if (!(Get-Service -Name SourceVault -ErrorAction SilentlyContinue)) { Fail 'SourceVault is not installed on this PC yet. Install it first (SourceVault Setup.cmd).' }
if (Get-Process -Name 'SourceVault.Editor', 'SourceVault.LabClient' -ErrorAction SilentlyContinue) { Fail 'Close all SourceVault editor and lab client windows first (setup replaces the program files).' }
Write-Host '   Builds run in Docker containers with no network. Docker access is administrator-equivalent on this PC:'
Write-Host '   only administrators and the SourceVault build service get it.'
$doWindows = Want $Windows 'Set up the Windows build target (Windows Containers; about 2.8 GB of downloads)?'
$doLinux = Want $Linux 'Set up the Linux build target (WSL; about 800 MB of downloads)?'
if (!$doWindows -and !$doLinux) { Write-Host 'Nothing to set up.'; exit 0 }

# ---- 1. engines ---------------------------------------------------------------------------------------------------
if ($doWindows) {
    Step "Windows engine (Microsoft's standalone Docker engine)"
    Enable-Feature 'Containers' 'Windows Containers'
    if (!$script:restart) {
        if (!(Get-Service -Name docker -ErrorAction SilentlyContinue)) {
            $zip = Get-PinnedFile 'docker-windows'
            $installer = Get-PinnedFile 'install-docker-ce'
            $work = Join-Path $env:TEMP ('sourcevault-docker-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $work | Out-Null
            try {
                Expand-Archive -LiteralPath $zip -DestinationPath $work
                Push-Location $work # Microsoft's installer writes its progress file in the current folder
                try {
                    $code = Invoke-Script $installer @('-DockerPath', (Join-Path $work 'docker\docker.exe'), '-DockerDPath', (Join-Path $work 'docker\dockerd.exe'), '-NoRestart')
                    if ($code -ne 0 -or (Test-Path -LiteralPath (Join-Path $work 'Install-ContainerHost.err'))) { Fail "Microsoft's install-docker-ce.ps1 did not finish (see above)." }
                } finally { Pop-Location }
            } finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
            Ok 'installed the docker service'
        } else { Ok 'docker service already installed' }
        if (!(Get-LocalGroup -Name docker -ErrorAction SilentlyContinue)) {
            New-LocalGroup -Name docker -Description 'May use Docker (SourceVault build service only)' | Out-Null # 48-char limit
            Ok "created the local group 'docker' (empty; only the build service joins it)"
        }
        $daemon = Join-Path $env:ProgramData 'docker\config\daemon.json'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $daemon) | Out-Null
        $settings = if (Test-Path -LiteralPath $daemon) { Get-Content -LiteralPath $daemon -Raw | ConvertFrom-Json } else { $null }
        if ($null -eq $settings) { $settings = New-Object PSObject }
        if ($settings.group -ne 'docker' -or @($settings.hosts) -notcontains 'npipe://') {
            $settings | Add-Member -NotePropertyName hosts -NotePropertyValue @('npipe://') -Force
            $settings | Add-Member -NotePropertyName group -NotePropertyValue 'docker' -Force
            [IO.File]::WriteAllText($daemon, ($settings | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
            Restart-Service docker
            Ok "engine settings: administrators and 'docker' may use it (other settings kept)"
        }
        $os = $null
        foreach ($i in 1..30) { $os = (Invoke-Docker version --format '{{.Server.Os}}') -join ''; if ($os -eq 'windows') { break }; Start-Sleep -Seconds 2 }
        if ($os -ne 'windows') { Fail "The Windows Docker engine did not answer on \\.\pipe\docker_engine. If Docker Desktop is installed, quit or uninstall it: it can take over that pipe." }
        Ok 'Windows engine running'
    }
}
if ($doLinux) {
    Step 'Linux engine (Docker in WSL)'
    & wsl.exe --version *> $null
    if ($LASTEXITCODE -ne 0) {
        Enable-Feature 'VirtualMachinePlatform' 'Virtual Machine Platform (needed by WSL 2)'
        $msi = Get-PinnedFile 'wsl'
        $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart') -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { Fail "The WSL installer failed (msiexec exit $($p.ExitCode))." }
        if ($p.ExitCode -eq 3010) { $script:restart = $true }
        Ok 'installed WSL'
    }
    if (!$script:restart) {
        $known = (& wsl.exe -l -q) -replace "`0", '' | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() }
        if ($known -notcontains $distro) {
            $rootfs = Get-PinnedFile 'ubuntu'
            New-Item -ItemType Directory -Force -Path $distroDir | Out-Null
            & wsl.exe --import $distro $distroDir $rootfs --version 2
            if ($LASTEXITCODE -ne 0) { Fail "wsl --import failed (exit $LASTEXITCODE)." }
            Ok "imported Ubuntu 24.04 as '$distro' for $env:USERNAME"
        } else { Ok "'$distro' already exists" }
        $running = (Invoke-Wsl 'docker version --format "{{.Server.Version}}" 2>/dev/null' -AllowFailure) -join ''
        if ($LASTEXITCODE -ne 0 -or !$running) {
            $tgz = Get-PinnedFile 'docker-linux'
            $tgzLinux = (& wsl.exe -d $distro -u root --exec wslpath -u $tgz) -join ''
            $install = @'
set -eu
printf '[boot]\nsystemd=true\n[user]\ndefault=root\n' > /etc/wsl.conf
tar -xzf '__TGZ__' -C /usr/local/bin --strip-components=1
getent group docker >/dev/null || groupadd docker
mkdir -p /etc/docker
[ -f /etc/docker/daemon.json ] || printf '{"bridge": "none", "iptables": false, "ip6tables": false, "hosts": ["unix:///var/run/docker.sock"]}\n' > /etc/docker/daemon.json
cat > /etc/systemd/system/docker.service <<'UNIT'
[Unit]
Description=Docker Engine (SourceVault build target)
After=network-online.target
[Service]
Type=notify
ExecStart=/usr/local/bin/dockerd
ExecReload=/bin/kill -s HUP $MAINPID
Restart=always
LimitNOFILE=infinity
Delegate=yes
KillMode=process
[Install]
WantedBy=multi-user.target
UNIT
mkdir -p /etc/systemd/system/multi-user.target.wants
ln -sf /etc/systemd/system/docker.service /etc/systemd/system/multi-user.target.wants/docker.service
'@
            Invoke-Wsl ($install -replace '__TGZ__', $tgzLinux) | Out-Null
            & wsl.exe --terminate $distro | Out-Null # restart the distro so systemd (from wsl.conf) starts Docker
            Start-Sleep -Seconds 3
            foreach ($i in 1..30) { $running = (Invoke-Wsl 'docker version --format "{{.Server.Version}}" 2>/dev/null' -AllowFailure) -join ''; if ($LASTEXITCODE -eq 0 -and $running) { break }; Start-Sleep -Seconds 2 }
            if (!$running) { Fail "Docker did not start inside '$distro'. Look with: wsl -d $distro -u root -- systemctl status docker" }
        }
        Ok "Docker $running running in '$distro' (no bridge network, no iptables)"
        # WSL stops an idle distro; this sign-in task keeps it running. conhost --headless: no console window.
        $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless wsl.exe -d $distro -u root --exec sleep infinity"
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
        $taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -Hidden
        Register-ScheduledTask -TaskName 'SourceVault WSL keep-alive' -Action $action -Trigger $trigger -Settings $taskSettings -User "$env:USERDOMAIN\$env:USERNAME" -RunLevel Limited -Force | Out-Null
        Start-ScheduledTask -TaskName 'SourceVault WSL keep-alive'
        Ok "sign-in task 'SourceVault WSL keep-alive' keeps '$distro' running while $env:USERNAME is signed in"
    }
}
if ($script:restart) {
    Write-Host "`nRestart this PC to finish turning on Windows features, then run the build setup again (guided setup, option 6). Finished steps are skipped." -ForegroundColor Yellow
    exit 0
}

# ---- 2. the build service (its account must exist before Docker access and the TLS key are granted to it) --------
if (!(Get-Service -Name SourceVaultBuild -ErrorAction SilentlyContinue)) {
    Step 'Build service'
    if ((Invoke-Script $setup @('-Update', '-WithBuilds')) -ne 0) { Fail 'Setup could not register the build service (see above).' }
}

# ---- 3. containers ------------------------------------------------------------------------------------------------
$windowsReady = $false; $linuxReady = $false
if ($doWindows) {
    Step 'Windows build container'
    $exists = (Invoke-Docker container inspect --format '{{.Name}}' 'sourcevault-build-windows') -join ''
    if ($LASTEXITCODE -eq 0 -and $exists) { Ok 'sourcevault-build-windows already exists (kept)'; $windowsReady = $true }
    elseif (Confirm-Step "Download Microsoft's pinned Windows .NET SDK image (about 2.7 GB) and create the container?") {
        if ((Invoke-Script (Join-Path $package 'New-WindowsBuildContainer.ps1') @()) -eq 0) { $windowsReady = $true }
        else { Warn 'the Windows build container is not usable (see above, especially the copy check); the windows target is left out' }
    } else { Warn 'no Windows build container; the windows target is left out' }
}
if ($doLinux) {
    Step 'Linux build container'
    $names = (Invoke-Wsl 'docker ps -a --format "{{.Names}}"') -join ' '
    if ($names -match '\bsourcevault-build-linux\b') { Ok 'sourcevault-build-linux already exists (kept)'; $linuxReady = $true }
    elseif (Confirm-Step "Download Microsoft's pinned Linux .NET SDK image (about 335 MB) and create the container?") {
        if ((Invoke-Script (Join-Path $package 'New-BuildContainer.ps1') @('-Distro', $distro)) -ne 0) { Fail 'The Linux build container could not be created (see above).' }
        $linuxReady = $true
    } else { Warn 'no Linux build container; the linux target is left out' }
}

# ---- 4. build.json (and TLS for the Linux engine) -----------------------------------------------------------------
Step 'Build configuration'
$config = if (Test-Path -LiteralPath $buildJson) { Get-Content -LiteralPath $buildJson -Raw | ConvertFrom-Json } else { [pscustomobject]@{ version = 1; artifactRoot = $ArtifactRoot; targets = @() } }
$existingLinux = @($config.targets | Where-Object name -eq 'linux')[0]
$targets = @($config.targets | Where-Object { $_.name -notin 'windows', 'linux' })
if ($windowsReady) {
    $targets += [pscustomobject][ordered]@{
        name = 'windows'; engine = 'npipe://./pipe/docker_engine'; container = 'sourcevault-build-windows'; platform = 'windows'
        sourcePath = 'C:\sourcevault\src'; command = @('cmd.exe', '/d', '/c', 'build.cmd'); artifactPath = 'C:\sourcevault\out'
        timeoutSeconds = 900; maxArtifactMegabytes = 512; allowNetwork = $false
    }
} elseif (@($config.targets | Where-Object name -eq 'windows')) { $targets += @($config.targets | Where-Object name -eq 'windows') } # not set up now: kept as it was
if ($linuxReady) {
    if ($existingLinux -and !$ReconnectTls) { $targets += $existingLinux; Ok 'linux target kept (its TLS certificate too; -ReconnectTls makes new ones)' }
    else {
        $before = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq 'CN=SourceVaultBuild' | ForEach-Object Thumbprint)
        if ((Invoke-Script (Join-Path $package 'New-WslDockerTls.ps1') @('-Distro', $distro, '-Port', "$port")) -ne 0) { Fail 'Could not set up TLS for the Linux engine (see above).' }
        $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq 'CN=SourceVaultBuild' -and $before -notcontains $_.Thumbprint } | Sort-Object NotBefore -Descending | Select-Object -First 1
        if (!$cert) { Fail 'The TLS client certificate was not found in LocalMachine\My.' }
        $daemonTls = @'
set -eu
cat > /etc/docker/daemon.json <<'JSON'
{"bridge": "none", "iptables": false, "ip6tables": false,
 "tls": true, "tlsverify": true,
 "tlscacert": "/etc/docker/sourcevault-tls/ca.pem", "tlscert": "/etc/docker/sourcevault-tls/server-cert.pem", "tlskey": "/etc/docker/sourcevault-tls/server-key.pem",
 "hosts": ["unix:///var/run/docker.sock", "tcp://127.0.0.1:__PORT__"]}
JSON
systemctl restart docker
'@
        Invoke-Wsl ($daemonTls -replace '__PORT__', $port) | Out-Null
        $reachable = $false
        foreach ($i in 1..20) {
            $client = New-Object Net.Sockets.TcpClient
            try { $client.Connect('127.0.0.1', $port); $reachable = $true; break } catch { Start-Sleep -Seconds 2 } finally { $client.Dispose() }
        }
        if (!$reachable) { Fail "Nothing answers on 127.0.0.1:$port from Windows. Check WSL's localhost forwarding and: wsl -d $distro -u root -- systemctl status docker" }
        Ok "Linux engine listening with TLS on 127.0.0.1:$port (client certificate $($cert.Thumbprint) required)"
        $targets += [pscustomobject][ordered]@{
            name = 'linux'; engine = "tcp://127.0.0.1:$port"
            tls = [pscustomobject][ordered]@{ caFile = (Join-Path $configDir 'wsl-docker-ca.pem'); clientCertificateThumbprint = $cert.Thumbprint }
            container = 'sourcevault-build-linux'; platform = 'linux'; sourcePath = '/sourcevault/src'
            command = @('/bin/sh', '-c', 'sed -i ''s/\r$//'' build.sh && exec /bin/sh build.sh'); artifactPath = '/sourcevault/out'
            timeoutSeconds = 900; maxArtifactMegabytes = 512; allowNetwork = $false
        }
    }
} elseif ($existingLinux) { $targets += $existingLinux }
if (!$targets) { Fail 'No build target is ready, so there is nothing to configure (see the notes above).' }
$config.targets = @($targets)
[IO.File]::WriteAllText($buildJson, ($config | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
Ok "build.json: targets $(($config.targets | ForEach-Object name) -join ', '); output in $($config.artifactRoot)"

# ---- 5. access and apply ------------------------------------------------------------------------------------------
if ($windowsReady) {
    Step 'Docker access for the build service'
    if ((Invoke-Script (Join-Path $package 'Grant-BuildDockerAccess.ps1') @('-Group', 'docker')) -ne 0) { Fail 'Could not give the build service access to the Windows engine (see above).' }
}
Step 'Apply'
if ((Invoke-Script $setup @('-Update', '-WithBuilds')) -ne 0) { Fail 'Setup could not apply the build configuration (see above).' }
Invoke-Script $setup @('-Status') | Out-Null
Write-Host "`nBuilds are set up. In the editor: open a project, then Builds; Build steps creates the build script a target runs." -ForegroundColor Green
