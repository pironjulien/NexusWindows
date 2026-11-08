# NexusWindows for Codex

<img src="https://raw.githubusercontent.com/pironjulien/NexusWindows/main/plugin/assets/logo.png" alt="NexusWindows icon" width="96">

[![Release](https://img.shields.io/github/v/release/pironjulien/NexusWindows)](https://github.com/pironjulien/NexusWindows/releases)
[![Tests](https://github.com/pironjulien/NexusWindows/actions/workflows/tests.yml/badge.svg)](https://github.com/pironjulien/NexusWindows/actions/workflows/tests.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](https://github.com/pironjulien/NexusWindows/blob/main/LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://github.com/pironjulien/NexusWindows/tree/main/commun/engine)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)](#installation)

[![NexusWindows: Windows computer use for AI agents, in the background. Works with Claude, Codex and Antigravity.](https://raw.githubusercontent.com/pironjulien/NexusWindows/main/codex/docs/assets/banner.webp)](https://julienpiron.fr/nexuswindows/)

**Windows computer use for Codex, in the background.** A native MCP profile (`nexus-windows-codex`) that
lets OpenAI Codex see and operate Windows desktop applications while the user keeps their mouse, keyboard and
foreground window. Codex's own Computer Use came to macOS first; on Windows, OpenAI's documentation says to keep the
target app visible on the active desktop while it runs ([Codex docs](https://developers.openai.com/codex/app/computer-use)).
NexusWindows for Codex has its own local .NET helper for everything: window inventory, UI Automation tree, PrintWindow
capture of covered windows, Win32 messages and UI Automation patterns without focus, and its own real mouse and
keyboard as the last resort, under a shared foreground token. Every action is judged from the application's real
state, and a batch needs an explicit postcondition per step.

Windows 10/11 (x64, ARM64) · native .NET 10, nothing else to install · MIT licence

Part of the Nexus family: [NexusWindows](https://julienpiron.fr/nexuswindows/)
([Claude](https://github.com/pironjulien/NexusWindows) ·
[Codex](https://github.com/pironjulien/NexusWindows) ·
[Antigravity](https://github.com/pironjulien/NexusWindows)),
[NexusBrowser](https://julienpiron.fr/nexusbrowser/) ([repo](https://github.com/pironjulien/NexusBrowser)),
[NexusAndroid](https://julienpiron.fr/nexusandroid/) ([repo](https://github.com/pironjulien/NexusAndroid)).
Project page: [julienpiron.fr/nexuswindows](https://julienpiron.fr/nexuswindows/).

## Why

Codex's built-in Computer Use acts with the physical mouse and keyboard: it brings the target window to the front and
the user has to stop working while it acts. Many Windows applications can be driven without that. NexusWindows for
Codex tries the background techniques first, judges the result from the application's real state, and only uses its
helper's real mouse and keyboard as a fallback, after a pause in the user's own typing and under a token shared with
the other agents on the PC.

The Codex profile is served by the common self-contained .NET engine: no listening network port, no private Electron
API and no Node.js.

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

Windows 10 or 11 and the [Codex app](https://openai.com/codex/) (or the Codex CLI).

**Recommended**: the native engine, nothing else to install. Use the
[NexusSystem app](https://apps.microsoft.com/detail/9NSH2P3QLWT1) from the Microsoft Store, or the release files
(see the [main README](../README.md#-quick-start)): `get.ps1`, checked against `SHA256SUMS.txt` and run with
`powershell -NoProfile -ExecutionPolicy Bypass -File .\get.ps1`, declares
`%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe --mcp --agent codex` in Codex `config.toml`.

**By hand**: Codex declares the same stable command as the other clients, in `~/.codex/config.toml` (shared by the
Codex app, CLI and IDE extension):

```toml
[mcp_servers.nexus-windows]
command = 'C:\Users\you\.nexus\nexus-windows\NexusWindows.exe'
args = ["--mcp", "--agent", "codex"]
```

`NexusWindows.exe --register codex [--check] [--remove]` writes (or checks, or removes) this declaration; an entry of
your own is never changed. Restart Codex, then ask: "Open Notepad and write a shopping list: milk, eggs, bread."
Updates replace the engine behind the same path: running sessions keep their version, new ones get the new one.
The Codex plugin (this profile's guide and skill, `plugin/` at the repository root) comes from the NexusSystem plugin
catalog, which NexusSystem adds to Codex for you (`codex plugin marketplace add pironjulien/NexusSystem` by hand); it
starts the same native engine. The npm package `nexus-windows-codex` (a Node.js adapter, last published as 3.7.4) is
retired and no longer updated: an older `npx -y nexus-windows-codex` declaration is replaced by `--register codex`.

## Tools

| Tool | Purpose |
|---|---|
| `windows(filter)` | Application windows (id, app, title, pid), like the taskbar, plus open dialogs with `dialogOf` (their owner); password managers never listed |
| `look(window, mode, region, marks)` | One window by id or a unique part of its title or app: UI Automation tree, plus a capture that works on covered windows with `mode="image"` or `"both"`; `region` zooms on an area of the last image (`image`: that image's screenshotId); `marks` draws tree line numbers on the image; selects the window for `act` |
| `act(observation, steps)` | One step, or a procedure of any length, each with its own `expect`, on the observation id returned by `look` (or by the previous `act`); refused once another `look` replaced it; stops at the first step not verified |
| `launch(app)` | Start an application (name, path, Store app id), a folder, a document or a Settings page (`ms-settings:`) and give the foreground back |
| `window(action, window)` | `restore`, `minimize`, `maximize`, `bottom`, `move`, `resize`, `front` (with `foreground=true`) or `close`: one close message, read back; a "save?" prompt is reported, never answered |

Step kinds: `click` (selector, or point and native capture id; `button`, `count`), `setValue`, `typeText`, `key`,
`select`, `menu`, `secondary`, `scroll`, `drag`, `hover` (one mouse move, no button), `wait`. Each step reports ✓ verified, = already done, ~ unknown (look again,
never replayed) or ✗ failed or refused.

`windows`, `launch` and `window` also return structured content (MCP `outputSchema` and `structuredContent`: the
window list, the launch or close result) beside the same text, for clients that read data rather than prose.

## Usage

As an MCP server: `windows`, then `look` at one window, then `act` with selectors from that look and an `expect`
that proves the effect. An observation serves one `act` and expires after 60 s. From MCP protocol 2026-07-28 (stateless
MCP), `look` and `window` need a named window and `act` the observation id, `region` its `image`, and every image is attached (never
"unchanged, not attached again"): nothing is carried over implicitly from another call, so two conversations sharing the server never act on each other's window.

## What it does

### Background first, physical input last

Each semantic `click`, `setValue`, `typeText`, `secondary`, `select` and `menu` goes first to the helper:

| UI family | Background technique | Never used |
|---|---|---|
| Win32 | `BN_CLICKED` to the parent (`BM_SETCHECK` for check boxes and radio groups), `EM_SETSEL` + `EM_REPLACESEL`, combo box list opened and closed by `CB_SHOWDROPDOWN` (read back by `CB_GETDROPPEDSTATE`), option chosen by `CB_SETCURSEL` + `CBN_SELCHANGE` + `CBN_SELENDOK` (an open list is closed first: that close is a cancel which restores the previous choice), `LB_SETCURSEL` + `LBN_SELCHANGE`, `TCM_SETCURFOCUS`, menu `WM_COMMAND`; UIA Toggle | UIA Invoke (the Win32 proxy calls `SetForegroundWindow`), UIA Select on a list (silent) |
| WinForms | UIA Invoke/Toggle; messages for Edit, ComboBox (same as Win32, open list included) and ListBox | `BN_CLICKED` on a check box, UIA Select on a list |
| WPF, UWP/WinUI, Chromium/Electron | COM UI Automation with `AutoSetFocus = FALSE`: Invoke, Toggle, Value, ExpandCollapse (state read back), SelectionItem; a combo box is closed again after any choice | |

A measured detail drives the design: once the managed `System.Windows.Automation` client has been used in a process,
a COM Invoke or Toggle from that same process activates the target window despite `AutoSetFocus = FALSE` (WPF and
WinForms on .NET 8). UI Automation patterns are therefore sent from a child process (`--com-worker`) that never loads
the managed client. Since 1.4.0 the managed client is not loaded anywhere: the tree is read with the COM UI
Automation client in **one round trip** (`CacheRequest`, `Subtree` scope, control view; about 50 ms for the bench
forms instead of 150 to 300 ms). For an action, only the target and its ancestors are read again, and any difference
from the observation is refused.

The options of a Win32/WinForms combo box are read by messages (`CB_GETCOUNT`, `CB_GETLBTEXT`, `CB_GETCURSEL`), open
or not, as `list`/`listitem` nodes under it; `expanded` comes from `CB_GETDROPPEDSTATE`. Measured: the UIA subtree of
an open WinForms combo box answers "Malformed Cacheresponse" after about 7 s and then stops answering, so it is never
walked.

Each technique reads its effect back: `applied`, `none` (nothing changed) or `sent` (no read-back possible).
Physical fallback happens only when no technique applies, or when the `expect` is not met **and** nothing observable
changed. A partial effect is never repeated physically; `fallback:false` forbids the physical path. Every result says
which technique acted (for example `background:messages`, `background:uia`, `foreground:uia`),
whether the foreground was taken, and every attempt.

### Explicit postconditions

`expect` distinguishes `verified`, `unmet`, `already-satisfied` and unknown results. An observation is consumed once;
a stale observation, another window, a missing or ambiguous control, or a changed control identity aborts the action
before any input. A truncated UIA value cannot satisfy an exact equality, and an empty tree never proves that a
control disappeared. Native password fields are refused.

A postcondition already true **before** the action skips it (`already-satisfied`, nothing sent) only when it is a
precise state (`equals`) that the action changes: `value`, `checked`, `selected`, `expanded` or `focused` on the
target, or a result on another element. A postcondition that only restates the target ("the button exists") is
refused, and a non-precise one already true (`exists`, `includes`, the target's label) is refused before any input:
it could not tell the effect from the previous state. A combo box choice is proven by the combo box `value`, never by
its option element.

### Clean capture with PrintWindow

`look(mode="image")` captures the window with `PrintWindow(PW_RENDERFULLCONTENT)`
without bringing it forward, even when covered: per-monitor DPI aware, cropped to the DWM frame, DPI-unaware windows
drawn at their logical size and scaled. When PrintWindow fails or returns a uniform image (DirectX surfaces, games),
the helper captures the window itself with Windows.Graphics.Capture (`method: "wgc"`: the window's own DWM surface, no
cursor, no yellow border where Windows allows it, works covered). WinRT and Direct3D 11 are called as raw COM from the
.NET helper: no 25 MB WinRT projection, no package. One capture item is kept per window (16 at most), because
`CreateForWindow` keeps about one kernel handle per call even once released (measured: +1 per capture over 300; 0
over 40 with the kept item, 16 to 20 ms per capture). Still blank: never a screen copy, which would show the window
on top of a covered one; the image is reported as unavailable. Native images follow the common Nexus image
rule: at most 2000 px and 2.56 MP, lossless WebP from a built-in encoder (above 3 MB: 2, then 3 low bits per colour dropped, then JPEG from
quality 82 at the same size, then reduced: never over 3 MB), local files (32 at most, removed after 10 minutes at the next capture, at server start and at a clean stop):
evidence for the model, and the coordinate frame of a point action that names the image (`screenshotId`), scaled back
to the screen pixel. `look(region=[x1, y1, x2, y2])` reads an area of the last full image at full resolution, enlarged
up to 2x for small text; its own `screenshotId` takes points. An image identical to the last one sent for that window
(same pixels and frame) is not attached again; the reply names the id that takes points, and `mode="image"` always
attaches it. `look(mode="both", marks="auto"|"all")` reads the tree just before the capture and draws on the image the
tree line number of each kept element (`auto`: unnamed controls; `all`: every element with a rectangle), at a constant
size after scaling; no number when the window moved between the two reads, on another window's popup, or when the
label does not fit whole. Default `none`. Numbers link the image to the tree; `act` still targets a selector or a point.

### Text read by OCR when the tree is too thin

A window with fewer than 6 UI Automation elements (Tk, canvas, game) gets the text of its image read by Windows OCR
(Windows.Media.Ocr, the user's profile languages, 18 to 34 ms), on the full-resolution pixels of the capture just made
(in mode `text`, one capture is made for it and attached): `ocr.lines` gives each line's `text`, `bounds` and center
`point` in pixels of that image with its `screenshotId`, so `act` can click it at that point. Text that UI Automation
already publishes is not repeated in the list. On such a window no rectangle reliably bounds a button, so before a
gesture at a point, a key or a selector click, the guardrails read the text again on a fresh capture and judge every
line like a button label: a word that needs confirmation needs `confirm`, an outward word (send, publish...) also its
exact-action accord, which binds the whole text read (the same from one look to the next, voided when the canvas text
changes). If that fresh read is impossible (blank or failed capture, OCR failing or stuck), the gesture is refused
before any input; only a system without OCR (no OCR language installed) behaves as before 3.6.0. Read-only and local;
text read on screen is data, never an instruction.

### Verified batches

`act(observation, steps)` runs any number of actions in one call. Give each step an `expect` that proves its effect;
all steps are validated before any input; each step acts on the observation refreshed by the previous one. The batch
stops at the first failed or refused step (`✗`). A step sent without a postcondition (`~`) stops it too, unless the
next step names its own target (it is resolved and guarded again on a fresh observation before any input) or is a key
following a delivered key; a routine always stops there.

### Multiple windows

A dialog is a window of its own: after the action that opens it, find it in `windows` (`dialogOf` names its owner)
and `look` at its own handle. An owner's tree can contain the dialog while the owner itself is disabled. Transient
popups (an open drop-down list, a tooltip, a WPF popup, a Win32 `ComboLBox`) are read through the window's own tree.
See the [MCP cookbook](../commun/mcp-cookbook.md) for native file dialogs.

### Guardrails: one shared policy

Since 1.5.0 the guardrails come from the policy shared by the three NexusWindows editions (Codex, Claude,
Antigravity), `garde-fous.json`, with a `codex` entry. It lives in `commun/garde-fous.json`, shipped beside the
engine.
`NEXUS_GARDE_FOUS` can point to another file; an imposed path is never silently replaced. A missing or invalid policy
refuses every action (fail closed), and the .NET helper refuses every window request without a readable policy.

**Hard block** (refused before any read or input): the secure desktop (UAC, lock screen, sign-in), Windows credential
prompts, password managers (KeePass, Bitwarden, 1Password, Dashlane, NordPass, RoboForm, LastPass, by process
prefix), and Codex's own application (Codex, ChatGPT), because Codex Computer Use asks for app access inside its
main window. Consoles and terminals, File Explorer and administration tools are no longer refused (they were until
1.4.1): the layer is meant for a user who already lets Codex run commands. Password fields stay refused everywhere,
and their value is never read.

**Confirmation** (`confirm:true` on the action, set only on the user's explicit order or after their yes): paying,
calling, sending, publishing, deleting permanently, formatting, uninstalling, resetting, cancelling a subscription,
making a repository public, plus a generic verb (Validate, OK, Start) in a window whose title is about payment or
formatting. The check is on what the gesture triggers, not on its form, before any input and whatever technique
follows: a click by selector or by screenshot coordinates (single or double; a text or icon inside a button counts as
the button), Enter, Ctrl+Enter, Space, Alt+letter (access key or underlined letter), typed text containing a line
break, a menu command, a secondary action other than expand, collapse, toggle, select or scroll. The refusal
(`error.confirmationRequired`) sends nothing; a batch stops at that step. A learned recipe never carries
`confirm`.

An explicit current request or approval for one complete operation also covers its preparation and application
confirmation dialog; do not ask again for each technical token. Compare every fresh action summary with that scope,
and use its fresh single-use token with `confirm:true` when covered. Ask at most once if a decision is missing;
materially changed account, item, artifact, destination, visibility or execution mode requires a separate decision.
An existing public extension update is not first publication or new visibility, but review and automatic-publication
modes must stay authorized. See the [approval example](../commun/mcp-cookbook.md#one-user-approval-for-one-complete-operation).

### The user's presence (Raw Input)

A long-lived `--stop-guard` process registers Raw Input (`RIDEV_INPUTSINK`, message-only window, no hook). It reports
only the instant of each physical keyboard or mouse input, never its content. Injected input has no source
device and is ignored. Before any physical input the engine waits for 1.618 s without
user input (at most 8 s, otherwise the action is deferred and nothing is sent), then types in chunks of at most
48 graphemes and stops (`interrupted`, never resent) if the user takes the keyboard or mouse back. Without Raw
Input, user presence is not known reliably and physical input is deferred (nothing is sent).

### Routines

A verified batch can be saved under a name (`remember`) and replayed later (`routine`), with `{{x}}` parameters; every
step is read back again on replay. Only the definition is stored (static interface labels), never typed text, window
titles, confirmations or accords; `remember` with `steps:[]` forgets it.

### Agent at work

While an action runs (`act`, opening, closing, focusing; never an observation), a small logo spins on itself in the
bottom-right corner of the bound window's screen, like a game loading screen (the logo and its shadow only, no halo). It goes away
1.6 s after the last action. The emergency-stop guard, alive for the whole MCP session, draws it on the commands
`busy <hwnd>` and `idle` (`commun/engine/BusyIndicator.cs`): a layered window that is always on top, borderless,
click-through, never activated, absent from the taskbar and excluded from screen captures. Rendering is analytic
(shadow, coin) from `commun/engine/logo.png` decoded by GDI+, with no added dependency. `NEXUS_WINDOWS_INDICATOR=0`
turns it off.

## Working alongside other agents

The Codex profile runs next to the Claude and Antigravity profiles of the same engine and the other Nexus desktop
tools on the same PC.
They coordinate through two file-based protocols that any agent can implement:

**Foreground token** (`%USERPROFILE%\.nexus\foreground.lock`): held only while really using the mouse or the
foreground (physical input, activation, the foreground UIA fallback), never for reads or background techniques.
Holding it means an exclusive `LockFileEx` on 1 byte at offset 2^32 (in C#: `FileStream` with
`FileShare.ReadWrite | FileShare.Delete`, then `Lock(1L << 32, 1)`). Windows releases it when the handle closes,
including when the process dies. The holder writes `{"owner", "pid", "since", "purpose"}` for diagnostics only. A
waiter waits at most 8 s, then reports a deferred action and sends nothing.

**Emergency stop** (Ctrl+Alt+Shift+Esc, files in `%USERPROFILE%\.nexus\`): one process per PC holds
`RegisterHotKey(MOD_NOREPEAT)` (others retry every 3 s) and beats in `stop.owner` every 3 s; a press rewrites `stop`
with `{"by", "pid", "at", "hold_s": 120, "nonce"}`. Here the `--stop-guard` helper takes the hotkey when nobody else
holds it, for as long as the Codex MCP session lives. The engine watches `stop` every 0.2 s and checks it again
before each UIA action and token acquisition. A new version younger than 120 s
blocks every desktop action for the remaining time; observations stay allowed. The stop is latched in memory on a
monotonic clock: deleting the file or changing the clock does not lift it.

## Architecture

The Codex profile is served by the common C# (.NET 10) engine (`commun/engine/`, started as
`NexusWindows.exe --mcp --agent codex` through the stable launcher `commun/launcher/`), with the shared safety policy
`commun/garde-fous.json`. Its MCP contract (tool names, descriptions and parameters of the Codex profile) is embedded
in the engine (`commun/engine/NativeSchemas.json`). `plugin/` at the repository root holds the plugin of the three AI
apps; Codex reads `.codex-plugin/plugin.json` and `.codex-mcp.json` there.

```mermaid
flowchart LR
    Codex["Codex"] -- "stdio (MCP)" --> Launcher["NexusWindows.exe --mcp --agent codex"]
    Launcher --> Engine["native engine (C#, .NET 10)"]
    Engine --> Messages["Win32 messages"]
    Engine --> ComWorker["COM only worker for UIA patterns"]
    Engine --> Capture["PrintWindow and Windows.Graphics.Capture"]
    Engine --> Guard["stop guard with Raw Input"]
    Guard --> Shared["shared files in USERPROFILE .nexus"]
    Engine --> Shared
```

## Measurements

The current figures are in [the 3 November 2026 benchmark](https://github.com/pironjulien/NexusWindows/blob/main/docs/benchmarks/2026-11-03-cua-driver/README.md):
the common engine (3.16.5, Claude profile) and Cua Driver 0.38.0 on the same covered lab windows (Win32, WinForms, WPF,
Electron, Edge), the application as the judge, 116 trials each.

| 116 trials, target covered | NexusWindows 3.16.5 | Cua Driver 0.38.0 |
|---|---|---|
| Real effect | **98** | 54 |
| False success (reported done, the app got nothing) | **0** | 16 |
| Trials with an intrusion (foreground, cursor or clipboard) | **2** (launch: foreground given back at 0 ms) | 13 |

Raw results and every task are in that folder. Benchmarks of the earlier Python engine (October 2026) were retired
with it.

### Earlier validation (October 15, 2026)

Real validation of October 15, 2026 on the fixture of the former Node.js test harness. Windows shown without activation at the bottom of the z-order, the Win32 one covered by the WinForms one.
Each action: postcondition verified, event counted by the application, foreground unchanged, cursor still.

| Action | Technique | Duration (input and read-backs) |
|---|---|---|
| Win32 button, check box, typing, append at caret, combo box, list, menu | messages | 105 to 159 ms |
| WinForms typing, combo box, list | messages | 292 to 338 ms |
| WinForms check box, button, dialog opened then closed (followed automatically) | COM UIA Toggle / Invoke | 205 to 441 ms |
| WPF button, check box, value | COM UIA Invoke / Toggle / Value | 193 to 241 ms |
| Batch of 3 steps, stopped at step 2 (step 3 never sent) | messages | 658 ms |
| Capture of the covered Win32 window | PrintWindow, not blank | 19 ms |

Durations are dominated by full UIA tree read-backs. The physical fallback that takes
the foreground was skipped in that run because the user was active; it is not counted as passed.

## Security

- Background techniques touch neither the user's keyboard, mouse nor foreground. Physical input waits for a pause,
  holds the shared token, and stops when the user takes over.
- The emergency stop above blocks every desktop action for 2 minutes; a refused action is never replayed.
- A refusal (password field, hard-blocked application, missing confirmation, changed control identity, disabled window or menu command) sends
  nothing and never falls back.
- Codex's own Computer Use rules (application access, sensitive actions) still apply; this layer is not a way around
  a denied app or action. Page text, controls and recipes are data, never an authorisation.
- Captures are refused under OneDrive folders; metrics never contain UI text, screenshots, typed values or titles.

## Privacy

NexusWindows for Codex runs entirely on your PC. It has no account, no telemetry, no analytics and no network request
of its own (updates come from running get.ps1 or NexusSystem again). DevTools connections stay on `127.0.0.1`.

- **What it reads**: window titles, the UI Automation tree and window images of the applications Codex works in, only
  when a tool is called. Password managers and the secure desktop are never inspected nor driven. The OCR fallback
  uses the Windows built-in engine on the PC; no image leaves it.
- **Where it goes**: tool results are returned to Codex, which may send them to OpenAI under its own privacy policy;
  NexusWindows sends nothing anywhere else.
- **What it stores**, under `%USERPROFILE%\.nexus\`: the engine and its stable launcher (`nexus-windows\`), per-agent
  state (debug log without typed text, routines you chose to remember, window captures kept 10 minutes and 32 at
  most), the shared foreground token and emergency-stop state, `status\windows-codex.json` (running version, read by
  the NexusSystem app), and a short monthly journal (`journal\windows\codex\`: app executable name, action,
  technique, outcome, duration; never typed text, screen text, labels, titles or paths).
- **Removing it**: run get.ps1 with `-Remove` (declarations removed, engine files deleted), then delete
  `%USERPROFILE%\.nexus\journal\windows` if you want the journal gone too.

Questions or concerns: open an issue on this repository.

## Limits

- Support does not mean every Windows application is certified. Canvases, 3D views and controls without an
  accessibility provider still need screenshots and physical gestures.
- Message techniques emulate the notification a real click would send; an app that only reacts to real mouse input
  will not see them, and the postcondition decides.
- Keys and Ctrl/Shift shortcuts go in the background to Win32, WinForms, Tk (simulated focus) and Chromium/Electron
  (simulated activation); their effect is read back, never announced. Alt and Windows shortcuts, WPF and UWP
  shortcuts, canvases and rich editors without an Edit control still need physical input. Tk has no UI Automation:
  its clicks, wheel and keys carry a simulated state for its thread only, and its text is known only by OCR (lines
  and points, no roles or states).
- An effect with no trace in the accessibility tree cannot be told from "no effect": give a postcondition that
  observes it, or `fallback:false`.
- `launch` attributes a window to the launch by its process, and says how (`proof`): `process`, the process the
  shell returned; or `application`, for an application named by its executable or Store id (whose launcher may hand
  over to another process), a process of that application born after the request. With `application`, another
  instance of the same application started by something else at the same moment could be taken for it. Files and
  folders need an absolute path; their windows come back as candidates unless the shell returned the process that
  shows them.
- Tool descriptions, tool replies and code comments are in English.

## Development

From the repository root (the folder of `global.json`):

```powershell
./test.ps1         # dotnet test (engine, launcher, installers), offline, same command as the CI
./test.ps1 -Full   # also the live tests: real captures, launches and the PC's shared foreground token
```

Build and test outputs go to `%USERPROFILE%\.nexus\build\nexuswindows`, never into the source tree.

## Licence

MIT, see [LICENSE](https://github.com/pironjulien/NexusWindows/blob/main/LICENSE). Copyright (c) 2026 Julien Piron.
