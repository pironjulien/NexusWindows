import test from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {helperPath} from '../lib/runtime.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const executable = process.env.NEXUS_FOREGROUND_EXE ?? helperPath(root);

test('observed identities, French contracts, image points and stable deltas', {skip: !executable && 'Native executable absent'}, () => {
  const report = JSON.parse(execFileSync(executable, ['--mcp-selftest'], {windowsHide: true, timeout: 30000}));
  assert.equal(report.ok, true);
  assert.ok(report.legacyObservationAssertions >= 21, 'run the compiled regression scenarios, not the old installed binary');
  assert.ok(report.fallbackTraceAssertions >= 11, 'trace the actual fallback seam, including unchanged exceptions and unknown outcomes');
});
