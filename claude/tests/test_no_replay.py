"""Une entrée transmise ne peut être rejouée sur la seule absence d'effet ou une réponse COM perdue."""

from types import SimpleNamespace
from typing import cast

import comtypes
import pytest
from test_contract import FakeAuto, executor, make_view, node

from nexus_windows import actions, methods, uia


@pytest.mark.parametrize("foreground", [False, True])
def test_sent_without_effect_stops_before_next_technique_and_physical_input(monkeypatch, foreground):
    ex = executor(make_view(node("Button", "Ouvrir")), FakeAuto())
    ex.allow_foreground = foreground
    calls = []
    monkeypatch.setattr(methods, "accepted_posts", lambda: 0)

    def sent():
        calls.append("sent")
        raise actions.NoEffect("no effect observed within the read budget")

    with pytest.raises(actions.Unconfirmed):
        ex._try_or_foreground(
            [("UIA Invoke", sent), ("other", lambda: calls.append("other"))],
            "click",
            lambda: calls.append("physical"),
        )
    assert calls == ["sent"]


def test_missing_pattern_can_fall_back_before_any_input(monkeypatch):
    ex = executor(make_view(node("Button", "Ouvrir")), FakeAuto())
    calls = []
    monkeypatch.setattr(methods, "accepted_posts", lambda: 0)

    def missing():
        raise uia.UIAError("Pattern invoke not supported by the element")

    result = ex._try([("UIA Invoke", missing), ("other", lambda: calls.append("sent"))], "click")
    assert calls == ["sent"] and result.method == "other"


def test_com_error_after_provider_call_is_unknown_and_never_replayed(monkeypatch):
    ex = executor(make_view(node("Button", "Ouvrir")), FakeAuto())
    calls = []
    monkeypatch.setattr(methods, "accepted_posts", lambda: 0)

    def invoke():
        calls.append("sent")
        raise comtypes.COMError(-2147467259, "response lost", (None, None, None, None, None))

    auto = SimpleNamespace(pattern=lambda *args: SimpleNamespace(Invoke=invoke))
    with pytest.raises(actions.Unconfirmed, match="response lost"):
        ex._try(
            [
                ("UIA Invoke", lambda: uia.Automation._action(cast(uia.Automation, auto), None, "invoke", "Invoke")),
                ("other", lambda: calls.append("other")),
            ],
            "click",
        )
    assert calls == ["sent"]


def test_append_timeout_never_replays_the_text_with_another_technique(monkeypatch):
    ex = executor(make_view(node("Edit", "Nom")), FakeAuto())
    calls = []
    monkeypatch.setattr(methods, "_is_class", lambda *args: True)
    monkeypatch.setattr(methods.w, "style", lambda *_: 0)
    monkeypatch.setattr(methods, "accepted_posts", lambda: 0)  # SendMessage n'est pas un message posté

    def send(_hwnd, message, *args, **kwargs):
        calls.append(message)
        return None if message == methods.w.EM_REPLACESEL else 0

    monkeypatch.setattr(methods.w, "send_message", send)
    with pytest.raises(actions.Unconfirmed, match="EM_REPLACESEL sent"):
        ex._try(
            [
                ("EM_REPLACESEL", lambda: methods.win32_edit_replace(1, "suffix", append=True)),
                ("UIA Value", lambda: calls.append("second append")),
            ],
            "append",
        )
    assert calls == [methods.w.EM_SETSEL, methods.w.EM_REPLACESEL]
