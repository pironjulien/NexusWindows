#!/usr/bin/env node
// Commande `nexus-windows-codex` : serveur MCP autonome (stdio) de NexusWindows for Codex, sans Sky.
// Helper natif cherché dans l'ordre : NEXUS_WINDOWS_CODEX_HELPER, celui du paquet npm (native/bin, construit par la
// CI), puis celui de install.ps1 (installation.json de l'état local).
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { NativeUIA } from '../lib/native.mjs';
import { nativeProvider } from '../lib/native-provider.mjs';
import { WindowsSession } from '../lib/session.mjs';
import { emergencyStop } from '../lib/stop.mjs';
import { WindowsTools, serve } from '../lib/mcp.mjs';
import { writeStatus } from '../lib/status.mjs';
import { codexHome, installSkill } from '../lib/skill-install.mjs';
import { readConfigurationState } from '../lib/plugin-state.mjs';
import { helperPath, publishRuntime, runtimeStateRoot } from '../lib/runtime.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
if (process.argv.includes('--codex-config-state')) {
  console.log(JSON.stringify(await readConfigurationState(path.join(codexHome(), 'config.toml'))));
  process.exit(0);
}
if (process.argv.includes('--install-skill')) {
  console.log(`NEXUS_SKILL_RESULT ${await installSkill({ root })}`);
  process.exit(0);
}
const stateRoot = runtimeStateRoot();

const version = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8')).version;
// Hors Windows ou sans helper, le serveur démarre et liste ses outils (annuaires MCP qui le lancent dans un conteneur
// Linux) ; chaque appel répond la cause, rien n'est tenté.
const unavailable = reason => ({ async call() { throw new Error(reason); } });
const helper = process.platform === 'win32' ? helperPath(root, { stateRoot }) : null;
if (helper) await publishRuntime(helper, version, stateRoot).catch(error => console.error(`NexusWindows guide runtime: ${error.message}`));
// État pour l'appli Nexus (~\.nexus\status\windows-codex.json), écrit en parallèle : jamais bloquant.
const status = process.platform === 'win32' ? writeStatus({ version }).catch(() => {}) : null;
if (process.platform !== 'win32') {
  await serve({ tools: unavailable('NexusWindows for Codex drives the Windows desktop: Windows 10 or 11 is required.'), version });
} else if (!helper) {
  await serve({ tools: unavailable('Native helper not found: install the npm package, run install.ps1, or set NEXUS_WINDOWS_CODEX_HELPER.'), version });
} else if (process.argv.includes('--native-mcp') || process.env.NEXUS_WINDOWS_NATIVE_MCP === '1') {
  const { spawn } = await import('node:child_process');
  const agentIdx = process.argv.indexOf('--agent');
  const agent = (agentIdx >= 0 && process.argv[agentIdx + 1]) ? process.argv[agentIdx + 1] : (process.env.NEXUS_AGENT || 'codex');
  const code = await new Promise(resolve => {
    const child = spawn(helper, ['--mcp', '--agent', agent], { stdio: 'inherit', windowsHide: true });
    child.on('close', exitCode => resolve(exitCode ?? 0));
    child.on('error', () => resolve(1));
  });
  process.exit(code);
} else {
  const stop = emergencyStop();
  stop.startGuard(helper);
  const native = new NativeUIA(helper, { emergencyStop: stop });
  const session = new WindowsSession(nativeProvider(native), { stateRoot, native, emergencyStop: stop, maxAgeMs: 60000 });
  await session.pruneCaptures(); // captures d'une session précédente : purgées dès le démarrage
  await serve({ tools: new WindowsTools(session), version });
  await session.close().catch(() => {});
  native.close();
}
// Contrôle de version en cours : 1 s au plus pour l'écrire, jamais plus (le registre peut ne pas répondre).
await Promise.race([status, new Promise(resolve => setTimeout(resolve, 1000))]);
process.exit(0);
