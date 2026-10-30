// État pour l'appli Nexus (~\.nexus\status\windows-codex.json) : version, dernière version du registre au plus une fois
// par jour, échec du contrôle dit sans empêcher l'écriture.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { writeStatus, compareVersions } from '../lib/status.mjs';

test('the status file says the running version and the latest one, checked at most once a day', async t => {
  const home = await mkdtemp(path.join(os.tmpdir(), 'nexus-codex-status-'));
  t.after(() => rm(home, { recursive: true, force: true }));
  const env = { NEXUS_SHARED_HOME: home };
  const file = path.join(home, 'status', 'windows-codex.json');
  let calls = 0;
  const day = new Date('2026-10-26T10:00:00Z');
  const first = await writeStatus({ version: '3.1.4', env, latest: async () => { calls++; return '3.1.5'; }, now: () => day });
  assert.deepEqual(first, { product: 'windows-codex', version: '3.1.4', latest: '3.1.5', state: 'idle', checked_at: '2026-10-26T10:00:00.000Z',
    message: 'Version 3.1.5 is available: Codex uses it at its next start.' });
  assert.deepEqual(JSON.parse(await readFile(file, 'utf8')), first);
  // Même jour : pas de nouvel appel au registre.
  const again = await writeStatus({ version: '3.1.5', env, latest: async () => { calls++; return '9.9.9'; }, now: () => new Date('2026-10-26T20:00:00Z') });
  assert.equal(calls, 1);
  assert.equal(again.message, 'Up to date (3.1.5).');
  // Version publiée depuis le contrôle du jour : la version qui tourne devient la dernière connue.
  assert.equal((await writeStatus({ version: '3.1.6', env, latest: async () => assert.fail('no request'), now: () => new Date('2026-10-26T21:00:00Z') })).latest, '3.1.6');
  // Le lendemain, registre injoignable : dernière valeur connue gardée, échec dit, état toujours écrit.
  const failed = await writeStatus({ version: '3.1.5', env, latest: async () => { throw new Error('offline'); }, now: () => new Date('2026-10-27T11:00:00Z') });
  assert.equal(failed.latest, '3.1.6');
  assert.equal(failed.checked_at, '2026-10-26T10:00:00.000Z');
  assert.match(failed.message, /not checked \(offline\)/);
});

test('NEXUS_NO_UPDATE_CHECK=1 writes the local version without any network request', async t => {
  const home = await mkdtemp(path.join(os.tmpdir(), 'nexus-codex-status-'));
  t.after(() => rm(home, { recursive: true, force: true }));
  const status = await writeStatus({ version: '3.1.5', env: { NEXUS_SHARED_HOME: home, NEXUS_NO_UPDATE_CHECK: '1' }, latest: async () => assert.fail('no request') });
  assert.equal(status.latest, null);
  assert.match(status.message, /check disabled/);
});

test('versions compare numerically', () => {
  assert.equal(compareVersions('3.1.10', '3.1.9'), 1);
  assert.equal(compareVersions('3.1.4', '3.1.4'), 0);
  assert.equal(compareVersions('3.0.0', '3.1.0'), -1);
});

test('a status write that cannot replace the file leaves no temporary file behind', async t => {
  const home = await mkdtemp(path.join(os.tmpdir(), 'nexus-codex-status-'));
  t.after(() => rm(home, { recursive: true, force: true }));
  const directory = path.join(home, 'status');
  // Un dossier à la place du fichier : rename échoue à chaque essai, comme un fichier tenu par un lecteur.
  await mkdir(path.join(directory, 'windows-codex.json'), { recursive: true });
  await assert.rejects(writeStatus({ version: '3.3.0', env: { NEXUS_SHARED_HOME: home, NEXUS_NO_UPDATE_CHECK: '1' } }));
  assert.deepEqual((await readdir(directory)).filter(name => name.endsWith('.tmp')), []);
});
