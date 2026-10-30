"""La valeur d'une ComboBox WPF non éditable est sa sélection, pas ValuePattern absent ni son libellé."""

from __future__ import annotations

import pytest
from test_contract import HWND, FakeAuto, as_call, executor, make_view, node

from nexus_windows import postcond, server, uia


def _automation(monkeypatch, *, selection=("Bleu",), has_value=False, value=""):
    combo = node("ComboBox", "Couleur", has_value=has_value, value=value, legacy_value="")
    other = node("ComboBox", "Autre", has_value=False, value="", legacy_value="")
    reads = []

    class Item:
        def __init__(self, name):
            self.name = name

        def GetCurrentPropertyValue(self, pid):
            assert pid == uia.PROPS["name"]
            return self.name

    class Array:
        Length = len(selection)

        def GetElement(self, index):
            return Item(selection[index])

    class Pattern:
        def GetCurrentSelection(self):
            return Array()

    auto = uia.Automation.__new__(uia.Automation)
    vars(auto)["_cache_one"] = object()
    monkeypatch.setattr(auto, "_props", lambda element: dict(element.live))
    monkeypatch.setattr(combo.element, "BuildUpdatedCache", lambda cache: combo.element, raising=False)

    def pattern(element, name):
        assert element is combo.element and name == "selection"
        reads.append((element, name))
        return Pattern()

    monkeypatch.setattr(auto, "pattern", pattern)
    return auto, combo, other, reads


def test_refresh_reads_selection_from_the_same_noneditable_combo(monkeypatch):
    auto, combo, _, reads = _automation(monkeypatch)
    props = auto.refresh(combo.element)
    assert props["selection_value"] == "Bleu" and reads == [(combo.element, "selection")]
    assert postcond._compare(props, {"value": "Bleu"}).state is True
    assert postcond._compare(props, {"value": "Rouge"}).state is False
    assert postcond._compare(props, {"contains": "ble"}).state is True


@pytest.mark.parametrize(
    "selection,wanted,state",
    [
        ((), "Bleu", False),
        (("Rouge",), "Bleu", False),
        (("Bleu", "Rouge"), "Bleu", None),
    ],
)
def test_absent_wrong_or_multiple_selection_never_verifies_blue(monkeypatch, selection, wanted, state):
    auto, combo, _, _ = _automation(monkeypatch, selection=selection)
    assert postcond._compare(auto.refresh(combo.element), {"value": wanted}).state is state


def test_unreadable_selection_does_not_fall_back_to_label_or_default_value(monkeypatch):
    auto, combo, _, _ = _automation(monkeypatch)

    def unavailable(*_):
        raise uia.UIAError("SelectionPattern unavailable")

    monkeypatch.setattr(auto, "pattern", unavailable)
    props = auto.refresh(combo.element)
    assert props["selection_value"] is None
    assert postcond._compare(props, {"value": ""}).state is None
    assert postcond._compare(props, {"value": "Couleur"}).state is None


def test_editable_combo_keeps_its_real_value_without_reading_selection(monkeypatch):
    auto, combo, _, reads = _automation(monkeypatch, has_value=True, value="Texte libre")
    props = auto.refresh(combo.element)
    assert postcond._compare(props, {"value": "Texte libre"}).state is True and not reads


def test_same_combo_postcondition_verifies_selection_and_skips_repeat(monkeypatch):
    monkeypatch.setattr(server.w, "IsWindow", lambda hwnd: True)
    native, combo, other, _ = _automation(monkeypatch, selection=("Rouge",))
    selected = {"name": "Rouge"}
    monkeypatch.setattr(
        native, "selection_value", lambda element: selected["name"] if element is combo.element else "Vert"
    )

    class Auto(FakeAuto):
        def refresh(self, element):
            if element is combo.element:
                return native.refresh(element)
            return super().refresh(element)

    auto = Auto()
    ex = executor(make_view(combo, other), auto)

    def select(target, option):
        assert target == "Couleur" and option == "Bleu"
        auto.calls.append(("select", option))
        selected["name"] = option
        from nexus_windows import actions

        return actions.Outcome(True, "selected", "UIA Select", verified=True)

    monkeypatch.setattr(ex, "select", select)
    step = server.Step(do="select", target="Couleur", option="Bleu", check=server.Check(target="Couleur", value="Bleu"))
    symbols: list[str] = []
    _, failed = server._run_steps(ex, [step, step], HWND, as_call(), symbols)
    assert not failed and symbols == ["✓", "="] and auto.calls == [("select", "Bleu")]
