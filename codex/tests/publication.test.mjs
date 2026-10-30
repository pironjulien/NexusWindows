// Contrat de lecture apres publication UIA Chromium : le motif de publication n'est jamais une preuve de l'effet,
// ni une permission de rejouer l'entree. Le mecanisme COM reel est valide dans le banc Windows.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { WindowsSession, summarize } from '../lib/session.mjs';
import { parseTree } from '../lib/tree.mjs';
import { EmergencyStop } from '../lib/stop.mjs';

const target = { role: 'link', name: 'NexusKeypad' };
const expect = { selector: { role: 'link', name: 'NexusBrowser' }, exists: false };

async function setup(t, publication, publish) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'nexus-publication-'));
  const window = { id: 10, app: 'test!Publication', title: 'Publication' };
  let sent = 0, reads = 0;
  const sky = {
    target: 'windows',
    async list_windows() { return [window]; },
    async get_window() { return window; },
    async click() { assert.fail('Publication never permits a physical replay'); },
  };
  const native = {
    async inspect() {
      reads++;
      const changed = sent > 0 && publish;
      const tree = '0 window Publication\n\t1 document Application ID: RootWebArea\n' +
        (changed ? '\t\t2 button Retour ID: back' : '\t\t2 link NexusKeypad\n\t\t3 link NexusBrowser');
      return { accessibility: { tree }, elements: parseTree(tree), revision: 'fixture', publication };
    },
    async call() { sent++; return { technique: 'background:uia', effect: 'sent' }; },
  };
  const session = new WindowsSession(sky, { stateRoot: directory, native,
    emergencyStop: new EmergencyStop({ directory, watch: false }) });
  t.after(async () => { await session.close(); await rm(directory, { recursive: true, force: true }); });
  await session.bind({ id: 10 });
  return { session, observation: await session.observe(), sent: () => sent, reads: () => reads };
}

test('a published navigation verifies the actual changed tree after a single click', async t => {
  const { session, observation, sent } = await setup(t, 'requested', true);
  const result = await session.act(observation, { kind: 'click', selector: target, expect, timeoutMs: 0 });
  assert.equal(result.status, 'verified');
  assert.equal(sent(), 1);
  assert.equal(result.observation.publication, 'requested');
  assert.equal(summarize(result.observation).publication, 'requested');
});

for (const publication of ['requested', 'unavailable', 'stopped', 'no-document']) {
  test(`publication ${publication} alone never verifies or replays an unchanged navigation`, async t => {
    const { session, observation, sent, reads } = await setup(t, publication, false);
    const result = await session.act(observation, { kind: 'click', selector: target, expect, timeoutMs: 0 });
    assert.equal(result.status, 'sent-unconfirmed');
    assert.equal(result.verified, false);
    assert.equal(sent(), 1);
    assert.ok(reads() >= 2);
    assert.equal(result.observation.publication, publication);
  });
}
