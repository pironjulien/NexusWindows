# NexusWindows for Claude

<!-- mcp-name: io.github.pironjulien/nexus-windows -->

<img src="https://raw.githubusercontent.com/pironjulien/NexusWindows/main/claude/mcpb/icon.png" alt="NexusWindows icon" width="96">

[![Release](https://img.shields.io/github/v/release/pironjulien/NexusWindows)](https://github.com/pironjulien/NexusWindows/releases)
[![Tests](https://github.com/pironjulien/NexusWindows/actions/workflows/tests.yml/badge.svg)](https://github.com/pironjulien/NexusWindows/actions/workflows/tests.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](https://github.com/pironjulien/NexusWindows/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://github.com/pironjulien/NexusWindows/tree/main/commun/engine)
[![MCP](https://img.shields.io/badge/MCP-server-7ee2a8)](https://modelcontextprotocol.io)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)](#installation)

[![NexusWindows: Windows computer use for AI agents, in the background. Works with Claude, Codex and Antigravity.](https://raw.githubusercontent.com/pironjulien/NexusWindows/main/claude/docs/assets/banner.webp)](https://julienpiron.fr/nexuswindows/)

![NexusWindows: an AI agent fills and submits a form in a window that stays behind another one, then reports five verified steps](https://raw.githubusercontent.com/pironjulien/NexusWindows/main/claude/docs/assets/demo.webp)

**Windows computer use for Claude, in the background.** An MCP server (`nexus-windows`, its plugin name in the NexusSystem catalog) that lets Claude Desktop
and Claude Code see and operate Windows desktop applications while the user keeps their mouse, keyboard and
foreground window.

Windows 10/11 (x64, ARM64) · native .NET 10, nothing else to install · MIT licence

Part of the Nexus family: [NexusWindows](https://julienpiron.fr/nexuswindows/)
([Claude](https://github.com/pironjulien/NexusWindows) ·
[Codex](https://github.com/pironjulien/NexusWindows) ·
[Antigravity](https://github.com/pironjulien/NexusWindows)),
[NexusBrowser](https://julienpiron.fr/nexusbrowser/) ([repo](https://github.com/pironjulien/NexusBrowser)),
[NexusAndroid](https://julienpiron.fr/nexusandroid/) ([repo](https://github.com/pironjulien/NexusAndroid)).
Project page: [julienpiron.fr/nexuswindows](https://julienpiron.fr/nexuswindows/).

## Why

Anthropic's own computer use (in Cowork and Claude Code inside Claude Desktop) is a beta for Pro, Max and Ultra
subscribers; Team and Enterprise plans don't have access to it
([Claude Help Center](https://support.claude.com/en/articles/14128542-let-claude-use-your-computer-in-cowork)).
NexusWindows for Claude is a local MCP server, so it works with Claude Desktop and Claude Code on any plan that
allows local MCP servers, Team and Enterprise included.

Screenshot-and-click computer use moves the real cursor and brings windows to the front, so the person at the PC has
to stop working while the agent acts. Most Windows applications can be driven without that: native controls accept
window messages, modern frameworks expose UI Automation, and Chromium can be reached through its accessibility tree.
This server picks, per UI family, a technique that was **measured** not to steal focus, reads the effect back, and
only falls back to the real mouse or keyboard when the user explicitly allows it.

## NexusWindows family

One native engine exposes three MCP profiles, with tools shaped for each agent. They share the same background
techniques, safeguards and read-back. Use the [MCP cookbook](../commun/mcp-cookbook.md) for current requests, native
file dialogs, UTF-8 stdio and diagnosing blank Chromium content.

| | Claude | Codex | Antigravity |
|---|---|---|---|
| Install | `get.ps1` (one engine, three profiles) | `get.ps1` | `get.ps1` |
| Package | [GitHub release](https://github.com/pironjulien/NexusWindows/releases/latest) | [GitHub release](https://github.com/pironjulien/NexusWindows/releases/latest) | [GitHub release](https://github.com/pironjulien/NexusWindows/releases/latest) |
| Tools | `windows` `look` `act` `launch` `window` `notifications` `clipboard` `logs` | same | same |
| Folder | [`claude/`](../claude/README.md) | [`codex/`](../codex/README.md) | [`agy/`](../agy/README.md) |

Every step reports ✓ verified, = already done, ~ unknown (never replayed) or ✗ failed or refused. The three share the
guardrail policy, the foreground token, the emergency stop (Ctrl+Alt+Shift+Esc), a common journal format and a common
bench.

## Installation

Windows 10 or 11, x64 or ARM64. Nothing else to install: NexusWindows is one self-contained native program
(C#, .NET 10); no Python, Node.js or .NET runtime is needed.

**Let your AI install it**: NexusSystem detects your AI apps and sets up every profile.

```text
Install Nexus for me by following https://raw.githubusercontent.com/pironjulien/NexusSystem/main/INSTALL-AI.md
```

**By hand**, from the release files (no administrator rights; the NexusSystem app does the same for you):

```powershell
$r = 'https://github.com/pironjulien/NexusWindows/releases/latest/download'
Invoke-WebRequest "$r/get.ps1" -OutFile get.ps1
Invoke-WebRequest "$r/SHA256SUMS.txt" -OutFile SHA256SUMS.txt
(Get-FileHash get.ps1 -Algorithm SHA256).Hash    # must equal the get.ps1 line of SHA256SUMS.txt
gh attestation verify get.ps1 --repo pironjulien/NexusWindows --signer-repo pironjulien/NexusSystem   # optional: provenance
powershell -NoProfile -ExecutionPolicy Bypass -File .\get.ps1
```

It downloads `NexusWindows-win-x64.zip` or `NexusWindows-win-arm64.zip` from the latest release, checks it against the
release's `SHA256SUMS.txt`, installs the engine under `%USERPROFILE%\.nexus\nexus-windows`, and declares it to Claude
Code (or its nexus-windows plugin), Claude Desktop, Codex and Antigravity when they are on the PC. Run it again to
update; sessions already running keep their version. Remove it with `-File .\get.ps1 -Remove`.
Every release file has a build provenance attestation: `gh attestation verify <file> --repo pironjulien/NexusWindows --signer-repo pironjulien/NexusSystem`.

**Claude Desktop extension**: `nexus-windows-claude.mcpb` from the
[latest release](https://github.com/pironjulien/NexusWindows/releases/latest) (Settings, Extensions, Install extension).

**By hand**: every client declares the same stable command,
`%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe --mcp --agent <claude|codex|antigravity>`, for example in
JSON (`claude_desktop_config.json`, `mcp_config.json`):

```json
{ "mcpServers": { "nexus-windows": { "command": "C:\\Users\\you\\.nexus\\nexus-windows\\NexusWindows.exe", "args": ["--mcp", "--agent", "claude"] } } }
```

`NexusWindows.exe --register claude|codex|antigravity|all [--check] [--remove]` writes (or checks, or removes) these
declarations; an entry of your own is never changed.

## Tools

| Tool | Purpose |
|---|---|
| `windows(filter)` | Application windows, front to back: hwnd, title, process, UI family, size; `[hidden]` Store apps launched in the background (never shown, still drivable); then the taskbar and the desktop |
| `look(window, show, query, within, region, detail, marks)` | Numbered `[N]` elements (role, name, value, state, position), without bringing the window forward; dense windows list a skeleton without layout wrappers and name their regions for `within=N`; text first: the image only when the tree is too thin or after an unverified step (`show="both"` always attaches it); also `window="taskbar"`, `"desktop"`, and `"screen"` (the whole screen with each window's place) |
| `act(steps, window, then, foreground, remember, routine, params)` | Several steps in one call, each read back, then the updated state; Windows panels `"quick settings"`, `"notifications"`, `"start"` with `foreground=true` |
| `launch(app, args, devtools, restart)` | Start an application (Start menu name, Store app, executable), a folder, a document or an address (`ms-settings:<page>`, `mailto:`, `https:`) without giving it focus; the opening is verified |
| `window(action, ...)` | Restore behind, minimize, maximize, close, move, resize |
| `notifications(limit, app)` | Windows notifications read from their database (read-only), nothing opened on screen; security codes masked |
| `logs(count, errors_only)` | Debug log: techniques tried, durations, errors, server state |

Step actions: `click`, `double_click`, `right_click`, `type`, `set`, `clear`, `key`, `select`, `check`, `uncheck`,
`toggle`, `expand`, `collapse`, `scroll`, `focus`, `menu`, `wait`, `drag`, `hover`. A target is an `[N]` from the last
`look`, a visible name (`"OK"`), a role-qualified name (`"button:OK"`, `"edit:Name"`, `"checkbox:Subscribe"`) or an
`x,y` point in the last image.

`windows`, `notifications` and `logs` also return structured content (MCP `outputSchema` and `structuredContent`:
window rows, notifications, log records) beside the same text, for clients that read data rather than prose.

## Usage

Typical flow: `look(window="Notepad")`, then one `act` with all the steps, then read the per-step report.

```text
act(window="Settings form", steps=[
  {"do": "set",   "target": "edit:Name", "text": "Ada",
   "check": {"target": "Name", "value": "Ada"}},
  {"do": "check", "target": "Subscribe"},
  {"do": "click", "target": "button:Save", "expect": "Saved"}
], remember="save-form")
```

Later: `act(window="Settings form", routine="save-form")`.

## What it does

### Only what changed (`then="changes"`, `show="changes"`)

After an action, `act(then="changes")` (or `look(show="changes")`) returns only what changed since the last list
shown for that window: `+` added, `~` changed (text, state or moved), `-` removed; every other element keeps its
`[N]`. A common scroll of many elements is one line naming them as ranges (`[2-21]`), not a full list. Comparison is
made in the window's own frame, so moving the window is not a change. A removed `[N]` is never given again, and an
element whose change was cut from a long report loses its `[N]` until the next `look`. When almost everything changed
(60 %), the full list is returned. Measured: 2 to 2.6 times shorter than the full list after a click.

### Text read from the image (OCR fallback)

When a window publishes almost no UI Automation elements (fewer than 6: Tk, canvases) or a Chromium page stops
publishing its content, the text of the window image is read with the Windows built-in OCR engine
(`Windows.Media.Ocr`, local, offline) and listed as `[N] Text "…" (OCR)` with its position; such an element can be
targeted by number or by name (click at its position, recomputed if the window moved). Measured on the lab apps: 10 to
21 ms per window; text correct 16/16 (WPF), 20/23 (Edge), 6/7 (Tk).

### Four outcomes per step

Each step is reported as one of:

- `✓` the effect was read back (value, state, window, image). A click or menu choice with no `expect` or `check` is
  also proven on its own, by the clicked element only: its own state read back (checked, expanded, selected), or the
  UI Automation "Invoked" event the application raises when the control receives the click (WPF, Chromium,
  Electron; not WinForms or Win32, whose buttons stay `~`). "Invoked" says the click arrived, not what it produced:
  `expect` checks that. A click the control acknowledged is never sent again by another technique;
- `=` the state requested by the step's `check` was already true, so nothing was sent;
- `~` the input was sent but its effect could not be read (never counted as a success);
- `✗` failure, refusal, or postcondition not met; the batch stops there.

A batch ends with a tally of the four counts.

### Postconditions (`check`)

A step may carry `check`: one element and the state it must reach (`value`, `contains`, `checked`, `selected`,
`expanded`, `focused`, `enabled`, `exists`). Already true before the step: the step is skipped (`=`). True after: `✓`.
False after: `✗` and the batch stops. A state the element does not publish stays unknown, never false.

### Routines

`act(remember="name")` saves a batch only if every step ended `✓` or `=` and every target is a visible name (never an
`[N]` or a point). `{{x}}` marks variable text, filled at replay with `params`. `act(routine="name")` finds each
target again by name and re-verifies every step; an application update is reported and a failed replay marks the
routine for review. Routines are stored per executable under `%USERPROFILE%\.nexus\nexus-windows\state\claude\routines`. An
optional mirror folder (`NEXUS_ROUTINES_MIRROR`) can share definitions between PCs; typed text is never mirrored in
clear (a routine whose text is not a `{{x}}` template stays local).

### Verified opening of paths

`launch(app=<folder or document>)` opens through the file association (ShellExecuteEx, no activation requested), then
checks what actually opened: the folder shown in File Explorer (read through Shell.Application) or a window titled
with the file name.

### Per UI family

- **Native Win32**: direct messages to controls (`BN_CLICKED`, `CB_SETCURSEL`/`LB_SETCURSEL` with their notification,
  `EM_REPLACESEL`, menu `WM_COMMAND`, `TCM_SETCURFOCUS`), read back. UI Automation is avoided where it was measured to
  steal focus (button Invoke) or to fail silently (list Select).
- **Win32 and WinForms drop-down lists**: opened and closed with `CB_SHOWDROPDOWN`, read back with
  `CB_GETDROPPEDSTATE` (`expand`, `collapse`, or `click` on the list). Choosing in an open list closes it first
  (Windows treats that close as a cancel and would drop a choice made before it), then sends `CB_SETCURSEL`,
  `CBN_SELCHANGE` and `CBN_SELENDOK` to the parent and reads the application back (index and label); an empty or other
  value is `✗`. While a list is open its UI Automation tree is not walked (it chains to every top-level window of the
  desktop): the options are read from the ComboLBox pop-up itself.
- **WinForms, WPF, UWP, WinUI**: UI Automation with `AutoSetFocus` disabled and bounded timeouts. A WPF drop-down list
  left open by a choice is closed and the choice read back.
- **Chromium and Electron, without activation**: for an app already open without DevTools, multiline fields, rich
  editors, `<select>`, keys and Ctrl/Alt shortcuts go through a *simulated activation*: `WM_NCACTIVATE` and
  `WM_ACTIVATE` are sent to that window only (Chromium takes its activation state from these messages; Windows does
  not change the foreground), DOM focus is set with MSAA `accSelect(SELFLAG_TAKEFOCUS)` and read back, characters go
  as `WM_CHAR`, and the deactivation is *posted* so it is processed after them. The page receives trusted `keydown`
  and `input` events. One caveat was measured: an app launched a moment ago may use its launch-time foreground right
  once (about 10 ms, handed back by the guard), so the first simulated activation per process is treated like a brief
  activation. Apps started with `launch(devtools=true)` are driven through the DevTools protocol instead.
- **Steam (CEF in an SDL frame)**: recognised as Chromium; `launch(app="Steam")` adds Steam's own
  `-cef-disable-occlusion -cef-force-accessibility` switches so the UI stays readable while covered.
- **Tk**: no UI Automation; its text is found by the OCR fallback. Clicks are sent with the mouse button marked
  pressed for Tk's thread only (Tk reads it with `GetKeyState`), keys and shortcuts get a simulated focus
  (`WM_SETFOCUS` to that window only, never `WM_ACTIVATE`), lists scroll through their native scroll bar; all read
  back. A wheel on a covered Tk canvas would be lost (Tk routes it to the window visible at the point): a Tk window
  that is entirely covered is moved beyond the desktop for the gesture (no activation), the wheel is posted to the
  same control there, and the window comes back to its place, read back to the pixel; a partly visible, maximized
  or minimized window is never moved (refused).
- **Scrolling a covered Chromium page without DevTools**: the wheel is dropped by Chromium when another window is on
  top, and scrolling areas expose no ScrollPattern; the next hidden line of the area is brought into view (UI
  Automation ScrollItem) and the line's offset inside its area is read back.

### Capture

`PrintWindow` (10 to 26 ms, covered windows included), Windows.Graphics.Capture only as a fallback. Images follow the
common Nexus image rule: at most 2000 px and 2.56 MP, so that neither Claude Code nor the model downscales them
(coordinates stay exact); lossless WebP up to 3 MB, then lossless WebP with 2, then 3 low bits per colour dropped, then JPEG from quality 82
at the same size, smaller only if none fits 3 MB (a 2560×1440
screen is sent as 2000×1125, about 110 KB lossless). To read small text, zoom with `region` rather than a larger image. An image identical to the last one
sent for that window (or zoomed area) is not attached again; the reply says so, and `show="image"` always attaches it. Coordinates are bound to the window: followed if it moves, refused if it was resized.

### Agent at work

While an action runs (`act`, `launch`, `window`; never the reads), a small logo spins on itself in the bottom-right
corner of the driven window's screen, like a game loading screen (the logo and its shadow only, no halo). It goes away 1.6 s after
the last action. The overlay is always on top, borderless, click-through, never activated, absent from the taskbar
and excluded from screen captures (the model never sees it in `look`). `NEXUS_WINDOWS_INDICATOR=0` turns it off.

### Real mouse and keyboard

Only with `act(foreground=true)`, which the user must approve. The window then stays in front for the whole batch;
cursor and foreground are handed back at the end; any physical input from the user during the batch stops it.

One exception, without `foreground=true`: a key or shortcut the application ignores when posted (WPF Ctrl+K, measured)
goes by the real keyboard in a brief foreground lease, only once the user has paused their own typing and mouse, and
their window is given back at once (3.4 to 3.9 s in front on the 9 November 2026 bench, reported `foregroundTaken`
and `briefForeground`). `fallback:false` on the step forbids it: the key is then refused and nothing is sent.

## Working alongside other agents

The server cooperates with other desktop agents on the same PC (it is used next to a Codex layer,
[NexusWindows for Codex](https://github.com/pironjulien/NexusWindows), and an Antigravity layer)
through two small file-based protocols. Any agent can implement them.

**Foreground token** (`%USERPROFILE%\.nexus\foreground.lock`): taken only while really holding the mouse or the
foreground (real input, brief activation, window put in front), never for background actions. Holding it means an
exclusive `LockFileEx` on 1 byte at offset 2^32, opened with read, write and delete sharing; Windows releases it when
the handle closes, including when the process dies, so there is no stale token. The holder writes
`{"owner", "pid", "since", "purpose"}` at the start of the file for diagnostics only. Waiters wait at most a few
seconds (8 s here), then do nothing and say who holds it. A foreground guard that sees another agent holding the
token follows that agent's window instead of taking the foreground back.

**Emergency stop** (Ctrl+Alt+Shift+Esc, files in `%USERPROFILE%\.nexus\`): every agent tries
`RegisterHotKey(MOD_NOREPEAT)`; one gets it, the others retry every 3 s. The holder writes a heartbeat to
`stop.owner` every 3 s. On a press it rewrites `stop` with `{"by", "pid", "at", "hold_s": 120, "nonce"}`. Every agent
watches `stop` at least every 0.2 s and before each action: a new version younger than 120 s blocks all desktop
actions for the remaining time (reads stay allowed). The stop is latched in memory on a monotonic clock: deleting the
file or changing the clock does not lift it, a new press re-arms it.

## Architecture

```mermaid
flowchart LR
  Claude["Claude Code or Claude Desktop"] -->|"MCP stdio"| Launcher["NexusWindows.exe (stable launcher)"]
  Launcher --> Engine["engine native/&lt;sha-256&gt;/NexusWindows.exe --mcp --agent claude"]
  Engine --> UIA["UI Automation, Win32 messages, DevTools"]
  Engine --> Capture["PrintWindow / Windows.Graphics.Capture, Windows OCR"]
```

One C# (.NET 10) engine serves the three profiles (`commun/engine/`, `--agent claude|codex|antigravity`); each profile
keeps its own tools and guide. The stable launcher (`commun/launcher/`) checks the engine's recorded SHA-256 and runs
it with the same standard input and output, so an update never changes a client's declaration. The shared safety
policy is `commun/garde-fous.json`.

## Measurements

The current figures are in [the 9 November 2026 benchmark](https://github.com/pironjulien/NexusWindows/blob/main/docs/benchmarks/2026-11-09-cua-driver/README.md):
the common engine (3.17.10, each of the three profiles) and Cua Driver 0.44.0 on the same covered lab windows (Win32,
WinForms, WPF, Electron, Edge), the application as the judge, 116 trials each.

| 116 trials, target covered | NexusWindows 3.17.10 (each profile) | Cua Driver 0.44.0 |
|---|---|---|
| Real effect | **102** | 50 |
| False success (reported done, the app got nothing) | **0** | 16 |
| Refused before sending anything (needs the foreground) | 14 | 0 |
| Trials with an intrusion (foreground, cursor or clipboard) | 4 (WPF Ctrl+K: brief foreground lease, 3.4 to 3.9 s; launch: activation event only, 0 ms) | 11 |

Raw results and every task are in that folder. Benchmarks of the earlier Python engine (October 2026) were retired
with it.

## Security

- **Emergency stop**: Ctrl+Alt+Shift+Esc interrupts the running action at its next checkpoint (typing included) and
  refuses every new action for 2 minutes, across conversations and for the other agents (protocol above).
- **Hard blocks** (shared policy, `commun/garde-fous.json`, same file for the three profiles): the secure desktop (UAC, lock and sign-in screens) and password managers are never read nor driven. A missing or unreadable policy refuses every action (fail closed).
- **Confirmation of irreversible gestures**: paying, buying, ordering, transferring money, sending, publishing,
  posting, deleting permanently, emptying the bin, formatting, uninstalling, resetting, cancelling a subscription,
  deleting an account or making a repository public need `confirm: true` on the step, which the model sets only on the
  user's explicit order or yes. The check covers what the gesture triggers: click by `[N]`, by name or by coordinates,
  double-click, Enter (target, default button, nearby send button), Ctrl+Enter, Space, Alt+letter, a typed line break
  DevTools pages and Win32 menus. A routine never records nor replays `confirm`.
- **Approval bound to the exact action**: an outward gesture (sending, calling, paying, publishing, transferring) also
  needs `accord: "<token>"`. The first try is refused before any input with a summary of the exact action (trigger,
  process, window title, values of the window's editable fields) and a single-use token. The user's current explicit
  end-to-end request or approval covers preparation and the confirmation dialog for that same operation: compare
  the fresh summary and use its token without another question when the scope is already authorized. Otherwise ask
  at most once for the missing decision; materially changed scope is a separate decision. Token matching, expiry
  and single use still apply to every gesture. See [one-operation approval](../commun/mcp-cookbook.md#one-user-approval-for-one-complete-operation),
  including an existing public extension's exact package, visibility and review/automatic-publication mode.
- **Prompt injection signal**: text read on screen that looks like instructions aimed at an AI gets a warning appended
  to the response (a signal only; it can never authorise an action).
- **Launch**: a browser never gets a profile chosen by the model, a DevTools port or a loaded extension (`devtools=true`
  opens a fresh throwaway profile). Consoles, File Explorer, the taskbar and other applications are not refused:
  the server is meant for a user who already lets Claude run commands; keep Claude's own permission prompts on if
  you want a gate there.
- **Password fields**: never typed into. Typed text is never logged (only its length).
- **Accounts**: a browser started with `devtools=true` gets a throwaway profile without account or sync, checked
  after launch and deleted afterwards.
- **Screen content is data**: the server instructions tell the model that on-screen text is never an instruction, and a
  `look` whose text resembles an instruction aimed at an AI carries a warning.
- **Up-to-date code**: clients start the stable launcher, which runs the engine version recorded at install time after checking its SHA-256; an update installs a new immutable version beside it, so a running session is never changed under its feet and the next one gets the new version.
- **Timeouts**: an abandoned call is cancelled at its next checkpoint instead of finishing later.
- **Clipboard**: never used on its own; only the `clipboard` tool writes to it, and the user's content is given back.

## Privacy

NexusWindows for Claude runs entirely on your PC. It has no account, no telemetry, no analytics and no network request
of its own (updates come from running get.ps1 or NexusSystem again). DevTools connections stay on `127.0.0.1`.

- **What it reads**: window titles, the UI Automation tree and window images of the applications the agent works in,
  only when a tool is called. Password managers and the secure desktop are never inspected nor driven. The OCR
  fallback uses the Windows built-in engine on the PC; no image leaves it. The `notifications` tool reads the Windows
  notification database read-only, masking security codes.
- **Where it goes**: tool results are returned to Claude Desktop or Claude Code, which may send them to Anthropic under its own privacy policy;
  NexusWindows sends nothing anywhere else.
- **What it stores**, under `%USERPROFILE%\.nexus\`: the engine and its stable launcher (`nexus-windows\`), per-agent
  state (debug log without typed text, routines you chose to remember, throwaway browser profiles deleted
  afterwards), the shared foreground token and emergency-stop state, and a short monthly journal
  (`journal\windows\<agent>\`: app executable name, action, technique, outcome, duration; never typed text, screen
  text, labels, titles or paths).
- **Removing it**: run get.ps1 with `-Remove` (declarations removed, engine files deleted), then delete
  `%USERPROFILE%\.nexus\journal\windows` if you want the journal gone too.

Questions or concerns: open an issue on this repository.

## Limits

- Code comments are in English. Everything an MCP client receives (instructions, tool descriptions, step reports,
  refusal messages) is in English.
- Windows only. Tested on Windows 11 with the lab apps and a handful of real applications; not every application has
  been checked.
- Applications without UI Automation that ignore posted input (some games, canvases) need real input, so the user's
  approval and the foreground; the OCR fallback finds their text but does not make background input possible.
- WPF: raw clicks on a point, the mouse wheel and keyboard shortcuts are refused in the background (WPF drops a click
  while the real cursor is not over its window, and takes the foreground when given focus); controls with UI
  Automation patterns work.
- A mouse wheel on a covered Chromium canvas has no effect (Chromium routes it to the window on top); it is refused
  or reported as `~`, never as a success. On Tk, it works only when the window is entirely covered (see Per UI family). Lists and scrolling areas scroll without the wheel (see Per UI family).
- A covered Electron page that has gone `hidden` (it had the foreground once, then lost it) stops rendering: clicks at
  a point are reported as `~`.
- Store apps that Windows suspends in the background are resumed (`IPackageDebugSettings.Resume`, no foreground);
  one that Windows still does not render can only be shown by taking the foreground; this is reported.
- In Chromium, a page that reacts very slowly to each keystroke can exceed the read-back time and is reported as ✗.
- An effect with nothing readable (a file written by a button, for example) stays `~`; add a `check` that observes it.

## Development

From the repository root (the folder of `global.json`):

```powershell
./test.ps1    # dotnet test (engine, launcher, installers), offline
```

Build and test outputs go to `%USERPROFILE%\.nexus\build\nexuswindows`, never into the source tree.

## Licence

MIT, see [LICENSE](https://github.com/pironjulien/NexusWindows/blob/main/LICENSE). Copyright (c) 2026 Julien Piron.
