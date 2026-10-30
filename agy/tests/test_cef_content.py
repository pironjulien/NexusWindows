"""Contenu Steam CEF séparé des fenêtres Chrome ; tests sans bureau ni entrée réelle."""

from types import SimpleNamespace
from unittest.mock import Mock

import computer_client as client
import pytest
import win32_controls as controls


def test_cef_is_exact_child_of_same_pid_and_root_without_simulated_chrome(monkeypatch):
    classes = {
        1: "SDL_app",
        2: "CefBrowserWindow",
        3: "CefBrowserWindow",
        4: "CefBrowserWindowExtra",
        5: "CefBrowserWindow",
    }
    pids = {1: 10, 2: 10, 3: 11, 4: 10, 5: 10}
    monkeypatch.setattr(controls, "root_of", lambda h: 9 if h == 5 else 1)
    monkeypatch.setattr(controls, "class_name", lambda h: classes[h])
    monkeypatch.setattr(controls, "_IsChild", lambda parent, child: child != 1)

    def process(hwnd, out):
        out._obj.value = pids[hwnd]
        return 100

    monkeypatch.setattr(controls, "_GetWindowThreadProcessId", process)
    monkeypatch.setattr(controls, "_EnumChildWindows", lambda top, cb, param: [cb(h, param) for h in (2, 3, 4, 5)])
    assert controls.cef_children(1) == (2,)
    assert controls.has_cef_content(1)
    assert controls.family(1) == "Win32"  # contenu web séparé du cadre natif
    assert not controls.is_chromium_top(1)
    monkeypatch.setitem(classes, 1, "Chrome_WidgetWin_1")
    assert controls.is_chromium_top(1)


def test_first_covered_cef_tree_publishes_every_document_then_rereads(monkeypatch):
    documents = [Mock(), Mock()]
    nodes = [SimpleNamespace(control_type="Document", element=doc, props={}) for doc in documents]
    tree = SimpleNamespace(nodes=nodes)
    reader = Mock()
    reader.read_tree.return_value = tree
    monkeypatch.setattr(client, "_WEB_DOCUMENTS", {})
    monkeypatch.setattr(client, "_fast", lambda: SimpleNamespace(reader=lambda: reader))
    monkeypatch.setattr(client, "_open_combos", lambda hwnd: [])
    monkeypatch.setattr(client, "_wrap_element", lambda element, info: element)
    monkeypatch.setattr(controls, "has_cef_content", lambda hwnd: True)
    monkeypatch.setattr(controls, "class_name", lambda hwnd: "SDL_app")
    monkeypatch.setattr(controls, "root_of", lambda hwnd: hwnd)
    monkeypatch.setattr(client.user32, "GetForegroundWindow", lambda: 500)
    assert client._window_tree(1) is tree
    assert reader.read_tree.call_count == 2
    for document in documents:
        document.GetPattern.return_value.ScrollIntoView.assert_called_once()


def _cef_plan(monkeypatch, ctrl, role="ListItem", *, cef=True, invoke=True):
    monkeypatch.setattr(controls, "has_cef_content", lambda hwnd: cef)
    monkeypatch.setattr(client, "_native_kind", lambda hwnd: "")
    monkeypatch.setattr(client, "_read_state", lambda *a: {})
    monkeypatch.setattr(client, "window_fingerprint", lambda *a, **kw: None)
    monkeypatch.setattr(client, "_restore_if_stolen", lambda hwnd: False)
    info = {
        "framework": "Chrome",
        "type": role,
        "name": "Library",
        "hwnd": 0,
        "props": {"has_invoke": invoke, "has_selection_item": True},
    }
    return client._click_plan(ctrl, info, 1)[1]


@pytest.mark.parametrize("role", ["ListItem", "TabItem", "MenuItem"])
def test_cef_click_prefers_invoke_without_select_replay(monkeypatch, role):
    ctrl = Mock()
    chain = _cef_plan(monkeypatch, ctrl, role)
    invoke = Mock(return_value="invoked")
    monkeypatch.setattr(client, "_invoke_with_timeout", invoke)
    result = client._run_chain(chain, lambda _: (False, "état inchangé"), 0)
    assert result["verified"] is None and result["technique"] == "UIA Invoke"
    invoke.assert_called_once_with(ctrl)
    ctrl.GetLegacyIAccessiblePattern.assert_not_called()
    ctrl.GetSelectionItemPattern.assert_not_called()


def test_cef_without_invoke_uses_legacy_default_action_before_select(monkeypatch):
    ctrl = Mock()
    chain = _cef_plan(monkeypatch, ctrl, invoke=False)
    assert [name for name, _ in chain] == ["UIA default action"]
    result = client._run_chain(chain, lambda _: (True, "nouvel état relu"), 0)
    assert result["verified"] is True
    ctrl.GetLegacyIAccessiblePattern.return_value.DoDefaultAction.assert_called_once()
    ctrl.GetSelectionItemPattern.assert_not_called()


def test_cef_click_never_uses_select_when_click_patterns_are_unavailable(monkeypatch):
    ctrl = Mock()
    ctrl.GetLegacyIAccessiblePattern.return_value = None
    chain = _cef_plan(monkeypatch, ctrl, invoke=False)
    result = client._run_chain(chain, lambda _: (True, "unrelated selection"), 0)
    assert result["technique"] is None  # aucune entrée, le caller peut seulement proposer le repli physique autorisé
    ctrl.GetSelectionItemPattern.assert_not_called()


def test_cef_default_action_exception_after_call_is_terminal_unknown(monkeypatch):
    ctrl = Mock()
    ctrl.GetLegacyIAccessiblePattern.return_value.DoDefaultAction.side_effect = RuntimeError("COM reply lost")
    chain = _cef_plan(monkeypatch, ctrl, invoke=False)
    result = client._run_chain(chain, lambda _: (True, "unrelated repaint"), 0)
    assert result["verified"] is None and result["pending"] is True
    ctrl.GetSelectionItemPattern.assert_not_called()


def test_plain_electron_list_keeps_select_without_invoke(monkeypatch):
    chain = _cef_plan(monkeypatch, Mock(), cef=False)
    assert [name for name, _ in chain] == ["UIA Select"]


def test_cef_multiline_does_not_gain_generic_value_typing(monkeypatch):
    ctrl = SimpleNamespace(AriaProperties="multiline=true")
    monkeypatch.setattr(controls, "has_cef_content", lambda hwnd: True)
    monkeypatch.setattr(client, "is_electron_window", lambda hwnd: False)
    monkeypatch.setattr(client, "_native_kind", lambda hwnd: "")
    info = {"type": "Edit", "props": {"has_value": True}}
    assert not client._background_writable(ctrl, info, 1)
    ctrl.AriaProperties = ""
    assert client._background_writable(ctrl, info, 1)


@pytest.fixture
def cef_scroll(monkeypatch):
    monkeypatch.setattr(client, "ensure_default_desktop", lambda: True)
    monkeypatch.setattr(client, "resolve_window", lambda hwnd: 1)
    monkeypatch.setattr(client, "is_protected_system_window", lambda hwnd: (False, ""))
    monkeypatch.setattr(controls, "has_cef_content", lambda hwnd: True)
    monkeypatch.setattr(controls, "family", lambda hwnd: "Win32")
    monkeypatch.setattr(controls, "root_of", lambda hwnd: 1)
    monkeypatch.setattr(client, "get_window_rect", lambda hwnd: {"left": 10, "top": 20})
    posted = Mock(side_effect=AssertionError("CEF ne reçoit pas une molette Win32"))
    monkeypatch.setattr(controls, "post_wheel", posted)
    pattern = Mock(side_effect=AssertionError("pas de défilement par ligne/ScrollPattern pour cette molette CEF"))
    monkeypatch.setattr(client.uia_scroll, "at_point", pattern)
    monkeypatch.setattr(client.uia_scroll, "item_at", pattern)
    return posted, pattern


def test_cef_wheel_tries_strict_cdp_then_refuses_without_input(cef_scroll, monkeypatch):
    cdp = Mock(side_effect=client.cdp_wheel.NotApplicable("page non attribuable"))
    monkeypatch.setattr(client.cdp_wheel, "wheel", cdp)
    monkeypatch.setattr(client, "_physical_gate", Mock(side_effect=client.NeedsForegroundError("foreground=true")))
    with pytest.raises(client.NeedsForegroundError):
        client.scroll(-1, hwnd=1, x=30, y=40, foreground=False)
    cdp.assert_called_once()
    assert cdp.call_args.args == (1, 40, 60, -1, 0)
    for unused in cef_scroll:
        unused.assert_not_called()


def test_cef_cdp_unknown_is_terminal_before_physical_fallback(cef_scroll, monkeypatch):
    monkeypatch.setattr(client.cdp_wheel, "wheel", Mock(side_effect=client.cdp_wheel.Uncertain("reply lost")))
    gate = Mock(side_effect=AssertionError("aucun repli après envoi"))
    monkeypatch.setattr(client, "_physical_gate", gate)
    result = client.scroll(-1, hwnd=1, x=30, y=40, foreground=True)
    assert result["verified"] is None and result["input_outcome"] == "unknown" and result["pending"] is True
    gate.assert_not_called()


def test_cef_does_not_inherit_simulated_keyboard_activation(monkeypatch):
    monkeypatch.setattr(controls, "family", lambda hwnd: "Win32")
    monkeypatch.setattr(controls, "root_of", lambda hwnd: hwnd)
    monkeypatch.setattr(controls, "class_name", lambda hwnd: "SDL_app")
    monkeypatch.setattr(controls, "has_cef_content", lambda hwnd: True)
    monkeypatch.setattr(client, "_designated_field", lambda hwnd: None)
    monkeypatch.setattr(controls, "keyboard_target", lambda hwnd: 0)
    simulated = Mock(side_effect=AssertionError("aucune activation simulée SDL"))
    monkeypatch.setattr(controls, "simulated_activation", simulated)
    posted = Mock(side_effect=AssertionError("aucun clavier natif posté au cadre SDL"))
    monkeypatch.setattr(controls, "post_shortcut", posted)
    monkeypatch.setattr(controls, "post_key", posted)
    # Le garde de classe doit rester fermé même en présence d'un CEF enfant.
    assert not controls.is_chromium_top(1)
    assert client._background_keys(1, "ctrl+k", ["ctrl", "k"], [(17, False), (75, False)], None) is None
    simulated.assert_not_called()
    posted.assert_not_called()


def test_partial_posted_wheel_never_promoted_by_repaint(cef_scroll, monkeypatch):
    monkeypatch.setattr(controls, "has_cef_content", lambda hwnd: False)
    monkeypatch.setattr(controls, "post_wheel", Mock(side_effect=controls.PartialSendError("partial")))
    monkeypatch.setattr(client, "window_fingerprint", lambda *a, **k: {})
    readback = Mock(side_effect=AssertionError("aucune relecture ne transforme un envoi partiel en succès"))
    monkeypatch.setattr(client, "_wait_window_effect", readback)
    result = client.scroll(-1, hwnd=1, x=30, y=40, foreground=True)
    assert result["verified"] is None and result["input_outcome"] == "unknown" and result["partial"] is True
    readback.assert_not_called()


def test_cef_popup_requires_owner_or_captionless_menu_thread_and_overlap(monkeypatch):
    # Principal1 ; vrai menu2 ; Friends3 et Store4 mêmePID+thread+above ; autreowner5 ; éloigné6 ; mauvaisthread7.
    names = {1: "SDL_app", 2: "#32768", 3: "SDL_app", 4: "SDL_app", 5: "#32768", 6: "#32768", 7: "#32768", 8: "SDL_app"}
    titles = {1: "Steam", 2: "", 3: "Friends", 4: "Store", 5: "", 6: "", 7: "", 8: "owned menu"}
    owners = {5: 99, 8: 1}
    monkeypatch.setattr(client, "app_pid", lambda hwnd: 10)
    monkeypatch.setattr(client, "is_window_cloaked", lambda hwnd: False)
    monkeypatch.setattr(client, "_window_text", lambda hwnd: titles[hwnd])
    monkeypatch.setattr(controls, "class_name", lambda hwnd: names[hwnd])
    monkeypatch.setattr(controls, "has_cef_content", lambda hwnd: hwnd in {1, 3, 4})
    monkeypatch.setattr(client.user32, "IsWindowVisible", lambda hwnd: True)
    monkeypatch.setattr(client.user32, "GetWindow", lambda hwnd, flag: owners.get(hwnd, 0))
    monkeypatch.setattr(client.user32, "GetWindowThreadProcessId", lambda hwnd, ptr: 200 if hwnd == 7 else 100)
    monkeypatch.setattr(client.user32, "GetWindowLongW", lambda hwnd, index: client.WS_POPUP if index == -16 else 0)
    monkeypatch.setattr(
        client.user32, "EnumWindows", lambda cb, param: [cb(h, param) for h in (2, 3, 4, 5, 6, 7, 8, 1)]
    )

    def bounds(hwnd):
        left = 500 if hwnd == 6 else 0
        return {"left": left, "top": 0, "right": left + 100, "bottom": 100, "width": 100, "height": 100}

    monkeypatch.setattr(client, "get_window_rect", bounds)
    assert client.cef_popups_of(1) == [8, 2]


def test_joined_popup_labels_have_no_main_window_indices(monkeypatch):
    monkeypatch.setattr(client, "cef_popups_of", lambda hwnd: [2])
    monkeypatch.setattr(client, "ensure_readable", lambda hwnd: None)
    monkeypatch.setattr(client, "get_window_rect", lambda hwnd: {})
    monkeypatch.setattr(client, "_window_text", lambda hwnd: "menu")
    monkeypatch.setattr(
        client,
        "_read_uia_elements",
        lambda *a, **kw: {
            "elements": [{"index": 1, "type": "MenuItem", "name": "Settings"}],
            "report": {"status": "ok"},
        },
    )
    views = client._cef_popup_views(1, 100, 30, 2, None)
    assert views[0]["hwnd"] == 2 and "index" not in views[0]["elements"][0]
    assert "look(hwnd=2)" in views[0]["next"]
