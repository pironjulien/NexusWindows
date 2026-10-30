"""État de fenêtre relu après la commande : aucune fenêtre réelle et aucun déplacement du bureau."""

import asyncio
from types import SimpleNamespace

import pytest

from nexus_windows import actions, server, snapshot
from nexus_windows import win32 as w


@pytest.fixture
def desk(monkeypatch):
    state = {
        "minimized": False,
        "maximized": False,
        "visible": True,
        "bottom": True,
        "front": 99,
        "rect": w.Rect(100, 100, 700, 500),
        "placement": True,
        "apply": True,
        "offset": 0,
    }
    sent = []
    clock = {"now": 0.0}

    def tick():
        clock["now"] += 0.2
        return clock["now"]

    def rect(hwnd, pointer):
        current = state["rect"]
        pointer._obj.left, pointer._obj.top = current.left, current.top
        pointer._obj.right, pointer._obj.bottom = current.right, current.bottom
        return True

    def placement(hwnd, pointer):
        pointer._obj.flags = 0x0002  # réduite depuis agrandie : doit revenir normale, pas agrandie
        return state["placement"]

    def restore(hwnd, pointer):
        sent.append(("restore", pointer._obj.showCmd, pointer._obj.flags))
        if state["apply"]:
            state.update(minimized=False, maximized=False, visible=True)
        return True

    def position(hwnd, after, x, y, width, height, flags):
        sent.append(("position", x, y, width, height, flags))
        if state["apply"]:
            if after == w.HWND_BOTTOM:
                state["bottom"] = True
            else:
                before = state["rect"]
                left = before.left if flags & w.SWP_NOMOVE else x + state["offset"]
                top = before.top if flags & w.SWP_NOMOVE else y + state["offset"]
                cx = before.width if flags & w.SWP_NOSIZE else width
                cy = before.height if flags & w.SWP_NOSIZE else height
                state["rect"] = w.Rect(left, top, left + cx, top + cy)
        return True

    def show(hwnd, command):
        sent.append(("show", command))
        if state["apply"]:
            state.update(minimized=command == w.SW_SHOWMINNOACTIVE, maximized=command == w.SW_MAXIMIZE)
        return True

    async def run(job, **_):
        return job(None)

    monkeypatch.setattr(server, "_run", run)
    monkeypatch.setattr(server, "_check_actionable_window", lambda hwnd: None)
    monkeypatch.setattr(server.worker, "check_cancelled", lambda: None)
    monkeypatch.setattr(server.SESSION, "resolve_window", lambda target: 42)
    monkeypatch.setattr(server.SESSION, "forget", lambda hwnd: None)
    monkeypatch.setattr(snapshot, "window_info", lambda hwnd: SimpleNamespace(label=lambda: "Fixture"))
    monkeypatch.setattr(w, "IsWindow", lambda hwnd: True)
    monkeypatch.setattr(w, "IsWindowVisible", lambda hwnd: state["visible"])
    monkeypatch.setattr(w, "IsIconic", lambda hwnd: state["minimized"])
    monkeypatch.setattr(w, "IsZoomed", lambda hwnd: state["maximized"])
    monkeypatch.setattr(w, "GetWindow", lambda hwnd, command: 0 if state["bottom"] else 13)
    monkeypatch.setattr(w, "GetShellWindow", lambda: 2)
    monkeypatch.setattr(w, "GetDesktopWindow", lambda: 3)
    monkeypatch.setattr(w, "foreground", lambda: state["front"])
    monkeypatch.setattr(w, "GetWindowRect", rect)
    monkeypatch.setattr(w, "GetWindowPlacement", placement)
    monkeypatch.setattr(w, "SetWindowPlacement", restore)
    monkeypatch.setattr(w, "SetWindowPos", position)
    monkeypatch.setattr(w, "ShowWindowAsync", show)
    monkeypatch.setattr(snapshot.time, "monotonic", tick)
    monkeypatch.setattr(snapshot.time, "sleep", lambda _: None)
    return state, sent


@pytest.mark.parametrize("initial", ["maximized", "minimized"])
def test_restore_handles_maximized_and_minimized_and_clears_restore_maximized_flag(desk, initial):
    state, sent = desk
    state[initial] = True
    assert snapshot.restore_behind(42)
    assert not state["maximized"] and not state["minimized"]
    assert sent[0] == ("restore", w.SW_SHOWNOACTIVATE, 0)


def test_restore_already_means_visible_normal_only(desk):
    _state, sent = desk
    assert snapshot.restore_behind(42) is False
    assert sent == []


def test_restore_refusal_before_placement_has_no_command(desk):
    state, sent = desk
    state.update(maximized=True, placement=False)
    with pytest.raises(RuntimeError, match="nothing was sent"):
        snapshot.restore_behind(42)
    assert sent == []


def test_restore_readback_failure_never_announces_already_or_replays(desk):
    state, sent = desk
    state.update(maximized=True, apply=False)
    result = asyncio.run(server.window("restore", 42))
    assert result.startswith("~") and "NOT verified" in result
    assert "already" not in result
    assert [item[0] for item in sent].count("restore") == 1


def test_move_reports_actual_rectangle_and_keeps_one_pixel_mismatch_unverified(desk):
    state, sent = desk
    state["offset"] = 1
    result = asyncio.run(server.window("move", 42, x=182, y=182))
    assert result.startswith("~") and "NOT verified" in result
    assert "Requested 600×400 @ (182,182); actual 600×400 @ (183,183)" in result
    assert len(sent) == 1 and sent[0][-1] & w.SWP_NOSIZE


def test_exact_move_is_verified_without_changing_dimensions(desk):
    _state, sent = desk
    result = asyncio.run(server.window("move", 42, x=184, y=184))
    assert result.startswith("✓") and "600×400 @ (184,184)" in result
    assert len(sent) == 1


@pytest.mark.parametrize("action,options", [("move", {"x": 182}), ("resize", {"width": 0, "height": 300})])
def test_bad_geometry_is_refused_before_any_command(desk, action, options):
    _state, sent = desk
    with pytest.raises(actions.ActionError, match="nothing was sent"):
        asyncio.run(server.window(action, 42, **options))
    assert not sent


def test_maximized_geometry_requires_restore_before_any_command(desk):
    state, sent = desk
    state["maximized"] = True
    with pytest.raises(actions.ActionError, match="Restore"):
        asyncio.run(server.window("move", 42, x=182, y=182))
    assert not sent


def test_minimize_and_bottom_require_real_state_after_command(desk):
    state, sent = desk
    state.update(apply=False, bottom=False)
    for action in ("minimize", "bottom"):
        result = asyncio.run(server.window(action, 42))
        assert result.startswith("~") and "NOT verified" in result
    assert len(sent) == 2


@pytest.mark.parametrize(("accepted", "front", "verified"), [(False, 42, False), (True, 99, False), (True, 42, True)])
def test_front_success_prefix_and_text_agree_with_native_readback(desk, monkeypatch, accepted, front, verified):
    state, _sent = desk
    state["front"] = front
    monkeypatch.setattr(server.realinput, "put_in_front", lambda hwnd: accepted)
    result = asyncio.run(server.window("front", 42, foreground=True))
    assert result.startswith("✓" if verified else "~")
    assert ("NOT verified" not in result) == verified


@pytest.mark.parametrize(
    ("chain", "expected"),
    [
        ({1: 0}, True),
        ({1: 2, 2: 0}, True),
        ({1: 3, 3: 0}, True),
        ({1: 2, 2: 3, 3: 0}, True),
        ({1: 4, 4: 0}, False),
        ({1: 2, 2: 4, 4: 0}, False),
        ({1: 2, 2: 3, 3: 2}, False),
        ({1: 1}, False),
    ],
)
def test_bottom_proves_only_api_shell_identities_below_and_no_cycle(monkeypatch, chain, expected):
    monkeypatch.setattr(w, "GetShellWindow", lambda: 2)
    monkeypatch.setattr(w, "GetDesktopWindow", lambda: 3)
    monkeypatch.setattr(w, "GetWindow", lambda hwnd, command: chain.get(hwnd, 0))
    monkeypatch.setattr(w, "IsWindowVisible", lambda hwnd: False)  # application masquée reste bloquante
    monkeypatch.setattr(w, "class_name", lambda hwnd: "Progman")  # homonyme ne vaut jamais identité du shell
    assert w.is_application_bottom(1) is expected


def test_restore_verifies_normal_visible_window_with_only_public_shell_below(desk, monkeypatch):
    state, _sent = desk
    state["maximized"] = True
    monkeypatch.setattr(w, "GetWindow", lambda hwnd, command: {42: 2}.get(hwnd, 0))
    result = asyncio.run(server.window("restore", 42))
    assert result.startswith("✓") and not state["maximized"]
