# Builds the offline deployment drive (docs/deployment-kit-design.md). Run on a CONNECTED staging PC, as your normal
# user, from this repository. Needs no Docker. About 4.5 GB is downloaded on the first run; later runs keep files
# that already match.
#
#   powershell -ExecutionPolicy Bypass -File scripts\Build-DeploymentKit.ps1 -Destination E:\SourceVaultKit
#   ... -RecordNewHashes     first run only: record SHA-256 for sources Docker publishes no checksum for (review them)
#   ... -SkipImages          everything except the two build images (e.g. to test the rest first)
#
# Every download is checked against installer\kit-sources.json (size and SHA-256); images are checked digest by
# digest by Save-RegistryImage.ps1. The kit ends with kit-manifest.sha256 over every file, which the target checks.
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$DotnetPath,
    [switch]$RecordNewHashes,
    [switch]$SkipImages,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourcesPath = Join-Path $repoRoot 'installer\kit-sources.json'
$sources = Get-Content -LiteralPath $sourcesPath -Raw | ConvertFrom-Json
if ($sources.version -ne 1) { throw 'Unsupported kit-sources.json version.' }
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
function Ok([string]$t) { Write-Host "   OK   $t" -ForegroundColor Green }
function Step([string]$t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Sha([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

# 1. The SourceVault setup package (and SourceVault Pack inside it).
Step 'SourceVault setup package'
if (!$SkipInstaller) {
    $installerArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'scripts\Build-Installer.ps1'))
    if ($DotnetPath) { $installerArgs += @('-DotnetPath', $DotnetPath) }
    & powershell.exe @installerArgs | Select-Object -Last 3 | ForEach-Object { Write-Host "   $_" }
    if ($LASTEXITCODE -ne 0) { throw 'Build-Installer.ps1 failed.' }
}
$package = Join-Path $repoRoot 'artifacts\installer\SourceVault-Setup'
if (!(Test-Path -LiteralPath (Join-Path $package 'package-manifest.sha256'))) { throw 'No setup package; run without -SkipInstaller.' }
$target = Join-Path $Destination 'sourcevault'
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Copy-Item -LiteralPath $package -Destination $target -Recurse
foreach ($script in 'Deploy-SourceVault.ps1', 'Test-DeploymentKit.ps1') { Copy-Item -LiteralPath (Join-Path $repoRoot "installer\$script") -Destination $Destination -Force }
Copy-Item -LiteralPath $sourcesPath -Destination $Destination -Force
New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'transfer') | Out-Null
Ok "copied to $target"

# 2. Pinned downloads.
Step 'Pinned downloads'
$recorded = $false
foreach ($file in $sources.files) {
    $path = Join-Path $Destination ($file.path -replace '/', '\')
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    $good = (Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -eq $file.size -and ($file.sha256 -and (Sha $path) -eq $file.sha256)
    if (!$good) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        Write-Host ("   downloading {0} ({1:N0} bytes) from {2}" -f $file.name, $file.size, $file.url)
        Invoke-WebRequest -UseBasicParsing -Uri $file.url -OutFile "$path.download" -TimeoutSec 3600
        $size = (Get-Item -LiteralPath "$path.download").Length
        if ($size -ne $file.size) { Remove-Item -LiteralPath "$path.download" -Force; throw "$($file.name): got $size bytes, expected $($file.size). Not using it." }
        $hash = Sha "$path.download"
        if (!$file.sha256) {
            if (!$RecordNewHashes) { Remove-Item -LiteralPath "$path.download" -Force; throw "$($file.name) has no pinned SHA-256 yet. Rerun with -RecordNewHashes to record it (trust on first use), then review and commit installer\kit-sources.json." }
            $file.sha256 = $hash; $recorded = $true
            Write-Host "   NOTE recorded $($file.name) SHA-256 $hash (trust on first use)" -ForegroundColor Yellow
        } elseif ($hash -ne $file.sha256) { Remove-Item -LiteralPath "$path.download" -Force; throw "$($file.name): SHA-256 $hash does not match the pinned $($file.sha256). Not using it." }
        Move-Item -LiteralPath "$path.download" -Destination $path -Force
        Ok "$($file.name) downloaded and verified"
    } else { Ok "$($file.name) already present and verified" }
}
if ($recorded) {
    $json = $sources | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($sourcesPath, $json, (New-Object Text.UTF8Encoding $false))
    Copy-Item -LiteralPath $sourcesPath -Destination $Destination -Force
    Write-Host '   NOTE new hashes were written to installer\kit-sources.json: review and commit them.' -ForegroundColor Yellow
}

# 3. Build images, straight from the registry (no Docker needed).
Step 'Build images'
if ($SkipImages) { Write-Host '   NOTE skipped (-SkipImages); the kit cannot create build containers without them' -ForegroundColor Yellow }
else {
    foreach ($image in $sources.images) {
        $out = Join-Path $Destination ($image.path -replace '/', '\')
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out) | Out-Null
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Save-RegistryImage.ps1') -Registry $image.registry -Repository $image.repository `
            -Manifest $image.manifest -ImageId $image.imageId -Tag $image.tag -OutFile $out | Select-Object -Last 1 | ForEach-Object { Write-Host "   $_" }
        if ($LASTEXITCODE -ne 0) { throw "Saving image $($image.name) failed." }
        Ok "$($image.name) saved"
    }
}

# 4. The kit manifest: SHA-256 of every file, checked on the target before anything is installed.
Step 'Kit manifest'
$files = Get-ChildItem -LiteralPath $Destination -Recurse -File | Where-Object { $_.Name -ne 'kit-manifest.sha256' -and $_.FullName -notlike (Join-Path $Destination 'transfer\*') } | Sort-Object FullName
$lines = foreach ($f in $files) { '{0}  {1}' -f (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash, $f.FullName.Substring($Destination.Length + 1) }
[IO.File]::WriteAllLines((Join-Path $Destination 'kit-manifest.sha256'), [string[]]$lines, (New-Object Text.UTF8Encoding $false))
$bytes = ($files | Measure-Object Length -Sum).Sum
Ok ("{0} files, {1:N1} GB, listed in kit-manifest.sha256" -f $files.Count, ($bytes / 1GB))
Write-Host "`nKit ready: $Destination. On the target PC, run Deploy-SourceVault.ps1 from it (elevated)." -ForegroundColor Green
Write-Host 'Keep the drive in your custody: the manifest catches damage and changes, but it is not signed.'
