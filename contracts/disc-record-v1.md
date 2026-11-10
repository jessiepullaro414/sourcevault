# Disc record, version 1 (2026-11-10)

The signed record on a finalized disc that SourceVault writes ([disc-export-design.md](../docs/disc-export-design.md)). It is implemented in `src/SourceVault.Packaging/DiscRecord.cs`, shared by the service, which writes and signs, and by Pack's `verify-disc`, which checks.

## Files at the disc's root

| File | Content |
|---|---|
| `source\` | The project's saved files, at their vault paths (`source\src\main.cs`) |
| `SOURCEVAULT-DISC.json` | The record: UTF-8 JSON, at most 32 MiB |
| `SOURCEVAULT-DISC.sig` | RSA-PSS (SHA-256, salt length = hash length) over SHA-256 of the **exact bytes** of `SOURCEVAULT-DISC.json`, by the disc signing key |
| `SOURCEVAULT-DISC-KEY.der` | That key's public half, DER SubjectPublicKeyInfo (RSA-2048). Trusted only through its check code |
| `README-SOURCEVAULT.txt` | Plain words for people. Not signed, and not checked |

## The record

```json
{
  "format": "sourcevault-disc", "version": 1,
  "discId": "<uuid, D format>", "createdUtc": "yyyy-MM-ddTHH:mm:ssZ",
  "computer": "<vault PC name, at most 64 characters>",
  "signingKey": "<lowercase hex SHA-256 of SOURCEVAULT-DISC-KEY.der>",
  "project": { "name": "<at most 128 characters>", "snapshotId": "<uuid>" },
  "burner": { "account": "DOMAIN\\name", "sid": "S-1-..." },
  "approvers": [ { "account": "...", "sid": "S-1-...", "approvedUtc": "yyyy-MM-ddTHH:mm:ssZ" }, { "...": "exactly two" } ],
  "files": [ { "path": "<vault logical name>", "length": 0, "sha256": "<lowercase hex>" } ]
}
```

**Rules:**
- **Shape:** exactly these properties, no others, and no duplicates.
- **Approvers:** exactly two, with different SIDs, and neither is the burner's SID.
- **Accounts:** at most 256 characters, no control characters.
- **Files:**
  - each path is a valid vault logical name, and paths are unique ignoring case;
  - at most 25,000 files;
  - every `sha256` is 32 bytes.

## Verifying

The order matters: nothing in the record is trusted before the signature is checked.

1. **The key:** the check code the verifier was given (from the vault PC's administrator) must equal the first 96 bits of SHA-256(`SOURCEVAULT-DISC-KEY.der`), as six groups of four hex characters. Separators and case are ignored.
2. **The signature:** RSA-PSS SHA-256 over SHA-256 of the record's bytes, with that key.
3. **The record:** parsed strictly, and its `signingKey` must equal the key's fingerprint.
4. **The files:** under `source\`, every file must be listed with exactly its length and SHA-256, every listed file must be present, and links are refused.

## The signing key

RSA-2048 in the vault PC's TPM (Microsoft Platform Crypto Provider).
- **Usage:** sign-only (`PCP_KEY_USAGE_POLICY` = signature).
- **Protection:** non-exportable.
- **Owner:** `NT SERVICE\SourceVault`.
- **Name:** `<vault key name>.Disc`, created once by `provision-disc-key`.

It is never used for anything but disc records.

**Changing this format** is a cryptographic format change. It needs coordinator review and a new version number.
