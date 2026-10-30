"""Steam : identité des cibles, précontrôles avant fermeture et transmission unique des commandes (sans GUI)."""

from __future__ import annotations

import ctypes
import json
import socket
import struct
from types import SimpleNamespace

import pytest

from nexus_windows import cdp, launch, policy, snapshot, steam
from nexus_windows import win32 as w


@pytest.fixture
def installation(tmp_path, monkeypatch):
    exe = tmp_path / "Steam" / "steam.exe"
    exe.parent.mkdir()
    exe.touch()
    (exe.parent / "steamclient64.dll").touch()
    helper = exe.parent / "bin" / "steamwebhelper.exe"
    helper.parent.mkdir()
    helper.touch()
    paths = {10: str(exe), 11: str(helper), 12: str(helper)}
    table = [(10, 1, "steam.exe"), (11, 10, "steamwebhelper.exe"), (12, 11, "steamwebhelper.exe")]
    monkeypatch.setattr(w, "process_path", lambda pid: paths.get(pid, ""))
    monkeypatch.setattr(steam, "_identity", lambda pid: pid * 100)
    monkeypatch.setattr(steam, "_table", lambda: table)
    return exe, helper, paths, table


def test_client_identity_requires_absolute_existing_canonical_path(installation):
    exe, _helper, _paths, _table = installation
    assert steam.canonical_exe(str(exe)) == str(exe.resolve())
    assert steam.is_steam_exe(str(exe))
    assert not steam.is_steam_exe("steam.exe")
    assert not steam.is_steam_exe(str(exe.parent / "missing" / "steam.exe"))


def test_root_must_be_the_selected_installation(installation, tmp_path):
    exe, _helper, _paths, table = installation
    assert steam.steam_pids(table, str(exe)) == [10]
    other = tmp_path / "Other" / "steam.exe"
    other.parent.mkdir()
    other.touch()
    (other.parent / "steamui.dll").touch()
    with pytest.raises(steam.SteamError, match="expected client path"):
        steam.steam_pids(table, str(other))


def test_unreadable_root_identity_is_an_error(installation):
    _exe, _helper, paths, table = installation
    paths[10] = ""
    with pytest.raises(steam.SteamError, match="expected client path"):
        steam.steam_pids(table)


def test_helper_requires_lineage_and_installation_path(installation, tmp_path):
    _exe, _helper, paths, table = installation
    foreign = tmp_path / "steamwebhelper.exe"
    foreign.touch()
    paths[12] = str(foreign)
    paths[99] = paths[11]
    table.append((99, 1, "steamwebhelper.exe"))
    assert steam.webhelper_pids(table) == {11}


def test_game_renamed_as_internal_helper_is_not_exempted_from_restart_guard(installation, tmp_path, monkeypatch):
    exe, _helper, paths, _table = installation
    foreign = tmp_path / "steamwebhelper.exe"
    foreign.touch()
    paths[12] = str(foreign)
    monkeypatch.setattr(steam.subprocess, "Popen", lambda *a, **kw: pytest.fail("Jeu renommé fermé"))
    assert steam.games_running() == ["steamwebhelper.exe"]
    with pytest.raises(steam.SteamError, match="steamwebhelper.exe"):
        steam.shutdown(str(exe))


def test_unreadable_internal_helper_is_not_exempted(installation):
    _exe, _helper, paths, _table = installation
    paths[12] = ""
    assert steam.games_running() == ["steamwebhelper.exe"]


@pytest.mark.parametrize("folder", ["System32", "SysWOW64"])
def test_real_system_conhost_can_be_exempted(installation, tmp_path, monkeypatch, folder):
    _exe, _helper, paths, table = installation
    system = tmp_path / "Windows"
    image = system / folder / "conhost.exe"
    image.parent.mkdir(parents=True)
    image.touch()
    paths[13] = str(image)
    table.append((13, 10, "conhost.exe"))
    monkeypatch.setenv("SystemRoot", str(system))
    assert steam.games_running() == []


def test_empty_native_process_table_is_unknown(monkeypatch):
    monkeypatch.setattr(w, "process_table", lambda: [])
    with pytest.raises(steam.SteamError, match="process table unavailable"):
        steam._table()


@pytest.mark.parametrize("titles", [{101: "Steam", 102: "Steam"}, {101: "Store", 102: "Library"}])
def test_ambiguous_hwnd_is_refused(installation, monkeypatch, titles):
    monkeypatch.setattr(w, "enum_windows", lambda: list(titles))
    monkeypatch.setattr(w, "window_pid", lambda hwnd: 11)
    monkeypatch.setattr(w, "window_text", lambda hwnd: titles[hwnd])
    monkeypatch.setattr(snapshot, "is_app_window", lambda hwnd: True)
    with pytest.raises(steam.SteamError, match="ambiguous"):
        steam.main_window()


def test_unique_main_title_is_selected_over_secondary_windows(installation, monkeypatch):
    titles = {101: "Steam", 102: "Settings", 103: "Steam"}
    monkeypatch.setattr(w, "enum_windows", lambda: list(titles))
    monkeypatch.setattr(w, "window_pid", lambda hwnd: 99 if hwnd == 103 else 11)
    monkeypatch.setattr(w, "window_text", lambda hwnd: titles[hwnd])
    monkeypatch.setattr(snapshot, "is_app_window", lambda hwnd: True)
    assert steam.main_window() == 101


@pytest.mark.parametrize(
    "rows",
    [
        [(8080, "127.0.0.1", 99)],
        [(8080, "0.0.0.0", 11)],
        [(8080, "127.0.0.1", 11), (8080, "::", 11)],
        [(8080, "127.0.0.1", 11), (8080, "127.0.0.1", 12)],
    ],
)
def test_port_collision_refuses_foreign_same_name_wildcard_and_ambiguous_owner(installation, monkeypatch, rows):
    monkeypatch.setattr(steam, "listeners", lambda: rows)
    monkeypatch.setattr(w, "process_name", lambda pid: "steamwebhelper.exe")
    monkeypatch.setattr(steam, "_cdp_alive", lambda port: pytest.fail("Endpoint étranger contacté"))
    assert steam.port_taken_by_other() is not None
    assert steam.debugging_port() is None


def test_verified_owned_loopback_endpoint_can_be_reused(installation, monkeypatch):
    monkeypatch.setattr(steam, "listeners", lambda: [(8080, "127.0.0.1", 11)])
    monkeypatch.setattr(steam, "_cdp_alive", lambda port: port == 8080)
    assert steam.port_taken_by_other() is None
    assert steam.debugging_port() == 8080


def test_dual_loopback_with_same_verified_owner_is_allowed(installation, monkeypatch):
    monkeypatch.setattr(steam, "listeners", lambda: [(8080, "127.0.0.1", 11), (8080, "::1", 11)])
    monkeypatch.setattr(steam, "_cdp_alive", lambda port: port == 8080)
    assert steam.port_taken_by_other() is None
    assert steam.debugging_port() == 8080


@pytest.mark.parametrize("changed", ["identity", "owner", "image"])
def test_endpoint_owner_and_process_identity_are_rechecked_after_cdp(installation, monkeypatch, changed):
    _exe, _helper, paths, _table = installation
    rows = [(8080, "127.0.0.1", 11)]
    identity = [1100]
    monkeypatch.setattr(steam, "listeners", lambda: rows.copy())
    monkeypatch.setattr(steam, "_identity", lambda pid: identity[0])

    def probe(port):
        if changed == "identity":
            identity[0] = 2200
        elif changed == "owner":
            rows[:] = [(8080, "127.0.0.1", 99)]
        else:
            paths[11] = ""
        return True

    monkeypatch.setattr(steam, "_cdp_alive", probe)
    assert steam.debugging_port() is None


@pytest.mark.parametrize(
    "url,reply,expected",
    [
        ("ws://127.0.0.1:8080/devtools/browser/abc", {"protocolVersion": "1.3", "product": "Chrome/1"}, True),
        ("ws://localhost:8080/devtools/browser/abc", {"protocolVersion": "1.3", "product": "Chrome/1"}, True),
        ("ws://remote.example:8080/devtools/browser/abc", {"protocolVersion": "1.3", "product": "Chrome/1"}, False),
        ("ws://127.0.0.1:9000/devtools/browser/abc", {"protocolVersion": "1.3", "product": "Chrome/1"}, False),
        ("ws://user@127.0.0.1:8080/devtools/browser/abc", {"protocolVersion": "1.3", "product": "Chrome/1"}, False),
        ("ws://127.0.0.1:8080/devtools/browser/abc", {}, False),
    ],
)
def test_cdp_requires_a_local_websocket_and_real_version_reply(monkeypatch, url, reply, expected):
    calls, closed = [], []

    class Connection:
        def __init__(self, host, port, timeout):
            assert (host, port) == ("127.0.0.1", 8080)

        def request(self, method, path):
            calls.append((method, path))

        def getresponse(self):
            return SimpleNamespace(status=200, read=lambda size: json.dumps({"webSocketDebuggerUrl": url}).encode())

        def close(self):
            closed.append("http")

    class Page:
        def __init__(self, port, timeout_s, target):
            assert target["webSocketDebuggerUrl"].startswith("ws://127.0.0.1:8080/")

        def call(self, method):
            calls.append(method)
            return reply

        def close(self):
            closed.append("ws")

    monkeypatch.setattr(steam.http.client, "HTTPConnection", Connection)
    monkeypatch.setattr(cdp, "Page", Page)
    assert steam._cdp_alive(8080) is expected
    assert calls[0] == ("GET", "/json/version") and "http" in closed
    if expected or reply == {}:
        assert calls[1:] == ["Browser.getVersion"] and "ws" in closed
    else:
        assert len(calls) == 1


def test_listener_enumeration_reads_ipv4_and_ipv6(monkeypatch):
    payloads = {
        socket.AF_INET: struct.pack(
            "<I6I", 1, 2, int.from_bytes(socket.inet_aton("127.0.0.1"), "little"), socket.htons(8080), 0, 0, 11
        ),
        socket.AF_INET6: struct.pack(
            "<I16sII16sIIII", 1, socket.inet_pton(socket.AF_INET6, "::"), 0, socket.htons(8080), bytes(16), 0, 0, 2, 99
        ),
    }

    def native_table(buffer, size, ordered, family, table_class, reserved):
        payload = payloads[family]
        size._obj.value = len(payload)
        if buffer is None:
            return 122
        ctypes.memmove(buffer, payload, len(payload))
        return 0

    monkeypatch.setattr(launch, "_GetExtendedTcpTable", native_table)
    assert steam.listeners() == [(8080, "127.0.0.1", 11), (8080, "::", 99)]


def test_listener_read_failure_is_not_a_free_port(monkeypatch):
    monkeypatch.setattr(launch, "_GetExtendedTcpTable", lambda *args: 5)
    with pytest.raises(steam.SteamError, match="listener table unavailable"):
        steam.listeners()


@pytest.mark.parametrize(
    "args",
    [
        "--",
        "--single-argument -silent",
        "-cef-enable-debugging",
        "/CEF-ENABLE-DEBUGGING=1",
        "--remote-debugging-port=8080",
        "--user-data-dir=C:\\Profile",
        "-shutdown",
        "/restart",
        "steam://exit",
        "steam://restart?x=1",
    ],
)
def test_steam_rejects_debug_profile_neutralizer_and_lifecycle_args_before_any_process_call(
    installation, monkeypatch, args
):
    exe, _helper, _paths, _table = installation
    monkeypatch.setattr(policy, "check_launch_exe", lambda *a, **kw: None)
    monkeypatch.setattr(steam.subprocess, "Popen", lambda *a, **kw: pytest.fail("Commande envoyée avant précontrôles"))
    with pytest.raises(launch.LaunchError, match="refused"):
        launch.launch_steam(str(exe), args, False, True, 5, 0, set())


@pytest.mark.parametrize(
    "args", ["-silent", "-cef-disable-occlusion -cef-force-accessibility", "-applaunch 42", "steam://open/library"]
)
def test_ordinary_and_managed_view_arguments_are_preserved(args):
    steam.check_args(args)
    assert args in steam.command(r"C:\\Steam\\steam.exe", args, False)


def test_collision_is_refused_before_closing_even_when_owner_has_steam_helper_name(installation, monkeypatch):
    exe, _helper, _paths, _table = installation
    monkeypatch.setattr(policy, "check_launch_exe", lambda *a, **kw: None)
    monkeypatch.setattr(steam, "listeners", lambda: [(8080, "127.0.0.1", 99)])
    monkeypatch.setattr(w, "process_name", lambda pid: "steamwebhelper.exe")
    monkeypatch.setattr(steam, "shutdown", lambda exe: pytest.fail("Steam fermé malgré collision"))
    with pytest.raises(launch.LaunchError, match="pid 99"):
        launch.launch_steam(str(exe), "", True, True, 5, 0, set())


def test_existing_instance_receives_arguments_once_without_restart_or_claimed_game_success(installation, monkeypatch):
    exe, _helper, _paths, _table = installation
    sent = []
    monkeypatch.setattr(policy, "check_launch_exe", lambda *a, **kw: None)
    monkeypatch.setattr(steam, "main_window", lambda **kw: 101)
    monkeypatch.setattr(w, "window_pid", lambda hwnd: 11)
    monkeypatch.setattr(launch, "_front_returned", lambda *a: True)
    monkeypatch.setattr(steam, "shutdown", lambda exe: pytest.fail("Instance existante fermée"))
    monkeypatch.setattr(steam.subprocess, "Popen", lambda cmd, **kw: sent.append(cmd))
    result = launch.launch_steam(str(exe), "-applaunch 42", False, False, 5, 0, set())
    assert sent == [[str(exe), "-applaunch", "42"]]
    assert result.hwnd == 101 and not result.own_session
    assert any("sent once" in note and "not verified" in note for note in result.notes)


def test_existing_devtools_precheck_refuses_before_forwarding_args(installation, monkeypatch):
    exe, _helper, _paths, _table = installation
    monkeypatch.setattr(policy, "check_launch_exe", lambda *a, **kw: None)
    monkeypatch.setattr(steam, "debugging_port", lambda **kw: None)
    monkeypatch.setattr(
        steam.subprocess, "Popen", lambda *a, **kw: pytest.fail("Arguments transmis malgré DevTools absent")
    )
    with pytest.raises(launch.LaunchError, match="Nothing was done"):
        launch.launch_steam(str(exe), "-applaunch 42", True, False, 5, 0, set())


def test_window_replaced_during_devtools_wait_is_not_returned_or_registered(installation, monkeypatch):
    exe, _helper, _paths, table = installation
    sent = []
    monkeypatch.setattr(policy, "check_launch_exe", lambda *a, **kw: None)
    table[:] = [(1, 0, "System")]
    monkeypatch.setattr(steam, "port_taken_by_other", lambda **kw: None)
    monkeypatch.setattr(launch, "_wait_steam", lambda *a: (101, False))
    monkeypatch.setattr(w, "window_pid", lambda hwnd: 11)
    monkeypatch.setattr(steam, "main_window", lambda **kw: 102)
    monkeypatch.setattr(steam, "debugging_port", lambda **kw: 8080)
    monkeypatch.setattr(launch, "_front_returned", lambda *a: True)
    monkeypatch.setattr(steam.subprocess, "Popen", lambda cmd, **kw: (sent.append(cmd), SimpleNamespace(pid=10))[1])
    result = launch.launch_steam(str(exe), "", True, False, 5, 0, set())
    assert len(sent) == 1
    assert result.hwnd == 0 and result.port == 0 and not result.own_session
    assert any("target unknown" in note for note in result.notes)


def test_shutdown_timeout_does_not_repeat_or_start_steam(installation, monkeypatch):
    exe, _helper, _paths, _table = installation
    sent = []
    monkeypatch.setattr(steam.subprocess, "Popen", lambda cmd, **kw: sent.append(cmd))
    with pytest.raises(steam.SteamError, match="nothing was restarted"):
        steam.shutdown(str(exe), wait_s=0)
    assert sent == [[str(exe), "-shutdown"]]
