# NexusWindows

**Windows computer use for Antigravity, in the background.** The agent sees and operates desktop applications through
UI Automation and Win32 messages while you keep your mouse, keyboard and foreground window. Every action is read
back; the real mouse and keyboard are only a fallback the step allows.

- **One look, one batch**: `look` lists a window's controls by label, `act` runs several steps (click, type, choose,
  menus, shortcuts, scroll) and returns the new state.
- **Covered windows too**: windows behind yours, minimized dialogs, long web pages in Chrome or Edge.
- **Safe by default**: paying, sending, publishing or deleting for good needs your explicit yes.

## Requirements

Windows 10 or 11 (x64 or ARM64). The plugin starts the NexusWindows engine installed on the PC
(`%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe`): one native program (C#, .NET 10), no Python, Node.js or
administrator rights. If it is missing, ask Antigravity to install NexusWindows: its skill downloads the latest
release's `get.ps1`, checks it against `SHA256SUMS.txt` and runs it. By hand:
[installation](https://github.com/pironjulien/NexusWindows/tree/main/agy#installation).

## Privacy

Everything runs on your PC: no account, no telemetry, no network request. Tool results go to Antigravity, which may
send them to Google under its own privacy policy; NexusWindows sends nothing anywhere else.

## Links

- Source and releases: [github.com/pironjulien/NexusWindows](https://github.com/pironjulien/NexusWindows) (MIT)
- Project page: [julienpiron.fr/nexuswindows](https://julienpiron.fr/nexuswindows/)
- Support: [GitHub issues](https://github.com/pironjulien/NexusWindows/issues)
