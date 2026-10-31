# Nexus Windows plugin

Gives Claude Code your Windows desktop applications through native UI Automation: it sees a window, clicks, types,
uses menus and shortcuts and launches apps, reading back every action. It works in the background when the app
supports it; gestures requiring the foreground are explicit. Irreversible gestures need your explicit "yes"; Ctrl+Alt+Shift+Esc stops everything for two minutes.

## What it runs
The plugin starts the native NexusWindows engine over stdio, from the stable path its installer keeps up to date:
`%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe --mcp --agent claude`. The plugin itself contains only this
launcher and a skill; it sends nothing anywhere (engine privacy:
[NexusWindows](https://github.com/pironjulien/NexusWindows/tree/main/claude#privacy)).
Install it once with the [NexusSystem app](https://apps.microsoft.com/detail/9NSH2P3QLWT1), which runs the product's
native installer. The file-based alternative downloads `get-nexus.ps1` from a release, checks its SHA-256 and runs it
with `-File`: see [the installation guide](https://github.com/pironjulien/NexusSystem/blob/main/INSTALL-AI.md).
## Requirements
- Windows 10 or 11 with NexusWindows installed (above).

## Try it
- "Open Notepad and write a shopping list: milk, eggs, bread."
- "Open Calculator, compute 1234 × 5678 and tell me the result."
- "Open my Downloads folder in File Explorer and tell me what is in it."

## Testing
Any Windows 10 or 11 PC (or VM) works: no account, no sample data. Notepad and Calculator are enough to see the
background flow (look, act, read back) while you keep using the mouse and keyboard.

Source, safeguards and privacy: https://github.com/pironjulien/NexusWindows (MIT).