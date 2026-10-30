// Programme lancé avec ses arguments : passage de args par l'outil et la session (la découpe de la ligne et les gardes
// des commutateurs du moteur natif sont vérifiées par tests/LaunchTests.cs, xUnit).
import test from 'node:test';
import assert from 'node:assert/strict';
import { WindowsSession } from '../lib/session.mjs';
import { WindowsTools } from '../lib/mcp.mjs';

test('the launch tool hands args to the helper; a Settings page with arguments is refused before anything starts', async () => {
  const sent = [];
  const native = {
    async launch(target, options) { sent.push([target, options]); return { opened: true, windows: [{ id: 7, proof: 'process' }], candidates: [] }; },
    async withForeground(_, run) { return run(); },
    async openSettings(uri) { sent.push(['settings', uri]); return { window: 10, title: 'Paramètres' }; },
  };
  const session = new WindowsSession({ target: 'windows', async list_windows() { return []; } }, { native });
  try {
    const tools = new WindowsTools(session);
    const note = 'C:\\Docs\\passe 2\\note.txt';
    await tools.call('launch', { app: 'C:\\NexusBanc\\EditeurTest.exe', args: `"${note}"` });
    await tools.call('launch', { app: `"C:\\NexusBanc\\EditeurTest.exe" "${note}"` });
    await assert.rejects(tools.call('launch', { app: 'ms-settings:display', args: 'x' }), /takes no arguments/);
    assert.deepEqual(sent.map(([target, options]) => [target, options?.args]), [
      ['C:\\NexusBanc\\EditeurTest.exe', `"${note}"`], [`"C:\\NexusBanc\\EditeurTest.exe" "${note}"`, undefined]]);
  } finally { await session.close(); }
});
