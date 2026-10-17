# LAB ONLY. Run as your NORMAL user (not elevated) while the service is running (Test-ServiceLab.ps1 -LeaveRunning).
# Probes the two access layers of \\.\pipe\SourceVault.v1 from Windows PowerShell, which is not an installed client:
#   - not an operator in this logon  -> the pipe DACL refuses the connection
#   - operator                       -> connects, but the service closes without answering (executable check)
# Either outcome is a pass; a protocol answer to PowerShell would be a failure. Sends no capability or content.
$ErrorActionPreference = 'Stop'
$pipeName = 'SourceVault.v1'
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Output 'Run this from a normal (non-elevated) PowerShell window.'; exit 2
}
if (-not ([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*\$pipeName" })) { Write-Output 'Pipe not found: start the service first (Test-ServiceLab.ps1 -LeaveRunning).'; exit 2 }
$operators = (New-Object Security.Principal.NTAccount('SourceVault Operators')).Translate([Security.Principal.SecurityIdentifier])
$inGroup = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole($operators)
# Exactly the rights the DACL grants operators: ReadData | WriteData | ReadAttributes | ReadPermissions | Synchronize.
$rights = [IO.Pipes.PipeAccessRights]0x00120083
$client = New-Object IO.Pipes.NamedPipeClientStream('.', $pipeName, $rights, [IO.Pipes.PipeOptions]::None,
    [Security.Principal.TokenImpersonationLevel]::Identification, [IO.HandleInheritability]::None)
try {
    try { $client.Connect(3000) }
    catch [UnauthorizedAccessException] {
        if ($inGroup) { Write-Output 'FAIL: you are an operator in this logon but the DACL refused the connection.'; exit 1 }
        Write-Output 'PASS: not an operator in this logon; the pipe DACL refused the connection.'
        Write-Output '      (After New-OperatorsGroup.ps1, sign out and in, then rerun to test the executable check.)'
        exit 0
    }
    if (-not $inGroup) { Write-Output 'FAIL: connected although this logon is not in SourceVault Operators.'; exit 1 }
    $json = [Text.Encoding]::UTF8.GetBytes('{"version":2,"requestId":"' + [guid]::NewGuid().ToString('D') + '","operation":"BeginSession","body":{}}')
    $frame = [BitConverter]::GetBytes([uint32]$json.Length) + $json
    $client.Write($frame, 0, $frame.Length); $client.Flush()
    $buffer = New-Object byte[] 4
    $read = 0
    try { $read = $client.Read($buffer, 0, 4) } catch [IO.IOException] { $read = 0 }
    if ($read -eq 0) { Write-Output 'PASS: operator connected, but the service closed without answering: PowerShell is not an installed client.'; exit 0 }
    Write-Output 'FAIL: the service answered a program that is not an installed client.'; exit 1
} finally { $client.Dispose() }
