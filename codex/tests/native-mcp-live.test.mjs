// Real Windows acceptance tests of the native MCP facade. The WinForms/WPF fixtures
// emit an independent event oracle; no user application or external service is acted on.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, rm, writeFile, readFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import readline from 'node:readline';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !executable ? 'Set NEXUS_NATIVE_TEST_EXE to the built native helper' : false;

function lineProcess(command, args, env = process.env, signal) {
  const child = spawn(command, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env });
  const lines = [], waiters = []; let stderr = '', ended, stopping;
  const fail = error => { ended = error; for (const w of waiters.splice(0)) { clearTimeout(w.timer); w.reject(error); } };
  const aborted = () => fail(new Error('Owned live test aborted'));
  signal?.addEventListener('abort', aborted, { once:true });
  const closed = new Promise(resolve => child.once('close', () => { fail(new Error('Owned test process exited')); signal?.removeEventListener('abort', aborted); resolve(); }));
  child.on('error', fail);
  child.stdin.on('error', fail);
  child.stderr.on('data', c => { stderr += c; });
  readline.createInterface({ input: child.stdout }).on('line', line => {
    let data; try { data = JSON.parse(line); } catch { return; }
    lines.push(data);
    for (const w of [...waiters]) if (w.predicate(data)) { clearTimeout(w.timer); waiters.splice(waiters.indexOf(w), 1); w.resolve(data); }
  });
  return { child, lines, stderr: () => stderr, stop() {
    return stopping ??= (async () => {
      fail(new Error('Owned test process stopped')); child.stdin.end();
      if (child.exitCode == null && child.signalCode == null) {
        // Only these two spawned test PIDs and their helper children; never a name-based process kill.
        if (process.platform === 'win32') await new Promise((resolve, reject) => {
          const killer = spawn('taskkill.exe', ['/PID',String(child.pid),'/T','/F'], { windowsHide:true,stdio:'ignore' });
          killer.once('error', reject); killer.once('close', resolve);
        });
        else child.kill();
      }
      await closed;
    })();
  }, wait(predicate, timeout = 20000) {
    if (signal?.aborted) return Promise.reject(new Error('Owned live test aborted'));
    const old = lines.find(predicate); if (old) return Promise.resolve(old);
    if (ended) return Promise.reject(ended);
    return new Promise((resolve, reject) => {
      const w = { predicate, resolve, reject, timer: setTimeout(() => { waiters.splice(waiters.indexOf(w), 1); reject(new Error(`Timed out: ${stderr}`)); }, timeout) };
      waiters.push(w);
    });
  } };
}

for (const [family, agent] of [['winforms', 'codex'], ['winforms', 'claude'], ['winforms', 'antigravity'], ['wpf', 'claude'], ['wpf', 'antigravity']]) {
  test(`native ${agent} ${family}: real labels, proofs, failures, routines and continuation`, { skip, timeout: 120000 }, async t => {
    const qualification = path.join(os.homedir(),'.nexus','nexus-windows','qualification'); await mkdir(qualification,{recursive:true});
    const evidence = await mkdtemp(path.join(qualification,`mcp-facade-${agent}-${family}-`));
    const state = path.join(evidence,'state'); await mkdir(state);
    const started = performance.now(), progress = []; let passed = false, cleanupPromise;
    const record = (event, data = {}) => progress.push({ ms:Math.round(performance.now()-started),event,...data });
    const fixture = lineProcess('powershell.exe', ['-NoProfile', '-STA', '-File', path.join(root, 'claude', 'lab', `${family}_app.ps1`)], process.env, t.signal);
    const server = lineProcess(executable, ['--mcp', '--agent', agent], { ...process.env, NEXUS_WINDOWS_HOME: state,
      NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json'), NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'), NEXUS_SHARED_HOME: state }, t.signal);
    const saveProgress = () => writeFile(path.join(evidence,'progress.json'),JSON.stringify({agent,family,passed,aborted:t.signal.aborted,fixtureEvents:fixture.lines.filter(r=>r.event).length,progress},null,2));
    const cleanup = () => cleanupPromise ??= (async () => {
      record('cleanup-start');
      const initialSave = saveProgress().catch(error => error);
      const stopped = await Promise.allSettled([fixture.stop(),server.stop()]);
      await initialSave; // Evidence I/O can never delay stopping an owned process.
      record('cleanup', {fixture:stopped[0].status,server:stopped[1].status});
      await saveProgress();
      if (!passed || t.signal.aborted) {
        t.diagnostic('Synthetic failure progress: '+path.join(evidence,'progress.json'));
      }
      for (const result of stopped) if (result.status === 'rejected') throw result.reason;
      await rm(state,{recursive:true,force:true,maxRetries:5,retryDelay:50});
    })();
    const aborted = () => { record('aborted'); void cleanup().catch(() => {}); };
    t.signal.addEventListener('abort',aborted,{once:true});
    t.after(cleanup); // Node awaits this hook after timeout, before starting the next case.
    let nextId = 0; let hwnd;
    async function rpc(method, params = {}) {
      t.signal.throwIfAborted();
      const id = ++nextId; const pending = server.wait(r => r.id === id);
      record('rpc-start',{id,method,tool:params.name});
      server.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
      const response = await pending;
      t.signal.throwIfAborted(); record('rpc-end',{id,error:!!response.error,isError:!!response.result?.isError,status:response.result?.structuredContent?.status});
      if (response.error) throw new Error(JSON.stringify(response.error));
      return response.result;
    }
    async function call(name, args) {
      const r = await rpc('tools/call', { name, arguments: args });
      if (r.isError) throw new Error(JSON.stringify(r));
      return r.structuredContent ?? r;
    }
    const look = () => call('look', { hwnd });
    const act = steps => call('act', { hwnd, steps });
    const summary = r => JSON.stringify({status:r.status,steps:r.steps?.map(s=>({status:s.status,mark:s.mark})),completed:r.progress?.completed});
    try {
      hwnd = (await fixture.wait(r => r.ready)).top;
      await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'native-live-acceptance', version: '1' } });
      const listed = await call('windows', {}); assert.ok(listed.windows.some(w => w.id === hwnd));
      if (agent === 'claude') for (const key of ['hwnd','process','framework','width','height','minimized','maximized','foreground','hidden','owner','surface']) assert.ok(Object.hasOwn(listed.windows.find(w => w.id === hwnd), key), key);
      const first = await look(); assert.ok(first.observation); assert.ok(first.elements.some(e => e.name === 'Nom'));
      let r = await act([{ action: 'set', target: 'Nom', text: 'Nexus native' }, { action: 'check', target: 'Option' }, { action: 'select', target: 'Couleur', option: 'Vert' }]);
      assert.equal(r.status, 'success', summary(r)); assert.ok(r.steps.every(s => s.success));
      await fixture.wait(r => r.event === 'coche on'); await fixture.wait(r => r.event === 'couleur Vert');
      const read = await look(); assert.equal(read.elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'Nexus native');
      r = await act([{ action: 'set', target: 'Nom', text: 'Nexus native' }]); assert.equal(r.steps[0].status, 'already-satisfied');
      await look(); r = await act([{ action: 'type', target: 'Nom', text: ' appended' }, { action: 'wait', seconds: 0.001 }]);
      assert.equal(r.status, 'success', summary(r)); assert.equal(r.steps[1].status, 'waited');
      assert.equal((await look()).elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'Nexus native appended');
      if (family === 'wpf') {
        r = await act([{ action: 'scroll', target: 'Défilement', delta_y: -1 }]); assert.equal(r.status, 'success', summary(r));
        await fixture.wait(r => r.event?.startsWith('defil A bas'));
        assert.ok(!fixture.lines.some(r => r.event?.startsWith('defil B ')), 'the outer scroll container must not move');
      }
      await look();
      r = await act([{ action: 'click', target: 'Valider', timeoutMs: 50, expect: { element: 'Nom', value: 'will never appear' } }, { action: 'uncheck', target: 'Option' }]);
      assert.equal(r.status, 'unverified'); assert.equal(r.steps.length, 1); assert.equal(r.steps[0].verified, false);
      await fixture.wait(r => r.event === 'clic 1');
      assert.equal((await look()).elements.find(e => e.name === 'Option').checked, true, 'later step must not execute after failed proof');
      r = await call('act', { hwnd, remember: 'native field', staticUI: true, params: { name: 'learned' }, steps: [{ action: 'set', target: 'Nom', text: '{{name}}' }] });
      assert.equal(r.status, 'success', summary(r));
      r = await call('act', { hwnd, operation: 'list' }); assert.equal(r.routines[0].name, 'native field'); assert.equal(r.routines[0].coverage, 'verified');
      await look(); r = await call('act', { hwnd, routine: 'native field', params: { name: 'replayed' } }); assert.equal(r.status, 'success', summary(r));
      assert.equal((await look()).elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'replayed');
      r = await call('act', { hwnd, time_budget: 0, steps: [{ action: 'set', target: 'Nom', text: 'prefix' }, { action: 'set', target: 'Nom', text: 'suffix' }] });
      assert.equal(r.status, 'paused'); assert.equal(r.progress.completed, 1); const token = r.continuation;
      await look(); r = await call('act', { hwnd, operation: 'continue', continuation: token }); assert.equal(r.status, 'success', summary(r));
      assert.equal((await look()).elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'suffix');
      await assert.rejects(() => call('act', { hwnd, operation: 'continue', continuation: token }), /Continuation expired/);
      await look();
      r = await act([{ action: 'click', target: 'Dialogue', expect: { element: 'OK', exists: true } }, { action: 'click', target: 'OK', expect: { element: 'OK', exists: false } }]);
      assert.equal(r.status, 'success', summary(r));
      await fixture.wait(r => r.event === 'modale ouverte'); await fixture.wait(r => r.event === 'modale ok');
      await look();
      r = await act([{ action: 'set', target: 'Lecture', text: 'must be refused', timeoutMs: 50 }, { action: 'set', target: 'Nom', text: 'must not run' }]);
      assert.notEqual(r.status, 'success'); assert.equal(r.steps.length, 1);
      assert.equal((await look()).elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'suffix');
      r = await act([{ action: 'set', target: 'Nom', text: 'consumed' }]); assert.equal(r.status, 'success');
      if (agent === 'claude') {
        assert.ok(r.observation?.observation, 'Claude then=auto returns the new observation after the batch');
        assert.equal(r.observation.elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'consumed');
        const displayed = await look(); const field = displayed.elements.find(e => e.name === 'Nom' && e.role === 'edit');
        r = await call('act', { hwnd, then:'none', steps:[{ do:'set', target:'Nom', text:'consumed' }] });
        assert.equal(r.status, 'success'); assert.equal(r.observation, undefined, 'then=none does not publish a new observation');
        await assert.rejects(() => act([{ do:'set', target:`#${field.index}`, text:'unshown index must not run' }]), /not displayed/);
        r = await call('act', { hwnd, then:'none', steps:[{ do:'set', target:'Nom', text:'fresh semantic act' }] });
        assert.equal(r.status, 'success', 'first semantic Claude act can create its own fresh snapshot without publishing indices');
        const fresh = await look(); assert.equal(fresh.elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'fresh semantic act');
        r = await act([{ do:'set', target:'Nom', text:'consumed' }]); assert.equal(r.status, 'success');
      } else {
        await assert.rejects(() => act([{ action: 'set', target: 'Nom', text: 'illegal replay' }]), /Look at the named window before acting/);
      }
      assert.equal((await look()).elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'consumed');
      if (agent === 'claude') {
        await look();
        r = await act([{ do: 'set', target: 'Nom', text: 'contrat Claude', check: { target: 'Nom', value: 'contrat Claude', enabled: true } },
          { do: 'check', target: 'Option', check: { target: 'Option', checked: true } },
          { do: 'wait', text: 'Nom', timeout: 0.1 }]);
        assert.equal(r.status, 'success', summary(r));
        const view = await look(); assert.equal(view.elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'contrat Claude');
        r = await act([{ do:'set', target:'Nom', text:'contrat Claude', check:{target:'Nom', value:'contrat Claude', enabled:true} }]);
        assert.equal(r.steps[0].status, 'already-satisfied');
      }
      r = await call('act', { hwnd, operation: 'forget', routine: 'native field' }); assert.equal(r.forgotten, true);
      r = await call('act', { hwnd, operation: 'list' }); assert.equal(r.routines.length, 0);
      t.diagnostic(JSON.stringify({ family, events: fixture.lines.filter(r => r.event), steps: 'real provider, independent fixture events' }));
      passed = true; record('passed');
    } catch (error) {
      record('failed',{type:error.name,code:error.code}); throw error;
    } finally {
      try { await cleanup(); } finally { t.signal.removeEventListener('abort',aborted); }
    }
  });
}

test('native AGY: local safety form validates refusal and exact-action token binding', { skip, timeout: 45000 }, async () => {
  const state = await mkdtemp(path.join(os.tmpdir(), 'nexus-native-safety-'));
  const fixture = lineProcess('powershell.exe', ['-NoProfile', '-STA', '-File', path.join(root, 'codex', 'tests', 'native-safety-form.ps1')]);
  const server = lineProcess(executable, ['--mcp', '--agent', 'antigravity'], { ...process.env, NEXUS_WINDOWS_HOME: state,
    NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json'), NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'), NEXUS_SHARED_HOME: state });
  let id = 0, hwnd;
  async function call(name, args) {
    const current = ++id, pending = server.wait(r => r.id === current);
    server.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method: 'tools/call', params: { name, arguments: args } }) + '\n');
    const r = await pending; assert.ok(!r.error && !r.result.isError, JSON.stringify(r)); return r.result.structuredContent;
  }
  try {
    hwnd = (await fixture.wait(r => r.ready)).top;
    await call('look', { hwnd }); await call('act', { hwnd, steps: [{ action: 'set', target: 'Nom', text: 'first content' }] });
    const submit = { action: 'click', target: 'Envoyer', expect: { title: 'Native safety clicked 1' } };
    await call('look', { hwnd }); let r = await call('act', { hwnd, steps: [submit] });
    assert.equal(r.steps[0].status, 'confirmation-required'); assert.equal(fixture.lines.filter(r => r.event).length, 0);
    const first = /accord="([a-f0-9]+)"/.exec(r.steps[0].message)?.[1]; assert.ok(first);
    await call('look', { hwnd }); await call('act', { hwnd, steps: [{ action: 'set', target: 'Nom', text: 'changed content' }] });
    await call('look', { hwnd }); r = await call('act', { hwnd, steps: [{ ...submit, confirm: true, accord: first }] });
    assert.equal(r.steps[0].status, 'confirmation-required'); assert.equal(fixture.lines.filter(r => r.event).length, 0);
    const current = /accord="([a-f0-9]+)"/.exec(r.steps[0].message)?.[1]; assert.ok(current); assert.notEqual(current, first);
    await call('look', { hwnd }); r = await call('act', { hwnd, steps: [{ ...submit, confirm: true, accord: current }] });
    assert.equal(r.status, 'success', JSON.stringify(r)); await fixture.wait(r => r.event === 'local send 1');
    assert.equal(fixture.lines.filter(r => r.event).length, 1);
    const screenshot = await call('computer_take_screenshot', { full_screen: true, output_path: path.join(state, 'screen-export.webp') });
    assert.equal(screenshot.method, 'desktop-bitblt'); assert.ok(screenshot.width <= 2000 && screenshot.height <= 2000); assert.ok(screenshot.size_bytes <= 500 * 1024);
    const exported = await readFile(path.join(state, 'screen-export.webp')); assert.equal(exported.toString('ascii', 0, 4), 'RIFF'); assert.equal(exported.toString('ascii', 8, 12), 'WEBP');
    await call('look', { hwnd });
    const privateTestValue = 'fixture-only-private-value';
    r = await call('act', { hwnd, steps: [{ action: 'set', target: 'Mot de passe', text: privateTestValue, timeoutMs: 20 }] });
    assert.equal(r.status, 'unverified', JSON.stringify(r)); await fixture.wait(r => r.event === 'password changed');
    assert.ok(!JSON.stringify(r).includes(privateTestValue));
    const masked = await call('look', { hwnd }); assert.ok(!JSON.stringify(masked).includes(privateTestValue));
    assert.equal(masked.elements.find(e => e.name === 'Mot de passe').value, null);
    const codex = lineProcess(executable, ['--mcp', '--agent', 'codex'], { ...process.env, NEXUS_WINDOWS_HOME: state,
      NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json'), NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'), NEXUS_SHARED_HOME: state });
    let codexId = 0;
    async function codexCall(name, args) {
      const current = ++codexId, pending = codex.wait(r => r.id === current);
      codex.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method: 'tools/call', params: { name, arguments: args } }) + '\n');
      return (await pending).result;
    }
    try {
      const eventsBefore = fixture.lines.filter(r => r.event === 'password changed').length;
      const spoof = await codexCall('look', { window: hwnd, agent: 'antigravity' });
      assert.equal(spoof.isError, true); assert.match(JSON.stringify(spoof), /profile.*fixed/);
      await codexCall('look', { window: hwnd });
      const refusal = await codexCall('act', { window: hwnd, steps: [{ kind: 'setValue', selector: { name: 'Mot de passe', role: 'edit' }, value: 'must not enter', agent: 'antigravity' }] });
      assert.equal(refusal.isError, true); assert.match(JSON.stringify(refusal), /Password controls/);
      assert.equal(fixture.lines.filter(r => r.event === 'password changed').length, eventsBefore, 'Codex cannot change profile via a tool argument');
    } finally { codex.child.stdin.end(); codex.child.kill(); }
    await call('look', { hwnd });
    await writeFile(path.join(state, 'stop'), JSON.stringify({ by: 'native-acceptance-test', nonce: Date.now() }));
    r = await call('act', { hwnd, steps: [{ action: 'set', target: 'Nom', text: 'must not enter after stop' }] });
    assert.equal(r.steps[0].status, 'stopped');
    const readable = await call('look', { hwnd }); assert.equal(readable.elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'changed content');
  } finally {
    if (hwnd) { try { await call('window', { hwnd, action: 'close' }); } catch {} }
    server.child.stdin.end(); fixture.child.kill(); server.child.kill();
    await rm(state, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
  }
});

for (const agent of ['codex', 'claude', 'antigravity']) test(`native ${agent}: physical fallback, native capture and foreground return on a local WPF fixture`, {
  skip: skip || process.env.NEXUS_NATIVE_PHYSICAL_TEST !== '1' && 'Opt in with NEXUS_NATIVE_PHYSICAL_TEST=1 when the desktop is free', timeout: 30000,
}, async t => {
  const state = await mkdtemp(path.join(os.tmpdir(), 'nexus-native-physical-'));
  const fixture = lineProcess('powershell.exe', ['-NoProfile', '-STA', '-File', path.join(root, 'codex', 'tests', 'native-physical-form.ps1')]);
  const server = lineProcess(executable, ['--mcp', '--agent', agent], { ...process.env, NEXUS_WINDOWS_HOME: state,
    NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json'), NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'), NEXUS_SHARED_HOME: state });
  let id = 0, hwnd;
  async function call(name, args) {
    const current = ++id, pending = server.wait(r => r.id === current);
    server.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method: 'tools/call', params: { name, arguments: args } }) + '\n');
    const r = await pending; assert.ok(!r.error && !r.result.isError, JSON.stringify({ tool:name,error:r.error?.code,isError:r.result?.isError,
      message:r.result?.isError ? r.result.content?.find(b => b.type === 'text')?.text : undefined })); return r.result;
  }
  const foreground = () => JSON.parse(execFileSync(executable, [], { input:JSON.stringify({ operation:'probe' }), windowsHide:true })).foreground;
  function checkForeground(result, before, oracle) {
    const after = foreground(), step = result.steps[0];
    const diagnostic = { agent,oracle,status:result.status,stepStatus:step.status,technique:step.technique,
      stepReturned:step.foregroundReturned,batchReturned:result.foregroundReturned,before,after };
    t.diagnostic(JSON.stringify(diagnostic));
    const message = JSON.stringify(diagnostic);
    assert.equal(result.status, 'success', message); assert.equal(step.technique, 'physical:native', message);
    assert.equal(typeof before, 'number', message); assert.equal(typeof after, 'number', message);
    assert.equal(after, before, 'independent native probes must prove restoration: ' + message);
    const directReturn = ['restored','not-taken','none'].includes(step.foregroundReturned);
    const scopedReturn = agent === 'claude' && step.foregroundReturned === 'held-for-interaction'
      && ['restored','not-taken'].includes(result.foregroundReturned);
    assert.ok(directReturn || scopedReturn, message);
  }
  try {
    hwnd = (await fixture.wait(r => r.ready)).top;
    const capture = await call('look', { hwnd, capture_screenshot: true });
    assert.ok(capture.content.some(b => b.type === 'image')); assert.equal(capture.structuredContent.capture.mimeType, 'image/webp');
    const before = foreground();
    const result = (await call('act', { hwnd, ...(agent !== 'codex' ? { foreground:true } : {}), steps: [{ action: 'key', key: 'ctrl+k', expect: { title: 'Native physical shortcut verified' } }] })).structuredContent;
    if (result.steps[0].status === 'deferred') { t.skip('The real presence guard deferred physical input; no desktop action sent'); return; }
    checkForeground(result, before, 'physical shortcut');
    assert.deepEqual(result.steps[0].attempts, [{method:'background',status:'refused-before-input',error_type:'BackgroundNotApplicableException',index:0},{method:'physical',status:'returned',index:0}], 'a real WPF fallback retains its no-input background refusal');
    await fixture.wait(r => r.event === 'physical shortcut');
    await call('look', { hwnd });
    const beforeAlt = foreground();
    const alt = (await call('act', { hwnd, ...(agent !== 'codex' ? { foreground:true } : {}), steps: [{ action: 'key', key: 'alt+k', expect: { title: 'Native physical alt verified' } }] })).structuredContent;
    checkForeground(alt, beforeAlt, 'physical alt');
    assert.deepEqual(alt.steps[0].attempts, [{method:'background',status:'refused-before-input',error_type:'BackgroundNotApplicableException',index:0},{method:'physical',status:'returned',index:0}]);
    await fixture.wait(r => r.event === 'physical alt');
    t.diagnostic(JSON.stringify({ technique: result.steps[0].technique, returned: result.steps[0].foregroundReturned, oracle: 'physical shortcut' }));
  } finally {
    if (hwnd) { try { await call('window', { hwnd, action: 'close' }); } catch {} }
    server.child.stdin.end(); fixture.child.kill(); server.child.kill();
    await rm(state, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
  }
});

test('native Codex interaction: a real context menu stays open between semantic steps and focus returns', {
  skip: skip || process.env.NEXUS_NATIVE_PHYSICAL_TEST !== '1' && 'Opt in with NEXUS_NATIVE_PHYSICAL_TEST=1', timeout: 45000,
}, async t => {
  const state = await mkdtemp(path.join(os.tmpdir(), 'nexus-native-interaction-'));
  const fixture = lineProcess('powershell.exe', ['-NoProfile', '-STA', '-File', path.join(root, 'claude', 'lab', 'winforms_app.ps1')]);
  const server = lineProcess(executable, ['--mcp', '--agent', 'codex'], { ...process.env, NEXUS_WINDOWS_HOME:state, NEXUS_ROUTINES_MIRROR:'', NEXUS_JOURNAL_HOME:path.join(state,'journal'), NEXUS_SHARED_HOME:state });
  let id = 0, hwnd;
  async function call(name, args) {
    const n = ++id, pending = server.wait(r => r.id === n);
    server.child.stdin.write(JSON.stringify({ jsonrpc:'2.0', id:n, method:'tools/call', params:{ name, arguments:args } }) + '\n');
    return (await pending).result;
  }
  const foreground = () => JSON.parse(execFileSync(executable, [], { input:JSON.stringify({ operation:'probe' }), windowsHide:true })).foreground;
  try {
    hwnd = (await fixture.wait(r => r.ready)).top;
    const view = (await call('look', { window:hwnd })).structuredContent;
    const list = view.elements.find(e => e.name === 'Liste' && e.role !== 'text'); assert.ok(list);
    const before = foreground();
    const r = await call('act', { window:hwnd, interaction:true, steps:[
      { kind:'click', selector:{ role:list.role, name:list.name }, button:'right', expect:{ selector:{ name:'Copier' }, exists:true } },
      { kind:'click', selector:{ role:'menuitem', name:'Copier' }, expect:{ selector:{ name:'Copier' }, exists:false } },
    ] });
    if (r.isError && /presence|user is typing|user presence/i.test(r.content[0].text)) { t.skip('Real presence guard deferred the interaction before input'); return; }
    assert.equal(r.structuredContent?.status, 'success', JSON.stringify(r));
    await fixture.wait(e => e.event === 'contexte copier');
    assert.equal(foreground(), before, 'the interaction returns the actual prior foreground');
  } finally {
    if (hwnd) await call('window', { window:hwnd, action:'close' }).catch(() => {});
    server.child.stdin.end(); fixture.child.kill(); server.child.kill();
    await rm(state, { recursive:true, force:true, maxRetries:5, retryDelay:50 });
  }
});

test('native AGY: external field/definition changes invalidate a continuation, proof failure after input stays unknown', { skip, timeout: 45000 }, async () => {
  const state = await mkdtemp(path.join(os.tmpdir(), 'nexus-native-boundary-'));
  const fixture = lineProcess('powershell.exe', ['-NoProfile', '-STA', '-File', path.join(root, 'codex', 'tests', 'native-safety-form.ps1')]);
  const env = { ...process.env, NEXUS_WINDOWS_HOME: state, NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json'), NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'), NEXUS_SHARED_HOME: state };
  const first = lineProcess(executable, ['--mcp', '--agent', 'antigravity'], env);
  const other = lineProcess(executable, ['--mcp', '--agent', 'antigravity'], env);
  let hwnd;
  function client(server) {
    let id = 0;
    return async (name, args) => {
      const current = ++id, pending = server.wait(r => r.id === current);
      server.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method: 'tools/call', params: { name, arguments: args } }) + '\n');
      const r = await pending; if (r.error || r.result.isError) throw new Error(JSON.stringify(r)); return r.result.structuredContent;
    };
  }
  const a = client(first), b = client(other);
  const set = text => ({ action: 'set', target: 'Nom', text });
  try {
    hwnd = (await fixture.wait(r => r.ready)).top;
    await a('look', { hwnd }); let r = await a('act', { hwnd, time_budget: 0, steps: [set('Alice'), set('suffix must not run')] });
    assert.equal(r.status, 'paused'); const changedValueToken = r.continuation;
    await b('look', { hwnd }); await b('act', { hwnd, steps: [set('Bob')] }); // An independent process really edits the field.
    await a('look', { hwnd }); await assert.rejects(() => a('act', { hwnd, operation: 'continue', continuation: changedValueToken }), /Continuation expired/);
    assert.equal((await a('look', { hwnd })).elements.find(e => e.name === 'Nom' && e.role === 'edit').value, 'Bob');
    const templates = [set('{{first}}'), set('{{second}}')];
    await a('act', { hwnd, remember: 'boundary routine', staticUI: true, params: { first: 'same', second: 'same' }, steps: templates });
    await a('look', { hwnd }); r = await a('act', { hwnd, routine: 'boundary routine', params: { first: 'same', second: 'future suffix' }, time_budget: 0 });
    assert.equal(r.status, 'paused'); const changedDefinitionToken = r.continuation;
    await b('look', { hwnd }); await b('act', { hwnd, remember: 'boundary routine', staticUI: true, params: { first: 'same', second: 'same' }, steps: templates }); // New saved definition, same visible state and anchors.
    await a('look', { hwnd }); await assert.rejects(() => a('act', { hwnd, operation: 'continue', continuation: changedDefinitionToken }), /Routine definition changed/);
    await a('look', { hwnd }); r = await a('act', { hwnd, routine: 'boundary routine', params: { first: 'same', second: 'future suffix' }, time_budget: 0 });
    const removedToken = r.continuation; await b('act', { hwnd, operation: 'forget', routine: 'boundary routine' });
    await a('look', { hwnd }); await assert.rejects(() => a('act', { hwnd, operation: 'continue', continuation: removedToken }), /Routine was removed/);
    await a('look', { hwnd }); await a('act', { hwnd, remember: 'review routine', staticUI: true, params: { first: 'review baseline' }, steps: [set('{{first}}')] });
    await b('look', { hwnd }); await b('act', { hwnd, steps: [{ action: 'click', target: 'Toggle readonly', expect: { title: 'Native safety readonly on' } }] });
    await a('look', { hwnd }); r = await a('act', { hwnd, routine: 'review routine', params: { first: 'refused value' } }); assert.equal(r.status, 'failed');
    await a('look', { hwnd }); await a('act', { hwnd, operation: 'inspect', routine: 'review routine' });
    r = await a('act', { hwnd, routine: 'review routine', params: { first: 'refused again' } }); assert.equal(r.status, 'failed');
    await a('look', { hwnd }); await assert.rejects(() => a('act', { hwnd, routine: 'review routine', params: { first: 'must inspect again' } }), /needs review/);
    await a('look', { hwnd });
    r = await a('act', { hwnd, steps: [{ action: 'click', target: 'Fermer avec délai', expect: { element: 'Nom', value: 'never' }, timeoutMs: 2000 }, set('must not run')] });
    assert.equal(r.status, 'unverified', JSON.stringify(r)); assert.equal(r.steps[0].status, 'unknown'); assert.equal(r.steps[0].mark, '~'); assert.equal(r.steps.length, 1);
    await fixture.wait(r => r.event === 'delayed close');
  } finally {
    if (hwnd) { try { await a('window', { hwnd, action: 'close' }); } catch {} }
    for (const server of [first, other]) { server.child.stdin.end(); server.child.kill(); }
    fixture.child.kill(); await rm(state, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
  }
});
