import test from 'node:test';
import assert from 'node:assert/strict';
import { cp, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { installSkill, pluginState } from '../lib/skill-install.mjs';
import { configurationState } from '../lib/plugin-state.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const run = promisify(execFile);

test('plugin state parses TOML quoting, dotted keys, whitespace, inline tables and several catalogues', () => {
  for (const config of [
    "[plugins.'nexus-windows@personal']\nenabled=false\n",
    "[ plugins . 'nexus-windows@another-catalogue' ] # kept\n enabled = false\n",
    'plugins."nexus-windows@public".enabled = false\n',
    "plugins = { 'nexus-windows@public' = { enabled = false } }\n",
    '[plugins."nexus-\\u0077indows@public"]\nenabled = false\n',
  ]) assert.equal(pluginState(config), 'disabled', config);
  assert.equal(pluginState("[ plugins . 'nexus-windows@another-catalogue' ]\nenabled = true\n"), 'enabled');
  assert.equal(pluginState("[plugins.'nexus-windows@old']\nenabled=false\n[plugins.'nexus-windows@current']\nenabled=true\n"), 'enabled');
  assert.deepEqual(configurationState("[mcp_servers . 'nexus-windows']\ncommand='custom.exe'\nenabled=false\n"),
    { plugin: 'absent', mcp: 'disabled' });
});

test('comments and multiline strings never declare a plugin or MCP server', () => {
  for (const delimiter of ['"""', "'''"]) {
    const config = `notes = ${delimiter}\n[plugins.'nexus-windows@public']\nenabled = false\n[mcp_servers.nexus-windows]\ncommand = 'fake'\n${delimiter}\n`;
    assert.deepEqual(configurationState(config), { plugin: 'absent', mcp: 'absent' });
  }
  assert.equal(pluginState('# [plugins."nexus-windows@public"]\n# enabled = true\n'), 'absent');
  assert.equal(pluginState('[unrelated]\n"[plugins.\\"nexus-windows@public\\"]" = "enabled = true"\n'), 'absent');
});

async function fixture(t) {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'nexus-windows-guide-'));
  t.after(() => rm(temporary, { recursive: true, force: true }));
  const packaged = path.join(temporary, 'package with spaces');
  const home = path.join(temporary, 'codex');
  const source = path.join(root, existsSync(path.join(root, 'skills')) ? 'skills' : 'skill', 'nexus-windows');
  await mkdir(packaged, { recursive: true });
  await cp(path.join(root, 'lib'), path.join(packaged, 'lib'), { recursive: true });
  await cp(source, path.join(packaged, 'skills', 'nexus-windows'), { recursive: true });
  await mkdir(home, { recursive: true });
  return { root: packaged, home, guide: path.join(packaged, 'skills', 'nexus-windows') };
}

test('direct setup installs the actual guide and references with an importable package entry', async t => {
  const options = await fixture(t);
  assert.equal(await installSkill(options), 'installed');
  const target = path.join(options.home, 'skills', 'nexus-windows');
  for (const relative of ['SKILL.md', 'references/api.md', 'references/apps.md']) {
    assert.equal(await readFile(path.join(target, relative), 'utf8'), await readFile(path.join(options.guide, relative), 'utf8'));
  }
  const entry = await import(pathToFileURL(path.join(target, 'entry.mjs')).href);
  assert.equal(typeof entry.createWindowsSession, 'function');
  assert.equal(typeof entry.summarize, 'function');
  assert.equal(existsSync(path.join(options.home, 'plugins')), false);
  assert.equal(existsSync(path.join(options.home, 'config.toml')), false);
});

test('an enabled cached plugin uses its own portable entry, with the former standalone guide archived', async t => {
  const options = await fixture(t);
  await installSkill(options);
  await writeFile(path.join(options.home, 'config.toml'), '[plugins."nexus-windows@public"]\n# user setting\nenabled = true\n\n[other]\nvalue = true\n');
  assert.equal(pluginState(await readFile(path.join(options.home, 'config.toml'), 'utf8')), 'enabled');
  assert.equal(await installSkill(options), 'plugin');
  assert.equal(existsSync(path.join(options.home, 'skills', 'nexus-windows')), false);
  assert.equal((await readdir(path.join(options.home, 'backups'))).length, 1);
  assert.equal(existsSync(path.join(options.home, 'plugins')), false, 'no second plugin installation');
  const entry = await import(pathToFileURL(path.join(options.guide, 'entry.mjs')).href);
  assert.equal(typeof entry.createWindowsSession, 'function');
});

test('a development entry is retained and a disabled plugin never gets a fallback installation', async t => {
  const options = await fixture(t);
  const alias = path.join(options.home, 'plugins', 'nexus-windows', 'skills', 'nexus-windows', 'entry.mjs');
  await mkdir(path.dirname(alias), { recursive: true });
  await writeFile(alias, '// source-linked development entry\n');
  await writeFile(path.join(options.home, 'config.toml'), '[plugins."nexus-windows@personal"]\nenabled = true\n');
  assert.equal(await installSkill(options), 'plugin');
  assert.equal(await readFile(alias, 'utf8'), '// source-linked development entry\n');
  await writeFile(path.join(options.home, 'config.toml'), '[plugins."nexus-windows@personal"]\nenabled = false\n');
  assert.equal(await installSkill(options), 'disabled');
  assert.equal(existsSync(path.join(options.home, 'skills')), false);
});

test('disabling a plugin archives an owned standalone fallback without changing the configuration', async t => {
  const options = await fixture(t);
  await installSkill(options);
  const initial = "[ plugins . 'nexus-windows@another-catalogue' ]\nenabled = false\n";
  await writeFile(path.join(options.home, 'config.toml'), initial);
  assert.equal(await installSkill(options), 'disabled');
  assert.equal(await installSkill(options), 'disabled');
  assert.equal(await readFile(path.join(options.home, 'config.toml'), 'utf8'), initial);
  assert.equal(existsSync(path.join(options.home, 'skills', 'nexus-windows')), false);
  assert.equal((await readdir(path.join(options.home, 'backups'))).length, 1);
});

test('invalid TOML refuses a fallback before any installation writes', async t => {
  const options = await fixture(t);
  const initial = "[plugins.'nexus-windows@public']\nenabled = false\n[plugins.'nexus-windows@public']\nenabled=true\n";
  await writeFile(path.join(options.home, 'config.toml'), initial);
  assert.throws(() => pluginState(initial), /Invalid Codex config.toml/);
  await assert.rejects(installSkill(options), /Invalid Codex config.toml/);
  assert.deepEqual(await readdir(options.home), ['config.toml']);
  assert.equal(await readFile(path.join(options.home, 'config.toml'), 'utf8'), initial);
});

test('setup keeps its pack result, supplies the direct guide and reuses or preserves a plugin', { skip: process.platform !== 'win32' }, async t => {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'nexus-windows-setup-'));
  t.after(() => rm(temporary, { recursive: true, force: true }));
  // Only the npm network/package lookup is replaced. Guide installation runs the real shipped CLI in each fresh home.
  const probe = path.join(temporary, 'probe.ps1');
  await writeFile(probe, `
function npx.cmd {
    if ($args -contains '--codex-config-state') { & $env:NEXUS_TEST_NODE $env:NEXUS_TEST_BIN --codex-config-state; return }
    if ($args -contains '--install-skill') { & $env:NEXUS_TEST_NODE $env:NEXUS_TEST_BIN --install-skill; return }
    if ($env:NEXUS_TEST_NO_RUNTIME -eq '1') { throw 'An existing plugin or MCP must not start a second runtime.' }
    '{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"version":"3.7.2"}}}'
    '{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"look"}]}}'
}
& $env:NEXUS_TEST_SETUP
`, 'utf8');
  for (const [state, initial, expected] of [
    ['absent', '', 'declared'],
    ['enabled', '[plugins."nexus-windows@public"]\nenabled = true\n', 'already'],
    ['disabled', "[plugins.'nexus-windows@public']\nenabled = false\n", 'disabled'],
    ['spaced', "[ plugins . 'nexus-windows@another-catalogue' ]\nenabled = false\n", 'disabled'],
    ['inline', "plugins = { 'nexus-windows@public' = { enabled = false } }\n", 'disabled'],
    ['custom', "[mcp_servers . 'nexus-windows']\ncommand = 'custom-not-installed.exe'\nargs = ['private argument']\n", 'already'],
    ['mcp-disabled', "[mcp_servers . 'nexus-windows']\ncommand='custom.exe'\nenabled=false\n", 'disabled'],
    ['fake', "notes = '''\n[plugins.'nexus-windows@public']\nenabled=false\n'''\n", 'declared'],
  ]) {
    const home = path.join(temporary, state);
    await mkdir(home, { recursive: true });
    if (initial) await writeFile(path.join(home, 'config.toml'), initial);
    const { stdout, stderr } = await run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', probe], {
      windowsHide: true, timeout: 30000, env: { ...process.env, NEXUS_PACK: '1', USERPROFILE: home, HOME: home, CODEX_HOME: home,
        LOCALAPPDATA: home, NEXUS_TEST_NODE: process.execPath, NEXUS_TEST_BIN: path.join(root, 'bin', 'nexus-windows-codex.mjs'),
        NEXUS_TEST_NO_RUNTIME: expected === 'declared' ? '0' : '1',
        NEXUS_TEST_SETUP: path.join(root, 'setup.ps1') },
    });
    assert.match(stdout, new RegExp(`NEXUS_PACK_RESULT Codex=${expected}`), stderr);
    const config = await readFile(path.join(home, 'config.toml'), 'utf8');
    if (expected === 'declared') {
      assert.match(config, /\[mcp_servers\.nexus-windows\]/);
      assert.ok(existsSync(path.join(home, 'skills', 'nexus-windows', 'SKILL.md')));
    } else {
      assert.equal(config, initial, 'plugin configuration is preserved, with no direct MCP added');
      assert.equal(existsSync(path.join(home, 'skills', 'nexus-windows')), state === 'custom');
    }
  }
});

test('setup refuses a missing CLI state before registration or guide installation', { skip: process.platform !== 'win32' }, async t => {
  const temporary = await mkdtemp(path.join(os.tmpdir(), 'nexus-windows-old-cli-'));
  t.after(() => rm(temporary, { recursive: true, force: true }));
  const initial = "[plugins.'nexus-windows@public']\nenabled=false\n";
  await writeFile(path.join(temporary, 'config.toml'), initial);
  const probe = path.join(temporary, 'probe.ps1');
  await writeFile(probe, `
function npx.cmd {
    if ($args -contains '--codex-config-state') { $global:LASTEXITCODE = 0; '{}'; return }
    throw 'A missing configuration state must not proceed to any other npm command.'
}
& $env:NEXUS_TEST_SETUP
`);
  await assert.rejects(run('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', probe], {
    windowsHide: true, timeout: 30000, env: { ...process.env, NEXUS_PACK: '1', USERPROFILE: temporary, HOME: temporary,
      CODEX_HOME: temporary, LOCALAPPDATA: temporary, NEXUS_TEST_SETUP: path.join(root, 'setup.ps1') },
  }), /valid Codex configuration state/);
  assert.equal(await readFile(path.join(temporary, 'config.toml'), 'utf8'), initial);
  assert.equal(existsSync(path.join(temporary, 'skills')), false);
});
