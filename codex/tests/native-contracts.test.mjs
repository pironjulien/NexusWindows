import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';

const exe = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = !exe || process.platform !== 'win32';

for (const mode of ['notifications', 'migration']) test(`native ${mode}: isolated persistent fixtures`, { skip }, () => {
  const result = JSON.parse(execFileSync(exe, [`--${mode}-selftest`], { windowsHide: true, timeout: 15000 }));
  assert.equal(result.ok, true);
});

test('Claude native logs: historical typed contract, count, limit and errors_only', { skip }, async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'nexus-contract-'));
  const dir = path.join(root, 'journal', 'windows', 'claude'); await mkdir(dir, { recursive: true });
  const rows = [
    { ts: '2026-10-29T12:00:00Z', action: 'click', issue: '✓', statut: 'verified', ms: 3 },
    { ts: '2026-10-29T12:01:00Z', action: 'setValue', issue: '~', statut: 'unknown', ms: 5 },
    { ts: '2026-10-29T12:02:00Z', action: 'wait', issue: '=', statut: 'already-satisfied', ms: 1 },
  ];
  await writeFile(path.join(dir, '2026-10.jsonl'), rows.map(r => JSON.stringify(r)).join('\n') + '\n');
  const child = spawn(exe, ['--mcp', '--agent', 'claude'], { windowsHide: true, env: { ...process.env, NEXUS_JOURNAL_HOME: path.join(root, 'journal'), NEXUS_WINDOWS_HOME: root, NEXUS_SHARED_HOME: root }, stdio: ['pipe','pipe','pipe'] });
  let id = 0; const pending = new Map();
  readline.createInterface({ input: child.stdout }).on('line', line => { const r = JSON.parse(line); pending.get(r.id)?.(r); });
  const rpc = (method, params = {}) => new Promise((resolve, reject) => { const n = ++id; const timer = setTimeout(() => reject(new Error('MCP timeout')), 10000); pending.set(n, r => { clearTimeout(timer); resolve(r); }); child.stdin.write(JSON.stringify({ jsonrpc:'2.0', id:n, method, params }) + '\n'); });
  try {
    const list = (await rpc('tools/list')).result.tools;
    for (const name of ['windows', 'notifications', 'logs']) assert.equal(list.find(t => t.name === name).outputSchema.type, 'object');
    for (const [args, wanted] of [[{ count:2 }, 2], [{ limit:1 }, 1], [{ count:100, errors_only:true }, 1]]) {
      const r = (await rpc('tools/call', { name:'logs', arguments:args })).result;
      assert.ok(!r.isError, JSON.stringify(r));
      const data = r.structuredContent;
      assert.equal(data.version, '3.8.0'); assert.equal(data.pid, child.pid);
      assert.ok(Array.isArray(data.status)); assert.equal(data.records.length, wanted);
      for (const row of data.records) for (const key of ['ts','tool','ms','ok','error','args','steps']) assert.ok(Object.hasOwn(row,key), key);
      if (args.errors_only) assert.equal(data.records[0].tool, 'setValue');
    }
    const spoof = (await rpc('tools/call', { name:'windows', arguments:{ agent:'antigravity' } })).result;
    assert.equal(spoof.isError, true); assert.match(spoof.content[0].text, /fixed/);
  } finally { child.stdin.end(); child.kill(); await rm(root, { recursive:true, force:true, maxRetries:5, retryDelay:50 }); }
});

test('native startup refuses duplicate or extra agent arguments', { skip }, () => {
  for (const args of [['--mcp','--agent','codex','--agent','antigravity'], ['--mcp','--agent','codex','--profile','claude']]) {
    assert.throws(() => execFileSync(exe, args, { windowsHide:true, timeout:10000, stdio:'pipe' }), e => e.status === 2);
  }
});


test('embedded Codex contract snapshot matches its preserved adapter', async () => {
  const { TOOLS } = await import('../lib/mcp.mjs');
  const embedded = JSON.parse(await readFile(new URL('../native/NativeSchemas.json', import.meta.url), 'utf8'));
  assert.deepEqual(embedded, TOOLS);
});
