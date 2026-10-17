[CmdletBinding()]
param()
# Query only. No WSL/Docker executable invocation (even version probes may
# trigger initialization). Do not output names, paths, serials, recovery keys,
# service accounts, distro names, or raw exception messages.
$ErrorActionPreference = 'Stop'
function Query-Fact([scriptblock] $Query) {
    try { [ordered]@{ status = 'available'; value = (& $Query) } }
    catch { [ordered]@{ status = 'unavailable'; value = $null } }
}
$facts = [ordered]@{
    schemaVersion = 1
    scope = 'read-only-local-sanitized'
    hyperVCommandPresent = [bool](Get-Command 'Hyper-V\Get-VM' -ErrorAction SilentlyContinue)
    dockerCommandPresent = [bool](Get-Command docker.exe -ErrorAction SilentlyContinue)
    wslCommandPresent = [bool](Get-Command wsl.exe -ErrorAction SilentlyContinue)
}
$facts.tpm = Query-Fact {
    $tpm = Get-Tpm
    [ordered]@{ present = [bool]$tpm.TpmPresent; ready = [bool]$tpm.TpmReady; enabled = [bool]$tpm.TpmEnabled }
}
$facts.os = Query-Fact {
    $os = Get-CimInstance Win32_OperatingSystem
    [ordered]@{ build = $os.BuildNumber; architecture = $os.OSArchitecture }
}
$facts.virtualization = Query-Fact {
    $computer = Get-CimInstance Win32_ComputerSystem
    $cpus = @(Get-CimInstance Win32_Processor)
    [ordered]@{
        hypervisorPresent = [bool]$computer.HypervisorPresent
        firmwareVirtualizationReported = @($cpus | ForEach-Object { [bool]$_.VirtualizationFirmwareEnabled })
        slatReported = @($cpus | ForEach-Object { [bool]$_.SecondLevelAddressTranslationExtensions })
    }
}
$facts.features = Query-Fact {
    $result = [ordered]@{}
    foreach ($feature in @('Microsoft-Hyper-V-All', 'VirtualMachinePlatform', 'Microsoft-Windows-Subsystem-Linux')) {
        $item = Get-WindowsOptionalFeature -Online -FeatureName $feature
        $result[$feature] = [string]$item.State
    }
    $result
}
$facts.backendServices = Query-Fact {
    $result = [ordered]@{}
    foreach ($service in @('vmms', 'vmcompute', 'WslService', 'LxssManager', 'com.docker.service')) {
        $item = Get-Service -Name $service -ErrorAction SilentlyContinue
        $result[$service] = if ($null -eq $item) { 'not-found' } else { [string]$item.Status }
    }
    $result
}
$facts.storage = Query-Fact {
    $volumes = @(Get-Volume | Where-Object { $_.DriveType -eq 'Fixed' })
    # Aggregate only; no volume labels, mount paths or identifiers.
    [ordered]@{
        fixedVolumeCount = $volumes.Count
        totalBytes = ($volumes | Measure-Object Size -Sum).Sum
        freeBytes = ($volumes | Measure-Object SizeRemaining -Sum).Sum
        fileSystems = @($volumes.FileSystemType | Sort-Object -Unique | ForEach-Object { [string]$_ })
    }
}
$facts.encryption = Query-Fact {
    $volumes = @(Get-BitLockerVolume)
    [ordered]@{
        volumeCount = $volumes.Count
        fullyEncryptedCount = @($volumes | Where-Object { $_.VolumeStatus -eq 'FullyEncrypted' }).Count
        protectionOnCount = @($volumes | Where-Object { $_.ProtectionStatus -eq 'On' }).Count
    }
}
$facts.vmSummary = Query-Fact {
    $vms = @(Hyper-V\Get-VM)
    [ordered]@{
        count = $vms.Count
        runningCount = @($vms | Where-Object { $_.State -eq 'Running' }).Count
        automaticCheckpointEnabledCount = @($vms | Where-Object { $_.AutomaticCheckpointsEnabled }).Count
    }
}
$facts | ConvertTo-Json -Depth 8
