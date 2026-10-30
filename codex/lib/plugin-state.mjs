// Shared by npm guide setup and the source installer. Only read the user's TOML; never rewrite it.
import { readFile, readdir, lstat } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import TOML from './vendor/smol-toml/index.cjs';

function parseConfiguration(config) {
  let parsed;
  try { parsed = TOML.parse(config, { integersAsBigInt: 'asNeeded' }); }
  catch { throw new Error('Invalid Codex config.toml: no installation was changed.'); }
  return parsed;
}

export function configurationState(config, { marketplace } = {}) {
  const parsed = parseConfiguration(config);
  const declarations = Object.entries(parsed.plugins ?? {}).filter(([key]) =>
    marketplace ? key === `nexus-windows@${marketplace}` : key.startsWith('nexus-windows@'));
  const plugin = declarations.some(([, value]) => value?.enabled === true) ? 'enabled'
    : declarations.length ? 'disabled' : 'absent';
  const server = parsed.mcp_servers?.['nexus-windows'];
  return { plugin, mcp: server === undefined ? 'absent' : server?.enabled === false ? 'disabled' : 'enabled' };
}

// The source installer must honor the public choice independently of any leftover personal declaration.
export function sourceInstallationState(config) {
  const parsed = parseConfiguration(config);
  const publicChoice = Object.hasOwn(parsed.plugins ?? {}, 'nexus-windows@nexus-plugins');
  return { ...configurationState(config, publicChoice ? { marketplace: 'nexus-plugins' } : {}),
    route: publicChoice ? 'public' : 'personal' };
}

export async function verifyPublicInstallation(home, catalogRoot) {
  const catalog = JSON.parse(await readFile(path.join(catalogRoot, '.agents', 'plugins', 'marketplace.json'), 'utf8'));
  const entries = catalog.plugins?.filter(entry => entry.name === 'nexus-windows') ?? [];
  if (catalog.name !== 'nexus-plugins' || entries.length !== 1 || entries[0].source?.source !== 'local' ||
      typeof entries[0].source.path !== 'string') throw new Error('The registered nexus-plugins catalog has no unique NexusWindows source.');
  const source = path.resolve(catalogRoot, entries[0].source.path);
  if (!source.startsWith(path.resolve(catalogRoot) + path.sep)) throw new Error('The NexusWindows catalog source leaves its registered root.');
  const manifest = JSON.parse(await readFile(path.join(source, '.codex-plugin', 'plugin.json'), 'utf8'));
  if (manifest.name !== 'nexus-windows' || manifest.skills !== './skills/' || manifest.mcpServers !== './.mcp.json' ||
      typeof manifest.version !== 'string' || !/^[0-9A-Za-z][0-9A-Za-z.+-]*$/.test(manifest.version)) {
    throw new Error('The public NexusWindows manifest is incomplete.');
  }
  const cache = path.join(home, 'plugins', 'cache', 'nexus-plugins', 'nexus-windows', manifest.version);
  const mcp = JSON.parse(await readFile(path.join(source, '.mcp.json'), 'utf8'));
  if (!mcp.mcpServers || typeof mcp.mcpServers !== 'object' || !Object.hasOwn(mcp.mcpServers, 'nexus-windows')) {
    throw new Error('The public NexusWindows MCP declaration is missing.');
  }
  await readFile(path.join(source, 'skills', 'nexus-windows', 'SKILL.md'));
  async function verify(directory, relative = '') {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const name = path.join(relative, entry.name), original = path.join(source, name), installed = path.join(cache, name);
      if ((await lstat(original)).isSymbolicLink() || (await lstat(installed)).isSymbolicLink()) throw new Error('A public plugin path is a symbolic link.');
      if (entry.isDirectory()) await verify(original, name);
      else if (!entry.isFile() || !(await readFile(original)).equals(await readFile(installed))) {
        throw new Error('The installed public NexusWindows cache differs from its registered catalog.');
      }
    }
  }
  await verify(source);
  return { state: 'verified', version: manifest.version };
}

export const pluginState = config => configurationState(config).plugin;

export async function readConfigurationState(file) {
  let config;
  try { config = await readFile(file, 'utf8'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; config = ''; }
  return configurationState(config);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const result = process.argv[2] === '--source-plan'
      ? sourceInstallationState(await readFile(process.argv[3], 'utf8').catch(error => { if (error.code === 'ENOENT') return ''; throw error; }))
      : process.argv[2] === '--verify-public' ? await verifyPublicInstallation(process.argv[3], process.argv[4])
      : await readConfigurationState(process.argv[2]);
    console.log(JSON.stringify(result));
  }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
