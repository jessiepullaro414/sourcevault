#!/bin/sh
# SourceVault synthetic sample. Runs in the build container, in the project folder, with no network.
# Whatever this script writes to /sourcevault/out is published (source maps and .pdb files are left out).
# Linux target; build.cmd is the Windows target. A two-part project builds its Linux part here.
set -eu
dotnet publish Hello.csproj -c Release -o /sourcevault/out --nologo
