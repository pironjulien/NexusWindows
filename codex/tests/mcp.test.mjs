// Serveur MCP autonome : protocole (initialize, tools/list, tools/call, erreurs) et fournisseur natif sans Sky.
// Session et helper factices : aucune entrée de bureau réelle.
import test from 'node:test';
import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';
import os from 'node:os';
import path from 'node:path';
import { writeFile, rm } from 'node:fs/promises';
import { serve, TOOLS, WindowsTools, PROTOCOL_VERSIONS, MODERN_PROTOCOL_VERSIONS, LEGACY_PROTOCOL_VERSIONS, OUTPUT_SCHEMAS, schemaErrors } from '../lib/mcp.mjs';
import { nativeProvider, NoSkyError } from '../lib/native-provider.mjs';

async function exchange(tools, messages) {
  const input = new PassThrough(), output = new PassThrough();
  let text = '';
  output.setEncoding('utf8'); output.on('data', c => { text += c; });
  const done = serve({ tools, version: '9.9.9', input, output });
  for (const m of messages) input.write(JSON.stringify({ jsonrpc: '2.0', ...m }) + '\n');
  input.end();
  await done;
  return text.trim().split('\n').map(line => JSON.parse(line));
}

test('initialize negotiates the version and every tool carries annotations', async () => {
  const [init, list] = await exchange({}, [
    { id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } },
    { method: 'notifications/initialized' },
    { id: 2, method: 'tools/list' },
  ]);
  assert.equal(init.result.protocolVersion, '2025-06-18');
  assert.equal(init.result.serverInfo.version, '9.9.9');
  assert.equal(list.result.tools.length, TOOLS.length);
  for (const tool of list.result.tools) {
    assert.ok(tool.annotations?.title, tool.name);
    assert.equal(typeof tool.annotations.readOnlyHint, 'boolean', tool.name);
    assert.equal(typeof tool.annotations.destructiveHint, 'boolean', tool.name);
    assert.equal(tool.inputSchema.type, 'object', tool.name);
  }
  const [other] = await exchange({}, [{ id: 3, method: 'initialize', params: { protocolVersion: '1999-01-01' } }]);
  assert.equal(other.result.protocolVersion, LEGACY_PROTOCOL_VERSIONS[0]);
  assert.equal(other.result.resultType, undefined);
});

test('protocol 2026-07-28: discover, stateless requests, resultType, serverInfo and version errors', async () => {
  const meta = version => ({ _meta: { 'io.modelcontextprotocol/protocolVersion': version, 'io.modelcontextprotocol/clientCapabilities': {} } });
  const tools = { async call() { return [{ type: 'text', text: 'ok' }]; } };
  const [discover, list, call, ping, init, wrong] = await exchange(tools, [
    { id: 1, method: 'server/discover' },
    { id: 2, method: 'tools/list', params: meta('2026-07-28') },
    { id: 3, method: 'tools/call', params: { name: 'windows', arguments: {}, ...meta('2026-07-28') } },
    { id: 4, method: 'ping', params: meta('2026-07-28') },
    { id: 5, method: 'initialize', params: { protocolVersion: '2026-07-28', ...meta('2026-07-28') } },
    { id: 6, method: 'tools/list', params: meta('2099-01-01') },
  ]);
  assert.deepEqual(discover.result.supportedVersions, MODERN_PROTOCOL_VERSIONS);
  assert.ok(discover.result.capabilities.tools);
  assert.equal(discover.result.resultType, 'complete');
  assert.equal(discover.result.ttlMs, 0);
  assert.equal(discover.result.cacheScope, 'private');
  assert.equal(discover.result._meta['io.modelcontextprotocol/serverInfo'].version, '9.9.9');
  // Aucun initialize avant : la requête est servie telle quelle.
  assert.equal(list.result.tools.length, TOOLS.length);
  assert.equal(list.result.resultType, 'complete');
  assert.equal(list.result.ttlMs, 0);
  assert.equal(list.result.cacheScope, 'private');
  assert.equal(list.result._meta['io.modelcontextprotocol/serverInfo'].name, 'nexus-windows-codex');
  assert.equal(call.result.content[0].text, 'ok');
  assert.equal(call.result.resultType, 'complete');
  assert.equal(ping.error.code, -32601);
  assert.equal(init.error.code, -32022);
  assert.equal(wrong.error.code, -32022);
  assert.deepEqual(wrong.error.data, { supported: MODERN_PROTOCOL_VERSIONS, requested: '2099-01-01' });
  // Un client d'avant ne voit aucun champ 2026 sur ses listes.
  const [legacyList] = await exchange({}, [{ id: 7, method: 'tools/list' }]);
  assert.equal(legacyList.result.resultType, undefined);
  assert.equal(legacyList.result.ttlMs, undefined);
  assert.equal(PROTOCOL_VERSIONS[0], '2026-07-28');
});

test('protocol 2026-07-28: an incomplete envelope reaches no tool, a notification gets no reply', async () => {
  let called = 0;
  const tools = { async call() { called++; return [{ type: 'text', text: 'ok' }]; } };
  const version = { 'io.modelcontextprotocol/protocolVersion': '2026-07-28' };
  const replies = await exchange(tools, [
    { id: 1, method: 'tools/call', params: { name: 'act', arguments: { steps: [{ kind: 'key', key: 'enter' }] }, _meta: version } },
    { id: 2, method: 'tools/list', params: { _meta: { ...version, 'io.modelcontextprotocol/clientCapabilities': [] } } },
    { method: 'server/discover', params: { _meta: { ...version, 'io.modelcontextprotocol/clientCapabilities': {} } } },
    { id: 3, method: 'tools/call', params: { name: 'windows', arguments: {}, _meta: { ...version, 'io.modelcontextprotocol/clientCapabilities': {} } } },
  ]);
  assert.equal(called, 1);
  assert.deepEqual(replies.map(r => r.id), [1, 2, 3]);
  assert.equal(replies[0].error.code, -32602);
  assert.equal(replies[1].error.code, -32602);
  assert.equal(replies[2].result.resultType, 'complete');
});

test('protocol 2026-07-28: images are always attached and a region takes the image its caller names', async () => {
  const logo = path.join(import.meta.dirname, '..', 'native', 'logo.png');
  let shots = 0, width = 1000;
  const zooms = [];
  const window = { id: 10, app: 'process:C:/a.exe', title: 'A' };
  const session = {
    async windows() { return [window]; },
    async bind(w) { return w; },
    async observe(options) {
      zooms.push(options.zoom);
      const frame = { left: 0, top: 0, width: width === 2000 ? 4000 : 1000, height: width === 2000 ? 1000 : 500 };
      return { token: 't', window, elements: [], source: 'native-uia', accessibility: { tree: '' },
        images: [{ id: `native-${++shots}`, path: logo, width, height: 500, frame, ...(options.zoom ? { zoom: true } : {}) }] };
    },
  };
  const tools = new WindowsTools(session);
  const modern = { modern: true };
  const images = content => content.filter(c => c.type === 'image').length;
  // A puis B regardent la même fenêtre, mêmes pixels : B reçoit l'image aussi.
  assert.equal(images(await tools.call('look', { window: 10, mode: 'both' }, modern)), 1);
  assert.equal(images(await tools.call('look', { window: 10, mode: 'both' }, modern)), 1);
  // Repère : A nomme native-2 ; B regarde après un redimensionnement (native-3) ; native-2 est alors refusée.
  await assert.rejects(tools.call('look', { window: 10, region: [10, 10, 20, 20] }, modern), /region needs image/);
  width = 2000;
  await tools.call('look', { window: 10, mode: 'both' }, modern);
  await assert.rejects(tools.call('look', { window: 10, region: [10, 10, 20, 20], image: 'native-2' }, modern), /not the latest full image/);
  zooms.length = 0;
  await tools.call('look', { window: 10, region: [10, 10, 20, 20], image: 'native-3' }, modern);
  assert.deepEqual(zooms, [[20, 20, 22, 22]]);
});

test('protocol 2026-07-28: act serves only the observation it names; another look makes it stale', async () => {
  const windows = [{ id: 10, app: 'process:C:/a.exe', title: 'A' }, { id: 20, app: 'process:C:/b.exe', title: 'B' }];
  const acted = [];
  let bound = null;
  const session = {
    async windows() { return windows; },
    async bind(w) { bound = w; return w; },
    async observe() { return { token: `token-${bound.id}`, window: bound, elements: [], images: [], source: 'native-uia', accessibility: { tree: '' } }; },
    async act(obs, action) { acted.push([obs.window.id, action.kind]); return { status: 'verified', observation: null }; },
  };
  const tools = new WindowsTools(session);
  const modern = { modern: true };
  const idOf = content => content.map(c => c.text ?? '').join('\n').match(/observation (obs-\S+)/)?.[1];
  // Conversation A regarde 10, conversation B regarde 20, puis A agit avec SON observation : refusée, rien envoyé.
  const a = idOf(await tools.call('look', { window: 10 }, modern));
  const b = idOf(await tools.call('look', { window: 20 }, modern));
  assert.ok(a && b && a !== b);
  await assert.rejects(tools.call('act', { observation: a, steps: [{ kind: 'key', key: 'enter' }] }, modern), /no longer current/);
  // Sans identifiant : refusé en 2026-07-28 ; look et window sans fenêtre nommée aussi.
  await assert.rejects(tools.call('act', { steps: [{ kind: 'key', key: 'enter' }] }, modern), /observation id/);
  await assert.rejects(tools.call('look', {}, modern), /Name the window/);
  await assert.rejects(tools.call('look', { region: [0, 0, 10, 10] }, modern), /Name the window/);
  await assert.rejects(tools.call('window', { action: 'close' }, modern), /Name the window/);
  assert.deepEqual(acted, []);
  // L'observation courante, nommée, passe ; elle ne sert qu'une fois.
  const c = idOf(await tools.call('look', { window: 20 }, modern));
  await tools.call('act', { observation: c, steps: [{ kind: 'key', key: 'enter' }] }, modern);
  assert.deepEqual(acted, [[20, 'key']]);
  await assert.rejects(tools.call('act', { observation: c, steps: [{ kind: 'key', key: 'enter' }] }, modern), /Look at the window first/);
});

test('tool failures come back as isError results, unknown methods and tools as JSON-RPC errors', async () => {
  const tools = { async call() { throw new Error('Windows 10 or 11 is required.'); } };
  const [call, unknownTool, unknownMethod, ping] = await exchange(tools, [
    { id: 1, method: 'tools/call', params: { name: 'windows', arguments: {} } },
    { id: 2, method: 'tools/call', params: { name: 'format_disk' } },
    { id: 3, method: 'resources/list' },
    { id: 4, method: 'ping' },
  ]);
  assert.equal(call.result.isError, true);
  assert.match(call.result.content[0].text, /Windows 10 or 11/);
  assert.equal(unknownTool.error.code, -32602);
  assert.equal(unknownMethod.error.code, -32601);
  assert.deepEqual(ping.result, {});
});

test('look chooses one window, act needs a fresh look and reports marks', async () => {
  const observation = { token: 't', window: { id: 1, title: 'Notes', app: 'process:C:/notes.exe' }, elements: [], images: [], source: 'native-uia', accessibility: { tree: '0 window Notes' } };
  const calls = [];
  const session = {
    async windows() { return [{ id: 1, app: 'process:C:/notes.exe', title: 'Notes' }, { id: 2, app: 'process:C:/notes.exe', title: 'Notes 2' }]; },
    async bind(w) { calls.push(['bind', w.id]); return w; },
    async observe(options) { calls.push(['observe', options]); return observation; },
    async act(obs, action) { calls.push(['act', obs === observation, action.kind]); return { status: 'verified', technique: 'background:uia', observation: null }; },
    async actBatch() { const error = new Error('Step 1 refused'); error.batch = { status: 'stopped', steps: [{ kind: 'click', status: 'verified' }, { kind: 'key', status: 'refused', error: 'needs confirm' }] }; throw error; },
  };
  const tools = new WindowsTools(session);
  await assert.rejects(tools.call('act', { steps: [{ kind: 'click' }] }), /Look at the window first/);
  await assert.rejects(tools.call('look', { window: 'note' }), /2 windows match/);
  const seen = await tools.call('look', { window: 'Notes' });
  assert.match(seen[0].text, /^window 1 "Notes"/);
  assert.deepEqual(calls.slice(0, 2), [['bind', 1], ['observe', { mode: 'text', backend: 'native', capture: 'native', zoom: null }]]);
  const [report] = await tools.call('act', { steps: [{ kind: 'click' }] });
  assert.match(report.text, /^✓ click — verified \(background:uia\)/);
  await assert.rejects(tools.call('act', { steps: [{ kind: 'click' }] }), /Look at the window first/);
  await tools.call('look', {});
  const [batch] = await tools.call('act', { steps: [{ kind: 'click' }, { kind: 'key' }] });
  assert.match(batch.text, /✓ click — verified\n✗ key — refused: needs confirm/);
});

test('act interaction: the foreground lease lives inside one call, selectors with expect only, observation dropped after it', async () => {
  // Passe 2 du 28/10 : beginInteraction absent du serveur MCP, 16 appels par la bibliothèque Node. Le bail reste borné à
  // l'appel (jamais tenu pendant que le modèle réfléchit) et rendu même après un refus.
  const observation = { token: 't', window: { id: 1, title: 'Fichiers', app: 'process:C:/explorer.exe' }, elements: [], images: [], source: 'native-uia', accessibility: { tree: '0 window Fichiers' } };
  const held = { ...observation, token: 'held' };
  const calls = [];
  const session = {
    async windows() { return [{ id: 1, app: 'process:C:/explorer.exe', title: 'Fichiers' }]; },
    async bind(w) { return w; },
    async observe() { return observation; },
    // La session reçoit l'observation de look (celle sur laquelle les étapes sont écrites), jamais une autre.
    async actInteraction(obs, steps) { calls.push(['interaction', obs === observation, steps.map(s => s.kind)]); return { status: 'completed', steps: steps.map(s => ({ kind: s.kind, status: 'verified' })), observation: held, interaction: { status: 'ended', foregroundReturned: 'restored' } }; },
    async act() { calls.push('act'); return { status: 'verified', observation: null }; },
  };
  const tools = new WindowsTools(session);
  const steps = [
    { kind: 'click', button: 'right', selector: { role: 'listitem', name: 'note' }, expect: { selector: { role: 'menuitem', name: 'Propriétés' }, exists: true } },
    { kind: 'click', selector: { role: 'menuitem', name: 'Propriétés' }, expect: { selector: { role: 'window', name: 'Propriétés' }, exists: true } },
  ];
  await tools.call('look', { window: 1 });
  const [report] = await tools.call('act', { interaction: true, steps });
  assert.deepEqual(calls, [['interaction', true, ['click', 'click']]]);
  assert.match(report.text, /✓ click — verified\n✓ click — verified\nforeground interaction ended \(foreground restored\)\nNo fresh observation/);
  await assert.rejects(tools.call('act', { steps: [{ kind: 'key', key: 'enter' }] }), /Look at the window first/);
  // Points d'une image d'avant le bail, ou étape sans preuve : refus avant tout bail.
  calls.length = 0;
  await tools.call('look', { window: 1 });
  await assert.rejects(tools.call('act', { interaction: true, steps: [{ kind: 'click', point: { x: 1, y: 2 }, screenshotId: 'native-x-1', expect: steps[1].expect }] }), /selectors only .*; nothing was sent/);
  await tools.call('look', { window: 1 });
  await assert.rejects(tools.call('act', { interaction: true, steps: [{ kind: 'key', key: 'F2' }] }), /expect on every step; nothing was sent/);
  assert.deepEqual(calls, []);
  // Étape refusée pendant le bail : le bail est rendu quand même.
  session.actInteraction = async () => { calls.push('interaction'); throw Object.assign(new Error('stop'), { batch: { status: 'stopped', steps: [{ kind: 'click', status: 'refused', error: 'needs confirm' }] }, interaction: { status: 'ended' } }); };
  await tools.call('look', { window: 1 });
  const [refused] = await tools.call('act', { interaction: true, steps: [steps[1]] });
  assert.deepEqual(calls, ['interaction']);
  assert.match(refused.text, /✗ click — refused: needs confirm\nforeground interaction ended/);
  // Le schéma publié accepte l'option.
  assert.deepEqual(schemaErrors(TOOLS.find(t => t.name === 'act').inputSchema, { interaction: true, steps }), []);
});

test('the native provider lists helper windows as native-only and never falls back to Sky', async () => {
  const activated = [];
  const native = {
    async listWindows(pid) { assert.equal(pid, 0); return { windows: [{ id: 5, app: 'process:C:/x.exe', title: 'X', pid: 42 }] }; },
    async call(window, op) { activated.push([window.id, op]); },
  };
  const provider = nativeProvider(native);
  assert.equal(provider.target, 'windows');
  const [w] = await provider.list_windows();
  assert.deepEqual(w, { id: 5, app: 'process:C:/x.exe', title: 'X', pid: 42, nativeOnly: true });
  assert.deepEqual(await provider.get_window({ id: 5, app: 'process:C:/x.exe' }), w);
  await assert.rejects(provider.get_window({ id: 6 }), /no longer listed/);
  await provider.activate_window({ window: w });
  assert.deepEqual(activated, [[5, 'activate']]);
  await assert.rejects(provider.click({}), error => error instanceof NoSkyError && error.beforeInput && error.refused);
});

test('an open dialog is listed with its owner: by title it is chosen, by app name the main window comes first', async () => {
  const native = {
    async listWindows() {
      return { windows: [
        { id: 7, app: 'process:C:/chrome.exe', title: 'Formulaire - Google Chrome', pid: 42 },
        { id: 8, app: 'process:C:/chrome.exe', title: 'Ouvrir', pid: 42, owner: 7, ownerTitle: 'Formulaire - Google Chrome' },
      ] };
    },
  };
  const listed = await nativeProvider(native).list_windows();
  assert.deepEqual(listed[1], { id: 8, app: 'process:C:/chrome.exe', title: 'Ouvrir', pid: 42, nativeOnly: true, owner: 7,
    dialogOf: { id: 7, title: 'Formulaire - Google Chrome' } });
  const binds = [];
  const session = {
    async windows() { return listed; },
    async bind(w) { binds.push(w.id); return w; },
    async observe() { return { window: { id: binds.at(-1) }, elements: [], images: [], source: 'native-uia', accessibility: { tree: '' } }; },
  };
  const tools = new WindowsTools(session);
  await tools.call('look', { window: 'ouvrir' });
  await tools.call('look', { window: 'chrome' });
  assert.deepEqual(binds, [8, 7]);
});

test('a named window is bound again even when the session followed a dialog, and close goes to that window', async () => {
  const binds = [], closed = [];
  let current = null;
  const session = {
    async windows() { return [{ id: 10, app: 'process:C:/app.exe', title: 'Main' }]; },
    async bind(w) { binds.push(w.id); current = w.id; return { ...w }; },
    async observe() { current = 20; return { window: { id: 20, title: 'Dialog', app: 'process:C:/app.exe' }, elements: [], images: [], source: 'native-uia' }; },
    async closeWindow() { closed.push(current); return { status: 'verified', state: 'closed', prompts: [] }; },
  };
  const tools = new WindowsTools(session);
  await tools.call('look', { window: 10 }); // la session suit le dialogue 20
  const { content: [line] } = await tools.call('window', { action: 'close', window: 10 });
  assert.deepEqual(binds, [10, 10]);
  assert.deepEqual(closed, [10]);
  assert.match(line.text, /^✓ close — closed/);
});

test('arguments are checked against the schema before any action: an unknown window action never closes', async () => {
  const calls = [];
  const tools = { async call(name, args) { calls.push([name, args]); return [{ type: 'text', text: 'done' }]; } };
  const [maximize, missing, extra, badStep, ok] = await exchange(tools, [
    { id: 1, method: 'tools/call', params: { name: 'window', arguments: { action: 'explode', window: 10 } } },
    { id: 2, method: 'tools/call', params: { name: 'window', arguments: { window: 10 } } },
    { id: 3, method: 'tools/call', params: { name: 'look', arguments: { window: 1, zoom: 2 } } },
    { id: 4, method: 'tools/call', params: { name: 'act', arguments: { steps: [{ kind: 'click', expect: { selector: { name: 'OK' }, field: 'exists' } }] } } },
    { id: 5, method: 'tools/call', params: { name: 'window', arguments: { action: 'close', window: 10 } } },
  ]);
  for (const r of [maximize, missing, extra, badStep]) {
    assert.equal(r.result.isError, true);
    assert.match(r.result.content[0].text, /^✗ \w+ — invalid arguments, nothing was sent/);
  }
  assert.deepEqual(calls, [['window', { action: 'close', window: 10 }]]);
  assert.equal(ok.result.content[0].text, 'done');
  // Appel direct (hors serveur) : l'action est relue aussi par l'outil lui-même.
  await assert.rejects(new WindowsTools({}).call('window', { action: 'explode', window: 10 }), /Unsupported window action.*nothing was sent/);
});

test('malformed requests get -32600 instead of silence', async () => {
  const replies = await exchange({}, [{ id: 7, method: null }, { id: 8, method: 42 }, { id: 9 }]);
  assert.deepEqual(replies.map(r => [r.id, r.error?.code]), [[7, -32600], [8, -32600], [9, -32600]]);
  // Sans jsonrpc "2.0" : refus, jamais exécuté.
  const input = new PassThrough(), output = new PassThrough(); let out = '';
  output.setEncoding('utf8'); output.on('data', c => { out += c; });
  const done = serve({ tools: { async call() { throw new Error('must not run'); } }, version: '1', input, output });
  input.end(JSON.stringify({ id: 10, method: 'tools/call', params: { name: 'windows', arguments: {} } }) + '\n'); await done;
  assert.equal(JSON.parse(out).error.code, -32600);
});

test('launch drops the previous observation and binds a hidden Settings page for look', async () => {
  const observation = { window: { id: 1, title: 'Notes', app: 'process:C:/notes.exe' }, elements: [], images: [], source: 'native-uia' };
  const observed = [];
  const session = {
    async windows() { return [{ id: 1, app: 'process:C:/notes.exe', title: 'Notes' }]; },
    async bind(w) { return w; },
    async observe() { observed.push('observe'); return observation; },
    async launch() { return { status: 'opened', window: null, hidden: true, bindable: 'bindOpenedSettings', nativeWindow: 20 }; },
    async bindOpenedSettings() { return { id: 20, app: 'process:C:/SystemSettings.exe', title: 'Settings', nativeOnly: true, pid: 7 }; },
    async returnToRoot(id) { observed.push(['root', id]); return { id, app: 'process:C:/SystemSettings.exe', title: 'Settings', nativeOnly: true, pid: 7 }; },
  };
  const tools = new WindowsTools(session);
  await tools.call('look', { window: 1 });
  const { content: [, detail], structuredContent } = await tools.call('launch', { app: 'ms-settings:display' });
  assert.equal(structuredContent.bound, 20);
  assert.match(detail.text, /"bound": 20/);
  await assert.rejects(tools.call('act', { steps: [{ kind: 'click' }] }), /Look at the window first/);
  await tools.call('look', { window: 20 }); // absente de l'inventaire, mais liée par launch
  assert.deepEqual(observed, ['observe', ['root', 20], 'observe']);
});

test('act with refresh image returns the refreshed capture', async () => {
  const dir = await import('node:fs/promises').then(fs => fs.mkdtemp(path.join(os.tmpdir(), 'nwc-')));
  const png = path.join(dir, 'after.png');
  await writeFile(png, Buffer.from([0x89, 0x50, 0x4e, 0x47]));
  const before = { window: { id: 1, title: 'Notes', app: 'process:C:/notes.exe' }, elements: [], images: [], source: 'native-uia' };
  const after = { ...before, images: [{ path: png }] };
  const session = {
    async windows() { return [{ id: 1, app: 'process:C:/notes.exe', title: 'Notes' }]; },
    async bind(w) { return w; },
    async observe() { return before; },
    async act() { return { status: 'verified', observation: after }; },
  };
  const tools = new WindowsTools(session);
  await tools.call('look', { window: 1 });
  const content = await tools.call('act', { steps: [{ kind: 'click', refresh: 'image' }] });
  assert.ok(content.some(c => c.type === 'image' && c.mimeType === 'image/png'));
  await rm(dir, { recursive: true, force: true });
});

test('a hidden Settings root named again is brought back from a followed dialog before close', async () => {
  let current = 10;
  const closed = [];
  const session = {
    async windows() { return []; },
    async launch() { return { status: 'opened', hidden: true, bindable: 'bindOpenedSettings', nativeWindow: 10 }; },
    async bindOpenedSettings() { current = 10; return { id: 10, app: 'process:C:/SystemSettings.exe', nativeOnly: true, pid: 42 }; },
    async returnToRoot(id) { current = id; return { id, app: 'process:C:/SystemSettings.exe', nativeOnly: true, pid: 42 }; },
    async observe() { return { window: { id: current, title: 'Settings', app: 'process:C:/SystemSettings.exe' }, elements: [], images: [], source: 'native-uia' }; },
    async act() { current = 20; return { status: 'verified', observation: null }; }, // dialogue 20 suivi par la session
    async closeWindow() { closed.push(current); return { status: 'verified', state: 'closed', prompts: [] }; },
  };
  const tools = new WindowsTools(session);
  await tools.call('launch', { app: 'ms-settings:display' });
  await tools.call('look', { window: 10 });
  await tools.call('act', { steps: [{ kind: 'click' }] });
  await tools.call('window', { action: 'close', window: 10 });
  assert.deepEqual(closed, [10]);
});

test('windows, launch and window publish an outputSchema and join structuredContent beside the unchanged text', async () => {
  const listed = [{ id: 1, app: 'process:C:/notes.exe', title: 'Notes', pid: 4, nativeOnly: true },
    { id: 2, app: 'process:C:/chrome.exe', title: 'Open', pid: 5, nativeOnly: true, owner: 3, dialogOf: { id: 3, title: 'Chrome' } }];
  const launched = { status: 'opened', windows: [{ id: 7, proof: 'process' }], candidates: [], foregroundReturned: true };
  const session = {
    async windows() { return listed; },
    async bind(w) { return w; },
    async launch() { return launched; },
    async closeWindow() { return { status: 'prompted', state: 'open', prompts: [{ id: 9, title: 'Save?' }] }; },
  };
  const byName = Object.fromEntries(TOOLS.map(t => [t.name, t]));
  for (const name of ['windows', 'launch', 'window']) assert.equal(byName[name].outputSchema, OUTPUT_SCHEMAS[name], name);
  for (const name of ['look', 'act']) assert.equal(byName[name].outputSchema, undefined, name);
  const [windows, launch, close] = await exchange(new WindowsTools(session), [
    { id: 1, method: 'tools/call', params: { name: 'windows', arguments: { filter: 'chrome' } } },
    { id: 2, method: 'tools/call', params: { name: 'launch', arguments: { app: 'notepad' } } },
    { id: 3, method: 'tools/call', params: { name: 'window', arguments: { action: 'close', window: 1 } } },
  ]);
  // Texte inchangé : la même sérialisation qu'avant la sortie structurée.
  assert.equal(windows.result.content[0].text, JSON.stringify([listed[1]], null, 1));
  assert.deepEqual(windows.result.structuredContent, { windows: [listed[1]] });
  assert.equal(launch.result.content[0].text, '✓ launch — opened');
  assert.equal(launch.result.content[1].text, JSON.stringify(launched, null, 1));
  assert.deepEqual(launch.result.structuredContent, launched);
  assert.equal(close.result.content[0].text, '~ close — open');
  assert.equal(close.result.structuredContent.prompts[0].title, 'Save?');
  for (const [name, reply] of [['windows', windows], ['launch', launch], ['window', close]]) {
    assert.deepEqual(schemaErrors(OUTPUT_SCHEMAS[name], reply.result.structuredContent, 'structuredContent'), [], name);
  }
});
