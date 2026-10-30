"""Chaînes de techniques : repli avant entrée seulement, jamais après une transmission non confirmée."""

from contextlib import nullcontext
from types import SimpleNamespace

import computer_client as client
import pytest
import win32_controls as native


@pytest.fixture(autouse=True)
def no_foreground(monkeypatch):
    monkeypatch.setattr(client, "_restore_if_stolen", lambda *args: False)


@pytest.mark.parametrize("verdict", [False, None])
def test_sent_with_unconfirmed_effect_never_tries_another_technique(verdict):
    calls = []
    result = client._run_chain(
        [("UIA Invoke", lambda: calls.append("sent")), ("other", lambda: calls.append("other"))],
        lambda _: (verdict, "unchanged"),
        0,
    )
    assert calls == ["sent"] and result["technique"] == "UIA Invoke"
    assert result["verified"] is None


def test_missing_pattern_can_fall_back_before_input():
    calls = []

    def missing():
        raise LookupError("Motif Invoke absent")

    result = client._run_chain(
        [("UIA Invoke", missing), ("other", lambda: calls.append("sent"))],
        lambda _: (True, "read back"),
        0,
    )
    assert calls == ["sent"] and result["technique"] == "other" and result["verified"] is True


def test_provider_error_after_entry_is_unknown_without_replay():
    calls = []

    def toggle():
        calls.append("sent")
        raise RuntimeError("provider response lost")

    ctrl = SimpleNamespace(GetTogglePattern=lambda: SimpleNamespace(Toggle=toggle))
    result = client._run_chain(
        [("UIA Toggle", lambda: client._UIA_TOGGLE(ctrl)), ("other", lambda: calls.append("other"))],
        lambda _: pytest.fail("the response was lost"),
        0,
    )
    assert calls == ["sent"] and result["verified"] is None and result["pending"] is True
    assert "provider response lost" in result["detail"]


def test_read_error_after_entry_is_unknown_without_replay():
    calls = []

    def unreadable(_):
        raise RuntimeError("read failed")

    result = client._run_chain(
        [("UIA Invoke", lambda: calls.append("sent")), ("other", lambda: calls.append("other"))], unreadable, 0
    )
    assert calls == ["sent"] and result["verified"] is None and result["pending"] is True


def test_user_interruption_during_read_still_stops_the_action():
    calls = []

    def interrupted(_):
        raise client.UserInterruptError("user resumed")

    with pytest.raises(client.UserInterruptError):
        client._run_chain(
            [("UIA Invoke", lambda: calls.append("sent")), ("other", lambda: calls.append("other"))], interrupted, 0
        )
    assert calls == ["sent"]


@pytest.mark.parametrize("reply", [None, 0])
def test_dropdown_command_timeout_never_replays_through_another_technique(monkeypatch, reply):
    calls = []
    monkeypatch.setattr(native, "is_class", lambda *args: True)
    monkeypatch.setattr(native, "_IsWindowEnabled", lambda *_: True)
    monkeypatch.setattr(native, "combo_list_of", lambda *_: 2)
    monkeypatch.setattr(native, "combo_dropped", lambda *_: False)
    monkeypatch.setattr(native, "send", lambda *args, **kwargs: calls.append("CB_SHOWDROPDOWN") or reply)
    result = client._run_chain(
        [
            ("CB_SHOWDROPDOWN", lambda: native.combo_show_dropdown(1, True, timeout_s=0)),
            ("UIA Expand", lambda: calls.append("second entry")),
        ],
        lambda _: pytest.fail("the command did not return a confirmed outcome"),
        0,
    )
    assert calls == ["CB_SHOWDROPDOWN"] and result["verified"] is None and result["pending"] is True


def test_parent_notification_timeout_is_never_followed_by_a_second_notification(monkeypatch):
    calls = []
    monkeypatch.setattr(native, "_GetParent", lambda *_: 2)
    monkeypatch.setattr(native, "_GetDlgCtrlID", lambda *_: 7)
    monkeypatch.setattr(native, "send", lambda *args, **kwargs: calls.append("send"))
    monkeypatch.setattr(native, "post", lambda *args: calls.append("post"))
    with pytest.raises(native.MessageOutcomeUnknown):
        native._notify_parent_sync(1, native.CBN_SELCHANGE)
    assert calls == ["send"]


@pytest.mark.parametrize("accepted", [0, 1])
def test_physical_sendinput_short_count_is_terminal_before_fallback(monkeypatch, accepted):
    monkeypatch.setattr(client, "ensure_default_desktop", lambda: None)
    monkeypatch.setattr(client.user32, "SendInput", lambda *args: accepted)
    release = client.INPUT(type=client.INPUT_KEYBOARD)
    release.union.ki.dwFlags = client.KEYEVENTF_KEYUP
    result = client._run_chain(
        [
            ("SendInput", lambda: client._send_inputs([release, release])),
            ("fallback", lambda: pytest.fail("aucun rejeu après compte SendInput incomplet")),
        ],
        lambda _: pytest.fail("une relecture ne corrige pas un envoi incomplet"),
        0,
    )
    assert result["input_outcome"] == "unknown" and result["input_error_type"] == "InputOutcomeUnknown"
    assert result["verified"] is None and result["pending"]


def test_real_uia_value_provider_error_cannot_fall_back_to_message_typing(monkeypatch):
    calls, state = [], {"value": ""}

    def set_value(text):
        calls.append("UIA Value")
        state["value"] = text
        raise RuntimeError("réponse fournisseur perdue après saisie")

    ctrl = SimpleNamespace(GetValuePattern=lambda: SimpleNamespace(IsReadOnly=False, SetValue=set_value))
    monkeypatch.setattr(client, "_element_family", lambda *args: "WinForms")
    monkeypatch.setattr(client, "_native_kind", lambda *args: "edit")
    monkeypatch.setattr(client, "_read_state", lambda *args: state)
    monkeypatch.setattr(native, "edit_set_text", lambda *args, **kwargs: calls.append("EM_REPLACESEL"))
    _, chain, verify, _ = client._text_plan(ctrl, {"hwnd": 7}, 7, "A", True, "auto")
    result = client._run_chain(chain, verify, 0)
    assert state["value"] == "A" and calls == ["UIA Value"]
    assert result["input_outcome"] == "unknown" and result["input_error_type"] == "InputOutcomeUnknown"


@pytest.mark.parametrize(
    "family,keys",
    [
        ("Chromium", "enter"),
        ("Chromium", "ctrl+k"),
        ("Win32", "enter"),
        ("Win32", "ctrl+k"),
        ("WinForms", "enter"),
        ("Tk", "ctrl+k"),
    ],
)
@pytest.mark.parametrize("error_class", [native.PartialSendError, native.MessageOutcomeUnknown])
def test_real_background_keys_and_batch_stop_even_if_structure_changed(monkeypatch, family, keys, error_class):
    calls = []

    def sent_then_lost(*args, **kwargs):
        calls.append(keys)
        raise error_class("keydown accepté ; keyup/réponse perdu")

    monkeypatch.setattr(client, "ensure_default_desktop", lambda: None)
    monkeypatch.setattr(client, "resolve_window", lambda hwnd: 7)
    monkeypatch.setattr(client, "is_protected_system_window", lambda hwnd: (False, ""))
    monkeypatch.setattr(client, "require_key_confirmation", lambda *args, **kwargs: None)
    monkeypatch.setattr(client.user32, "IsWindow", lambda hwnd: True)
    monkeypatch.setattr(client.user32, "GetForegroundWindow", lambda: 7)
    monkeypatch.setattr(client, "window_fingerprint", lambda *args, **kwargs: {})
    monkeypatch.setattr(client, "window_browser_note", lambda hwnd: None)
    monkeypatch.setattr(client, "_settle_first_activation", lambda *args: None)
    monkeypatch.setattr(client, "_give_back_after_simulation", lambda *args: None)
    monkeypatch.setattr(client, "_physical_gate", lambda *args: pytest.fail("aucun repli physique"))
    monkeypatch.setattr(client, "_wait_window_effect", lambda *args, **kwargs: (True, "fenêtre changée"))
    monkeypatch.setattr(client, "check_expect", lambda *args: (bool(calls), {"match": "exact"}))
    monkeypatch.setattr(client, "wait_expect", lambda *args: (True, {"match": "exact"}))
    monkeypatch.setattr(native, "family", lambda hwnd: family)
    monkeypatch.setattr(native, "root_of", lambda hwnd: hwnd)
    monkeypatch.setattr(native, "has_cef_content", lambda hwnd: False)
    monkeypatch.setattr(native, "is_chromium_top", lambda hwnd: family == "Chromium")
    monkeypatch.setattr(native, "keyboard_target", lambda hwnd: hwnd)
    monkeypatch.setattr(native, "simulated_activation", lambda hwnd: nullcontext())
    for producer in ("post_key", "post_shortcut", "tk_keys"):
        monkeypatch.setattr(native, producer, sent_then_lost)
    request = [
        {"action": "key", "key": keys, "expect": {"element": "Nom", "value": "A"}},
        {"action": "key", "key": keys},
    ]
    result = client.act(request, 7, then="none", foreground=True)
    assert calls == [keys] and result["success"] is None
    assert [step["status"] for step in result["steps"]] == ["unverified"]
    assert result["steps"][0]["result"]["input_outcome"] == "unknown"
    assert result["steps"][0]["result"]["input_error_type"] == error_class.__name__
    assert not result.get("paused") and "continuation" not in result
