# Changelog

All notable changes to NexusWindows are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases up to 30 October 2026 were made in separate repositories for each profile (Claude 3.7.4, Codex 3.7.4,
Antigravity 3.6.0), since removed; those versions remain on PyPI (`nexus-windows-claude`, `nexus-windows-antigravity`)
and npm (`nexus-windows-codex`).

## [Unreleased]

## [3.16.0] - 2026-11-01

### Changed
- A step that only the real mouse or keyboard can do in this window is refused the same way for the three profiles:
  `needsForeground: true` and a message starting "Needs the foreground", which says to ask the user's agreement and
  then repeat the step with `foreground:true`. On the common banc in the background, every such refusal (WPF raw click,
  WPF keys, WPF context menu, WinForms and WPF double click, desktop drag and drop) now says so; with the foreground
  granted, each of these gestures succeeds.

## [3.15.1] - 2026-11-01

### Fixed
- A shortcut sent in the background to a window where nothing has the keyboard focus (a window never activated) goes
  to the window itself instead of being refused because "focus cannot be verified" (Ctrl+K on the Win32 and WinForms
  labs).
- A WPF menu is opened in the background by UI Automation, by its names, as WinForms menus already were, instead of
  "No Win32 menu bar".
- `launch` no longer hangs on a program or folder that only exists in a packaged app's virtualized AppData (the engine
  started by Claude Desktop, an MSIX app, sees its package's private copy of `%LOCALAPPDATA%`). Explorer, which starts
  user applications, did not see that path: it showed a modal "Windows cannot find" box on the user's desktop and its
  synchronous `ShellExecute` never returned, holding the shared foreground token, so the other two AIs got "Foreground
  token unavailable" and the next call of the same client waited too. Explorer now receives the file system's backing
  path, and the launched window is recognized by that path. On the common banc, the covered launch goes from 0/3
  (120 s timeout) to 3/3 in about 1.9 s for the three profiles, and the long page no longer waits 120 s behind it.

## [3.15.0] - 2026-11-01

### Added
- Drag and drop in the background in a Chromium or Electron page opened with a DevTools port, for the three profiles:
  the mouse is pressed, moved and released through DevTools, and an HTML5 drag (`draggable`) is intercepted and
  dropped by `Input.dispatchDragEvent` instead of opening the Windows drag loop, which follows the real mouse. On the
  common banc's new drag-and-drop task, Edge and Electron go from refused to the exact drop, with no intrusion.
  In desktop applications (Win32, WinForms, WPF) a drag is never posted: the application may start the Windows drag
  loop (OLE), which follows the real mouse and would drop under the user's cursor. It stays refused in the background
  and takes the real mouse only with `foreground:true`.

## [3.14.0] - 2026-11-01

### Changed
- The three profiles behave the same way wherever no client requires otherwise, each time with the better of the
  former behaviors (Julien's decision of 01/11; the behavior test compares these defaults too):
  - an `act` without a prior look observes the named window first, Codex included; an observation id stays optional
    and, when given, designates the window and must still be current;
  - element indices stay the same from one look to the next for the same element (Claude's numbering);
  - `look` lists 150 lines by default (up to 2000 on request, the rest summarized with its regions), adds an image
    when the tree is too poor, and numbers the unnamed controls on images (`marks="auto"`); `mode="text"` is the tree
    for the three profiles, `show="text"` the document text;
  - `act` and `launch` return the updated window in the same answer by default (`then="auto"`, `then="none"` to skip);
  - a window moved since its image keeps its image usable for points and `region` (only a resize asks for a new look);
    `region` takes the image id optionally, and a point carrying the region's own id is read in its pixels;
  - text postconditions compare with spaces normalized (line breaks, non-breaking spaces), `includes` ignoring case;
  - French and English key names and modifiers (`entrée`, `échap`, `ctl`, `maj`…) are accepted by the three profiles
    and made canonical before the guards read them;
  - a simulated Enter in the MCP server always needs its text submission scope (send and agreement checks);
  - `remember` saves a routine unless `staticUI:false`, and `remember` with no steps forgets it;
  - `windows` has one output schema (Codex's fields and the window details) for the three profiles.

## [3.13.0] - 2026-11-01

### Changed
- Typing into a modern UI Automation field (WPF, UWP/WinUI, WinForms rich controls) appends in the background for the
  three profiles: the current value is read in the action worker and extended by UIA `SetValue`, as Claude already
  did. Codex used to type it with brief foreground input (`physical:native`). A test runs the same `type` and `click`
  on the same WPF lab window for the three profiles and requires the same technique and result.
- Antigravity's `type` no longer rewrites the whole field with the value seen at the last look plus the new text: it
  appends like the other profiles, from the value read at the moment of the action (a change made since the look is
  no longer overwritten).
- A postcondition on a radio button's `checked` reads its selection, and a `value` on a text or a label reads its text,
  for the three profiles (Claude only before; Codex and Antigravity left such a step unverified).
- `windows` gives each window's process, framework (Win32, WPF, Chromium, UWP), size and state (minimized, maximized,
  foreground, hidden, taskbar or desktop surface) for the three profiles (Claude only before).
- `act(then=…, settle_ms=…)` returns the updated window in the same answer for the three profiles. Claude keeps
  `then="auto"` by default; Codex and Antigravity get it on request (`then="none"` by default, answers unchanged).
- Typing one line into a Chromium field or rich editor (Chrome, Edge, Electron) takes Claude's DOM-focus text route
  for the three profiles, read back packet by packet. At the banc, Codex and Antigravity reported a false success on
  the rich editor of Edge and Electron (the page received nothing); they now write it, like Claude. Line breaks take
  it too for the three profiles, each Enter checked against the send and agreement safeguards.
- `set` on a Chromium field writes it by UI Automation `SetValue` for the three profiles: Claude's simulated typing
  failed there at the banc (0/3) where the other two profiles succeeded.
- One set of guards for the three profiles (Julien's decisions of 01/11; the behavior test now compares them too):
  - a password field is refused to the three (Antigravity could write into one);
  - the real mouse and keyboard are used only with `foreground:true`, Codex included (it took them by default);
  - the Windows panels (`"start"`, `"quick settings"`, `"notifications"`) and one foreground lease for a whole
    `act` with `foreground:true` are open to the three (Claude only before);
  - `launch` takes Claude's route for the three: Start menu names, any URI through its Windows association, Store
    apps, documents and folders proven open, `devtools` on an isolated profile, Steam DevTools lease, `then`/`wait_s`
    (`then="none"` by default for Codex and Antigravity); Codex and Antigravity opened only executables, paths, app
    ids and `ms-settings:`;
  - a step whose `{selector, exists}` postcondition already holds answers "=" without sending anything, for the three
    (Codex and Antigravity refused it as vague);
  - without a named or bound window, the three refuse (Claude acted on the foreground window);
  - the hard blocks are the union of the three lists for each profile: secure desktop, Windows credentials window,
    password managers and Codex's own approval window;
  - `look(window="screen")` captures the whole screen for Codex too;
  - an `act` without a prior look observes the named window first for Antigravity as for Claude; Codex's contract
    still needs the look's observation id.
- Chromium text (Chrome, Edge, Electron) may take up to 170 s for the three profiles, packet by packet, each packet
  still checked (focus, presence, STOP, client cancellation); Codex and Antigravity stopped a long text at 9 s.

### Fixed
- Agents acting at the same time slowed each other down: a `type` on the WPF lab took 2.4 s alone, 10 to 11.5 s with
  three agents on three windows. The cause is in Windows, not in a NexusWindows lock: simultaneous UI Automation tree
  reads of different windows are slower than the same reads in turn (0.45 s alone, 1.9 s each for three, also with
  clients outside NexusWindows). Full tree reads now take turns across the agents (a file lock in `.nexus`, bounded at
  4.854 s, after which a read goes ahead anyway).
- A WPF window is read about a third faster (lab window: 1.1 s to 0.77 s): its MSAA state, which only tells a default
  button and costs a bridge per element, is read on the invokable elements only; the observed tree is identical.
- A text postcondition (`expect: "clic 1"`) that the window shows but its accessibility tree does not carry (a WPF
  TextBlock named by `AutomationProperties.Name`, a drawn label) is also looked for by OCR in a background capture of
  the window, before the gesture (a text already shown cannot verify it) and after. The click arrived but the step
  answered "Postcondition not observed". A text that must disappear (`gone`) is still judged on the tree.

## [3.12.0] - 2026-10-31

### Added
- `look(within=N, show="table")` reads a table: column headers, then one line per row (`max_elements` rows at most),
  through UI Automation `GridPattern`/`TablePattern` (Explorer Details view, WinForms ListView), or from the observed
  tree when the control has no grid (DataGridView: one row per row container; Task Manager: one cell per child).
- A window is held by one agent at a time during an `act`: a file lock per window in `.nexus\windows`, held for the
  whole batch like the foreground token and freed by Windows if its holder dies. Another agent waits up to 4.854 s,
  then gets ✗ "Window busy: codex (pid …, for … s)" with `retry_safe: true` and `outcome: "not_done"` (nothing sent).

### Changed
- A click at a point (`"x,y"`) on an interface that ignores posted mouse input (WPF, UWP/WinUI, Firefox) is the UI
  Automation gesture (Invoke, Toggle or SelectionItem) of the single enabled element under the point, in the
  background, instead of the brief foreground input; the read-back and the confirmation safeguards are unchanged.
  Several such elements under the point (a check box in a list item) keep the point.

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
