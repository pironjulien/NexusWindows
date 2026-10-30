// Tests boîte noire du moteur C# unifié (GardeFous.cs, Journal.cs, Routines.cs, McpServer.cs) :
// vérifie les selftests hors ligne et le serveur MCP stdio natif (--mcp --agent codex|claude|antigravity).
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { helperPath } from '../lib/runtime.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const executable = process.env.NEXUS_FOREGROUND_EXE ?? helperPath(root);

test('C# unified engine selftests (garde-fous, journal, routines, mcp)', { skip: !executable && 'Native executable absent' }, () => {
  const gf = JSON.parse(execFileSync(executable, ['--garde-fous-selftest'], { windowsHide: true, timeout: 15000 }));
  assert.equal(gf.ok, true);
  assert.deepEqual(gf.agents, ['claude', 'antigravity', 'codex']);
  assert.ok(gf.lookalikes >= 60);

  const jr = JSON.parse(execFileSync(executable, ['--journal-selftest'], { windowsHide: true, timeout: 15000 }));
  assert.equal(jr.ok, true);

  const rt = JSON.parse(execFileSync(executable, ['--routines-selftest'], { windowsHide: true, timeout: 15000 }));
  assert.equal(rt.ok, true);

  const mcp = JSON.parse(execFileSync(executable, ['--mcp-selftest'], { windowsHide: true, timeout: 15000 }));
  assert.equal(mcp.ok, true);
  assert.deepEqual(mcp.agents, ['claude', 'antigravity', 'codex']);
});

async function runMcpSession(agent, requests) {
  const child = spawn(executable, ['--mcp', '--agent', agent], {
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  const replies = [];
  let buffer = '';
  return await new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error(`Native MCP timeout for agent ${agent}`));
    }, 15000);
    child.on('error', err => { clearTimeout(timer); reject(err); });
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      buffer += chunk;
      let idx;
      while ((idx = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, idx).trim();
        buffer = buffer.slice(idx + 1);
        if (line) replies.push(JSON.parse(line));
        if (replies.length === requests.length) {
          clearTimeout(timer);
          child.stdin.end();
        }
      }
    });
    child.on('close', () => {
      clearTimeout(timer);
      resolve(replies);
    });
    for (const req of requests) {
      child.stdin.write(JSON.stringify(req) + '\n');
    }
  });
}

for (const agent of ['codex', 'claude', 'antigravity']) {
  test(`C# native MCP stdio server serves ${agent} facade (2026-07-28 and legacy)`, { skip: !executable && 'Native executable absent' }, async () => {
    const replies = await runMcpSession(agent, [
      { jsonrpc: '2.0', id: 1, method: 'server/discover' },
      { jsonrpc: '2.0', id: 2, method: 'tools/list' },
      {
        jsonrpc: '2.0',
        id: 3,
        method: 'tools/call',
        params: {
          name: agent === 'antigravity' ? 'computer_list_windows' : 'windows',
          arguments: {},
          _meta: {
            'io.modelcontextprotocol/protocolVersion': '2026-07-28',
            'io.modelcontextprotocol/clientCapabilities': {},
          },
        },
      },
    ]);
    assert.equal(replies.length, 3);
    assert.equal(replies[0].result._meta['io.modelcontextprotocol/serverInfo'].name, `nexus-windows-${agent}`);
    const toolNames = replies[1].result.tools.map(t => t.name);
    assert.deepEqual(toolNames, agent === 'claude'
      ? ['windows', 'look', 'act', 'launch', 'window', 'logs', 'notifications']
      : ['windows', 'look', 'act', 'launch', 'window']);
    assert.equal(replies[2].result.resultType, 'complete');
    assert.ok(Array.isArray(replies[2].result.structuredContent.windows));
  });
}

// The raw Codex provider cannot be relabelled by an action payload or inherited agent environment.
test('raw native Codex profile rejects request-level agent switching', { skip: !executable && 'Native executable absent' }, () => {
  let result;
  try { execFileSync(executable, [], { input: JSON.stringify({ operation: 'windows', processId: 0, agent: 'antigravity', requestId: randomUUID() }), env: { ...process.env, NEXUS_AGENT: 'antigravity' }, windowsHide: true, timeout: 15000 }); }
  catch (error) { result = JSON.parse(error.stdout.toString()); }
  assert.equal(result?.ok, false); assert.equal(result.inputStarted, false); assert.match(result.message, /profile is fixed/);
});
