"""Procédures longues et continuations sans fenêtre ni entrée native."""

from __future__ import annotations

import asyncio
import concurrent.futures
import copy
import json
import threading
from types import SimpleNamespace

import pytest
from test_contract import HWND, FakeAuto, as_call, executor, make_view, node

from nexus_windows import actions, long_workflows, methods, routines, server, worker

APP, FP = "long-fixture.exe", {"path": "C:/fixture.exe", "size": 10, "mtime": 20}


@pytest.fixture(autouse=True)
def isolated(monkeypatch, tmp_path):
    broker = long_workflows.Continuations(server._workflow_failed)
    monkeypatch.setattr(server, "CONTINUATIONS", broker)
    monkeypatch.setattr(routines, "ROOT", tmp_path / "routines")
    monkeypatch.setattr(routines, "MIRROR", None)
    monkeypatch.setattr(server.w, "IsWindow", lambda hwnd: True)
    monkeypatch.setattr(server.worker, "check_cancelled", lambda: None)
    yield
    broker.discard()


def plan(count=65):
    return [
        server.Step(
            do="check" if index % 2 == 0 else "uncheck",
            target="Option",
            check=server.Check(target="Option", checked=index % 2 == 0),
        )
        for index in range(count)
    ]


def state(steps):
    return {
        "hwnd": HWND,
        "app": APP,
        "fingerprint": FP,
        "routine": None,
        "entry": None,
        "remember": "long",
        "template": [step.model_dump() for step in steps],
        "completed": 0,
        "report": [],
        "symbols": [],
    }


def setup(monkeypatch):
    box = node("CheckBox", "Option", has_toggle=True, toggle=0)
    auto = FakeAuto()
    ex = executor(
        make_view(box, node("Edit", "Nom", has_value=True, value=""), node("Edit", "Notes", has_value=True, value="")),
        auto,
    )
    clock = [0.0]
    dispatch = server._dispatch

    def timed(current, step):
        result = dispatch(current, step)
        clock[0] += 0.5
        return result

    monkeypatch.setattr(server.time, "monotonic", lambda: clock[0])
    monkeypatch.setattr(server, "_dispatch", timed)
    monkeypatch.setattr(server, "_workflow_identity", lambda hwnd: (hwnd, 10, 100, 10, 100))
    monkeypatch.setattr(server, "_workflow_structure", lambda view: "structure")
    return ex, auto, clock


@pytest.mark.parametrize("count", [34, 65])
def test_long_definition_real_simulated_transitions_pause_and_global_completion(monkeypatch, count):
    ex, auto, _ = setup(monkeypatch)
    steps = plan(count)
    current = state(steps)
    starts, controls = [], []
    while True:
        starts.append(len(auto.calls))
        _, failed, control = server._run_workflow(ex, steps, HWND, as_call(), current, 15.75)
        assert not failed
        controls.append(control)
        if control["status"] == "completed":
            break
        assert control["progress"]["completed"] == len(auto.calls)
        assert control["progress"]["nextStep"] == len(auto.calls)
        token = control["continuation"]
        server.CONTINUATIONS.observed(HWND)
        current = server.CONTINUATIONS.take(token)
        server._workflow_revalidate(ex, current, FP)
        steps = [server.Step.model_validate(step) for step in current["steps"]]
    assert starts == ([0, 32] if count == 34 else [0, 32, 64])
    assert len(auto.calls) == count and current["symbols"] == ["✓"] * count
    assert controls[-1] == {"status": "completed", "progress": {"completed": count, "total": count, "nextStep": None}}
    assert [line.split(".", 1)[0] for line in current["report"]] == [str(i) for i in range(1, count + 1)]
    assert "saved for" in server._remember(APP, FP, "Fixture", "long", plan(count), current["symbols"], False)
    info = routines.inspect(APP, "long", FP)
    assert len(info["steps"]) == count and info["verifiedTransitions"] == count


def test_no_partial_learning_and_no_authorizations_retained(monkeypatch):
    ex, _, _ = setup(monkeypatch)
    steps = plan(65)
    steps[-1] = steps[-1].model_copy(update={"confirm": True, "accord": "secret-token"})
    current = state(steps)
    _, _, control = server._run_workflow(ex, steps, HWND, as_call(), current, 15.75)
    assert "NOT saved" in server._remember(APP, FP, "Fixture", "long", steps, current["symbols"], False)
    server.CONTINUATIONS.observed(HWND)
    resumed = server.CONTINUATIONS.take(control["continuation"])
    assert "secret-token" not in json.dumps(resumed) and '"confirm"' not in json.dumps(resumed)
    todo = server._workflow_authorizations([server.Step.model_validate(item) for item in resumed["steps"]], None, 32)
    assert not todo[-1].confirm and todo[-1].accord is None
    todo = server._workflow_authorizations(todo, {"64": {"confirm": True, "accord": "current"}}, 32)
    assert todo[-1].confirm and todo[-1].accord == "current"
    with pytest.raises(actions.ActionError, match="remaining global"):
        server._workflow_authorizations(todo, {"0": {"confirm": True}}, 32)


def test_token_single_use_requires_fresh_look_and_mutation_cannot_change_saved_state():
    failures = []
    broker = long_workflows.Continuations(failures.append)
    data = {"hwnd": HWND, "steps": [{"text": "Alice", "confirm": True, "accord": "old"}]}
    token = broker.put(data)
    data["steps"][0]["text"] = "Bob"
    with pytest.raises(ValueError, match="fresh successful look"):
        broker.take(token)
    assert len(failures) == 1
    broker.observed(HWND)
    with pytest.raises(ValueError, match="absent"):
        broker.take(token)
    token = broker.put(data)
    broker.observed(HWND)
    saved = broker.take(token)
    assert saved["steps"] == [{"text": "Bob"}]
    with pytest.raises(ValueError, match="absent"):
        broker.take(token)


def test_expiry_removes_state_and_requires_review_without_any_next_input():
    failed = threading.Event()
    broker = long_workflows.Continuations(lambda state: failed.set(), lifetime=0.01)
    token = broker.put({"hwnd": HWND})
    assert failed.wait(1)
    with pytest.raises(ValueError, match="expired"):
        broker.take(token)


@pytest.mark.parametrize("change", ["instance", "fingerprint", "structure", "boundary"])
def test_changed_boundary_or_window_context_refuses_before_suffix(monkeypatch, change):
    ex, auto, _ = setup(monkeypatch)
    steps, current = plan(), state(plan())
    _, _, control = server._run_workflow(ex, steps, HWND, as_call(), current, 15.75)
    server.CONTINUATIONS.observed(HWND)
    resumed = server.CONTINUATIONS.take(control["continuation"])
    if change == "instance":
        monkeypatch.setattr(server, "_workflow_identity", lambda hwnd: (hwnd, 10, 999, 10, 999))
    elif change == "fingerprint":
        resumed["fingerprint"] = FP | {"size": 99}
    elif change == "structure":
        resumed["structure"] = "different"
    else:
        resumed["boundary"] = {"check": {"target": "Option", "checked": True}}
    with pytest.raises(actions.ActionError, match="Continuation"):
        server._workflow_revalidate(ex, resumed, FP)
    assert len(auto.calls) == 32


def test_unknown_at_33_is_terminal_no_following_input_or_continuation(monkeypatch):
    ex, auto, _ = setup(monkeypatch)
    original = server._dispatch

    def unknown(current, step):
        if len(auto.calls) == 32:
            return actions.Outcome(True, "sent; provider lost response", "fixture")
        return original(current, step)

    monkeypatch.setattr(server, "_dispatch", unknown)
    steps = plan()
    steps[32] = server.Step(do="hover", target="Option")
    _, _, control = server._run_workflow(ex, steps, HWND, as_call(), state(steps), 30)
    assert control["status"] == "stopped" and control["progress"]["completed"] == 32
    assert len(auto.calls) == 32 and "continuation" not in control


def test_full_plan_prevalidated_before_any_input(monkeypatch):
    _, auto, _ = setup(monkeypatch)
    steps = plan()
    steps[64] = server.Step(do="drag", target="Option")
    with pytest.raises(actions.ActionError, match="Step 64"):
        server._workflow_prevalidate(steps)
    assert not auto.calls


def test_abandoned_worker_never_recreates_continuation(monkeypatch):
    ex, auto, _ = setup(monkeypatch)

    def stopped():
        if len(auto.calls) >= 32:
            raise worker.Cancelled("abandoned")

    monkeypatch.setattr(server.worker, "check_cancelled", stopped)
    with pytest.raises(worker.Cancelled):
        server._run_workflow(ex, plan(), HWND, as_call(), state(plan()), 15.75)
    with pytest.raises(ValueError, match="absent"):
        server.CONTINUATIONS.take("any")
    assert len(auto.calls) == 32


def test_failure_review_is_independent_of_failed_disk_write(monkeypatch):
    routines.remember(APP, "long", [{"do": "check", "target": "Option"}], FP, "Fixture")
    entry = routines.get(APP, "long")
    current = {"app": APP, "routine": "long", "entry": copy.deepcopy(entry)}
    monkeypatch.setattr(routines, "record_run", lambda *args: (_ for _ in ()).throw(OSError("disk")))
    server._workflow_failed(current)
    assert routines.needs_review(APP, "long", entry)
    with pytest.raises(routines.RoutineError, match="needs review"):
        routines.require_review(APP, "long", entry)


@pytest.mark.parametrize("value", [0, -1, 30.001, True, float("inf"), float("nan")])
def test_budget_keeps_call_duration_bounded(value):
    with pytest.raises(ValueError, match="time_budget"):
        long_workflows.budget(value)


def test_process_identity_requires_real_creation_and_content_instance(monkeypatch):
    monkeypatch.setattr(server.w, "window_pid", lambda hwnd: 10)
    monkeypatch.setattr(server.foreground, "app_pid", lambda hwnd: 20)
    monkeypatch.setattr(server, "process_identity", lambda pid: pid + 100)
    assert server._workflow_identity(HWND) == (HWND, 10, 110, 20, 120)
    monkeypatch.setattr(server, "process_identity", lambda pid: 0)
    with pytest.raises(actions.ActionError, match="Process instance unavailable"):
        server._workflow_identity(HWND)


def test_binding_names_confirm_and_accord_are_inert_text_not_authorizations():
    template = [
        server.Step(do="set", target="Nom", text="{{confirm}}", check=server.Check(target="Nom", value="{{accord}}"))
    ]
    filled = server._filled(template, {"confirm": "Alice", "accord": "Alice"})
    token = server.CONTINUATIONS.put(
        {
            "hwnd": HWND,
            "steps": [step.model_dump() for step in filled],
            "template": [step.model_dump() for step in template],
        }
    )
    server.CONTINUATIONS.observed(HWND)
    retained = server.CONTINUATIONS.take(token)
    assert retained["steps"][0]["text"] == "Alice" and retained["steps"][0]["check"]["value"] == "Alice"
    assert (
        retained["template"][0]["text"] == "{{confirm}}" and retained["template"][0]["check"]["value"] == "{{accord}}"
    )
    assert "confirm" not in retained["steps"][0] and "accord" not in retained["steps"][0]


def integrated(monkeypatch):
    ex, auto, clock = setup(monkeypatch)
    view = ex.view
    monkeypatch.setattr(
        server,
        "SESSION",
        SimpleNamespace(
            views={HWND: view},
            frames={},
            resolve_window=lambda window: HWND,
            check_account=lambda hwnd: None,
            view=lambda *args, **kwargs: view,
            shown_for=lambda hwnd: None,
        ),
    )
    monkeypatch.setattr(server.actions, "Executor", lambda *args, **kwargs: ex)
    monkeypatch.setattr(server, "_check_actionable_window", lambda hwnd: None)
    monkeypatch.setattr(server, "_resume_store_app", lambda *args: None)
    monkeypatch.setattr(server.routines, "app_of", lambda hwnd: (APP, FP))
    monkeypatch.setattr(server.w, "window_text", lambda hwnd: "Fixture")
    monkeypatch.setattr(server.w, "IsIconic", lambda hwnd: False)
    monkeypatch.setattr(server.stop, "hotkey_note", lambda: "")

    class Watch:
        stolen = False

        def __init__(self, *args):
            pass

        def __enter__(self):
            return self

        def __exit__(self, *args):
            pass

        def note(self):
            return ""

    monkeypatch.setattr(server, "Watch", Watch)
    monkeypatch.setattr(server, "render", lambda *args, extra=None, **kwargs: ["\n".join(extra or [])])

    async def immediate(fn, **kwargs):
        return fn(auto)

    monkeypatch.setattr(server, "_run", immediate)
    return ex, auto, clock


def control(result):
    return next(json.loads(line) for line in result[0].splitlines() if line.startswith('{"status":'))


def test_real_act_api_preserves_65_steps_across_continue_and_only_then_remembers(monkeypatch):
    _, auto, _ = integrated(monkeypatch)
    result = asyncio.run(server.act(plan(), window=HWND, then="none", remember="long", time_budget=15.75))
    current = control(result)
    assert current["status"] == "paused" and not routines.names(APP)
    with pytest.raises(ValueError, match="fresh successful look"):
        asyncio.run(server.act(operation="continue", continuation=current["continuation"], window=HWND, then="none"))
    assert len(auto.calls) == 32
    # Nouvelle demande distincte, pas rejeu de l'ancien préfixe : ici fixture réinitialisée en RAM.
    auto.calls.clear()
    result = asyncio.run(server.act(plan(), window=HWND, then="none", remember="long", time_budget=15.75))
    while (current := control(result))["status"] == "paused":
        asyncio.run(server.look(window=HWND, show="elements"))
        result = asyncio.run(
            server.act(
                operation="continue", continuation=current["continuation"], window=HWND, then="none", time_budget=15.75
            )
        )
    assert current["progress"] == {"completed": 65, "total": 65, "nextStep": None}
    assert len(auto.calls) == 65
    assert routines.inspect(APP, "long", FP)["verifiedTransitions"] == 65


def test_stop_after_32_keeps_proved_prefix_and_global_progress(monkeypatch):
    ex, auto, _ = setup(monkeypatch)
    steps, current = plan(), state(plan())
    _, _, pause = server._run_workflow(ex, steps, HWND, as_call(), current, 15.75)
    server.CONTINUATIONS.observed(HWND)
    current = server.CONTINUATIONS.take(pause["continuation"])
    monkeypatch.setattr(server.worker, "check_cancelled", lambda: (_ for _ in ()).throw(worker.Stopped("STOP")))
    with pytest.raises(worker.Stopped) as caught:
        server._run_workflow(ex, steps, HWND, as_call(), current, 15.75)
    text = str(caught.value)
    assert '"completed": 32' in text and "1. ✓" in text and "32. ✓" in text and "33. ⛔" in text
    assert len(auto.calls) == 32


def test_34_step_api_preserves_three_binding_parameters_and_snapshots_before_await(monkeypatch):
    ex, auto, _ = integrated(monkeypatch)
    steps = plan(32) + [
        server.Step(do="set", target="Nom", text="{{nom}}", check=server.Check(target="Nom", value="{{attendu}}")),
        server.Step(do="set", target="Notes", text="{{notes}}", check=server.Check(target="Notes", value="{{notes}}")),
    ]
    params = {"nom": "Alice", "attendu": "Alice", "notes": "Notes privées"}

    async def mutate_after_snapshot(fn, **kwargs):
        steps[-1].target = "Missing"
        params["nom"] = "Injected"
        return fn(auto)

    monkeypatch.setattr(server, "_run", mutate_after_snapshot)
    result = asyncio.run(server.act(steps, window=HWND, then="none", remember="form", params=params, time_budget=15.75))
    paused = control(result)
    assert paused["progress"]["completed"] == 32 and not routines.names(APP)

    async def immediate(fn, **kwargs):
        return fn(auto)

    monkeypatch.setattr(server, "_run", immediate)
    asyncio.run(server.look(window=HWND, show="elements"))
    result = asyncio.run(
        server.act(operation="continue", continuation=paused["continuation"], window=HWND, then="none")
    )
    assert control(result)["status"] == "completed" and len(auto.calls) == 34
    assert server.postcond.evaluate(ex, {"target": "Nom", "value": "Alice"}).state is True
    assert server.postcond.evaluate(ex, {"target": "Notes", "value": "Notes privées"}).state is True
    info = routines.inspect(APP, "form", FP)
    assert info["parameters"] == ["attendu", "nom", "notes"] and info["verifiedTransitions"] == 34
    saved = (routines.ROOT / (APP + ".json")).read_text(encoding="utf-8")
    assert "Alice" not in saved and "Notes privées" not in saved and "Missing" not in saved


def test_changed_saved_definition_while_paused_cannot_resume(monkeypatch):
    ex, auto, _ = setup(monkeypatch)
    steps = plan()
    routines.remember(APP, "long", [step.model_dump() for step in steps], FP, "Fixture")
    current = state(steps) | {"routine": "long", "entry": routines.get(APP, "long"), "remember": None}
    _, _, paused = server._run_workflow(ex, steps, HWND, as_call(), current, 15.75)
    server.CONTINUATIONS.observed(HWND)
    current = server.CONTINUATIONS.take(paused["continuation"])
    routines.remember(APP, "long", [step.model_dump() for step in plan(34)], FP, "Fixture")
    with pytest.raises(actions.ActionError, match="definition changed"):
        server._workflow_revalidate(ex, current, FP)
    assert len(auto.calls) == 32


@pytest.mark.parametrize("kind,expected", [("partial", ["~"]), ("provider", ["~"]), ("not-yet-visible", ["✓", "✓"])])
def test_partial_input_cannot_become_verified_by_check_but_late_visible_effect_can(monkeypatch, kind, expected):
    field = node("Edit", "Nom", has_value=True, value="")
    ex = executor(make_view(field, node("CheckBox", "Option", has_toggle=True, toggle=0)), FakeAuto())
    accepted, calls = [0], []
    monkeypatch.setattr(methods, "accepted_posts", lambda: accepted[0])
    dispatch = server._dispatch

    def partial():
        field.props["value"] = "Alice"
        field.element.live["value"] = "Alice"
        calls.append("keydown accepted")
        if kind == "partial":
            accepted[0] += 1
            raise methods.MethodError("keyup refused")
        if kind == "provider":
            raise methods.InputOutcomeUnknown("provider reply lost")
        raise actions.NoVisibleEffect("not yet visible")

    def act(current, step):
        if step.do == "set":
            return current._try([("first", partial)], "set")
        calls.append("following input")
        return dispatch(current, step)

    monkeypatch.setattr(server, "_dispatch", act)
    steps = [
        server.Step(do="set", target="Nom", text="Alice", check=server.Check(target="Nom", value="Alice")),
        server.Step(do="check", target="Option"),
    ]
    symbols = []
    report, _ = server._run_steps(ex, steps, HWND, as_call(), symbols)
    assert symbols == expected, report
    assert calls == (["keydown accepted", "following input"] if kind == "not-yet-visible" else ["keydown accepted"])


@pytest.mark.parametrize("target", [[], [1, 2, 3], [float("nan"), 2], [1, float("inf")], -1, 0])
def test_malformed_coordinate_or_index_at_65_is_rejected_before_any_input(monkeypatch, target):
    _, auto, _ = setup(monkeypatch)
    steps = plan()
    steps[64] = server.Step(do="click", target=target)
    with pytest.raises(actions.ActionError, match="Step 64"):
        server._workflow_prevalidate(steps)
    assert not auto.calls


@pytest.mark.parametrize("direction", ["DOWN", "bas", "haut", "gauche", "droite", "into_view"])
def test_scroll_validation_preserves_legacy_aliases(direction):
    server._workflow_prevalidate([server.Step(do="scroll", target="Area", direction=direction)])


def test_cancelled_generation_never_publishes_a_late_pause():
    broker = long_workflows.Continuations(lambda state: None)
    generation = broker.generation
    broker.discard()
    with pytest.raises(ValueError, match="cancelled"):
        broker.put({"hwnd": HWND}, generation=generation)
    with pytest.raises(ValueError, match="absent"):
        broker.take("old")


@pytest.mark.parametrize("saved", [False, True])
def test_cancel_after_last_dispatched_step_never_learns_or_clears_review(monkeypatch, saved):
    _, auto, _ = integrated(monkeypatch)
    steps = plan(34)
    if saved:
        routines.remember(APP, "long", [step.model_dump() for step in steps], FP, "Fixture")

    def check():
        if len(auto.calls) == 34:
            raise worker.Cancelled("client cancelled during last dispatch")

    monkeypatch.setattr(server.worker, "check_cancelled", check)
    with pytest.raises(worker.Cancelled):
        asyncio.run(
            server.act(
                steps=None if saved else steps,
                routine="long" if saved else None,
                remember=None if saved else "new",
                window=HWND,
                then="none",
            )
        )
    assert len(auto.calls) == 34
    if saved:
        assert routines.needs_review(APP, "long", routines.get(APP, "long"))
    else:
        assert not routines.names(APP)
    with pytest.raises(ValueError, match="absent"):
        server.CONTINUATIONS.take("late")


def test_async_cancellation_abandons_worker_and_revokes_late_publication(monkeypatch):
    entered, release, cancelled = threading.Event(), threading.Event(), threading.Event()
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=1)

    class FakeWorker:
        def submit(self, fn):
            return pool.submit(fn, None)

        def abandon(self):
            cancelled.set()

    monkeypatch.setattr(server, "WORKER", FakeWorker())
    monkeypatch.setattr(server, "SESSION", SimpleNamespace(forget_all=lambda: None))
    monkeypatch.setattr(server.freshness, "stale_reason", lambda: None)
    generation = server.CONTINUATIONS.generation
    results = []

    def late(_):
        entered.set()
        release.wait(2)
        try:
            server.CONTINUATIONS.put({"hwnd": HWND}, generation=generation)
        except ValueError:
            results.append("publication refused")

    late.__dict__["_long_workflow"] = True

    async def run():
        task = asyncio.create_task(server._run(late))
        assert await asyncio.to_thread(entered.wait, 1)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        assert cancelled.is_set()
        release.set()

    try:
        asyncio.run(run())
    finally:
        release.set()
        pool.shutdown(wait=True)
    assert results == ["publication refused"]
    with pytest.raises(ValueError, match="absent"):
        server.CONTINUATIONS.take("late")


def test_queued_other_action_invalidates_token_when_it_executes_not_before(monkeypatch):
    entered, release = threading.Event(), threading.Event()
    pool = concurrent.futures.ThreadPoolExecutor(max_workers=1)

    class FakeWorker:
        def submit(self, fn):
            return pool.submit(fn, None)

        def abandon(self):
            pass

    monkeypatch.setattr(server, "WORKER", FakeWorker())
    monkeypatch.setattr(server.freshness, "stale_reason", lambda: None)
    tokens = []

    def first(_):
        entered.set()
        release.wait(2)
        tokens.append(server.CONTINUATIONS.put({"hwnd": HWND}))

    first.__dict__["_long_workflow"] = True

    async def run():
        one = asyncio.create_task(server._run(first))
        assert await asyncio.to_thread(entered.wait, 1)
        two = asyncio.create_task(server._run(lambda _: "other action"))
        await asyncio.sleep(0)
        release.set()
        await asyncio.gather(one, two)

    try:
        asyncio.run(run())
    finally:
        release.set()
        pool.shutdown(wait=True)
    server.CONTINUATIONS.observed(HWND)
    with pytest.raises(ValueError, match="absent"):
        server.CONTINUATIONS.take(tokens[0])
