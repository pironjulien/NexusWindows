// Molette Tk d'une fenêtre entièrement recouverte (TkWheel.cs, recette de NexusWindows Claude 3.7.0 du 27/10) :
// géométrie de couverture par le vrai helper (--tk-cover-selftest) quand il est fourni (NEXUS_FOREGROUND_EXE), et
// fenêtre non remise en place rendue comme un échec terminal, même après l'entrée.
import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { nativeError } from '../lib/native.mjs';

const executable = process.env.NEXUS_FOREGROUND_EXE;
const skip = !executable && 'NEXUS_FOREGROUND_EXE absent';

test('coverage: exact, a one-pixel strip refuses, corners follow the monitor scale', { skip }, () => {
  const lines = execFileSync(executable, ['--tk-cover-selftest'], { windowsHide: true }).toString().trim().split(/\r?\n/);
  assert.equal(lines.length, 5);
  for (const line of lines) assert.match(line, /^ok /);
});

test('a window not put back is a terminal failure, never an unknown outcome to retry', () => {
  const after = nativeError({ message: 'not put back', error: 'WindowNotRestoredException', inputStarted: true });
  assert.equal(after.refused, true);
  assert.equal(after.windowNotRestored, true);
  assert.equal(after.afterInput, true);
  assert.equal(after.notApplicable, false);
  const before = nativeError({ message: 'not put back', error: 'WindowNotRestoredException', inputStarted: false });
  assert.equal(before.refused, true);
  assert.equal(nativeError({ message: 'x', error: 'ActionRefusedException', inputStarted: true }).refused, false);
});
