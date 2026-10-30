// Validation réelle (tester-computer-use.ps1 -LiveInstallation, test.ps1 -Full). Le test crée ses propres fenêtres
// (fixture WinForms, Win32 natif, WPF), affichées sans activation au fond de l'empilement, et ne touche qu'à elles.
// Chaque action d'arrière-plan doit réussir avec effet relu ET compté par l'application, sans vol du premier plan ni
// déplacement du curseur. Ce qui prend le premier plan n'est lancé que si Julien est inactif depuis 120 s ; sinon le
// sous-test est « sauté », jamais compté réussi. Les fenêtres, captures et preuves sont retirées à la fin.
// Présence de Julien (règle commune du banc, incident du 25/10 : une fenêtre du labo au premier plan pendant qu'il
// travaillait) : aucune fenêtre n'est ouverte sans 120 s d'inactivité (NEXUS_LIVE_WAIT_S : attente permise, 0 par
// défaut) ; son absence est revérifiée avant chaque sous-test, et dès qu'il revient les fenêtres sont fermées aussitôt
// et le reste est « sauté ».
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm, stat, mkdir, writeFile } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { WindowsSession } from '../../lib/session.mjs';
import { NativeUIA } from '../../lib/native.mjs';
import { EmergencyStop } from '../../lib/stop.mjs';
import { startBenchForm } from './bench-forms.mjs';
import { EMBEDDED_POLICY } from '../../lib/garde-fous.mjs';

const executable = process.env.NEXUS_LIVE_EXE;
const fixture = process.env.NEXUS_LIVE_FIXTURE;
const reports = process.env.NEXUS_LIVE_REPORTS;
const IDLE_REQUIRED_MS = 120000;
const WAIT_MS = Math.max(0, Number(process.env.NEXUS_LIVE_WAIT_S ?? 0)) * 1000;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

// Fournisseur « Sky » de laboratoire : inventaire et identité des fenêtres de la fixture par le helper, aucun arbre (la
// session passe donc par UIA natif), une capture seulement décrite (`shot` : rectangle de la fenêtre, pour viser en
// coordonnées) et AUCUNE entrée physique : toute tentative est comptée et échoue.
class LabSky {
  target = 'windows'; physical = []; shot = null;
  constructor(native, pid) { this.native = native; this.pids = [pid]; }
  async list_windows() {
    const all = [];
    for (const pid of this.pids) all.push(...(await this.native.listWindows(pid)).windows.map(({ id, app, title }) => ({ id, app, title })));
    return all;
  }
  async get_window({ id, app }) {
    const found = (await this.list_windows()).find(w => w.id === id && (app === undefined || w.app === app));
    if (!found) throw new Error('Window is gone');
    return found;
  }
  async get_window_state({ window, include_screenshot }) { return { window, accessibility: null, screenshots: include_screenshot && this.shot ? [this.shot] : [] }; }
  #refuse(kind) { this.physical.push(kind); throw new Error('Physical Sky input is not available in the live laboratory'); }
  async click() { this.#refuse('click'); }
  async type_text() { this.#refuse('type_text'); }
  async press_key() { this.#refuse('press_key'); }
  async set_value() { this.#refuse('set_value'); }
  async scroll() { this.#refuse('scroll'); }
  async drag() { this.#refuse('drag'); }
  async perform_secondary_action() { this.#refuse('perform_secondary_action'); }
  async activate_window() { this.#refuse('activate_window'); }
}

test('live background validation on self-created covered windows', { skip: !executable || !fixture ? 'NEXUS_LIVE_EXE / NEXUS_LIVE_FIXTURE absents' : false, timeout: WAIT_MS + 240000 }, async t => {
  const probeNative = new NativeUIA(executable);
  let idleAtStart = (await probeNative.probe()).idleMs; // avant toute action : aucune injection de ce test
  for (const until = Date.now() + WAIT_MS; idleAtStart < IDLE_REQUIRED_MS && Date.now() < until;) {
    await delay(Math.min(5000, IDLE_REQUIRED_MS - idleAtStart));
    idleAtStart = (await probeNative.probe()).idleMs;
  }
  if (idleAtStart < IDLE_REQUIRED_MS) {
    probeNative.close();
    t.skip(`sauté : Julien actif (inactif depuis ${Math.round(idleAtStart / 1000)} s ; 120 s requises avant d'ouvrir une fenêtre)`);
    return;
  }
  const work = await mkdtemp(path.join(os.tmpdir(), 'nexus-codex-live-'));
  const evidence = path.join(work, 'evidence');
  const child = spawn(fixture, [evidence], { windowsHide: false, stdio: ['pipe', 'ignore', 'ignore'] });
  const exited = new Promise(resolve => child.once('close', resolve));
  const stop = new EmergencyStop({ directory: process.env.NEXUS_SHARED_HOME ?? path.join(work, 'shared') });
  const measures = [];
  let session, labNative, sessionNative, samplerNative, sampling = false, sampler;
  // Formulaires du banc hébergés par powershell.exe (WinForms et WPF .NET Framework), lancés en parallèle.
  const benchForms = Promise.allSettled([startBenchForm('winforms'), startBenchForm('wpf')]);
  let winformsBench, wpfBench;
  // Fermeture des fenêtres, une seule fois : au retour de Julien (aussitôt) ou à la fin.
  let windowsClosed = null;
  const closeWindows = () => windowsClosed ??= (async () => {
    for (const form of (await benchForms).filter(r => r.status === 'fulfilled').map(r => r.value)) await form.close().catch(() => {});
    child.stdin.end();
    const timer = setTimeout(() => child.kill(), 5000);
    await exited; clearTimeout(timer);
  })();
  // Sous-test seulement si Julien n'a rien touché depuis le contrôle précédent (ces scénarios n'injectent aucune entrée).
  let checkedAt = Date.now(), returned = null;
  const step = async (name, ...args) => {
    const fn = args.pop();
    const options = args[0] ?? {};
    if (!returned) {
      const idle = (await probeNative.probe()).idleMs;
      if (idle < Date.now() - checkedAt) returned = `arrêté : Julien est revenu (entrée il y a ${Math.round(idle / 1000)} s), fenêtres fermées`;
      checkedAt = Date.now();
    }
    if (returned) { await closeWindows(); return t.test(name, { skip: returned }, () => {}); }
    return t.test(name, options, fn);
  };
  try {
    const [wf, wp] = await benchForms;
    if (wf.status === 'rejected') throw wf.reason;
    if (wp.status === 'rejected') throw wp.reason;
    winformsBench = wf.value; wpfBench = wp.value;
    let ready;
    for (let i = 0; i < 150 && !ready; i++) {
      try { ready = JSON.parse(await readFile(path.join(evidence, 'ready.json'), 'utf8')); } catch { await delay(100); }
    }
    assert.ok(ready, 'fixture ready');
    const fixtureWindows = new Set([ready.winforms, ready.win32, ready.wpf]);
    const state = async () => JSON.parse(await readFile(path.join(evidence, 'state.json'), 'utf8').catch(() => '{}'));
    stop.startGuard(executable);
    for (let i = 0; i < 50 && !stop.presence.available; i++) await delay(100);
    labNative = new NativeUIA(executable);
    sessionNative = new NativeUIA(executable, { emergencyStop: stop });
    const sky = new LabSky(labNative, ready.pid);
    sky.pids.push(winformsBench.pid, wpfBench.pid);
    session = new WindowsSession(sky, { stateRoot: path.join(work, 'state'), native: sessionNative, emergencyStop: stop });
    // Vol de premier plan : la fenêtre au premier plan est échantillonnée toutes les 20 ms pendant tous les scénarios
    // d'arrière-plan ; elle ne doit jamais appartenir à un processus de test.
    const fixturePids = new Set([ready.pid, winformsBench.pid, wpfBench.pid]);
    const foreground = { samples: 0, hits: [] };
    samplerNative = new NativeUIA(executable);
    sampling = true;
    sampler = (async () => {
      while (sampling) {
        const p = await samplerNative.probe().catch(() => null);
        if (p) { foreground.samples++; if (fixturePids.has(p.foregroundPid)) foreground.hits.push(p.foreground); }
        await delay(20);
      }
    })();

    // Action d'arrière-plan vérifiée : postcondition, technique, premier plan et curseur intacts.
    const background = async (label, observation, request, { technique = /^background:/ } = {}) => {
      const before = await probeNative.probe();
      const result = await session.act(observation, request);
      const after = await probeNative.probe();
      assert.equal(result.status, 'verified', `${label}: ${result.status}`);
      assert.match(result.technique, technique, label);
      assert.equal(result.foregroundTaken, false, `${label}: foreground taken`);
      assert.equal(fixtureWindows.has(after.foreground) && !fixtureWindows.has(before.foreground), false, `${label}: a fixture window became foreground`);
      // Julien a pu bouger la souris pendant l'action : le curseur n'est jugé que s'il n'y a eu aucune entrée.
      const julienInput = after.idleMs < result.durationMs + 50;
      if (!julienInput) assert.deepEqual(after.cursor, before.cursor, `${label}: cursor moved`);
      measures.push({ label, technique: result.technique, mechanism: result.mechanism, durationMs: result.durationMs, cursorChecked: !julienInput });
      return result;
    };
    const text = (automationId, equals) => ({ selector: { automationId }, field: 'name', equals });

    await step('presence: the guard reports Raw Input (physical input only)', () => {
      assert.equal(stop.presence.source, 'raw-input');
    });

    await step('WinForms: Value, Toggle, Invoke, CB/LB_SETCURSEL in background', async () => {
      await session.bind({ id: ready.winforms });
      let obs = await session.observe();
      assert.equal(obs.source, 'native-uia');
      obs = (await background('WinForms setValue', obs, { kind: 'setValue', selector: { automationId: 'nom', role: 'edit' }, value: 'Bonjour é Ω',
        expect: { selector: { automationId: 'nom' }, field: 'value', equals: 'Bonjour é Ω' } })).observation;
      obs = (await background('WinForms checkbox', obs, { kind: 'click', selector: { automationId: 'option' }, expect: { selector: { automationId: 'option' }, field: 'checked', equals: true } })).observation;
      obs = (await background('WinForms button', obs, { kind: 'click', selector: { automationId: 'enregistrer' }, expect: text('etat', 'Enregistré 1') })).observation;
      obs = (await background('WinForms combo', obs, { kind: 'select', selector: { automationId: 'couleur' }, option: 'Vert', expect: text('etat', 'Couleur Vert') })).observation;
      await background('WinForms list', obs, { kind: 'select', selector: { automationId: 'fruits' }, option: 'Poire', expect: text('etat', 'Fruit Poire') });
      const s = await state();
      assert.equal(s['winforms.text'], 'Bonjour é Ω');
      assert.equal(s['winforms.checked'], true);
      assert.equal(s['winforms.checkChanged'], 1);
      assert.equal(s['winforms.saves'], 1);
      assert.equal(s['winforms.colorChanged'], 1);
      assert.equal(s['winforms.fruitChanged'], 1);
    });

    await step('hover: one WM_MOUSEMOVE posted to the WinForms button, counted by the application, no button, cursor still', async () => {
      await session.bind({ id: ready.winforms });
      const before = await probeNative.probe();
      const hovers = (await state())['winforms.saveHover'] ?? 0;
      const saves = (await state())['winforms.saves'] ?? 0;
      const result = await session.act(await session.observe(), { kind: 'hover', selector: { automationId: 'enregistrer', role: 'button' } });
      const after = await probeNative.probe();
      assert.equal(result.technique, 'background:messages');
      assert.equal(result.foregroundTaken, false);
      assert.equal(result.status, 'observed'); // aucune postcondition : jamais « verified »
      let counted = hovers;
      for (let i = 0; i < 20 && counted === hovers; i++) { await delay(50); counted = (await state())['winforms.saveHover'] ?? 0; }
      assert.ok(counted > hovers, 'the button received the mouse move');
      assert.equal((await state())['winforms.saves'] ?? 0, saves, 'a hover never clicks');
      if (after.idleMs >= result.durationMs + 50) assert.deepEqual(after.cursor, before.cursor, 'hover: cursor moved');
      measures.push({ label: 'WinForms hover', technique: result.technique, mechanism: result.mechanism, durationMs: result.durationMs });
    });

    await step('multi-window: an owned dialog is followed, then left when it closes', async () => {
      let obs = await session.observe();
      const opened = await background('WinForms dialog open', obs, { kind: 'click', selector: { automationId: 'dialogue' }, expect: { selector: { automationId: 'fermer' }, exists: true } });
      const dialog = (await state())['winforms.dialog'];
      assert.equal(opened.windowChange?.reason, 'opened');
      assert.equal(opened.observation.window.id, dialog);
      fixtureWindows.add(dialog);
      const closed = await background('WinForms dialog close', opened.observation, { kind: 'click', selector: { automationId: 'fermer' }, expect: { selector: { automationId: 'dialogue' }, exists: true } });
      assert.equal(closed.windowChange?.reason, 'closed');
      assert.equal(closed.observation.window.id, ready.winforms);
      const s = await state();
      assert.equal(s['winforms.dialogOpened'], 1);
      assert.equal(s['winforms.dialogClosed'], 1);
    });

    await step('Win32: BN_CLICKED, BM_SETCHECK, EM_REPLACESEL, CB/LB_SETCURSEL, WM_COMMAND menu', async () => {
      await session.bind({ id: ready.win32 });
      let obs = await session.observe();
      obs = (await background('Win32 button', obs, { kind: 'click', selector: { name: 'Valider', role: 'button' }, expect: text('106', 'Valider 1') }, { technique: /^background:messages$/ })).observation;
      obs = (await background('Win32 checkbox', obs, { kind: 'click', selector: { name: 'Activer', role: 'checkbox' }, expect: { selector: { automationId: '102' }, field: 'checked', equals: true } }, { technique: /^background:messages$/ })).observation;
      obs = (await background('Win32 setValue', obs, { kind: 'setValue', selector: { automationId: '103', role: 'edit' }, value: 'Texte 32', expect: { selector: { automationId: '103' }, field: 'value', equals: 'Texte 32' } }, { technique: /^background:messages$/ })).observation;
      obs = (await background('Win32 typeText', obs, { kind: 'typeText', focus: { automationId: '103', role: 'edit' }, text: ' suite', expect: { selector: { automationId: '103' }, field: 'value', equals: 'Texte 32 suite' } }, { technique: /^background:messages$/ })).observation;
      obs = (await background('Win32 combo', obs, { kind: 'select', selector: { automationId: '104', role: 'combobox' }, option: 'Bravo', expect: text('106', 'Choix Bravo') }, { technique: /^background:messages$/ })).observation;
      obs = (await background('Win32 list item', obs, { kind: 'click', selector: { name: 'Trois', role: 'listitem' }, expect: text('106', 'Liste Trois') }, { technique: /^background:messages$/ })).observation;
      obs = (await background('Win32 menu', obs, { kind: 'menu', path: 'Fichier > Compter', expect: text('106', 'Compter 1') }, { technique: /^background:messages$/ })).observation;
      await assert.rejects(session.act(obs, { kind: 'menu', path: 'Fichier > Désactivé' }), error => error.refused === true && /disabled/.test(error.message));
      const s = await state();
      assert.equal(s['win32.valider'], 1);
      assert.equal(s['win32.activer'], 1);
      assert.equal(s['win32.checked'], true);
      assert.ok(s['win32.editChanged'] >= 2);
      assert.equal(s['win32.comboChanged'], 1);
      assert.equal(s['win32.listChanged'], 1);
      assert.equal(s['win32.menu'], 1);
      assert.equal(s['win32.disabledMenu'], undefined);
    });

    await step('batch: stops at the first unmet step, the next step is never sent', async () => {
      const obs = await session.observe();
      const started = Date.now();
      const batch = await session.actBatch(obs, [
        { kind: 'click', selector: { name: 'Valider', role: 'button' }, expect: text('106', 'Valider 2') },
        { kind: 'setValue', selector: { automationId: '103', role: 'edit' }, value: 'x', timeoutMs: 300, expect: { selector: { automationId: '103' }, field: 'value', equals: 'impossible' } },
        { kind: 'click', selector: { name: 'Valider', role: 'button' }, expect: text('106', 'Valider 3') },
      ]);
      measures.push({ label: 'Win32 batch (2 of 3 steps sent)', technique: batch.steps.map(s => s.technique).join(' + '), durationMs: Date.now() - started });
      assert.equal(batch.status, 'stopped');
      assert.equal(batch.stoppedAt, 1);
      assert.deepEqual(batch.steps.map(s => s.status), ['verified', 'unmet']);
      assert.equal((await state())['win32.valider'], 2);
    });

    await step('capture: covered Win32 window, PrintWindow, not blank, without foreground', async () => {
      const before = await probeNative.probe();
      const obs = await session.observe({ mode: 'image', capture: 'native' });
      const after = await probeNative.probe();
      assert.equal(obs.imageFallback, null);
      assert.equal(obs.images.length, 1);
      const image = obs.images[0];
      assert.match(image.method, /^printwindow/);
      assert.ok(image.width > 100 && image.height > 100);
      assert.ok((await stat(image.path)).size > 1000);
      assert.ok(image.path.startsWith(path.join(work, 'state', 'captures')));
      assert.equal(fixtureWindows.has(after.foreground), false);
      assert.equal(after.foreground === before.foreground || !fixtureWindows.has(after.foreground), true);
      measures.push({ label: 'Capture covered Win32 window', technique: image.method, durationMs: image.ms });
    });

    await step('WPF: COM UI Automation with AutoSetFocus = FALSE', async () => {
      await session.bind({ id: ready.wpf });
      let obs = await session.observe();
      obs = (await background('WPF button', obs, { kind: 'click', selector: { automationId: 'valider-wpf' }, expect: text('etat-wpf', 'Validé 1') }, { technique: /^background:uia$/ })).observation;
      obs = (await background('WPF checkbox', obs, { kind: 'click', selector: { automationId: 'option-wpf' }, expect: { selector: { automationId: 'option-wpf' }, field: 'checked', equals: true } }, { technique: /^background:uia$/ })).observation;
      await background('WPF setValue', obs, { kind: 'setValue', selector: { automationId: 'editeur' }, value: 'WPF ok', expect: { selector: { automationId: 'editeur' }, field: 'value', equals: 'WPF ok' } }, { technique: /^background:uia$/ });
      const s = await state();
      assert.equal(s['wpf.clicks'], 1);
      assert.equal(s['wpf.checked'], 1);
      assert.equal(s['wpf.text'], 'WPF ok');
    });

    // Liste déroulante classique (.NET 8 WinForms et Win32 natif) : ouverte par CB_SHOWDROPDOWN, option choisie DANS la
    // liste ouverte (fermée d'abord, puis CB_SETCURSEL + CBN_SELCHANGE), liste relue fermée.
    await step('classic combo boxes: open the list, choose in the open list, the list is closed again', async () => {
      for (const [id, combo, option, statusId, expected, counter] of [
        [ready.winforms, { automationId: 'couleur' }, 'Bleu', 'etat', 'Couleur Bleu', 'winforms.colorChanged'],
        [ready.win32, { automationId: '104', role: 'combobox' }, 'Charlie', '106', 'Choix Charlie', 'win32.comboChanged'],
      ]) {
        await session.bind({ id });
        const before = (await state())[counter];
        let obs = await session.observe();
        obs = (await background('Classic combo open', obs, { kind: 'secondary', selector: combo, action: 'Expand', expect: { selector: combo, field: 'expanded', equals: true } }, { technique: /^background:messages$/ })).observation;
        assert.deepEqual(obs.windowStack, [id]); // la ComboLBox ouverte n'est pas suivie comme une fenêtre
        assert.ok(obs.popups.length >= 1);
        obs = (await background('Classic combo choose in open list', obs, { kind: 'click', selector: { name: option, role: 'listitem' }, expect: text(statusId, expected) }, { technique: /^background:messages$/ })).observation;
        const current = obs.elements.find(e => e.role === 'combobox' && (e.automationId === combo.automationId));
        assert.equal(current.expanded, false);
        assert.equal(current.value, option);
        assert.equal((await state())[counter], before + 1);
      }
    });

    // Banc réel : « ouvrir la liste puis choisir Bravo puis Valider », formulaires hébergés par powershell.exe.
    const bench = async (label, form, family) => {
      await session.bind({ id: form.hwnd });
      let obs = await session.observe();
      assert.equal(obs.source, 'native-uia');
      const field = n => family === 'wpf' ? { automationId: n, role: 'edit' } : { name: n, role: 'edit' };
      const formule = { name: 'Formule', role: 'combobox' };
      const resultat = { automationId: 'resultat' };
      const valider = { name: 'Valider', role: 'button' };
      const expected = 'Résultat : Nom=Julien;Email=julien@example.com;Ville=Paris;Accepter=oui;Formule=Bravo;Envois=1';
      const steps = [
        ...[['Nom', 'Julien'], ['Email', 'julien@example.com'], ['Ville', 'Paris']].map(([n, v]) => ({ kind: 'setValue', selector: field(n), value: v, expect: { selector: field(n), field: 'value', equals: v } })),
        { kind: 'click', selector: { name: 'Accepter', role: 'checkbox' }, expect: { selector: { name: 'Accepter', role: 'checkbox' }, field: 'checked', equals: true } },
        // WPF : d'abord un choix direct (liste fermée, dépliée puis repliée par le helper), puis le parcours du banc.
        ...(family === 'wpf' ? [{ kind: 'select', selector: formule, option: 'Alpha', expect: { selector: formule, field: 'value', equals: 'Alpha' } }] : []),
        { kind: 'secondary', selector: formule, action: 'Expand', expect: { selector: formule, field: 'expanded', equals: true } },
        { kind: 'click', selector: { name: 'Bravo', role: 'listitem' }, expect: { selector: formule, field: 'value', equals: 'Bravo' } },
        { kind: 'click', selector: valider, expect: { selector: resultat, field: 'name', equals: expected } },
      ];
      const before = await probeNative.probe();
      const started = Date.now();
      const batch = await session.actBatch(obs, steps);
      const elapsed = Date.now() - started;
      const after = await probeNative.probe();
      assert.equal(batch.status, 'completed', JSON.stringify(batch.steps));
      for (const s of batch.steps) {
        assert.match(s.technique, /^background:/, label + ' ' + s.kind);
        assert.equal(s.foregroundTaken, false, label + ' ' + s.kind);
        measures.push({ label: label + ' ' + steps[s.index].kind + (steps[s.index].selector?.name ?? steps[s.index].selector?.automationId ? ' ' + (steps[s.index].selector.name ?? steps[s.index].selector.automationId) : ''), technique: s.technique, durationMs: s.durationMs });
      }
      measures.push({ label: label + ' (lot complet)', technique: 'batch', durationMs: elapsed });
      if (after.idleMs >= elapsed + 50) assert.deepEqual(after.cursor, before.cursor, label + ': cursor moved');
      obs = batch.observation;
      const combo = obs.elements.find(e => e.role === 'combobox' && e.name === 'Formule');
      assert.equal(combo.value, 'Bravo');
      assert.equal(combo.expanded, false, label + ': list left open');
      assert.deepEqual(obs.popups, [], label + ': popup left open');
      assert.deepEqual(obs.windowStack, [form.hwnd]);
      assert.equal(obs.elements.find(e => e.automationId === 'resultat').name, expected);
      // Postcondition triviale (« le bouton existe ») : refusée avant toute entrée ; condition précise déjà vraie :
      // already-satisfied ; dans les deux cas rien n'est renvoyé (Envois=1).
      await assert.rejects(session.act(obs, { kind: 'click', selector: valider, expect: { selector: valider, exists: true } }), error => error.beforeInput === true);
      const again = await session.act(await session.observe(), { kind: 'click', selector: valider, expect: { selector: resultat, field: 'name', equals: expected } });
      assert.equal(again.status, 'already-satisfied');
      assert.equal((await session.observe()).elements.find(e => e.automationId === 'resultat').name, expected);
    };
    await step('bench WinForms hosted by powershell.exe: open the list, choose Bravo, validate (Formule=Bravo, Envois=1)', () => bench('Banc WinForms', winformsBench, 'winforms'));
    await step('bench WPF hosted by powershell.exe: choose directly, open the list, choose Bravo, validate (Formule=Bravo, Envois=1)', () => bench('Banc WPF', wpfBench, 'wpf'));

    await step('garde-fous: a console window is free; a policy-blocked app and a missing policy are refused by the helper', async () => {
      const listed = (await labNative.listWindows(winformsBench.pid)).windows;
      assert.ok(listed.some(w => w.id === winformsBench.hwnd));
      assert.ok(listed.some(w => w.id === winformsBench.console)); // libre depuis l'accord du 18/10
      const app = listed[0].app;
      assert.equal((await labNative.inspect({ id: winformsBench.console, app })).ok, true);
      // Vrai fichier de politique lu par le helper : powershell.exe ajouté à l'application de Codex lui-même → refus.
      const policy = JSON.parse(readFileSync(EMBEDDED_POLICY, 'utf8'));
      policy.blocage_dur.fenetre_autorisation_codex.processus.push('powershell.exe');
      const blockedFile = path.join(work, 'garde-fous-bloque.json');
      await writeFile(blockedFile, JSON.stringify(policy));
      const withPolicy = async (file, fn) => {
        const saved = process.env.NEXUS_GARDE_FOUS;
        process.env.NEXUS_GARDE_FOUS = file;
        const native = new NativeUIA(executable);
        try { return await fn(native); }
        finally { native.close(); if (saved === undefined) delete process.env.NEXUS_GARDE_FOUS; else process.env.NEXUS_GARDE_FOUS = saved; }
      };
      await withPolicy(blockedFile, async native => {
        await assert.rejects(native.inspect({ id: winformsBench.hwnd, app }), /shared garde-fous policy: application de Codex/);
        await assert.rejects(native.call({ id: winformsBench.hwnd, app }, 'click', { selector: { name: 'Valider' }, mode: 'background' }), error => error.refused === true);
        assert.equal((await native.listWindows(winformsBench.pid)).windows.length, 0);
      });
      await withPolicy(path.join(work, 'absente.json'), async native => {
        await assert.rejects(native.inspect({ id: winformsBench.hwnd, app }), /policy unreadable.*fail closed/);
      });
    });

    // Confirmation étanche : « Envoyer » exige confirm:true + accord, quel que soit le geste (clic par sélecteur ou en
    // coordonnées, Entrée vers le bouton par défaut, Ctrl+Entrée, Alt+E, saut de ligne tapé, menu) ; rien n'est envoyé
    // (compteurs de l'application intacts). Le jeton simule l'accord uniquement dans nos fixtures locales :
    // leurs boutons incrémentent un compteur, aucune communication réelle n'est possible.
    await step('garde-fous: every send gesture needs an exact accord; a fixture accord sends exactly once', async () => {
      const refusedHere = what => error => error.confirmationRequired === true || assert.fail(`${what}: ${error.message}`);
      const fixtureAccord = async request => {
        const obs = await session.observe();
        assert.ok([ready.winforms, ready.win32, ready.wpf].includes(obs.window.id), 'test accord only for this run\'s local fixtures');
        let accord;
        await assert.rejects(session.act(obs, { ...request, confirm: true }), error => {
          assert.equal(error.confirmationRequired, true);
          assert.equal(error.accordRequired, true);
          assert.equal(error.beforeInput, true);
          assert.match(error.accord, /^[0-9a-f]{8}$/);
          accord = error.accord;
          return true;
        });
        return { ...request, confirm: true, accord };
      };
      for (const [id, family, send, counter, field, statusId] of [
        [ready.winforms, 'WinForms', { automationId: 'envoyer' }, 'winforms.sends', { automationId: 'nom', role: 'edit' }, 'etat'],
        [ready.win32, 'Win32', { name: 'Envoyer', role: 'button' }, 'win32.sends', { automationId: '103', role: 'edit' }, '106'],
        [ready.wpf, 'WPF', { automationId: 'envoyer-wpf' }, 'wpf.sends', { automationId: 'editeur' }, 'etat-wpf'],
      ]) {
        await session.bind({ id });
        await assert.rejects(session.act(await session.observe(), { kind: 'click', selector: send }), refusedHere(`${family} click`));
        for (const key of ['Return', 'ctrl+Return', 'alt+e']) {
          await assert.rejects(session.act(await session.observe(), { kind: 'key', key }), refusedHere(`${family} ${key}`));
        }
        await assert.rejects(session.act(await session.observe(), { kind: 'typeText', focus: field, text: 'x\n' }), refusedHere(`${family} newline`));
        let obs = await session.observe();
        const [rx, ry, rw, rh] = obs.elements[0].bounds;
        const [bx, by, bw, bh] = obs.elements.find(e => Object.entries(send).every(([k, v]) => e[k] === v)).bounds;
        sky.shot = { id: `lab-${id}`, width: rw, height: rh, originX: rx, originY: ry };
        obs = await session.observe({ mode: 'both' });
        await assert.rejects(session.act(obs, { kind: 'click', screenshotId: sky.shot.id, point: { x: bx + bw / 2 - rx, y: by + bh / 2 - ry } }), refusedHere(`${family} coordinates`));
        sky.shot = null;
        assert.equal((await state())[counter], undefined, `${family}: nothing sent before confirm`);
        const request = await fixtureAccord({ kind: 'click', selector: send, expect: text(statusId, 'Envoyé 1') });
        assert.equal((await state())[counter], undefined, `${family}: confirm:true alone sent nothing`);
        await background(`${family} fixture send with exact accord`, await session.observe(), request);
        assert.equal((await state())[counter], 1);
      }
      await session.bind({ id: ready.win32 });
      await assert.rejects(session.act(await session.observe(), { kind: 'menu', path: 'Fichier > Envoyer' }), refusedHere('Win32 menu'));
      assert.equal((await state())['win32.menuSends'], undefined);
      const menuRequest = await fixtureAccord({ kind: 'menu', path: 'Fichier > Envoyer', expect: text('106', 'Menu envoyé 1') });
      assert.equal((await state())['win32.menuSends'], undefined, 'menu: confirm:true alone sent nothing');
      await background('Win32 fixture menu send with exact accord', await session.observe(), menuRequest, { technique: /^background:messages$/ });
      assert.equal((await state())['win32.menuSends'], 1);
    });

    await step('no fixture window ever took the foreground during the background scenarios', async () => {
      sampling = false; await sampler;
      measures.push({ label: 'Échantillons du premier plan (20 ms)', technique: `${foreground.hits.length} sur ${foreground.samples} sur une fenêtre de test`, durationMs: 0 });
      assert.ok(foreground.samples > 50);
      assert.deepEqual(foreground.hits, []);
    });

    await step('no physical Sky input was ever attempted by the background scenarios', () => {
      assert.deepEqual(sky.physical, []);
    });

    // Repli qui prend le premier plan : seulement si Julien est inactif depuis 120 s (mesuré avant le test et maintenant).
    const idleNow = (await probeNative.probe()).idleMs;
    const physicalAllowed = idleAtStart >= IDLE_REQUIRED_MS && stop.presence.idleMs() >= 5000;
    await step('physical native click: UIA button without Invoke moves on activation, exactly one target event', {
      skip: physicalAllowed ? false : 'sauté : pause de Julien insuffisante pour le repli physique',
    }, async () => {
      await session.bind({ id: ready.wpf });
      const selector = { automationId: 'pointer-wpf', role: 'button' };
      const expected = text('etat-wpf', 'Physique 1');
      let obs = await session.observe();
      const initial = obs.elements.find(e => e.automationId === selector.automationId);
      assert.ok(initial, 'the pointer-only button is exposed by native UIA');
      assert.equal(initial.invoke, false, 'the fixture must not expose Invoke');
      assert.equal((await state())['wpf.pointerMoved'], undefined, 'the fixture has not been activated');
      // Interdiction de repli : refus avant toute entree, et la cible n'a meme pas ete activee/deplacee.
      await assert.rejects(session.act(obs, { kind: 'click', selector, fallback: false, expect: expected }),
        error => error.beforeInput === true);
      assert.equal((await state())['wpf.pointerClicks'], undefined);
      assert.equal((await state())['wpf.pointerMoved'], undefined);
      obs = await session.observe();
      const before = await probeNative.probe();
      const result = await session.act(obs, { kind: 'click', selector, expect: expected });
      const after = await probeNative.probe();
      assert.equal(result.status, 'verified');
      assert.equal(result.technique, 'physical:native');
      assert.equal(result.foregroundTaken, true);
      assert.equal(result.attempts.filter(a => a.technique === 'physical:native').length, 1);
      const s = await state();
      assert.equal(s['wpf.pointerMoved'], true, 'activation moved the button before input');
      assert.equal(s['wpf.pointerClicks'], 1, 'the application received exactly one click');
      assert.equal(s['wpf.pointerDecoyClicks'], undefined, 'no click at the stale pre-activation centre');
      const fresh = result.observation.elements.find(e => e.automationId === selector.automationId);
      assert.ok(fresh.bounds[0] > initial.bounds[0] + initial.bounds[2], 'the target moved beyond its old rectangle');
      if (!result.julienInput) {
        assert.equal(result.foregroundReturned, 'restored');
        assert.equal(after.foreground, before.foreground);
        assert.deepEqual(after.cursor, before.cursor);
      }
      // La condition precise est deja vraie : aucune nouvelle entree physique ou semantique n'est autorisee.
      const again = await session.act(await session.observe(), { kind: 'click', selector, expect: expected });
      assert.equal(again.status, 'already-satisfied');
      assert.equal((await state())['wpf.pointerClicks'], 1);
      assert.deepEqual(sky.physical, [], 'NexusWindows handled the input without a separate Sky action');
      measures.push({ label: 'Pointer-only WPF button moved on activation (1 click, 0 decoy)', technique: result.technique,
        mechanism: result.mechanism, durationMs: result.durationMs, foregroundTaken: result.foregroundTaken,
        foregroundReturned: result.foregroundReturned, targetEvents: s['wpf.pointerClicks'], decoyEvents: s['wpf.pointerDecoyClicks'] ?? 0 });
    });
    await step('physical fallback (foreground UIA under the shared token)', {
      skip: physicalAllowed ? false : `sauté : Julien actif (inactif depuis ${Math.round(idleAtStart / 1000)} s au départ, ${Math.round(idleNow / 1000)} s maintenant ; 120 s requises)`,
    }, async st => {
      await session.bind({ id: ready.winforms });
      const obs = await session.observe();
      // Juge : le compteur de focus de la fixture, pas focused=true après coup (le premier plan rendu à Julien emporte le
      // focus clavier global ; avis de Codex du 24/10). Technique et restitution vérifiées à part.
      const focusBefore = (await state())['winforms.nameFocus'] ?? 0;
      const result = await session.act(obs, { kind: 'click', selector: { automationId: 'nom', role: 'edit' } });
      assert.equal(result.technique, 'foreground:uia');
      assert.equal(result.attempts[0].outcome, 'not-applicable');
      assert.equal(result.foregroundTaken, true);
      // Julien revenu pendant le geste : le premier plan lui est laissé (bon comportement), la restitution n'est pas jugée.
      if (result.foregroundReturned === 'kept-by-julien' && (await probeNative.probe()).idleMs < result.durationMs + 1000) {
        st.skip('Julien est revenu pendant le repli : premier plan laissé à Julien, restitution non jugée');
        return;
      }
      assert.equal(result.foregroundReturned, 'restored');
      assert.ok(((await state())['winforms.nameFocus'] ?? 0) > focusBefore, 'the field received the focus');
      assert.notEqual(result.status, 'verified'); // aucune postcondition demandée : jamais un faux « vérifié »
      measures.push({ label: 'Foreground fallback (SetFocus)', technique: result.technique, durationMs: result.durationMs, foregroundTaken: result.foregroundTaken });
    });

  } finally {
    sampling = false; await sampler?.catch(() => {});
    await session?.close().catch(() => {});
    labNative?.close(); sessionNative?.close(); samplerNative?.close(); probeNative.close();
    stop.close();
    await closeWindows();
    await rm(work, { recursive: true, force: true });
    if (reports && measures.length) {
      await mkdir(reports, { recursive: true });
      await writeFile(path.join(reports, `live-background-${new Date().toISOString().replace(/[:.]/g, '-')}.json`), JSON.stringify({ schema: 1, idleAtStartMs: idleAtStart, measures }, null, 2));
    }
    for (const m of measures) console.log(`MESURE ${m.label} | ${m.technique} | ${m.mechanism ?? ''} | ${m.durationMs} ms${m.cursorChecked === false ? ' | curseur non jugé (entrée de Julien)' : ''}`);
  }
});
