# T03 builder feasibility spike

This implements a bounded offline context/artifact validation path and a tiny synthetic compiler. It does **not** implement a deployed broker, Docker backend, VM provisioning, TypeScript compiler, artifact publication, or a secret-bearing guest channel.

## Implemented profile

- Context: uncompressed USTAR, exactly one regular UTF-8 file named `main.sv`; no names chosen by an operator. Archive <=1 MiB, content 1..256 KiB. Physical headers are checked before TarReader, rejecting PAX/GNU extensions, directories, links, duplicate entries, nonzero padding/trailers, truncation, and non-USTAR formats. TarReader verifies checksums and reads the bounded payload. Nothing is extracted.
- Synthetic compiler: exact `ADD integer integer` grammar, checked 32-bit addition, deterministic `console.log(result);` output. It launches no process and interprets no arbitrary JavaScript. It demonstrates a build boundary without external compiler packages. A real approved compiler/profile remains a separate gate.
- Artifact: same archive limits and one exact regular UTF-8 filename `app.js`. Exclusive output creation; trusted parent directory required. The validator checks structure, not confidentiality, safety of JavaScript, or absence of source embedded inside output.
- CLI errors contain only `Rejected`; test labels and counts contain no source, compiler output, or OS paths. This does not constrain a future real compiler's internal logging.
- Every archive is buffered before parsing, with an explicit 1 MiB maximum. Memory buffers can be paged or captured; this is not a RAM-only guarantee. CLI inputs are trusted local developer paths, separate from protocol v1 requests. There is no network listener.

The narrow single-file USTAR format is experimental T03 profile `synthetic-add-v1`, not a new wire protocol or a production artifact standard. Expanding it requires a reviewed recipe/artifact contract. A hostile compiler may still produce source-bearing content inside `app.js`; allowlisting filenames does not prevent intentional extraction.

## Run offline tests

Use a preinstalled .NET 10 SDK. No NuGet runtime dependencies are needed; repository NuGet.Config clears package sources. From repository root, with a writable CLI home and a fully qualified workspace:

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location) 'artifacts/cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
# Restricted lab only: redirect unavailable user NuGet settings for this process.
$env:APPDATA = Join-Path $env:DOTNET_CLI_HOME 'AppData/Roaming'
dotnet restore spikes/builder/SourceVault.BuilderSpike.csproj --configfile ./NuGet.Config
dotnet run --no-restore --project spikes/builder/SourceVault.BuilderSpike.csproj -- self-test (Join-Path (Get-Location) 'artifacts/builder-tests')
./spikes/builder/Get-BuilderInventory.ps1
```

The tests retain synthetic fixtures in a unique directory beneath the supplied absolute workspace. No recursive deletion is performed. CLI modes also include `validate-context INPUT_TAR`, `synthetic-compile INPUT_TAR NEW_OUTPUT_TAR`, and `validate-artifacts INPUT_TAR`. Trust the containing directories; this CLI is not a hardened service-side filesystem interface.

## Observed development results (2026-10-17)

.NET SDK 10.0.402 built the project with warnings treated as errors; all **25 self-tests passed**. Positive tests validate the context and compiled artifact. Negative tests cover output overwrite, seven path variants, symbolic/hard links, directories, content/archive limits, empty content, invalid UTF-8, truncation, nonzero trailers, duplicate entries, PAX, checksum corruption, compiler injection/overflow, and source filename presented as artifact.

Read-only inventory succeeded and emitted sanitized JSON: Docker/Hyper-V cmdlets absent; WSL executable present but never invoked; Get-Tpm returned present/ready/enabled false. CIM, optional features, volume/BitLocker, and VM summaries were unavailable. The report intentionally omits paths, account/VM/distro names, serials and exception text. `unavailable` is not evidence a feature is absent. No features, VMs, services or machine policies were configured. First SDK invocation emitted a dev-certificate installation message before certificate generation was disabled; no trust operation was requested.

Linux guest execution and `run-guest-synthetic.sh` have **not** been tested here. Do not infer Hyper-V isolation or persistence properties from the passing host tests.

## Service-managed VM lab procedure — not executed

1. On a disposable, approved Windows Hyper-V lab workstation, an administrator records Windows build, hardware, TPM/provider availability, BitLocker protection on all host volumes holding VM disks, configuration, page files, checkpoints, dumps and quarantine. Offline means provisioned media and compiler/runtime artifacts are checksum-verified before use; package restore must have no online source. Use synthetic canaries only.
2. Provision a minimal Linux guest template from verified offline media; preinstall the fixed .NET 10 runtime and publish this project as a framework-dependent application (`dotnet publish spikes/builder/SourceVault.BuilderSpike.csproj -c Release --no-restore`). Pin/check the published DLL and dependencies. No guest package downloads during jobs. Build workers are unprivileged guest accounts, separate from the eventual key service.
3. Under administrator-controlled configuration, define one lab VM identity and a dedicated VM storage root on encrypted host storage. Do not grant operators Hyper-V Administrators or Docker access. Do not put the key-service account in Hyper-V Administrators. A future separately deployed broker uses a narrowly scoped management helper/ACL boundary for this exact VM; validating that delegation is a release gate, not implemented by this CLI. Virtual service-account login/profile assumptions must be tested on the target machine.
4. Administrator uses documented Hyper-V cmdlets to configure a Generation 2 VM with 2 vCPUs, fixed 2 GiB memory (dynamic memory disabled), no NIC/switch, no host shared folders/drives or clipboard integrations, no automatic checkpoints, automatic start `Nothing`, automatic stop `TurnOff`; VM disks and configuration remain in the lab root. No checkpoints, save/resume, host backup snapshots or dump collection during canary trials. `TurnOff` loses in-flight work and does not erase prior disk content. Record the actual supported configuration and host policy; this document performs none of these changes.
5. Transfer **synthetic** context and published compiler via offline administrator-controlled media. A read-only input ISO is acceptable for synthetic trials because it deliberately persists synthetic source. Do not use that transport for confidential data. A production authenticated host-to-guest channel remains unimplemented: review Hyper-V sockets with host/VM identity binding, bounded frames/deadlines, replay protection and VM allowlisting before sending any real source. No TCP/HTTP listener is supplied.
6. Practical profile permits source on an encrypted, broker-owned per-job VM disk and disposes the VM after the job. Deleting VHDX or checkpoints is not proof of secure erasure on SSDs; assess encryption-key lifecycle and residual storage. The experimental volatile profile mounts a bounded tmpfs inside the guest and separately disables guest swap, core dumps, hibernation and persistent compiler cache/logging. Investigate host paging, saved memory, crash dumps and hypervisor behavior independently. [Kernel tmpfs documentation](https://kernel.org/doc/html/latest/filesystems/tmpfs.html) explicitly permits swapping tmpfs pages.
7. For the experimental profile only, a guest administrator precreates a private tmpfs workspace (for example `/run/sourcevault-lab`) and ensures `/proc/swaps` lists no active devices. Run `bash run-guest-synthetic.sh /opt/sourcevault/SourceVault.BuilderSpike.dll /media/synthetic/context.tar /run/sourcevault-lab` as the dedicated guest worker with write permission only to that workspace. The script refuses non-tmpfs/active swap, applies CPU/core/file/time limits, runs no shell content from the context, and retains its synthetic job for inspection. This does not mount filesystems or alter swap settings. It is not a general sandbox and does not enforce memory limits; the VM's fixed memory bounds the lab.
8. Broker-side artifact handling must read a bounded response into broker-owned quarantine, validate it with the exact trusted profile, then publish only approved artifact bytes via an atomic same-volume operation into an operator-readable directory. Reject the complete job if any entry is invalid. Never relay raw guest stdout/stderr. This spike validates archives but does not implement publication, authenticated transport, a queue, revocation, or cancellation.
9. Instrument synthetic canaries across successful build, failed build, timeout, guest crash, host reboot and disk-full cases. Inspect guest disk/cache/log/dump/swap, host VHDX/config/checkpoints/saved-state/pagefile/dumps/backups, broker quarantine, output and logs. Record where the canary persists, who can read it, retention/key disposal, and failure cleanup behavior. A filename search alone cannot prove absence from raw storage. Security review must approve a precise claim before production inputs.

## Primary references

- [Microsoft TarReader.GetNextEntry](https://learn.microsoft.com/en-us/dotnet/api/system.formats.tar.tarreader.getnextentry?view=net-10.0)
- [Microsoft archive security practices](https://github.com/dotnet/docs/blob/main/docs/standard/io/zip-tar-best-practices.md)
- [Hyper-V cmdlet reference](https://learn.microsoft.com/en-us/powershell/module/hyper-v/)
- [Set-VM](https://learn.microsoft.com/en-us/powershell/module/hyper-v/set-vm)
- [Get-Tpm](https://learn.microsoft.com/en-us/powershell/module/trustedplatformmodule/get-tpm)
- [Get-WindowsOptionalFeature](https://learn.microsoft.com/en-us/powershell/module/dism/get-windowsoptionalfeature)
- [BitLocker operations](https://learn.microsoft.com/en-us/windows/security/operating-system-security/data-protection/bitlocker/operations-guide)

Next gates: approved real compiler/profile, artifact content/exfiltration policy, authenticated guest channel, broker identity/delegation implementation, real VM lifecycle/cancellation tests, and measured persistence review. No RAM-only, administrator-resistance, or source-concealment claim is supported by this spike.
