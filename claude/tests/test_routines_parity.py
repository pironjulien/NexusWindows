"""Contrat commun des procédures : preuve, paramètres, reprise explicite et miroir sans contenu courant."""

from __future__ import annotations

import asyncio
import contextlib
import json

import pytest
from test_accord import _mail_executor
from test_contract import HWND, FakeAuto, as_call, executor, make_view, node, token_of

from nexus_windows import actions, routines, server, worker

APP = "test.exe"
FP = {"path": "C:/app/test.exe", "size": 10, "mtime": 1}
TEMPLATE = [
    server.Step(do="set", target="Nom", text="{{nom}}", check=server.Check(target="Nom", value="{{nom}}")),
    server.Step(do="check", target="Option", check=server.Check(target="Option", checked=True)),
]


def _save(name="fiche", steps=None):
    routines.remember(APP, name, steps or [{"do": "click", "target": "OK"}], FP, "Titre privé")


def test_learn_replay_other_parameter_then_already_sends_nothing(monkeypatch):
    monkeypatch.setattr(server.w, "IsWindow", lambda hwnd: True)
    field = node("Edit", "Nom", has_value=True, value="")
    option = node("CheckBox", "Option", has_toggle=True, toggle=0)
    auto = FakeAuto()
    ex = executor(make_view(field, option), auto)
    filled = server._filled(TEMPLATE, {"nom": "Alpha privé"})
    symbols: list[str] = []
    _, failed = server._run_saved_steps(ex, filled, HWND, as_call(), symbols, APP, None)
    assert symbols == ["✓", "✓"] and not failed
    assert "saved for" in server._remember(APP, FP, "Titre privé", "fiche", TEMPLATE, symbols, failed)
    assert routines.get(APP, "fiche")["verifiedTransitions"] == 2
    steps, template = server._routine_steps(APP, FP, "fiche", {"nom": "Bravo privé"}, [])
    symbols = []
    server._run_saved_steps(ex, steps, HWND, as_call(), symbols, APP, "fiche")
    assert symbols == ["✓", "="] and field.element.live["value"] == "Bravo privé"
    before = list(auto.calls)
    symbols = []
    server._run_saved_steps(ex, steps, HWND, as_call(), symbols, APP, "fiche")
    assert symbols == ["=", "="] and auto.calls == before
    info = routines.inspect(APP, "fiche", FP)
    assert info["coverage"] == "verified" and not info["needsReview"]
    assert template[0].text == "{{nom}}" and info["parameters"] == ["nom"]
    assert routines.MIRROR is not None
    persisted = (routines.ROOT / f"{APP}.json").read_text(encoding="utf-8")
    mirrored = (routines.MIRROR / f"{APP}.json").read_text(encoding="utf-8")
    for private in ("Alpha privé", "Bravo privé", "Titre privé", '"window"', '"accord"', '"confirm"'):
        assert private not in persisted + mirrored + json.dumps(info)
    assert routines.forget(APP, "fiche") and routines.names(APP) == []


@pytest.mark.parametrize(
    "steps,symbols,count",
    [
        (TEMPLATE, ["=", "="], 0),
        ([server.Step(do="wait", timeout=0.1)], ["✓"], 0),
        ([server.Step(do="check", target="Option")], ["✓"], 1),
        ([server.Step(do="check", target="Option")] * 34, ["="] * 34, 0),
    ],
)
def test_saved_provenance_distinguishes_real_input_transitions(steps, symbols, count):
    assert "saved for" in server._remember(APP, FP, "Titre privé", "fiche", steps, symbols, False)
    info = routines.inspect(APP, "fiche", FP)
    assert info["verifiedTransitions"] == count
    assert info["coverage"] == ("verified" if count else "not-exercised")


def test_fingerprint_compares_path_and_normalizes_windows_spelling():
    _save()
    entry = routines.get(APP, "fiche")
    assert not routines.changed(entry, FP | {"path": "c:\\APP\\test.exe"})
    assert routines.inspect(APP, "fiche", FP | {"path": "C:/other/test.exe"})["fingerprintChanged"]
    assert routines.inspect(APP, "fiche", FP)["coverage"] == "unqualified"


@pytest.mark.parametrize("role", ["Window", "Document"])
def test_learning_observed_bare_window_or_document_title_remains_local(role):
    view = make_view(node(role, "SECRET_TITLE", has_invoke=True))
    steps = [server.Step(do="click", target="SECRET_TITLE")]
    assert "saved for" in server._remember(APP, FP, "Window title", "fiche", steps, ["✓"], False, view)
    assert routines.get(APP, "fiche")["localOnly"] is True
    assert routines.expand(routines.get(APP, "fiche"), None)[0]["target"] == "SECRET_TITLE"
    assert routines.MIRROR is not None
    path = routines.MIRROR / f"{APP}.json"
    assert not path.exists() or "SECRET_TITLE" not in path.read_text(encoding="utf-8")


def test_intrinsic_already_true_proof_does_not_claim_an_input_transition(monkeypatch):
    monkeypatch.setattr(server.w, "IsWindow", lambda hwnd: True)
    box = node("CheckBox", "Option", has_toggle=True, toggle=1)
    auto = FakeAuto()
    steps = [server.Step(do="check", target="Option")]
    symbols: list[str] = []
    _, failed = server._run_saved_steps(executor(make_view(box), auto), steps, HWND, as_call(), symbols, APP, None)
    assert symbols == ["="] and not failed and not auto.calls
    server._remember(APP, FP, "T", "fiche", steps, symbols, failed)
    assert routines.inspect(APP, "fiche", FP)["coverage"] == "not-exercised"


@pytest.mark.parametrize("params", [{"nom": 12}, {"nom": True}, {"nom": ["private"]}, []])
def test_parameters_are_text_values_only(params):
    with pytest.raises(routines.RoutineError, match="text values"):
        routines.fill([{"do": "set", "text": "{{nom}}"}], params)


@pytest.mark.parametrize(
    "step",
    [
        server.Step(do="click", target="{{cible}}"),
        server.Step(do="key", keys="{{cible}}"),
        server.Step(do="key", text="{{cible}}"),
        server.Step(do="menu", path="{{cible}}"),
        server.Step(do="menu", text="{{cible}}"),
        server.Step(do="click", target="OK", check=server.Check(target="{{cible}}", exists=True)),
    ],
)
def test_explicit_parameterized_plan_refuses_structural_substitution_before_execution(step):
    with pytest.raises(actions.ActionError, match="Parameters refused"):
        server._planned(APP, FP, [step], None, {"cible": "12,34"}, [])


def test_explicit_direct_targets_keys_and_menu_paths_remain_available():
    steps = [
        server.Step(do="click", target="12,34"),
        server.Step(do="key", keys="ctrl+s"),
        server.Step(do="menu", path="File > Open"),
    ]
    filled, template = server._planned(APP, FP, steps, None, None, [])
    assert filled is steps and template is steps


@pytest.mark.parametrize(
    "step",
    [
        {"do": "click", "target": {"private": "secret"}},
        {"do": "click", "target": "OK", "check": {"target": {"private": "secret"}}},
        {"do": "click", "target": "OK", "check": {"target": "12,34", "exists": True}},
        {"do": "key", "keys": "{{shortcut}}"},
        {"do": "key", "text": "{{shortcut}}"},
        {"do": "menu", "path": "{{menu}}"},
        {"do": "menu", "text": "{{menu}}"},
        {"do": "select", "target": "Couleur", "option": "{{choice}}"},
        {"do": "select", "target": "Couleur", "text": "{{choice}}"},
        {"do": "click", "target": "OK", "timeout": {"private": "secret"}},
    ],
)
def test_invalid_or_structural_parameters_are_refused_before_storage(step):
    with pytest.raises(routines.RoutineError):
        _save(steps=[step])
    assert routines.names(APP) == []


def test_legacy_fixed_typed_content_is_refused_at_replay():
    with pytest.raises(routines.RoutineError, match="fixed typed text"):
        routines.expand({"steps": [{"do": "set", "target": "Nom", "text": "Contenu privé"}]}, None)


def test_authorization_is_current_rechecked_and_never_inherited(monkeypatch):
    monkeypatch.setattr(server.w, "IsWindow", lambda hwnd: True)
    _save("envoyer", [{"do": "click", "target": "Envoyer", "confirm": True, "accord": "ancien privé"}])
    auto = FakeAuto()
    ex, body = _mail_executor(auto)
    steps, _ = server._routine_steps(APP, FP, "envoyer", None, [])
    symbols: list[str] = []
    report, failed = server._run_saved_steps(ex, steps, HWND, as_call(), symbols, APP, "envoyer")
    assert failed and not auto.calls and routines.get(APP, "envoyer")["last_ok"] is False
    token = token_of(report[0])
    with pytest.raises(routines.RoutineError, match="needs review"):
        server._routine_steps(APP, FP, "envoyer", None, [])
    routines.inspect(APP, "envoyer", FP)  # lire la définition seule ne qualifie pas une nouvelle observation
    with pytest.raises(routines.RoutineError, match="needs review"):
        server._routine_steps(APP, FP, "envoyer", None, [])
    routines.reviewed(APP, "envoyer")
    steps, template = server._routine_steps(
        APP,
        FP,
        "envoyer",
        None,
        [],
        {
            "0": server.RoutineAuthorization(confirm=True, accord=token),
        },
    )
    assert steps[0].accord == token and not template[0].confirm and template[0].accord is None
    body.element.live["value"] = body.props["value"] = "Autre contenu courant"
    symbols = []
    report, failed = server._run_saved_steps(ex, steps, HWND, as_call(), symbols, APP, "envoyer")
    assert failed and not auto.calls and "changed since the accord" in report[0]
    with pytest.raises(routines.RoutineError, match="needs review"):
        server._routine_steps(APP, FP, "envoyer", None, [])  # inspection antérieure invalidée par le nouvel échec
    routines.reviewed(APP, "envoyer")
    steps, _ = server._routine_steps(APP, FP, "envoyer", None, [])
    assert not steps[0].confirm and steps[0].accord is None
    assert "ancien privé" not in (routines.ROOT / f"{APP}.json").read_text(encoding="utf-8")


@pytest.mark.parametrize("index", ["-1", "01", "1"])
def test_authorization_indices_must_name_exact_existing_steps(index):
    _save()
    with pytest.raises(routines.RoutineError, match="indices"):
        server._routine_steps(APP, FP, "fiche", None, [], {index: server.RoutineAuthorization(confirm=True)})


@pytest.mark.parametrize("outcome,failed", [(["~"], False), (["✗"], True), (["✓"], False), (["✓", "="], False)])
def test_incomplete_unknown_and_failed_replays_are_marked_for_review(monkeypatch, outcome, failed):
    _save(steps=[{"do": "click", "target": "A"}, {"do": "click", "target": "B"}])

    def execute(ex, steps, hwnd, call, symbols):
        symbols.extend(outcome)
        return ["report"], failed

    monkeypatch.setattr(server, "_run_steps", execute)
    server._run_saved_steps(None, TEMPLATE, HWND, as_call(), [], APP, "fiche")
    assert routines.inspect(APP, "fiche", FP)["needsReview"] is (outcome != ["✓", "="])


@pytest.mark.parametrize("interruption", [worker.Stopped, worker.Cancelled])
def test_interruption_marks_review_and_retains_original_exception(monkeypatch, interruption):
    _save()

    def interrupted(*_):
        raise interruption("interrupted")

    monkeypatch.setattr(server, "_run_steps", interrupted)
    with pytest.raises(interruption, match="interrupted"):
        server._run_saved_steps(None, TEMPLATE, HWND, as_call(), [], APP, "fiche")
    assert routines.inspect(APP, "fiche", FP)["needsReview"]


def test_disk_failure_cannot_restore_old_success_or_reuse_old_inspection(monkeypatch):
    _save()
    path = routines.ROOT / f"{APP}.json"
    old_success = path.read_bytes()
    routines.reviewed(APP, "fiche")

    def refused(*_):
        raise OSError("disk full")

    with monkeypatch.context() as failing:
        failing.setattr(routines, "_save", refused)
        for _attempt in range(2):
            with pytest.raises(OSError, match="disk full"):
                routines.record_run(APP, "fiche", False)
            path.write_bytes(old_success)  # fichier intact ou restauration d'un ancien succès
            assert routines.get(APP, "fiche")["last_ok"] is True
            assert routines.inspect(APP, "fiche", FP)["needsReview"] and "needs review" in routines.summary(APP)
            with pytest.raises(routines.RoutineError, match="needs review"):
                server._routine_steps(APP, FP, "fiche", None, [])
            routines.reviewed(APP, "fiche")  # normalement uniquement après le look réel terminé
            server._routine_steps(APP, FP, "fiche", None, [])
            assert not routines.inspect(APP, "fiche", FP)["needsReview"]
    routines.record_run(APP, "fiche", True)
    assert not routines.inspect(APP, "fiche", FP)["needsReview"]


def test_disk_failure_review_lock_is_scoped_to_store_and_definition(monkeypatch, tmp_path):
    _save()
    original = routines.ROOT
    save = routines._save

    def refused(*_):
        raise OSError("disk full")

    monkeypatch.setattr(routines, "_save", refused)
    with pytest.raises(OSError):
        routines.record_run(APP, "fiche", False)
    monkeypatch.setattr(routines, "_save", save)
    monkeypatch.setattr(routines, "ROOT", tmp_path / "other-store")
    _save()
    server._routine_steps(APP, FP, "fiche", None, [])  # même application/nom, autre stockage
    monkeypatch.setattr(routines, "ROOT", original)
    with pytest.raises(routines.RoutineError, match="needs review"):
        server._routine_steps(APP, FP, "fiche", None, [])
    _save(steps=[{"do": "click", "target": "Another"}])  # nouvelle définition enregistrée explicitement
    server._routine_steps(APP, FP, "fiche", None, [])


@pytest.mark.parametrize("show,qualifies", [("elements", True), ("none", False)])
def test_explicit_look_inspection_qualifies_replay_without_input(monkeypatch, show, qualifies):
    _save()
    routines.record_run(APP, "fiche", False)

    async def run(job, **_):
        return job(None)

    monkeypatch.setattr(server, "_run", run)
    monkeypatch.setattr(server.SESSION, "resolve_window", lambda window: HWND)
    monkeypatch.setattr(routines, "app_of", lambda hwnd: (APP, FP))
    monkeypatch.setattr(server, "Watch", lambda hwnd: contextlib.nullcontext())
    monkeypatch.setattr(server, "render", lambda *args, **kwargs: kwargs["extra"])
    result = asyncio.run(server.look(window=HWND, routine_info="fiche", show=show))
    assert '"needsReview": true' in "\n".join(result)
    if qualifies:
        server._routine_steps(APP, FP, "fiche", None, [])
    else:
        with pytest.raises(routines.RoutineError, match="needs review"):
            server._routine_steps(APP, FP, "fiche", None, [])


def test_mirror_sanitizes_metadata_and_excludes_fixed_expect_and_malformed_entries():
    assert routines.MIRROR is not None
    routines.MIRROR.mkdir(parents=True)
    good = {
        "steps": [{"do": "click", "target": "OK", "confirm": True, "accord": "private accord"}],
        "saved": "2026-10-29T10:00:00",
        "window": "Private document",
        "runs": 88,
        "fingerprint": {**FP, "private": "Private metadata"},
        "coverage": "Private coverage",
    }
    data = {
        "schema": 1,
        "app": APP,
        "routines": {
            "good": good,
            "malformed": {**good, "steps": [{"do": "click", "target": {"private": "secret"}}]},
            "private": {**good, "steps": [{"do": "click", "target": "OK", "expect": "Private text"}]},
            "template": {**good, "steps": [{"do": "click", "target": "OK", "expect": "{{result}}"}]},
        },
    }
    (routines.MIRROR / f"{APP}.json").write_text(json.dumps(data), encoding="utf-8")
    routines.sync_app(APP)
    assert routines.names(APP) == ["good", "template"]
    assert routines.get(APP, "good")["coverage"] == "unqualified"
    mirrored = (routines.MIRROR / f"{APP}.json").read_text(encoding="utf-8")
    assert all(value not in mirrored for value in ("Private", "private accord", '"runs"', '"window"', '"confirm"'))
    assert routines.expand(routines.get(APP, "template"), {"result": "Fini"})[0]["expect"] == "Fini"


def test_failed_atomic_write_keeps_previous_definition_and_removes_temp(monkeypatch):
    _save()
    path = routines.ROOT / f"{APP}.json"
    before = path.read_bytes()

    def refused(*_):
        raise OSError("file locked")

    monkeypatch.setattr(routines.os, "replace", refused)
    with pytest.raises(OSError, match="file locked"):
        _save(steps=[{"do": "click", "target": "Other"}])
    assert path.read_bytes() == before and list(routines.ROOT.glob("*.tmp")) == []
