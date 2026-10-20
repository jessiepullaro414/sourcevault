SourceVault editor DEMO
=======================

!! NOTHING IN THIS DEMO IS ENCRYPTED. USE MADE-UP CODE ONLY. !!

This is the SourceVault editor running on a simulated vault, so it can be shown on any PC. There is no SourceVault
service, no TPM key and no encryption. The demo is red everywhere so it is never mistaken for the real vault.

Spillage can happen: what you type stays in this PC's memory, Windows can write memory to the page file or crash
data, and the window can be screen-captured and shared (the real vault editor asks Windows to exclude itself from
capture; the demo deliberately does not, so it can be presented). Never type or paste real source code into it.

Needs: Windows 10 or 11 (x64) with the Microsoft Edge WebView2 Runtime (Windows 11 includes it). Nothing to install
and no administrator rights. Keep this folder together: editor\ runs on runtime\.

START: double-click "Start SourceVault Demo.cmd"

WHAT IS IN IT
  - Three fictional projects (TypeScript, C++ and C#). Projects, files, history and settings live only
    in this window's memory and are gone when it closes. Its browser profile is deleted when it closes.
  - Every editor feature works as in the vault: projects and files, history and restore, conflicts and merge,
    deleted files, builds (simulated: nothing is compiled), search all files, go to file (Ctrl+P), outlines,
    split editor, settings, Save all, compare with saved, focus mode. Ctrl+K Ctrl+S lists the shortcuts.
  - "Demo controls" (bottom right) make the events other people and the service cause: someone else edits or
    deletes the open file, a Windows lock, session expiry, a failed unlock, a failed build. Click its title to fold it.
  - Copy, paste, drag and right-click are blocked, as in the vault, and the editor has no network access.

A SHORT DEMO
  1. Open "Synthetic C++ demo", open src/gadget.cpp, press Alt+O (header) and Ctrl+Shift+O (outline).
  2. Type a line, then Demo controls > "Someone else edits this file", then Ctrl+S: a conflict. Click Merge.
  3. Ctrl+Shift+F, search "gadget": results include your unsaved edits.
  4. Builds > Build saved files. Demo controls > "Next build fails", build again.
  5. Demo controls > "Windows lock", then Unlock vault: unsaved edits are kept and re-checked.

Integrity: demo-manifest.sha256 lists the SHA-256 of every file in this folder.
Licences: licenses\.
