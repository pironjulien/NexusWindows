import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';

test('native routine compatibility: permissions, refusals and private mirror isolation without GUI', {
  skip: process.platform !== 'win32' || !process.env.NEXUS_NATIVE_TEST_EXE,
}, () => {
  const result = JSON.parse(execFileSync(process.env.NEXUS_NATIVE_TEST_EXE, ['--routine-compat-selftest'], {
    windowsHide: true, timeout: 15000,
  }));
  assert.equal(result.ok, true);
  assert.equal(result.gui, false);
  assert.ok(result.assertions >= 97);
});
