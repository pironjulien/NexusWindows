import test from 'node:test';
import assert from 'node:assert/strict';
import { legacyEdgeSnapshotSelfTest } from './fixtures/legacy-edge-snapshot.mjs';

const python = process.env.NEXUS_LEGACY_EDGE_PYTHON;
test('read-only legacy comparison preserves the exact 100ns process birth without importing UIA', { skip: !python }, async () => {
  const proof = await legacyEdgeSnapshotSelfTest(python);
  assert.equal(proof.oneTickDifference, 1); assert.equal(proof.rejectShortBirth, true);
  assert.equal(proof.gui, false); assert.equal(proof.importsLegacyProvider, false);
});
