import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, stat, rm } from 'node:fs/promises';
import { EventEmitter } from 'node:events';
import path from 'node:path';
import os from 'node:os';
import vm from 'node:vm';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

test('owned simulated fixture creates its isolated profile before Electron setPath; no Electron/GUI is started', async () => {
  const state = await mkdtemp(path.join(os.tmpdir(), 'nexus-simulated-live-'));
  const script = fileURLToPath(new URL('./fixtures/claude-simulated.cjs', import.meta.url)), url = 'http://127.0.0.1:16183/';
  const require = createRequire(import.meta.url), processEvents = new EventEmitter(); let configured;
  try {
    await writeFile(path.join(state, '.nexus-simulated-fixture.json'), JSON.stringify({ owner: 'nexus-simulated-fixture', run: 'offline-fixture-regression', state, script, url }));
    const source = await readFile(script, 'utf8');
    const execute = argv => vm.runInNewContext(source, { __filename: script, URL, Date,
      process: Object.assign(processEvents, { argv, execPath: process.execPath, pid: process.pid, versions: { node: process.versions.node }, env: {}, exit: () => { throw new Error('Fixture must not exit this offline host'); } }),
      require: name => name !== 'electron' ? require(name) : { app: {
        setPath: (name, profile) => { assert.equal(name, 'userData'); assert.equal(require('node:fs').statSync(profile).isDirectory(), true); configured = profile; },
        commandLine: { appendSwitch() {} }, whenReady: () => new Promise(() => {}), on() {},
      }, BrowserWindow: class { constructor() { throw new Error('No GUI may be constructed in this offline test'); } } },
    }, { filename: script });
    execute([process.execPath, script, url, state]);
    assert.equal(configured, path.join(state, 'electron-profile')); assert.equal((await stat(configured)).isDirectory(), true);
    const events = (await readFile(path.join(state, 'fixture-startup.jsonl'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    assert.deepEqual(events.map(event => event.stage), ['module-enter', 'profile-configured']);
    assert.equal(events[1].profileExists, true);
    execute([process.execPath, script, '--disable-features=CalculateNativeWinOcclusion', '--force-renderer-accessibility', state, url]);
    const reordered = (await readFile(path.join(state, 'fixture-startup.jsonl'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    assert.equal(reordered.at(-1).stage, 'profile-configured', 'The exact owned state and URL must survive switch reordering');
    assert.throws(() => execute([process.execPath, script, '--unknown-private-value', state, url]), /Unknown or duplicate fixture arguments/);
    const refused = (await readFile(path.join(state, 'fixture-startup.jsonl'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    assert.equal(refused.at(-1).stage, 'arguments-refused'); assert.equal(refused.at(-1).unexpectedCount, 1);
    assert.ok(!JSON.stringify(refused).includes('--unknown-private-value'), 'Unknown argument contents must not enter the owned boot log');
  } finally {
    assert.equal(path.dirname(state), path.resolve(os.tmpdir())); await rm(state, { recursive: true, force: true });
  }
});
