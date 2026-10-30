import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { helperPath, publishRuntime } from '../lib/runtime.mjs';

test('a cached plugin reuses the helper published by its matching npm release, without starting another process', async t => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'nexus-windows-runtime-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const stateRoot = path.join(root, 'state');
  const npmHelper = path.join(root, 'npm', 'NexusCodex.UIA.exe');
  await mkdir(path.dirname(npmHelper));
  await writeFile(npmHelper, 'fixture, never executed');
  await writeFile(path.join(root, 'package.json'), JSON.stringify({ version: '3.7.2' }));
  const options = { stateRoot, env: {} };
  assert.equal(helperPath(root, options), null);
  await publishRuntime(npmHelper, '3.7.2', stateRoot);
  assert.equal(helperPath(root, options), npmHelper);
  assert.deepEqual(JSON.parse(await readFile(path.join(stateRoot, 'runtime.json'), 'utf8')), { helper: npmHelper, version: '3.7.2' });
  await publishRuntime(npmHelper, '3.7.1', stateRoot);
  assert.equal(helperPath(root, options), null, 'a different native release is not selected');
  assert.equal(helperPath(root, { ...options, env: { NEXUS_WINDOWS_CODEX_HELPER: 'missing.exe' } }), null, 'explicit path never falls back');
});

test('a source-linked server uses its current installed build over an older runtime of the same version', async t => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'nexus-source-runtime-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const stateRoot = path.join(root, 'state');
  const installed = path.join(root, 'current.exe'), previous = path.join(root, 'previous.exe');
  await writeFile(installed, 'current fixture, never executed');
  await writeFile(previous, 'previous fixture, never executed');
  await writeFile(path.join(root, 'package.json'), JSON.stringify({ version: '3.7.4' }));
  await publishRuntime(previous, '3.7.4', stateRoot);
  await writeFile(path.join(stateRoot, 'installation.json'), JSON.stringify({ SourceRoot: root, NativeExecutable: installed }));
  assert.equal(helperPath(root, { stateRoot, env: {} }), installed);
  // Un autre paquet/cache utilise toujours le runtime publie : pas de substitution par le build d'un autre clone.
  const other = path.join(root, 'other');
  await mkdir(other);
  await writeFile(path.join(other, 'package.json'), JSON.stringify({ version: '3.7.4' }));
  assert.equal(helperPath(other, { stateRoot, env: {} }), previous);
});
