# Design: building C++ offline (proposal, 2026-10-19; awaiting owner decisions)

The owner's code is C++ as well as C#/.NET (next-tasks item 4). Both build containers carry only Microsoft's .NET SDK image, so C++ needs a pinned toolchain in each. This note proposes how to add one. **Nothing here is built, downloaded or installed.** Versions marked *to pin* are fixed only once the owner has answered the questions at the end. Sizes are estimates until the first kit build measures them.

## Summary

| | Windows target | Linux target |
|---|---|---|
| Compiler | MSVC, from Microsoft's **Visual Studio Build Tools** | **GCC**, or **Clang**, or both, from Ubuntu 24.04's own packages |
| Build tools | MSBuild (in Build Tools); CMake and Ninja from the *C++ CMake tools* component | CMake and Ninja from Ubuntu 24.04 |
| SDK | One Windows SDK (Windows 11 SDK, *to pin*) | glibc and libstdc++ as shipped in the image |
| Base | The pinned .NET SDK Windows Server Core image already on the drive | The pinned .NET SDK Linux image already on the drive |
| Pinned by | The SHA-256 and Microsoft signature of a **fixed-version** bootstrapper, plus the SHA-256 of every file in an offline **layout** | The SHA-256 of every `.deb`, taken from a dated Ubuntu snapshot whose signed index lists them |
| Comes onto the drive as | `toolchains\msvc-layout\` | `toolchains\linux-cpp\*.deb` |
| Image made | On the target, offline, by the container script: run the base image with the toolchain folder mounted read-only, install, `docker commit` | same |

**One container per platform, not one per language.** The proposal adds C++ to the existing `sourcevault-build-windows` and `sourcevault-build-linux` containers instead of creating new ones. A project with a C# part and a C++ part then builds both in one `build.cmd` or `build.sh`, and `build.json` keeps its two targets. A C++-only container per platform is the alternative, if the owner prefers images that stay small for .NET-only projects (question 6).

## 1. Windows: MSVC Build Tools on Windows Server Core

### Which product
The **Visual Studio Build Tools** are Microsoft's command-line edition of the C++ compiler, linker, MSBuild and the Windows SDK, without the IDE. They are the only supported way to get MSVC into a container: Microsoft documents installing them into a `windowsservercore` image (Nano Server cannot hold them). The release, for example Visual Studio 2022 17.14 (MSVC 14.44) or Visual Studio 2026 18.x (MSVC 14.5x), is the owner's choice (question 2). A Long-Term Servicing Channel release suits a toolchain that is pinned and rarely moved.

Components (exact IDs *to pin* from the chosen release's component list; Microsoft renames some between major versions):
- `Microsoft.VisualStudio.Workload.VCTools`: the C++ build tools workload, including MSBuild;
- `Microsoft.VisualStudio.Component.VC.Tools.x86.x64`: the x64 compiler and linker;
- `Microsoft.VisualStudio.Component.Windows11SDK.<build>`: **one** Windows SDK;
- `Microsoft.VisualStudio.Component.VC.CMake.Project`: Microsoft's bundled CMake and Ninja, if the owner uses CMake (question 4).

Components are named one by one, not with `--includeRecommended`. That keeps the image smaller and the list reviewable, and a later release cannot add a component silently. ARM64, Clang-cl, ATL/MFC, older toolsets and Spectre-mitigated libraries are left out unless the owner needs them (question 3).

### How it is pinned
1. **The bootstrapper.** Microsoft's evergreen link (`aka.ms/vs/…/vs_buildtools.exe`) always installs the newest release, so it is **not** used. Each release has a *fixed-version* bootstrapper on Microsoft's release-history page. The kit records its URL, size and SHA-256 in `kit-sources.json`, and checks that its Authenticode signature chains to Microsoft. This is trust on first use for the hash, as with the Docker archives, but backed by the signature.
2. **The layout.** On the connected staging PC, the kit builder runs that bootstrapper with `--layout <kit>\toolchains\msvc-layout --add <components> --lang en-US`. The layout is a folder holding the installer, its catalog and every package, which Microsoft signs and which its catalog lists with hashes.
3. **The layout manifest.** The kit builder writes the SHA-256 of every file in the layout into `installer\toolchains\msvc-layout.sha256`. The first build records it (`-RecordNewHashes`), and it is committed after review. A later kit build must reproduce the same files, or it fails. Files the bootstrapper generates locally, such as its log, are excluded; whether anything else differs between two layout runs is a lab check.
4. **On the target.** The deploy step checks the layout against `kit-manifest.sha256` (as for every kit file), then installs with `--noWeb`, so the installer uses only the layout and never tries the network. The container has no network anyway.

### How the image is made (on the target, offline)
`New-WindowsBuildContainer.ps1` gains `-CppLayout <kit>\toolchains\msvc-layout` (name *to choose*). It:
1. loads and checks the pinned .NET SDK image by image ID, as now;
2. runs a temporary container from it with `--network none`, at least 8 GB of memory, the layout **mounted read-only** (`-v <layout>:C:\layout:ro`), and the isolation mode the existing copy check settled on;
3. inside it runs `C:\layout\vs_buildtools.exe --noWeb --quiet --wait --norestart --nocache --installPath C:\BuildTools --add …`. Exit code 3010 ("restart needed") counts as success, as Microsoft's container guide says;
4. `docker commit`s it as `sourcevault/build-windows:<kit version>`, labelled with the base image ID and the layout manifest's hash, and removes the temporary container;
5. creates `sourcevault-build-windows` from that image with the same settings as today, and records the committed image ID in the container's label and the script's output.

**Why run-and-commit, not a Dockerfile.** A Dockerfile would have to `COPY` the 2–4 GB layout into a layer. Deleting it in a later step still leaves it in the image. A read-only mount keeps it out. The committed image's ID cannot be known in advance (it contains timestamps), so the pin is on the **inputs** (base image ID and layout manifest), and the result is recorded at creation.

**The compiler environment.** `cl.exe` works only after Microsoft's `vcvars64.bat` has set `PATH`, `INCLUDE` and `LIB`. The image gets one fixed wrapper, `C:\sourcevault\bin\build-env.cmd`:

```bat
@echo off
call "C:\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
build.cmd
```

The Windows target's `command` becomes `["cmd.exe", "/d", "/c", "C:\\sourcevault\\bin\\build-env.cmd"]`. It stays in the administrator-only `build.json`, so projects need not know where the tools are installed, and operators still cannot change what runs. A .NET-only `build.cmd` is unaffected.

**The check.** The script's existing checks stay: network mode none, the SDK version, the copy check, and the .NET test build. It adds: `cl` reports the pinned compiler version, `cmake --version` reports the pinned CMake (if installed), and a fictional `hello.cpp` compiles, links and runs offline.

## 2. Linux: GCC or Clang with CMake in the WSL Docker container

### Which packages
The pinned .NET SDK Linux image (tag `10.0.402`) is, as far as we know, built on Ubuntu 24.04. The script confirms that from `/etc/os-release`, and the plan changes if it is not. So the toolchain comes from **Ubuntu 24.04's own packages**, built and signed by Ubuntu, matching the image's own C library:
- **GCC:** `g++` (GCC 13 in 24.04; `g++-14` is also packaged), `make`;
- **Clang:** `clang-18` and `lld-18` (or another packaged LLVM version), plus `libc++` if the owner wants LLVM's standard library instead of GCC's;
- **Build tools:** `cmake` (3.28 in 24.04) and `ninja-build` (1.11).

Versions are *to pin* when the snapshot date is chosen. If a newer CMake is needed than 24.04 ships, Kitware's own `cmake-<v>-linux-x86_64.tar.gz` is the alternative. Kitware publishes a SHA-256 list with a signature for each release, and one archive is pinned the same way as the WSL installer.

### How it is pinned
1. **A dated snapshot.** Ubuntu's archive changes daily, so the kit uses **`snapshot.ubuntu.com`** at a fixed timestamp (*to pin*). The same date always gives the same packages.
2. **The package list.** A helper on the staging PC asks apt, inside the staging WSL Ubuntu, which `.deb` files the chosen packages need on top of the SDK image: the full dependency closure, with nothing the image already has. It writes `installer\toolchains\linux-cpp.json`, with each package's name, version, snapshot URL, size and SHA-256 **taken from the snapshot's `Packages` index**.
3. **Ubuntu's signature.** That index is covered by the snapshot's `InRelease` file, which Ubuntu signs. The helper checks the signature with `gpgv` against Ubuntu's archive keyring in the staging distro. The JSON is then reviewed and committed. Unlike the Docker archives, this is not trust on first use: every hash traces back to Ubuntu's signature.
4. **The kit builder** downloads exactly those files and checks size and SHA-256, as for every other pinned source. It needs no Docker and no apt.

### How the image is made (on the target, offline)
`New-BuildContainer.ps1` gains `-CppPackages <kit>\toolchains\linux-cpp` (name *to choose*). Inside the distro it:
1. loads and checks the pinned .NET SDK image by image ID, as now;
2. runs a temporary container from it with `--network none` and the package folder mounted read-only, and runs `apt-get install -y --no-install-recommends /debs/*.deb`. With no network, apt can only use those files: a missing dependency fails the install instead of fetching anything;
3. removes apt's lists and caches, `docker commit`s the container as `sourcevault/build-linux:<kit version>` with the same labels as on Windows, and removes the temporary container;
4. creates `sourcevault-build-linux` from that image with the same settings as today (no network, capabilities dropped, `/tmp` in memory). It sets `CC` and `CXX` to the owner's default compiler (question 1);
5. checks `g++ -dumpfullversion` and/or `clang++ --version`, `cmake --version`, and that a fictional `hello.cpp` builds and runs offline with each installed compiler.

## 3. What the drive carries

```
SourceVaultKit\
  toolchains\
    msvc-layout\          the Build Tools layout (bootstrapper, catalog, packages); about 2–4 GB, estimate
    linux-cpp\            the .deb files listed in linux-cpp.json; about 0.1–0.4 GB, estimate
```

- **`kit-sources.json`** gains a `toolchains` section: the fixed-version bootstrapper (URL, size, SHA-256, expected signer), the component list, the snapshot timestamp, and pointers to `msvc-layout.sha256` and `linux-cpp.json`. Changing any of them is a reviewed commit, as for the SDK images.
- **`Build-DeploymentKit.ps1`** gains a toolchain step after the images: it runs the bootstrapper's `--layout`, checks the layout against its manifest, and downloads the `.deb` files. The layout step runs a Microsoft executable on the staging PC, and may need elevation (a lab check). `-SkipToolchains` builds a .NET-only kit, as today. The step can resume, like the rest of the kit.
- **`kit-manifest.sha256`** covers the new files like every other kit file, so `Test-DeploymentKit.ps1` and the deploy pre-flight check them unchanged.
- **`Deploy-SourceVault.ps1`** phase 6 passes `-CppLayout` and `-CppPackages` to the two container scripts when the kit has them.
- **A PC already set up** without the drive gets C++ by pointing the two scripts at a kit's `toolchains\` folder. There is deliberately no online "install C++ from the internet" path: every route goes through the pinned, hashed files.

**Size.** The drive grows from about 4.5 GB to roughly **7–9 GB**, nearly all of it the MSVC layout (estimate). On the target, the Windows image grows by roughly **5–8 GB** and the Linux image by **0.5–1.5 GB** (estimates). The kit builder prints the real figures, which then replace these.

## 4. Licences (the owner must review these)

- **Visual Studio Build Tools: needs the owner's review before anything is downloaded.** The Build Tools are free to download, but their use is governed by Microsoft's licence terms, which the layout carries and the installer shows. As we understand it, using them to build your own software requires a valid Visual Studio licence (Community, if your organisation qualifies, or a paid subscription), with a narrow exception for compiling open-source dependencies. The terms also restrict redistribution. A drive kept in the owner's custody, installing onto the owner's own PCs, seems to fit, but **pushing the resulting image to a registry, or giving the drive to someone else, may not**. This is our reading, not legal advice: the owner should read the exact terms of the chosen release.
- **The Windows SDK** comes under its own Microsoft licence inside the layout, shown by the same installer.
- **The Visual C++ runtime.** A program built with the default dynamic runtime needs the VC++ runtime DLLs where it runs. Shipping them is allowed only under the redistribution terms. The sample links the runtime statically (`/MT`), so its `.exe` runs alone.
- **The Windows Server Core base image** is already accepted for the .NET SDK image: Microsoft licenses it for use on a properly licensed Windows host.
- **Linux:** GCC is GPLv3 with the GCC Runtime Library Exception, so programs it compiles carry no GPL obligation, including when they link `libstdc++` statically. LLVM/Clang is Apache 2.0 with the LLVM exception. CMake is BSD-3-Clause, Ninja Apache 2.0, glibc LGPL. Keeping the `.deb` files on a drive for your own PCs triggers no obligation. Giving the drive to someone else would mean distributing GPL binaries, which carries a source-offer duty.
- **SourceVault itself** stays MIT. None of these tools is linked into it.

## 5. A fictional sample: `installer/samples/cpp-hello`

Synthetic code, for testing imports and builds, as with `dotnet-hello`. It uses CMake. Section 6 shows the MSBuild variant.

```
cpp-hello\
  CMakeLists.txt
  build.sh
  build.cmd
  src\main.cpp
  src\greeting.h
  src\greeting.cpp
```

`CMakeLists.txt`:
```cmake
# SourceVault synthetic sample: fictional code for testing imports and builds. Not real source.
cmake_minimum_required(VERSION 3.25)
project(CppHello LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# MSVC: link the C++ runtime statically, so the .exe needs no redistributable DLLs.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
add_executable(cpp-hello src/main.cpp src/greeting.cpp)
install(TARGETS cpp-hello RUNTIME DESTINATION .)
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

`build.sh` (Linux target; runs in `/sourcevault/src` with no network):
```sh
#!/bin/sh
# SourceVault synthetic sample, Linux target. Whatever this writes to /sourcevault/out is published
# (.o, .debug and similar files are left out). The build folder stays inside the project folder, which the
# build service empties after the build.
set -eu
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
cmake --install build --prefix /sourcevault/out --strip
```

`build.cmd` (Windows target; `build-env.cmd` has already entered the MSVC environment):
```bat
@echo off
rem SourceVault synthetic sample, Windows target. Whatever this writes to C:\sourcevault\out is published
rem (.obj, .pdb, .ilk and similar files are left out).
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1
cmake --build build
if errorlevel 1 exit /b 1
cmake --install build --prefix C:\sourcevault\out
if errorlevel 1 exit /b 1
```

Expected output: `cpp-hello` (Linux) and `cpp-hello.exe` (Windows), and nothing else. `cmake --install` copies only the installed program. That matters more for C++ than for .NET: a C++ build folder is full of intermediates.

## 6. CMake or MSBuild

- **CMake** (with Ninja) gives one project description for both targets. The sample uses it. On Windows it comes from the Build Tools layout, on Linux from Ubuntu, so the two CMake versions differ slightly (unless Kitware's archive is pinned for both).
- **MSBuild** (`.vcxproj`, as Visual Studio creates) is Windows-only, and comes with the Build Tools. Its `build.cmd` would be:
  ```bat
  msbuild CppHello.vcxproj /p:Configuration=Release /p:Platform=x64 /p:OutDir=C:\sourcevault\out\ /nologo
  ```
  Then `OutDir` receives the `.exe` and the `.pdb`; the artifact filter drops the `.pdb`. A Linux part then needs its own CMake or Makefile build.
- Both can be installed. The difference is the `VC.CMake.Project` component, about a few hundred MB.

## 7. What this does and doesn't protect (be aware)

Everything in `files-history-builds-design.md` section 4 still applies (builds are an export path; plaintext source sits in long-lived containers). C++ adds:
- **Compiled C++ is harder to read than IL, but it does not conceal anything.** Strings, `assert` expressions and `__FILE__` paths go into the binary; symbol names survive unless the binary is stripped; disassembly is routine. Release builds with `NDEBUG`, stripped output and dropped `.pdb` files reduce what leaks; they do not prevent it.
- **More leftovers inside the container.** MSVC writes to `%TEMP%` inside the container (`C:\Users\ContainerAdministrator\AppData\Local\Temp`), which is on the container's disk, not in memory, and is not emptied by the build service. The proposed `build-env.cmd` can point `TEMP` and `TMP` into the project folder so they are emptied with it; that is a lab check. On Linux, `/tmp` is already in memory. **No compiler cache** (ccache, sccache) is installed: it would keep source-derived data between builds by design.
- **The artifact filter's list is not C++-complete.** It drops `.obj`, `.o`, `.ilk`, `.iobj`, `.ipdb`, `.pdb` and the debug-symbol types (`Artifacts.cs`). It does not drop `.pch`, `.ipch`, `.idb`, `.tlog`, `.exp` or `.lib` files. Static libraries (`.lib`, `.a`) can be wanted outputs, so they should stay. Whether to add the others is a coordinator decision. Until then, writing only `cmake --install` output (as the sample does) keeps them out.
- **CMake runs project code.** `CMakeLists.txt` can run any command at configure time, just as `build.sh` can. It adds no new power, but reviewers should read it like a script.
- **Third-party C++ libraries.** Offline, vcpkg and Conan cannot download anything, just as NuGet cannot. Libraries must be in the project's source, or come from a reviewed local source (not designed yet; question 7).
- **Resources.** MSVC builds in parallel and uses a lot of memory: the Windows target should get at least 8 GB (`-MemoryGB 8`), and large projects may need `timeoutSeconds` raised toward its 3,600 maximum.

## 8. Testable without a target PC (development)

- The fixed-version bootstrapper's URL, size, hash and signer (metadata only).
- The snapshot's `InRelease` signature, `Packages` hashes and the dependency closure, computed in the staging WSL distro; nothing installed.
- The scripts' syntax, and the new `kit-sources.json` fields' checks in `Test-DeploymentKit.ps1`.
- The sample builds on a developer PC that already has a compiler (not in SourceVault's containers).

Everything that downloads the toolchains, makes a layout, installs into a container or builds there is an owner lab run.

## 9. Lab steps (unverified; for docs/lab-checklist.md once the scripts exist)

These go into the lab checklist when the scripts are written. They are listed here so the owner can see the cost now.

| | Step | Needs |
|---|---|---|
| [ ] | Owner reads the Build Tools and Windows SDK licence terms of the chosen release, and records the decision in `next-tasks.md` | — |
| [ ] | Staging PC: `Build-DeploymentKit.ps1 -Destination <drive>\SourceVaultKit -RecordNewHashes` with toolchains. Record the layout size, check that the bootstrapper's signer is Microsoft, and review and commit `msvc-layout.sha256` | download, about 3–5 GB extra; possibly admin |
| [ ] | Run the kit build a second time into an empty folder: the layout must match `msvc-layout.sha256` (proves the layout is reproducible) | download |
| [ ] | Staging WSL: generate `linux-cpp.json`; the `InRelease` signature checks against Ubuntu's keyring; review and commit it | download (metadata) |
| [ ] | Target or this PC: elevated `New-WindowsBuildContainer.ps1 -Replace -ImageArchive … -CppLayout …`. Record the isolation mode, the install's exit code, the image size, and that the .NET **and** C++ test builds ran with no network | admin; about 15–30 min |
| [ ] | `New-BuildContainer.ps1 -Distro … -Replace -ImageArchive … -CppPackages …`. Record the image size and the compiler and CMake versions; every installed compiler builds the test program | — |
| [ ] | Update `build.json`'s Windows `command` to `build-env.cmd`; `Setup-SourceVault.ps1 -Update -WithBuilds`, `-Status` | admin |
| [ ] | Import `samples\cpp-hello`; **Build** on Linux: the output has `cpp-hello` only. **Build** on Windows: the output has `cpp-hello.exe` only; it runs on the host with no VC++ runtime installed | — |
| [ ] | Rebuild `samples\dotnet-hello` on both targets: unchanged results (the C++ tools must not break .NET builds) | — |
| [ ] | After a Windows build, inside the container: `%TEMP%` holds nothing from the build. Record what it holds | admin |
| [ ] | Deploy a fresh PC from the kit with toolchains (the existing "Offline deployment drive" rows): both C++ builds work with no internet | fresh PC; restarts |

## Questions for the owner

1. **Compilers on Linux:** GCC, Clang, or both? Which is the default (`CC`/`CXX`)? GCC is the usual choice for software that runs on Linux; Clang is closer to MSVC's diagnostics and is useful if the code must build with both.
2. **MSVC release:** Visual Studio 2022 (an LTSC release such as 17.14, MSVC 14.44) or Visual Studio 2026 (MSVC 14.5x)? And which C++ standard does the code need (C++17, C++20, C++23)?
3. **Windows extras:** x64 only, or also ARM64 or 32-bit x86? Is ATL/MFC, Clang-cl or an older MSVC toolset needed?
4. **CMake or MSBuild** (section 6)? Or both? If CMake, are Microsoft's bundled CMake on Windows and Ubuntu's 3.28 on Linux enough, or should one Kitware CMake be pinned for both?
5. **The Build Tools licence** (section 4): does the owner (or the organisation) hold a Visual Studio licence that covers this use? Without that, MSVC cannot be used and the Windows target would need another compiler (for example LLVM's Clang with MinGW, which is a separate design).
6. **One container per platform** (C# and C++ together, recommended), or separate C++ containers so .NET-only builds stay small?
7. **Third-party libraries:** does the C++ code use vcpkg, Conan, or libraries outside the repository (Boost, OpenSSL and so on)? If so, they need a reviewed offline source, like NuGet packages.
8. **How the output runs:** static C++ runtime (as in the sample), or the VC++ redistributable installed where the programs run? On Linux, must the programs run on distributions older than Ubuntu 24.04? Output built against 24.04's C library may not run there.
