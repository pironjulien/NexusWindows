# Changelog

All notable changes to NexusWindows are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases up to 30 October 2026 were made in separate repositories for each profile (Claude 3.7.4, Codex 3.7.4,
Antigravity 3.6.0), since removed; those versions remain on PyPI (`nexus-windows-claude`, `nexus-windows-antigravity`)
and npm (`nexus-windows-codex`).

## [Unreleased]

### Fixed
- Codex source installer: the public catalog check accepts NexusSystem's `git-subdir` entry (this repository at a
  release tag) and verifies the copy Codex installed (version, manifest-declared MCP file and guide). It only knew the
  former in-catalog `local` source, so every check on a PC using the catalog failed.

## [3.11.0] - 2026-10-31

### Changed
- The three profiles serve the same tools and capabilities: Codex and Antigravity now also get `notifications`,
  `clipboard`, `logs`, `look(within=N)`, the skeleton list with named regions, compact listed elements and the taskbar
  and desktop as windows (by name and by id). A test fails if a tool or one of these capabilities is reserved to one AI.
- `clipboard` `write` borrows the clipboard instead of replacing it: the user's content (every memory format) is kept
  aside and given back on its own after 61.8 s, or at once with `action="restore"`; a copy made by the user meanwhile
  is kept, and a write is refused while the user's clipboard holds private content (password manager).

### Fixed
- `clipboard` `write` in 3.10.0 overwrote the user's copy for good.

## [3.10.0] - 2026-10-31

### Added
- Claude: `look(within=N)` lists only element N and its descendants. When a dense window's list is cut (chat apps,
  IDEs), the summary names its regions (`[N] list "Messages" (340)`), like agent-desktop's `--skeleton`/`--root`.
- Claude: `clipboard` tool. `read` (default) returns the copied text (security codes masked), the copied file paths
  and whether an image is there. Content that a password manager marked private (`ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory` = 0) is never read. `write` replaces it with text and reads it back.

### Changed
- Claude: `look` no longer lists unnamed layout wrappers (pane/group/custom with no name, id, value or focus that hold
  other elements); the summary counts them, and `detail="full"` lists everything.
- Claude: listed elements are compact in the structured result, without default values (null, false, empty) or internal
  identities (`runtimeId`, `nativeWindow`; the ancestor chain becomes `parent`). Acts still resolve against the full
  observation. On the Claude desktop window: 150 elements in about 22,000 characters instead of about 60,000.
- Docs: profile READMEs point to this repository only (release badge, family table); the Codex README installs the
  native engine only, without the npm package or the removed `NexusWindows-Codex` plugin marketplace.
- Release: a push whose version is not released yet becomes the stable release right after its development build
  (same files); "Run workflow" still promotes by hand.
- Deferred experimental native freshness reconciliation until unsupported UIA properties provide reliable proof.
  The candidate and qualification work are archived outside the repository; this experimental work is not included in 3.9.4.

### Fixed
- Claude: `look` with the id that `windows` gives for the taskbar or the desktop was refused ("No window matches").

### Security
- CodeQL (GitHub default setup) and the OpenSSF Scorecard workflow run again on every push to main.

## [3.9.8] - 2026-10-31

### Fixed
- `act` on a web element above or below the visible part of a long page (Chrome Web Store dashboard, 31/10): a target
  shown by `look` as `[N]` is now scrolled into view (UI Automation `ScrollItem.ScrollIntoView`) before input, like a
  named target, and read back visible (1.618 s at most) before the gesture; `set` + `click` on it work in one `act`.
  Before, Claude's background `set` stopped with "The observed Chromium field identity changed" and a click was
  refused as off-screen.
- A filtered `look` (`query`, `max_elements`, `show="changes"`) no longer returns the whole accessibility tree and
  document text in its structured result (about 45,000 characters on a long page, which Claude Code shows the model
  next to any image): only the listed elements, with their `[N]`, and a one-line `summary`. The tree and document
  text come with `detail="full"` or `show="text"`; `show="image"` returns no element list.
- `show="changes"` compares the whole window with the previous look, even after a narrow `query`, and returns only the
  added or changed elements; the indices shown before stay usable.
- `scroll` on an element without a name or automation id (a web page's document) uses the center of its visible part
  instead of being refused for lack of a stable selector.
- The wheel on a Chromium window without a DevTools port (the user's own browser) scrolls the page through its UI
  Automation `ScrollPattern`, read back, instead of being refused.
- An image returned by `act(then="image")` or `look` stays usable for points after the 60-second observation renewal,
  as long as the window keeps its size (a resized window still refuses the point).

## [3.9.7] - 2026-10-31

### Added
- Development builds: every push to `main` checks one version everywhere, runs `test.ps1`, builds the same release
  files and publishes them, attested, as the pre-release `vX.Y.Z-dev.N` (NexusSystem's development channel); each
  published build is then installed from its release files in an isolated profile.
- `get.ps1` honours `NEXUS_RELEASE_TAG` (a stable `vX.Y.Z` or a development `vX.Y.Z-dev.N`) to install that release
  instead of the latest; its `SHA256SUMS.txt` still verifies everything.

### Changed
- A stable release is the promotion ("Run workflow") of a tested development build: the same files, unchanged,
  become `vX.Y.Z` with this changelog's section, then `server.json` goes to the official MCP registry. Pushing a
  version tag no longer releases anything.
- The release workflow calls the shared NexusSystem workflows (`product-dev-release`, `product-promote`), pinned by
  commit SHA. Provenance is now signed by them: `gh attestation verify <file> --repo pironjulien/NexusWindows
  --signer-repo pironjulien/NexusSystem`.

## [3.9.6] - 2026-10-31

### Changed
- `plugin/` is now the single source of the NexusWindows plugin for the three AI apps: Claude Code
  (`.claude-plugin`, `.mcp.json`), Codex (`.codex-plugin`, `.codex-mcp.json`), their shared guide (`skills/`) and
  icons (`assets/`), plus Antigravity's own plugin (`antigravity/`, formerly `agy/plugins/nexus-windows`), which
  still ships with the engine. The NexusSystem catalog points to this folder at the release tag instead of keeping
  its own copies. The engine is unchanged.

## [3.9.5] - 2026-10-30

### Added
- Public `get.ps1 -UpdateOnly` maintenance installs the engine alongside running sessions without registering,
  enabling or creating any client. Optional `-RefreshExistingProfiles` refreshes an already active Antigravity
  plugin's shipped files while preserving all configuration, disabled/custom declarations and unrelated assets.
- MCP discovery and initialization expose the actual process identity and assembly SHA-256. Atomic runtime
  receipts record each session's PID, exact process birth, agent, version, loaded executable and assembly hash,
  with idle/busy/stopped lifecycle. A saved receipt alone is not proof that an AI host loaded the engine.

### Fixed
- Public and source installers share installation exclusion, preventing concurrent launcher/manifest swaps.
  Existing MCP sessions continue with their current engine; new connections use the installed version.

## [3.9.4] - 2026-10-30

### Changed
- Clarified that an existing explicit end-to-end user request or approval covers the same operation's preparation
  and application confirmation steps, without repeated questions for fresh technical tokens. Exact token binding,
  single use and expiry remain unchanged; materially changed scope or publication mode requires a separate decision.
- Updated the three profile guides and skills to prefer discovered MCP schemas, fresh observations and modern
  equality postconditions. A shared cookbook records tested background file-dialog handling, explicit UTF-8 for
  custom stdio clients, and the distinction between optional Chromium warmup retries and a genuinely blank page.

### Fixed
- Multiline value replacements no longer inherit an unrelated publish/send button as their trigger. Physical multiline
  fallbacks recheck the bound field, its focus and current outward controls before every real Enter for all three agents.
- Classic editable combo boxes, including the common file picker, expose their live edit text as the combo value.
  Empty text also replaces a stale provider value, so a background write can be verified against the same control.
  Hidden Win32 control regressions cover file paths, clearing the field, selected options and synthetic edit input.
- Chrome tab structure checks ignore only the terminal memory-usage metric in French and English, including high
  memory and localized decimal values. Actual tab titles, group names, control identities and other controls remain
  part of the strict structure hash; title text that resembles a metric in the middle is preserved.
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
