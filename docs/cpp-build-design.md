# Design: building C++ offline (proposal, 2026-10-19; mostly decided)

The owner's code is C++ as well as C#/.NET (next-tasks item 4). Both build containers carry only Microsoft's .NET SDK image, so C++ needs a pinned toolchain in each. This note proposes how to add one. **Nothing here is built, downloaded or installed.** Versions marked *to pin* are fixed once the open questions at the end are answered. Sizes are estimates until the first kit build measures them.

Owner answers (2026-10-19):
- **Linux compiler: GCC** only, and it is the default. No Clang.
- **MSVC: Visual Studio 2026.** The code also supports the Visual Studio 2022 and 2019 compilers. Whether the container must carry those older toolsets too is open (question A).
- **Windows architectures: x86 and x64, with ATL and MFC.** No ARM64, no Clang-cl.
- **Windows build system: MSBuild** (`.sln`/`.vcxproj`). No CMake on Windows.
- **Licence:** the owner holds a Visual Studio licence that covers this use.
- **Containers:** shared. C++ goes into the existing two containers, next to .NET.
- **Third-party libraries:** used, and already in the owner's code. No package manager download is needed. But see section 5: they must fit the vault's limits.
- **C++ runtime:** both static and dynamic linking are used.
- **Linux output** runs only in the owner's own Docker setup, not on older distributions.

## Summary

| | Windows target | Linux target |
|---|---|---|
| Compiler | MSVC from **Visual Studio 2026 Build Tools**, x86 and x64, with ATL and MFC; the 2022 and 2019 toolsets if question A says so | **GCC**, from Ubuntu 24.04's own packages |
| Build system | **MSBuild**, part of the Build Tools | open (question B): `make`, CMake or plain `g++` |
| SDK | One Windows SDK (Windows 11 SDK, *to pin*) | glibc and libstdc++ as shipped in the image |
| Base | The pinned .NET SDK Windows Server Core image already on the drive | The pinned .NET SDK Linux image already on the drive |
| Pinned by | The SHA-256 and Microsoft signature of a **fixed-version** bootstrapper, plus the SHA-256 of every file in an offline **layout** | The SHA-256 of every `.deb`, taken from a dated Ubuntu snapshot whose signed index lists them |
| Comes onto the drive as | `toolchains\msvc-layout\` | `toolchains\linux-cpp\*.deb` |
| Image made | On the target, offline, by the container script: run the base image with the toolchain folder mounted read-only, install, `docker commit` | same |

**One container per platform, not one per language (owner, 2026-10-19).** C++ is added to the existing `sourcevault-build-windows` and `sourcevault-build-linux` containers. A project with a C# part and a C++ part builds both in one `build.cmd` or `build.sh`, and `build.json` keeps its two targets.

## 1. Windows: MSVC Build Tools on Windows Server Core

### Which product
The **Visual Studio Build Tools** are Microsoft's command-line edition of the C++ compiler, linker, MSBuild and the Windows SDK, without the IDE. They are the only supported way to get MSVC into a container: Microsoft documents installing them into a `windowsservercore` image (Nano Server cannot hold them).

**Release (owner): Visual Studio 2026 Build Tools.** Its toolset is, as we understand it, `v145` (MSVC 14.5x). The exact 18.x release is *to pin*.

**Older toolsets.** The code also supports Visual Studio 2022 (`v143`, MSVC 14.3x–14.44) and 2019 (`v142`, MSVC 14.29). With MSBuild, what matters is the `PlatformToolset` each `.vcxproj` names:
- If a project names `v142` or `v143` and only `v145` is installed, MSBuild stops with error MSB8020. Installing those toolsets avoids it.
- So does retargeting the projects to `v145`, which is the owner's change to make, not the build's.

If they are needed (question A), they are installed as extra components of the same 2026 layout. Whether the 2026 installer still offers both is checked against its component list when pinning. If it doesn't, a second, pinned Visual Studio 2022 Build Tools layout supplies them; that one offers `v142` too.

### Components
Exact IDs are *to pin* from the chosen release's component list; Microsoft renames some between major versions.
- `Microsoft.VisualStudio.Workload.VCTools`: the C++ build tools workload, including MSBuild;
- `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`: the latest compiler and linker, for **x86 (`Win32`) and x64** (one component covers both);
- `Microsoft.VisualStudio.Component.VC.ATL` and `Microsoft.VisualStudio.Component.VC.ATLMFC`: **ATL and MFC** for the latest toolset, x86 and x64, as static and shared libraries;
- `Microsoft.VisualStudio.Component.Windows11SDK.<build>`: **one** Windows SDK. Projects that name a different `WindowsTargetPlatformVersion` need that SDK too (question C);
- only if question A says so: the `v143` and `v142` x86/x64 toolsets, each with its own ATL and MFC components. Examples are `…VC.v142.x86.x64`, `…VC.v142.ATL` and `…VC.v142.MFC` (IDs *to pin*). Without its own MFC component, an older toolset can't build MFC code.

Components are named one by one, not with `--includeRecommended`. That keeps the image smaller and the list reviewable, and a later release cannot add a component silently. ARM64, Clang-cl, CMake and Spectre-mitigated libraries are left out.

### How it is pinned
1. **The bootstrapper.** Microsoft's evergreen link (`aka.ms/vs/…/vs_buildtools.exe`) always installs the newest release, so it is **not** used. Each release has a *fixed-version* bootstrapper on Microsoft's release-history page. The kit records its URL, size and SHA-256 in `kit-sources.json`, and checks that its Authenticode signature chains to Microsoft. The hash is trust on first use, as for the Docker archives, but backed by the signature.
2. **The layout.** On the connected staging PC, the kit builder runs that bootstrapper with `--layout <kit>\toolchains\msvc-layout --add <components> --lang en-US`. The layout is a folder holding the installer, its catalog and every package. Microsoft signs the packages, and the catalog lists them with hashes.
3. **The layout manifest.** The kit builder writes the SHA-256 of every file in the layout into `installer\toolchains\msvc-layout.sha256`. The first build records it (`-RecordNewHashes`), and it is committed after review. A later kit build must reproduce the same files, or it fails. Files the bootstrapper generates locally, such as its log, are excluded. Whether anything else differs between two layout runs is a lab check.
4. **On the target.** The deploy step checks the layout against `kit-manifest.sha256`, as for every kit file. It then installs with `--noWeb`, so the installer uses only the layout and never tries the network. The container has no network anyway.

### How the image is made (on the target, offline)
`New-WindowsBuildContainer.ps1` gains `-CppLayout <kit>\toolchains\msvc-layout` (name *to choose*). It:
1. loads and checks the pinned .NET SDK image by image ID, as now;
2. runs a temporary container from it with:
   - `--network none`;
   - at least 8 GB of memory;
   - enough container disk for the ATL/MFC libraries. Windows containers default to a 20 GB disk; whether that is enough is a lab check;
   - the layout **mounted read-only** (`-v <layout>:C:\layout:ro`);
   - the isolation mode the existing copy check settled on;
3. inside it, runs `C:\layout\vs_buildtools.exe --noWeb --quiet --wait --norestart --nocache --installPath C:\BuildTools --add …`. Exit code 3010 ("restart needed") counts as success, as Microsoft's container guide says;
4. `docker commit`s it as `sourcevault/build-windows:<kit version>`, labelled with the base image ID and the layout manifest's hash, then removes the temporary container;
5. creates `sourcevault-build-windows` from that image with the same settings as today. It records the committed image ID in the container's label and the script's output.

**Why run-and-commit, not a Dockerfile.** A Dockerfile would have to `COPY` the multi-gigabyte layout into a layer, and deleting it in a later step still leaves it in the image. A read-only mount keeps it out. The committed image's ID cannot be known in advance, because it contains timestamps. So the pin is on the **inputs** (the base image ID and the layout manifest), and the result is recorded when it is created.

### The compiler environment
MSBuild must be on `PATH`, and Microsoft's environment script puts it there. The image gets one fixed wrapper, `C:\sourcevault\bin\build-env.cmd`:

```bat
@echo off
rem Puts MSBuild and the latest x64 tools on PATH, then runs the project's build.cmd.
call "C:\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul || exit /b 1
build.cmd
```

The Windows target's `command` becomes `["cmd.exe", "/d", "/c", "C:\\sourcevault\\bin\\build-env.cmd"]`:
- it stays in the administrator-only `build.json`, so operators still cannot change what runs;
- projects need not know where the tools are installed;
- a .NET-only `build.cmd` is unaffected.

**x86, x64 and toolsets** need nothing more. MSBuild takes the architecture from `/p:Platform=Win32` or `x64`, and the toolset from each project's `PlatformToolset`, whatever environment the wrapper entered.

### The check
The script's existing checks stay: network mode none, the SDK version, the copy check, and the .NET test build. It adds, all offline:
- MSBuild and `cl` report the pinned versions, for each installed toolset;
- a fictional console `.vcxproj` builds as **Win32 and x64**, with the static runtime (`/MT`) and the dynamic one (`/MD`), and runs;
- a fictional MFC/ATL file (`CString` and `CComPtr`, nothing more) builds for both architectures, with MFC as a static and as a shared library, for each installed toolset.

## 2. Linux: GCC in the WSL Docker container

### Which packages
The pinned .NET SDK Linux image (tag `10.0.402`) is, as far as we know, built on Ubuntu 24.04. The script confirms that from `/etc/os-release`, and the plan changes if it is not. So the toolchain comes from **Ubuntu 24.04's own packages**, built and signed by Ubuntu, and matching the image's own C library:
- **GCC (owner):** `g++` (GCC 13 in 24.04; `g++-14` is also packaged, question D);
- the build system the Linux part uses (question B): `make`, and `cmake` (3.28) with `ninja-build` (1.11) only if it uses CMake. MSBuild cannot build C++ on Linux. Visual Studio's "Linux" `.vcxproj` projects compile on a remote machine over SSH, which a container with no network cannot offer.

Versions are *to pin* when the snapshot date is chosen.

### How it is pinned
1. **A dated snapshot.** Ubuntu's archive changes daily, so the kit uses **`snapshot.ubuntu.com`** at a fixed timestamp (*to pin*). The same date always gives the same packages.
2. **The package list.** A helper on the staging PC asks apt, inside the staging WSL Ubuntu, which `.deb` files the chosen packages need on top of the SDK image. That is the full dependency closure, minus what the image already has. It writes `installer\toolchains\linux-cpp.json`, with each package's name, version, snapshot URL, size and SHA-256, **taken from the snapshot's `Packages` index**.
3. **Ubuntu's signature.** That index is covered by the snapshot's `InRelease` file, which Ubuntu signs. The helper checks the signature with `gpgv` against Ubuntu's archive keyring in the staging distro. The JSON is then reviewed and committed. Unlike the Docker archives, this is not trust on first use: every hash traces back to Ubuntu's signature.
4. **The kit builder** downloads exactly those files and checks their size and SHA-256, as for every other pinned source. It needs no Docker and no apt.

### How the image is made (on the target, offline)
`New-BuildContainer.ps1` gains `-CppPackages <kit>\toolchains\linux-cpp` (name *to choose*). Inside the distro it:
1. loads and checks the pinned .NET SDK image by image ID, as now;
2. runs a temporary container from it with `--network none` and the package folder mounted read-only, and runs `apt-get install -y --no-install-recommends /debs/*.deb`. With no network, apt can only use those files: a missing dependency fails the install instead of fetching anything;
3. removes apt's lists and caches, `docker commit`s the container as `sourcevault/build-linux:<kit version>` with the same labels as on Windows, and removes the temporary container;
4. creates `sourcevault-build-linux` from that image with the same settings as today (no network, capabilities dropped, `/tmp` in memory), with `CC=gcc` and `CXX=g++`;
5. checks `g++ -dumpfullversion` (and `cmake --version` if installed), and that a fictional `hello.cpp` builds and runs offline, linked both dynamically and with `-static-libstdc++ -static-libgcc`.

**Where the output runs (owner):** only in the owner's own Docker setup. Programs built here need glibc 2.39 or newer at run time, so the owner's run-time image should be Ubuntu 24.04 (or newer, or the same base). An older run-time image would fail to start them.

## 3. What the drive carries

```
SourceVaultKit\
  toolchains\
    msvc-layout\          the Build Tools layout (bootstrapper, catalog, packages); about 4–8 GB with ATL/MFC, estimate
    linux-cpp\            the .deb files listed in linux-cpp.json; about 0.1–0.2 GB, estimate
```

- **`kit-sources.json`** gains a `toolchains` section with:
  - the fixed-version bootstrapper (URL, size, SHA-256, expected signer);
  - the component list;
  - the snapshot timestamp;
  - pointers to `msvc-layout.sha256` and `linux-cpp.json`.

  Changing any of them is a reviewed commit, as for the SDK images.
- **`Build-DeploymentKit.ps1`** gains a toolchain step after the images:
  - it runs the bootstrapper's `--layout`, checks the layout against its manifest, and downloads the `.deb` files;
  - the layout step runs a Microsoft executable on the staging PC, and may need elevation (a lab check);
  - `-SkipToolchains` builds a .NET-only kit, as today;
  - the step can resume, like the rest of the kit.
- **`kit-manifest.sha256`** covers the new files like every other kit file, so `Test-DeploymentKit.ps1` and the deploy pre-flight check them unchanged.
- **`Deploy-SourceVault.ps1`** phase 6 passes `-CppLayout` and `-CppPackages` to the two container scripts when the kit has them.
- **A PC already set up** without the drive gets C++ by pointing the two scripts at a kit's `toolchains\` folder. There is deliberately no online "install C++ from the internet" path: every route goes through the pinned, hashed files.

**Size.** The drive grows from about 4.5 GB to roughly **9–13 GB**, nearly all of it the MSVC layout. ATL and MFC for two architectures, and each extra toolset, are a large part of it. On the target, the Windows image grows by roughly **8–15 GB** and the Linux image by **0.3–0.6 GB**. These are estimates; the kit builder prints the real figures, which then replace them.

## 4. Licences

- **Visual Studio Build Tools: covered (owner, 2026-10-19).** The owner holds a Visual Studio licence for this use. The terms still restrict redistribution, so:
  - keep the drive in the owner's custody, installing only onto the owner's own PCs;
  - never push the committed build image to a registry;
  - don't give the drive to anyone else.
- **The Windows SDK** comes under its own Microsoft licence inside the layout, shown by the same installer.
- **Dynamic runtime (owner: used).** Programs linked with `/MD`, or with MFC as a shared library, need the VC++ runtime and MFC DLLs where they run. Two ways:
  - the run-time machine has Microsoft's VC++ redistributable installed;
  - or the build copies the DLLs next to the program, from `C:\BuildTools\VC\Redist\MSVC\<version>\` (app-local deployment).

  Either way, only the files on Microsoft's redistribution list may be shipped. Static linking (`/MT`, MFC as a static library) has no DLLs to ship.
- **The Windows Server Core base image** is already accepted for the .NET SDK image: Microsoft licenses it for use on a properly licensed Windows host.
- **Linux:**
  - GCC is GPLv3 with the GCC Runtime Library Exception, so programs it compiles carry no GPL obligation, including when they link `libstdc++` statically;
  - make is GPLv3, CMake BSD-3-Clause, Ninja Apache 2.0, glibc LGPL;
  - keeping the `.deb` files on a drive for the owner's own PCs triggers no obligation. Giving the drive to someone else would mean distributing GPL binaries, which carries a duty to offer their source.
- **Third-party libraries in the owner's code** keep their own licences. Nothing here changes them.
- **SourceVault itself** stays MIT. None of these tools is linked into it.

## 5. Fit with the vault's limits (blocking for real MFC projects; needs a decision)

The vault holds **strict UTF-8 text only: at most 256 KiB per file and 1,024 files per project** (`revision-store.md`). The importer and Pack skip everything else, plus `bin`, `obj` and similar folders, and list what they skipped. A build sees only what is in the vault. Typical Windows C++ code breaks these rules in four ways, and each one makes a build fail:

| What | Why it's skipped | What to do |
|---|---|---|
| **`.rc` resource scripts.** Visual Studio saves them as UTF-16 by default | not UTF-8 | Save them as UTF-8 with `#pragma code_page(65001)`. The resource compiler supports it |
| **Icons, bitmaps, cursors and other binary resources** (`.ico`, `.bmp`, `.cur`, `.png`). MFC apps nearly always have them | binary | No route today. Needs binary files in the vault: a storage format change, which needs the coordinator's review |
| **Sources saved in a Windows code page** (for example Windows-1252, often with accented characters in comments) | not UTF-8 | Convert them to UTF-8, and compile with `/utf-8` |
| **Vendored third-party libraries** (owner: "it's there"). Boost alone has many thousands of headers, and prebuilt `.lib`/`.dll` files are binary | over 1,024 files, binary, or over 256 KiB | Carry public libraries **in the build image**, not the vault (below), or raise the limits (a format change) |

**Proposal for third-party libraries.** They are public code, not the owner's secret. So they can come through the same pinned route as the toolchain:
- each library at a fixed version, with SHA-256s, in `toolchains\libs\`;
- installed read-only into the build images at `C:\sourcevault\libs` and `/sourcevault/libs`;
- projects find them through an include path (an MSBuild property sheet the image provides, or an environment variable).

The vault then holds only the owner's own code. Any library the owner has **modified** is the owner's source and stays in the vault.

**What the owner can check alone:** run the importer's scan (or Pack) on the real code. It lists every file it would skip, and why. A short list means a few conversions; a long one means the format change or the library route first. Development stays on synthetic code, as AGENTS.md requires.

## 6. A fictional sample: `installer/samples/cpp-hello`

Synthetic code, for testing imports and builds, as with `dotnet-hello`. Windows builds it with MSBuild, for Win32 and x64. The Linux script below uses plain `g++` until question B is answered.

```
cpp-hello\
  CppHello.vcxproj
  build.cmd
  build.sh
  src\main.cpp
  src\greeting.h
  src\greeting.cpp
```

`CppHello.vcxproj` (UTF-8):
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

`build.cmd` (Windows target; `build-env.cmd` has already put MSBuild on `PATH`):
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

Expected output: `x86\CppHello.exe` and `x64\CppHello.exe` (Windows), and `cpp-hello` (Linux), and nothing else. Copying only the finished programs matters more for C++ than for .NET: a C++ build folder is full of intermediates. A real project with the dynamic runtime also copies its redistributable DLLs (section 4).

## 7. What this does and doesn't protect (be aware)

Everything in `files-history-builds-design.md` section 4 still applies: builds are an export path, and plaintext source sits in long-lived containers. C++ adds:
- **Compiled C++ is harder to read than IL, but it does not conceal anything.** Strings, `assert` expressions and `__FILE__` paths go into the binary, and disassembly is routine. Release builds with `NDEBUG`, stripped output and dropped `.pdb` files reduce what leaks; they do not prevent it.
- **More leftovers inside the container.** MSVC and MSBuild write to `%TEMP%` inside the container (`C:\Users\ContainerAdministrator\AppData\Local\Temp`). That folder is on the container's disk, not in memory, and the build service does not empty it. `build-env.cmd` can point `TEMP` and `TMP` into the project folder, so they are emptied with it; that is a lab check. MSBuild node reuse is already off (`MSBUILDDISABLENODEREUSE=1`). On Linux, `/tmp` is already in memory. **No compiler cache** (ccache, sccache) is installed: it would keep source-derived data between builds by design.
- **The artifact filter's list is not C++-complete.** It drops `.obj`, `.o`, `.ilk`, `.iobj`, `.ipdb`, `.pdb` and the debug-symbol types (`Artifacts.cs`). It does not drop:
  - `.pch`, `.ipch`, `.idb`, `.tlog`, `.exp`, `.res`: adding them is a coordinator decision;
  - `.lib` and `.a`: static libraries can be wanted outputs, so they should stay.

  Until the list changes, copying only finished outputs (as the sample does) keeps the rest out.
- **MSBuild runs project code.** A `.vcxproj` can run any command (`Exec` tasks, custom build steps), just as `build.cmd` can. That gives a project no new power, but reviewers should read project files like scripts.
- **Resources.** MSBuild with `/m` builds in parallel and uses a lot of memory:
  - the Windows target should get at least 8 GB (`-MemoryGB 8`);
  - building x86 and x64 doubles the time, so large projects may need `timeoutSeconds` raised toward its 3,600 maximum.

## 8. Testable without a target PC (development)

- The fixed-version bootstrapper's URL, size, hash and signer (metadata only).
- The snapshot's `InRelease` signature, `Packages` hashes and the dependency closure, computed in the staging WSL distro; nothing installed.
- The scripts' syntax, and checks for the new `kit-sources.json` fields in `Test-DeploymentKit.ps1`.
- The sample's import: every file passes the vault's rules (UTF-8, size, count).

Everything that downloads the toolchains, makes a layout, installs into a container or builds there is an owner lab run.

## 9. Lab steps (unverified; for docs/lab-checklist.md once the scripts exist)

These go into the lab checklist when the scripts are written. They are listed here so the owner can see the cost now.

| | Step | Needs |
|---|---|---|
| [ ] | Owner runs the importer's scan (or Pack) on the real code and notes what it would skip (section 5) | — |
| [ ] | Staging PC: `Build-DeploymentKit.ps1 -Destination <drive>\SourceVaultKit -RecordNewHashes` with toolchains. Record the layout size, check that the bootstrapper's signer is Microsoft, and review and commit `msvc-layout.sha256` | download, about 5–9 GB extra; possibly admin |
| [ ] | Run the kit build a second time into an empty folder: the layout must match `msvc-layout.sha256`, which proves it is reproducible | download |
| [ ] | Staging WSL: generate `linux-cpp.json`; the `InRelease` signature checks against Ubuntu's keyring; review and commit it | download (metadata) |
| [ ] | Elevated `New-WindowsBuildContainer.ps1 -Replace -MemoryGB 8 -ImageArchive … -CppLayout …`. Record: the isolation mode; the install's exit code; the image size; whether the container disk was large enough; and that the .NET and C++ test builds ran with no network (Win32 and x64, `/MT` and `/MD`, MFC static and shared, each toolset) | admin; about 30–60 min |
| [ ] | `New-BuildContainer.ps1 -Distro … -Replace -ImageArchive … -CppPackages …`. Record the image size and the GCC version. The test program builds, dynamic and static | — |
| [ ] | Update `build.json`'s Windows `command` to `build-env.cmd`; `Setup-SourceVault.ps1 -Update -WithBuilds`, `-Status` | admin |
| [ ] | Import `samples\cpp-hello`. **Build** on Windows: the output has `x86\CppHello.exe` and `x64\CppHello.exe` only, and both run on the host with no VC++ runtime installed. **Build** on Linux: the output has `cpp-hello` only, and it runs in an Ubuntu 24.04 container | — |
| [ ] | Rebuild `samples\dotnet-hello` on both targets: the results are unchanged (the C++ tools must not break .NET builds) | — |
| [ ] | After a Windows build, inside the container: `%TEMP%` holds nothing from the build. Record what it holds | admin |
| [ ] | Deploy a fresh PC from the kit with toolchains (the existing "Offline deployment drive" rows): both C++ builds work with no internet | fresh PC; restarts |

## Questions for the owner

Answered on 2026-10-19: the compiler on Linux, the MSVC release, the architectures and ATL/MFC, MSBuild, the licence, shared containers, libraries in the code, the runtime, and where Linux output runs (see the top). Still open:

- **A. Older toolsets.** Which `PlatformToolset` values do the real `.vcxproj` files name (`v142`, `v143`, `v145`)? Must the container carry the 2019 and 2022 toolsets, or will the projects be moved to `v145`?
- **B. The Linux build system.** MSBuild can't build C++ on Linux. How is the Linux C++ part built today: a Makefile, CMake, or a script calling `g++`?
- **C. Windows SDK.** Which `WindowsTargetPlatformVersion` do the projects name? One SDK is assumed.
- **D. GCC version.** Ubuntu 24.04's default GCC 13, or GCC 14?
- **E. The vault's limits (section 5).** Once the scan shows what would be skipped: convert the `.rc` and code-page files to UTF-8, carry the public third-party libraries in the build images, and decide whether binary files (icons, bitmaps) in the vault become a design task. That task changes the storage format, so it needs the coordinator's review.
