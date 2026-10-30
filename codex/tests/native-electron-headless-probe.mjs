import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import readline from 'node:readline';
import { randomUUID, createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const exe = process.env.NEXUS_NATIVE_TEST_EXE, electron = process.env.NEXUS_SIMULATED_LIVE_ELECTRON;
if (!exe || !electron) throw new Error('Explicit checkpoint helper and existing Electron executable are required');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const script = path.join(root, 'codex', 'tests', 'fixtures', 'electron-headless-boot.cjs');
const run = randomUUID(), state = path.join(os.homedir(), '.nexus', 'nexus-windows', 'qualification', `electron-headless-${run}`);
const url = `http://127.0.0.1/nexus-headless/${run}/`;
await mkdir(state, { recursive: false });
await writeFile(path.join(state, 'marker.json'), JSON.stringify({ owner: 'nexus-electron-headless-probe', run, state, script, url }));
const sourceSha256 = createHash('sha256').update(await readFile(script)).digest('hex');
const child = spawn(exe, ['--mcp', '--agent', 'claude'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env: {
  ...process.env, NEXUS_WINDOWS_HOME: state, NEXUS_SHARED_HOME: state, NEXUS_ROUTINES_MIRROR: '', NEXUS_JOURNAL_HOME: path.join(state, 'journal'),
  NEXUS_GARDE_FOUS: path.join(root, 'commun', 'garde-fous.json'),
} });
let id = 0, stderr = '', ended = false;
const pending = new Map();
const fail = error => { for (const waiter of pending.values()) { clearTimeout(waiter.timer); waiter.reject(error); } pending.clear(); };
child.on('error', error => { ended = true; fail(error); }); child.on('exit', (code, signal) => { ended = true; fail(new Error(`Headless MCP exited ${code ?? signal}: ${stderr}`)); });
child.stdin.on('error', fail); child.stderr.on('data', data => { stderr += data; });
const lines = readline.createInterface({ input: child.stdout });
lines.on('line', line => {
  let message; try { message = JSON.parse(line); } catch { return; }
  const waiter = pending.get(message.id); if (waiter) { clearTimeout(waiter.timer); pending.delete(message.id); waiter.resolve(message); }
});
async function rpc(method, params) {
  if (ended) throw new Error('The one probe MCP already exited');
  const current = ++id, wait = new Promise((resolve, reject) => pending.set(current, { resolve, reject, timer: setTimeout(() => { pending.delete(current); reject(new Error('Single headless probe timed out')); }, 30000) }));
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: current, method, params }) + '\n');
  const reply = await wait; if (reply.error) throw new Error(JSON.stringify(reply.error)); return reply.result;
}
let launch, boot, exit, failure;
try {
  await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'single-owned-headless-probe', version: '1' } });
  // This is the only Electron launch: same native launch/Explorer broker/argument shape as the failing fixture.
  const result = await rpc('tools/call', { name: 'launch', arguments: { app: electron, args: `"${script}" ${url} "${state}"`, then: 'none' } });
  launch = result.structuredContent ?? result;
  boot = JSON.parse(await readFile(path.join(state, 'boot.json'), 'utf8'));
  exit = JSON.parse(await readFile(path.join(state, 'exit.json'), 'utf8').catch(() => 'null'));
  if (boot.run !== run || boot.scriptReadSha256 !== sourceSha256 || boot.stage !== 'app-exit-requested' || boot.requestedExitCode !== 0 || boot.browserWindowImported !== false) throw new Error('The exact owned headless boot proof is incomplete');
} catch (error) { failure = error.message; }
finally {
  fail(new Error('Owned headless probe finished')); if (child.stdin.writable) child.stdin.end();
  if (!ended) await Promise.race([new Promise(resolve => child.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 1618))]);
  if (!ended) child.kill(); lines.close(); child.stdout.destroy(); child.stderr.destroy(); child.stdin.destroy();
  const receipt = { run, state, helper: exe, electron, sourceSha256, launch, boot, exit, failure: failure ?? null };
  await writeFile(path.join(state, 'receipt.json'), JSON.stringify(receipt, null, 2));
  process.stdout.write(JSON.stringify(receipt) + '\n');
}
if (failure) process.exitCode = 1;
