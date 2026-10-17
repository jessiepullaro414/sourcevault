# SourceVault implementation rules

Use synthetic inputs only until the feasibility gates pass. Read `docs/architecture.md` and `contracts/protocol-v1.md` before implementation. The parent planning documents are outside this repository, in `../`.

The coordinator owns shared contracts, repository configuration, integration, and status. Workers own their assigned directories; do not edit another worker's files concurrently. Do not install services, modify machine policies/ACLs, clear TPMs, enable Hyper-V, or provision VMs during development checks. Provide explicit lab scripts and report tests requiring those changes as unverified. Never clear a TPM as a test step.

No software fallback for TPM keys. No logs containing source, keys, session secrets, or document contents. No claim of administrator resistance, runtime executable identity, guaranteed capture blocking, intentional-exfiltration prevention, or RAM-only processing. Cryptographic format changes require coordinator review. No third-party runtime dependency downloads. Report failed/unavailable tests honestly.

Target .NET 10; prefer BCL-only feasibility projects, with no NuGet packages. Use `scripts/Verify.ps1` for integration verification. Keep all generated test files under the explicitly supplied test workspace. Do not choose an open-source license on the owner's behalf.
