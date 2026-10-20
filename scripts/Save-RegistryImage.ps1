# Saves one container image from a registry as a `docker load` archive, WITHOUT Docker (docs/deployment-kit-design.md).
# The manifest is fetched by its pinned digest and must hash to it; the configuration and every layer must hash to the
# digests the manifest names. The image ID after `docker load` is the configuration digest, which the deploy step checks.
#
#   Save-RegistryImage.ps1 -Registry mcr.microsoft.com -Repository dotnet/sdk -Manifest sha256:<64 hex> `
#       -ImageId sha256:<64 hex> -Tag 10.0.402 -OutFile E:\SourceVaultKit\images\sdk-linux.tar [-MetadataOnly]
#
# Resumable: verified blobs already in <OutFile>.parts\ are kept. -MetadataOnly checks the manifest and configuration
# and lists the layers without downloading them. Anonymous pulls only (Microsoft's registry needs no login).
param(
    [Parameter(Mandatory)][string]$Registry,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^sha256:[0-9a-f]{64}$')][string]$Manifest,
    [Parameter(Mandatory)][ValidatePattern('^sha256:[0-9a-f]{64}$')][string]$ImageId,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$OutFile,
    [switch]$MetadataOnly
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Windows PowerShell's progress bar slows large downloads badly
if ($Registry -notmatch '^[a-z0-9.-]+$' -or $Repository -notmatch '^[a-z0-9._/-]+$' -or $Tag -notmatch '^[A-Za-z0-9._-]+$') { throw 'Invalid registry, repository or tag.' }
$base = "https://$Registry/v2/$Repository"

function Get-Sha256([string]$path) { 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-Blob([string]$digest, [long]$size, [string]$path) {
    if ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -eq $size -and (Get-Sha256 $path) -eq $digest) { return 'kept' }
    $partial = "$path.download"
    Invoke-WebRequest -UseBasicParsing -Uri "$base/blobs/$digest" -OutFile $partial -TimeoutSec 3600
    $actual = Get-Sha256 $partial
    if ((Get-Item -LiteralPath $partial).Length -ne $size -or $actual -ne $digest) { Remove-Item -LiteralPath $partial -Force; throw "Blob $digest did not match its digest (got $actual); not using it." }
    Move-Item -LiteralPath $partial -Destination $path -Force
    return 'downloaded'
}

$parts = "$OutFile.parts"
New-Item -ItemType Directory -Force -Path $parts | Out-Null

# 1. The manifest, by digest: its bytes must hash to the pinned digest.
$accept = 'application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json'
$response = Invoke-WebRequest -UseBasicParsing -Uri "$base/manifests/$Manifest" -Headers @{ Accept = $accept } -TimeoutSec 120
$bytes = if ($response.Content -is [byte[]]) { $response.Content } else { [Text.Encoding]::UTF8.GetBytes($response.Content) }
$sha = [Security.Cryptography.SHA256]::Create()
$actual = 'sha256:' + ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
if ($actual -ne $Manifest) { throw "The manifest did not match its pinned digest (got $actual)." }
$doc = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
if ($doc.schemaVersion -ne 2 -or !$doc.config -or !$doc.layers) { throw 'Unsupported manifest (expected a single-platform schema 2 image manifest).' }
if ($doc.config.digest -ne $ImageId) { throw "The manifest's configuration is $($doc.config.digest), not the pinned image ID $ImageId." }

# 2. The configuration (its digest is the image ID).
$configName = $doc.config.digest.Substring(7) + '.json'
$state = Get-Blob $doc.config.digest ([long]$doc.config.size) (Join-Path $parts $configName)
Write-Output "configuration $($doc.config.digest): $state"

# 3. The layers.
$layerNames = @()
$total = 0L
foreach ($layer in $doc.layers) {
    if ($layer.urls) { throw "Layer $($layer.digest) is a foreign layer (urls); not supported." }
    if ($layer.mediaType -notmatch 'tar(\.gzip|\+gzip)?$') { throw "Unexpected layer media type $($layer.mediaType)." }
    $name = $layer.digest.Substring(7) + '.tar.gz'
    $layerNames += $name
    $total += [long]$layer.size
    if ($MetadataOnly) { Write-Output ("layer {0}: {1:N0} bytes (not downloaded)" -f $layer.digest, $layer.size); continue }
    $state = Get-Blob $layer.digest ([long]$layer.size) (Join-Path $parts $name)
    Write-Output ("layer {0}: {1:N0} bytes, {2}" -f $layer.digest, $layer.size, $state)
}
if ($MetadataOnly) { Write-Output ("{0} layers, {1:N0} bytes in all. Metadata verified; nothing archived." -f $layerNames.Count, $total); return }

# 4. The docker-save layout, then one tar (Windows' built-in tar.exe). docker load decompresses gzip layers itself.
$repoTag = "$Registry/$Repository`:$Tag"
$layersJson = ($layerNames | ForEach-Object { '"' + $_ + '"' }) -join ','
$manifestJson = '[{"Config":"' + $configName + '","RepoTags":["' + $repoTag + '"],"Layers":[' + $layersJson + ']}]'
[IO.File]::WriteAllText((Join-Path $parts 'manifest.json'), $manifestJson, (New-Object Text.UTF8Encoding $false))
Get-ChildItem -LiteralPath $parts -Filter '*.download' | Remove-Item -Force
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (!(Test-Path -LiteralPath $tar)) { throw 'Windows tar.exe is missing.' }
$tmp = "$OutFile.tmp"
if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
$members = @('manifest.json', $configName) + $layerNames
& $tar -cf $tmp -C $parts @members
if ($LASTEXITCODE -ne 0) { throw "tar.exe failed (exit $LASTEXITCODE)." }
Move-Item -LiteralPath $tmp -Destination $OutFile -Force
Remove-Item -LiteralPath $parts -Recurse -Force
Write-Output ("Saved $repoTag as $OutFile ({0:N0} bytes); image ID after docker load: $ImageId" -f (Get-Item -LiteralPath $OutFile).Length)
