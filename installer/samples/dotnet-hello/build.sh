#!/bin/sh
# SourceVault synthetic sample. Runs in the build container, in the project folder, with no network.
# Whatever this script writes to /sourcevault/out is published (source maps and .pdb files are left out).
set -eu
dotnet publish Hello.csproj -c Release -o /sourcevault/out --nologo
