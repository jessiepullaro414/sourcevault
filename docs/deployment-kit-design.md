# Design: the offline deployment drive (owner request 2026-10-19)

One SATA drive carries everything needed to set up a SourceVault PC with no internet: SourceVault itself, Docker for Windows and for Linux, WSL with Ubuntu, both pinned build images, the scripts, and SourceVault Pack. Encrypted projects travel separately, in two short legs.

Owner answers:
- **Target PC:** a fresh Windows 11 Pro (or Enterprise or Education) PC with a TPM 2.0, fully offline.
- **Vault location:** the installer asks; the default is C:, or a fixed drive such as K:.
- **Projects:** two short legs, below.

## The two legs for projects (why the drive can't carry a ready package for a new PC)

A package opens only with the target PC's TPM import key, and setup creates that key on the target. So:

1. **The drive installs everything.** Setup writes the target's **recipient file**, which is public, to the drive's `transfer\` folder and shows its fingerprint.
2. **The recipient file reaches the source computer by any channel** (drive, email, USB). Pack there encrypts the project.
3. **The `.svpkg` comes back by any channel**, since it is ciphertext. On the target, `Deploy-SourceVault.ps1 -ImportPackages` (or `SourceVault.Import.exe --package`) imports every package in `transfer\`.

For a PC that was set up earlier, step 3 alone works, and the drive can carry its packages from the start.

## What the drive holds

```
SourceVaultKit\
  Deploy-SourceVault.ps1       run this on the target (elevated)
  Test-DeploymentKit.ps1       checks every file against kit-manifest.sha256
  kit-manifest.sha256          SHA-256 of every file on the kit
  kit-sources.json             the pinned sources it was built from
  sourcevault\                 the setup package (Setup-SourceVault.ps1, payload, tools\SourceVault-Pack, samples)
  docker-windows\              docker-<v>.zip (Docker's static Windows binaries) and Microsoft's install-docker-ce.ps1
  wsl\                         the WSL installer (.msi), the Ubuntu 24.04 WSL root file system, docker-<v>.tgz (static Linux binaries)
  images\                      sdk-linux.tar and sdk-windows.tar, the pinned .NET SDK images, in docker-load format
  transfer\                    recipient files out; encrypted packages in
```

The drive is about **4.5 GB**, most of it the Windows image (2.8 GB compressed).

## Pinned sources (`installer/kit-sources.json`)

| Item | Source | Integrity |
|---|---|---|
| Docker for Windows 29.9.1 | `download.docker.com/win/static/stable/x86_64/docker-29.9.1.zip` (49.7 MB) | Docker publishes no checksum. The first build records SHA-256 for review (trust on first use); later builds must match |
| Docker for Linux 29.9.1 | `download.docker.com/linux/static/stable/x86_64/docker-29.9.1.tgz` (86.4 MB) | same |
| Microsoft `install-docker-ce.ps1` | `raw.githubusercontent.com/microsoft/Windows-Containers/Main/helpful_tools/Install-DockerCE/install-docker-ce.ps1` | SHA-256 `09ded921…84e63e`, recorded 2026-10-19 after a full read |
| WSL 3.0.3 | GitHub release `wsl.3.0.3.0.x64.msi` (367.6 MB) | SHA-256 from GitHub's release metadata, `ae977286…91f211` |
| Ubuntu 24.04 WSL | `cloud-images.ubuntu.com/wsl/releases/24.04/20240423/ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz` (356.7 MB) | SHA-256 from Ubuntu's `SHA256SUMS`, `8251e27f…99315ea` |
| .NET SDK image, Linux | `mcr.microsoft.com/dotnet/sdk@sha256:3a48280a…` (amd64 manifest of `10.0.402`) | manifest, config and every layer checked against their digests; image ID `sha256:9cd0d6fa…177d23fd` |
| .NET SDK image, Windows | `mcr.microsoft.com/dotnet/sdk@sha256:c3fad2bd…` (`10.0.402-windowsservercore-ltsc2025`) | same; image ID `sha256:22585ab0…c9a33854` |

**Images without Docker.** The kit builder needs no Docker. It downloads the image manifest, configuration and layers straight from Microsoft's registry and checks each against its digest. It then writes them in both layouts `docker load` reads, as `docker save` does: the legacy `manifest.json` and an OCI `index.json` holding the registry's exact manifest. On the target, the loaded image's ID must equal the pinned configuration digest (Docker's classic image store) or the pinned manifest digest (the containerd image store, Docker 29's default in WSL), so no other image can stand in. Changed 2026-10-20 and unverified until the lab row runs; drives built before that hold only `manifest.json` and must be rebuilt.

## Building the kit (a connected staging PC)

`scripts\Build-DeploymentKit.ps1 -Destination E:\SourceVaultKit`, run as a normal user:
1. builds the setup package (`Build-Installer.ps1`) and copies it;
2. downloads every pinned source, checking sizes and SHA-256; with `-RecordNewHashes`, it records the two Docker archives' hashes on the first build;
3. fetches both images from the registry, checking digests;
4. writes `kit-manifest.sha256`.

It is resumable: files already present with the right hash aren't downloaded again.

## Deploying (the target PC, offline)

`Deploy-SourceVault.ps1`, elevated, runs in phases. It records its progress in `C:\ProgramData\SourceVault-Deploy\state.json`, so after each restart you simply run it again.

1. **Pre-flight:**
   - the edition is Pro, Enterprise or Education; you are an administrator;
   - the TPM is present and ready; virtualization is on in the firmware;
   - there is enough disk space;
   - **every kit file matches `kit-manifest.sha256`**;
   - it asks for the vault location (C: or another fixed drive).
2. **Windows features:** Containers, Microsoft-Hyper-V, Virtual Machine Platform and Windows Subsystem for Linux. Then: restart, and run it again.
3. **Windows Docker engine**, offline:
   - Microsoft's `install-docker-ce.ps1 -DockerPath … -DockerDPath …` installs the `docker` service from the drive's zip;
   - the local `docker` group is created, and `daemon.json` lists it (`"group": "docker"`) next to the npipe host.
4. **WSL and Ubuntu**, offline:
   - the WSL `.msi` is installed;
   - `wsl --import SourceVault-Ubuntu C:\ProgramData\SourceVault-WSL <rootfs>` creates the distro for the deploying user, with systemd on;
   - Docker's static Linux binaries go into `/usr/local/bin`, with systemd units for containerd and Docker;
   - `daemon.json` has no bridge network and no iptables: build containers use no network, so the engine never touches the firewall.
5. **Images:** `docker load` of each tar into its engine; each image ID must equal its pinned configuration digest.
6. **Build containers:** `New-WindowsBuildContainer.ps1 -ImageArchive …` and `New-BuildContainer.ps1 -Distro SourceVault-Ubuntu -ImageArchive …`. With an archive they load and check by image ID instead of pulling. The Windows copy check still decides whether the Windows target works.
7. **SourceVault:**
   - `Setup-SourceVault.ps1 -VaultRoot <chosen> -WithBuilds`;
   - `New-WslDockerTls.ps1`, with its Docker settings applied automatically;
   - `Grant-BuildDockerAccess.ps1 -Group docker`;
   - `build.json` written with both targets;
   - `Setup-SourceVault.ps1 -Update -WithBuilds` and `-Status`.
8. **Transfer:** the recipient file goes to `transfer\recipient-<computer>.json`, its fingerprint is shown, and the next steps are printed. `-ImportPackages` later imports `transfer\*.svpkg`.

## What this does and doesn't protect (be aware)

- **The drive is not encrypted,** and doesn't need to be for secrecy: it holds no source, only tools, public images and ciphertext packages. Turn on BitLocker To Go anyway if the drive also travels with anything else.
- **Integrity of the drive:**
  - `kit-manifest.sha256` catches corruption, or a change made after the kit was built;
  - **it is not signed**, so someone who can rewrite the drive can rewrite the manifest too;
  - signing waits for the signing decision; until then, keep the drive in your custody.
- **Trust on first use:** the Docker archives' hashes are trusted on first use, because Docker publishes none.
- **WSL is per user:** the Linux distro belongs to the account that deployed it, and the Linux target works only while that account's WSL runs (see `files-history-builds-design.md`).
- **An old Ubuntu image:** it is from April 2024, and the target is offline, so it gets no updates. The Linux engine runs no network, and the distro is used only for builds.

## Testable without a target PC

- The pinned-source and image metadata (fetched here).
- The registry image fetch and the docker-load layout, checked with small layers.
- The scripts' syntax.
- The kit manifest check.

Everything that installs software or loads images is an owner lab run, recorded in the lab checklist.
