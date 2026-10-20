# Checks every file of the deployment drive against kit-manifest.sha256 (docs/deployment-kit-design.md), and that no
# unlisted file was added (transfer\ excepted). Read-only; no administrator rights needed. Exit 0 = intact.
#   powershell -ExecutionPolicy Bypass -File .\Test-DeploymentKit.ps1 [-Kit E:\SourceVaultKit]
param([string]$Kit = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$Kit = [IO.Path]::GetFullPath($Kit)
$manifest = Join-Path $Kit 'kit-manifest.sha256'
if (!(Test-Path -LiteralPath $manifest)) { Write-Host "   FAIL kit-manifest.sha256 is missing: this is not a complete deployment kit." -ForegroundColor Red; exit 1 }
$expected = @{}
foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$' -or $Matches[2] -match '(^|\\)\.\.(\\|$)' -or [IO.Path]::IsPathRooted($Matches[2])) { Write-Host "   FAIL malformed manifest line." -ForegroundColor Red; exit 1 }
    $expected[$Matches[2]] = $Matches[1].ToUpperInvariant()
}
$bad = 0; $checked = 0
foreach ($relative in $expected.Keys) {
    $path = Join-Path $Kit $relative
    $checked++
    if (!(Test-Path -LiteralPath $path)) { $bad++; Write-Host "   missing: $relative" -ForegroundColor Red; continue }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected[$relative]) { $bad++; Write-Host "   changed: $relative" -ForegroundColor Red }
}
$extra = Get-ChildItem -LiteralPath $Kit -Recurse -File | Where-Object { $_.Name -ne 'kit-manifest.sha256' -and $_.FullName -notlike (Join-Path $Kit 'transfer\*') } |
    ForEach-Object { $_.FullName.Substring($Kit.Length + 1) } | Where-Object { !$expected.ContainsKey($_) }
foreach ($e in $extra) { $bad++; Write-Host "   not in the manifest: $e" -ForegroundColor Red }
if ($bad) { Write-Host "   FAIL $bad problem(s) in $checked listed file(s). Do not deploy from this drive." -ForegroundColor Red; exit 1 }
Write-Host "   OK   all $checked kit files match kit-manifest.sha256" -ForegroundColor Green
exit 0
