// Programme lancé avec ses arguments (passe 2 du 28/10 : « EditeurTest.exe note.txt » et la même ligne entre
// guillemets refusés sans lancement) : découpe de la ligne, champ args, gardes des commutateurs Chromium/Electron, par
// le vrai helper (--launch-selftest) quand il est fourni (NEXUS_FOREGROUND_EXE), hors ligne : rien n'est lancé, les
// « programmes » sont des fichiers vides d'un dossier temporaire. Puis le passage de args par l'outil et la session.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { WindowsSession } from '../lib/session.mjs';
import { WindowsTools } from '../lib/mcp.mjs';

const executable = process.env.NEXUS_FOREGROUND_EXE;
const skip = !executable && 'NEXUS_FOREGROUND_EXE absent';
const plan = cases => JSON.parse(execFileSync(executable, ['--launch-selftest'], { input: JSON.stringify(cases), windowsHide: true }).toString());

function bench(t) {
  const root = mkdtempSync(join(tmpdir(), 'nexus launch '));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const file = (path, body = '') => { mkdirSync(join(path, '..'), { recursive: true }); writeFileSync(path, body); return path; };
  return {
    root,
    editor: file(join(root, 'NexusBanc', 'EditeurTest.exe')),
    spaced: file(join(root, 'Mes Outils', 'Editeur Test.exe')),
    note: file(join(root, 'passe2', 'note.txt'), 'x'),
    spacedNote: file(join(root, 'passe 2', 'ma note.txt'), 'x'),
    electron: (file(join(root, 'Appli', 'resources', 'app.asar')), file(join(root, 'Appli', 'Appli.exe'))),
    browser: (file(join(root, 'Navigateur', 'Application', '156.0.1', 'resources.pak')), file(join(root, 'Navigateur', 'Application', 'browser.exe'))),
  };
}

test('a program followed by its arguments is launched with them, quoted or not, spaces kept', { skip }, t => {
  const b = bench(t);
  const [bare, quoted, field, spacedQuoted, spacedUnquoted, spacedField] = plan([
    { target: `${b.editor} ${b.note}` },
    { target: `"${b.editor}" "${b.note}"` },
    { target: b.editor, args: `"${b.note}"` },
    { target: `"${b.spaced}" "${b.spacedNote}"` },
    { target: `${b.spaced} "${b.spacedNote}"` },
    { target: `"${b.spaced}"`, args: `"${b.spacedNote}"` },
  ]);
  assert.deepEqual([bare.target, bare.args, bare.start, bare.arguments], [b.editor, b.note, b.editor, b.note]);
  assert.deepEqual([quoted.target, quoted.args, quoted.exePath], [b.editor, `"${b.note}"`, b.editor]);
  assert.deepEqual([field.target, field.arguments], [b.editor, `"${b.note}"`]);
  for (const one of [spacedQuoted, spacedUnquoted, spacedField]) {
    assert.deepEqual([one.target, one.isExe, one.exePath, one.arguments], [b.spaced, true, b.spaced, `"${b.spacedNote}"`]);
  }
});

test('an existing path with spaces is never cut, and quotes around a lone path are only quotes', { skip }, t => {
  const b = bench(t);
  const [spacedExe, spacedNote, quotedNote, folder, name] = plan([
    { target: b.spaced }, { target: b.spacedNote }, { target: `"${b.spacedNote}"` }, { target: join(b.root, 'Mes Outils') },
    { target: `notepad "${b.spacedNote}"` },
  ]);
  assert.deepEqual([spacedExe.target, spacedExe.args, spacedExe.isExe], [b.spaced, '', true]);
  assert.deepEqual([spacedNote.target, spacedNote.args, spacedNote.isExe], [b.spacedNote, '', false]);
  assert.deepEqual([quotedNote.target, quotedNote.args], [b.spacedNote, '']);
  assert.deepEqual([folder.target, folder.args], [join(b.root, 'Mes Outils'), '']);
  assert.deepEqual([name.target, name.args, name.start], ['notepad', `"${b.spacedNote}"`, 'notepad']);
});

test('arguments only for a program, never a switch for a Chromium/Electron application, existing guards kept', { skip }, t => {
  const b = bench(t);
  const results = plan([
    { target: b.note, args: 'x' },
    { target: b.electron, args: '--remote-debugging-port=9333' },
    { target: `"${b.browser}" --user-data-dir=C:\\x` },
    { target: b.browser, args: `"${b.note}" /safe` },
    { target: 'nexus-introuvable-zz', args: '--x' },
    { target: 'ms-settings:display', args: 'x' },
    { target: 'tel:0600000000' },
    { target: 'relatif\\app.exe x' },
    { target: 'Microsoft.Paint_8wekyb3d8bbwe!App', args: 'x' },
  ]);
  assert.match(results[0].refused, /Arguments go only to a program/);
  assert.match(results[1].refused, /Switch "--remote-debugging-port=9333" refused for a Chromium\/Electron/);
  assert.match(results[2].refused, /Switch "--user-data-dir=C:\\x" refused/);
  assert.match(results[3].refused, /Switch "\/safe" refused/);
  assert.match(results[4].refused, /does not resolve: give the program's absolute path/);
  assert.match(results[5].refused, /Only ms-settings/); // le selftest ne passe pas par OpenSettings : aucune adresse lancée
  assert.match(results[6].refused, /Only ms-settings/);
  assert.match(results[7].refused, /Give an absolute path/);
  assert.match(results[8].refused, /Arguments go only to a program/);
  // Fichier donné à une application Electron : après les commutateurs de l'outil, jamais à leur place.
  const [electron] = plan([{ target: b.electron, args: `"${b.note}"` }]);
  assert.equal(electron.arguments, `--disable-features=CalculateNativeWinOcclusion --force-renderer-accessibility "${b.note}"`);
});

test('the launch tool hands args to the helper; a Settings page with arguments is refused before anything starts', async () => {
  const sent = [];
  const native = {
    async launch(target, options) { sent.push([target, options]); return { opened: true, windows: [{ id: 7, proof: 'process' }], candidates: [] }; },
    async withForeground(_, run) { return run(); },
    async openSettings(uri) { sent.push(['settings', uri]); return { window: 10, title: 'Paramètres' }; },
  };
  const session = new WindowsSession({ target: 'windows', async list_windows() { return []; } }, { native });
  try {
    const tools = new WindowsTools(session);
    const note = 'C:\\Docs\\passe 2\\note.txt';
    await tools.call('launch', { app: 'C:\\NexusBanc\\EditeurTest.exe', args: `"${note}"` });
    await tools.call('launch', { app: `"C:\\NexusBanc\\EditeurTest.exe" "${note}"` });
    await assert.rejects(tools.call('launch', { app: 'ms-settings:display', args: 'x' }), /takes no arguments/);
    assert.deepEqual(sent.map(([target, options]) => [target, options?.args]), [
      ['C:\\NexusBanc\\EditeurTest.exe', `"${note}"`], [`"C:\\NexusBanc\\EditeurTest.exe" "${note}"`, undefined]]);
  } finally { await session.close(); }
});
