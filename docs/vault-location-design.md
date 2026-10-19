# Vault location: design note (awaiting owner decisions)

Owner request (2026-10-19): "I would like the vault to be settable so it can be on a different drive like drive K:".

Status: **proposal only, nothing built.** Today the root is fixed at `%ProgramData%\SourceVault\vault` (`VaultRoot.Path`). The goal is an administrator-chosen root such as `K:\SourceVault\vault` with every existing check kept: path syntax, fixed NTFS/ReFS volume, no reparse points, trusted owners, the protected root DACL from `vault-root-sddl`, and the per-level ancestor masks. No record, manifest or catalog format changes. Encrypted records bind project, file and revision ids, not paths, so the files are location-independent.

Each decision below has a recommendation (**R**). Answer by number, e.g. "D1 R, D3 b, ...".

## D1. Where the setting lives

**R:** `vaultRoot` in `service\sourcevault.service.json`, beside the service under `C:\Program Files\SourceVault` (admin-only, hashed in `install-manifest.sha256`). Bump the schema:

- `schemaVersion: 1`: exactly the 3 current fields, meaning the default root. Existing installs keep working unchanged.
- `schemaVersion: 2`: exactly 4 fields, `vaultRoot` required. The parser rejects unknown, duplicate or missing fields as today.

The service never takes the root from arguments, the environment or the registry. `vault-root-check` and `vault-root-sddl` read the same file, so setup, `-Status` and the service always agree.

Alternative: `HKLM\SYSTEM\CurrentControlSet\Services\SourceVault\Parameters`. Rejected because it would be a second admin-only store with its own ACL to check.

## D2. Path shape

**R (a):** a full path `X:\<folder>\<leaf>` that passes the existing syntax rules: drive letter, canonical, no UNC, `\\?\` or device paths, no `.`/`..`, no reserved names. It must also be **at least two levels below the drive root**. The parser enforces that, matching the validator's "dedicated parent" rule. Setup creates both levels itself, so their ACLs are known.

(b) Drive letter only (`-VaultDrive K`), giving a fixed `K:\SourceVault\vault`. Simpler and fewer cases, but less flexible.

A volume mounted into a folder (no drive letter) is already refused, because a mount point is a reparse point. Deeper paths such as `K:\Data\Work\SourceVault\vault` are accepted only if the intermediate folders pass the ancestor rules as they are. Setup never changes an existing folder.

## D3. Which drives are allowed

These checks already apply to any path: `DriveType` must be Fixed, the filesystem NTFS or ReFS, and the ACL chain must pass. A FAT/exFAT stick, a network share and a mapped drive are already refused (code 10). One gap remains: **many USB hard disks and SSDs report as Fixed**, and a VHD/VHDX or a `subst` drive reports the type of what sits underneath. Options:

- **(a) R:** add a read-only "local disk" check to `TrustedStorageRoot`. This is new finding `volume_not_local_disk` and only adds refusals. The drive letter must map to a volume device (`QueryDosDevice` → `\Device\HarddiskVolumeN`, which rules out `subst`). The disk's bus type (`IOCTL_STORAGE_QUERY_PROPERTY`) must be on an allow-list: SATA, ATA, SAS, SCSI, NVMe, RAID, Storage Spaces. USB, 1394, SD, MMC, iSCSI and file-backed virtual disks are refused. If the query fails, the check fails closed. This is BCL P/Invoke only, and it applies to C: too. The lab must confirm C: still passes on this machine.
- (b) Same as (a), but only for roots outside the system volume. This avoids any risk to the existing C: installs.
- (c) No new check: document that USB disks must not be used.

Why it matters: a vault on a separate disk is easier to unplug, copy or swap. A swapped-in disk prepared elsewhere can carry ACLs that pass, because the service SID and the Administrators/SYSTEM SIDs are the same on every machine. It still fails catalog authentication (code 11/12), because it lacks this TPM's key. But an **older copy of this same vault** would authenticate. Whole-store rollback is explicitly not prevented (`contracts/storage-v1.md`). Unplugging the disk is also a denial of service. Option (a) narrows this but does not close it, and none of this is a capture or exfiltration claim.

## D4. Drive missing or locked at boot

Today a missing drive, or a BitLocker-locked one (its filesystem is unreadable), already fails the validator and gives code 10. Options:

- **(a) R:** before the ACL checks, report a distinct **service-specific exit code 17, "vault drive not available (missing, locked or not mounted)"**, with finding `volume_unavailable`. Code 10 stays "present but untrusted". The service stays stopped with no automatic retry, and nothing is opened or created. After unlocking or attaching the drive, an administrator runs `sc.exe start SourceVault` or `-Status`. Setup and `-Status` print the reason.
- (b) As (a), plus SCM failure actions: restart up to 3 times, 60 s apart. This covers a slow BitLocker auto-unlock, but the setting is persistent service configuration.
- (c) Keep code 10 for everything.

Note: BitLocker auto-unlock of a fixed data drive is not proven to finish before a delayed-auto service starts. That is a lab step.

## D5. ACL of the drive root (`K:\`) and other ancestors

The validator is **unchanged** in every option except (b). For `K:\SourceVault\vault`, the volume root `K:\` is a *higher ancestor*. There, untrusted principals may hold create/append rights, but not Delete, DeleteChild, WRITE_DAC, WRITE_OWNER or generic rights. Its owner must be SYSTEM, Administrators or TrustedInstaller.

The stock root ACL of a data drive is not fixed. This machine has only C:, so nothing was observed here. Windows versions are believed to use one of two forms, which the lab must confirm:

1. Users: create folders on the root (`(CI)(AD)`), plus inherit-only rights for files in subfolders. **Passes:** create rights on a higher ancestor are tolerated, and inherit-only ACEs do not apply to the root.
2. Authenticated Users: **Modify** `(OI)(CI)(M)` on the root itself. **Fails:** Modify includes Delete (0x10000), which the ancestor mask counts, giving `untrusted_write_access at ancestor:2`. Modify does not include DeleteChild, so users cannot remove `K:\SourceVault`. Delete on a volume root is inert in practice, because NTFS does not delete or rename a volume's root directory.

A drive formatted on another computer may also have an untrusted owner (`owner_untrusted`).

Options when `K:\` fails:

- **(a) R:** setup never changes the drive root. It stops and prints the findings. The user guide documents an explicit, owner-run, C:\-style fix: on the root itself, Authenticated Users keep list, read and create-folder only. Their Modify becomes inherit-only, so every existing folder inherits exactly what it did before. Windows re-applies inherited ACLs down the drive, which can take a while on a full drive.
- (b) Relax the policy for the **volume root only**: tolerate the Delete bit there, and keep every other bit and level as today. This makes form 2 pass with no ACL change. It is a narrow relaxation of a security check and needs review.
- (c) Setup applies the fix in (a) itself behind an explicit switch such as `-FixDriveRootAcl`.

The dedicated parent setup creates (`K:\SourceVault`) gets a protected DACL, as `C:\ProgramData\SourceVault` does today. Nothing on K:\ is inherited into it.

## D6. Moving an existing vault

- **(a) R, now:** choose the location only at a **new install** (`Setup-SourceVault.ps1 -VaultRoot K:\SourceVault\vault`). `-Update` keeps the configured location and refuses `-VaultRoot` with a different path. To start over elsewhere: uninstall (the vault is kept), then reinstall with `-VaultRoot`. This gives a new empty vault, and the old one stays untouched. With synthetic data only, that is enough.
- (b) Later, as its own reviewed change, `-Update -MoveVault <path>`:
  1. Stop the services and create the new root with the exact SDDL.
  2. Copy every file byte-for-byte; the files inherit the new root's ACL, and old ACLs are not copied.
  3. Compare the SHA-256 of every file.
  4. Write the new `vaultRoot` and start the service, which authenticates the index and every project.
  5. On code 11/12, restore the old config and stop.
  6. Delete the old copy only after a typed confirmation, because a kept stale copy is a ready-made rollback.
- (c) Build (b) now.

## D7. `-Status`, uninstall and "keep vault"

**R:**

- `-Status` prints the configured root (from `vault-root-check`, which already prints `vaultRoot=`), plus drive type, filesystem, bus type and code 17/10 reasons.
- Uninstall reads `vaultRoot` from the service config *before* removing program files. The keep-vault message names the path and the exact `-VaultRoot` to pass on reinstall. A reinstall pointed at an existing **trusted** root keeps it (as today for C:). An untrusted existing root is refused and never modified.
- `-DeleteVaultData` (typed confirmation as today) deletes the vault root and its setup-created parent. It refuses if the parent holds anything other than the vault. It also deletes `C:\ProgramData\SourceVault` (config), and it never touches a drive root.
- If the configured drive is missing at uninstall, uninstall still removes the program and reports that the vault was not checked.
- The lab scripts follow the same rules: `New-VaultRoot.ps1 -VaultRoot`, `Uninstall-ServiceLab.ps1`, and `Test-VaultTamper.ps1` / `New-SyntheticProject.ps1` reading the root from config.

## D8. Knock-on changes (no decision needed unless you object)

- `C:\ProgramData\SourceVault` still exists with a K: vault, because `config\build.json` lives there. Setup creates it with today's protected ACL whenever the vault is elsewhere. This also fixes a latent issue where `config\` could be created under an unprotected parent.
- `build.json` checks (`BuildConfig`): `artifactRoot` must also not be inside the configured vault's parent folder, so build output can never land in the vault (which would fail startup with code 11). The build service reads the same service config file read-only.

## Coordination

The concurrent package-import session also edits `ServiceConfiguration.cs`, `ScmHost.cs`, `Program.cs` and `Setup-SourceVault.ps1`. This change keeps its edits there small:

- `ServiceConfiguration.cs`: one field plus the parser branch.
- `ScmHost.cs`: pass `config.VaultRoot` to the validator and the catalog (two lines).
- `Program.cs`: the two vault-root commands load the config.
- Setup: one parameter, the config write, and the vault-folder step.

Exit code 17 and any new finding code should be added to the coordinator's status/contracts by the coordinator.

## Lab checklist (owner-run, elevated; after implementation)

1. `Get-Acl K:\ | Format-List Owner, Sddl` and `Get-Volume K | Format-List DriveType, FileSystem`, plus `Get-Disk -Number (Get-Partition -DriveLetter K).DiskNumber | Format-List BusType`. Record which D5 form the drive has.
2. On a test machine or a fresh install: `Setup-SourceVault.ps1 -VaultRoot K:\SourceVault\vault -WithSyntheticDemo`. Expect RUNNING and `-Status` showing the K: path.
3. `vault-root-check` reports `trusted=yes` with the K: path. C: installs (schema 1 config) still pass after `-Update`.
4. Stop the service, take K: offline (`Set-Disk -IsOffline $true`, or lock it with BitLocker on a test drive), then `sc.exe start SourceVault`. Expect 1066/17 and nothing created. Bring it online, start again, and expect RUNNING.
5. If D3 (a): a USB disk formatted NTFS is refused with `volume_not_local_disk`.
6. Reboot with K: as the vault (delayed-auto start; BitLocker auto-unlock if used). Record RUNNING or 17.
7. `Uninstall-SourceVault.ps1` keeps `K:\SourceVault\vault` and prints the reinstall command. Reinstall with the same `-VaultRoot` and expect the demo project to be authenticated. `-DeleteVaultData` removes `K:\SourceVault` and `C:\ProgramData\SourceVault` only.
8. `Test-VaultTamper.ps1` against the K: vault: codes 11 and 12, then a byte-for-byte restore.
