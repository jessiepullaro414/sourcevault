# SourceVault implementation rules

Use synthetic inputs only until the feasibility gates pass. Read `docs/architecture.md` and `contracts/protocol-v1.md` before implementation. The original planning documents are in `local/planning/` (git-ignored; see `docs/local-project.md`).

The coordinator owns shared contracts, repository configuration, integration, and status. Workers own their assigned directories; do not edit another worker's files concurrently. Do not install services, modify machine policies/ACLs, clear TPMs, enable Hyper-V, or provision VMs during development checks. Provide explicit lab scripts and report tests requiring those changes as unverified. Never clear a TPM as a test step.

No software fallback for TPM keys. No logs containing source, keys, session secrets, or document contents. No claim of administrator resistance, runtime executable identity, guaranteed capture blocking, intentional-exfiltration prevention, or RAM-only processing. Cryptographic format changes require coordinator review. Editor development may vendor the explicitly pinned Microsoft Monaco and WebView2 packages from their official package registries, with recorded checksums/licenses; runtime asset fetching, CDNs and unreviewed transitive packages remain forbidden. Report failed/unavailable tests honestly.

Target .NET 10; keep service/storage feasibility projects BCL-only. The editor host may reference the vendored pinned WebView2 package from a local-only NuGet feed. Use `scripts/Verify.ps1` for integration verification. Keep all generated test files under the explicitly supplied test workspace. Do not choose an open-source license on the owner's behalf.
