# SourceVault setup — installs, updates or checks a SourceVault vault on this computer.
# Run from an ELEVATED Windows PowerShell inside the unpacked setup package. See README.md in the package.
#
#   Setup-SourceVault.ps1 -CheckOnly                  pre-flight only (no admin needed, changes nothing)
#   Setup-SourceVault.ps1 [-Operators user1,user2]    new install (default operator: you)
#   Setup-SourceVault.ps1 -Update                     replace program files, keep key, vault and settings
#   Setup-SourceVault.ps1 -Status                     health check of an installed vault
#   Setup-SourceVault.ps1 -Update -WithBuilds         also install/refresh the build service (Docker builds; see README)
#
# DEVELOPMENT BUILD: use synthetic data only until an independent security review says otherwise.
param(
    [string[]]$Operators = @("$env:USERDOMAIN\$env:USERNAME"),
    [switch]$Update,
    [switch]$CheckOnly,
    [switch]$Status,
    [switch]$WithSyntheticDemo,
    [switch]$ManualStart,
    [switch]$WithBuilds
)
$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
$payload = Join-Path $package 'payload'
$installRoot = Join-Path $env:ProgramFiles 'SourceVault'
$serviceName = 'SourceVault'
$serviceAccount = 'NT SERVICE\SourceVault'
$groupName = 'SourceVault Operators'
$keyName = 'SourceVault.Lab.Service01'
$vaultParent = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SourceVault'
$vaultRoot = Join-Path $vaultParent 'vault'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SourceVault Editor.lnk'
$buildServiceName = 'SourceVaultBuild'
$buildAccount = 'NT SERVICE\SourceVaultBuild'
$configDir = Join-Path $vaultParent 'config'
$buildConfig = Join-Path $configDir 'build.json'
$exitReasons = @{
    10 = 'vault folder missing or its permissions are not locked down'
    11 = 'unexpected item in the vault folder, or the project index failed authentication'
    12 = 'a project failed authentication (possible tampering or damage)'
    13 = "the local group '$groupName' is missing"
    14 = 'the service pipe could not be created (name already taken?)'
    15 = 'the vault service account NT SERVICE\SourceVault does not exist (install the vault first)'
    16 = 'the build service is not running as NT SERVICE\SourceVaultBuild'
}

$script:reported = $false
# Any stop ends with one clear line and exit code 1, not a PowerShell error dump.
trap {
    if (!$script:reported) { Write-Host "   FAIL $($_.Exception.Message)" -ForegroundColor Red }
    Write-Host "`nSetup stopped. Fix the problem above and run it again; completed steps are safe to repeat with -Update." -ForegroundColor Red
    exit 1
}
function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "   OK   $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "   NOTE $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "   FAIL $text" -ForegroundColor Red; $script:reported = $true; throw $text }

# ---- pre-flight --------------------------------------------------------------------------------------------------
function Test-Package {
    $manifest = Join-Path $package 'package-manifest.sha256'
    if (!(Test-Path -LiteralPath $manifest)) { Fail 'package-manifest.sha256 is missing: this is not a complete SourceVault setup package.' }
    $bad = 0; $count = 0
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if (!$line) { continue }
        $hash, $relative = $line -split '  ', 2
        $path = Join-Path $package $relative
        $count++
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash) { $bad++; Write-Host "   changed or missing: $relative" -ForegroundColor Red }
    }
    if ($bad) { Fail "$bad of $count package files do not match the manifest. Get a fresh copy of the package." }
    Ok "package intact ($count files match package-manifest.sha256)"
}

function Test-Machine {
    if (![Environment]::Is64BitOperatingSystem) { Fail 'A 64-bit Windows is required.' }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 19041) { Fail "Windows 10 2004 / Windows 11 or newer is required (this is build $build)." }
    Ok "Windows build $build, 64-bit"
    $tpm = (& tpmtool getdeviceinformation 2>$null) -join "`n"
    if ($tpm -notmatch 'TPM Present:\s*True') { Fail 'No TPM found. SourceVault requires a TPM 2.0 (it may be disabled in the BIOS/UEFI settings).' }
    if ($tpm -notmatch 'TPM Version:\s*2\.0') { Fail 'TPM 2.0 is required.' }
    if ($tpm -notmatch 'Ready For Storage:\s*True') { Fail 'The TPM is not ready for storage. Initialize it in Windows Security > Device security first. Never clear a TPM that protects data.' }
    Ok 'TPM 2.0 present and ready for storage'
    $webView = $null
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
                     'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') {
        $pv = (Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue).pv
        if ($pv -and $pv -ne '0.0.0.0') { $webView = $pv; break }
    }
    if (!$webView) { Fail 'The Microsoft Edge WebView2 Runtime is not installed. Install the Evergreen Standalone runtime from Microsoft (setup never downloads anything), then run setup again.' }
    Ok "WebView2 Runtime $webView"
}

function Test-Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }

# ---- helpers ----------------------------------------------------------------------------------------------------
function Wait-Service([string[]]$states, [int]$seconds, [string]$name = $serviceName) {
    $service = Get-Service -Name $name
    $deadline = (Get-Date).AddSeconds($seconds)
    do { Start-Sleep -Milliseconds 300; $service.Refresh() } while ($states -notcontains [string]$service.Status -and (Get-Date) -lt $deadline)
    return [string]$service.Status
}
function Stop-VaultService {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        & sc.exe stop $serviceName | Out-Null
        if ((Wait-Service 'Stopped' 30) -ne 'Stopped') { Fail 'The service did not stop within 30 seconds.' }
    }
}
function Stop-BuildService {
    $service = Get-Service -Name $buildServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        & sc.exe stop $buildServiceName | Out-Null
        if ((Wait-Service 'Stopped' 60 $buildServiceName) -ne 'Stopped') { Fail 'The build service did not stop within 60 seconds (a build may be running).' }
    }
}
function Get-ExitInfo([string]$name = $serviceName) { $s = Get-CimInstance Win32_Service -Filter "Name='$name'"; return @{ Win32 = [int]$s.ExitCode; Specific = [int]$s.ServiceSpecificExitCode } }
function Invoke-ServiceOnce([string]$parameter) {
    $output = & sc.exe start $serviceName $parameter
    if ($LASTEXITCODE -ne 0) { $output | Write-Host; Fail "Could not start the service ($parameter): sc.exe exit $LASTEXITCODE." }
    if ((Wait-Service 'Stopped' 120) -ne 'Stopped') { Fail "The service did not finish '$parameter' within 120 seconds." }
    return Get-ExitInfo
}
function Assert-NoUserWrite([string[]]$paths) {
    # Write-type bits only: WriteData, AppendData, WriteEA, DeleteChild, WriteAttributes, Delete, WriteDAC, WriteOwner, GENERIC_ALL/WRITE.
    $untrusted = 'BUILTIN\Users', 'NT AUTHORITY\Authenticated Users', 'Everyone', 'NT AUTHORITY\INTERACTIVE'
    foreach ($path in $paths) {
        foreach ($rule in (Get-Acl -LiteralPath $path).Access) {
            $applies = ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0
            if ($rule.AccessControlType -eq 'Allow' -and (([int]$rule.FileSystemRights) -band 0x500D0156) -ne 0 -and $applies -and $untrusted -contains $rule.IdentityReference.Value) {
                Fail "Ordinary users could modify $path ($($rule.IdentityReference)). Fix the Program Files permissions and run setup again."
            }
        }
    }
}
function Write-InstallManifest {
    $manifest = Get-ChildItem -LiteralPath $installRoot -Recurse -File | Where-Object Name -ne 'install-manifest.sha256' | Sort-Object FullName | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.FullName.Substring($installRoot.Length + 1)
    }
    [IO.File]::WriteAllLines((Join-Path $installRoot 'install-manifest.sha256'), [string[]]$manifest, (New-Object Text.UTF8Encoding $false))
    return $manifest.Count
}
function Copy-Payload([bool]$keepConfig) {
    foreach ($part in 'runtime', 'service', 'client', 'editor') {
        $target = Join-Path $installRoot $part
        $config = Join-Path $target 'sourcevault.service.json'
        $kept = if ($keepConfig -and (Test-Path -LiteralPath $config)) { [IO.File]::ReadAllBytes($config) } else { $null }
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Copy-Item -LiteralPath (Join-Path $payload $part) -Destination $installRoot -Recurse
        if ($kept) { [IO.File]::WriteAllBytes($config, $kept) }
    }
    $serviceConfig = Join-Path $installRoot 'service\sourcevault.service.json'
    if (!(Test-Path -LiteralPath $serviceConfig)) {
        [IO.File]::WriteAllText($serviceConfig, '{"schemaVersion":1,"mode":"MetadataOnly","keyName":"' + $keyName + '"}', (New-Object Text.UTF8Encoding $false))
    }
    Assert-NoUserWrite (@($installRoot) + @(Get-ChildItem -LiteralPath $installRoot -Recurse | ForEach-Object FullName))
}
function Get-SddlShape([string]$sddl) {
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    $canonical = New-Object Security.AccessControl.DirectorySecurity
    $canonical.SetSecurityDescriptorSddlForm($sddl, $sections)
    $sddl = $canonical.GetSecurityDescriptorSddlForm($sections)
    if ($sddl -notmatch '^O:(?<owner>[^D]+)D:(?<flags>[A-Z]*)(?<aces>\(.*\))$') { return "unparsed:$sddl" }
    $aces = [regex]::Matches($Matches.aces, '\([^)]*\)') | ForEach-Object Value | Sort-Object
    "$($Matches.owner)|protected=$($Matches.flags.Contains('P'))|$($aces -join '')"
}
function Set-ExactSecurity([string]$path, [string]$sddl) {
    $sections = [Security.AccessControl.AccessControlSections]'Owner, Access'
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetSecurityDescriptorSddlForm($sddl, $sections)
    (Get-Item -LiteralPath $path).SetAccessControl($security)
    $applied = (Get-Item -LiteralPath $path).GetAccessControl($sections).GetSecurityDescriptorSddlForm($sections)
    if ((Get-SddlShape $applied) -ne (Get-SddlShape $sddl)) { Fail "Permissions on $path did not apply exactly." }
}
function Invoke-ServiceTool([string]$command) {
    $output = & (Join-Path $installRoot 'runtime\dotnet.exe') (Join-Path $installRoot 'service\SourceVault.Service.dll') $command
    return @{ Output = ($output | Out-String).Trim(); Exit = $LASTEXITCODE }
}

# ---- status -----------------------------------------------------------------------------------------------------
function Show-Status {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (!$service) { Fail 'SourceVault is not installed on this computer.' }
    $info = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($info.StartName -eq $serviceAccount) { Ok "service account $serviceAccount, start mode $($info.StartMode)" } else { Warn "service runs as $($info.StartName)" }
    $manifest = Join-Path $installRoot 'install-manifest.sha256'; $bad = 0
    foreach ($line in Get-Content -LiteralPath $manifest) { $hash, $rel = $line -split '  ', 2; $p = Join-Path $installRoot $rel; if (!(Test-Path -LiteralPath $p) -or (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne $hash) { $bad++ } }
    if ($bad) { Warn "$bad installed file(s) differ from install-manifest.sha256 (run setup -Update)" } else { Ok 'installed files match install-manifest.sha256' }
    if (Get-LocalGroup -Name $groupName -ErrorAction SilentlyContinue) {
        $members = @(Get-LocalGroupMember -Group $groupName | ForEach-Object Name)
        Ok "operators: $(if ($members) { $members -join ', ' } else { '(none)' })"
    } else { Warn "local group '$groupName' is missing" }
    $check = Invoke-ServiceTool 'vault-root-check'
    if ($check.Exit -eq 0) { Ok 'vault folder permissions trusted' } else { Warn "vault folder check failed:`n$($check.Output)" }
    if ($service.Status -eq 'Running') {
        $pipe = [bool]([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*\SourceVault.v1' })
        if ($pipe) { Ok 'service RUNNING and listening' } else { Warn 'service running but its pipe was not found' }
    } else {
        $exit = Get-ExitInfo
        $reason = if ($exit.Win32 -eq 1066 -and $exitReasons.ContainsKey($exit.Specific)) { $exitReasons[$exit.Specific] } elseif ($exit.Win32 -eq 0) { 'stopped normally' } else { "Win32 exit $($exit.Win32)" }
        Warn "service is $($service.Status): $reason"
    }
    $build = Get-Service -Name $buildServiceName -ErrorAction SilentlyContinue
    if ($build) {
        $config = Invoke-ServiceTool 'build-config-check'
        if ($config.Exit -eq 0) { Ok "build configuration valid ($(($config.Output -split "`r?`n") -join '; '))" } else { Warn "build configuration: $($config.Output)" }
        if ($build.Status -eq 'Running') { Ok 'build service RUNNING' }
        else {
            $exit = Get-ExitInfo $buildServiceName
            $reason = if ($exit.Win32 -eq 1066 -and $exitReasons.ContainsKey($exit.Specific)) { $exitReasons[$exit.Specific] } elseif ($exit.Win32 -eq 0) { 'stopped normally' } else { "Win32 exit $($exit.Win32)" }
            Warn "build service is $($build.Status): $reason"
        }
    } else { Ok 'build service not installed (builds unavailable)' }
}

# ---- main -------------------------------------------------------------------------------------------------------
Write-Host 'SourceVault setup (development build: synthetic data only until security review)' -ForegroundColor White
if ($Status) {
    if (!(Test-Admin)) { Fail 'Run -Status from an elevated PowerShell (Run as administrator).' }
    Step 'Status'; Show-Status; return
}
Step 'Pre-flight checks'
Test-Package
Test-Machine
if ($CheckOnly) { Write-Host "`nPre-flight passed. Nothing was changed." -ForegroundColor Green; return }
if (!(Test-Admin)) { Fail 'Run setup from an elevated PowerShell (right-click > Run as administrator).' }
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and !$Update) { Fail 'SourceVault is already installed. Use -Update to replace the program files (key, vault and settings are kept), or -Status to check it.' }
if (!$existing -and $Update) { Fail 'SourceVault is not installed yet; run setup without -Update.' }
$running = Get-Process -Name 'SourceVault.Editor', 'SourceVault.LabClient' -ErrorAction SilentlyContinue
if ($running) { Fail 'Close all SourceVault editor and lab client windows first.' }

Step "Operators group '$groupName'"
if (!(Get-LocalGroup -Name $groupName -ErrorAction SilentlyContinue)) {
    New-LocalGroup -Name $groupName -Description 'May use the SourceVault service pipe' | Out-Null
    Ok 'created'
} else { Ok 'already exists' }
foreach ($operator in $Operators) {
    $sid = (New-Object Security.Principal.NTAccount($operator)).Translate([Security.Principal.SecurityIdentifier]).Value
    if (Get-LocalGroupMember -Group $groupName | Where-Object { $_.SID.Value -eq $sid }) { Ok "$operator is already a member" }
    else { Add-LocalGroupMember -Group $groupName -Member $operator; Ok "added $operator (takes effect at their next sign-in)" }
}

Step "Program files ($installRoot)"
if ($Update) { Stop-BuildService; Stop-VaultService } else { if (Test-Path -LiteralPath $installRoot) { Fail "$installRoot already exists without a registered service. Remove it, then run setup again." }; New-Item -ItemType Directory -Path $installRoot | Out-Null }
Copy-Payload -keepConfig ([bool]$Update)
Ok "installed; $(Write-InstallManifest) files hashed in install-manifest.sha256"

$binPath = '"{0}" "{1}" service' -f (Join-Path $installRoot 'runtime\dotnet.exe'), (Join-Path $installRoot 'service\SourceVault.Service.dll')
if (!$Update) {
    Step "Service account $serviceAccount"
    New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName 'SourceVault' -StartupType Manual `
        -Description 'SourceVault encrypted source vault (development build). Opens its own TPM key; serves the installed editor.' | Out-Null
    & sc.exe config $serviceName obj= $serviceAccount | Out-Null
    if ($LASTEXITCODE -ne 0) { & sc.exe delete $serviceName | Out-Null; Fail "Could not set the service account (sc.exe exit $LASTEXITCODE)." }
    & sc.exe sidtype $serviceName unrestricted | Out-Null
    $startMode = if ($ManualStart) { 'demand' } else { 'delayed-auto' }
    & sc.exe config $serviceName start= $startMode | Out-Null
    $info = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($info.StartName -ne $serviceAccount -or $info.PathName -ne $binPath) { & sc.exe delete $serviceName | Out-Null; Fail 'Service registration did not match the expected account or path.' }
    Ok "registered; starts $(if ($ManualStart) { 'manually' } else { 'automatically (delayed) at boot' })"
}

Step 'TPM key for the service'
$result = Invoke-ServiceOnce 'provision-key'
if ($result.Win32 -eq 0) { Ok "created decrypt-only key $keyName under $serviceAccount (export refused, test round trip passed)" }
elseif ($result.Win32 -eq 1066 -and $result.Specific -eq 1) { Ok 'key already exists (kept)' }
else {
    $what = @{ 2 = 'identity, config or TPM provider unavailable'; 3 = 'created key failed the policy checks'; 4 = 'created key was exportable'; 5 = 'test round trip failed' }[$result.Specific]
    Fail "Key provisioning failed (Win32 $($result.Win32), code $($result.Specific): $what)."
}

Step "Vault folder ($vaultRoot)"
$serviceSid = ([Security.Principal.NTAccount]$serviceAccount).Translate([Security.Principal.SecurityIdentifier]).Value
if (Test-Path -LiteralPath $vaultParent) {
    $check = Invoke-ServiceTool 'vault-root-check'
    if ($check.Exit -ne 0) { Write-Host $check.Output; Fail "The existing vault folder is not trusted. Setup does not change an existing vault; review $vaultParent." }
    Ok 'existing vault folder kept; permissions trusted'
} else {
    $rootSddl = (Invoke-ServiceTool 'vault-root-sddl').Output
    if ($rootSddl -notmatch '^D:P(\(A;OICI;FA;;;S-1-5-[0-9-]+\))+$' -or $rootSddl -notmatch [regex]::Escape($serviceSid)) { Fail 'The service did not report the expected vault permissions.' }
    New-Item -ItemType Directory -Path $vaultParent | Out-Null
    Set-ExactSecurity $vaultParent "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x1200a9;;;$serviceSid)"
    New-Item -ItemType Directory -Path $vaultRoot | Out-Null
    Set-ExactSecurity $vaultRoot ('O:BA' + $rootSddl)
    $check = Invoke-ServiceTool 'vault-root-check'
    if ($check.Exit -ne 0) { Write-Host $check.Output; Fail 'The new vault folder did not pass the service check.' }
    Ok 'created; only the service, SYSTEM and Administrators can access it'
}

if ($WithSyntheticDemo) {
    Step 'Synthetic demo project'
    $result = Invoke-ServiceOnce 'create-synthetic-project'
    if ($result.Win32 -eq 0) { Ok 'created a fictional three-file project' } else { Fail "Could not create the demo project (Win32 $($result.Win32)/$($result.Specific))." }
}

Step 'Starting the service'
& sc.exe start $serviceName | Out-Null
$state = Wait-Service 'Running', 'Stopped' 60
if ($state -ne 'Running') {
    $exit = Get-ExitInfo
    $reason = if ($exit.Win32 -eq 1066 -and $exitReasons.ContainsKey($exit.Specific)) { $exitReasons[$exit.Specific] } else { "Win32 exit $($exit.Win32)" }
    Fail "The service refused to start: $reason."
}
Start-Sleep -Milliseconds 500
if (!([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*\SourceVault.v1' })) { Fail 'The service is running but its pipe is missing.' }
Ok "RUNNING as $serviceAccount and listening"

# ---- builds (optional) ------------------------------------------------------------------------------------------
$buildExisting = Get-Service -Name $buildServiceName -ErrorAction SilentlyContinue
if ($WithBuilds -or $buildExisting) {
    Step "Build service $buildAccount"
    $buildBin = '"{0}" "{1}" build-service' -f (Join-Path $installRoot 'runtime\dotnet.exe'), (Join-Path $installRoot 'service\SourceVault.Service.dll')
    if (!$buildExisting) {
        New-Service -Name $buildServiceName -BinaryPathName $buildBin -DisplayName 'SourceVault Build' -StartupType Manual `
            -Description 'SourceVault build service (development build). Runs builds in the configured Docker containers for the SourceVault service; never opens the vault or its key.' | Out-Null
        & sc.exe config $buildServiceName obj= $buildAccount | Out-Null
        if ($LASTEXITCODE -ne 0) { & sc.exe delete $buildServiceName | Out-Null; Fail "Could not set the build service account (sc.exe exit $LASTEXITCODE)." }
        & sc.exe sidtype $buildServiceName unrestricted | Out-Null
        & sc.exe config $buildServiceName start= $(if ($ManualStart) { 'demand' } else { 'delayed-auto' }) | Out-Null
        $info = Get-CimInstance Win32_Service -Filter "Name='$buildServiceName'"
        if ($info.StartName -ne $buildAccount -or $info.PathName -ne $buildBin) { & sc.exe delete $buildServiceName | Out-Null; Fail 'Build service registration did not match the expected account or path.' }
        Ok 'registered'
    } else { Ok 'already registered' }
    $buildSid = ([Security.Principal.NTAccount]$buildAccount).Translate([Security.Principal.SecurityIdentifier]).Value

    # Admin-only configuration: Administrators and SYSTEM change it; the build service only reads it; operators can't see it.
    if (!(Test-Path -LiteralPath $configDir)) {
        New-Item -ItemType Directory -Path $configDir | Out-Null
        Set-ExactSecurity $configDir "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;$buildSid)"
        Ok "created $configDir (administrators only; the build service can read it)"
    } else { Ok "$configDir kept" }
    $example = Join-Path $package 'build.example.json'
    if (Test-Path -LiteralPath $example) { Copy-Item -LiteralPath $example -Destination (Join-Path $configDir 'build.example.json') -Force }

    $check = Invoke-ServiceTool 'build-config-check'
    if ($check.Exit -eq 0) {
        $artifactRoot = ($check.Output -split "`r?`n" | Where-Object { $_ -like 'artifactRoot=*' }) -replace '^artifactRoot=', ''
        Ok "build.json is valid ($(($check.Output -split "`r?`n" | Where-Object { $_ -like 'targets=*' }) -replace '^targets=', 'targets: '))"
        if (!(Test-Path -LiteralPath $artifactRoot)) {
            New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
            $operatorsSid = (New-Object Security.Principal.NTAccount($groupName)).Translate([Security.Principal.SecurityIdentifier]).Value
            # The build service writes artifacts; operators read them; ordinary users get nothing.
            Set-ExactSecurity $artifactRoot "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;$buildSid)(A;OICI;0x1200a9;;;$operatorsSid)"
            Ok "created artifact folder $artifactRoot (build service writes; '$groupName' can read)"
        } else { Warn "artifact folder $artifactRoot already exists; its permissions were not changed (the build service needs Modify, operators Read)" }
        Stop-BuildService
        & sc.exe start $buildServiceName | Out-Null
        $state = Wait-Service 'Running', 'Stopped' 60 $buildServiceName
        if ($state -ne 'Running') {
            $exit = Get-ExitInfo $buildServiceName
            $reason = if ($exit.Win32 -eq 1066 -and $exitReasons.ContainsKey($exit.Specific)) { $exitReasons[$exit.Specific] } else { "Win32 exit $($exit.Win32)" }
            Fail "The build service refused to start: $reason."
        }
        Start-Sleep -Milliseconds 500
        if (!([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*\SourceVault.Build.v1' })) { Fail 'The build service is running but its pipe is missing.' }
        Ok "RUNNING as $buildAccount"
        Warn "Builds also need Docker access for $buildAccount (administrator-equivalent on this PC): run Grant-BuildDockerAccess.ps1 from this package. For a WSL engine, run New-WslDockerTls.ps1 first."
    } elseif (!(Test-Path -LiteralPath $buildConfig)) {
        Warn "No build configuration yet. Copy $configDir\build.example.json to build.json, edit it (container names, paths, artifactRoot), then run setup -Update -WithBuilds again. The build service is not started until then."
    } else {
        Write-Host "   $($check.Output)" -ForegroundColor Yellow
        Warn "build.json is invalid; fix it and run setup -Update -WithBuilds again. The build service was not started."
    }
}

Step 'Start menu shortcut'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $installRoot 'editor\SourceVault.Editor.exe'
$link.Arguments = '--vault'
$link.WorkingDirectory = Join-Path $installRoot 'editor'
$link.Description = 'SourceVault vault editor'
$link.Save()
Ok "'SourceVault Editor' added to the Start menu for all users"

Write-Host "`nSetup complete." -ForegroundColor Green
Write-Host "Operators must SIGN OUT AND BACK IN once before the editor can connect. Then open 'SourceVault Editor' from the Start menu."
Write-Host 'Use synthetic data only until an independent security review says otherwise. Read README.md before first use.'
