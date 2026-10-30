# Changelog

All notable changes to NexusWindows are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases up to 30 October 2026 were made in the separate repositories of each profile (now archived):
[Claude 3.7.4](https://github.com/pironjulien/NexusWindows-Claude/blob/main/CHANGELOG.md),
[Codex 3.7.4](https://github.com/pironjulien/NexusWindows-Codex/blob/main/CHANGELOG.md),
[Antigravity 3.6.0](https://github.com/pironjulien/NexusWindows-Antigravity/blob/main/CHANGELOG.md).

## [Unreleased]

### Changed
- One repository for the three profiles (`claude/`, `codex/`, `agy/`) and their shared policy (`commun/`), replacing
  NexusWindows-Claude, NexusWindows-Codex and NexusWindows-Antigravity. Installation goes through NexusSystem.
- The separate PyPI, MCP registry and directory listings (`glama.json`, `server.json`) are no longer published from
  here; earlier packages stay available.

### Added
- One common C# (.NET 10) engine, `NexusWindows.exe` (`codex/native/`), serving the three profiles as MCP stdio
  servers; installed as an immutable version under `%USERPROFILE%\.nexus\nexus-windows\native\<hash>`, outside
  MSIX-virtualized AppData and the Codex plugin cache. Running sessions keep their server until their host reloads it.
- Antigravity profile: persistent semantic routines with text parameters, inspection, deletion and cross-PC
  definition sync (authorizations never saved); window-targeted WGC capture with local Windows OCR; window
  restore, minimize, maximize, bottom, front, move and resize with native state readback.

### Fixed
- Shared log in `%USERPROFILE%\.nexus\journal\windows` instead of AppData: started by Claude Desktop (an MSIX app),
  the engine wrote into a private copy of AppData other tools could not see.
- Routines: a fixed expected text is no longer exported to the routine mirror; a routine received from another PC is
  reviewed before its first replay. The shared log no longer records open document or folder names, nor the value
  of an unknown key.
- Claude profile `launch` of a Settings page: the window is described by its content, as the inventory names it,
  instead of its ApplicationFrameHost frame, so a successful launch is no longer refused as "identity changed".
- Long Chromium text input without DevTools (Claude profile): 170 s per gesture again instead of 9 s, which stopped
  2,000 characters around 650; every chunk is still read back.
- MCP stdio: `notifications/cancelled` stops the running request at its next guard, like an emergency stop (no
  replay, unknown outcome after input); a batch ended by EOF is always processed in full.
- Clicks posted in the exact DPI context of the target; Chromium rich-text focus under exact identity.
- Antigravity profile: a read right after an input is recognised as a newer observation (the repeat guard ordered
  events with a clock that only moves every ~15 ms on Windows; it now uses an event counter).
- A UIA or Win32 input whose effect is not confirmed no longer triggers another technique or a physical click;
  repeated inputs require a newer observation of the same window; `expanded` and `focused` postconditions are checked.
