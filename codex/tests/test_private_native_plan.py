"""Real private TOML planner on fixtures; no profile writes, trust or runtime execution."""
import importlib.util
import json
import shutil
import tomllib
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("private_windows_plan", ROOT / "scripts/plan_private_plugin.py")
planner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(planner)


@pytest.mark.parametrize("server", [
    {"command": "custom.exe", "args": ["private"]},
    {"command": "custom.exe", "enabled": False},
    {"command": "custom.exe", "env": {"CUSTOM": "keep"}},
])
def test_custom_or_disabled_server_and_hook_choices_are_preserved(server):
    fields = []
    for key, value in server.items():
        if isinstance(value, dict):
            fields.append(key + " = { " + ", ".join(k + " = " + json.dumps(v) for k, v in value.items()) + " }")
        else:
            fields.append(key + " = " + json.dumps(value))
    content = "[mcp_servers.nexus-windows]\n" + "\n".join(fields) + '\n[hooks]\ncustom_trust = ["keep"]\n'
    result = planner.plan({"content": content, "marketplace": "personal", "context": {"node": shutil.which("node"), "source_root": str(ROOT)}})
    after = tomllib.loads(result["content"])
    assert after["mcp_servers"]["nexus-windows"] == server
    assert after["hooks"] == {"custom_trust": ["keep"]}
    assert result["plugins"]["nexus-windows"]["guide_only"]


@pytest.mark.parametrize("args", [["--mcp", "--agent", "codex"], ["--mcp", "--agent", "claude"], ["--mcp", "--agent", "codex", "--custom"]])
def test_only_the_exact_owned_native_codex_signature_can_migrate(args):
    executable = Path.home() / ".nexus/nexus-windows/native" / ("a" * 64) / "NexusWindows.exe"
    content = '[mcp_servers.nexus-windows]\ncommand = ' + json.dumps(str(executable)) + '\nargs = ' + json.dumps(args) + '\n'
    result = planner.plan({"content": content, "marketplace": "personal", "context": {"node": shutil.which("node"), "source_root": str(ROOT)}})
    servers = tomllib.loads(result["content"]).get("mcp_servers", {})
    assert ("nexus-windows" not in servers) == (args == ["--mcp", "--agent", "codex"])
