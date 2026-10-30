import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = !executable || process.platform !== 'win32';
const plan = cases => JSON.parse(execFileSync(executable, ['--launch-compat-selftest'], {
  input: JSON.stringify(cases), windowsHide: true, timeout: 15000, encoding: 'utf8',
  env: { ...process.env, NEXUS_GARDE_FOUS: fileURLToPath(new URL('../../commun/garde-fous.json', import.meta.url)) },
}));
function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'nexus native launch '));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const file = (relative, body = '') => { const path = join(root, relative); mkdirSync(join(path, '..'), { recursive: true }); writeFileSync(path, body); return path; };
  const editor = file('Mes Outils/Editeur Test.exe');
  const chrome = file('Navigateur/chrome.exe');
  const proxy = file('Navigateur/chrome_proxy.exe');
  const electron = file('Electron/Appli.exe'); file('Electron/resources/app.asar');
  return { root, file, editor, chrome, proxy, electron };
}

test('Claude native launch preserves URI, document/folder and explicit packaged arguments without launching', { skip }, t => {
  const b = fixture(t); const document = b.file('Documents/été été.txt');
  const [web, mail, tel, settings, doc, folder, packaged, badUri, badDoc] = plan([
    { app: 'https://example.test/a?q=été' }, { app: 'mailto:fixture@example.test?subject=Local' }, { app: 'tel:fixture' },
    { app: 'ms-settings:display' }, { app: document }, { app: join(b.root, 'Documents') },
    { app: 'Microsoft.Fixture_123!App', args: 'fixture value' },
    { app: 'https://example.test', args: '--x' }, { app: document, devtools: true },
  ]);
  for (const value of [web, mail, tel, settings]) assert.equal(value.kind, 'uri');
  assert.equal(web.target, 'https://example.test/a?q=été');
  assert.equal(doc.kind, 'document'); assert.equal(folder.kind, 'folder');
  assert.equal(packaged.kind, 'packaged'); assert.equal(packaged.args, 'fixture value');
  assert.match(badUri.refused, /no args/); assert.match(badDoc.refused, /no args/);
  const [unsupported, unknown] = plan([{ app: document, activationHResult: 0x80270254 | 0 }, { app: document, activationHResult: 0x80004005 | 0 }]);
  assert.equal(unsupported.fileContractFallbackAllowed, true);
  assert.equal(unknown.fileContractFallbackAllowed, false, 'an uncertain failed activation is never replayed through the shell');
});

test('Claude native launch resolves complete friendly names before command-line splitting, and preserves shortcuts', { skip }, t => {
  const b = fixture(t);
  const catalogue = [
    { name: 'Mon Editeur Test', id: 'Fixture.Editor', target: b.editor },
    { name: 'Options Fixture', id: 'Fixture.Options', target: b.electron, args: '--existing-choice' },
    { name: 'Calculatrice Fixture', id: 'Microsoft.Fixture_123!App' },
  ];
  const [friendly, partial, shortcut, packaged, quoted, field, ambiguous] = plan([
    { app: 'Mon Editeur Test', args: '"été espace.txt"', catalogue },
    { app: 'Editeur Test', catalogue }, { app: 'Options Fixture', args: 'ignored', catalogue },
    { app: 'Calculatrice Fixture', args: 'fixture', catalogue },
    { app: `"${b.editor}" "été espace.txt"`, catalogue }, { app: b.editor, args: '"été espace.txt"' },
    { app: 'Même nom', catalogue: [{ name: 'Même nom', id: 'A' }, { name: 'Même nom', id: 'B' }] },
  ]);
  assert.equal(friendly.kind, 'executable'); assert.equal(friendly.target, b.editor);
  assert.equal(partial.target, b.editor); assert.equal(friendly.args, '"été espace.txt"');
  assert.equal(shortcut.kind, 'shortcut'); assert.equal(shortcut.target, 'shell:AppsFolder\\Fixture.Options');
  assert.equal(shortcut.args, ''); assert.equal(packaged.kind, 'packaged'); assert.equal(packaged.args, 'fixture');
  assert.equal(quoted.target, field.target); assert.equal(quoted.args, field.args);
  assert.match(ambiguous.refused, /Several Start/);
});

test('Claude native Chromium policy preserves ordinary switches and rejects control/profile injection', { skip }, t => {
  const b = fixture(t);
  const [normal, fresh, proxy, unknown, ...refused] = plan([
    { app: b.chrome, args: '--app=https://example.test --disable-features=A,B "--disable-features=B,C" "été espace.txt"' },
    { app: b.chrome, args: '--new-window https://example.test', devtools: true },
    { app: b.proxy, devtools: true }, { app: b.editor, devtools: true },
    ...['--remote-debugging-port=9222', '/LOAD-EXTENSION=x', '--user-data-dir=x', '--profile-directory=Default', '--single-argument=x', '--'].map(args => ({ app: b.chrome, args })),
  ]);
  assert.equal(normal.kind, 'executable');
  assert.ok(normal.parsedArguments.includes('--app=https://example.test'));
  assert.ok(normal.parsedArguments.includes('été espace.txt'));
  assert.equal(normal.parsedArguments.filter(v => v.startsWith('--disable-features=')).length, 1);
  assert.ok(normal.parsedArguments.includes('--disable-features=A,B,C,CalculateNativeWinOcclusion'));
  assert.ok(fresh.parsedArguments.includes('--user-data-dir=C:\\NexusFixture\\fresh profile'));
  assert.ok(fresh.parsedArguments.includes('--remote-debugging-port=16183'));
  assert.ok(fresh.parsedArguments.includes('--disable-features=CalculateNativeWinOcclusion,msImplicitSignin'));
  assert.ok(fresh.parsedArguments.includes('--disable-sync'));
  assert.match(proxy.refused, /main executable/); assert.match(unknown.refused, /verified Chromium/);
  for (const value of refused) assert.ok(value.refused, JSON.stringify(value));
});

test('Claude native launch keeps fixed profile hard blocks and rejects restart outside Steam', { skip }, t => {
  const b = fixture(t); const keeper = b.file('PasswordManager/keepass.exe');
  const values = plan([{ app: keeper }, { app: b.editor, restart: true },
    { app: 'Fixture_123!App', devtools: true }, { app: 'Fixture_123!App', args: '--remote-debugging-port=9222' }]);
  assert.match(values[0].refused, /shared garde-fous/);
  assert.match(values[1].refused, /Steam/); assert.match(values[2].refused, /full Chromium/);
  assert.match(values[3].refused, /control channel/);
});

test('Claude native fresh-profile guard reads only marker locations and never emits account values', { skip }, t => {
  const b = fixture(t); const profile = join(b.root, 'profile');
  const state = b.file('profile/Local State', JSON.stringify({ profile: { info_cache: { Default: { user_name: 'PRIVATE-FIXTURE-VALUE' } } } }));
  const [account] = plan([{ app: b.editor, profileFixture: profile }]);
  assert.match(account.accountMarker, /Account marker/); assert.ok(!JSON.stringify(account).includes('PRIVATE-FIXTURE-VALUE'));
  writeFileSync(state, JSON.stringify({ profile: { info_cache: { Default: { name: 'Ordinary profile', user_name: '' } } } }));
  b.file('profile/Default/Preferences', JSON.stringify({ account_info: [], google: { services: {} } }));
  const [clean] = plan([{ app: b.editor, profileFixture: profile }]); assert.equal(clean.accountMarker, null);
  b.file('profile/Default/Preferences', JSON.stringify({ account_info: [{ value: 'PRIVATE-FIXTURE-VALUE' }] }));
  const [preferences] = plan([{ app: b.editor, profileFixture: profile }]);
  assert.match(preferences.accountMarker, /Preferences/); assert.ok(!JSON.stringify(preferences).includes('PRIVATE-FIXTURE-VALUE'));
});

test('Claude native Steam lease needs one genuine helper owning an exclusive loopback listener', { skip }, () => {
  const [valid, foreign, exposed, multiple, absent] = plan([
    { steamPortFixture: { helpers: [42, 43], listeners: [{ address: '127.0.0.1', pid: 42 }] } },
    { steamPortFixture: { helpers: [42], listeners: [{ address: '127.0.0.1', pid: 99 }] } },
    { steamPortFixture: { helpers: [42], listeners: [{ address: '0.0.0.0', pid: 42 }] } },
    { steamPortFixture: { helpers: [42, 43], listeners: [{ address: '127.0.0.1', pid: 42 }, { address: '127.0.0.1', pid: 43 }] } },
    { steamPortFixture: { helpers: [42], listeners: [] } },
  ]);
  assert.equal(valid.steamOwner, 42); assert.equal(valid.sessionKind, 'genuine-steam-session');
  for (const result of [foreign, exposed, multiple, absent]) assert.ok(result.refused);
});
