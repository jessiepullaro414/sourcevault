# Encrypted record codec

`SourceVault.Storage.RecordCodec` implements `contracts/storage-v1.md`. This is a synthetic development component, not evidence of a deployed TPM vault. The API is:

```csharp
byte[] RecordCodec.Encrypt(RecordContext context, ReadOnlySpan<byte> plaintext, IKeyProtector protector);
byte[] RecordCodec.Decrypt(RecordContext expectedContext, ReadOnlySpan<byte> record, IKeyProtector protector);
```

The codec is BCL-only and platform independent. Production storage contains only the wrapping interface; it supplies no software key protector or configuration fallback. The caller must supply the separately reviewed TPM adapter and trusted context. Caller contexts require nonempty project and revision IDs; documents require a nonempty file ID and manifests require an empty file ID.

## Format and limits

The public constants are `FixedPrefixLength = 74`, `WrappedKeyLength = 256`, `TagLength = 16`, `RecordOverhead = 346`, `MaxDocumentLength = 262144`, and `MaxManifestLength = 1048576`. UUID bytes use RFC/network order. AES-256-GCM uses a newly generated 32-byte key and random 12-byte nonce for each encryption. The complete fixed prefix and wrapped key are authenticated as AAD. The supplied protector must wrap using the contract's RSA-2048/OAEP-SHA256 policy; a 256-byte output alone does not prove that policy or hardware protection.

Before unwrapping or allocating a plaintext buffer, decryption checks minimum size, magic, kind, reserved bytes, all expected IDs, wrapped-key length, kind-specific payload bound, and exact overall size. A header cannot choose its own trusted context. Empty payloads are permitted. Documents must be strict UTF-8 on both encryption and authenticated decryption; manifests remain bounded raw bytes for the revision store to parse.

Invalid caller contexts/plaintexts throw `ArgumentException`; a null protector throws `ArgumentNullException`. Invalid encrypted records and context mismatches throw `CryptographicException` with generic text. Underlying protector and platform failures propagate; callers must return protocol error codes rather than exception details or raw logs.

## Buffer ownership and limits

Fresh and unwrapped data-key arrays are explicitly zeroed in `finally`, including invalid-length keys and authenticated-decryption failures. Failed plaintext buffers are zeroed. Successful plaintext is owned by the caller, which must clear it after use. Input source belongs to the caller; the codec does not overwrite it. The protector must return a fresh owned unwrapped-key array because the codec clears that array. A protector must never retain/use the borrowed wrapping input after returning.

This does not guarantee RAM-only processing or erase runtime copies, paging, dumps, provider-internal buffers, or plaintext retained by callers. No secure deletion, administrator resistance, key recovery, or full-store rollback prevention is claimed.

## Synthetic verification

`tests/SourceVault.Storage.Tests` is a dependency-free console test executable. Its ephemeral RSA protector exists only in that test project. Tests cover document and raw-manifest roundtrips and limits; exact byte layout and network-order UUIDs; fresh record keys/nonces; strict UTF-8; caller-context validation; all truncations of a synthetic record; trailing bytes; header, nonce, wrapped-key, tag and ciphertext mutation; mismatched context; RSA recipient mismatch; wrapped-key AAD binding even when an unwrap stub ignores its input; invalid/null unwrapped keys; and observable unwrapped-key zeroization after success/failure.

Fresh encryption-key zeroization and failed-plaintext zeroization are implemented with `finally` but are not directly observed by the managed test harness, since neither array escapes. TPM operations, hardware provider policy, service isolation, machine ACLs and physical persistence require separate lab validation.

From the repository directory, with the workspace portable .NET SDK:

```powershell
$env:APPDATA = Join-Path $PWD '../../work/codec-profile'
$env:DOTNET_CLI_HOME = Join-Path $PWD '../../work/codec-cli'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
& ../../work/toolchain/dotnet/dotnet.exe restore tests/SourceVault.Storage.Tests/SourceVault.Storage.Tests.csproj --configfile NuGet.Config
& ../../work/toolchain/dotnet/dotnet.exe build tests/SourceVault.Storage.Tests/SourceVault.Storage.Tests.csproj --no-restore -p:UseSharedCompilation=false
& ../../work/toolchain/dotnet/dotnet.exe tests/SourceVault.Storage.Tests/bin/Debug/net10.0/SourceVault.Storage.Tests.dll
```

Check each command's exit code before proceeding. This test executable takes no arguments and creates no vault files.
