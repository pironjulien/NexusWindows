import test from 'node:test';
import assert from 'node:assert/strict';
import { edgeBirthProbeSelfTest, privateChromiumWindowProof } from './fixtures/signed-edge-window.mjs';

const skip = process.platform !== 'win32';
test('owned Edge test helper preserves all seven birth decimals and rejects a changed 100ns tick', { skip }, async () => {
  const proof = await edgeBirthProbeSelfTest();
  assert.equal(proof.inputType, 'System.String');
  assert.equal(proof.exactIso, '2026-10-30T02:40:46.9512907Z');
  assert.equal(proof.exact, true); assert.equal(proof.sameInstantOffset, true);
  assert.equal(proof.differentTick, false); assert.equal(proof.differentSecond, false);
  assert.equal(proof.invalidRejected, true); assert.equal(proof.gui, false);
});
test('private Chromium qualification requires visible privacy and refuses foreign or multiple tabs', () => {
  const title = 'Nexus simulated 01234567-0123-0123-0123-012345678901';
  const current = { title };
  const privacy = { role: 'button', name: 'Navigation privée' };
  assert.equal(privateChromiumWindowProof('chrome', current, [privacy], title).layout, 'app-without-tabs');
  assert.equal(privateChromiumWindowProof('chrome', current, [privacy, { role: 'tab', name: title }], title).layout, 'one-exact-fixture-tab');
  assert.throws(() => privateChromiumWindowProof('chrome', current, [], title), /Private mode/);
  assert.throws(() => privateChromiumWindowProof('chrome', current, [privacy, { role: 'tab', name: 'foreign' }], title), /other tabs/);
  assert.throws(() => privateChromiumWindowProof('chrome', current, [privacy, { role: 'tab', name: title }, { role: 'tab', name: title }], title), /other tabs/);
  assert.throws(() => privateChromiumWindowProof('edge', { title: title + ' InPrivate' }, [{ role: 'tab', name: title }], title), /other tabs/);
  assert.equal(privateChromiumWindowProof('chrome', current, [], title, false).privacy, null);
  assert.throws(() => privateChromiumWindowProof('chrome', current, [{ role: 'tab', name: 'foreign' }], title, false), /other tabs/);
});
test('owned Edge helper errors expose only a synthetic stage/type without commands, CLIXML or private details', { skip }, async () => {
  await assert.rejects(() => edgeBirthProbeSelfTest(true), error => {
    assert.match(error.message, /offline-test\/[A-Za-z]+/);
    assert.ok(!/CLIXML|EncodedCommand|private-fixture-detail|CgAk|pwsh\.exe/.test(error.message + error.stack));
    assert.equal(error.cause, undefined); assert.equal(error.stderr, undefined); assert.equal(error.cmd, undefined);
    return true;
  });
});
