# NexusWindows

Windows computer use **in the background** for AI agents: your desktop apps observed through UI Automation and
driven without taking your mouse, keyboard or foreground, with every action read back and outward actions (send, pay,
publish, delete) held for your explicit yes. One repository, one common engine, three profiles:

| Profile | Folder | For |
|---|---|---|
| Claude | [`claude/`](claude/README.md) | Claude Code and Claude Desktop |
| Codex | [`codex/`](codex/README.md) | the OpenAI Codex desktop app |
| Antigravity | [`agy/`](agy/README.md) | Google Antigravity |

For current MCP requests, native file dialogs, UTF-8 stdio and blank Chromium pages, use the shared
[MCP cookbook](commun/mcp-cookbook.md).

[![Tests](https://github.com/pironjulien/NexusWindows/actions/workflows/tests.yml/badge.svg)](https://github.com/pironjulien/NexusWindows/actions/workflows/tests.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/pironjulien/NexusWindows/badge)](https://scorecard.dev/viewer/?uri=github.com/pironjulien/NexusWindows)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

## Install

Windows 10 or 11, x64 or ARM64; nothing else to install (one self-contained native program, no Python, Node.js or .NET
runtime needed).

Let your AI install it: NexusSystem detects your AI apps and sets up the right profile.

```text
Install Nexus for me by following https://raw.githubusercontent.com/pironjulien/NexusSystem/main/INSTALL-AI.md
```

Or get the [NexusSystem app](https://apps.microsoft.com/detail/9NSH2P3QLWT1) from the Microsoft Store
(`winget install --id 9NSH2P3QLWT1 --source msstore`). By hand, from the release files (no administrator rights):

```powershell
$r = 'https://github.com/pironjulien/NexusWindows/releases/latest/download'
Invoke-WebRequest "$r/get.ps1" -OutFile get.ps1
Invoke-WebRequest "$r/SHA256SUMS.txt" -OutFile SHA256SUMS.txt
(Get-FileHash get.ps1 -Algorithm SHA256).Hash    # must equal the get.ps1 line of SHA256SUMS.txt
gh attestation verify get.ps1 --repo pironjulien/NexusWindows   # optional: build provenance
powershell -NoProfile -ExecutionPolicy Bypass -File .\get.ps1
```

get.ps1 downloads `NexusWindows-win-x64.zip` or `NexusWindows-win-arm64.zip` from the latest release, checks its
SHA-256 against `SHA256SUMS.txt`, installs the engine under `%USERPROFILE%\.nexus\nexus-windows` and declares it to
Claude Code, Claude Desktop, Codex and Antigravity when they are present. Every client starts the same stable path,
`%USERPROFILE%\.nexus\nexus-windows\NexusWindows.exe --mcp --agent <claude|codex|antigravity>`; updates never change
it. Release files carry a build provenance attestation (`gh attestation verify <file> --repo pironjulien/NexusWindows`).
The Claude Desktop extension is `nexus-windows-claude.mcpb` in each release.

## Layout

- `codex/native/`: the common C# (.NET 10) engine, `NexusWindows.exe`, serving the three profiles as MCP stdio
  servers (`--mcp --agent claude|codex|antigravity`) and declaring itself to them (`--register`).
- `commun/`: the shared safety policy (`garde-fous.json`), the stable launcher (`launcher/`), the workstation
  installer of the immutable engine (`native-runtime.ps1`) and the release packaging (`package.ps1`).
- `claude/`, `agy/`, `codex/`: each profile's guides, plugin and documentation; `codex/` also keeps the Node.js
  adapter of the Codex REPL guide (`lib/`, npm package `nexus-windows-codex`).
- `tests/`: xUnit tests of the engine, the launcher and the installers.

## Development

```powershell
./test.ps1    # dotnet test (xUnit), then node --test of the Codex adapter; offline, same command as the CI
```

Build and test outputs go to `%USERPROFILE%\.nexus\build\nexuswindows`. The three profiles were separate repositories
until 30 October 2026 (`NexusWindows-Claude`, `NexusWindows-Codex`, `NexusWindows-Antigravity`, now archived); their
release history stays there.

## Privacy

Everything runs on your PC. Nothing is sent anywhere by NexusWindows; screenshots, logs and routines stay under
`%USERPROFILE%\.nexus`. See [SECURITY.md](SECURITY.md) to report a vulnerability.
