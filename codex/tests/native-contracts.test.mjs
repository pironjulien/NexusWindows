// Le contrat Codex embarqué dans le moteur natif (NativeSchemas.json) reste celui de l'adaptateur JavaScript conservé.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

test('embedded Codex contract snapshot matches its preserved adapter', async () => {
  const { TOOLS } = await import('../lib/mcp.mjs');
  const embedded = JSON.parse(await readFile(new URL('../native/NativeSchemas.json', import.meta.url), 'utf8'));
  assert.deepEqual(embedded, TOOLS);
});
