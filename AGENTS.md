# Development instructions

## Pitfalls

- One engine, three profiles (Claude, Codex, Antigravity): every tool and capability goes to all three, never behind an
  `Agent == "claude"` branch. `The_three_profiles_serve_the_same_tools_and_capabilities` fails when a profile misses a
  tool; search the diff for `Agent == "claude"` before pushing. About 57 older Claude-only branches remain from the
  former repositories: harmonize them when touching that code.
- Any write into state shared with the user (clipboard, focus, foreground) is a loan returned automatically, and the
  user's newer action always wins: without clipboard history, an overwritten copy is lost for good. Test such changes
  only when the user's clipboard content no longer matters.
- Background gestures stay on native paths (UI Automation, simulated keyboard state for the app's thread, accessibility
  action at the target point). Forcing a CDP port on Chromium apps or running apps in a separate session or desktop
  were tried and rejected; CDP is only for the AI apps' already-open ports, when the native path would risk the account.
- Never send a second technique after a first one was sent for the same step (no double action).
- Restart Antigravity through its Start menu shortcut, which carries `--remote-debugging-port`; launching
  `Antigravity.exe` directly loses the port and cuts the relay and the keypad off from it.
