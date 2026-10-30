# Changelog

All notable changes to NexusWindows are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases up to 30 October 2026 were made in the separate repositories of each profile (now archived):
[Claude 3.7.4](https://github.com/pironjulien/NexusWindows-Claude/blob/main/CHANGELOG.md),
[Codex 3.7.4](https://github.com/pironjulien/NexusWindows-Codex/blob/main/CHANGELOG.md),
[Antigravity 3.6.0](https://github.com/pironjulien/NexusWindows-Antigravity/blob/main/CHANGELOG.md).

## [Unreleased]

### Fixed
- Release-installation fixture now represents an installed Claude Code with its existing `.claude.json`, so the
  all-client check tests the same clients as installation. Published tags can be checked again without rebuilding
  or republishing their assets.

## [3.9.2] - 2026-10-30

### Fixed
- The public installer works in Windows PowerShell 5.1 started from PowerShell 7: its built-in module directory
  takes precedence over inherited Core modules, so checksum validation and file unblocking remain available.
  Existing custom module paths are preserved. A child-process regression checks the real bootstrap and hash helper.
- Workstation source setup preserves an explicit public `nexus-windows@nexus-plugins` choice, including disablement,
  rather than recreating a personal plugin. It checks the registered catalog and exact installed cache; missing or
  changed files remain a reported repair without rewriting plugin choices or custom MCP declarations.

### Added
- Release installation check on an isolated Windows profile, including checksum verification, client declarations,
  stable-launcher MCP startup and a second installation of the published package.

## [3.9.1] - 2026-10-30

### Added
- Claude Desktop extension for ARM64 PCs, `nexus-windows-claude-arm64.mcpb`, next to the x64 one in each release.

### Fixed
- MCP registry entry: description within the registry's 100-character limit; the registry publication can be rerun
  by hand for a published release.
- Installation is documented and tested only from the release files (get.ps1 checked against `SHA256SUMS.txt`, then
  run with `-File`); test PowerShell sources run as script files instead of encoded command lines.

## [3.9.0] - 2026-10-30

### Added
- Native release files for Windows x64 and ARM64: `NexusWindows-win-x64.zip` and `NexusWindows-win-arm64.zip`
  (self-contained engine, nothing else to install), `SHA256SUMS.txt` and a Sigstore build provenance attestation
  (`gh attestation verify <file> --repo pironjulien/NexusWindows`).
- `get.ps1`, attached to each release (run as a verified file with `-File`; `-Remove` to uninstall): installer and updater that picks the PC's
  architecture, checks the SHA-256 and declares NexusWindows to the clients present.
- Stable launcher `%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe`: every client declares this one path; it runs
  the engine version recorded in `installation.json` after checking its SHA-256, passes stdin, stdout and the exit
  code through, and is replaced safely while sessions run (they keep their version, the next one gets the new one).
- `NexusWindows.exe --register claude|codex|antigravity|all [--check] [--remove]`: declarations for Claude Code (or
  its nexus-windows plugin), Claude Desktop, Codex (`config.toml`) and Antigravity (plugin), replacing the Python
  installers; an entry of your own or a disabled one is never changed.
- Claude Desktop extension `nexus-windows-claude.mcpb` built on the native engine, published to the MCP registry as
  `io.github.pironjulien/nexus-windows`.
- xUnit test suite (`tests/`, `dotnet test`), and secret (gitleaks) and workflow (zizmor) checks in the CI.

### Changed
- The native engine is the only engine: `test.ps1` and the CI run `dotnet test` and the Codex adapter's
  `node --test`; the engine's `--*-selftest` options became xUnit tests.

### Removed
- The Python engines and their tooling (Claude `src/`, Antigravity `client/`, shared `garde_fous.py`, pyproject,
  uv.lock, Python tests, bench and lab) and the uv-based setup scripts. Earlier PyPI packages stay available but are
  no longer updated.

## [3.8.0] - 2026-10-30

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
