// Molette Tk d'une fenêtre entièrement recouverte : fenêtre non remise en place rendue comme un échec terminal, même
// après l'entrée (la géométrie de couverture du moteur natif est vérifiée par tests/WindowTests.cs, xUnit).
import test from 'node:test';
import assert from 'node:assert/strict';
import { nativeError } from '../lib/native.mjs';

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
