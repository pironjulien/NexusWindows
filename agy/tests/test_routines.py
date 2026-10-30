"""Procédures réelles : stockage, lot vérifié, état à revoir et MCP ; aucun geste envoyé au poste."""

import copy
import json
import os
import threading
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace

import agent_guard
import computer_client as client
import mcp_server
import pytest
import routines


def steps():
    return [
        {"action": "set", "target": "Nom", "text": "{{nom}}", "expect": {"element": "Nom", "value": "{{nom}}"}},
        {"action": "check", "target": "Actif", "expect": {"element": "Actif", "checked": True}},
    ]


@pytest.fixture
def desktop(monkeypatch, tmp_path):
    app = tmp_path / "lab.exe"
    app.write_bytes(b"application")
    state = {"value": "", "toggle": 0, "expanded": 0, "focus": False}
    sent = []
    native_run = client._run_act_step

    def node(kind, name, parent, props):
        return SimpleNamespace(
            control_type=kind,
            name=name,
            parent=parent,
            framework="WinForms",
            host_hwnd=7,
            element=object(),
            props={"automation_id": name.lower(), "class_name": kind, **props},
        )

    def tree(*args, **kwargs):
        return SimpleNamespace(
            status="ok",
            nodes=[
                node("Window", "Titre privé", -1, {}),
                node("Edit", "Nom", 0, {"value": state["value"], "focus": state["focus"]}),
                node("CheckBox", "Actif", 0, {"toggle": state["toggle"], "expand": state["expanded"]}),
            ],
        )

    def run(action, step, *args):
        sent.append(copy.deepcopy(step))
        if action == "set":
            state["value"] = step["text"]
        elif action == "check":
            state["toggle"] = 1
        return {"success": True, "verified": True}

    monkeypatch.setattr(client, "resolve_window", lambda value: 7)
    monkeypatch.setattr(client, "is_protected_system_window", lambda hwnd: (False, ""))
    monkeypatch.setattr(client, "ensure_default_desktop", lambda: None)
    monkeypatch.setattr(client, "get_process_image_path", lambda pid: (str(app), app.name))
    monkeypatch.setattr(client, "app_pid", lambda hwnd: 17)
    monkeypatch.setattr(client, "_routine_process_instance", lambda hwnd: (client.app_pid(hwnd), 42))
    monkeypatch.setattr(client, "_window_text", lambda hwnd: "Titre privé")
    monkeypatch.setattr(client, "_window_tree", tree)
    monkeypatch.setattr(client, "_read_state", lambda *args: {})
    monkeypatch.setattr(client, "_run_act_step", run)
    monkeypatch.setattr(client, "window_browser_note", lambda hwnd: None)
    monkeypatch.setattr(client, "EXPECT_TIMEOUT_S", 0)
    return SimpleNamespace(state=state, sent=sent, app=app, tree=tree, native_run=native_run)


def learn():
    return client.act(steps(), 7, then="none", params={"nom": "Alice privée"}, remember="profil", staticUI=True)


def long_steps(count):
    """Chaque saisie change réellement l'état : aucune répétition déjà faite ne gonfle le compteur."""
    return [
        {
            "action": "set",
            "target": "Nom",
            "text": "{{" + f"v{i}" + "}}",
            "expect": {"element": "Nom", "value": "{{" + f"v{i}" + "}}"},
        }
        for i in range(count)
    ]


@pytest.mark.parametrize("count", [34, 65])
def test_long_procedure_learns_reloads_and_replays_real_transitions(desktop, count):
    request = long_steps(count)
    values = {f"v{i}": f"apprentissage privé {i}" for i in range(count)}
    result = client.act(request, 7, params=values, remember="longue", staticUI=True, then="none")
    assert result["success"] is True and result["routine"]["verifiedTransitions"] == count
    assert [item["step"] for item in result["steps"]] == list(range(count))
    assert len(desktop.sent) == count and not any(item.get("already") for item in result["steps"])
    assert desktop.sent[31]["text"] == values["v31"] and desktop.sent[32]["text"] == values["v32"]
    assert routines.get("lab.exe", "longue")["steps"] == request
    stored = (routines.ROOT / "lab.exe.json").read_text(encoding="utf-8")
    assert "apprentissage privé" not in stored
    desktop.sent.clear()
    replay_values = {f"v{i}": f"rejeu privé {i}" for i in range(count)}
    replay = client.act(hwnd=7, routine="longue", params=replay_values, then="none")
    assert replay["success"] is True and not replay["routine"]["needsReview"]
    assert len(desktop.sent) == count and desktop.state["value"] == replay_values[f"v{count - 1}"]
    assert not any(item.get("already") for item in replay["steps"])


def test_long_procedure_validates_last_step_before_any_input(desktop):
    request = long_steps(65)
    request[-1]["target"] = "#3"
    with pytest.raises(routines.RoutineError, match="64"):
        client.act(
            request, 7, params={f"v{i}": str(i) for i in range(65)}, remember="longue", staticUI=True, then="none"
        )
    assert desktop.sent == []


@pytest.mark.parametrize("partial", [True, False])
def test_unknown_or_failure_after_old_boundary_has_no_following_input_or_replay(desktop, monkeypatch, partial):
    request = long_steps(65)
    values = {f"v{i}": str(i) for i in range(65)}
    assert client.act(request, 7, params=values, remember="longue", staticUI=True, then="none")["success"]
    desktop.sent.clear()
    desktop.state["value"] = ""
    original_run = client._run_act_step

    def unknown(action, step, *args):
        if len(desktop.sent) == 32:
            desktop.sent.append(copy.deepcopy(step))
            return {"success": None if partial else False, "verified": None if partial else False, "partial": partial}
        return original_run(action, step, *args)

    monkeypatch.setattr(client, "_run_act_step", unknown)
    result = client.act(hwnd=7, routine="longue", params=values, then="none")
    assert result["success"] is (None if partial else False) and result["routine"]["needsReview"]
    assert len(desktop.sent) == 33 and result["steps"][-1]["step"] == 32
    assert not any(item["step"] > 32 for item in result["steps"])
    with pytest.raises(routines.RoutineError, match="inspect"):
        client.act(hwnd=7, routine="longue", params=values, then="none")
    assert len(desktop.sent) == 33


@pytest.fixture
def timed_desktop(desktop, monkeypatch):
    clock = SimpleNamespace(value=0.0)
    original_run = client._run_act_step

    def run(action, step, *args):
        result = original_run(action, step, *args)
        clock.value += 0.5
        return result

    monkeypatch.setattr(client.time, "perf_counter", lambda: clock.value)
    monkeypatch.setattr(client, "_run_act_step", run)
    monkeypatch.setattr(client, "get_window_state", lambda **kwargs: {"hwnd": 7, "elements": []})
    return desktop


@pytest.mark.parametrize("count", [34, 65])
def test_budget_pauses_long_learning_and_continues_only_unexecuted_suffix(timed_desktop, count):
    values = {f"v{i}": f"valeur privée {i}" for i in range(count)}
    result = client.act(
        long_steps(count), 7, params=values, remember="longue", staticUI=True, time_budget=15.75, then="none"
    )
    assert result["success"] is None and result["paused"]
    assert result["progress"] == {"completed": 32, "total": count, "nextStep": 32}
    assert result["routine"]["saved"] is False and routines.names("lab.exe") == []
    assert agent_guard.status_for(result) == "paused"
    assert len(timed_desktop.sent) == 32
    token = result["continuation"]
    with pytest.raises(routines.RoutineError, match="look frais"):
        client.act(hwnd=7, operation="continue", continuation=token, then="none")
    assert len(timed_desktop.sent) == 32
    while result.get("paused"):
        token, completed = result["continuation"], result["progress"]["completed"]
        client.look(7, capture_screenshot=False)
        result = client.act(hwnd=7, operation="continue", continuation=token, time_budget=15.75, then="none")
        assert result["steps"][0]["step"] == completed
        with pytest.raises(routines.RoutineError, match="Continuation"):
            client.act(hwnd=7, operation="continue", continuation=token, then="none")
    assert result["success"] is True and result["routine"]["saved"] is True
    assert result["routine"]["verifiedTransitions"] == count
    assert result["progress"] == {"completed": count, "total": count, "nextStep": None}
    assert len(timed_desktop.sent) == count
    assert [step["text"] for step in timed_desktop.sent] == list(values.values())
    assert len(routines.get("lab.exe", "longue")["anchors"]) == count
    assert routines._CONTINUATIONS == {}
    assert "valeur privée" not in (routines.ROOT / "lab.exe.json").read_text(encoding="utf-8")


def test_continuation_discards_permissions_and_accepts_only_current_global_authorizations(timed_desktop):
    request = long_steps(34)
    request[0].update(confirm=True, accord="ancien-execute")
    request[32].update(confirm=True, accord="ancien-futur")
    result = client.act(
        request,
        7,
        params={f"v{i}": str(i) for i in range(34)},
        remember="longue",
        staticUI=True,
        time_budget=15.75,
        then="none",
    )
    assert timed_desktop.sent[0]["accord"] == "ancien-execute"
    assert not any("accord" in step for step in next(iter(routines._CONTINUATIONS.values()))["executed"])
    client.look(7, capture_screenshot=False)
    result = client.act(
        hwnd=7,
        operation="continue",
        continuation=result["continuation"],
        then="none",
        authorizations={"32": {"confirm": True, "accord": "courant"}},
    )
    assert result["success"] is True
    assert timed_desktop.sent[32]["accord"] == "courant"
    assert "confirm" not in timed_desktop.sent[33] and "accord" not in timed_desktop.sent[33]
    assert "accord" not in (routines.ROOT / "lab.exe.json").read_text(encoding="utf-8")


@pytest.mark.parametrize("changed", ["fingerprint", "boundary"])
def test_continuation_revalidates_application_and_verified_boundary_before_input(timed_desktop, changed):
    result = client.act(
        long_steps(34),
        7,
        params={f"v{i}": str(i) for i in range(34)},
        remember="longue",
        staticUI=True,
        time_budget=15.75,
        then="none",
    )
    token = result["continuation"]
    if changed == "fingerprint":
        timed_desktop.app.write_bytes(b"nouvelle application")
    else:
        timed_desktop.state["value"] = "changement externe"
    client.look(7, capture_screenshot=False)
    with pytest.raises(routines.RoutineError, match="continuation"):
        client.act(hwnd=7, operation="continue", continuation=token, then="none")
    assert len(timed_desktop.sent) == 32 and routines.names("lab.exe") == []
    with pytest.raises(routines.RoutineError, match="Continuation"):
        client.act(hwnd=7, operation="continue", continuation=token, then="none")


def test_unknown_during_continuation_destroys_token_and_requires_named_inspection(desktop, monkeypatch):
    values = {f"v{i}": str(i) for i in range(65)}
    assert client.act(long_steps(65), 7, params=values, remember="longue", staticUI=True, then="none")["success"]
    desktop.sent.clear()
    desktop.state["value"] = ""
    clock = SimpleNamespace(value=0.0)
    original_run = client._run_act_step

    def run(action, step, *args):
        if len(desktop.sent) == 33:
            desktop.sent.append(copy.deepcopy(step))
            return {"success": None, "verified": None, "partial": True}
        result = original_run(action, step, *args)
        clock.value += 0.5
        return result

    monkeypatch.setattr(client.time, "perf_counter", lambda: clock.value)
    monkeypatch.setattr(client, "_run_act_step", run)
    monkeypatch.setattr(client, "get_window_state", lambda **kwargs: {"hwnd": 7, "elements": []})
    paused = client.act(hwnd=7, routine="longue", params=values, time_budget=15.75, then="none")
    assert paused["paused"] and not paused["routine"]["needsReview"]
    client.look(7, capture_screenshot=False)
    result = client.act(hwnd=7, operation="continue", continuation=paused["continuation"], then="none")
    assert result["success"] is None and not result.get("paused") and "continuation" not in result
    assert result["progress"] == {"completed": 33, "total": 65, "nextStep": 33}
    assert [item["step"] for item in result["steps"]] == [32, 33]
    assert len(desktop.sent) == 34 and result["routine"]["needsReview"]
    assert routines._CONTINUATIONS == {}
    with pytest.raises(routines.RoutineError, match="Continuation"):
        client.act(hwnd=7, operation="continue", continuation=paused["continuation"], then="none")
    with pytest.raises(routines.RoutineError, match="inspect"):
        client.act(hwnd=7, routine="longue", params=values, then="none")
    assert len(desktop.sent) == 34


@pytest.mark.parametrize("changed", ["pid", "process_start", "boundary_error", "authorization"])
def test_consumed_continuation_failure_marks_named_routine_for_review(timed_desktop, monkeypatch, changed):
    values = {f"v{i}": str(i) for i in range(34)}
    assert client.act(long_steps(34), 7, params=values, remember="longue", staticUI=True, then="none")["success"]
    timed_desktop.sent.clear()
    timed_desktop.state["value"] = ""
    result = client.act(hwnd=7, routine="longue", params=values, time_budget=15.75, then="none")
    token = result["continuation"]
    client.look(7, capture_screenshot=False)
    if changed == "pid":
        monkeypatch.setattr(client, "app_pid", lambda hwnd: 18)
    elif changed == "process_start":
        monkeypatch.setattr(client, "_routine_process_instance", lambda hwnd: (17, 43))
    elif changed == "boundary_error":
        monkeypatch.setattr(client, "check_expect", lambda *args: (_ for _ in ()).throw(RuntimeError("lecture perdue")))
    auth = {"32": {"confirm": "invalide"}} if changed == "authorization" else None
    with pytest.raises((routines.RoutineError, RuntimeError)):
        client.act(hwnd=7, operation="continue", continuation=token, authorizations=auth, then="none")
    assert len(timed_desktop.sent) == 32 and routines._CONTINUATIONS == {}
    assert routines.needs_inspection(routines.get("lab.exe", "longue"))


def test_continuation_is_bound_to_window_and_only_look_unlocks_it(timed_desktop, monkeypatch):
    result = client.act(
        long_steps(34),
        7,
        params={f"v{i}": str(i) for i in range(34)},
        remember="longue",
        staticUI=True,
        time_budget=15.75,
        then="none",
    )
    token = result["continuation"]
    routines.observed(8)
    with pytest.raises(routines.RoutineError, match="look frais"):
        client.act(hwnd=7, operation="continue", continuation=token, then="none")
    monkeypatch.setattr(client, "resolve_window", lambda hwnd: int(hwnd))
    with pytest.raises(routines.RoutineError, match="autre fenêtre"):
        client.act(hwnd=8, operation="continue", continuation=token, then="none")
    assert token in routines._CONTINUATIONS and len(timed_desktop.sent) == 32
    client.look(7, capture_screenshot=False)
    assert client.act(hwnd=7, operation="continue", continuation=token, then="none")["success"] is True


def test_mcp_reports_budget_pause_and_continuation_as_distinct_from_unknown(timed_desktop):
    agent_guard.GUARD.reset()
    result = json.loads(
        mcp_server.computer_act(
            long_steps(34),
            7,
            params={f"v{i}": str(i) for i in range(34)},
            remember="longue",
            staticUI=True,
            time_budget=15.75,
            then="none",
        )
    )
    assert result["status"] == "paused" and result["data"]["mark"] == "…"
    token = result["data"]["continuation"]
    refused = json.loads(mcp_server.computer_act(hwnd=7, operation="continue", continuation=token, then="none"))
    assert refused["status"] == "error" and "look frais" in refused["error"]
    mcp_server.computer_look(7, capture_screenshot=False, format="json")
    final = json.loads(mcp_server.computer_act(hwnd=7, operation="continue", continuation=token, then="none"))
    assert final["status"] == "success" and final["data"]["progress"]["completed"] == 34
    assert len(timed_desktop.sent) == 34


def test_process_instance_uses_pid_and_native_creation_time_without_window_input(monkeypatch):
    monkeypatch.setattr(client, "app_pid", lambda hwnd: os.getpid())
    first = client._routine_process_instance(7)
    assert first[0] == os.getpid() and first[1] > 0
    assert client._routine_process_instance(7) == first


def test_global_cache_guard_refuses_concurrent_actions_looks_and_inspection_before_input(desktop, monkeypatch):
    boundary, release = threading.Barrier(2), threading.Event()

    def paused_run(action, step, *args):
        boundary.wait(timeout=5)
        if not release.wait(timeout=5):
            raise RuntimeError("Le test n'a pas libéré l'action.")
        return desktop.native_run(action, step, *args)

    def collect(text, element_index, **kwargs):
        chosen = client._element_info[element_index]["name"]
        desktop.sent.append({"target": chosen, "text": text})
        desktop.state["value"] = text
        return {"success": True, "verified": True}

    monkeypatch.setattr(client, "_run_act_step", paused_run)
    monkeypatch.setattr(client, "type_text", collect)  # collecte seule : aucun input Windows
    with ThreadPoolExecutor(max_workers=1) as pool:
        first = pool.submit(
            client.act, [steps()[0]], 7, params={"nom": "A"}, remember="premiere", staticUI=True, then="none"
        )
        try:
            boundary.wait(timeout=5)  # la cible Nom est publiée dans cache1, avant son entrée réelle
            contenders = (
                lambda: client.act([steps()[1]], 7, params={}, remember="seconde", staticUI=True, then="none"),
                lambda: client.look(7, capture_screenshot=False),
                lambda: client.get_window_state(hwnd=7, capture_screenshot=False),
                lambda: client.act(hwnd=7, operation="inspect", routine="premiere"),
                lambda: client.click(hwnd=7, element_index=1),
                lambda: client.press_key("enter", hwnd=7),
            )
            for attempt in contenders:
                with pytest.raises(routines.RoutineError, match="concurrent refusé"):
                    attempt()
            assert client.act(hwnd=7, operation="list")["routines"] == []
            prevented = json.loads(
                mcp_server._computer_action(
                    "test", 7, {}, False, lambda hwnd: desktop.sent.append({"target": "AUTRE"}), image=False
                )
            )
            assert prevented["status"] == "error" and "concurrent refusé" in prevented["error"]
            assert desktop.sent == []
        finally:
            release.set()
        assert first.result(timeout=5)["success"] is True
    assert desktop.sent == [{"target": "Nom", "text": "A"}]


def test_emergency_stop_destroys_continuation_before_it_can_be_reauthorized(timed_desktop, monkeypatch):
    result = client.act(
        long_steps(34),
        7,
        params={f"v{i}": str(i) for i in range(34)},
        remember="longue",
        staticUI=True,
        time_budget=15.75,
        then="none",
    )
    token = result["continuation"]
    client.look(7, capture_screenshot=False)
    check = client.emergency_stop.check

    def stopped():
        raise client.emergency_stop.EmergencyStopError("Arrêt d'urgence demandé par Julien")

    monkeypatch.setattr(client.emergency_stop, "check", stopped)
    with pytest.raises(client.emergency_stop.EmergencyStopError):
        client.act(hwnd=7, operation="continue", continuation=token, then="none")
    monkeypatch.setattr(client.emergency_stop, "check", check)
    client.look(7, capture_screenshot=False)
    with pytest.raises(routines.RoutineError, match="Continuation"):
        client.act(hwnd=7, operation="continue", continuation=token, then="none")
    assert len(timed_desktop.sent) == 32 and routines._CONTINUATIONS == {}


def test_long_preparation_still_makes_one_verified_transition_before_budget_pause(desktop, monkeypatch):
    ticks = iter([0.0, 2.0])
    monkeypatch.setattr(client.time, "perf_counter", lambda: next(ticks, 2.0))
    result = client.act(steps(), 7, params={"nom": "A"}, remember="profil", staticUI=True, time_budget=1.0, then="none")
    assert result["success"] is None and result["steps"][0]["status"] == "ok"
    assert result["paused"] and result["continuation"] in routines._CONTINUATIONS
    assert result["progress"] == {"completed": 1, "total": 2, "nextStep": 1}
    assert not result["routine"]["saved"] and len(desktop.sent) == 1


@pytest.mark.parametrize("budget", [0, -1, 31, float("nan"), float("inf"), True])
def test_invalid_call_duration_is_refused_before_input(desktop, budget):
    with pytest.raises(ValueError, match="time_budget"):
        client.act(steps(), 7, params={"nom": "A"}, remember="profil", staticUI=True, time_budget=budget, then="none")
    assert desktop.sent == []


def test_real_batch_learns_and_replays_with_new_text(desktop):
    out = learn()
    assert out["routine"] == {"name": "profil", "saved": True, "coverage": "verified", "verifiedTransitions": 2}
    entry = routines.get("lab.exe", "profil")
    assert entry["steps"] == steps()
    assert "Alice privée" not in (routines.ROOT / "lab.exe.json").read_text(encoding="utf-8")
    assert "Titre privé" not in json.dumps(entry)
    assert not any("element_index" in step for step in entry["steps"])
    desktop.state["toggle"] = 0
    replay = client.act(hwnd=7, routine="profil", params={"nom": "Bob privé"}, then="none")
    assert replay["success"] is True and desktop.state["value"] == "Bob privé"
    assert len(desktop.sent) == 4


def test_all_already_is_saved_without_invented_transition(desktop):
    desktop.state.update(value="Alice privée", toggle=1)
    out = learn()
    assert out["routine"]["coverage"] == "not-exercised" and out["routine"]["verifiedTransitions"] == 0
    assert desktop.sent == []
    assert routines.get("lab.exe", "profil")["last_ok"] is False


def test_intrinsic_verified_effect_can_be_learned(desktop):
    out = client.act(
        [{"action": "set", "target": "Nom", "text": "{{nom}}"}],
        7,
        params={"nom": "A"},
        remember="intrinseque",
        staticUI=True,
        then="none",
    )
    assert out["routine"]["saved"] and out["routine"]["verifiedTransitions"] == 1


def test_unknown_stops_before_next_step_and_requires_inspection(desktop, monkeypatch):
    learn()
    desktop.sent.clear()
    desktop.state.update(value="", toggle=0)

    def lost(action, step, *args):
        desktop.sent.append(step)
        return {"success": None, "verified": None, "partial": True}

    monkeypatch.setattr(client, "_run_act_step", lost)
    out = client.act(hwnd=7, routine="profil", params={"nom": "B"}, then="none")
    assert out["success"] is None and len(desktop.sent) == 1
    assert out["routine"]["needsReview"]
    with pytest.raises(routines.RoutineError, match="inspect"):
        client.act(hwnd=7, routine="profil", params={"nom": "B"}, then="none")
    assert len(desktop.sent) == 1
    inspected = client.act(hwnd=7, operation="inspect", routine="profil")
    assert inspected["inspected"] and len(desktop.sent) == 1
    client.act(hwnd=7, routine="profil", params={"nom": "B"}, then="none")
    assert len(desktop.sent) == 2


def test_unknown_batch_is_never_learned(desktop, monkeypatch):
    monkeypatch.setattr(client, "_run_act_step", lambda *args: {"success": None, "verified": None, "partial": True})
    result = learn()
    assert not result["routine"]["saved"] and routines.names("lab.exe") == []


@pytest.mark.parametrize("action", ["set", "click"])
@pytest.mark.parametrize("outcome", ["partial", "unknown", "message_unknown", "pending"])
def test_real_wrappers_preserve_terminal_input_even_when_requested_state_became_true(
    desktop, monkeypatch, action, outcome
):
    """Vrais act/_run_act_step/type_text ou click/_run_chain : seul le fournisseur d'entrée est simulé."""
    monkeypatch.setattr(client, "_run_act_step", desktop.native_run)
    monkeypatch.setattr(client, "get_cached_element", lambda index, hwnd: SimpleNamespace())
    monkeypatch.setattr(client, "_read_state", lambda ctrl, info: {"value": desktop.state["value"]})
    monkeypatch.setattr(client, "is_electron_window", lambda hwnd: False)
    monkeypatch.setattr(client.win32_controls, "has_cef_content", lambda hwnd: False)
    monkeypatch.setattr(client, "_background_writable", lambda *args: False)
    monkeypatch.setattr(client, "_restore_if_stolen", lambda *args: False)
    monkeypatch.setattr(client, "_physical_gate", lambda *args: pytest.fail("aucun repli après issue inconnue"))
    errors = {
        "partial": client.win32_controls.PartialSendError,
        "unknown": client.InputOutcomeUnknown,
        "message_unknown": client.win32_controls.MessageOutcomeUnknown,
    }

    def input_provider():
        desktop.sent.append({"action": action})
        desktop.state.update(value="A", toggle=1)
        if outcome == "pending":
            return "pending"
        raise errors[outcome]("entrée commencée, réponse perdue")

    chain = [
        ("UIA Value" if action == "set" else "UIA Invoke", input_provider),
        ("autre technique", lambda: pytest.fail("aucune technique après entrée partielle")),
    ]
    monkeypatch.setattr(client, "_text_plan", lambda *args: ("WinForms", chain, lambda _: (True, "état atteint"), "A"))
    monkeypatch.setattr(client, "_click_plan", lambda *args: ("WinForms", chain, lambda _: (True, "état atteint")))
    first = (
        steps()[0]
        if action == "set"
        else {"action": "click", "target": "Actif", "expect": {"element": "Actif", "checked": True}}
    )
    result = client.act([first, steps()[0]], 7, params={"nom": "A"}, remember="incertaine", staticUI=True, then="none")
    assert desktop.state["value"] == "A" and desktop.state["toggle"] == 1
    assert desktop.sent == [{"action": action}]
    assert result["success"] is None and [item["status"] for item in result["steps"]] == ["unverified"]
    terminal = result["steps"][0]["result"]
    assert terminal["input_outcome"] == "unknown" and terminal["pending"] is True
    assert terminal["expect"]["not_evaluated"] == "input_outcome_unknown"
    assert terminal["input_error_type"] == ("InputOutcomeUnknown" if outcome == "pending" else errors[outcome].__name__)
    assert not result["routine"]["saved"] and "continuation" not in result and not result.get("paused")
    assert routines.names("lab.exe") == []


def test_completed_input_without_visible_effect_can_still_be_proven_by_exact_postcondition(desktop, monkeypatch):
    monkeypatch.setattr(client, "_restore_if_stolen", lambda *args: False)

    def run(action, step, *args):
        def provider():
            desktop.sent.append(copy.deepcopy(step))
            desktop.state["value"] = step["text"]

        out = client._run_chain([("entrée complète", provider)], lambda _: (None, "pas d'effet visuel"), 0)
        return {"success": None, **out}

    monkeypatch.setattr(client, "_run_act_step", run)
    result = client.act(long_steps(2), 7, params={"v0": "A", "v1": "B"}, remember="tardive", staticUI=True, then="none")
    assert result["success"] is True and result["routine"]["saved"]
    assert len(desktop.sent) == 2 and all(item["status"] == "ok" for item in result["steps"])


def test_update_is_announced_and_compatible_targets_still_run(desktop):
    learn()
    desktop.app.write_bytes(b"application mise a jour")
    result = client.act(hwnd=7, routine="profil", params={"nom": "B"}, then="none")
    assert result["success"] is True and result["routine"]["fingerprintChanged"] is True


def test_changed_context_refuses_before_input(desktop, monkeypatch):
    learn()
    desktop.sent.clear()

    def moved(*args, **kwargs):
        tree = desktop.tree()
        tree.nodes[1].props["class_name"] = "AutreEditeur"
        return tree

    monkeypatch.setattr(client, "_window_tree", moved)
    out = client.act(hwnd=7, routine="profil", params={"nom": "B"}, then="none")
    assert out["success"] is False and out["routine"]["needsReview"] and not desktop.sent


def test_full_tree_required_before_every_step(desktop, monkeypatch):
    reads = []

    def cut(*args, **kwargs):
        reads.append(True)
        tree = desktop.tree()
        tree.status = "truncated"
        return tree

    monkeypatch.setattr(client, "_window_tree", cut)
    result = learn()
    assert result["success"] is False and desktop.sent == [] and not result["routine"]["saved"]
    assert reads


@pytest.mark.parametrize("params", [{}, {"nom": "B", "extra": "C"}, {"nom": 4}])
def test_parameters_are_checked_before_input(desktop, params):
    with pytest.raises(routines.RoutineError):
        client.act(steps(), 7, params=params, remember="profil", staticUI=True, then="none")
    assert not desktop.sent


@pytest.mark.parametrize(
    "step",
    [
        {"action": "click", "target": "{{cible}}"},
        {"action": "click", "target": "#2"},
        {"action": "click", "target": "@20,30"},
        {"action": "click", "target": "Nom", "element_index": 1},
        {"action": "key", "key": "{{touche}}"},
        {"action": "menu", "path": "Fichier > {{commande}}"},
        {"action": "select", "target": "Nom", "option": "{{option}}"},
        {"action": "set", "target": "Nom", "text": "donnée privée"},
        {"action": "click", "target": "Nom", "value": "PRIVATE_UNUSED_TEXT"},
        {"action": "key", "key": "f5", "text": "PRIVATE_UNUSED_TEXT"},
        {"action": "set", "target": "Nom", "text": "{{nom}}", "expect": {"target": "{{cible}}", "exists": True}},
    ],
)
def test_nonportable_steps_are_refused_without_execution(desktop, step):
    with pytest.raises(routines.RoutineError):
        client.act([step], 7, remember="invalide", staticUI=True, then="none")
    assert not desktop.sent


def test_no_persistent_authorization_and_current_call_only(desktop):
    request = steps()
    request[0].update(confirm=True, accord="jeton-de-cet-appel")
    client.act(request, 7, params={"nom": "A"}, remember="profil", staticUI=True, then="none")
    assert desktop.sent[0]["confirm"] and desktop.sent[0]["accord"] == "jeton-de-cet-appel"
    disk = (routines.ROOT / "lab.exe.json").read_text(encoding="utf-8")
    assert "confirm" not in disk and "accord" not in disk
    desktop.sent.clear()
    client.act(hwnd=7, routine="profil", params={"nom": "B"}, then="none")
    assert "confirm" not in desktop.sent[0] and "accord" not in desktop.sent[0]
    desktop.sent.clear()
    client.act(
        hwnd=7,
        routine="profil",
        params={"nom": "C"},
        then="none",
        authorizations={"0": {"confirm": True, "accord": "jeton-actuel"}},
    )
    assert desktop.sent[0]["accord"] == "jeton-actuel"
    assert "jeton-actuel" not in (routines.ROOT / "lab.exe.json").read_text(encoding="utf-8")


def test_mirror_is_definition_only_import_requires_inspection_and_forget_syncs(desktop, monkeypatch, tmp_path):
    mirror = tmp_path / "miroir"
    monkeypatch.setattr(routines, "MIRROR", mirror)
    learn()
    remote = json.loads((mirror / "lab.exe.json").read_text(encoding="utf-8"))
    exported = remote["routines"]["profil"]
    assert not {"runs", "last_ok", "needs_review", "reviewed"} & set(exported)
    assert not {"path", "window", "title"} & set(exported["fingerprint"])
    assert "Alice privée" not in json.dumps(remote) and "Titre privé" not in json.dumps(remote)
    monkeypatch.setattr(routines, "ROOT", tmp_path / "autre-pc")
    routines.sync_app("lab.exe")
    assert routines.needs_inspection(routines.get("lab.exe", "profil"))
    assert client.act(hwnd=7, operation="forget", routine="profil")["forgotten"]
    monkeypatch.setattr(routines, "ROOT", tmp_path / "troisieme-pc")
    routines.sync_app("lab.exe")
    assert routines.names("lab.exe") == []
    assert "profil" in json.loads((mirror / "lab.exe.json").read_text(encoding="utf-8"))["forgotten"]


def test_private_expected_literal_stays_local(desktop, monkeypatch, tmp_path):
    monkeypatch.setattr(routines, "MIRROR", tmp_path / "mirror")
    request = steps()
    request[0]["expect"]["value"] = "Alice privée"
    client.act(request, 7, params={"nom": "Alice privée"}, remember="local", staticUI=True, then="none")
    assert routines.names("lab.exe") == ["local"]
    remote = json.loads((tmp_path / "mirror" / "lab.exe.json").read_text(encoding="utf-8"))
    assert remote["routines"] == {} and "Alice privée" not in json.dumps(remote)


def test_automation_id_never_exports_dynamic_private_name(desktop, monkeypatch, tmp_path):
    monkeypatch.setattr(routines, "MIRROR", tmp_path / "mirror")
    private_name = ["PRIVATE_DOCUMENT_42"]

    def dynamic(*args, **kwargs):
        tree = desktop.tree()
        tree.nodes[1].props["automation_id"] = "stable-editor"
        tree.nodes[1].name = private_name[0]
        return tree

    monkeypatch.setattr(client, "_window_tree", dynamic)
    request = [{"action": "set", "target": "stable-editor", "text": "{{nom}}"}]
    result = client.act(request, 7, params={"nom": "A"}, remember="editeur", staticUI=True, then="none")
    assert result["routine"]["saved"]
    mirror = (tmp_path / "mirror" / "lab.exe.json").read_text(encoding="utf-8")
    assert "PRIVATE_DOCUMENT_42" not in mirror
    private_name[0] = "PRIVATE_DOCUMENT_43"
    assert client.act(hwnd=7, routine="editeur", params={"nom": "B"}, then="none")["success"] is True


def test_named_document_target_stays_local(desktop, monkeypatch, tmp_path):
    monkeypatch.setattr(routines, "MIRROR", tmp_path / "mirror")

    def document(*args, **kwargs):
        tree = desktop.tree()
        tree.nodes[1].control_type = "Document"
        tree.nodes[1].name = "Contrat privé.docx"
        tree.nodes[1].props["automation_id"] = "document-editor"
        return tree

    monkeypatch.setattr(client, "_window_tree", document)
    result = client.act(
        [{"action": "click", "target": "Contrat privé.docx"}], 7, remember="document", staticUI=True, then="none"
    )
    assert result["routine"]["saved"] and routines.names("lab.exe") == ["document"]
    text = (tmp_path / "mirror" / "lab.exe.json").read_text(encoding="utf-8")
    assert "Contrat privé.docx" not in text and json.loads(text)["routines"] == {}


def test_corrupted_file_is_not_overwritten(desktop):
    path = routines.ROOT / "lab.exe.json"
    path.parent.mkdir(parents=True)
    path.write_text("invalid", encoding="utf-8")
    with pytest.raises(routines.RoutineError):
        learn()
    assert path.read_text(encoding="utf-8") == "invalid" and not desktop.sent


def test_unknown_write_failure_cannot_restore_old_success(desktop, monkeypatch):
    learn()
    original = (routines.ROOT / "lab.exe.json").read_text(encoding="utf-8")
    desktop.state["value"] = ""
    monkeypatch.setattr(client, "_run_act_step", lambda *args: {"success": None, "verified": None, "partial": True})

    def denied(*args):
        raise OSError("disque indisponible")

    write = routines._write
    monkeypatch.setattr(routines, "_write", denied)
    result = client.act(hwnd=7, routine="profil", params={"nom": "B"}, then="none")
    assert result["success"] is None and result["routine"]["needsReview"] and result["routine"]["storageError"]
    monkeypatch.setattr(routines, "_write", write)
    (routines.ROOT / "lab.exe.json").write_text(original, encoding="utf-8")
    with pytest.raises(routines.RoutineError, match="inspect"):
        client.act(hwnd=7, routine="profil", params={"nom": "B"}, then="none")
    assert client.act(hwnd=7, operation="inspect", routine="profil")["inspected"]


def test_equal_timestamp_forget_wins_and_never_resurrects(desktop, monkeypatch, tmp_path):
    monkeypatch.setattr(routines, "MIRROR", tmp_path / "mirror")
    monkeypatch.setattr(routines, "_stamp", lambda: "00000000000000000042")
    learn()
    first = routines.ROOT
    monkeypatch.setattr(routines, "ROOT", tmp_path / "second")
    routines.sync_app("lab.exe")
    routines.forget("lab.exe", "profil")
    for _ in range(4):
        monkeypatch.setattr(routines, "ROOT", first)
        routines.sync_app("lab.exe")
        assert routines.names("lab.exe") == []
        monkeypatch.setattr(routines, "ROOT", tmp_path / "second")
        routines.sync_app("lab.exe")
        assert routines.names("lab.exe") == []


def test_postcondition_partial_label_cannot_qualify_procedure(desktop, monkeypatch):
    changed = [False]

    def tree(*args, **kwargs):
        current = desktop.tree()
        current.nodes[1].props["automation_id"] = "stable-editor"
        if changed[0]:
            current.nodes[1].name = "Nom secondaire"
        return current

    def sent(action, step, *args):
        desktop.state["value"] = step["text"]
        changed[0] = True
        return {"success": True, "verified": True}

    monkeypatch.setattr(client, "_window_tree", tree)
    monkeypatch.setattr(client, "_run_act_step", sent)
    result = learn()
    assert result["success"] is None and not result["routine"]["saved"]
    assert len(result["steps"]) == 1


def test_mcp_real_surface_manages_without_consuming_action_guard(desktop, monkeypatch):
    learn()
    monkeypatch.setattr(mcp_server.agent_guard.GUARD, "check", lambda *args: pytest.fail("gestion sans entrée"))
    out = json.loads(mcp_server.computer_act(hwnd=7, operation="list"))
    assert out["status"] == "success" and out["data"]["routines"][0]["name"] == "profil"
    out = json.loads(mcp_server.computer_act(hwnd=7, operation="inspect", routine="profil"))
    assert out["data"]["inspected"]
    out = json.loads(mcp_server.computer_act(hwnd=7, operation="forget", routine="profil"))
    assert out["data"]["forgotten"]


@pytest.mark.parametrize("key,expected", [("expanded", False), ("focused", False)])
def test_expect_state_is_never_ignored(desktop, key, expected):
    target = "Actif" if key == "expanded" else "Nom"
    expect = {"element": target, "exists": True, key: expected}
    assert client.check_expect(7, expect)[0] is True
    assert client._exact_postcondition(expect)
    desktop.state["expanded" if key == "expanded" else "focus"] = 1 if key == "expanded" else True
    assert client.check_expect(7, expect)[0] is False


@pytest.mark.parametrize("key", ["expanded", "focused"])
def test_unavailable_state_is_unknown_not_already(desktop, monkeypatch, key):
    def unavailable(*args, **kwargs):
        tree = desktop.tree()
        for node in tree.nodes:
            node.props.pop("expand", None)
            node.props.pop("focus", None)
        return tree

    monkeypatch.setattr(client, "_window_tree", unavailable)
    assert client.check_expect(7, {"element": "Actif", "exists": True, key: False})[0] is None


@pytest.mark.parametrize(
    "expect",
    [
        {"element": "Nom", "exists": True, "focussed": False},
        {"element": "Nom", "expanded": 0},
        {"focused": False},
        {"element": "Nom", "timeout": -1},
        {"element": "Nom"},
    ],
)
def test_invalid_expect_is_refused_before_input(desktop, expect):
    result = client.act([{"action": "click", "target": "Nom", "expect": expect}], 7, then="none")
    assert result["success"] is False and not desktop.sent


def test_enabled_control_is_not_proof_that_click_already_happened(desktop):
    result = client.act(
        [{"action": "click", "target": "Nom", "expect": {"element": "Nom", "enabled": True}}], 7, then="none"
    )
    assert len(desktop.sent) == 1 and not result["steps"][0].get("already")


@pytest.fixture
def labeled_combo(desktop, monkeypatch):
    """Même nom exact pour l'étiquette Text et sa ComboBox, comme le vrai labo WinForms Couleur."""
    desktop.state["color"] = "Rouge"

    def node(kind, name, parent, automation_id, **props):
        return SimpleNamespace(
            control_type=kind,
            name=name,
            parent=parent,
            framework="WinForms",
            host_hwnd=7,
            element=object(),
            props={"automation_id": automation_id, "class_name": kind, **props},
        )

    def tree(*args, **kwargs):
        current = desktop.tree()
        current.nodes.extend(
            [
                node("Text", "Couleur", 0, "color-label"),
                node("ComboBox", "Couleur", 0, "color-selector", value=desktop.state["color"], has_value=True),
            ]
        )
        return current

    base_run = client._run_act_step

    def run(action, step, *args):
        if action == "select":
            assert client._element_info[step["element_index"]]["type"] == "ComboBox"
            desktop.sent.append(copy.deepcopy(step))
            desktop.state["color"] = step["option"]
            return {"success": True, "verified": True}
        return base_run(action, step, *args)

    monkeypatch.setattr(client, "_window_tree", tree)
    monkeypatch.setattr(client, "_run_act_step", run)
    return SimpleNamespace(desktop=desktop, tree=tree, node=node)


def test_routine_selects_combo_instead_of_same_named_static_label(labeled_combo):
    request = [
        *steps(),
        {"action": "select", "target": "Couleur", "option": "Bleu", "expect": {"element": "Couleur", "value": "Bleu"}},
    ]
    result = client.act(request, 7, params={"nom": "A"}, remember="couleur", staticUI=True, then="none")
    assert result["success"] is True and result["routine"]["saved"] and result["routine"]["verifiedTransitions"] == 3
    entry = routines.get("lab.exe", "couleur")
    assert entry["anchors"][2]["target"]["type"] == "ComboBox"
    assert entry["anchors"][2]["expect"]["type"] == "ComboBox"
    assert entry["anchors"][2]["post"]["type"] == "ComboBox"
    labeled_combo.desktop.state["color"] = "Rouge"
    replay = client.act(hwnd=7, routine="couleur", params={"nom": "B"}, then="none")
    assert replay["success"] is True and labeled_combo.desktop.state["color"] == "Bleu"
    assert replay["steps"][2]["verified"] is True and not replay["steps"][2].get("already")
    already = client.act(hwnd=7, routine="couleur", params={"nom": "B"}, then="none")
    assert already["success"] is True and all(item.get("already") for item in already["steps"])


def test_routine_still_refuses_two_real_same_named_combos_before_input(labeled_combo, monkeypatch):
    def duplicate(*args, **kwargs):
        tree = labeled_combo.tree()
        tree.nodes.append(labeled_combo.node("ComboBox", "Couleur", 0, "other-color", value="Rouge"))
        return tree

    monkeypatch.setattr(client, "_window_tree", duplicate)
    result = client.act(
        [{"action": "select", "target": "Couleur", "option": "Bleu"}], 7, remember="ambigu", staticUI=True, then="none"
    )
    assert result["success"] is False and not result["routine"]["saved"] and not labeled_combo.desktop.sent


def test_routine_never_resolves_partial_combo_label(labeled_combo, monkeypatch):
    def partial(*args, **kwargs):
        tree = labeled_combo.tree()
        tree.nodes[-2].name = "Couleur secondaire"
        tree.nodes[-1].name = "Couleur secondaire"
        return tree

    monkeypatch.setattr(client, "_window_tree", partial)
    result = client.act(
        [{"action": "select", "target": "Couleur", "option": "Bleu"}], 7, remember="partiel", staticUI=True, then="none"
    )
    assert result["success"] is False and not labeled_combo.desktop.sent


def test_routine_typing_resolves_only_edit_of_same_named_parent_combo(labeled_combo):
    tree = labeled_combo.tree()
    tree.nodes.append(labeled_combo.node("Edit", "Couleur", len(tree.nodes) - 1, "color-edit", value="Rouge"))
    target = client._routine_target(tree, "Couleur", roles=client.ACTION_ROLES["set"])
    assert target is tree.nodes[-1]
    nodes, match = client._find_nodes(tree, "Couleur", client._expect_roles({"value": "Rouge"}))
    assert nodes == [tree.nodes[-1]] and match == "exact"


def test_expect_roles_resolve_checkbox_state_without_static_text_ambiguity(desktop):
    tree = desktop.tree()
    static = copy.copy(tree.nodes[2])
    static.control_type = "Text"
    static.props = {"automation_id": "label-actif"}
    tree.nodes.append(static)
    # Deux noms identiques, mais des identifiants différents : le rôle attendu départage la coche.
    tree.nodes[2].props["automation_id"] = "checkbox-actif"
    nodes, match = client._find_nodes(tree, "Actif", client._expect_roles({"checked": True}))
    assert nodes == [tree.nodes[2]] and match == "exact"
