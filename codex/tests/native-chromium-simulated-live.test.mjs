// Explicit opt-in: one installed signed Edge/InPrivate or Chrome/Incognito local fixture, no DevTools/CDP.
// Each substrate is reported separately. This never qualifies Electron or hides the Edge publication limit.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { EventEmitter } from 'node:events';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import readline from 'node:readline';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { signedEdgeProof, signedChromeProof, chromiumWindowIdentity as edgeWindowIdentity, chromiumWindowInventory as edgeWindowInventory, privateChromiumWindowProof } from './fixtures/signed-edge-window.mjs';
import { assertRenderedRichText } from './fixtures/claude-cdp-rendered-oracle.mjs';
import { fixtureReadOnlyChild, legacyEdgeSnapshot } from './fixtures/legacy-edge-snapshot.mjs';
import { physicalDesktopProbe, assertCursorKept } from './fixtures/physical-desktop-probe.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const exe = process.env.NEXUS_NATIVE_TEST_EXE, edge = process.env.NEXUS_SIMULATED_LIVE_EDGE, chrome = process.env.NEXUS_SIMULATED_LIVE_CHROME;
if (edge && chrome) throw new Error('Select exactly one signed Chromium substrate for this single-launch qualification');
const browser = chrome ? 'chrome' : 'edge', executable = chrome ?? edge;
const browserName = chrome ? 'Google Chrome' : 'Microsoft Edge', privateSwitch = chrome ? '--incognito' : '--inprivate';
const signedBrowserProof = chrome ? signedChromeProof : signedEdgeProof;
const comparisonOnly = process.env.NEXUS_LEGACY_EDGE_COMPARE_ONLY === '1';
const skip = process.platform !== 'win32' || !exe || !executable;
test(comparisonOnly ? `read-only historical comparison on one signed ${browserName} fixture without DevTools` : `native Claude on signed ${browserName} without DevTools: six text gestures and reactive Enter refusal`, { skip, timeout: 420000 }, async t => {
  const root = path.resolve(directory, '../..');
  const runId = randomUUID(), state = path.join(os.homedir(), '.nexus', 'nexus-windows', 'qualification', `${browser}-simulated-${runId}`);
  await mkdir(state, { recursive: false });
  const signature = await signedBrowserProof(executable), title = `Nexus simulated ${runId}`;
  await writeFile(path.join(state, `${browser}-signature.json`), JSON.stringify(signature, null, 2));
  const html = (await readFile(path.join(directory, 'fixtures', 'claude-simulated.html'), 'utf8')).replaceAll('__NEXUS_FIXTURE_RUN__', runId);
  let readyResolve, readyReject, url, owned, passed = false, closeAttempted = false, primaryError, domReady, readyDeadline;
  const preexisting = new Set(); let initialEdge = [], finalEdge = [], launchResult, inPrivateDisplayProof, launchCandidates = [], legacyComparison;
  const ready = new Promise((resolve, reject) => { readyResolve = resolve; readyReject = reject; }); ready.catch(() => {});
  const events = [], gestureProofs = [], signal = new EventEmitter();
  let checkpointSequence = 0, pageCheckpoint;
  const http = createServer(async (req, res) => {
    try {
    if (req.url === '/checkpoint' && req.method === 'GET') { res.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({ sequence: checkpointSequence })); return; }
    if (req.url === '/checkpoint' && req.method === 'POST') {
      let body = ''; for await (const chunk of req) body += chunk;
      const snapshot = JSON.parse(body);
      if (snapshot.run !== runId || snapshot.sequence !== checkpointSequence) { res.writeHead(400).end(); return; }
      pageCheckpoint = snapshot; signal.emit('checkpoint'); res.writeHead(204).end(); return;
    }
    if (req.url === '/ready' && req.method === 'POST') {
      let body = ''; for await (const chunk of req) body += chunk;
      const page = JSON.parse(body);
      if (page.run === runId && page.href === url && page.title === title) { domReady = page; await writeFile(path.join(state, 'fixture-dom-ready.json'), JSON.stringify(page)); readyResolve(page); res.writeHead(204).end(); }
      else res.writeHead(400).end(); return;
    }
    if (req.url === '/oracle' && req.method === 'POST') {
      let body = ''; for await (const chunk of req) body += chunk;
      events.push(JSON.parse(body)); signal.emit('oracle'); res.writeHead(204).end(); return;
    }
    if (req.url === '/') {
      res.writeHead(200, { 'content-type': 'text/html;charset=utf-8', 'content-security-policy': "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'" });
      res.end(html); return;
    }
    res.writeHead(404).end();
    } catch { res.destroy(); }
  });
  http.on('clientError', (_error, socket) => socket.destroy());
  http.listen(0, '127.0.0.1'); await once(http, 'listening'); url = `http://127.0.0.1:${http.address().port}/`;
  const child = spawn(exe, ['--mcp', '--agent', 'claude'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: {
    ...process.env, NEXUS_WINDOWS_HOME: state, NEXUS_SHARED_HOME: state, NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'), NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json'),
  } });
  const pending = new Map(); let id = 0, stderr = '', hwnd, failure;
  function failPending(error) {
    failure = error;
    for (const waiter of pending.values()) { clearTimeout(waiter.timer); waiter.reject(error); }
    pending.clear();
    readyReject(error);
  }
  child.on('error', error => failPending(error));
  child.stdin.on('error', error => failPending(error));
  child.on('exit', (code, reason) => failPending(new Error(`Fixture MCP exited (${code}, ${reason}): ${stderr}`)));
  const aborted = () => failPending(new Error('Fixture test aborted')); t.signal.addEventListener('abort', aborted, { once: true });
  child.stderr.on('data', data => { stderr += data; });
  readline.createInterface({ input: child.stdout }).on('line', line => {
    let value; try { value = JSON.parse(line); } catch { return; }
    const waiter = pending.get(value.id); if (waiter) { pending.delete(value.id); clearTimeout(waiter.timer); waiter.resolve(value); }
  });
  async function rpc(method, params = {}, cleanup = false) {
    if (failure) throw failure;
    if (t.signal.aborted && !cleanup) throw new Error('Fixture test aborted');
    const current = ++id;
    const wait = new Promise((resolve, reject) => pending.set(current, { resolve, reject, timer: setTimeout(() => { pending.delete(current); reject(new Error('MCP timeout: ' + stderr)); }, cleanup ? 5000 : 185000) }));
    try { child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method, params }) + '\n'); }
    catch (error) { failPending(error); }
    const reply = await wait; assert.ok(!reply.error, JSON.stringify(reply)); return reply.result;
  }
  async function call(name, args, cleanup = false) {
    const result = await rpc('tools/call', { name, arguments: args }, cleanup);
    assert.ok(!result.isError, `${name}: ${JSON.stringify(result)}\n${stderr}`); return result.structuredContent ?? result;
  }
  async function probe() {
    return await new Promise((resolve, reject) => {
      const process = spawn(exe, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); let output = '', error = '';
      const timer = setTimeout(() => { process.kill(); reject(new Error('Independent native probe timed out')); }, 10000);
      process.stdout.on('data', data => { output += data; }); process.stderr.on('data', data => { error += data; });
      process.stdin.on('error', reject); process.once('error', value => { clearTimeout(timer); reject(value); });
      process.once('close', code => { clearTimeout(timer); try { if (code) throw new Error(error || output); const physical = physicalDesktopProbe(); resolve({ ...JSON.parse(output), cursor: physical.cursor, display: physical.display, lastInput: physical.lastInput }); } catch (value) { reject(value); } });
      process.stdin.end(JSON.stringify({ operation: 'probe' }));
    });
  }
  async function waitOracle(offset, predicate) {
    if (events.slice(offset).some(predicate)) return;
    await new Promise((resolve, reject) => {
      const check = () => { if (events.slice(offset).some(predicate)) finish(); };
      const timer = setTimeout(() => finish(new Error('Independent DOM event was not received')), 1618);
      function finish(error) { signal.off('oracle', check); clearTimeout(timer); error ? reject(error) : resolve(); }
      signal.on('oracle', check); check();
    });
  }
  async function checkpoint() {
    const sequence = ++checkpointSequence;
    await new Promise((resolve, reject) => {
      const check = () => { if (pageCheckpoint?.sequence === sequence) finish(); };
      const timer = setTimeout(() => finish(new Error('The independent read-only page checkpoint was not received')), 1618);
      function finish(error) { signal.off('checkpoint', check); clearTimeout(timer); error ? reject(error) : resolve(); }
      signal.on('checkpoint', check); check();
    });
    return pageCheckpoint;
  }
  async function act(step, target, value) {
    const eventOffset = events.length;
    await ownLook(); const before = await probe();
    assert.notEqual(before.foreground, hwnd, 'The fixture must remain behind the user');
    const result = await call('act', { hwnd, steps: [step], then: 'none' }); const after = await probe();
    assert.equal(after.foreground, before.foreground, 'simulated typing changed foreground'); assertCursorKept(before, after);
    assert.equal(result.status, 'success', JSON.stringify(result));
    assert.ok(JSON.stringify(result.steps).includes('simulated-activation'), 'The actual background simulated transport must be used');
    assert.ok(!JSON.stringify(result.steps).toLowerCase().includes('devtools'), 'This fixture has no DevTools route');
    await waitOracle(eventOffset, e => e.kind === 'input' && e.target === target && e.trusted && (target === 'editor'
      ? e.richSnapshot?.renderedLines?.map(line => line.text).join('\n') === value
      : e.value.replace(/\r\n/g, '\n') === value));
    const actual = events.slice(eventOffset).filter(e => e.kind === 'input' && e.target === target).at(-1);
    if (target === 'editor') assertRenderedRichText(actual.richSnapshot, value);
    else assert.equal(actual.value.replace(/\r\n/g, '\n'), value, 'The last independent input event must carry the exact final DOM value');
    gestureProofs.push({ gesture: step.do, target, requestedCharacters: Array.from(value).length, result, foregroundBefore: before.foreground,
      foregroundAfter: after.foreground, cursorBefore: before.cursor, cursorAfter: after.cursor, exactOracle: actual });
  }
  async function ownLook(requirePrivate = true) {
    assert.ok(owned && !preexisting.has(hwnd), 'Only the exact new local fixture window may be inspected');
    const current = await edgeWindowIdentity({ hwnd, executable: signature.path, title, pid: owned.pid, birth: owned.birth });
    assert.equal(current.exists, true);
    const observation = await call('look', { hwnd, show: 'elements' });
    await edgeWindowIdentity({ hwnd, executable: signature.path, title, pid: owned.pid, birth: owned.birth });
    const documents = observation.elements.filter(e => e.role === 'document');
    if (browser === 'edge' && documents.length === 0 && !legacyComparison && process.env.NEXUS_LEGACY_EDGE_PYTHON) {
      assert.equal(observation.elements.filter(e => e.role === 'tab').length, 0, 'Diagnostic refuses a tabbed browser window');
      const spec = { hwnd, executable: signature.path, executableSha256: signature.sha256, title, url, pid: owned.pid, birth: owned.birth };
      const bounded = { operation: 'inspect', windowId: hwnd, app: `process:${signature.path}`, expectedPid: owned.pid, expectedProcessStartedUtc: owned.birth };
      const environment = { ...process.env, NEXUS_WINDOWS_HOME: state, NEXUS_SHARED_HOME: state, NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'), NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json') };
      legacyComparison = { run: runId, fixture: spec, inputSent: false, firstNativeObservation: observation };
      try {
        await edgeWindowIdentity(spec);
        legacyComparison.nativeFullBefore = await fixtureReadOnlyChild(exe, ['--agent', 'claude'], bounded, 'Complete native fixture observation', environment);
        await edgeWindowIdentity(spec);
        legacyComparison.legacy = await legacyEdgeSnapshot(process.env.NEXUS_LEGACY_EDGE_PYTHON, spec);
        await edgeWindowIdentity(spec);
        legacyComparison.nativeFullAfter = await fixtureReadOnlyChild(exe, ['--agent', 'claude'], bounded, 'Complete native fixture observation', environment);
        await edgeWindowIdentity(spec);
      } catch (error) { legacyComparison.error = error.message; throw error; }
      finally {
        await writeFile(path.join(state, 'legacy-comparison.json'), JSON.stringify(legacyComparison, null, 2));
        const summary = value => ({ nodes: value?.elements?.length, documents: value?.elements?.filter(e => e.role === 'document').length, notes: value?.elements?.filter(e => e.name === 'Notes').length });
        t.diagnostic(JSON.stringify({ stage: 'read-only-legacy-comparison', state, nativeBefore: summary(legacyComparison.nativeFullBefore), legacy: legacyComparison.legacy?.view, nativeAfter: summary(legacyComparison.nativeFullAfter), error: legacyComparison.error ?? null, inputSent: false }));
      }
    }
    assert.equal(documents.length, 1, 'The dedicated app window must have exactly one accessible document');
    assert.ok(documents[0].name.includes(title), 'The accessible document must be the exact local fixture');
    const proof = privateChromiumWindowProof(browser, current, observation.elements, title);
    if (requirePrivate) inPrivateDisplayProof = proof;
    return observation;
  }
  async function waitReady() {
    if (domReady) return domReady;
    const timer = setTimeout(() => readyReject(new Error('The exact local fixture never reported readiness')), Math.max(0, readyDeadline - Date.now()));
    try { return await ready; } finally { clearTimeout(timer); }
  }
  async function bindFreshTitle(candidate) {
    const expected = { hwnd: candidate.id, executable: signature.path, pid: candidate.pid,
      birth: candidate.processStartedUtc ?? candidate.birth };
    while (true) {
      const current = await edgeWindowIdentity(expected);
      assert.equal(current.exists, true, 'The newly attributed Edge window disappeared');
      if (current.title.includes(title)) return current;
      if (Date.now() >= readyDeadline) throw new Error('The current title of the new Edge window never identified the local fixture');
      await new Promise(resolve => setTimeout(resolve, 50)); // Read-only title publication, never a repeated launch/input.
    }
  }
  async function cleanupOwnedWindow() {
    if (!launchCandidates.length) return false;
    await waitReady();
    // A primary assertion may fail before owned is assigned. Recover only metadata for the new windows
    // already attributed by this single launch, then select the exact current nonce title.
    const matching = [];
    for (const candidate of launchCandidates) {
      const current = await edgeWindowIdentity({ hwnd: candidate.id, executable: signature.path, pid: candidate.pid,
        birth: candidate.processStartedUtc ?? candidate.birth });
      if (current.exists && current.title.includes(title)) matching.push(current);
    }
    if (matching.length === 0 && hwnd && !(await edgeWindowIdentity({ hwnd, executable: signature.path })).exists) return true;
    assert.equal(matching.length, 1, 'Cleanup requires exactly one fresh local nonce window; no other window may be closed');
    owned = matching[0]; hwnd = owned.hwnd;
    assert.ok(!preexisting.has(hwnd));
    const observation = await call('look', { hwnd, show: 'elements' }, true);
    assert.equal(observation.processId, owned.pid); assert.equal(observation.processStartedUtc, owned.birth);
    // Privacy gates every text gesture. Cleanup still closes this exact newly opened local fixture if
    // privacy was unavailable, with the same nonce/URL/document/tab/identity proof and no text sent.
    const cleanupLayout = privateChromiumWindowProof(browser, owned, observation.elements, title, false);
    inPrivateDisplayProof ??= cleanupLayout;
    const documents = observation.elements.filter(e => e.role === 'document');
    const localAddress = new URL(url).host;
    const addressShown = observation.elements.some(e => e.role === 'edit' && String(e.value ?? '').replace(/^https?:\/\//, '').replace(/\/$/, '') === localAddress);
    assert.ok(documents.length === 1 && documents[0].name.includes(title) || documents.length === 0 && addressShown,
      'Cleanup requires the exact local document or its exact address plus independently attested DOM readiness');
    // Recheck the nonce after reading UIA and dispatch one normal close. Its unknown result is never retried.
    await edgeWindowIdentity({ hwnd, executable: signature.path, title, pid: owned.pid, birth: owned.birth });
    closeAttempted = true; await call('window', { hwnd, action: 'close' }, true);
    const current = await edgeWindowIdentity({ hwnd, executable: signature.path, title, pid: owned.pid, birth: owned.birth });
    assert.equal(current.exists, false, 'The exact local Edge window did not close normally');
    return true;
  }
  try {
    await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'native-simulated-local-acceptance', version: '1' } });
    const beforeWindows = (await call('windows', {})).windows;
    for (const window of beforeWindows) preexisting.add(window.id);
    initialEdge = await edgeWindowInventory(signature.path, beforeWindows);
    await writeFile(path.join(state, 'fixture.json'), JSON.stringify({ run: runId, title, url, substrate: browserName, requestedArgs: `${privateSwitch} --app=${url}`, preexistingWindows: initialEdge }));
    readyDeadline = Date.now() + 15000;
    const launched = launchResult = await call('launch', { app: signature.path, args: `${privateSwitch} --app=${url}`, then: 'none' });
    t.diagnostic(JSON.stringify({ stage: `${browser}-launch-return`, launched, state, signature: { sha256: signature.sha256, signature: signature.signature, version: signature.version } }));
    launchCandidates = (launched.windows ?? []).filter(window => !preexisting.has(window.id) && window.shared !== true);
    await writeFile(path.join(state, 'launch-attribution.json'), JSON.stringify({ run: runId, launchCandidates }, null, 2));
    assert.equal(launchCandidates.length, 1, 'Native launch must attribute exactly one new fixture window');
    hwnd = launchCandidates[0].id;
    // Keep the new HWND/PID/birth before any title/readiness assertion, so cleanup survives a primary failure.
    owned = await edgeWindowIdentity({ hwnd, executable: signature.path, pid: launchCandidates[0].pid, birth: launchCandidates[0].processStartedUtc });
    assert.equal(owned.exists, true);
    assert.equal(launched.devtoolsPort, undefined);
    await waitReady();
    owned = await bindFreshTitle(launchCandidates[0]);
    assert.equal((await signedBrowserProof(executable)).sha256, signature.sha256, 'The signed browser executable changed during launch');
    await ownLook();
    if (comparisonOnly) { t.diagnostic('Native document is accessible; diagnostic ends without any text gesture.'); return; }
    const notes = 'Première ligne\nDeuxième 😀';
    await act({ do: 'set', target: 'Notes', text: notes, check: { target: 'Notes', value: notes } }, 'notes', notes);
    await act({ do: 'type', target: 'Notes', text: ' suite', check: { target: 'Notes', value: notes + ' suite' } }, 'notes', notes + ' suite');
    const rich = 'Bonjour\n\nJulien 😀';
    await act({ do: 'set', target: 'Éditeur', text: rich, check: { target: 'Éditeur', value: rich } }, 'editor', rich);
    await act({ do: 'type', target: 'Éditeur', text: ' suite', check: { target: 'Éditeur', value: rich + ' suite' } }, 'editor', rich + ' suite');
    const long = 'n'.repeat(2000);
    await act({ do: 'set', target: 'Notes', text: long, check: { target: 'Notes', value: long } }, 'notes', long);
    await act({ do: 'clear', target: 'Notes', check: { target: 'Notes', value: '' } }, 'notes', '');
    // The initial snapshot has no Envoyer button. Typing A reveals it synchronously while retaining focus,
    // target runtime and ancestors; the next real Enter must refresh outward-action authorization.
    await ownLook(); const offset = events.length, before = await probe();
    const blocked = await call('act', { hwnd, steps: [{ do: 'type', target: 'Brouillon adversaire', text: 'A\nMUST NOT SEND' }], then: 'none' });
    assert.equal(blocked.status, 'unverified', 'The batch must remain unverified after a stopped text step');
    assert.equal(blocked.steps?.[0]?.status, 'unknown', 'The already typed A requires unknown classification, never automatic replay');
    assert.equal(blocked.success, false); assert.equal(blocked.steps[0].success, false);
    assert.ok(!blocked.continuation, 'An uncertain prefix must never produce an automatic continuation');
    const after = await probe(); assert.equal(after.foreground, before.foreground); assertCursorKept(before, after);
    await waitOracle(offset, e => e.kind === 'send-revealed' && e.value === 'A');
    const snapshot = await checkpoint();
    assert.equal(snapshot.value, 'A'); assert.equal(snapshot.sendVisible, true);
    assert.deepEqual(snapshot.counts, { input: 1, enter: 0, submit: 0 }, 'Fresh local page state must prove one prefix input, zero Enter/submit and no replay');
    assert.equal(events.slice(offset).filter(e => e.kind === 'input' && e.target === 'adversary').at(-1)?.value, 'A');
    assert.ok(!events.slice(offset).some(e => e.kind === 'enter' || e.kind === 'adversary-submit'), 'No Enter/submit may be delivered after the changed outward context');
    t.diagnostic(JSON.stringify({ oracle: 'independent localhost DOM events, native foreground/cursor probes', eventCount: events.length, lastInputs: ['notes', 'editor', 'adversary'].map(target => ({ target, ...events.filter(e => e.kind === 'input' && e.target === target).at(-1) })), adversary: 'A retained; zero Enter/submit' }));
    passed = true;
  } catch (error) { primaryError = error; throw error; }
  finally {
    let cleanupError, closed = false;
    if (launchCandidates.length && child.exitCode == null && !failure) try { closed = await cleanupOwnedWindow(); } catch (error) { cleanupError = error; }
    else if (launchCandidates.length) cleanupError = new Error('The owned Edge window could not be safely closed through its MCP session; no second close or process kill attempted');
    if (child.exitCode == null && !failure) try {
      const remaining = (await call('windows', {}, true)).windows;
      finalEdge = await edgeWindowInventory(signature.path, remaining);
      for (const original of initialEdge) assert.ok(finalEdge.some(window => window.id === original.id && window.pid === original.pid && window.birth === original.birth), 'A preexisting Edge window disappeared during qualification');
    } catch (error) { cleanupError ??= error; }
    failPending(new Error('Fixture cleanup')); t.signal.removeEventListener('abort', aborted);
    if (child.stdin.writable) child.stdin.end();
    const httpClosed = new Promise(resolve => http.close(resolve)); http.closeAllConnections();
    await Promise.race([httpClosed, new Promise(resolve => setTimeout(resolve, 1618))]);
    if (child.exitCode == null) { await Promise.race([once(child, 'exit'), new Promise(resolve => setTimeout(resolve, 1618))]); if (child.exitCode == null) child.kill(); }
    child.stdout.destroy(); child.stderr.destroy(); child.stdin.destroy();
    await writeFile(path.join(state, 'receipt.json'), JSON.stringify({ run: runId, substrate: browserName, qualifiesElectron: false,
      requestedPrivateMode: true, privateSwitch, inPrivateDisplayProof, devtools: false, cdpSession: false, signature, launch: launchResult, owned, initialEdge, finalEdge,
      comparisonOnly, numericTimings: stderr.split(/\r?\n/).filter(line=>line.startsWith('[native-text-timing]')).map(line=>{try{return JSON.parse(line.slice('[native-text-timing]'.length));}catch{return {invalid:true};}}),
      launchCandidates, legacyComparison: legacyComparison ? path.join(state, 'legacy-comparison.json') : null, primaryError: primaryError?.message ?? null, gesturesPassed: passed, passed: passed && closed && !cleanupError,
      closeAttempted, closed, cleanupError: cleanupError?.message ?? null, gestureProofs, oracleEvents: events, pageCheckpoint }, null, 2));
    t.diagnostic(JSON.stringify({ receipt: path.join(state, 'receipt.json'), passed: passed && closed && !cleanupError, closed, qualifiesElectron: false, cleanupError: cleanupError?.message ?? null }));
    if (cleanupError && !primaryError) throw cleanupError;
  }
});
