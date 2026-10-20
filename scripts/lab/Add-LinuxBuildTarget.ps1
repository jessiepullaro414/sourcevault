#Requires -RunAsAdministrator
# Lab helper (2026-10-20; owner-run, see README.md): connects the build service to the Linux engine in WSL, the way phase 6 of Deploy-SourceVault.ps1 does.
#   1. New-WslDockerTls.ps1: a private CA, server and client certificates; the client key goes to LocalMachine\My,
#      readable only by NT SERVICE\SourceVaultBuild (and administrators).
#   2. The engine listens with TLS on 127.0.0.1:2376 (client certificate required); still no bridge, no iptables.
#   3. build.json gets a "linux" target (any windows target is kept), then setup -Update -WithBuilds applies it.
# Run after Install-WslBuildEngine.ps1 and New-BuildContainer.ps1. Close SourceVault editor windows first.
#   powershell -ExecutionPolicy Bypass -File .\Add-LinuxBuildTarget.ps1
param(
    [string]$Distro = 'SourceVault-Ubuntu',
    [string]$Package = (Join-Path $PSScriptRoot '..\..\artifacts\installer\SourceVault-Setup'),
    [int]$Port = 2376
)
$ErrorActionPreference = 'Stop'
$configDir = Join-Path $env:ProgramData 'SourceVault\config'
$buildJson = Join-Path $configDir 'build.json'
function Ok([string]$m) { Write-Host "   OK   $m" -ForegroundColor Green }
function Invoke-Wsl([string]$script) {
    $output = ($script -replace "`r", '') | & wsl.exe -d $Distro -u root --exec sh -c 'sed -e ''1s/^\xEF\xBB\xBF//'' -e ''s/\r$//'' | sh' # strip the CR/BOM PowerShell adds to piped text
    if ($LASTEXITCODE -ne 0) { throw "A command in the WSL distro failed (exit $LASTEXITCODE)." }
    return $output
}
if (!(Test-Path -LiteralPath $buildJson)) { throw "$buildJson is missing: set up the build service first (Setup-SourceVault.ps1 -Update -WithBuilds)." }
$names = (Invoke-Wsl 'docker ps -a --format "{{.Names}}"') -join ' '
if ($names -notmatch '\bsourcevault-build-linux\b') { throw "No container 'sourcevault-build-linux' in '$Distro': run New-BuildContainer.ps1 -Distro $Distro first." }

# 1. Certificates.
$before = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq 'CN=SourceVaultBuild' | ForEach-Object Thumbprint)
& (Join-Path $Package 'New-WslDockerTls.ps1') -Distro $Distro -Port $Port | Out-Null
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq 'CN=SourceVaultBuild' -and $before -notcontains $_.Thumbprint } | Sort-Object NotBefore -Descending | Select-Object -First 1
if (!$cert) { throw 'The TLS client certificate was not found in LocalMachine\My.' }
Ok "client certificate $($cert.Thumbprint) in LocalMachine\My (key readable by the build service only)"

# 2. The engine's TLS listener.
$daemon = @'
set -eu
cat > /etc/docker/daemon.json <<'JSON'
{"bridge": "none", "iptables": false, "ip6tables": false,
 "tls": true, "tlsverify": true,
 "tlscacert": "/etc/docker/sourcevault-tls/ca.pem", "tlscert": "/etc/docker/sourcevault-tls/server-cert.pem", "tlskey": "/etc/docker/sourcevault-tls/server-key.pem",
 "hosts": ["unix:///var/run/docker.sock", "tcp://127.0.0.1:__PORT__"]}
JSON
systemctl restart docker
'@
Invoke-Wsl ($daemon -replace '__PORT__', $Port) | Out-Null
$reachable = $false
foreach ($i in 1..20) {
    $client = New-Object Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $Port); $reachable = $true; break } catch { Start-Sleep -Seconds 2 } finally { $client.Dispose() }
}
if (!$reachable) { throw "Nothing answers on 127.0.0.1:$Port from Windows. Check WSL localhost forwarding and: wsl -d $Distro -u root -- systemctl status docker" }
Ok "Linux engine listening with TLS on 127.0.0.1:$Port, reachable from Windows (client certificate required)"

# 3. build.json: replace or add the linux target, keep the others.
$config = Get-Content -LiteralPath $buildJson -Raw | ConvertFrom-Json
$linux = [ordered]@{
    name = 'linux'; engine = "tcp://127.0.0.1:$Port"
    tls = [ordered]@{ caFile = (Join-Path $configDir 'wsl-docker-ca.pem'); clientCertificateThumbprint = $cert.Thumbprint }
    container = 'sourcevault-build-linux'; platform = 'linux'; sourcePath = '/sourcevault/src'
    command = @('/bin/sh', '-c', 'sed -i ''s/\r$//'' build.sh && exec /bin/sh build.sh')
    artifactPath = '/sourcevault/out'; timeoutSeconds = 900; maxArtifactMegabytes = 512; allowNetwork = $false
}
$config.targets = @(@($config.targets | Where-Object { $_.name -ne 'linux' }) + (New-Object PSObject -Property $linux))
[IO.File]::WriteAllText($buildJson, ($config | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
Ok "build.json targets: $(($config.targets | ForEach-Object name) -join ', ')"

& (Join-Path $Package 'Setup-SourceVault.ps1') -Update -WithBuilds
if ($LASTEXITCODE -ne 0) { throw "Setup-SourceVault.ps1 -Update -WithBuilds failed (exit $LASTEXITCODE); fix the FAIL line above and run it again." }
& (Join-Path $Package 'Setup-SourceVault.ps1') -Status
