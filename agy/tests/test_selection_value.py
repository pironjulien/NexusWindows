"""Valeur WPF d'une ComboBox non éditable : sélection courante du même conteneur, aucune fenêtre réelle."""

from types import SimpleNamespace
from typing import Any, cast
from unittest.mock import Mock

import computer_client as client
import pytest
import uia_fast


def selection_reader(names, *, available=True, array_available=True, has_value=False):
    calls = []

    def element_at(index):
        assert index == 0

        def property_value(prop):
            assert prop == uia_fast.PROPS["name"]
            return names[0]

        return SimpleNamespace(GetCurrentPropertyValue=property_value)

    class Selection:
        @property
        def Length(self):
            return len(names)

        GetElement = staticmethod(element_at)

    def current_selection():
        calls.append("selection")
        return Selection() if array_available else None

    pattern = SimpleNamespace(GetCurrentSelection=current_selection)

    def query_interface(interface):
        assert interface is uia_fast.U.IUIAutomationSelectionPattern
        return pattern

    def current_pattern(pattern_id):
        assert pattern_id == uia_fast.U.UIA_SelectionPatternId
        calls.append("pattern")
        return SimpleNamespace(QueryInterface=query_interface) if available else None

    container = SimpleNamespace(GetCurrentPattern=current_pattern)
    source = SimpleNamespace(BuildUpdatedCache=lambda cache: container)
    props = {
        "control_type": uia_fast.U.UIA_ComboBoxControlTypeId,
        "has_value": has_value,
        "value": "DEFAULT_NOT_SELECTION",
        "name": "Couleur",
        "automation_id": "color-selector",
    }
    reader = object.__new__(uia_fast.Reader)
    reader.cache_one = cast(Any, object())
    reader.props = lambda element: dict(props)
    return SimpleNamespace(reader=reader, source=source, props=props, names=names, calls=calls)


@pytest.mark.parametrize(
    "names,available,array_available,expected",
    [
        (["Bleu"], True, True, "Bleu"),
        ([], True, True, ""),
        (["Bleu", "Rouge"], True, True, None),
        (["Bleu"], False, True, None),
        (["Bleu"], True, False, None),
        ([42], True, True, None),
    ],
)
def test_selection_pattern_unknown_differs_from_known_empty(names, available, array_available, expected):
    provider = selection_reader(names, available=available, array_available=array_available)
    assert provider.reader.refresh(provider.source)["selection_value"] == expected


def test_editable_combo_keeps_value_pattern_without_selection_query():
    provider = selection_reader(["Bleu"], has_value=True)
    props = provider.reader.refresh(provider.source)
    assert props["value"] == "DEFAULT_NOT_SELECTION" and "selection_value" not in props
    assert provider.calls == []


@pytest.fixture
def wpf_combo(monkeypatch, tmp_path):
    provider = selection_reader(["Rouge"])
    monkeypatch.setattr(uia_fast, "reader", lambda: provider.reader)
    monkeypatch.setattr(client, "_native_kind", lambda hwnd: "")
    monkeypatch.setattr(client, "resolve_window", lambda hwnd: 7)
    monkeypatch.setattr(client, "_window_text", lambda hwnd: "Labo WPF")
    monkeypatch.setattr(client, "ensure_default_desktop", lambda: None)
    monkeypatch.setattr(client, "is_protected_system_window", lambda hwnd: (False, ""))
    monkeypatch.setattr(client, "window_browser_note", lambda hwnd: None)
    monkeypatch.setattr(client, "_element_family", lambda info, hwnd: "WPF")
    monkeypatch.setattr(client.user32, "GetForegroundWindow", lambda: 0)
    monkeypatch.setattr(client, "EFFECT_WAIT_S", 0)
    monkeypatch.setattr(client, "EXPECT_TIMEOUT_S", 0)
    app = tmp_path / "wpf.exe"
    app.write_bytes(b"test application")
    monkeypatch.setattr(client, "app_pid", lambda hwnd: 17)
    monkeypatch.setattr(client, "_routine_process_instance", lambda hwnd: (17, 42))
    monkeypatch.setattr(client, "get_process_image_path", lambda pid: (str(app), app.name))
    info = {"type": "ComboBox", "hwnd": 0, "framework": "WPF"}
    monkeypatch.setattr(client, "_target_element", lambda *args, **kwargs: (1, provider.source, info))

    def node(role, name, props=None, element=None):
        return SimpleNamespace(
            control_type=role,
            name=name,
            framework="WPF",
            parent=-1 if role == "Window" else 0,
            host_hwnd=7,
            element=element or object(),
            props=props or {},
        )

    def tree(*args, **kwargs):
        return SimpleNamespace(
            status="ok",
            nodes=[
                node("Window", "Labo WPF"),
                node("Text", "Couleur"),
                node("ComboBox", "Couleur", {**provider.props, "class_name": "ComboBox"}, provider.source),
            ],
        )

    monkeypatch.setattr(client, "_window_tree", tree)
    sent = []

    def select(ctrl, option, hwnd):
        assert ctrl is provider.source
        sent.append(option)
        provider.names[:] = [option]
        return "uia"

    monkeypatch.setattr(client, "_uia_select_option", select)
    provider.sent = sent
    provider.info = info
    return provider


def test_read_state_and_expect_read_selection_of_named_combo(wpf_combo):
    assert client._read_state(wpf_combo.source, wpf_combo.info)["value"] == "Rouge"
    met, observed = client.check_expect(7, {"element": "Couleur", "value": "Rouge"})
    assert met is True and observed["value"] == "Rouge"
    assert client.check_expect(7, {"element": "Couleur", "value": "Bleu"})[0] is False


def test_real_select_and_routine_expect_share_current_selection_proof(wpf_combo):
    request = [
        {"action": "select", "target": "Couleur", "option": "Bleu", "expect": {"element": "Couleur", "value": "Bleu"}}
    ]
    learned = client.act(request, 7, remember="couleur-wpf", staticUI=True, then="none")
    assert learned["success"] is True and learned["routine"]["saved"]
    assert learned["steps"][0]["result"]["verified_by"] == "option relue « Bleu »"
    assert learned["steps"][0]["result"]["expect"]["observed"]["value"] == "Bleu"
    already = client.act(hwnd=7, routine="couleur-wpf", then="none")
    assert already["success"] is True and already["steps"][0]["already"] and wpf_combo.sent == ["Bleu"]


def test_unavailable_selection_stays_unknown_even_with_matching_descendant(wpf_combo, monkeypatch):
    def unavailable(element):
        return {**wpf_combo.props, "selection_value": None, "value": "Bleu"}

    monkeypatch.setattr(uia_fast, "refresh", unavailable)
    descendants = Mock(return_value=[(object(), {"selected": True, "name": "Bleu"})])
    monkeypatch.setattr(client, "_uia_descendants", descendants)
    assert client._read_state(wpf_combo.source, wpf_combo.info).get("value") is None
    assert client.check_expect(7, {"element": "Couleur", "value": "Bleu"})[0] is None
    result = client.select_option("Bleu", hwnd=7, element_index=1)
    assert result["verified"] is None and result["success"] is None and wpf_combo.sent == ["Bleu"]
    descendants.assert_not_called()
