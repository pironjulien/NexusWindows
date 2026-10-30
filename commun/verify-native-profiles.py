"""Read real installed declarations, hash every artifact and probe each exact native command.

Read-only Windows query with an impossible filter; no notification contents, personal app gestures or reloads.
Python is qualification tooling only, never the production MCP server.
"""
from __future__ import annotations

import hashlib
import argparse
import json
import os
import subprocess
import tempfile
from pathlib import Path


def read(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--desktop-config", type=Path, help="Existing config path discovered by the Desktop installer; never guessed here")
    options = parser.parse_args()
    home = Path(os.environ["USERPROFILE"])
    manifest = read(home / ".nexus/nexus-windows/installation.json")
    executable = Path(manifest["NativeExecutable"])
    assert manifest["Schema"] == 2 and manifest["Version"] == "3.8.0"
    assert executable == home / ".nexus/nexus-windows/native" / manifest["NativeHash"] / "NexusWindows.exe"
    hashes = {}
    for name, digest in manifest["NativeArtifacts"].items():
        assert Path(name).name == name and name not in (".", ".."), name
        assert not (executable.parent / name).is_symlink(), name
        actual = hashlib.sha256((executable.parent / name).read_bytes()).hexdigest()
        assert actual.lower() == digest.lower(), name
        hashes[name] = actual
    registry = read(home / ".claude/plugins/installed_plugins.json")
    claude = [Path(item["installPath"]) / ".mcp.json" for item in registry["plugins"]["nexus-windows@nexus"]]
    paths = {
        "codex": [home / ".codex/plugins/nexus-windows/.mcp.json", *sorted((home / ".codex/plugins/cache").glob("*/nexus-windows/*/.mcp.json"))],
        "claude": claude,
        "antigravity": [home / ".gemini/config/plugins/nexus-windows/mcp_config.json"],
    }
    if options.desktop_config:
        assert options.desktop_config.is_absolute() and options.desktop_config.is_file()
        paths["claude-desktop"] = [options.desktop_config]
    report = {"version": manifest["Version"], "sourceHash": manifest["NativeHash"], "executable": str(executable), "artifacts": hashes, "desktopChatToolInvocationVerified": False, "profiles": {}}
    for profile, configs in paths.items():
        agent = "claude" if profile == "claude-desktop" else profile
        assert configs, agent
        for config in configs:
            server = read(config)["mcpServers"]["nexus-windows"]
            assert Path(server["command"]) == executable, config
            assert server["args"] == ["--mcp", "--agent", agent], config
        with tempfile.TemporaryDirectory(prefix="nexus-installed-probe-") as isolated:
            env = {**os.environ, "NEXUS_WINDOWS_HOME": isolated, "NEXUS_SHARED_HOME": isolated, "NEXUS_ROUTINES_MIRROR": "", "NEXUS_AGY_ROUTINES_MIRROR": "", "NEXUS_JOURNAL_HOME": str(Path(isolated) / "journal")}
            requests = [
                {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "installed-profile-qualification", "version": "1"}}},
                {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
                {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "windows", "arguments": {"filter": "NexusWindowsQualificationNoSuchWindow-6e897b"}}},
            ]
            result = subprocess.run([server["command"], *server["args"]], input="".join(json.dumps(r) + "\n" for r in requests), env=env, capture_output=True, text=True, encoding="utf-8", timeout=20, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
            replies = {r["id"]: r["result"] for line in result.stdout.splitlines() if (r := json.loads(line)).get("id")}
            assert replies[1]["serverInfo"]["version"] == manifest["Version"]
            assert replies[1]["serverInfo"]["name"] == f"nexus-windows-{agent}"
            names = [t["name"] for t in replies[2]["tools"]]
            assert len(names) == (7 if agent == "claude" else 5), names
            assert not replies[3].get("isError"), replies[3]
            assert replies[3]["structuredContent"]["windows"] == []
            report["profiles"][profile] = {"agent": agent, "declarations": list(map(str, configs)), "args": server["args"], "tools": names, "stdio": "verified", "readOnlyProbe": "verified", "realHostConnectionVerified": False}
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
