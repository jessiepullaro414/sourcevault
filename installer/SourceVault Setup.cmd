@echo off
rem SourceVault setup, guided: asks for administrator rights, then shows the menu (install, check, people, Pack, update).
rem Runs Setup-SourceVault.ps1 from this folder with no options.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath powershell.exe -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','\"%~dp0Setup-SourceVault.ps1\"'"
