# NexusWindows and Cua Driver on Windows (3 November 2026)

Google and Cua AI published a tutorial on 2 November 2026: "Computer use with Gemini and Cua Driver". Cua Driver
(trycua/cua) drives desktop apps in the background through the accessibility tree, as NexusWindows does. We ran
Cua Driver 0.38.0 through the bench NexusWindows uses for every change: same apps, same tasks, same judge.

Cua Driver did well on the basics, and its covered-window capture was much faster than ours. Cua is pre-release
software, and these are Windows results only. Benchmarks on other systems, such as Linux, can show other results.

## Method

- **Tool only, no model.** The bench issues the calls itself, so we measure the tool, not a model's choices.
- **Windows 11, background mode.** The lab window is covered by another window during every trial. Any foreground
  change, real cursor move or clipboard change is recorded as an intrusion.
- **The application is the judge.** Each lab app logs every event it receives. A trial succeeds only when the
  expected event arrives exactly once (the bench keeps listening 0.8 s for a duplicate).
- **False success**: the tool reports success but the app received nothing.
- **Families**: Win32, WinForms, WPF, Electron, Edge (app mode), a launch behind a maximized window, and a field below
  the fold of a long Chromium page. Two rounds, tool order alternated, a new value each time.
- **NexusWindows**: engine 3.16.5, Claude profile, `look` then one `act` batch with labelled targets.
- **Cua Driver**: 0.38.0, used as its tutorial agent uses it. The bench calls `get_window_state` (elements and
  tokens), then one action per element token, with the default `background` delivery. It uses pixel coordinates
  only for the raw canvas click and wheel tasks. Other choices: `set_value` for fields and drop-downs, `invoke_menu`
  for menus, `hotkey` for shortcuts, `launch_app` for the launch task.

## Totals (116 trials each)

| | Real effect | False success | Trials with an intrusion |
|---|---|---|---|
| NexusWindows 3.16.5 | 98 | 0 | 2 (launch: foreground given back at 0 ms) |
| Cua Driver 0.38.0 | 54 | 16 | 13 |

NexusWindows often reports "unverified" (sent, effect not confirmed) where it cannot prove the effect. It never
reported success without one.

## By task (successes out of 2)

| Task | Family | NexusWindows | Cua Driver |
|---|---|---|---|
| Button | all five | 10/10 | 10/10 (intrusion in 3 trials) |
| Checkbox | all five | 10/10 | 10/10 |
| Text field | all five | 10/10 | 10/10 |
| Drop-down | Win32, WinForms | 4/4 | 4/4 |
| Drop-down | WPF | 2/2 | 0/2 |
| Drop-down (`<select>`) | Electron, Edge | 4/4 | 0/4, 4 false successes |
| List item | Win32, WinForms | 4/4 | 0/4, 4 false successes |
| List item | WPF, Electron, Edge | 6/6 | 6/6 |
| Menu | Win32 | 2/2 | 0/2, foreground taken |
| Menu | WinForms, WPF | 4/4 | 4/4, foreground taken |
| Raw click (canvas) | Win32, WinForms, Electron, Edge | 8/8 | 8/8 |
| Raw click (canvas) | WPF | 0/2 | 0/2 |
| Mouse wheel | all five | 10/10 | 0/10 (background not available) |
| Ctrl+K | Win32, WinForms | 4/4 | 0/4, 4 false successes |
| Ctrl+K | Electron | 2/2 | 0/2 |
| Ctrl+K | WPF, Edge | 0/4 | 0/4 |
| Rich text editor | Electron, Edge | 4/4 | 0/4 (background not available) |
| Double click | WinForms | 0/2 | 0/2, 2 false successes, foreground taken |
| Double click | WPF | 0/2 | 0/2 |
| Context menu | WinForms | 2/2 | 0/2 |
| Context menu | WPF | 0/2 | 0/2 |
| Modal dialog | WinForms | 2/2 | 0/2 (call blocked, cut at 120 s) |
| Modal dialog | WPF | 2/2 | 2/2 |
| Drag and drop | Electron, Edge | 4/4 | 0/4 |
| Drag and drop | Win32, WinForms, WPF | 0/6 | 0/6 |
| Launch behind a maximized window, then click | Electron | 2/2 | 0/2 (app stayed in front about 7 s) |
| Field below the fold of a long page | Chromium | 2/2 | 0/2, 2 false successes |

Covered-window capture (first look, ms): Cua Driver 55 to 126, NexusWindows 165 to 1481.

## Limits of this run

- These are our bench and our adapter for Cua Driver. In two cases the target was missing from Cua's element list:
  the drag token (static text) and the WinForms list (context menu). A model would fall back to pixels there, so
  those failures belong to the adapter as much as to the tool.
- Two rounds per task: small samples, read the table as patterns, not percentages.
- Windows only. Cua Driver's Windows support is more recent than its macOS and Linux support.

## Raw data

`results-native.json` (Win32, WinForms, WPF) and `results-web.json` (Electron, Edge, launch, long page) hold every
trial: events received, claimed status, intrusions, timing and the tool's reply.
