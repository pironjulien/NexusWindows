// OCR en secours d'un arbre trop pauvre (Tk, canevas, jeu) et capture Windows.Graphics.Capture d'une image PrintWindow
// vide (3.6.0) : lignes ramenées en pixels de l'image et ciblables au point + screenshotId, image déjà prise relue sans
// recapture, arbre riche sans OCR, image vide dite indisponible (jamais une copie de l'écran). Fournisseurs factices
// (moteur OCR et capture WGC réels du moteur natif : tests/WindowTests.cs, xUnit).
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { WindowsSession, summarize } from '../lib/session.mjs';
import { EmergencyStop } from '../lib/stop.mjs';

function sky() {
  const window = { id: 10, app: 'test!App', title: 'Tk' };
  return {
    target: 'windows', windows: [window],
    async list_windows() { return this.windows; },
    async get_window({ id }) { return this.windows.find(x => x.id === id); },
    async get_window_state({ window: w }) { return { window: w, accessibility: { tree: '0 window Tk' }, screenshots: [] }; },
  };
}
async function setup(t, native) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'nexus-codex-ocr-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const session = new WindowsSession(sky(), { stateRoot: root, native: { async withForeground(_, a) { return a(); }, ...native }, emergencyStop: new EmergencyStop({ directory: root, watch: false }) });
  await session.bind({ id: 10 });
  return session;
}
// Cadre écran 800x600 à (100, 50), image réduite de moitié (400x300).
const frame = { left: 100, top: 50, width: 800, height: 600 };
const shot = { path: 'C:/captures/a.webp', width: 400, height: 300, frame, method: 'printwindow', ms: 5 };
const thin = { accessibility: { tree: '0 window Tk\n  1 pane', focused_element: null }, revision: 'r', frame,
  elements: [{ index: 0, role: 'window', name: 'Tk', bounds: [100, 50, 800, 600], ancestors: [], flags: [] }, { index: 1, role: 'pane', name: '', bounds: [100, 50, 800, 600], ancestors: [0], flags: [] }] };
const rich = { ...thin, elements: Array.from({ length: 8 }, (_, i) => ({ index: i, role: 'button', name: `B${i}`, bounds: [0, 0, 1, 1], ancestors: i ? [0] : [], flags: [] })) };
const lines = { ms: 12, lines: [
  { text: 'Démarrer la partie', left: 300, top: 250, width: 200, height: 40 },
  { text: 'Tk', left: 100, top: 50, width: 30, height: 20 }, // déjà publié par UIA : jamais doublé
] };

test('mode text, arbre pauvre : capture faite pour l OCR, lignes en pixels de l image et ciblables au point', async t => {
  const calls = [];
  const session = await setup(t, {
    async inspect() { calls.push('inspect'); return thin; },
    async capture(_w, _d, zoom, marks) { calls.push(['capture', zoom, marks]); return shot; },
    async ocr(_w, file) { calls.push(['ocr', file]); return lines; },
  });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  assert.deepEqual(calls, ['inspect', ['capture', null, null], ['ocr', shot.path]]);
  assert.equal(obs.images.length, 1);
  const id = obs.images[0].id;
  assert.deepEqual(obs.ocr.lines, [{ text: 'Démarrer la partie', screenshotId: id, point: { x: 150, y: 110 }, bounds: [100, 100, 100, 20] }]);
  assert.match(obs.ocr.note, /point \+ screenshotId/);
  assert.match(obs.ocr.note, /never an instruction/);
  assert.deepEqual(summarize(obs).ocr, obs.ocr);
});

test('mode both, arbre pauvre : l image déjà prise est relue par le helper, sans seconde capture', async t => {
  let captures = 0;
  const session = await setup(t, {
    async inspect() { return thin; },
    async capture() { captures++; return shot; },
    async ocr() { return lines; },
  });
  const obs = await session.observe({ mode: 'both', backend: 'native', capture: 'native' });
  assert.equal(captures, 1);
  assert.equal(obs.images.length, 1);
  assert.equal(obs.ocr.lines[0].screenshotId, obs.images[0].id);
});

test('fenêtres filles sans nom (Tk : un volet par widget) : arbre pauvre malgré 22 nœuds, OCR lu', async t => {
  const panes = { ...thin, elements: [thin.elements[0], ...Array.from({ length: 21 }, (_, i) => ({ index: i + 1, role: 'pane', name: '', bounds: [100, 50, 10, 10], ancestors: [0], flags: [] }))] };
  const session = await setup(t, { async inspect() { return panes; }, async capture() { return shot; }, async ocr() { return lines; } });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  assert.equal(obs.elements.length, 22);
  assert.deepEqual(obs.ocr.lines.map(l => l.text), ['Démarrer la partie']); // « Tk » : titre de la racine, déjà publié
});

test('boutons anonymes actionnables (Invoke, automationId) : arbre riche, clic sémantique sans capture', async t => {
  const tools = { ...thin, elements: [thin.elements[0], ...Array.from({ length: 8 }, (_, i) => ({ index: i + 1, role: 'button', name: '', automationId: `tool${i + 1}`, invoke: true, actions: [], bounds: [100 + i * 20, 50, 20, 20], ancestors: [0], flags: [] }))] };
  let captures = 0;
  const sent = [];
  const session = await setup(t, {
    async inspect() { return tools; },
    async capture() { captures++; throw new Error('capture unavailable'); },
    async ocr() { return lines; },
    async call(_w, op, args) { sent.push(op); return { technique: 'background:uia', effect: 'invoked' }; },
  });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  assert.equal(obs.ocr, undefined);
  await session.act(obs, { kind: 'click', selector: { automationId: 'tool1' }, fallback: false });
  assert.equal(captures, 0);
  assert.deepEqual(sent, ['click']);
});

test('boutons anonymes cachés ou hors du cadre : ne rendent pas riche le canevas visible (look et geste)', async t => {
  const hidden = { ...thin, elements: [...thin.elements, ...Array.from({ length: 6 }, (_, i) => ({ index: i + 2, role: 'button', name: '', automationId: `tool${i}`, invoke: true, actions: [],
    bounds: i % 2 ? [2000, 2000, 20, 20] : [150, 60, 20, 20], ancestors: [0], flags: i % 2 ? [] : ['offscreen'] }))] };
  const sent = [];
  const session = await setup(t, {
    async inspect() { return hidden; },
    async capture() { return shot; },
    async ocr() { return send; },
    async call(_w, op, args) { sent.push(op); return { effect: 'sent' }; },
  });
  const obs = await session.observe({ mode: 'both', backend: 'native', capture: 'native' });
  assert.deepEqual(obs.ocr.lines.map(l => l.text), ['Envoyer', 'Nouvelle partie']);
  await assert.rejects(session.act(obs, { kind: 'click', point: { x: 10, y: 10 }, screenshotId: obs.images[0].id }), error => error.confirmationRequired === true);
  assert.deepEqual(sent, []);
});

test('arbre Sky (jamais de rectangles) riche : ni capture ni OCR, clic sémantique envoyé même capture indisponible', async t => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'nexus-codex-ocr-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const provider = sky();
  provider.get_window_state = async ({ window: w }) => ({ window: w, screenshots: [],
    accessibility: { tree: ['0 window Tk', ...Array.from({ length: 6 }, (_, i) => `  ${i + 1} button B${i + 1} ID: tool${i + 1}`)].join('\n') } });
  let captures = 0;
  const sent = [];
  const native = { async withForeground(_, a) { return a(); }, async capture() { captures++; throw new Error('capture unavailable'); }, async ocr() { return send; },
    async call(_w, op) { sent.push(op); return { technique: 'background:uia', effect: 'invoked' }; } };
  const session = new WindowsSession(provider, { stateRoot: root, native, emergencyStop: new EmergencyStop({ directory: root, watch: false }) });
  await session.bind({ id: 10 });
  const obs = await session.observe({ mode: 'text', backend: 'sky' });
  assert.equal(obs.source, 'sky');
  assert.equal(obs.ocr, undefined);
  await session.act(obs, { kind: 'click', selector: { automationId: 'tool1' }, fallback: false });
  assert.equal(captures, 0);
  assert.deepEqual(sent, ['click']);
});

test('lecture native : six boutons anonymes SANS rectangle ne rendent pas le canevas riche (provenance, pas absence de rectangles)', async t => {
  const unlocated = { ...thin, elements: [thin.elements[0], ...Array.from({ length: 6 }, (_, i) => ({ index: i + 1, role: 'button', name: '', automationId: `tool${i + 1}`, invoke: true, actions: [], bounds: [], ancestors: [0], flags: [] }))] };
  const sent = [];
  const session = await setup(t, { async inspect() { return unlocated; }, async capture() { return shot; }, async ocr() { return send; },
    async call(_w, op) { sent.push(op); return { effect: 'sent' }; } });
  const obs = await session.observe({ mode: 'both', backend: 'native', capture: 'native' });
  assert.equal(obs.ocr.lines.length, 2);
  await assert.rejects(session.act(obs, { kind: 'click', point: { x: 10, y: 10 }, screenshotId: obs.images[0].id }), error => error.confirmationRequired === true);
  assert.deepEqual(sent, []);
});

test('arbre riche ou mode image : aucun OCR', async t => {
  let read = 0;
  const session = await setup(t, {
    async inspect() { return rich; },
    async capture() { return shot; },
    async ocr() { read++; return lines; },
  });
  const text = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  assert.equal(text.ocr, undefined);
  assert.equal(text.images.length, 0);
  await session.observe({ mode: 'image', backend: 'native', capture: 'native' });
  assert.equal(read, 0);
});

test('OCR indisponible ou en échec : le look reste servi, la raison est dite', async t => {
  const session = await setup(t, {
    async inspect() { return thin; },
    async capture() { return shot; },
    async ocr() { return { lines: [], ms: 0, unavailable: 'no OCR language installed in Windows' }; },
  });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  assert.deepEqual(obs.ocr.lines, []);
  assert.match(obs.ocr.unavailable, /no OCR language/);
  const failing = await setup(t, { async inspect() { return thin; }, async capture() { return shot; }, async ocr() { throw new Error('worker exited'); } });
  assert.match((await failing.observe({ mode: 'both', backend: 'native', capture: 'native' })).ocr.unavailable, /worker exited/);
});

test('image vide après PrintWindow et WGC, fenêtre listée par Sky : la capture Sky prend le relais, comme avant', async t => {
  const session = await setup(t, {
    async inspect() { return thin; },
    async capture() { return { path: null, width: 0, height: 0, frame, blank: true, wgcError: 'uniform image too' }; },
    async ocr() { throw new Error('never called'); },
  });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  assert.equal(obs.images.length, 0);
  assert.match(obs.imageFallback, /Windows\.Graphics\.Capture returned a uniform \(blank\) image \(Windows\.Graphics\.Capture: uniform image too\): Sky's screenshot is used instead/);
  assert.equal(obs.ocr.lines.length, 0);
});

test('image vide, fenêtre native seule (serveur MCP autonome) : image indisponible, jamais une copie de l écran', async t => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'nexus-codex-ocr-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const native = {
    async listWindows() { return { windows: [{ id: 20, app: 'process:C:/game.exe', title: 'Jeu', pid: 7 }] }; },
    async alive() { return { state: 'alive' }; },
    async inspect() { return thin; },
    async capture() { return { path: null, width: 0, height: 0, frame, blank: true }; },
    async ocr() { throw new Error('never called'); },
  };
  const { nativeProvider } = await import('../lib/native-provider.mjs');
  const session = new WindowsSession(nativeProvider(native), { stateRoot: root, native, emergencyStop: new EmergencyStop({ directory: root, watch: false }) });
  await session.bind({ id: 20, app: 'process:C:/game.exe' });
  const obs = await session.observe({ mode: 'both', backend: 'native', capture: 'native' });
  assert.equal(obs.images.length, 0);
  assert.deepEqual(obs.screenshots, []);
  assert.match(obs.imageFallback, /image unavailable, never a screen copy, which would show the window on top/);
});

// Relecture de Codex du 27/10 (P1, deux tours) : sur une fenêtre à l'arbre pauvre, aucun rectangle sûr ne délimite un
// bouton ; le texte lu par OCR À L'INSTANT du geste (capture neuve) compte entier, comme les boutons de la fenêtre.
const send = { ms: 3, lines: [{ text: 'Envoyer', left: 300, top: 250, width: 120, height: 30 }, { text: 'Nouvelle partie', left: 300, top: 400, width: 160, height: 30 }] };
const calm = { ms: 3, lines: [{ text: 'Nouvelle partie', left: 300, top: 400, width: 160, height: 30 }] };
function canvas(t, read, sent = []) {
  let reads = 0;
  return setup(t, {
    async inspect() { return thin; },
    async capture() { return shot; },
    async ocr() { reads++; return typeof read === 'function' ? read(reads) : read; },
    async call(_w, op, args) { sent.push([op, args]); return { technique: 'background:messages', effect: 'sent' }; },
  });
}
test('« Envoyer » lu sur un canevas : tout clic au point est refusé sans accord, au texte comme loin de lui', async t => {
  const sent = [];
  const session = await canvas(t, send, sent);
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  const [envoyer] = obs.ocr.lines;
  await assert.rejects(session.act(obs, { kind: 'click', point: envoyer.point, screenshotId: envoyer.screenshotId }),
    error => error.confirmationRequired === true && /Envoyer/.test(error.summary));
  // Bouton de canevas large (scénario de Codex) : point dans le bouton, loin du texte lu.
  const far = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  await assert.rejects(session.act(far, { kind: 'click', point: { x: 25, y: 108 }, screenshotId: far.images[0].id }), error => error.confirmationRequired === true);
  assert.deepEqual(sent, []);
});

test('fenêtre sans texte sortant : clic au point envoyé ; image seule (aucun OCR au look) : le texte est lu au geste', async t => {
  const sent = [];
  const free = await canvas(t, calm, sent);
  const obs = await free.observe({ mode: 'text', backend: 'native', capture: 'native' });
  await free.act(obs, { kind: 'click', point: obs.ocr.lines[0].point, screenshotId: obs.ocr.lines[0].screenshotId });
  assert.equal(sent.length, 1);
  assert.equal(sent[0][0], 'point');
  const guarded = await canvas(t, send, []);
  const image = await guarded.observe({ mode: 'image', backend: 'native', capture: 'native' });
  assert.equal(image.ocr, undefined);
  await assert.rejects(guarded.act(image, { kind: 'click', point: { x: 10, y: 10 }, screenshotId: image.images[0].id }), error => error.confirmationRequired === true);
});

test('un « Envoyer » UIA ailleurs ne masque pas l « Envoyer » lu sur le canevas (dédoublonnage d affichage seulement)', async t => {
  const homonym = { ...thin, elements: [...thin.elements, { index: 2, role: 'text', name: 'Envoyer', bounds: [700, 600, 50, 20], ancestors: [0], flags: [] }] };
  const sent = [];
  const session = await setup(t, {
    async inspect() { return homonym; },
    async capture() { return shot; },
    async ocr() { return send; },
    async call(_w, op, args) { sent.push([op, args]); return { effect: 'sent' }; },
  });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  assert.deepEqual(obs.ocr.lines.map(l => l.text), ['Nouvelle partie']);
  await assert.rejects(session.act(obs, { kind: 'click', point: { x: 160, y: 135 }, screenshotId: obs.images[0].id }), error => error.confirmationRequired === true);
  assert.deepEqual(sent, []);
});

test('accord : même action et même texte, d un look à l autre ; texte du canevas changé : accord annulé', async t => {
  const sent = [];
  let message = 'Bonjour';
  const session = await canvas(t, () => ({ ms: 3, lines: [...send.lines, { text: `dest: Alice; message: ${message}`, left: 300, top: 300, width: 300, height: 30 }] }), sent);
  const first = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  const point = { kind: 'click', point: first.ocr.lines[0].point, screenshotId: first.ocr.lines[0].screenshotId };
  let token;
  await assert.rejects(session.act(first, point), error => { token = error.accord; return /Alice; message: Bonjour/.test(error.summary); });
  // Nouveau look (autre screenshotId), même texte : l'accord vaut, le clic part.
  const second = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  await session.act(second, { ...point, screenshotId: second.ocr.lines[0].screenshotId, confirm: true, accord: token });
  assert.equal(sent.length, 1);
  // Accord neuf, puis le canevas change entre le look et le geste : relu à l'instant, l'accord ne vaut plus.
  const third = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  const again = { ...point, screenshotId: third.ocr.lines[0].screenshotId };
  await assert.rejects(session.act(third, again), error => { token = error.accord; return true; });
  const fourth = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  message = 'Autre';
  await assert.rejects(session.act(fourth, { ...again, screenshotId: fourth.ocr.lines[0].screenshotId, confirm: true, accord: token }),
    error => /changed since the accord \(content\)/.test(error.message));
  assert.equal(sent.length, 1);
});

test('contenu OCR long : lié en entier à l accord (une fin différente l annule)', async t => {
  const sent = [];
  let last = 'Alice';
  const filler = Array.from({ length: 30 }, (_, i) => ({ text: `ligne ${i} ${'x'.repeat(50)}`, left: 300, top: 300 + i, width: 300, height: 10 }));
  const session = await canvas(t, () => ({ ms: 3, lines: [...send.lines, ...filler, { text: `destinataire ${last}`, left: 300, top: 900, width: 300, height: 10 }] }), sent);
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  const point = { kind: 'click', point: obs.ocr.lines[0].point, screenshotId: obs.ocr.lines[0].screenshotId };
  let token;
  await assert.rejects(session.act(obs, point), error => { token = error.accord; return true; });
  const next = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  last = 'Bob';
  await assert.rejects(session.act(next, { ...point, screenshotId: next.ocr.lines[0].screenshotId, confirm: true, accord: token }), /changed since the accord/);
  assert.deepEqual(sent, []);
});

// Troisième tour (relecture de Codex du 27/10) : lecture fraîche impossible → refus ; clic par sélecteur jugé aussi ;
// lignes de déclencheur liées en entier.
test('lecture fraîche impossible au geste (OCR en panne, capture en échec ou vide) : refus, jamais l OCR du look', async t => {
  for (const [what, failure] of [
    ['ocr', { ocr: () => ({ lines: [], unavailable: 'OCR failed: busy', missing: false }) }],
    ['capture', { capture: () => { throw new Error('PrintWindow failed'); } }],
    ['blank', { capture: () => ({ path: null, width: 0, height: 0, frame, blank: true }) }],
  ]) {
    const sent = [];
    let looked = false;
    const session = await setup(t, {
      async inspect() { return thin; },
      async capture(...a) { if (looked && failure.capture) return failure.capture(...a); return shot; },
      async ocr(...a) { if (looked && failure.ocr) return failure.ocr(...a); return calm; },
      async call(_w, op, args) { sent.push([op, args]); return { effect: 'sent' }; },
    });
    const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
    looked = true;
    await assert.rejects(session.act(obs, { kind: 'click', point: obs.ocr.lines[0].point, screenshotId: obs.images[0].id }),
      error => error.beforeInput === true && /could not be read again/.test(error.message), what);
    assert.deepEqual(sent, [], what);
  }
});

test('OCR absent du système (aucune langue : missing) : aucun texte connu, comportement d avant la 3.6.0', async t => {
  const sent = [];
  const session = await setup(t, {
    async inspect() { return thin; },
    async capture() { return shot; },
    async ocr() { return { lines: [], ms: 0, unavailable: 'no OCR language installed in Windows', missing: true }; },
    async call(_w, op, args) { sent.push([op, args]); return { effect: 'sent' }; },
  });
  const obs = await session.observe({ mode: 'image', backend: 'native', capture: 'native' });
  await session.act(obs, { kind: 'click', point: { x: 10, y: 10 }, screenshotId: obs.images[0].id });
  assert.equal(sent.length, 1);
});

test('clic par sélecteur sur une fenêtre à l arbre pauvre : « Envoyer » lu par OCR jugé, contenu visible lié', async t => {
  const anonymous = { ...thin, elements: [...thin.elements, { index: 2, role: 'button', name: '', automationId: 'action1', invoke: true, bounds: [300, 250, 120, 30], ancestors: [0], flags: [] }] };
  const sent = [];
  let who = 'Alice';
  const session = await setup(t, {
    async inspect() { return anonymous; },
    async capture() { return shot; },
    async ocr() { return { ms: 3, lines: [...send.lines, { text: `dest: ${who}`, left: 300, top: 300, width: 200, height: 30 }] }; },
    async call(_w, op, args) { sent.push([op, args]); return { technique: 'background:uia', effect: 'invoked' }; },
  });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  const click = { kind: 'click', selector: { automationId: 'action1' } };
  let token;
  await assert.rejects(session.act(obs, click), error => { token = error.accord; return error.confirmationRequired === true && /dest: Alice/.test(error.summary); });
  const next = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  who = 'Bob';
  await assert.rejects(session.act(next, { ...click, confirm: true, accord: token }), /changed since the accord/);
  assert.deepEqual(sent, []);
});

test('ligne OCR qui porte le mot sortant : liée en entier (une fin changée au-delà de 120 caractères annule l accord)', async t => {
  const sent = [];
  let tail = 'Alice';
  const session = await canvas(t, () => ({ ms: 3, lines: [{ text: `Message: merci d envoyer ${'x'.repeat(130)} ${tail}`, left: 300, top: 250, width: 900, height: 30 }] }), sent);
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  const point = { kind: 'click', point: obs.ocr.lines[0].point, screenshotId: obs.ocr.lines[0].screenshotId };
  let token;
  await assert.rejects(session.act(obs, point), error => { token = error.accord; return /Alice/.test(error.summary); });
  const next = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  tail = 'Bob';
  await assert.rejects(session.act(next, { ...point, screenshotId: next.ocr.lines[0].screenshotId, confirm: true, accord: token }), /changed since the accord/);
  assert.deepEqual(sent, []);
});

test('touche Entrée sur une fenêtre lue par OCR : « Envoyer » visible compte comme un bouton', async t => {
  const session = await setup(t, {
    async inspect() { return thin; },
    async capture() { return shot; },
    async ocr() { return send; },
    async call() { throw new Error('nothing may be sent'); },
  });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  await assert.rejects(session.act(obs, { kind: 'key', key: 'enter' }), error => error.confirmationRequired === true && /OCR/.test(error.message));
});

test('âge de l observation : compté depuis la lecture de l arbre, jamais remis à zéro par l OCR (P2)', async t => {
  const session = await setup(t, {
    async inspect() { return thin; },
    async capture() { return shot; },
    async ocr() { await new Promise(r => setTimeout(r, 250)); return lines; },
  });
  const obs = await session.observe({ mode: 'text', backend: 'native', capture: 'native' });
  assert.ok(Date.now() - obs.capturedAt >= 240, `âge ${Date.now() - obs.capturedAt} ms`);
});

test('point d une ligne OCR : vise l écran au centre de la ligne (même conversion que tout point natif)', async () => {
  const { nativeScreen } = await import('../lib/garde-fous.mjs');
  // ligne écran 300..500 x 250..290 → image 100..200 x 100..120, centre (150, 110) → écran (401, 271), dans la ligne.
  const at = nativeScreen({ width: 400, height: 300, frame }, { x: 150, y: 110 });
  assert.ok(at.x >= 300 && at.x < 500 && at.y >= 250 && at.y < 290, JSON.stringify(at));
});
