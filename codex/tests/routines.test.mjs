import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, readFile, writeFile, readdir } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { RoutineMemory, routineSteps, expandRoutine, routineAuthorizations, routineAnchor, defaultRoutineMirror } from '../lib/routines.mjs';
import { WindowsSession } from '../lib/session.mjs';
import { WindowsTools, TOOLS, schemaErrors } from '../lib/mcp.mjs';
import { parseTree, digest } from '../lib/tree.mjs';

const selector = id => ({ role: 'edit', automationId: id });
const templates = () => routineSteps([{ kind: 'setValue', selector: selector('nom'), value: '{{nom}}', timeoutMs: 0 },
  { kind: 'setValue', selector: selector('ville'), value: '{{ville}}', timeoutMs: 0 }]);
async function directory(t) { const root = await mkdtemp(path.join(os.tmpdir(), 'nexus-routines-')); t.after(() => rm(root, { recursive: true, force: true })); return root; }
async function fixture(t) {
  const root = await directory(t), window = { id: 10, pid: 42, app: 'process:C:\\Apps\\Fixture.exe', title: 'PRIVATE WINDOW DOCUMENT' };
  const state = { nom: '', ville: '', revision: digest('v1'), group: 'form', trigger: 'Valider', calls: [], fail: false, scroll: false, done: false };
  const sky = { target: 'windows', async list_windows() { return [window]; }, async get_window() { return window; },
    async get_window_state() { return { window, accessibility: null, screenshots: [] }; } };
  const native = {
    async inspect() {
      const tree = `0 fenêtre ${window.title}\n\t1 Groupe ID: ${state.group}\n\t\t2 édition Nom Value: ${state.nom} ID: nom\n\t\t3 édition Ville Value: ${state.ville} ID: ville\n\t\t4 Bouton ${state.trigger} ID: go\n\t\t5 Texte Status ID: status\n\t\t6 édition Autre ID: other\n\t\t7 Texte ${state.done ? 'Done' : 'Ready'} ID: result`;
      return { accessibility: { tree }, revision: state.revision, processStartedUtc: state.processStartedUtc ?? '2026-10-29T00:00:00.0000000Z',
        contentProcessId: state.contentProcessId ?? window.pid, contentProcessStartedUtc: state.contentProcessStartedUtc ?? '2026-10-29T00:00:00.0000000Z', elements: parseTree(tree).map(e => ({ ...e,
        ...(e.index === 2 ? { automationId: 'nom', value: state.nom } : e.index === 3 ? { automationId: 'ville', value: state.ville } : {}),
        runtimeId: [42, e.index], bounds: [10, 10 + e.index * 20, 80, 18] })) };
    },
    async call(_, op, args) {
      state.calls.push({ op, args });
      if (state.fail) throw Error('transport lost after partial input');
      if (op === 'setValue') state[args.selector.automationId] = args.value;
      if (op === 'click') state.done = true;
      if (op === 'physical') { state.scroll = true; state.done = true; return { mechanism: 'SendInput wheel' }; }
      return {};
    },
    async withForeground(_, run) { return run(); },
  };
  const session = new WindowsSession(sky, { stateRoot: root, routineMirror: null, native, emergencyStop: { assertClear() {
    if (state.stopped) throw Object.assign(Error('Emergency stop'), { emergencyStop: true });
  } } });
  t.after(() => session.close()); await session.bind({ id: 10 });
  return { root, session, state, native, window, observation: await session.observe() };
}
async function learn(t) {
  const f = await fixture(t), steps = templates(), params = { nom: 'PRIVATE ALICE', ville: 'PRIVATE PARIS' };
  const report = await f.session.actBatch(f.observation, expandRoutine(steps, params));
  assert.equal(report.status, 'completed');
  const learned = await f.session.rememberRoutine('formulaire', report, { steps, params, staticUI: true });
  return { ...f, steps, params, report, learned };
}

function longSteps(length) {
  return routineSteps(Array.from({ length }, (_, i) => ({ kind: 'setValue', selector: selector('nom'), value: i % 2 ? '{{b}}' : '{{a}}', timeoutMs: 0 })));
}
function clockInputs(t, f) {
  let instant = Date.now(); t.mock.method(Date, 'now', () => instant);
  const call = f.native.call;
  f.native.call = async (...args) => { try { return await call(...args); } finally { instant++; } };
  return { advance(ms) { instant += ms; } };
}

test('34 real transitions run without a count ceiling and every later step is prevalidated before the first input', async t => {
  const f = await fixture(t), steps = longSteps(34), params = { a: 'PRIVATE A', b: 'PRIVATE B' };
  const report = await f.session.actBatch(f.observation, expandRoutine(steps, params));
  assert.equal(report.status, 'completed'); assert.equal(report.completed, 34);
  assert.equal(report.steps.filter(s => s.verified && s.performed).length, 34); assert.equal(f.state.calls.length, 34);
  const invalid = expandRoutine(longSteps(65), params); invalid[64].kind = 'invented';
  await assert.rejects(f.session.actBatch(await f.session.observe(), invalid), /Step 64/);
  assert.equal(f.state.calls.length, 34);
  assert.deepEqual(schemaErrors(TOOLS.find(t => t.name === 'act').inputSchema, { steps: expandRoutine(longSteps(65), params) }), []);
});

test('65 verified transitions continue explicitly from step 32, learn the whole proof, and replay new parameters', async t => {
  const f = await fixture(t), steps = longSteps(65), params = { a: 'PRIVATE A', b: 'PRIVATE B' }; clockInputs(t, f);
  let report = await f.session.actBatch(await f.session.observe(), expandRoutine(steps, params), { timeBudgetMs: 32 });
  assert.equal(report.status, 'paused'); assert.deepEqual(report.progress, { completed: 32, total: 65, nextStep: 32 });
  assert.equal(f.state.calls.length, 32); assert.equal(f.state.nom, params.b);
  await assert.rejects(f.session.rememberRoutine('longue', report, { steps, params, staticUI: true }), /complete/);
  const used = report.continuation;
  report = await f.session.continueBatch(await f.session.observe(), used, { timeBudgetMs: 32 });
  assert.equal(report.status, 'paused'); assert.deepEqual(report.progress, { completed: 64, total: 65, nextStep: 64 });
  assert.equal(f.state.calls.length, 64);
  await assert.rejects(f.session.continueBatch(await f.session.observe(), used), /no longer current/);
  report = await f.session.continueBatch(await f.session.observe(), report.continuation);
  assert.equal(report.status, 'completed'); assert.deepEqual(report.progress, { completed: 65, total: 65, nextStep: null });
  assert.equal(report.continuation, undefined); assert.equal(f.state.calls.length, 65);
  assert.equal((await f.session.rememberRoutine('longue', report, { steps, params, staticUI: true })).verifiedTransitions, 65);
  const memory = new RoutineMemory(f.root, { mirror: null }), stored = await memory.read(f.observation.window.app, 'longue');
  assert.equal(stored.steps.length, 65); assert.equal(stored.anchors.length, 65);
  assert.doesNotMatch(await readFile(memory.file(f.observation.window.app), 'utf8'), /PRIVATE|confirm|accord|continuation/);
  const replay = await f.session.runRoutine(await f.session.observe(), 'longue', { params: { a: 'PRIVATE C', b: 'PRIVATE D' } });
  assert.equal(replay.status, 'completed'); assert.equal(replay.steps.filter(s => s.verified && s.performed).length, 65);
  assert.equal(f.state.calls.length, 130); assert.equal(f.state.nom, 'PRIVATE C');
});

test('an unknown input after the step-32 pause sends no following input and no continuation, and marks routine review', async t => {
  const f = await fixture(t), steps = longSteps(65), params = { a: 'A', b: 'B' }; clockInputs(t, f);
  const learned = await f.session.actBatch(await f.session.observe(), expandRoutine(steps, params));
  await f.session.rememberRoutine('longue', learned, { steps, params, staticUI: true });
  let report = await f.session.runRoutine(await f.session.observe(), 'longue', { params: { a: 'C', b: 'D' }, timeBudgetMs: 32 });
  assert.equal(report.status, 'paused'); assert.equal(report.needsReview, false);
  assert.equal((await f.session.listRoutines()).routines[0].runs, 0);
  const token = report.continuation; f.state.fail = true;
  report = await f.session.continueBatch(await f.session.observe(), token);
  assert.equal(report.status, 'stopped'); assert.equal(report.stoppedAt, 32); assert.equal(report.completed, 32);
  assert.equal(report.steps.at(-1).status, 'unknown'); assert.equal(report.continuation, undefined);
  assert.equal(f.state.calls.length, 98); assert.equal(report.needsReview, true);
  await assert.rejects(f.session.continueBatch(await f.session.observe(), token), /no longer current/);
  f.state.fail = false;
  await assert.rejects(f.session.runRoutine(await f.session.observe(), 'longue', { params }), /needs review/);
  assert.equal(f.state.calls.length, 98);
});

test('a continuation needs a new observation, expires, and is invalidated by another action or an application change', async t => {
  const f = await fixture(t), clock = clockInputs(t, f), concrete = expandRoutine(longSteps(34), { a: 'A', b: 'B' });
  let report = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 1 });
  await assert.rejects(f.session.continueBatch(report.observation, report.continuation), /changed/);
  assert.equal(f.state.calls.length, 1);
  report = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 1 }); clock.advance(f.session.maxAgeMs + 1);
  await assert.rejects(f.session.continueBatch(await f.session.observe(), report.continuation), /expired/);
  report = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 1 });
  await f.session.act(await f.session.observe(), { kind: 'setValue', selector: selector('ville'), value: 'other', expect: { selector: selector('ville'), field: 'value', equals: 'other' }, timeoutMs: 0 });
  await assert.rejects(f.session.continueBatch(await f.session.observe(), report.continuation), /no longer current/);
  report = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 1 }); f.state.revision = digest('v2');
  await assert.rejects(f.session.continueBatch(await f.session.observe(), report.continuation), /changed/);
});

test('confirm and accord from the initial call never authorize a sensitive step after pause', async t => {
  const f = await fixture(t); clockInputs(t, f); f.state.trigger = 'Désinstaller';
  const steps = [...expandRoutine(longSteps(32), { a: 'A', b: 'B' }), { kind: 'click', selector: { automationId: 'go' }, confirm: true, accord: '1234abcd',
    expect: { selector: { automationId: 'result' }, field: 'name', equals: 'Done' }, timeoutMs: 0 }];
  let report = await f.session.actBatch(await f.session.observe(), steps, { timeBudgetMs: 32 });
  assert.equal(report.status, 'paused');
  report = await f.session.continueBatch(await f.session.observe(), report.continuation);
  assert.equal(report.status, 'stopped'); assert.equal(report.steps.at(-1).status, 'confirmation-required'); assert.equal(f.state.calls.length, 32);
  report = await f.session.actBatch(await f.session.observe(), steps, { timeBudgetMs: 32 });
  assert.equal(report.status, 'paused');
  report = await f.session.continueBatch(await f.session.observe(), report.continuation, { authorizations: { '32': { confirm: true } } });
  assert.equal(report.status, 'completed'); assert.equal(f.state.calls.at(-1).op, 'click');
});

test('an unmet step after the step-32 boundary ends the procedure without sending step 34', async t => {
  const f = await fixture(t); clockInputs(t, f);
  const concrete = expandRoutine(longSteps(34), { a: 'A', b: 'B' }); concrete[32].expect.equals = 'never';
  const paused = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 32 });
  const report = await f.session.continueBatch(await f.session.observe(), paused.continuation);
  assert.equal(report.status, 'stopped'); assert.equal(report.stoppedAt, 32); assert.equal(report.steps.at(-1).status, 'unmet');
  assert.equal(report.continuation, undefined); assert.equal(f.state.calls.length, 33); assert.equal(f.state.nom, 'A');
});

test('recycled HWND/PID identity and the emergency stop both invalidate a pending continuation before input', async t => {
  const f = await fixture(t); clockInputs(t, f);
  const concrete = expandRoutine(longSteps(34), { a: 'A', b: 'B' });
  let paused = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 1 });
  f.window.pid++; await f.session.bind({ id: 10 });
  await assert.rejects(f.session.continueBatch(await f.session.observe(), paused.continuation), /changed/);
  assert.equal(f.state.calls.length, 1);
  f.state.nom = '';
  paused = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 1 });
  f.state.stopped = true;
  await assert.rejects(f.session.continueBatch(await f.session.observe(), paused.continuation), /Emergency stop/);
  f.state.stopped = false;
  await assert.rejects(f.session.continueBatch(await f.session.observe(), paused.continuation), /no longer current/);
  assert.equal(f.state.calls.length, 2);
  f.state.nom = '';
  paused = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 1 });
  f.state.processStartedUtc = '2026-10-29T01:00:00.0000000Z';
  await assert.rejects(f.session.continueBatch(await f.session.observe(), paused.continuation), /changed/);
  assert.equal(f.state.calls.length, 3);
  f.state.nom = '';
  paused = await f.session.actBatch(await f.session.observe(), concrete, { timeBudgetMs: 1 });
  f.state.contentProcessStartedUtc = '2026-10-29T02:00:00.0000000Z';
  await assert.rejects(f.session.continueBatch(await f.session.observe(), paused.continuation), /changed/);
  assert.equal(f.state.calls.length, 4);
});

test('an older helper without process creation proof cannot issue a continuation', async t => {
  const f = await fixture(t); clockInputs(t, f);
  const inspect = f.native.inspect; f.native.inspect = async (...args) => { const value = await inspect(...args); delete value.processStartedUtc; return value; };
  const report = await f.session.actBatch(await f.session.observe(), expandRoutine(longSteps(34), { a: 'A', b: 'B' }), { timeBudgetMs: 1 });
  assert.equal(report.status, 'stopped'); assert.equal(report.continuation, undefined); assert.equal(f.state.calls.length, 1);
  assert.match(report.reason, /process creation identity is unavailable/);
});

test('a changed verified boundary is refused before continuation even when the interface structure is unchanged', async t => {
  const f = await fixture(t); clockInputs(t, f);
  const paused = await f.session.actBatch(await f.session.observe(), expandRoutine(longSteps(34), { a: 'A', b: 'B' }), { timeBudgetMs: 32 });
  f.state.nom = 'CHANGED BY USER';
  await assert.rejects(f.session.continueBatch(await f.session.observe(), paused.continuation), /Last verified postcondition changed/);
  assert.equal(f.state.calls.length, 32);
  await assert.rejects(f.session.continueBatch(await f.session.observe(), paused.continuation), /no longer current/);
});

test('the complete step definition is snapshotted before awaiting the first input', async t => {
  const f = await fixture(t), steps = expandRoutine(longSteps(34), { a: 'A', b: 'B' });
  let entered, release;
  const first = new Promise(resolve => { entered = resolve; }), gate = new Promise(resolve => { release = resolve; }), call = f.native.call;
  f.native.call = async (...args) => { if (!f.state.calls.length) { entered(); await gate; } return call(...args); };
  const running = f.session.actBatch(await f.session.observe(), steps);
  await first; steps[33].value = 'MUTATED'; steps[33].expect.equals = 'MUTATED'; release();
  const report = await running;
  assert.equal(report.status, 'completed'); assert.equal(f.state.calls.length, 34);
  assert.equal(f.state.nom, 'B'); assert.equal(f.state.calls.at(-1).args.value, 'B');
});

test('MCP keeps the 65-step learning proof in RAM and exposes explicit progress/continuation, then records the complete routine', async t => {
  const f = await fixture(t); clockInputs(t, f);
  const tools = new WindowsTools(f.session), id = content => /observation ([^ ]+)/.exec(content.find(c => c.text?.startsWith('observation ')).text)[1];
  const progress = content => content.map(c => { try { return JSON.parse(c.text); } catch { return null; } }).find(c => c?.progress);
  let response = await tools.call('act', { observation: id(await tools.call('look', { window: 10 })), remember: 'longue-mcp', staticUI: true,
    steps: longSteps(65), params: { a: 'PRIVATE A', b: 'PRIVATE B' }, timeBudgetMs: 32 });
  let p = progress(response); assert.equal(p.status, 'paused'); assert.equal(p.progress.completed, 32);
  assert.deepEqual((await f.session.listRoutines()).routines, []);
  response = await tools.call('act', { operation: 'continue', observation: id(await tools.call('look', { window: 10 })), continuation: p.continuation, timeBudgetMs: 32 });
  p = progress(response); assert.equal(p.status, 'paused'); assert.equal(p.progress.completed, 64);
  response = await tools.call('act', { operation: 'continue', observation: id(await tools.call('look', { window: 10 })), continuation: p.continuation });
  assert.equal(progress(response).status, 'completed'); assert.match(JSON.stringify(response), /learned/);
  assert.equal((await f.session.listRoutines()).routines[0].steps, 65); assert.equal(f.state.calls.length, 65);
  assert.deepEqual(schemaErrors(TOOLS.find(t => t.name === 'act').inputSchema, { operation: 'continue', continuation: 'token', observation: 'obs' }), []);
});

test('parameters change text only; fixed typed content, target/key/path parameters and authorizations never enter definitions', () => {
  const steps = templates(); assert.equal(expandRoutine(steps, { nom: 'A {{other}}', ville: 'B' })[0].value, 'A {{other}}');
  assert.throws(() => expandRoutine(steps, { nom: 'A' }), /missing ville/);
  assert.throws(() => expandRoutine(steps, { nom: 'A', ville: 'B', other: 'x' }), /unknown/);
  assert.throws(() => routineSteps([{ kind: 'setValue', selector: selector('nom'), value: 'SECRET' }]), /only.*templates/);
  for (const step of [{ kind: 'key', key: '{{key}}' }, { kind: 'menu', path: '{{path}}' }, { kind: 'click', selector: { name: '{{target}}' } }]) {
    assert.throws(() => routineSteps([{ ...step, expect: { selector: { automationId: 'result' }, field: 'name', equals: 'Done' } }]), /parameters.*only|parameters cannot/);
  }
  assert.throws(() => routineSteps([{ kind: 'click', selector: { ancestor: { window: 3 }, name: 'Go' }, expect: { selector: { name: 'Done' }, exists: true } }]), /window handle/);
  for (const target of [{ role: 'window', name: 'PRIVATE TITLE' }, { role: 'document', name: 'PRIVATE DOCUMENT' },
    { name: 'Go', ancestor: { role: 'window', name: 'PRIVATE TITLE' } }, { name: 'Go', ancestor: { role: 'document', name: 'PRIVATE DOCUMENT' } }]) {
    assert.throws(() => routineSteps([{ kind: 'click', selector: target, expect: { selector: { automationId: 'result' }, field: 'name', equals: 'Done' } }]), /window or document title/);
  }
  const authorized = routineAuthorizations(steps, { '0': { confirm: true, accord: '1234abcd' } });
  assert.equal(routineSteps(authorized)[0].confirm, undefined);
  assert.throws(() => routineAuthorizations(steps, { '2': { confirm: true } }), /authorization/);
  assert.throws(() => routineAuthorizations(steps, { '0': { path: 'x' } }), /authorization/);
});

test('storage synchronizes definitions only, preserves local failure state, and propagates deletion without resurrection', async t => {
  const root = await directory(t), mirror = path.join(root, 'mirror'); let instant = 0;
  const now = () => new Date(Date.UTC(2026, 0, 1, 0, 0, instant++));
  const a = new RoutineMemory(path.join(root, 'a'), { mirror, now }), b = new RoutineMemory(path.join(root, 'b'), { mirror, now });
  const app = 'process:C:\\PRIVATE\\fixture.exe', entry = { steps: templates(), revision: digest('v1'), structureHash: digest('tree'), anchors: [digest('a'), digest('b')], coverage: 'verified', verifiedTransitions: 2 };
  await a.save(app, 'formulaire', entry); await a.recordRun(app, 'formulaire', false); await a.sync(app); await b.sync('process:D:\\Other\\Fixture.exe');
  assert.equal((await a.list(app))[0].needsReview, true); assert.equal((await b.list(app))[0].needsReview, false);
  const shared = await readFile(a.file(app, true), 'utf8');
  assert.doesNotMatch(shared, /PRIVATE|lastOk|lastRun|runs|window|confirm|accord/);
  assert.equal((await b.read(app, 'formulaire')).coverage, 'verified');
  await b.forget(app, 'formulaire'); await a.sync(app); assert.deepEqual(await a.list(app), []);
  await a.sync(app); assert.equal(await a.read(app, 'formulaire'), null);
  await writeFile(a.file(app), '{bad'); await assert.rejects(a.list(app), /JSON|property/);
  assert.equal(defaultRoutineMirror(path.join(root, 'other'), { LOCALAPPDATA: root }), null);
  assert.equal(defaultRoutineMirror(root, { NEXUS_ROUTINES_MIRROR: '' }), null);
});

test('a genuine multi-step proof learns templates; replay revalidates targets even after an application update', async t => {
  const f = await learn(t); assert.equal(f.learned.status, 'learned'); assert.equal(f.learned.verifiedTransitions, 2);
  const files = await readdir(path.join(f.root, 'routines')), raw = await readFile(path.join(f.root, 'routines', files[0]), 'utf8');
  assert.doesNotMatch(raw, /PRIVATE|PARIS|ALICE|confirm|accord|runtimeId|bounds/);
  await assert.rejects(f.session.rememberRoutine('forged', { ...f.report }, { steps: f.steps, params: f.params, staticUI: true }), /from this session/);
  await assert.rejects(f.session.rememberRoutine('wrong', f.report, { steps: f.steps, params: { nom: 'other', ville: 'other' }, staticUI: true }), /exactly/);
  f.state.revision = digest('v2'); const observation = await f.session.observe();
  const inspected = await f.session.inspectRoutine('formulaire', observation); assert.equal(inspected.applicationChanged, true); assert.equal(inspected.status, 'ready');
  const replay = await f.session.runRoutine(observation, 'formulaire', { params: { nom: 'Bob', ville: 'Lyon' } });
  assert.equal(replay.status, 'completed'); assert.equal(replay.applicationChanged, true); assert.equal(f.state.calls.length, 4);
  f.state.group = 'different'; const changed = await f.session.observe();
  await assert.rejects(f.session.runRoutine(changed, 'formulaire', { params: { nom: 'C', ville: 'D' } }), /changed/); assert.equal(f.state.calls.length, 4);
});

test('all already-satisfied steps can be recorded with zero exercised transitions', async t => {
  const f = await learn(t), observation = await f.session.observe();
  const report = await f.session.actBatch(observation, expandRoutine(f.steps, f.params));
  const learned = await f.session.rememberRoutine('deja-fait', report, { steps: f.steps, params: f.params, staticUI: true });
  assert.equal(learned.status, 'recorded-unexercised'); assert.equal(learned.verified, false); assert.equal(learned.coverage, 'not-exercised'); assert.equal(learned.verifiedTransitions, 0);
});

test('unknown outcome stops once, marks needs-review, and requires an explicit fresh inspection before a new run', async t => {
  const f = await learn(t); f.state.fail = true;
  const report = await f.session.runRoutine(await f.session.observe(), 'formulaire', { params: { nom: 'B', ville: 'C' } });
  assert.equal(report.status, 'stopped'); assert.equal(report.steps[0].status, 'unknown'); assert.equal(report.steps.length, 1); assert.equal(report.needsReview, true); assert.equal(f.state.calls.length, 3);
  f.state.fail = false; const fresh = await f.session.observe();
  await assert.rejects(f.session.runRoutine(fresh, 'formulaire', { params: { nom: 'B', ville: 'C' } }), /needs review/); assert.equal(f.state.calls.length, 3);
  await f.session.inspectRoutine('formulaire', fresh);
  const resumed = await f.session.runRoutine(fresh, 'formulaire', { params: { nom: 'B', ville: 'C' } }); assert.equal(resumed.status, 'completed'); assert.equal(f.state.calls.length, 5);
});

test('an unmet first text postcondition stops before the tail and never repeats input', async t => {
  const f = await fixture(t), steps = templates(); steps[0].expect.equals = '{{attendu}}';
  const params = { nom: 'A', ville: 'B', attendu: 'A' };
  const report = await f.session.actBatch(f.observation, expandRoutine(steps, params));
  await f.session.rememberRoutine('preuve', report, { steps, params, staticUI: true });
  const replay = await f.session.runRoutine(await f.session.observe(), 'preuve', { params: { nom: 'C', ville: 'D', attendu: 'never' } });
  assert.equal(replay.status, 'stopped'); assert.equal(replay.steps[0].status, 'unmet'); assert.equal(replay.steps.length, 1); assert.equal(f.state.calls.length, 3); assert.equal(f.state.ville, 'B');
});

test('wait performs no input and semantic scroll delegates a freshly identified selector, never stored coordinates', async t => {
  const f = await fixture(t), expect = { selector: { automationId: 'result' }, field: 'name', equals: 'Done' };
  const waited = await f.session.act(f.observation, { kind: 'wait', expect, timeoutMs: 0 }); assert.equal(waited.status, 'unmet'); assert.equal(waited.performed, false); assert.equal(f.state.calls.length, 0);
  const scroll = { kind: 'scroll', selector: selector('nom'), scrollY: 120, expect, timeoutMs: 0 };
  const report = await f.session.actBatch(await f.session.observe(), [scroll]); assert.equal(report.status, 'completed');
  const call = f.state.calls[0]; assert.equal(call.op, 'physical'); assert.equal(call.args.action, 'wheel'); assert.deepEqual(call.args.runtimeId, [42, 2]); assert.equal(call.args.x, undefined);
  await f.session.rememberRoutine('descendre', report, { steps: [scroll], staticUI: true });
  f.state.done = false; const replay = await f.session.runRoutine(await f.session.observe(), 'descendre'); assert.equal(replay.status, 'completed'); assert.equal(f.state.calls.length, 2);
});

test('MCP learns, inventories, inspects, replays and forgets a procedure through the same five tools', async t => {
  const f = await fixture(t), tools = new WindowsTools(f.session), look = () => tools.call('look', { window: 10 });
  const id = content => /observation ([^ ]+)/.exec(content.find(c => c.text?.startsWith('observation ')).text)[1];
  const first = await look();
  const learned = await tools.call('act', { observation: id(first), steps: templates(), params: { nom: 'A', ville: 'B' }, remember: 'formulaire', staticUI: true });
  assert.match(JSON.stringify(learned), /learned/);
  assert.match(JSON.stringify(await tools.call('act', { operation: 'list' })), /formulaire/);
  const fresh = await look(), observation = id(fresh);
  assert.match(JSON.stringify(await tools.call('act', { operation: 'inspect', routine: 'formulaire', observation })), /ready/);
  const replay = await tools.call('act', { observation, routine: 'formulaire', params: { nom: 'C', ville: 'D' } }); assert.match(JSON.stringify(replay), /verified/); assert.equal(f.state.nom, 'C');
  assert.match(JSON.stringify(await tools.call('act', { operation: 'forget', routine: 'formulaire' })), /true/);
  const schema = TOOLS.find(t => t.name === 'act').inputSchema;
  assert.ok(schemaErrors(schema, {}).length); assert.ok(schemaErrors(schema, { routine: 'x', params: { x: 3 } }).length); assert.deepEqual(schemaErrors(schema, { operation: 'list' }), []);
});

test('confirmation from learning is stripped and must come from the current conversation on every replay', async t => {
  const f = await fixture(t); f.state.trigger = 'Désinstaller';
  const steps = [{ kind: 'click', selector: { role: 'button', automationId: 'go' }, confirm: true,
    expect: { selector: { automationId: 'result' }, field: 'name', equals: 'Done' }, timeoutMs: 0 }];
  const learned = await f.session.actBatch(await f.session.observe(), steps); assert.equal(learned.status, 'completed');
  await f.session.rememberRoutine('sensible', learned, { steps, staticUI: true });
  f.state.done = false;
  const refused = await f.session.runRoutine(await f.session.observe(), 'sensible');
  assert.equal(refused.steps[0].status, 'confirmation-required'); assert.equal(f.state.calls.length, 1);
  const fresh = await f.session.observe(); await f.session.inspectRoutine('sensible', fresh);
  const authorized = await f.session.runRoutine(fresh, 'sensible', { authorizations: { '0': { confirm: true } } });
  assert.equal(authorized.status, 'completed'); assert.equal(f.state.calls.length, 2);
  f.state.done = false; const again = await f.session.runRoutine(await f.session.observe(), 'sensible'); assert.equal(again.steps[0].status, 'confirmation-required');
  const file = new RoutineMemory(f.root, { mirror: null }).file('process:Fixture.exe'); assert.doesNotMatch(await readFile(file, 'utf8'), /confirm|accord/);
});

test('a deletion wins a same-time synchronization tie', async t => {
  const root = await directory(t), now = () => new Date('2026-01-01T00:00:00Z'), mirror = path.join(root, 'mirror');
  const a = new RoutineMemory(path.join(root, 'a'), { mirror, now }), b = new RoutineMemory(path.join(root, 'b'), { mirror, now });
  await a.save('fixture', 'exemple', { steps: templates(), revision: null, structureHash: digest('tree'), anchors: [digest('a'), digest('b')], coverage: 'not-exercised', verifiedTransitions: 0 });
  await b.sync('fixture'); await b.forget('fixture', 'exemple'); await a.sync('fixture'); assert.equal(await a.read('fixture', 'exemple'), null);
});

test('routine names shared with Object properties remain ordinary data across synchronization', async t => {
  const root = await directory(t), mirror = path.join(root, 'mirror');
  const a = new RoutineMemory(path.join(root, 'a'), { mirror }), b = new RoutineMemory(path.join(root, 'b'), { mirror });
  await a.save('fixture', 'constructor', { steps: templates(), revision: null, structureHash: digest('tree'), anchors: [digest('a'), digest('b')], coverage: 'not-exercised', verifiedTransitions: 0 });
  await b.sync('fixture'); assert.equal((await b.read('fixture', 'constructor')).coverage, 'not-exercised'); assert.equal((await b.list('fixture'))[0].name, 'constructor');
});

test('outward authorization tokens stay current-only, are never saved, and are never replayed from learning', async t => {
  const f = await fixture(t); f.state.trigger = 'Envoyer';
  const steps = [{ kind: 'click', selector: { role: 'button', automationId: 'go' }, confirm: true,
    expect: { selector: { automationId: 'result' }, field: 'name', equals: 'Done' }, timeoutMs: 0 }];
  const refused = await f.session.actBatch(await f.session.observe(), steps); assert.equal(refused.steps[0].status, 'confirmation-required');
  const token = /accord:"([a-f0-9]{8})"/.exec(refused.steps[0].error)?.[1]; assert.ok(token); assert.equal(f.state.calls.length, 0);
  const authorizedSteps = routineAuthorizations(steps, { '0': { confirm: true, accord: token } });
  const batch = await f.session.actBatch(await f.session.observe(), authorizedSteps); assert.equal(batch.status, 'completed');
  await f.session.rememberRoutine('envoyer', batch, { steps: authorizedSteps, staticUI: true });
  const file = new RoutineMemory(f.root, { mirror: null }).file('process:Fixture.exe'); assert.doesNotMatch(await readFile(file, 'utf8'), /confirm|accord/);
  f.state.done = false;
  const replay = await f.session.runRoutine(await f.session.observe(), 'envoyer', { authorizations: { '0': { confirm: true, accord: token } } });
  assert.equal(replay.steps[0].status, 'confirmation-required'); assert.equal(f.state.calls.length, 1); assert.match(replay.steps[0].error, /already used/);
});

test('another failure invalidates an earlier explicit inspection receipt', async t => {
  const f = await learn(t); f.state.fail = true;
  await f.session.runRoutine(await f.session.observe(), 'formulaire', { params: { nom: 'C', ville: 'D' } });
  f.state.fail = false; const observation = await f.session.observe(); await f.session.inspectRoutine('formulaire', observation);
  await new RoutineMemory(f.root, { mirror: null }).recordRun('process:Fixture.exe', 'formulaire', false);
  await assert.rejects(f.session.runRoutine(observation, 'formulaire', { params: { nom: 'C', ville: 'D' } }), /needs review/); assert.equal(f.state.calls.length, 3);
});

test('needs-review survives a failed outcome write after unknown input', async t => {
  const f = await learn(t), file = new RoutineMemory(f.root, { mirror: null }).file('process:Fixture.exe'), saved = await readFile(file, 'utf8');
  const original = f.native.call;
  f.native.call = async () => { await writeFile(file, '{broken'); throw Error('partial input then disconnected'); };
  const result = await f.session.runRoutine(await f.session.observe(), 'formulaire', { params: { nom: 'B', ville: 'C' } });
  assert.equal(result.needsReview, true); assert.equal(result.steps[0].status, 'unknown'); assert.match(result.memoryError, /could not be saved/);
  await writeFile(file, saved); f.native.call = original;
  await assert.rejects(f.session.runRoutine(await f.session.observe(), 'formulaire', { params: { nom: 'B', ville: 'C' } }), /needs review/);
});

test('MCP learning executes current step confirmations while persisting no authorization', async t => {
  const f = await fixture(t), tools = new WindowsTools(f.session); f.state.trigger = 'Désinstaller';
  const content = await tools.call('look', { window: 10 }), observation = /observation ([^ ]+)/.exec(content.find(c => c.text?.startsWith('observation ')).text)[1];
  const steps = [{ kind: 'click', selector: { role: 'button', automationId: 'go' }, confirm: true,
    expect: { selector: { automationId: 'result' }, field: 'name', equals: 'Done' }, timeoutMs: 0 }];
  const result = await tools.call('act', { observation, steps, remember: 'sensible', staticUI: true });
  assert.match(JSON.stringify(result), /learned/); assert.equal(f.state.calls.length, 1);
  const file = new RoutineMemory(f.root, { mirror: null }).file('process:Fixture.exe'); assert.doesNotMatch(await readFile(file, 'utf8'), /confirm|accord/);
});

test('learning rejects resolved window titles even when the selector omitted the window role', async t => {
  const f = await fixture(t), steps = [{ kind: 'wait', expect: { selector: { name: 'PRIVATE WINDOW DOCUMENT' }, field: 'name', equals: 'PRIVATE WINDOW DOCUMENT' }, timeoutMs: 0 }];
  const report = await f.session.actBatch(f.observation, steps); assert.equal(report.status, 'completed');
  await assert.rejects(f.session.rememberRoutine('titre', report, { steps, staticUI: true }), /window or document title/);
  assert.deepEqual((await f.session.listRoutines()).routines, []);
});
