#Requires -RunAsAdministrator
# Sets up a SourceVault PC entirely from the deployment drive, with no internet (docs/deployment-kit-design.md).
# Run from the drive, in an ELEVATED Windows PowerShell:
#
#   powershell -ExecutionPolicy Bypass -File .\Deploy-SourceVault.ps1 [-VaultRoot K:\SourceVault\vault]
#   powershell -ExecutionPolicy Bypass -File .\Deploy-SourceVault.ps1 -ImportPackages     later: import transfer\*.svpkg
#
# It works in phases and remembers where it got to (C:\ProgramData\SourceVault-Deploy\state.json). When it asks you to
# restart or sign out, do so and run it again: it continues. Every phase is safe to repeat.
#   1 checks   kit integrity, Windows edition, TPM, virtualization, disk space; asks where the vault goes
#   2 features Containers, Hyper-V, Virtual Machine Platform, WSL (then a restart)
#   3 docker   Docker's Windows engine from the drive (Microsoft's installer, offline), "docker" group
#   4 wsl      WSL and Ubuntu from the drive, Docker's Linux engine inside it (no network, no iptables)
#   5 builds   both build containers from the drive's images (checked by image ID)
#   6 vault    SourceVault: setup, build service, TLS to the WSL engine, build.json (then a sign-out)
#   7 transfer this PC's recipient file to the drive's transfer\ folder
#
# DEVELOPMENT BUILD: synthetic data only until an independent security review says otherwise.
param([string]$VaultRoot, [switch]$ImportPackages)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$kit = $PSScriptRoot
$stateDir = Join-Path $env:ProgramData 'SourceVault-Deploy'
$statePath = Join-Path $stateDir 'state.json'
$distro = 'SourceVault-Ubuntu'
$distroDir = Join-Path $env:ProgramData 'SourceVault-WSL\Ubuntu'
$dockerExe = Join-Path $env:ProgramFiles 'Docker\docker.exe'
$installRoot = Join-Path $env:ProgramFiles 'SourceVault'
$sources = Get-Content -LiteralPath (Join-Path $kit 'kit-sources.json') -Raw | ConvertFrom-Json
function KitFile([string]$name) { $f = $sources.files | Where-Object name -eq $name; Join-Path $kit ($f.path -replace '/', '\') }
function KitImage([string]$name) { $i = $sources.images | Where-Object name -eq $name; Join-Path $kit ($i.path -replace '/', '\') }

$script:reported = $false
trap {
    if (!$script:reported) { Write-Host "   FAIL $($_.Exception.Message)" -ForegroundColor Red }
    Write-Host "`nDeployment stopped. Fix the problem above and run Deploy-SourceVault.ps1 again; it continues where it stopped." -ForegroundColor Red
    exit 1
}
function Step([string]$t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Ok([string]$t) { Write-Host "   OK   $t" -ForegroundColor Green }
function Warn([string]$t) { Write-Host "   NOTE $t" -ForegroundColor Yellow }
function Fail([string]$t) { Write-Host "   FAIL $t" -ForegroundColor Red; $script:reported = $true; throw $t }
function Save-State($state) { New-Item -ItemType Directory -Force -Path $stateDir | Out-Null; [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json), (New-Object Text.UTF8Encoding $false)) }
function Get-State { if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } else { [pscustomobject]@{ done = @(); vaultRoot = $null; windowsTarget = $null } } }
function Done($state, [string]$phase) { $state.done = @($state.done) + $phase; Save-State $state }
# Runs one of the kit's scripts; its output goes to the screen and only its exit code is returned.
function Invoke-Script([string]$path, [string[]]$arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $path @arguments | Out-Host
    return $LASTEXITCODE
}
function Invoke-Wsl([string]$script, [switch]$AllowFailure) {
    $output = ($script -replace "`r", '') | & wsl.exe -d $distro -u root --exec sh -c 'sed -e ''1s/^\xEF\xBB\xBF//'' -e ''s/\r$//'' | sh' # strip the CR/BOM PowerShell adds to piped text
    if ($LASTEXITCODE -ne 0 -and !$AllowFailure) { Fail "A command in the WSL distro failed (exit $LASTEXITCODE)." }
    return $output
}

$state = Get-State
Write-Host 'SourceVault deployment from this drive (development build: synthetic data only)' -ForegroundColor White

# ---- importing packages later (needs a finished deployment) -------------------------------------------------------
if ($ImportPackages) {
    Step 'Import encrypted packages from transfer\'
    $import = Join-Path $installRoot 'client\SourceVault.Import.exe'
    if (!(Test-Path -LiteralPath $import)) { Fail 'SourceVault is not installed on this PC yet; run Deploy-SourceVault.ps1 first.' }
    $packages = @(Get-ChildItem -LiteralPath (Join-Path $kit 'transfer') -Filter '*.svpkg' -File -ErrorAction SilentlyContinue)
    if (!$packages) { Warn "no .svpkg files in $(Join-Path $kit 'transfer')"; return }
    foreach ($p in $packages) {
        $project = [IO.Path]::GetFileNameWithoutExtension($p.Name)
        Write-Host "   importing $($p.Name) as project `"$project`""
        & $import --package $p.FullName --project $project | ForEach-Object { Write-Host "      $_" }
        if ($LASTEXITCODE -eq 0) { Ok "$($p.Name) imported" } else { Warn "$($p.Name) was not imported (see above)" }
    }
    return
}

# ---- 1. checks ----------------------------------------------------------------------------------------------------
Step '1. Checks'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $kit 'Test-DeploymentKit.ps1') -Kit $kit
if ($LASTEXITCODE -ne 0) { Fail 'The drive does not match its manifest. Do not deploy from it.' }
$os = Get-CimInstance Win32_OperatingSystem
if ($os.Caption -notmatch 'Pro|Enterprise|Education') { Fail "Windows edition '$($os.Caption)' cannot run Windows containers; Pro, Enterprise or Education is needed." }
Ok $os.Caption
$tpm = Get-Tpm
if (!$tpm.TpmPresent -or !$tpm.TpmReady) { Fail 'The TPM is missing or not ready (tpm.msc). SourceVault keeps its keys only in the TPM.' }
Ok 'TPM present and ready'
$cs = Get-CimInstance Win32_ComputerSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
if (!$cs.HypervisorPresent -and !$cpu.VirtualizationFirmwareEnabled) { Fail 'Hardware virtualization is off. Turn it on in the firmware (BIOS/UEFI) settings, then run this again.' }
Ok 'virtualization available'
$free = (Get-PSDrive -Name ($env:SystemDrive.TrimEnd(':'))).Free
if ($free -lt 25GB) { Fail ("Only {0:N0} GB free on {1}; about 25 GB is needed (images, WSL, vault)." -f ($free / 1GB), $env:SystemDrive) }
Ok ("{0:N0} GB free on {1}" -f ($free / 1GB), $env:SystemDrive)
if ($VaultRoot) { $state.vaultRoot = $VaultRoot }
if (!$state.vaultRoot) {
    $default = Join-Path $env:ProgramData 'SourceVault\vault'
    $answer = Read-Host "   Vault folder (Enter for $default, or e.g. K:\SourceVault\vault on another fixed drive)"
    $state.vaultRoot = if ($answer.Trim()) { $answer.Trim() } else { $default }
}
$defaultVault = Join-Path $env:ProgramData 'SourceVault\vault'
if ($state.vaultRoot -ne $defaultVault -and !(Get-Service -Name SourceVault -ErrorAction SilentlyContinue)) {
    # Checked now (fixed local drive, allowed path), not only when setup runs in phase 6.
    if ((Invoke-Script (Join-Path $kit 'sourcevault\Setup-SourceVault.ps1') @('-CheckOnly', '-VaultRoot', $state.vaultRoot)) -ne 0) { $state.vaultRoot = $null; Save-State $state; Fail 'That vault location is not usable (see above). Run again and choose another.' }
}
Ok "vault folder: $($state.vaultRoot)"
Save-State $state

# ---- 2. Windows features --------------------------------------------------------------------------------------------
if ($state.done -notcontains 'features') {
    Step '2. Windows features'
    $restart = $false
    foreach ($feature in 'Containers', 'Microsoft-Hyper-V-All', 'VirtualMachinePlatform', 'Microsoft-Windows-Subsystem-Linux') {
        $info = Get-WindowsOptionalFeature -Online -FeatureName $feature
        if ($info.State -eq 'Enabled') { Ok "$feature already on"; continue }
        $result = Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart
        if ($result.RestartNeeded) { $restart = $true }
        Ok "$feature turned on"
    }
    Done $state 'features'
    if ($restart) { Write-Host "`nRestart this PC now, then run Deploy-SourceVault.ps1 again from the drive (elevated)." -ForegroundColor Yellow; exit 0 }
}

# ---- 3. Docker's Windows engine -------------------------------------------------------------------------------------
if ($state.done -notcontains 'docker') {
    Step "3. Docker's Windows engine"
    if (!(Get-Service -Name docker -ErrorAction SilentlyContinue)) {
        $work = Join-Path $stateDir 'docker-windows'
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
        New-Item -ItemType Directory -Path $work | Out-Null
        Expand-Archive -LiteralPath (KitFile 'docker-windows') -DestinationPath $work
        Push-Location $work # Microsoft's installer writes its progress file in the current folder
        try {
            $code = Invoke-Script (KitFile 'install-docker-ce') @('-DockerPath', (Join-Path $work 'docker\docker.exe'), '-DockerDPath', (Join-Path $work 'docker\dockerd.exe'), '-NoRestart')
            if ($code -ne 0 -or (Test-Path -LiteralPath (Join-Path $work 'Install-ContainerHost.err'))) { Fail "Microsoft's install-docker-ce.ps1 did not finish (see above)." }
        } finally { Pop-Location }
        Ok 'installed the docker service from the drive'
    } else { Ok 'docker service already installed' }
    if (!(Get-LocalGroup -Name docker -ErrorAction SilentlyContinue)) { New-LocalGroup -Name docker -Description 'May use Docker (SourceVault build service only)' | Out-Null; Ok 'created local group docker' }
    $daemon = Join-Path $env:ProgramData 'docker\config\daemon.json'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $daemon) | Out-Null
    [IO.File]::WriteAllText($daemon, '{"hosts": ["npipe://"], "group": "docker"}', [Text.Encoding]::ASCII)
    Restart-Service docker
    Start-Sleep -Seconds 5
    $os = (& $dockerExe version --format '{{.Server.Os}}' 2>$null) -join ''
    if ($os -ne 'windows') { Fail 'The Windows Docker engine did not come up (docker version).' }
    Ok 'Windows engine running on \\.\pipe\docker_engine; only the docker group (and administrators) may use it'
    Done $state 'docker'
}

# ---- 4. WSL, Ubuntu and Docker's Linux engine ------------------------------------------------------------------------
if ($state.done -notcontains 'wsl') {
    Step '4. WSL, Ubuntu and the Linux engine'
    & wsl.exe --version *> $null
    if ($LASTEXITCODE -ne 0) {
        $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$(KitFile 'wsl')`"", '/qn', '/norestart') -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { Fail "The WSL installer failed (msiexec exit $($p.ExitCode))." }
        Ok 'installed WSL from the drive'
    } else { Ok 'WSL already installed' }
    $known = (& wsl.exe -l -q) -replace "`0", '' | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() }
    if ($known -notcontains $distro) {
        New-Item -ItemType Directory -Force -Path $distroDir | Out-Null
        & wsl.exe --import $distro $distroDir (KitFile 'ubuntu') --version 2
        if ($LASTEXITCODE -ne 0) { Fail "wsl --import failed (exit $LASTEXITCODE)." }
        Ok "imported Ubuntu 24.04 as '$distro' for $env:USERNAME"
    } else { Ok "'$distro' already exists" }
    # Docker's static Linux binaries; its own containerd; no bridge network and no iptables (builds use no network).
    $tgz = (& wsl.exe -d $distro -u root --exec wslpath -u (KitFile 'docker-linux')) -join ''
    Invoke-Wsl @"
set -eu
printf '[boot]\nsystemd=true\n[user]\ndefault=root\n' > /etc/wsl.conf
tar -xzf '$tgz' -C /usr/local/bin --strip-components=1
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
ExecReload=/bin/kill -s HUP `$MAINPID
Restart=always
LimitNOFILE=infinity
Delegate=yes
KillMode=process
[Install]
WantedBy=multi-user.target
UNIT
mkdir -p /etc/systemd/system/multi-user.target.wants
ln -sf /etc/systemd/system/docker.service /etc/systemd/system/multi-user.target.wants/docker.service
"@ | Out-Null
    & wsl.exe --terminate $distro | Out-Null
    Start-Sleep -Seconds 3
    $ready = $false
    foreach ($i in 1..30) { $v = Invoke-Wsl 'docker version --format "{{.Server.Version}}" 2>/dev/null' -AllowFailure; if ($LASTEXITCODE -eq 0 -and "$v".Trim()) { $ready = $true; break }; Start-Sleep -Seconds 2 }
    if (!$ready) { Fail "Docker did not start inside '$distro' (systemctl status docker in the distro)." }
    Ok "Docker's Linux engine running in '$distro' (no network, no iptables)"
    # WSL stops an idle distro; this sign-in task keeps it running so the Linux build target stays reachable.
    # conhost --headless: no console window at sign-in.
    $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless wsl.exe -d $distro -u root --exec sleep infinity"
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -Hidden
    Register-ScheduledTask -TaskName 'SourceVault WSL keep-alive' -Action $action -Trigger $trigger -Settings $settings -User "$env:USERDOMAIN\$env:USERNAME" -RunLevel Limited -Force | Out-Null
    Start-ScheduledTask -TaskName 'SourceVault WSL keep-alive'
    Ok "sign-in task 'SourceVault WSL keep-alive' keeps '$distro' running while $env:USERNAME is signed in"
    Done $state 'wsl'
}

# ---- 5. build containers ------------------------------------------------------------------------------------------
if ($state.done -notcontains 'builds') {
    Step '5. Build containers (from the drive''s images)'
    $code = Invoke-Script (Join-Path $kit 'sourcevault\New-WindowsBuildContainer.ps1') @('-ImageArchive', (KitImage 'sdk-windows'), '-Replace')
    $state.windowsTarget = ($code -eq 0)
    if ($state.windowsTarget) { Ok 'Windows build container ready' } else { Warn 'the Windows build container is not usable (see above, especially the copy check); the Windows target is left out of build.json' }
    $code = Invoke-Script (Join-Path $kit 'sourcevault\New-BuildContainer.ps1') @('-Distro', $distro, '-ImageArchive', (KitImage 'sdk-linux'), '-Replace')
    if ($code -ne 0) { Fail 'The Linux build container could not be created (see above).' }
    Ok 'Linux build container ready'
    Done $state 'builds'
}

# ---- 6. SourceVault -----------------------------------------------------------------------------------------------
if ($state.done -notcontains 'vault') {
    Step '6. SourceVault'
    $setup = Join-Path $kit 'sourcevault\Setup-SourceVault.ps1'
    $installed = [bool](Get-Service -Name SourceVault -ErrorAction SilentlyContinue)
    $setupArgs = if ($installed) { @('-Update', '-WithBuilds') } elseif ($state.vaultRoot -ne (Join-Path $env:ProgramData 'SourceVault\vault')) { @('-VaultRoot', $state.vaultRoot, '-WithBuilds') } else { @('-WithBuilds') }
    if ((Invoke-Script $setup $setupArgs) -ne 0) { Fail 'SourceVault setup failed (see above).' }
    if ((Invoke-Script (Join-Path $kit 'sourcevault\Grant-BuildDockerAccess.ps1') @('-Group', 'docker')) -ne 0) { Fail 'Could not give the build service access to the Windows engine.' }
    # TLS for the Linux engine, then its daemon.json with the TLS listener (still no bridge, no iptables).
    $before = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq 'CN=SourceVaultBuild' | ForEach-Object Thumbprint)
    if ((Invoke-Script (Join-Path $kit 'sourcevault\New-WslDockerTls.ps1') @('-Distro', $distro)) -ne 0) { Fail 'Could not set up TLS for the Linux engine.' }
    $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq 'CN=SourceVaultBuild' -and $before -notcontains $_.Thumbprint } | Sort-Object NotBefore -Descending | Select-Object -First 1
    if (!$cert) { Fail 'The TLS client certificate was not found in LocalMachine\My.' }
    Invoke-Wsl @'
set -eu
cat > /etc/docker/daemon.json <<'JSON'
{"bridge": "none", "iptables": false, "ip6tables": false,
 "tls": true, "tlsverify": true,
 "tlscacert": "/etc/docker/sourcevault-tls/ca.pem", "tlscert": "/etc/docker/sourcevault-tls/server-cert.pem", "tlskey": "/etc/docker/sourcevault-tls/server-key.pem",
 "hosts": ["unix:///var/run/docker.sock", "tcp://127.0.0.1:2376"]}
JSON
systemctl restart docker
'@ | Out-Null
    Ok 'Linux engine listening with TLS on 127.0.0.1:2376 (client certificate required)'
    $artifactRoot = Join-Path ([IO.Path]::GetPathRoot($state.vaultRoot)) 'SourceVault\artifacts'
    $ca = Join-Path $env:ProgramData 'SourceVault\config\wsl-docker-ca.pem'
    $targets = @()
    if ($state.windowsTarget) {
        $targets += '    {"name": "windows", "engine": "npipe://./pipe/docker_engine", "container": "sourcevault-build-windows", "platform": "windows", "sourcePath": "C:\\sourcevault\\src", "command": ["cmd.exe", "/d", "/c", "build.cmd"], "artifactPath": "C:\\sourcevault\\out", "timeoutSeconds": 900, "maxArtifactMegabytes": 512, "allowNetwork": false}'
    }
    $targets += '    {"name": "linux", "engine": "tcp://127.0.0.1:2376", "tls": {"caFile": ' + ($ca | ConvertTo-Json) + ', "clientCertificateThumbprint": "' + $cert.Thumbprint + '"}, "container": "sourcevault-build-linux", "platform": "linux", "sourcePath": "/sourcevault/src", "command": ["/bin/sh", "-c", "sed -i ''s/\\r$//'' build.sh && exec /bin/sh build.sh"], "artifactPath": "/sourcevault/out", "timeoutSeconds": 900, "maxArtifactMegabytes": 512, "allowNetwork": false}'
    $buildJson = "{`n  `"version`": 1,`n  `"artifactRoot`": " + ($artifactRoot | ConvertTo-Json) + ",`n  `"targets`": [`n" + ($targets -join ",`n") + "`n  ]`n}`n"
    [IO.File]::WriteAllText((Join-Path $env:ProgramData 'SourceVault\config\build.json'), $buildJson, (New-Object Text.UTF8Encoding $false))
    Ok "build.json written ($(if ($state.windowsTarget) { 'windows and linux' } else { 'linux only' }); output in $artifactRoot)"
    if ((Invoke-Script $setup @('-Update', '-WithBuilds')) -ne 0) { Fail 'SourceVault setup could not apply the build configuration (see above).' }
    Invoke-Script $setup @('-Status') | Out-Null
    Done $state 'vault'
    Write-Host "`nSign out of Windows and back in (setup added you to 'SourceVault Operators'), then run Deploy-SourceVault.ps1 again for the last step." -ForegroundColor Yellow
    exit 0
}

# ---- 7. transfer: this PC's recipient file ------------------------------------------------------------------------
if ($state.done -notcontains 'transfer') {
    Step '7. Recipient file for encrypted projects'
    $computer = ($env:COMPUTERNAME -replace '[^A-Za-z0-9._-]', '')
    $out = Join-Path $kit "transfer\recipient-$computer.json"
    if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Force }
    & (Join-Path $installRoot 'client\SourceVault.Import.exe') --export-recipient $out | ForEach-Object { Write-Host "   $_" }
    if ($LASTEXITCODE -ne 0) { Fail 'Could not export the recipient file. If it says access denied: sign out and in again, then rerun.' }
    Done $state 'transfer'
}

Write-Host "`nDeployment complete." -ForegroundColor Green
Write-Host '  Next, for each project (two short legs, any channel is fine for both files):'
Write-Host "  1. Take transfer\recipient-$($env:COMPUTERNAME).json to the computer where the code lives; there, SourceVault Pack"
Write-Host '     encrypts the project folder to it (check that the fingerprint matches the one shown above).'
Write-Host '  2. Put the .svpkg file in this drive''s transfer\ folder, then on this PC run:'
Write-Host '       powershell -ExecutionPolicy Bypass -File .\Deploy-SourceVault.ps1 -ImportPackages'
Write-Host '  The source computer must be registered here once: Setup-SourceVault.ps1 -RegisterSender <its sender file>.'
