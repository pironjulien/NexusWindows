// A plugin cache contains the JavaScript guide but the native binary comes from the npm server package.
// Publish only its location and version so the guide can use that same helper; no second server or credentials.
import { existsSync, readFileSync } from 'node:fs';
import { mkdir, rename, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import os from 'node:os';
import path from 'node:path';

export const runtimeStateRoot = () => path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local'), 'NexusCodex', 'ComputerUse');

export function helperPath(root, { stateRoot = runtimeStateRoot(), env = process.env } = {}) {
  if (env.NEXUS_WINDOWS_CODEX_HELPER) return existsSync(env.NEXUS_WINDOWS_CODEX_HELPER) ? env.NEXUS_WINDOWS_CODEX_HELPER : null;
  const candidates = [path.join(root, 'native', 'bin', 'NexusCodex.UIA.exe')];
  let installation;
  try { installation = JSON.parse(readFileSync(path.join(stateRoot, 'installation.json'), 'utf8')); } catch { /* no source install */ }
  // Un serveur lie a CE depot doit prendre son build installe. runtime.json peut encore designer le build de la
  // meme version publie par un ancien serveur en cours : il ne doit pas annuler une correction locale par empreinte.
  const canonical = value => process.platform === 'win32' ? path.resolve(value).toLowerCase() : path.resolve(value);
  if (typeof installation?.SourceRoot === 'string' && canonical(installation.SourceRoot) === canonical(root)) {
    candidates.push(installation.NativeExecutable);
  }
  try {
    const runtime = JSON.parse(readFileSync(path.join(stateRoot, 'runtime.json'), 'utf8'));
    const version = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8')).version;
    if (runtime.version === version && typeof runtime.helper === 'string') candidates.push(runtime.helper);
  } catch { /* server not started, or a different release */ }
  candidates.push(installation?.NativeExecutable);
  return candidates.find(candidate => typeof candidate === 'string' && existsSync(candidate)) ?? null;
}

export async function publishRuntime(helper, version, stateRoot = runtimeStateRoot()) {
  await mkdir(stateRoot, { recursive: true });
  const target = path.join(stateRoot, 'runtime.json');
  const temporary = `${target}.${randomUUID()}.tmp`;
  await writeFile(temporary, JSON.stringify({ helper, version }) + '\n', 'utf8');
  await rename(temporary, target);
}
