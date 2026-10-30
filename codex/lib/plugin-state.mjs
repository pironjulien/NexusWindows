// Shared by npm guide setup and the source installer. Only read the user's TOML; never rewrite it.
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import TOML from './vendor/smol-toml/index.cjs';

export function configurationState(config) {
  let parsed;
  try { parsed = TOML.parse(config, { integersAsBigInt: 'asNeeded' }); }
  catch { throw new Error('Invalid Codex config.toml: no installation was changed.'); }
  const declarations = Object.entries(parsed.plugins ?? {}).filter(([key]) => key.startsWith('nexus-windows@'));
  const plugin = declarations.some(([, value]) => value?.enabled === true) ? 'enabled'
    : declarations.length ? 'disabled' : 'absent';
  const server = parsed.mcp_servers?.['nexus-windows'];
  return { plugin, mcp: server === undefined ? 'absent' : server?.enabled === false ? 'disabled' : 'enabled' };
}

export const pluginState = config => configurationState(config).plugin;

export async function readConfigurationState(file) {
  let config;
  try { config = await readFile(file, 'utf8'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; config = ''; }
  return configurationState(config);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { console.log(JSON.stringify(await readConfigurationState(process.argv[2]))); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
