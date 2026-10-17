#Requires -RunAsAdministrator
# LAB ONLY, synthetic vaults only. Fail-closed drill; every change is undone before the script exits.
#   1. Add a stray empty file to the vault root   -> normal start must be refused with 1066/11
#   2. Flip one bit in a project's live manifest  -> normal start must be refused with 1066/12
#      (original bytes are restored and verified by SHA-256)
#   3. With everything restored                   -> normal start must reach RUNNING again
$ErrorActionPreference = 'Stop'
$serviceName = 'SourceVault'
$vault = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SourceVault\vault'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (!$service) { throw 'Service not installed.' }
if ($service.Status -ne 'Stopped') { throw "Service is $($service.Status); stop it first." }
$project = Get-ChildItem -LiteralPath (Join-Path $vault 'projects') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$project) { throw 'No project in the vault. Run New-SyntheticProject.ps1 first.' }

function Invoke-Start {
    & sc.exe start $serviceName | Out-Null
    $deadline = (Get-Date).AddSeconds(60)
    do { Start-Sleep -Milliseconds 250; $service.Refresh() } while (@('Running', 'Stopped') -notcontains [string]$service.Status -and (Get-Date) -lt $deadline)
    $result = if ($service.Status -eq 'Running') { 'RUNNING' } else {
        $s = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"; "$($s.ExitCode)/$($s.ServiceSpecificExitCode)"
    }
    if ($service.Status -eq 'Running') {
        & sc.exe stop $serviceName | Out-Null
        $deadline = (Get-Date).AddSeconds(30)
        do { Start-Sleep -Milliseconds 250; $service.Refresh() } while ($service.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
    }
    $result
}
$failures = 0
function Report([string]$what, [string]$expected, [string]$actual) {
    $ok = $actual -eq $expected
    if (!$ok) { $script:failures++ }
    Write-Output ("{0}: {1} -> {2} (expected {3})" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $what, $actual, $expected)
}

# 1. Stray entry.
$stray = Join-Path $vault 'stray-drill.txt'
try { New-Item -ItemType File -Path $stray | Out-Null; Report 'stray file in vault root' '1066/11' (Invoke-Start) }
finally { Remove-Item -LiteralPath $stray -Force -ErrorAction SilentlyContinue }

# 2. One flipped bit in the project's live manifest.
$head = (Get-Content -LiteralPath (Join-Path $project.FullName 'HEAD') -Raw).Trim()
$manifest = Join-Path $project.FullName "$head.svr"
$original = [IO.File]::ReadAllBytes($manifest)
$originalHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
try {
    $tampered = [byte[]]$original.Clone(); $tampered[$tampered.Length - 1] = $tampered[$tampered.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($manifest, $tampered)
    Report 'one flipped bit in a project manifest' '1066/12' (Invoke-Start)
} finally {
    [IO.File]::WriteAllBytes($manifest, $original)
    if ((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash -ne $originalHash) { throw "RESTORE FAILED for $manifest. Stop and review." }
}

# 3. Restored state starts normally.
if (Test-Path -LiteralPath $stray) { throw 'Stray file still present.' }
Report 'restored vault' 'RUNNING' (Invoke-Start)
if ($failures) { exit 1 } else { Write-Output 'All drill steps behaved as expected; vault restored byte-for-byte.' }
