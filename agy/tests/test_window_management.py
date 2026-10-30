"""Contrats de placement : aucun HWND réel, aucun affichage et aucune entrée native."""

import contextlib
from types import SimpleNamespace

import pytest
import window_management as windows


@pytest.mark.parametrize(
    ("chain", "expected"),
    [
        ({1: 0}, True),
        ({1: 2, 2: 0}, True),
        ({1: 3, 3: 0}, True),
        ({1: 2, 2: 3, 3: 0}, True),
        ({1: 4, 4: 0}, False),  # application masquée ou homonyme Progman/explorer : même refus par identité
        ({1: 2, 2: 4, 4: 0}, False),
        ({1: 2, 2: 3, 3: 2}, False),
        ({1: 1}, False),
    ],
)
def test_bottom_requires_public_shell_identities_and_finite_complete_chain(chain, expected):
    assert windows.application_bottom(1, lambda hwnd: chain.get(hwnd, 0), 2, 3) is expected


def test_bottom_does_not_guess_shell_identity_when_windows_cannot_supply_it():
    assert not windows.application_bottom(1, lambda hwnd: {1: 4}.get(hwnd, 0), 0, 0)


class Backend:
    def __init__(self):
        self.state = dict(
            visible=True,
            minimized=False,
            maximized=False,
            foreground=False,
            bottom=False,
            left=20,
            top=30,
            width=600,
            height=400,
        )
        self.current = 99
        self.sent = []
        self.identities = [(123, "fixture.exe", 0)]
        self.fail_after_send = False

    def identity(self, _):
        return self.identities.pop(0) if len(self.identities) > 1 else self.identities[0]

    def read(self, _):
        if self.sent and self.fail_after_send:
            raise RuntimeError("Fenêtre disparue après commande")
        return dict(self.state)

    def front(self):
        return self.current

    def enabled(self, _):
        return True

    def placement(self, _):
        return object()

    def close(self):
        pass

    def send(self, target, plan, _):
        self.sent.append(plan.action)
        if plan.action in {"front", "maximize"}:
            self.current, self.state["foreground"] = target, True
        if plan.action == "maximize":
            self.state["maximized"] = True
        if plan.action == "move":
            self.state.update(left=plan.x, top=plan.y)
        if plan.action == "resize":
            self.state.update(width=plan.width, height=plan.height)
        return True


def host(*, idle=True, touched=False, token=True):
    events = []
    presence = SimpleNamespace(
        start=lambda **_: None,
        available=lambda: True,
        user_idle_s=lambda: 10,
        mark=lambda: 5,
        user_touched_since=lambda _: touched,
        injecting=contextlib.nullcontext,
    )

    def acquire(_):
        events.append("token")
        if not token:
            raise TimeoutError("token busy")
        return SimpleNamespace(release=lambda: events.append("release"))

    def restore(hwnd):
        events.append(("restore", hwnd))
        return True

    return SimpleNamespace(
        emergency_stop=SimpleNamespace(check=lambda: None),
        ensure_default_desktop=lambda: None,
        resolve_window=lambda _: 7,
        physical_input=presence,
        wait_for_user_idle=lambda: idle,
        _take_foreground_token=acquire,
        restore_foreground=restore,
    ), events


@pytest.mark.parametrize(
    ("action", "options"),
    [
        ("front", {}),
        ("move", {"x": 1}),
        ("resize", {"width": 0, "height": 8}),
        ("move", {"x": True, "y": 1}),
        ("restore", {"width": 1}),
        ("minimize", {"foreground": "yes"}),
    ],
)
def test_invalid_plan_before_any_command(action, options):
    with pytest.raises(ValueError):
        windows.prepare(action, **options)


def test_front_persists_explicit_request_and_maximize_returns_original():
    client, events = host()
    native = Backend()
    result = windows.manage(client, 7, "front", foreground=True, backend=native)
    assert result["verified"] and result["foregroundReturned"] == "kept-requested"
    assert events == ["token", "release"]
    client, events = host()
    result = windows.manage(client, 7, "maximize", backend=Backend())
    assert result["verified"] and result["foregroundReturned"] == "restored"
    assert events == ["token", ("restore", 99), "release"]


@pytest.mark.parametrize("options", [{"idle": False}, {"token": False}])
def test_presence_and_token_refuse_before_command(options):
    client, _ = host(**options)
    native = Backend()
    with pytest.raises(TimeoutError):
        windows.manage(client, 7, "maximize", backend=native)
    assert not native.sent


def test_identity_changed_while_waiting_refuses_before_command():
    client, events = host()
    native = Backend()
    native.identities = [(123, "fixture.exe", 0), (456, "other.exe", 0)]
    with pytest.raises(ValueError, match="Identité"):
        windows.manage(client, 7, "maximize", backend=native)
    assert not native.sent and events == ["token", "release"]


def test_after_command_error_is_unknown_and_never_replayed():
    client, events = host()
    native = Backend()
    native.fail_after_send = True
    result = windows.manage(client, 7, "maximize", backend=native)
    assert result["status"] == "sent-unconfirmed" and result["success"] is None
    assert native.sent == ["maximize"]
    assert events[-1] == "release"


def test_user_resumption_keeps_the_users_foreground_choice():
    client, events = host(touched=True)
    result = windows.manage(client, 7, "maximize", backend=Backend())
    assert result["foregroundReturned"] == "kept-by-julien"
    assert events == ["token", "release"]


def test_minimized_geometry_refuses_and_movement_never_claims_a_resize():
    client, _ = host()
    native = Backend()
    native.state["minimized"] = True
    with pytest.raises(ValueError, match="Restaurer"):
        windows.manage(client, 7, "move", x=20, y=30, backend=native)
    assert not native.sent
    before = dict(native.state, minimized=False)
    assert not windows.reached(windows.prepare("move", x=20, y=30), before, dict(before, width=601))
