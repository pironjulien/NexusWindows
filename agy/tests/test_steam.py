"""Steam sans GUI : aucune vraie fenêtre, socket CDP ou commande Steam n'est utilisée."""

import ctypes
import json
import socket
import struct
from contextlib import nullcontext
from typing import cast
from unittest.mock import Mock

import computer_client
import mcp_server
import pytest
import steam


@pytest.fixture
def bench(monkeypatch, tmp_path):
    exe = tmp_path / "Steam" / "steam.exe"
    exe.parent.mkdir()
    exe.write_bytes(b"MZ")
    (exe.parent / "steamui.dll").write_bytes(b"")
    helper = exe.parent / "bin" / "steamwebhelper.exe"
    helper.parent.mkdir()
    helper.write_bytes(b"MZ")
    images = {10: str(exe), 11: str(helper)}
    state = {
        "rows": [(10, 1, "steam.exe"), (11, 10, "steamwebhelper.exe")],
        "windows": [{"hwnd": 88, "process_id": 11, "title": "Steam"}],
        "time": 0.0,
    }
    monkeypatch.setattr(steam, "process_table", lambda: list(state["rows"]))
    monkeypatch.setattr(steam, "process_creation", lambda pid: pid * 100)
    monkeypatch.setattr(steam, "_app_window", lambda hwnd: True)
    monkeypatch.setattr(computer_client.user32, "GetForegroundWindow", lambda: 500)
    monkeypatch.setattr(computer_client.physical_input, "mark", lambda: (0.0, 0))
    monkeypatch.setattr(steam, "listeners", Mock(side_effect=AssertionError("DevTools doit rester opt-in")))
    monkeypatch.setattr(steam, "_cdp_alive", Mock(side_effect=AssertionError("Aucun vrai CDP")))
    started = Mock(side_effect=AssertionError("Aucun vrai lancement"))
    monkeypatch.setattr(steam, "_start", started)
    monkeypatch.setattr(steam.time, "monotonic", lambda: state["time"])
    monkeypatch.setattr(steam.time, "sleep", lambda seconds: state.update(time=state["time"] + seconds))
    monkeypatch.setattr(steam, "START_WAIT_S", 0.45)
    monkeypatch.setattr(steam, "SHUTDOWN_WAIT_S", 0.45)
    monkeypatch.setattr(steam, "FOCUS_SETTLE_S", 0.0)
    checks = Mock()
    finish = Mock(return_value=False)

    def launch(**options):
        return steam.launch(
            str(exe),
            options.pop("args", []),
            restart=options.pop("restart", False),
            devtools=options.pop("devtools", False),
            windows=lambda: list(state["windows"]),
            image=lambda pid: images.get(pid, ""),
            check=checks,
            restore_focus=finish,
            **options,
        )

    return exe, images, state, started, launch, checks, finish


def test_valve_registry_resolves_alias_without_path_lookup(bench, monkeypatch):
    exe, *_ = bench
    monkeypatch.setattr(steam.winreg, "OpenKey", lambda *a: nullcontext("key"))
    monkeypatch.setattr(steam.winreg, "QueryValueEx", lambda *a: (str(exe).replace("\\", "/"), 1))
    assert computer_client._resolve_program("Steam") == str(exe)
    assert steam.is_target(str(exe))
    assert not steam.is_target(str(exe.parent / "other.exe"))


@pytest.mark.parametrize(
    "argument",
    [
        "-cef-enable-debugging",
        "--remote-debugging-port=8080",
        "/remote-debugging-port:8080",
        "-debug",
        "-profile=x",
        "--user-data-dir=C:/private",
        "--load-extension=x",
        "-inspect",
        "/inspect-port=1",
        "--remote-allow-origins=*",
        "-disable-web-security",
        "--",
        "-single-argument",
        "-shutdown",
        "/restart",
        "steam://exit",
        "steam://quit/",
        "steam://sh%75tdown",
        "steam://restart",
    ],
)
def test_control_arguments_cannot_bypass_explicit_options(argument):
    with pytest.raises(PermissionError):
        steam.check_args([argument])


def test_ordinary_steam_arguments_are_preserved():
    steam.check_args(["-silent", "-applaunch", "123", "steam://open/main", "steam://run/123", *steam.VIEW_SWITCHES])


def test_roots_require_exact_executable_not_basename(bench):
    exe, images, state, *_ = bench
    images[10] = str(exe.parent.parent / "other" / "steam.exe")
    with pytest.raises(steam.SteamError, match="autre chemin"):
        steam.roots(str(exe), state["rows"], lambda pid: images.get(pid, ""))


def test_helpers_require_descent_and_installation_path(bench):
    exe, images, state, *_ = bench
    state["rows"] += [(12, 1, "steamwebhelper.exe"), (13, 10, "steamwebhelper.exe")]
    images[12] = images[11]
    images[13] = str(exe.parent.parent / "fake" / "steamwebhelper.exe")
    assert steam.helpers(str(exe), {10}, state["rows"], lambda pid: images.get(pid, "")) == {11}


def test_ambiguous_windows_and_foreign_window_are_never_guessed(bench):
    _, _, state, *_ = bench
    state["windows"] = [{"hwnd": 1, "process_id": 91, "title": "Steam"}]
    assert steam.main_window(state["windows"], {11}) is None
    state["windows"] = [{"hwnd": h, "process_id": 11, "title": "Library"} for h in (1, 2)]
    with pytest.raises(steam.SteamError, match="plusieurs"):
        steam.main_window(state["windows"], {11})
    state["windows"].append({"hwnd": 3, "process_id": 11, "title": "Steam"})
    selected = steam.main_window(state["windows"], {11})
    assert selected is not None and selected["hwnd"] == 3


@pytest.mark.parametrize(
    "rows",
    [
        [("127.0.0.1", 99)],
        [("0.0.0.0", 11)],
        [("::", 11)],
        [("127.0.0.1", 11), ("::1", 99)],
        [("127.0.0.1", 11), ("::", 11)],
    ],
)
def test_debugging_rejects_foreign_or_public_listener_before_cdp(bench, monkeypatch, rows):
    monkeypatch.setattr(steam, "listeners", lambda port: rows)
    with pytest.raises(steam.SteamError):
        steam.debugging_port({11})
    cast(Mock, steam._cdp_alive).assert_not_called()


def test_debugging_rechecks_port_owner_after_cdp(bench, monkeypatch):
    monkeypatch.setattr(steam, "listeners", Mock(side_effect=[[("127.0.0.1", 11)], [("127.0.0.1", 99)]]))
    monkeypatch.setattr(steam, "_cdp_alive", lambda port: True)
    assert steam.debugging_port({11}) is None


def test_debugging_rejects_recycled_pid_after_protocol_response(bench, monkeypatch):
    state = {"created": 100}
    monkeypatch.setattr(steam, "listeners", lambda port: [("127.0.0.1", 11)])
    monkeypatch.setattr(steam, "process_creation", lambda pid: state["created"])
    monkeypatch.setattr(steam, "_cdp_alive", lambda port: state.update(created=200) or True)
    assert steam.debugging_port({11}) is None


def test_tcp_inventory_reads_ipv4_and_ipv6_owner_rows_without_network(monkeypatch):
    port = struct.pack("!H", 8080) + b"\0\0"
    ipv4 = struct.pack("<I4s4s8xI", 2, socket.inet_pton(socket.AF_INET, "127.0.0.1"), port, 11)
    ipv6 = struct.pack(
        "<16sI4s16sI4sII", socket.inet_pton(socket.AF_INET6, "::"), 0, port, bytes(16), 0, bytes(4), 2, 99
    )
    calls = []

    def table(buffer, size, ordered, family, table_class, reserved):
        calls.append(family)
        data = struct.pack("<I", 1) + (ipv4 if family == socket.AF_INET else ipv6)
        size._obj.value = len(data)
        if buffer is None:
            return 122
        ctypes.memmove(buffer, data, len(data))
        return 0

    api = Mock()
    api.GetExtendedTcpTable.side_effect = table
    monkeypatch.setattr(steam.ctypes, "WinDLL", lambda name: api)
    assert steam.listeners(8080) == [("127.0.0.1", 11), ("::", 99)]
    assert calls == [socket.AF_INET, socket.AF_INET, socket.AF_INET6, socket.AF_INET6]


def test_tcp_inventory_failure_is_not_a_free_port(monkeypatch):
    api = Mock()
    api.GetExtendedTcpTable.return_value = 5
    monkeypatch.setattr(steam.ctypes, "WinDLL", lambda name: api)
    with pytest.raises(steam.SteamError, match="TCP illisible"):
        steam.listeners(8080)


def test_cdp_requires_loopback_websocket_and_real_protocol_response(monkeypatch):
    page = Mock()
    page.call.return_value = {"protocolVersion": "1.3", "product": "Chrome/1"}
    factory = Mock(return_value=page)
    monkeypatch.setattr(steam.cdp_wheel, "_Page", factory)
    payload = [{"webSocketDebuggerUrl": "ws://other:8080/devtools/page/x"}]
    monkeypatch.setattr(steam.cdp_wheel, "fetch_json_list", lambda *a, **k: json.dumps(payload).encode())
    assert not steam._cdp_alive(8080)
    factory.assert_not_called()
    payload[0]["webSocketDebuggerUrl"] = "ws://127.0.0.1:8080/devtools/page/x"
    assert steam._cdp_alive(8080)
    page.call.assert_called_once_with("Browser.getVersion")
    page.close.assert_called_once()
    page.call.return_value = {"webSocketDebuggerUrl": "arbitrary HTTP JSON"}
    assert not steam._cdp_alive(8080)


def test_existing_instance_reused_without_any_launch_or_devtools_probe(bench):
    _, _, _, started, launch, _, finish = bench
    result = launch()
    assert result["verified"] is True and result["reused"] is True and result["hwnd"] == 88
    assert result["restarted"] is False and "own_session" not in result
    started.assert_not_called()
    cast(Mock, steam.listeners).assert_not_called()
    cast(Mock, steam._cdp_alive).assert_not_called()
    finish.assert_called_once()


def test_tray_instance_opens_main_once_without_restart(bench):
    _, _, state, started, launch, *_ = bench
    state["windows"] = []

    def open_main(exe, argv):
        assert argv == ["steam://open/main"]
        state["windows"] = [{"hwnd": 88, "process_id": 11, "title": "Steam"}]

    started.side_effect = open_main
    assert launch()["verified"] is True
    assert started.call_count == 1


def test_new_start_adds_view_flags_and_no_debugging_by_default(bench):
    exe, _, state, started, launch, *_ = bench
    running = state["rows"]
    state["rows"] = []

    def start(path, argv):
        assert path == str(exe) and argv == ["-silent", *steam.VIEW_SWITCHES]
        state["rows"] = running

    started.side_effect = start
    result = launch(args=["-silent"])
    assert result["verified"] is True and result["reused"] is False
    started.assert_called_once()
    cast(Mock, steam.listeners).assert_not_called()


def test_existing_steam_without_cdp_requires_explicit_restart(bench, monkeypatch):
    _, _, _, started, launch, *_ = bench
    monkeypatch.setattr(steam, "listeners", lambda port: [])
    with pytest.raises(steam.SteamError, match="restart=true"):
        launch(devtools=True, args=["steam://run/123"])
    started.assert_not_called()


def test_existing_steam_forwards_requested_arguments_exactly_once(bench):
    exe, _, _, started, launch, *_ = bench
    started.side_effect = None
    result = launch(args=["steam://run/123"])
    started.assert_called_once_with(str(exe), ["steam://run/123"])
    assert result["verified"] is True and result["arguments_sent"] is True
    assert result["arguments_verified"] is None


def test_argument_with_missing_window_is_not_replayed_or_replaced_by_main(bench):
    exe, _, state, started, launch, *_ = bench
    state["windows"] = []
    started.side_effect = None
    result = launch(args=["steam://run/123"])
    started.assert_called_once_with(str(exe), ["steam://run/123"])
    assert result["verified"] is None and result["arguments_verified"] is None


def test_ambiguous_window_refuses_arguments_before_effect(bench):
    _, _, state, started, launch, *_ = bench
    state["windows"] = [{"hwnd": h, "process_id": 11, "title": "Library"} for h in (1, 2)]
    with pytest.raises(steam.SteamError, match="plusieurs"):
        launch(args=["steam://run/123"])
    started.assert_not_called()


@pytest.mark.parametrize("name", ["game.exe", "ThirdPartyLauncher.exe"])
def test_restart_refuses_games_and_launchers_before_shutdown(bench, name):
    _, _, state, started, launch, *_ = bench
    state["rows"].append((20, 10, name))
    with pytest.raises(steam.SteamError, match="jeu ou lanceur"):
        launch(restart=True)
    started.assert_not_called()


@pytest.mark.parametrize("name", ["steamwebhelper.exe", "steamservice.exe"])
def test_whitelisted_basename_outside_install_does_not_exempt_a_game(bench, name):
    exe, images, state, started, launch, *_ = bench
    foreign = exe.parent.parent / name
    foreign.write_bytes(b"MZ")
    images[20] = str(foreign)
    state["rows"].append((20, 10, name))
    with pytest.raises(steam.SteamError, match="jeu ou lanceur"):
        launch(restart=True)
    started.assert_not_called()


def test_restart_sends_one_graceful_shutdown_and_never_forces_or_restarts_on_timeout(bench):
    exe, _, _, started, launch, *_ = bench
    started.side_effect = None
    with pytest.raises(steam.SteamError, match="fin non prouvée"):
        launch(restart=True)
    started.assert_called_once_with(str(exe), ["-shutdown"])


def test_restart_waits_for_old_helper_before_new_process(bench):
    exe, _, state, started, launch, *_ = bench
    old_sleep = steam.time.sleep

    def start(path, argv):
        if argv == ["-shutdown"]:
            state["rows"] = [(11, 10, "steamwebhelper.exe")]
        else:
            assert state["time"] >= 0.15
            assert argv == list(steam.VIEW_SWITCHES)
            state["rows"] = [(10, 1, "steam.exe"), (11, 10, "steamwebhelper.exe")]

    def sleep(seconds):
        old_sleep(seconds)
        state["rows"] = []

    started.side_effect = start
    steam.time.sleep = sleep
    result = launch(restart=True)
    assert result["verified"] is True and result["restarted"] is True
    assert started.call_args_list[0].args == (str(exe), ["-shutdown"])
    assert started.call_count == 2


def test_restart_waits_for_helper_born_during_shutdown_and_now_orphaned(bench):
    exe, images, state, started, launch, *_ = bench
    images[12] = images[11]

    def shutdown(path, argv):
        assert argv == ["-shutdown"]
        state["rows"] = [(12, 10, "steamwebhelper.exe")]

    started.side_effect = shutdown
    with pytest.raises(steam.SteamError, match="fin non prouvée"):
        launch(restart=True)
    started.assert_called_once_with(str(exe), ["-shutdown"])


def test_devtools_collision_refuses_before_shutdown(bench, monkeypatch):
    _, _, _, started, launch, *_ = bench
    monkeypatch.setattr(steam, "listeners", lambda port: [("127.0.0.1", 999)])
    with pytest.raises(steam.SteamError):
        launch(restart=True, devtools=True)
    started.assert_not_called()


def test_devtools_inventory_failure_refuses_before_any_effect(bench, monkeypatch):
    _, _, _, started, launch, *_ = bench
    monkeypatch.setattr(steam, "listeners", Mock(side_effect=steam.SteamError("TCP illisible")))
    with pytest.raises(steam.SteamError, match="TCP illisible"):
        launch(devtools=True, restart=True)
    started.assert_not_called()


def test_new_devtools_is_opt_in_and_its_success_requires_protocol_proof(bench, monkeypatch):
    _, _, state, started, launch, *_ = bench
    running = state["rows"]
    state["rows"] = []
    monkeypatch.setattr(steam, "listeners", lambda port: [("127.0.0.1", 11)] if state["rows"] else [])
    monkeypatch.setattr(steam, "_cdp_alive", lambda port: True)

    def start(path, argv):
        assert argv == [*steam.VIEW_SWITCHES, steam.DEVTOOLS_SWITCH]
        state["rows"] = running

    started.side_effect = start
    result = launch(devtools=True)
    assert result["verified"] is True and result["devtools_verified"] is True and result["devtools_port"] == 8080
    assert result["devtools_input_registered"] is False and "aucun canal" in result["devtools_input_note"]


def test_missing_window_never_claims_success_or_designates_foreign_hwnd(bench):
    _, _, state, started, launch, *_ = bench
    state["windows"] = [{"hwnd": 99, "process_id": 99, "title": "Steam"}]
    started.side_effect = None
    result = launch()
    assert result["success"] is None and result["verified"] is None and "hwnd" not in result
    assert started.call_count == 1


def test_window_disappearing_after_focus_restore_is_unknown(bench):
    _, _, state, _, launch, _, finish = bench
    finish.side_effect = lambda window: state.update(windows=[]) or False
    result = launch()
    assert result["verified"] is None and "hwnd" not in result


def test_focus_restitution_respects_new_user_window_and_real_user_input():
    state = {"hwnd": 500, "pid": 33, "touched": False}
    hand_back = Mock(return_value=True)
    restore = steam.focus_guard(500, lambda: (state["hwnd"], state["pid"]), lambda: state["touched"], hand_back)
    assert not restore({10, 11})
    state.update(hwnd=200, pid=44)  # Julien a choisi une autre application pendant le démarrage.
    assert not restore({10, 11})
    state.update(hwnd=88, pid=11)  # Steam reprend le premier plan sans geste physique.
    assert restore({10, 11})
    hand_back.assert_called_once_with(88, 200)
    state.update(hwnd=89, pid=11, touched=True)  # Julien choisit lui-même une fenêtre Steam.
    assert not restore({10, 11})
    assert hand_back.call_count == 1


def test_startup_focus_is_observed_before_main_window_exists(bench):
    _, _, state, started, launch, _, restore = bench
    state["windows"] = []
    started.side_effect = None
    assert launch()["verified"] is None
    assert restore.call_count >= 1
    assert restore.call_args.args == ({10, 11},)


def test_options_are_steam_only_and_mcp_forwards_them(bench, monkeypatch):
    exe, *_ = bench
    monkeypatch.setattr(computer_client, "ensure_default_desktop", lambda: True)
    opened = Mock(return_value={})
    monkeypatch.setattr(computer_client.steam, "launch", opened)
    computer_client.open_path(str(exe), devtools=True, restart=True)
    assert opened.call_args.kwargs["devtools"] is True and opened.call_args.kwargs["restart"] is True
    other = exe.with_name("other.exe")
    other.write_bytes(b"MZ")
    with pytest.raises(PermissionError, match="que pour Steam"):
        computer_client.open_path(str(other), restart=True)
    seen = {}
    monkeypatch.setattr(computer_client, "open_path", lambda **kw: seen.update(kw) or {})
    mcp_server.launch("Steam", devtools=True, restart=True)
    assert seen == {"path_or_url": "Steam", "args": "", "devtools": True, "restart": True}


def test_steam_shortcut_keeps_its_ordinary_arguments_and_uses_canonical_client(bench, monkeypatch):
    exe, *_ = bench
    shortcut = exe.parent / "Steam.lnk"
    shortcut.write_bytes(b"shortcut")
    monkeypatch.setattr(computer_client, "ensure_default_desktop", lambda: True)
    monkeypatch.setattr(computer_client, "_shortcut_target", lambda path: (str(exe), "-silent"))
    opened = Mock(return_value={})
    monkeypatch.setattr(computer_client.steam, "launch", opened)
    computer_client.open_path(str(shortcut), args="steam://open/main")
    assert opened.call_args.args == (str(exe.resolve()), ["-silent", "steam://open/main"])
