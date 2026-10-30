"""Miroir des routines entre les PC de Julien (NexusWindows/claude/routines) : fusion par date, oublis, jamais de texte en
clair. Le miroir et le dossier local sont ceux de conftest (dossiers du test, jamais ceux du poste)."""

from __future__ import annotations

import json
import re
from datetime import UTC, datetime
from pathlib import Path

import pytest

from nexus_windows import routines

STEPS = [{"do": "set", "target": "Nom", "text": "{{nom}}"}, {"do": "click", "target": "Enregistrer"}]


def _mirror() -> Path:
    """Miroir des routines, toujours posé par conftest (dossier du test) sauf dans le test qui le désactive."""
    assert routines.MIRROR is not None
    return routines.MIRROR


def _write(folder, app, data):
    folder.mkdir(parents=True, exist_ok=True)
    (folder / f"{app}.json").write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")


def _routine(saved, steps=STEPS, **extra):
    return {"steps": steps, "fingerprint": {"size": 1}, "window": "Fiche", "saved": saved, **extra}


def test_remember_writes_the_mirror_without_run_statistics():
    routines.remember("app.exe", "fiche", STEPS, {"size": 1}, "Fiche")
    routines.record_run("app.exe", "fiche", False)
    mirror = json.loads((_mirror() / "app.exe.json").read_text(encoding="utf-8"))
    assert mirror["routines"]["fiche"]["steps"] == STEPS
    assert "runs" not in mirror["routines"]["fiche"] and "last_ok" not in mirror["routines"]["fiche"]


def test_plain_typed_text_never_reaches_the_mirror():
    with pytest.raises(routines.RoutineError, match="fixed typed text"):  # jamais gardé, même au poste
        routines.remember("app.exe", "secret", [{"do": "set", "target": "Nom", "text": "Alice Martin"}], {}, "Fiche")
    routines.remember(
        "app.exe", "verif", [{"do": "click", "target": "OK", "check": {"target": "Nom", "value": "x"}}], {}, "F"
    )
    assert routines.names("app.exe") == ["verif"]  # gardée au poste
    mirror = (_mirror() / "app.exe.json").read_text(encoding="utf-8") if _mirror().exists() else ""
    assert "Alice Martin" not in mirror and '"secret"' not in mirror and '"verif"' not in mirror
    assert routines.plain_text({"steps": [{"do": "set", "text": "{{prenom}} {{nom}}"}]}) == ""


def test_newer_mirror_version_wins_and_older_is_kept_out():
    _write(
        routines.ROOT,
        "app.exe",
        {"schema": 1, "app": "app.exe", "routines": {"fiche": _routine("2026-10-01T10:00:00", runs=5)}},
    )
    newer = [{"do": "click", "target": "Valider"}]
    _write(
        _mirror(),
        "app.exe",
        {"schema": 1, "app": "app.exe", "routines": {"fiche": _routine("2026-10-02T10:00:00", newer)}},
    )
    changes = routines.sync()
    assert changes and routines.get("app.exe", "fiche")["steps"] == newer
    assert routines.get("app.exe", "fiche")["runs"] == 0  # statistiques d'une autre version : remises à zéro
    # Plus ancienne au miroir : le poste garde la sienne et la publie.
    _write(
        _mirror(),
        "app.exe",
        {"schema": 1, "app": "app.exe", "routines": {"fiche": _routine("2026-09-01T10:00:00")}},
    )
    routines.sync()
    mirror = json.loads((_mirror() / "app.exe.json").read_text(encoding="utf-8"))
    assert mirror["routines"]["fiche"]["steps"] == newer


def test_forgetting_propagates_and_is_not_undone_by_an_older_copy():
    routines.remember("app.exe", "fiche", STEPS, {}, "Fiche")
    assert routines.forget("app.exe", "fiche")
    mirror = json.loads((_mirror() / "app.exe.json").read_text(encoding="utf-8"))
    assert "fiche" not in mirror["routines"] and "fiche" in mirror["forgotten"]
    # Un autre poste avait une copie plus ancienne : elle ne revient pas.
    _write(
        routines.ROOT,
        "app.exe",
        {"schema": 1, "app": "app.exe", "routines": {"fiche": _routine("2026-01-01T00:00:00")}},
    )
    routines.sync()
    assert routines.names("app.exe") == []


def test_equal_timestamp_forget_wins_on_both_computers_and_cannot_resurrect(monkeypatch, tmp_path):
    stamp = "2026-10-29T10:00:00.123456Z"
    monkeypatch.setattr(routines, "_stamp", lambda: stamp)
    first, second = tmp_path / "first-pc", tmp_path / "second-pc"
    monkeypatch.setattr(routines, "ROOT", first)
    routines.remember("app.exe", "fiche", STEPS, {}, "Fiche")
    monkeypatch.setattr(routines, "ROOT", second)
    routines.sync_app("app.exe")
    assert routines.get("app.exe", "fiche")["saved"] == stamp
    assert routines.forget("app.exe", "fiche")  # même instant exact que l'enregistrement du premier PC
    for computer in (first, second, first, second):
        monkeypatch.setattr(routines, "ROOT", computer)
        routines.sync_app("app.exe")
        assert routines.names("app.exe") == []
        data = json.loads((computer / "app.exe.json").read_text(encoding="utf-8"))
        assert data["forgotten"]["fiche"] == stamp
    mirror = json.loads((_mirror() / "app.exe.json").read_text(encoding="utf-8"))
    assert mirror["routines"] == {} and mirror["forgotten"]["fiche"] == stamp
    # Une nouvelle création explicitement plus récente reste possible : l'oubli ne verrouille pas le nom.
    monkeypatch.setattr(routines, "_stamp", lambda: "2026-10-29T10:00:00.123457Z")
    routines.remember("app.exe", "fiche", STEPS, {}, "Fiche")
    monkeypatch.setattr(routines, "ROOT", first)
    routines.sync_app("app.exe")
    assert routines.names("app.exe") == ["fiche"]


def test_new_timestamps_are_precise_utc_and_compare_instants():
    before = datetime.now(UTC)
    routines.remember("app.exe", "fiche", STEPS, {}, "Fiche")
    routines.record_run("app.exe", "fiche", True)
    entry = routines.get("app.exe", "fiche")
    assert routines.forget("app.exe", "fiche")
    data = json.loads((routines.ROOT / "app.exe.json").read_text(encoding="utf-8"))
    after = datetime.now(UTC)
    for stamp in (entry["saved"], entry["last_run"], data["forgotten"]["fiche"]):
        assert re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z", stamp)
        assert before <= datetime.fromisoformat(stamp) <= after
    assert routines._stamp_order("2026-10-29T12:00:00+02:00") == routines._stamp_order("2026-10-29T10:00:00Z")
    legacy = datetime(2026, 10, 29, 12, 0)
    assert routines._stamp_order(legacy.isoformat()) == legacy.astimezone(UTC)


def test_sync_imports_routines_of_another_pc_and_never_rewrites_identically():
    _write(
        _mirror(),
        "autre.exe",
        {"schema": 1, "app": "autre.exe", "routines": {"r": _routine("2026-10-03T08:00:00")}},
    )
    assert routines.sync() == ["autre.exe: r: version of 2026-10-03T08:00:00 taken from the mirror"]
    path = _mirror() / "autre.exe.json"
    before = path.stat().st_mtime_ns
    assert routines.sync() == []
    assert path.stat().st_mtime_ns == before and routines.names("autre.exe") == ["r"]


def test_mirror_disabled_does_nothing(monkeypatch):
    monkeypatch.setattr(routines, "MIRROR", None)
    routines.remember("app.exe", "fiche", STEPS, {}, "Fiche")
    assert routines.sync() == [] and routines.main(["sync"]) == 0


def test_test_servers_never_use_julien_mirror(monkeypatch):
    monkeypatch.delenv("NEXUS_ROUTINES_MIRROR", raising=False)
    monkeypatch.setenv("NEXUS_WINDOWS_HOME", "C:/tmp/serveur-de-test")
    assert routines._default_mirror() is None
    monkeypatch.setenv("NEXUS_ROUTINES_MIRROR", "")
    assert routines._default_mirror() is None


@pytest.mark.parametrize(
    "step",
    [
        {"do": "click", "target": "document:SECRET_TITLE"},
        {"do": "focus", "target": 'Window "SECRET_TITLE"'},
        {"do": "drag", "target": "Item", "to": "Document:SECRET_TITLE"},
        {"do": "click", "target": "document:SECRET_TITLE > button:OK"},
        {"do": "click", "target": "OK", "check": {"target": "Window:SECRET_TITLE > Text:Statut", "exists": True}},
    ],
)
def test_document_window_titles_stay_local_and_old_mirrors_are_removed(step):
    routines.remember("app.exe", "private", [step], {}, "Fiche")
    assert routines.get("app.exe", "private")["steps"] == [step]  # capacité locale conservée
    assert routines.expand(routines.get("app.exe", "private"), None) == [step]
    path = _mirror() / "app.exe.json"
    assert not path.exists() or "SECRET_TITLE" not in path.read_text(encoding="utf-8")
    # Ancien miroir d'un autre PC : ni importé ni conservé dans le miroir nettoyé.
    _write(
        _mirror(),
        "app.exe",
        {
            "schema": 1,
            "app": "app.exe",
            "routines": {
                "remote private": _routine("2099-10-29T10:00:00Z", [step]),
            },
        },
    )
    routines.sync_app("app.exe")
    assert routines.names("app.exe") == ["private"]
    assert "SECRET_TITLE" not in path.read_text(encoding="utf-8")


def test_observed_bare_document_title_stays_local_without_persisting_title_metadata():
    steps = [{"do": "click", "target": "SECRET_TITLE"}]
    routines.remember("app.exe", "private", steps, {}, "Window title", private_titles={"SECRET_TITLE"})
    assert routines.get("app.exe", "private")["localOnly"] is True
    assert routines.expand(routines.get("app.exe", "private"), None) == steps
    routines.sync_app("app.exe")
    path = _mirror() / "app.exe.json"
    assert not path.exists() or "SECRET_TITLE" not in path.read_text(encoding="utf-8")
