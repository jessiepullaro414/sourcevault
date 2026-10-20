# Design: building C++ offline (proposal, 2026-10-19; decided in principle)

The owner's code is C++ as well as C#/.NET (next-tasks item 4). SourceVault's two build containers carry only Microsoft's .NET SDK image. **Decision (owner, 2026-10-19): the C++ toolchains come in the owner's own build images.** SourceVault does not install compilers. It carries those images offline, pins each one by image ID, and runs it under its usual rules (no network, an administrator-named container). **Nothing here is built, downloaded or installed yet.** Sizes depend on the owner's images and are measured at the first kit build.

Owner answers (2026-10-19):
- **Windows:** the owner's Windows container has MSVC from Visual Studio 2026, plus the 2019 and 2022 toolsets (`v142`, `v143`), for x86 and x64, with ATL and MFC. The Windows SDK is in the image, and the build scripts choose it. Builds use **MSBuild**.
- **Linux:** a container in WSL, with GCC and all the tools needed. Which GCC version doesn't matter to SourceVault: it is whatever that image carries.
- **Licence:** the owner holds a Visual Studio licence that covers this use.
- **Containers:** shared for C# and C++ where the image carries both (question 1).
- **Third-party libraries:** in the owner's code.
- **C++ runtime:** both static and dynamic linking are used.
- **Linux output** runs only in the owner's own Docker setup.
- **The vault must hold any file a user adds,** not only UTF-8 text (section 5). That is a separate design task.

## 1. The plan: the owner's images, carried and pinned by SourceVault

| | Windows target | Linux target |
|---|---|---|
| Image | The owner's Windows build image: MSVC (`v142`, `v143`, `v145`), MSBuild, ATL/MFC, the Windows SDK | The owner's Linux build image: GCC and the tools its builds use |
| Engine | Microsoft's standalone Docker engine (`\\.\pipe\docker_engine`), as now | Docker Engine in the SourceVault WSL distro, as now |
| Pinned by | **Image ID** (the SHA-256 of the image's configuration), recorded once and committed after review | same |
| On the drive | `images\owner-windows.tar` (`docker save` format) | `images\owner-linux.tar` |
| Container | `sourcevault-build-windows`, created from the pinned ID by `New-WindowsBuildContainer.ps1` | `sourcevault-build-linux`, by `New-BuildContainer.ps1` |
| SourceVault applies | no network, a memory limit, restart with the engine, the copy check | no network, all capabilities dropped, no privilege escalation, process and memory limits, `/tmp` in memory |

**Who is responsible for what.** The owner's image decides which compilers, toolsets, SDKs and libraries exist. SourceVault decides that exactly that image runs: the same ID on every PC, loaded from the drive, never pulled. It also decides how the image runs: no network, no extra rights, and only the administrator's `build.json` naming what runs. SourceVault no longer pins compiler versions itself. The image ID pins all of them at once, because any change to the image gives it a new ID.

### Where the images come from (question 2)
- **Saved from a local engine:** the owner's images live on a connected PC's Docker engines. `Build-DeploymentKit.ps1 -OwnerImages` runs `docker save` against the right engine: the Windows engine for the Windows image, the WSL distro's engine for the Linux one. This is the only kit step that needs Docker on the staging PC.
- **Pulled from a registry by digest:** if the images sit in a registry, the kit fetches them by manifest digest without Docker, as it does for the .NET SDK images (`Save-RegistryImage.ps1`). A private registry needs credentials on the staging PC. They are never written to the drive.

Either way, the kit builder then reads the archive's own `manifest.json` and checks that its configuration digest is the pinned image ID. `Test-DeploymentKit.ps1` repeats that check without Docker.

### Pinning (`installer/kit-sources.json`)
A new `ownerImages` list, one entry per image:

```json
{
  "name": "owner-windows",
  "platform": "windows",
  "path": "images/owner-windows.tar",
  "source": "local",
  "reference": "<the owner's local tag or registry name>",
  "imageId": "sha256:<recorded at the first kit build>",
  "note": "MSVC v142/v143/v145, MSBuild, ATL/MFC, Windows SDK. Owner-built."
}
```

- The first kit build records the `imageId` (`-RecordNewHashes`), as for the Docker archives. The owner then reviews it and commits it.
- From then on, a kit build fails if the image has changed. **Moving to a rebuilt image is a reviewed commit** that changes the ID.
- The owner's own build files for the images (a Dockerfile or script) stay with the owner. Keeping them under version control next to the pinned ID is advisable: it records what the ID stands for.

### On the target (offline)
`Deploy-SourceVault.ps1` phase 5 loads each owner archive into its engine (`docker load`), and the loaded image ID must equal the pinned one. Phase 6 then creates the containers with a new mode in the two scripts:

```
New-WindowsBuildContainer.ps1 -OwnerImageId sha256:… -ImageArchive images\owner-windows.tar [-MemoryGB 8]
New-BuildContainer.ps1 -Distro SourceVault-Ubuntu -OwnerImageId sha256:… -ImageArchive images\owner-linux.tar
```

In that mode each script:
- loads and checks the archive by image ID, then creates the container **from the ID**, never from a tag;
- applies the same container settings as today;
- starts the container with SourceVault's keep-alive as its entry point (`--entrypoint`). The owner's image may define its own `ENTRYPOINT`, which would otherwise wrap or replace the keep-alive;
- skips the .NET SDK version check. Instead it prints the tool versions it finds (`dotnet`, `msbuild`, `cl`, `gcc`, `g++`, `make`, `cmake`, whichever exist), for the lab record;
- keeps the network-mode check and, on Windows, **the copy check**. That check decides whether the build service can work with the image's isolation mode at all;
- labels the container with the image ID and its kit entry's name.

### What the owner's images must satisfy
1. **Everything offline is inside.** Builds run with no network: no NuGet, vcpkg, apt or installer downloads at build time. Anything a build fetches today must be in the image or in the project.
2. **No secrets in the image.** The drive isn't encrypted (deployment-kit-design.md). Registry credentials, signing keys, licence keys and tokens must not be in any layer, including deleted ones: an earlier layer keeps them.
3. **Linux:** has `/bin/sh` and `sleep`, and builds as whatever user the image sets, with **all capabilities dropped** and no privilege escalation. A build that needs `sudo`, `apt` or a mount fails, by design.
4. **Windows: the base OS must suit this host.** The host is Windows 11 25H2 (build 26200). An image based on `ltsc2025` may run with process isolation; one based on `ltsc2022` or older needs Hyper-V isolation. The copy check then decides whether the build service can copy into it. Record which base the image uses (question 3).
5. **Writable paths.** The build service copies the project into `C:\sourcevault\src` (`/sourcevault/src`) and reads `C:\sourcevault\out` (`/sourcevault/out`). The script creates both, so the image only has to allow it.
6. **The .NET SDK, if the image is to build C# too** (question 1). Otherwise SourceVault's own .NET containers stay as separate targets.

### Targets in `build.json`
If the owner's images also carry the .NET SDK, they **replace** SourceVault's .NET images, and `build.json` keeps its two targets, `windows` and `linux`. If they don't, `build.json` lists four targets, each container under its own name:
- `windows` and `linux`: SourceVault's pinned .NET SDK images, as now;
- `windows-cpp` and `linux-cpp`: the owner's images.

The service already supports any number of targets, and operators pick one by name. Each target runs one build at a time, so separate C++ targets also let a C# build and a C++ build run at once.

The Windows `command` stays administrator-set. If the image doesn't put MSBuild on `PATH` itself, the command calls the image's environment script first. For example, if the image has Build Tools under `C:\BuildTools`:

```json
"command": ["cmd.exe", "/d", "/c", "call C:\\BuildTools\\Common7\\Tools\\VsDevCmd.bat -arch=x64 >nul && build.cmd"]
```

MSBuild then takes the architecture (`/p:Platform=Win32` or `x64`), toolset (`PlatformToolset`) and SDK (`WindowsTargetPlatformVersion`) from the project and its build scripts, as the owner's builds already do.

## 2. What the drive carries, and size

```
SourceVaultKit\
  images\
    sdk-linux.tar, sdk-windows.tar     SourceVault's pinned .NET SDK images (as now; left out if the owner's replace them)
    owner-windows.tar                  the owner's Windows build image
    owner-linux.tar                    the owner's Linux build image
```

- `kit-manifest.sha256` covers the new archives like every other kit file.
- `-SkipOwnerImages` builds a kit without them, as today.

**Size:** unknown until measured. A Windows image with three MSVC toolsets, ATL/MFC for two architectures and a Windows SDK is commonly **15–30 GB uncompressed**. Its `docker save` archive is about the same size, because the archive isn't compressed. That would take the drive from about 4.5 GB to roughly **20–40 GB**. The kit builder prints the real size. A drive of 64 GB or more leaves room for a second image during an upgrade.

## 3. Licences

- **Visual Studio Build Tools: covered (owner, 2026-10-19).** The terms still restrict redistribution, and the owner's Windows image contains the Build Tools. So:
  - keep the drive in the owner's custody, and use it only for the owner's own PCs;
  - never push that image to a public registry;
  - don't give the drive to anyone else.
- **Dynamic runtime (owner: used).** Programs linked with `/MD`, or with MFC as a shared library, need the VC++ runtime and MFC DLLs where they run. Either Microsoft's redistributable is installed there, or the build copies the DLLs next to the program (from the Build Tools' `VC\Redist\MSVC\<version>` folder). Either way, only files on Microsoft's redistribution list may be shipped.
- **The Windows base image** is licensed for use on a properly licensed Windows host, as for the .NET SDK image.
- **The Linux image:** GCC's runtime exception means programs it compiles carry no GPL obligation. Keeping GPL binaries on a drive for the owner's own PCs triggers no obligation. Giving the drive away would mean distributing them, which carries a duty to offer their source.
- **SourceVault itself** stays MIT. None of these tools is linked into it.

## 4. A fictional sample: `installer/samples/cpp-hello`

Synthetic code, for testing imports and builds on the owner's images, as `dotnet-hello` does for the .NET images. Windows builds it with MSBuild, for Win32 and x64. Linux builds it with plain `g++`.

```
cpp-hello\
  CppHello.vcxproj
  build.cmd
  build.sh
  src\main.cpp
  src\greeting.h
  src\greeting.cpp
```

`CppHello.vcxproj` (UTF-8). It deliberately names no `WindowsTargetPlatformVersion`, so MSBuild uses the newest SDK in the image:
```xml
<?xml version="1.0" encoding="utf-8"?>
<!-- SourceVault synthetic sample: fictional code for testing imports and builds. Not real source. -->
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <ItemGroup Label="ProjectConfigurations">
    <ProjectConfiguration Include="Release|Win32"><Configuration>Release</Configuration><Platform>Win32</Platform></ProjectConfiguration>
    <ProjectConfiguration Include="Release|x64"><Configuration>Release</Configuration><Platform>x64</Platform></ProjectConfiguration>
  </ItemGroup>
  <PropertyGroup Label="Globals"><RootNamespace>CppHello</RootNamespace></PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.Default.props" />
  <PropertyGroup Label="Configuration">
    <ConfigurationType>Application</ConfigurationType>
    <PlatformToolset>v145</PlatformToolset>
    <CharacterSet>Unicode</CharacterSet>
  </PropertyGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.props" />
  <ItemDefinitionGroup>
    <ClCompile>
      <!-- Static runtime, so the .exe needs no redistributable DLLs. -->
      <RuntimeLibrary>MultiThreaded</RuntimeLibrary>
      <LanguageStandard>stdcpp20</LanguageStandard>
      <AdditionalOptions>/utf-8 %(AdditionalOptions)</AdditionalOptions>
      <PreprocessorDefinitions>NDEBUG;%(PreprocessorDefinitions)</PreprocessorDefinitions>
    </ClCompile>
    <Link><SubSystem>Console</SubSystem></Link>
  </ItemDefinitionGroup>
  <ItemGroup>
    <ClCompile Include="src\main.cpp" />
    <ClCompile Include="src\greeting.cpp" />
    <ClInclude Include="src\greeting.h" />
  </ItemGroup>
  <Import Project="$(VCTargetsPath)\Microsoft.Cpp.targets" />
</Project>
```

`src/greeting.h`, `src/greeting.cpp` and `src/main.cpp`:
```cpp
// greeting.h — SourceVault synthetic sample. Not real source.
#pragma once
#include <string>
std::string greeting(const char* name);

// greeting.cpp
#include "greeting.h"
std::string greeting(const char* name) {
    return name ? "Hello, " + std::string(name) : "Hello from a SourceVault synthetic C++ sample";
}

// main.cpp
#include <iostream>
#include "greeting.h"
int main(int argc, char** argv) {
    std::cout << greeting(argc > 1 ? argv[1] : nullptr) << '\n';
    return 0;
}
```

`build.cmd` (Windows target; the `build.json` command has already put MSBuild on `PATH`):
```bat
@echo off
rem SourceVault synthetic sample, Windows target. Whatever this writes to C:\sourcevault\out is published
rem (.obj, .pdb, .ilk and similar files are left out). Intermediates stay in the project folder, which the
rem build service empties after the build; only the two programs are copied out.
msbuild CppHello.vcxproj /p:Configuration=Release /p:Platform=Win32 /p:OutDir=%CD%\build\x86\ /m /nologo
if errorlevel 1 exit /b 1
msbuild CppHello.vcxproj /p:Configuration=Release /p:Platform=x64 /p:OutDir=%CD%\build\x64\ /m /nologo
if errorlevel 1 exit /b 1
mkdir C:\sourcevault\out\x86 C:\sourcevault\out\x64
copy /y build\x86\CppHello.exe C:\sourcevault\out\x86\ >nul || exit /b 1
copy /y build\x64\CppHello.exe C:\sourcevault\out\x64\ >nul || exit /b 1
```

`build.sh` (Linux target; runs in `/sourcevault/src` with no network):
```sh
#!/bin/sh
# SourceVault synthetic sample, Linux target. Whatever this writes to /sourcevault/out is published
# (.o, .debug and similar files are left out).
set -eu
g++ -std=c++20 -O2 -DNDEBUG -o /sourcevault/out/cpp-hello src/main.cpp src/greeting.cpp
strip /sourcevault/out/cpp-hello
```

Expected output: `x86\CppHello.exe` and `x64\CppHello.exe` (Windows), and `cpp-hello` (Linux), and nothing else. Copying only finished programs matters more for C++ than for .NET: a C++ build folder is full of intermediates. A real project with the dynamic runtime also copies its redistributable DLLs (section 3).

## 5. The vault must hold any file (owner, 2026-10-19; separate design task)

> **Update 2026-10-20:** designed, approved and built in [vault-any-file-design.md](vault-any-file-design.md) (merged with PR #2). This section is the original problem statement.

Today the vault holds **strict UTF-8 text only: at most 256 KiB per file and 1,024 files per project** (`revision-store.md`). The importer and Pack skip everything else and list it. A build sees only what is in the vault, so real C++ code would fail to build:
- `.rc` files that Visual Studio saved as UTF-16;
- icons, bitmaps and other binary resources;
- sources in a Windows code page;
- vendored libraries with thousands of files or prebuilt `.lib`/`.dll` files.

**Owner decision: the vault must hold other files as well, because users can add anything.** Converting files or moving libraries into the images is therefore not the fix. The vault itself changes. That is a storage and protocol change, and AGENTS.md requires the coordinator's review for it. So it is its own design, not part of this one. What it has to cover:
- **Storage:** binary content, larger files and more files per project. The record format and manifest limits; whether large files are split into chunks; the cost of TPM-wrapped keys per record at that scale (`store-performance-design.md`).
- **Protocol:** `OpenDocument`/`ApplyEdits` carry text in 1 MiB frames. Files larger than that need chunked transfer, or a separate path for content that isn't edited.
- **Editor:** a binary or oversized file is listed, can be renamed, deleted and replaced, but isn't opened in Monaco. History shows its versions without a text diff, and merge offers "keep mine" or "keep theirs".
- **Search:** skips binary files.
- **Importer and Pack:** take every file, with only the folder exclusions (`.git`, `bin`, `obj`). The package format moves to v2, which the coordinator also reviews.
- **Builds:** unchanged. The tar copy already carries bytes.
- **Limits:** new per-file, per-project and total bounds, each with a reason. "Any file" still needs an upper bound to keep memory and frame sizes bounded.

## 6. What this does and doesn't protect (be aware)

Everything in `files-history-builds-design.md` section 4 still applies: builds are an export path, and plaintext source sits in long-lived containers. Owner images add:
- **SourceVault vouches only for the image ID, not its contents.** Whoever builds the owner's image decides what runs in every build. Treat changing the image like changing the build scripts: a reviewed commit.
- **More leftovers inside the container.** MSVC and MSBuild write to `%TEMP%` inside the Windows container. That folder is on the container's disk, not in memory, and the build service does not empty it. The `build.json` command can point `TEMP` and `TMP` into the project folder, so they are emptied with it; that is a lab check. Compiler caches (ccache, sccache) in the owner's images would keep source-derived data between builds: they should be off.
- **Compiled C++ is harder to read than IL, but it does not conceal anything.** Strings, `assert` expressions and `__FILE__` paths go into the binary.
- **The artifact filter's list is not C++-complete.** It drops `.obj`, `.o`, `.ilk`, `.iobj`, `.ipdb`, `.pdb` and the debug-symbol types (`Artifacts.cs`). It does not drop:
  - `.pch`, `.ipch`, `.idb`, `.tlog`, `.exp`, `.res`: adding them is a coordinator decision;
  - `.lib` and `.a`: static libraries can be wanted outputs, so they should stay.

  Copying only finished outputs, as the sample does, keeps the rest out.
- **MSBuild runs project code.** A `.vcxproj` can run any command, just as `build.cmd` can.
- **Resources:** MSBuild with `/m` uses a lot of memory, so give the Windows target at least 8 GB. Building x86 and x64 may need `timeoutSeconds` raised toward its 3,600 maximum.

## 7. Testable without a target PC (development)

- Reading a `docker save` archive's `manifest.json` and matching its configuration digest to the pinned ID, on small synthetic archives.
- The new `kit-sources.json` fields, checked in `Test-DeploymentKit.ps1`.
- The scripts' syntax.

Saving, loading and running the owner's images, and building in them, are owner lab runs.

## 8. Lab steps (unverified; for docs/lab-checklist.md once the scripts exist)

| | Step | Needs |
|---|---|---|
| [ ] | Owner: confirm the images contain no credentials or keys in any layer (`docker history --no-trunc`, and the owner's Dockerfile) | — |
| [ ] | Staging PC: `Build-DeploymentKit.ps1 -Destination … -OwnerImages -RecordNewHashes`. Record each image's ID and archive size; review and commit `ownerImages` in `kit-sources.json` | Docker on the staging PC |
| [ ] | Elevated `New-WindowsBuildContainer.ps1 -OwnerImageId … -ImageArchive images\owner-windows.tar -MemoryGB 8`. Record the base OS, **the isolation mode and the copy check**, and the tool versions it prints | admin |
| [ ] | `New-BuildContainer.ps1 -Distro SourceVault-Ubuntu -OwnerImageId … -ImageArchive images\owner-linux.tar`. Record the tool versions; the container runs with all capabilities dropped | — |
| [ ] | Write the targets into `build.json` (two or four, question 1); `Setup-SourceVault.ps1 -Update -WithBuilds`, `-Status` | admin |
| [ ] | Import `samples\cpp-hello`. **Build** on Windows: the output has `x86\CppHello.exe` and `x64\CppHello.exe` only, and both run on the host with no VC++ runtime installed. **Build** on Linux: the output has `cpp-hello` only, and it runs in the owner's Linux run-time container | — |
| [ ] | `samples\dotnet-hello` still builds on whichever targets carry .NET | — |
| [ ] | After a Windows build, inside the container: `%TEMP%` holds nothing from the build. Record what it holds | admin |
| [ ] | Deploy a fresh PC from the kit (the existing "Offline deployment drive" rows): both C++ builds work with no internet | fresh PC; restarts |

## Questions for the owner

Answered on 2026-10-19: see the top of this note. Still open:

1. **Do your images also carry the .NET SDK?** If yes, they replace SourceVault's .NET images, with two targets. If not, there are four targets (section 1).
2. **Where are the images now:** on a local engine (saved with `docker save`), or in a registry (fetched by digest)?
3. **Which Windows base** does the Windows image use (`ltsc2022`, `ltsc2025`, …)? It decides the isolation mode, and so whether the copy check can pass on this PC.
4. **Is the Linux image's WSL engine** the SourceVault distro's Docker Engine, or another distro's? The build service reaches only one Linux engine per target, over TLS (`New-WslDockerTls.ps1`).

## Appendix: SourceVault building the toolchain images itself (not the current plan)

The first version of this note (commits d1f1e63 and f893f14) had SourceVault build the C++ images itself, from pinned inputs. That route is kept for reference, in case the owner's images can't be used:
- **Windows:** the inputs were a fixed-version Visual Studio Build Tools bootstrapper and an offline layout, with a SHA-256 manifest of every file.
- **Linux:** they were Ubuntu 24.04 `.deb` files from a dated `snapshot.ubuntu.com` index, checked against Ubuntu's signature.
- **Both:** each was installed into the pinned .NET SDK image with no network, and saved with `docker commit`.
