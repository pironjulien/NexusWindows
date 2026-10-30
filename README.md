# NexusWindows

Windows computer use **in the background** for AI agents: your desktop apps observed through UI Automation and
driven without taking your mouse, keyboard or foreground, with every action read back and outward actions (send, pay,
publish, delete) held for your explicit yes. One repository, one common engine, three profiles:

| Profile | Folder | For |
|---|---|---|
| Claude | [`claude/`](claude/README.md) | Claude Code and Claude Desktop |
| Codex | [`codex/`](codex/README.md) | the OpenAI Codex desktop app |
| Antigravity | [`agy/`](agy/README.md) | Google Antigravity |

[![Tests](https://github.com/pironjulien/NexusWindows/actions/workflows/tests.yml/badge.svg)](https://github.com/pironjulien/NexusWindows/actions/workflows/tests.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/pironjulien/NexusWindows/badge)](https://scorecard.dev/viewer/?uri=github.com/pironjulien/NexusWindows)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

## Install

Let your AI install it: NexusSystem detects your AI apps and sets up the right profile.

```text
Install Nexus for me by following https://raw.githubusercontent.com/pironjulien/NexusSystem/main/INSTALL-AI.md
```

Or get the [NexusSystem app](https://apps.microsoft.com/detail/9NSH2P3QLWT1) from the Microsoft Store. Each profile's
folder also documents a manual install.

## Layout

- `codex/native/`: the common C# (.NET 10) engine, `NexusWindows.exe`, serving the three profiles as MCP stdio
  servers (`--mcp --agent claude|codex|antigravity`). Each profile keeps its host's guides and policies.
- `commun/`: the shared safety policy (`garde-fous.json`, `garde_fous.py`), the foreground lock and the native
  runtime publisher (`native-runtime.ps1`), embedded by every profile.
- `claude/`, `agy/` (Python) and `codex/` (Node.js): each profile's adapter, guides and earlier engine, kept as an
  explicit fallback while the native engine is qualified.

## Development

```powershell
./test.ps1    # shared policy tests, then each profile's own test.ps1 (offline)
```

The three profiles were separate repositories until 30 October 2026 (`NexusWindows-Claude`, `NexusWindows-Codex`,
`NexusWindows-Antigravity`, now archived); their release history stays there.

## Privacy

Everything runs on your PC. Nothing is sent anywhere by NexusWindows; screenshots, logs and routines stay under
`%USERPROFILE%\.nexus`. See [SECURITY.md](SECURITY.md) to report a vulnerability.
