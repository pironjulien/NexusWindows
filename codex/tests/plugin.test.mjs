// Plugin Codex (.codex-plugin/plugin.json + .mcp.json) : même version que package.json, serveur lancé par npx depuis npm ;
// le dépôt est aussi sa propre marketplace (.agents/plugins/marketplace.json : `codex plugin marketplace add`).
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { existsSync } from 'node:fs';

const read = (file) => JSON.parse(readFileSync(new URL(`../${file}`, import.meta.url), 'utf8'));

test('the Codex plugin carries the package version and starts the npm server', () => {
  const plugin = read('.codex-plugin/plugin.json');
  assert.equal(plugin.version, read('package.json').version);
  assert.equal(plugin.mcpServers, './.mcp.json');
  assert.equal(plugin.skills, './skills/');
  const server = read('.mcp.json').mcpServers['nexus-windows'];
  assert.equal(server.command, 'npx');
  assert.deepEqual(server.args, ['-y', 'nexus-windows-codex@latest']); // dernière version à chaque démarrage de Codex
});

test('the public plugin includes the canonical guide, references and portable entry', () => {
  const publicRoot = new URL('../skills/nexus-windows/', import.meta.url);
  const sourceRoot = new URL('../skill/nexus-windows/', import.meta.url);
  for (const relative of ['SKILL.md', 'references/api.md', 'references/apps.md', 'entry.mjs']) {
    if (existsSync(publicRoot)) {
      assert.ok(existsSync(new URL(relative, publicRoot)), relative);
    } else {
      // The private source keeps one guide. Its exporter maps those exact bytes into public skills/.
      const mapping = readFileSync(new URL('../.nexus-publish.toml', import.meta.url), 'utf8');
      assert.ok(mapping.includes(`"skills/nexus-windows/${relative}" = "skill/nexus-windows/${relative}"`), relative);
      assert.ok(existsSync(new URL(relative, sourceRoot)), relative);
    }
  }
  const pkg = read('package.json');
  if (!pkg.private) {
    for (const component of ['skills/', '.codex-plugin/', '.mcp.json', 'assets/']) assert.ok(pkg.files.includes(component), component);
  }
});

test('the repository is a Codex marketplace listing the plugin at its root', () => {
  const market = read('.agents/plugins/marketplace.json');
  const entry = market.plugins.find((plugin) => plugin.name === read('.codex-plugin/plugin.json').name);
  assert.ok(entry);
  assert.deepEqual(entry.source, { source: 'local', path: './' });
  assert.equal(entry.policy.installation, 'AVAILABLE');
});

// Icônes du plugin (schéma des plugins Codex : interface.logo et interface.composerIcon, chemins relatifs à la racine du
// plugin) : le médaillon NexusWindows du kit de la famille (-mark), 512 px pour la fiche, 64 px pour le compositeur.
test('the Codex plugin declares its logo and composer icon, square PNG files of the plugin', () => {
  const { interface: ui } = read('.codex-plugin/plugin.json');
  for (const [field, size] of [['logo', 512], ['composerIcon', 64]]) {
    assert.match(ui[field], /^\.\/assets\/[\w-]+\.png$/, field);
    const png = readFileSync(new URL(`../${ui[field].slice(2)}`, import.meta.url));
    assert.equal(png.subarray(1, 4).toString('latin1'), 'PNG', field);
    assert.deepEqual([png.readUInt32BE(16), png.readUInt32BE(20)], [size, size], field);
  }
});
