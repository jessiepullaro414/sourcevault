@echo off
rem SourceVault synthetic sample, Windows target. Runs in the Windows build container, in the project folder, with no
rem network. Whatever this writes to C:\sourcevault\out is published (source maps and .pdb files are left out).
rem A project with a Windows part and a Linux part builds each one here and in build.sh respectively.
dotnet publish Hello.csproj -c Release -o C:\sourcevault\out --nologo
if errorlevel 1 exit /b 1
