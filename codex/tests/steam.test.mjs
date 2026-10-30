// Contrats Steam de l'outil MCP JavaScript (les contrats du moteur natif sont vérifiés par tests/LaunchTests.cs, xUnit).
import test from 'node:test';
import assert from 'node:assert/strict';
import { WindowsSession } from '../lib/session.mjs';
import { WindowsTools, TOOLS } from '../lib/mcp.mjs';
import { actionTimeoutMs, STEAM_LAUNCH_TIMEOUT_MS } from '../lib/native.mjs';

test('MCP launch passes explicit Steam options, preserves attested reuse metadata, and rejects malformed options before input', async () => {
  const sent = [], steam = { reused: true, restarted: false, devtoolsRequested: true, devtoolsPort: 8080, viewSwitchesApplied: false, notes: [] };
  const native = { async launch(target, options) { sent.push({ target, options }); return { opened: true, windows: [{ id: 7, proof: 'steam' }], steam }; }, async withForeground(_, run) { return run(); } };
  const session = new WindowsSession({ target: 'windows', async list_windows() { return []; } }, { native });
  try {
    const tools = new WindowsTools(session);
    const result = await tools.call('launch', { app: 'Steam', args: '-silent', devtools: true, restart: false });
    assert.deepEqual(sent, [{ target: 'Steam', options: { args: '-silent', devtools: true, restart: false } }]);
    assert.deepEqual(result.structuredContent.steam, steam);
    await assert.rejects(session.launch('Steam', '', { restart: 'yes' }), /booleans/);
    await assert.rejects(session.launch('ms-settings:display', '', { devtools: true }), /only for Steam/);
    assert.equal(sent.length, 1);
    const schema = TOOLS.find(t => t.name === 'launch').inputSchema;
    assert.equal(schema.properties.devtools.type, 'boolean'); assert.equal(schema.properties.restart.type, 'boolean');
    assert.ok(actionTimeoutMs('launch', {}, 100) >= STEAM_LAUNCH_TIMEOUT_MS);
    assert.ok(STEAM_LAUNCH_TIMEOUT_MS > 8000 + 60000 + 90000 + 10000);
  } finally { await session.close(); }
});
