#Requires -RunAsAdministrator
# Sets up TLS client-certificate authentication so the SourceVault build service can reach a Docker engine running
# inside a WSL distro on this PC (docs/files-history-builds-design.md, section 4, decision D4).
#
#   New-WslDockerTls.ps1 -Distro Ubuntu-24.04 [-Port 2376] [-Days 825]
#
# What it does:
#   1. In the distro (as root, with openssl): creates a private CA, a server certificate for 127.0.0.1/localhost and a
#      client certificate, in /etc/docker/sourcevault-tls (keys readable by root only).
#   2. Imports the client certificate into LocalMachine\My (private key NOT exportable) and gives only
#      NT SERVICE\SourceVaultBuild read access to its key. Copies the CA certificate to
#      %ProgramData%\SourceVault\config\wsl-docker-ca.pem.
#   3. Prints the Docker settings to apply inside the distro and the "tls" block for build.json.
# It does not change Docker's configuration or restart anything: you apply the printed settings yourself.
#
# BE AWARE: the engine listens on 127.0.0.1 only and accepts only clients holding the client certificate. WSL distros
# run under your Windows account and stop when idle or when you sign out: Linux builds work only while the distro and
# its dockerd are running. LAB SCRIPT: not run during development; verify on the target machine.
param([Parameter(Mandatory)][string]$Distro, [int]$Port = 2376, [int]$Days = 825)
$ErrorActionPreference = 'Stop'
$account = 'NT SERVICE\SourceVaultBuild'
$configDir = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SourceVault\config'
if (!(Get-Service -Name 'SourceVaultBuild' -ErrorAction SilentlyContinue)) { throw 'The build service is not installed. Run Setup-SourceVault.ps1 -Update -WithBuilds first.' }
if (!(Test-Path -LiteralPath $configDir)) { throw "$configDir is missing. Run Setup-SourceVault.ps1 -Update -WithBuilds first." }
if ($Distro -notmatch '^[A-Za-z0-9._-]{1,64}$') { throw 'Invalid distro name.' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Port must be 1024-65535.' }

function Invoke-Wsl([string]$script) {
    # The script goes in on stdin (LF line endings) so nothing is interpolated into a command line.
    $output = ($script -replace "`r", '') | & wsl.exe -d $Distro -u root -- sh -s
    if ($LASTEXITCODE -ne 0) { throw "The command in WSL distro '$Distro' failed (exit $LASTEXITCODE)." }
    return $output
}

& wsl.exe -d $Distro -u root -- true
if ($LASTEXITCODE -ne 0) { throw "WSL distro '$Distro' could not be started (see 'wsl -l -v')." }
Invoke-Wsl 'command -v openssl >/dev/null || { echo "openssl is not installed in the distro (apt install openssl)" >&2; exit 3; }' | Out-Null

$password = -join ((1..32) | ForEach-Object { [char](Get-Random -Minimum 97 -Maximum 123) })
Write-Output "Creating certificates in /etc/docker/sourcevault-tls inside '$Distro'..."
Invoke-Wsl @"
set -eu
umask 077
d=/etc/docker/sourcevault-tls
mkdir -p "`$d" && cd "`$d"
openssl genrsa -out ca-key.pem 3072 2>/dev/null
openssl req -x509 -new -key ca-key.pem -sha256 -days $Days -subj "/CN=SourceVault WSL Docker CA" -out ca.pem
openssl genrsa -out server-key.pem 3072 2>/dev/null
openssl req -new -key server-key.pem -subj "/CN=localhost" -out server.csr
printf 'subjectAltName=IP:127.0.0.1,DNS:localhost\nextendedKeyUsage=serverAuth\n' > server.ext
openssl x509 -req -in server.csr -CA ca.pem -CAkey ca-key.pem -CAcreateserial -days $Days -sha256 -extfile server.ext -out server-cert.pem 2>/dev/null
openssl genrsa -out client-key.pem 3072 2>/dev/null
openssl req -new -key client-key.pem -subj "/CN=SourceVaultBuild" -out client.csr
printf 'extendedKeyUsage=clientAuth\n' > client.ext
openssl x509 -req -in client.csr -CA ca.pem -CAkey ca-key.pem -CAcreateserial -days $Days -sha256 -extfile client.ext -out client-cert.pem 2>/dev/null
openssl pkcs12 -export -inkey client-key.pem -in client-cert.pem -passout pass:$password -out /root/sourcevault-client.pfx
rm -f server.csr client.csr server.ext client.ext
chmod 644 ca.pem server-cert.pem client-cert.pem
"@ | Out-Null

$caText = (Invoke-Wsl 'cat /etc/docker/sourcevault-tls/ca.pem') -join "`n"
$caFile = Join-Path $configDir 'wsl-docker-ca.pem'
[IO.File]::WriteAllText($caFile, $caText + "`n", (New-Object Text.UTF8Encoding $false))
$pfxBase64 = (Invoke-Wsl 'base64 -w0 /root/sourcevault-client.pfx; rm -f /root/sourcevault-client.pfx') -join ''
$pfxPath = Join-Path $env:TEMP ("sv-client-" + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($pfxBase64))
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    $cert = Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation Cert:\LocalMachine\My -Password $secure # not exportable
} finally { if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force } }

# Give only the build service read access to the client key (CNG or legacy CSP storage).
$keyFile = $null
$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
if ($rsa -is [Security.Cryptography.RSACng]) {
    $keyFile = Join-Path $env:ProgramData ("Microsoft\Crypto\Keys\" + $rsa.Key.UniqueName)
} elseif ($cert.PrivateKey.CspKeyContainerInfo) {
    $keyFile = Join-Path $env:ProgramData ("Microsoft\Crypto\RSA\MachineKeys\" + $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName)
}
if (!$keyFile -or !(Test-Path -LiteralPath $keyFile)) { throw 'Imported the certificate but could not find its private key file to grant the build service access.' }
& icacls.exe $keyFile /grant "${account}:R" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed (exit $LASTEXITCODE)." }

Write-Output ''
Write-Output "Done. Client certificate $($cert.Thumbprint) is in LocalMachine\My; only $account (and administrators) can use its key."
Write-Output "CA certificate: $caFile"
Write-Output ''
Write-Output "1) Inside '$Distro', make dockerd listen with TLS on 127.0.0.1:$Port. For example /etc/docker/daemon.json:"
Write-Output @"
   {
     "tls": true, "tlsverify": true,
     "tlscacert": "/etc/docker/sourcevault-tls/ca.pem",
     "tlscert": "/etc/docker/sourcevault-tls/server-cert.pem",
     "tlskey": "/etc/docker/sourcevault-tls/server-key.pem",
     "hosts": ["unix:///var/run/docker.sock", "tcp://127.0.0.1:$Port"]
   }
"@
Write-Output "   (with systemd, also remove '-H fd://' from the docker service's ExecStart, then restart docker.)"
Write-Output ''
Write-Output '2) In build.json, the Linux target:'
Write-Output @"
   "engine": "tcp://127.0.0.1:$Port",
   "tls": { "caFile": "$($caFile -replace '\\', '\\')", "clientCertificateThumbprint": "$($cert.Thumbprint)" },
"@
Write-Output '3) Run Setup-SourceVault.ps1 -Update -WithBuilds (or -Status) to check the configuration.'
