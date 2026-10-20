#Requires -RunAsAdministrator
# Lab helper (2026-10-20; owner-run, see README.md) for a PC that already has WSL: sets up Docker's Linux engine in its own WSL distro, the way phase 4 of
# Deploy-SourceVault.ps1 does from the offline drive. Your own distros are not touched.
#   powershell -ExecutionPolicy Bypass -File .\Install-WslBuildEngine.ps1
# Needs, in -Folder: ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz and docker-29.9.1.tgz (installer\kit-sources.json).
# The distro is registered for the account running this (WSL distros are per user) and stops when that account signs
# out; a sign-in task keeps it running while you are signed in, so the Linux build target stays reachable.
param(
    [string]$Distro = 'SourceVault-Ubuntu',
    [string]$Folder = 'C:\WslInstall'
)
$ErrorActionPreference = 'Stop'
$rootfs = Join-Path $Folder 'ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz'
$tgz = Join-Path $Folder 'docker-29.9.1.tgz'
$pinned = @{
    $rootfs = '8251E27FFFF381A4AF5F41DCB94D867DE3E0D9774A9241908AB34555D99315EA' # Ubuntu's SHA256SUMS, release 20240423
    $tgz    = 'C158ECC55ADB3AFB01BBD3539ED3B997D20C26DBDC418080506CC1BF5243FF57' # recorded at download 2026-10-20 (Docker publishes none)
}
$distroDir = Join-Path $env:ProgramData 'SourceVault-WSL\Ubuntu'
function Ok([string]$m) { Write-Host "   OK   $m" -ForegroundColor Green }
function Invoke-Wsl([string]$script, [switch]$AllowFailure) {
    $output = ($script -replace "`r", '') | & wsl.exe -d $Distro -u root --exec sh -c 'sed -e ''1s/^\xEF\xBB\xBF//'' -e ''s/\r$//'' | sh' # strip the CR/BOM PowerShell adds to piped text
    if ($LASTEXITCODE -ne 0 -and !$AllowFailure) { throw "A command in the WSL distro failed (exit $LASTEXITCODE)." }
    return $output
}

& wsl.exe --version *> $null
if ($LASTEXITCODE -ne 0) { throw 'WSL is not installed (wsl --version failed).' }
foreach ($file in $pinned.Keys) {
    if (!(Test-Path -LiteralPath $file)) { throw "Missing $file" }
    if ((Get-FileHash -LiteralPath $file).Hash -ne $pinned[$file]) { throw "$file does not match its recorded SHA-256; not using it." }
}
Ok 'Ubuntu image and Docker engine match their recorded hashes'

$known = (& wsl.exe -l -q) -replace "`0", '' | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() }
if ($known -notcontains $Distro) {
    New-Item -ItemType Directory -Force -Path $distroDir | Out-Null
    & wsl.exe --import $Distro $distroDir $rootfs --version 2
    if ($LASTEXITCODE -ne 0) { throw "wsl --import failed (exit $LASTEXITCODE)." }
    Ok "imported Ubuntu 24.04 as '$Distro' ($distroDir) for $env:USERNAME"
} else { Ok "'$Distro' already exists (kept)" }

# Docker's static Linux binaries; its own containerd; no bridge network and no iptables (builds use no network).
$tgzLinux = (& wsl.exe -d $Distro -u root --exec wslpath -u $tgz) -join ''
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
rm -f "/etc/systemd/system/multi-user.target.wants/docker.service$(printf '\r')" # left by the first run (stray CR)
ln -sf /etc/systemd/system/docker.service /etc/systemd/system/multi-user.target.wants/docker.service
'@
Invoke-Wsl ($install -replace '__TGZ__', $tgzLinux) | Out-Null
Ok 'Docker 29.9.1 unpacked into /usr/local/bin; systemd starts it with the distro'

& wsl.exe --terminate $Distro | Out-Null # restart the distro so systemd (from wsl.conf) runs
Start-Sleep -Seconds 3
$ready = $false
foreach ($i in 1..30) {
    $v = Invoke-Wsl 'docker version --format "{{.Server.Version}} {{.Server.Os}}" 2>/dev/null' -AllowFailure
    if ($LASTEXITCODE -eq 0 -and "$v".Trim()) { $ready = $true; break }
    Start-Sleep -Seconds 2
}
if (!$ready) { throw "Docker did not start inside '$Distro'. Look with: wsl -d $Distro -u root -- systemctl status docker" }
Ok "Docker's Linux engine running in '$Distro': $("$v".Trim()) (no bridge network, no iptables)"

# WSL stops an idle distro; this sign-in task keeps it running. conhost --headless: no console window.
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless wsl.exe -d $Distro -u root --exec sleep infinity"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -Hidden
Register-ScheduledTask -TaskName 'SourceVault WSL keep-alive' -Action $action -Trigger $trigger -Settings $settings -User "$env:USERDOMAIN\$env:USERNAME" -RunLevel Limited -Force | Out-Null
Start-ScheduledTask -TaskName 'SourceVault WSL keep-alive'
Ok "sign-in task 'SourceVault WSL keep-alive' keeps '$Distro' running while $env:USERNAME is signed in"
Write-Host ''
Write-Host "Next: & `"`$pkg\New-BuildContainer.ps1`" -Distro $Distro" -ForegroundColor White
