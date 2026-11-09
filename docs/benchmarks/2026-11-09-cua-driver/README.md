# NexusWindows and Cua Driver on Windows (9 November 2026)

The same bench as [the 3 November run](../2026-11-03-cua-driver/README.md), redone with the current versions of both
tools: NexusWindows 3.17.10 (its three profiles, Claude, Antigravity and Codex) and Cua Driver 0.44.0 (trycua/cua,
released 8 November 2026). Cua is pre-release software; these are Windows results only.

## Method

- **Tool only, no model.** The bench issues the calls itself, so we measure the tool, not a model's choices.
- **Windows 11, background mode.** The lab window is covered by another window during every trial. Any foreground
  change, real cursor move or clipboard change is recorded as an intrusion.
- **The application is the judge.** Each lab app logs every event it receives. A trial succeeds only when the
  expected event arrives exactly once (the bench keeps listening 0.8 s for a duplicate).
- **False success**: the tool reports success but the app received nothing.
- **Clean refusal**: NexusWindows refused before sending anything because the task needs the foreground
  ("Needs the foreground"); the app received nothing.
- **Families**: Win32, WinForms, WPF, Electron, Edge (app mode), a launch behind a maximized window, and a field below
  the fold of a long Chromium page. Two rounds, tool order alternated, a new value each time.
- **NexusWindows**: engine 3.17.10 (`act(foreground=false)`), `look` then one `act` batch with labelled targets.
- **Cua Driver**: 0.44.0 (`cua-driver-rs-0.44.0-windows-x86_64-binary.zip`, SHA-256 checked against the release's
  `SHA256SUMS`, Authenticode signature valid), driven as on 3 November: `get_window_state`, then one action per
  element token with the default `background` delivery.

## Totals (116 trials each)

| | Real effect | Real effect, no intrusion | False success | Clean refusals | Trials with an intrusion |
|---|---|---|---|---|---|
| NexusWindows 3.17.10, Claude profile | 102 | 98 | 0 | 14 | 4 |
| NexusWindows 3.17.10, Antigravity profile | 102 | 98 | 0 | 14 | 4 |
| NexusWindows 3.17.10, Codex profile | 102 | 98 | 0 | 14 | 4 |
| Cua Driver 0.44.0 | 50 of 114 | 43 of 114 | 16 | 0 | 11 |

Cua's two launch trials are not counted: its launched window was not covered when captured, so the trial was invalid.

NexusWindows' intrusions, per profile: Ctrl+K on WPF (2), which WPF ignores when posted and which goes by the real
keyboard in a brief foreground lease (3.4 to 3.9 s in front, window given back); and the launch (2), where Windows
signals an activation of the newly started app that never reached the front ("event only", 0 ms).
Since 3 November: Ctrl+K on WPF went from refused to done; the rest is unchanged.

## By task (successes out of 2)

| Task | Family | Claude | Antigravity | Codex | Cua Driver |
|---|---|---|---|---|---|
| Button | Electron | 2/2 | 2/2 | 2/2 | 2/2 intrusion |
| Button | Edge | 2/2 | 2/2 | 2/2 | 2/2 |
| Button | Win32 | 2/2 | 2/2 | 2/2 | 2/2 intrusion |
| Button | WinForms | 2/2 | 2/2 | 2/2 | 2/2 |
| Button | WPF | 2/2 | 2/2 | 2/2 | 1/2 |
| Checkbox | Electron | 2/2 | 2/2 | 2/2 | 2/2 |
| Checkbox | Edge | 2/2 | 2/2 | 2/2 | 2/2 |
| Checkbox | Win32 | 2/2 | 2/2 | 2/2 | 2/2 |
| Checkbox | WinForms | 2/2 | 2/2 | 2/2 | 2/2 |
| Checkbox | WPF | 2/2 | 2/2 | 2/2 | 1/2 |
| Text field | Electron | 2/2 | 2/2 | 2/2 | 2/2 |
| Text field | Edge | 2/2 | 2/2 | 2/2 | 2/2 |
| Text field | Win32 | 2/2 | 2/2 | 2/2 | 2/2 |
| Text field | WinForms | 2/2 | 2/2 | 2/2 | 2/2 |
| Text field | WPF | 2/2 | 2/2 | 2/2 | 1/2 |
| Rich text editor | Electron | 2/2 | 2/2 | 2/2 | 0/2 |
| Rich text editor | Edge | 2/2 | 2/2 | 2/2 | 0/2 |
| Drop-down | Electron | 2/2 | 2/2 | 2/2 | 0/2 false success |
| Drop-down | Edge | 2/2 | 2/2 | 2/2 | 0/2 false success |
| Drop-down | Win32 | 2/2 | 2/2 | 2/2 | 2/2 |
| Drop-down | WinForms | 2/2 | 2/2 | 2/2 | 2/2 |
| Drop-down | WPF | 2/2 | 2/2 | 2/2 | 0/2 |
| List item | Electron | 2/2 | 2/2 | 2/2 | 2/2 |
| List item | Edge | 2/2 | 2/2 | 2/2 | 2/2 |
| List item | Win32 | 2/2 | 2/2 | 2/2 | 0/2 false success |
| List item | WinForms | 2/2 | 2/2 | 2/2 | 0/2 false success |
| List item | WPF | 2/2 | 2/2 | 2/2 | 1/2 |
| Menu | Win32 | 2/2 | 2/2 | 2/2 | 0/2 intrusion |
| Menu | WinForms | 2/2 | 2/2 | 2/2 | 2/2 intrusion |
| Menu | WPF | 2/2 | 2/2 | 2/2 | 2/2 intrusion |
| Raw click (canvas) | Electron | 2/2 | 2/2 | 2/2 | 2/2 |
| Raw click (canvas) | Edge | 2/2 | 2/2 | 2/2 | 2/2 |
| Raw click (canvas) | Win32 | 2/2 | 2/2 | 2/2 | 2/2 |
| Raw click (canvas) | WinForms | 2/2 | 2/2 | 2/2 | 2/2 |
| Raw click (canvas) | WPF | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 |
| Mouse wheel | Electron | 2/2 | 2/2 | 2/2 | 0/2 |
| Mouse wheel | Edge | 2/2 | 2/2 | 2/2 | 0/2 |
| Mouse wheel | Win32 | 2/2 | 2/2 | 2/2 | 0/2 |
| Mouse wheel | WinForms | 2/2 | 2/2 | 2/2 | 0/2 |
| Mouse wheel | WPF | 2/2 | 2/2 | 2/2 | 0/2 |
| Ctrl+K | Electron | 2/2 | 2/2 | 2/2 | 0/2 |
| Ctrl+K | Edge | 2/2 | 2/2 | 2/2 | 0/2 |
| Ctrl+K | Win32 | 2/2 | 2/2 | 2/2 | 0/2 false success |
| Ctrl+K | WinForms | 2/2 | 2/2 | 2/2 | 0/2 false success |
| Ctrl+K | WPF | 2/2 intrusion | 2/2 intrusion | 2/2 intrusion | 0/2 |
| Double click | WinForms | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 false success intrusion |
| Double click | WPF | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 |
| Context menu | WinForms | 2/2 | 2/2 | 2/2 | 0/2 |
| Context menu | WPF | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 |
| Modal dialog | WinForms | 2/2 | 2/2 | 2/2 | 0/2 |
| Modal dialog | WPF | 2/2 | 2/2 | 2/2 | 2/2 |
| Drag and drop | Electron | 2/2 | 2/2 | 2/2 | 0/2 |
| Drag and drop | Edge | 2/2 | 2/2 | 2/2 | 0/2 |
| Drag and drop | Win32 | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 |
| Drag and drop | WinForms | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 |
| Drag and drop | WPF | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 (clean refusal) | 0/2 |
| Launch behind a maximized window, then click | Electron (launch) | 2/2 intrusion | 2/2 intrusion | 2/2 intrusion | not counted (window not covered at capture) |
| Field below the fold of a long page | Chromium | 2/2 | 2/2 | 2/2 | 0/2, 2 false successes |

Covered-window capture (first look, ms): Cua Driver 55 to 77, NexusWindows 137 to 1905 (WPF and Electron slowest).

## Limits of this run

- One PC (Windows 11, laptop), lab applications only; real applications vary.
- The NexusWindows engine was built from the tagged 3.17.10 source; the release files are the same code.
- Raw results: [`results.json`](results.json) (user paths replaced by `~`).