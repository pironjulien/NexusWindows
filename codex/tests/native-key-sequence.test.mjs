import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';

test('native key sequence: transport, fresh guards and global proof without GUI', {
  skip: process.platform !== 'win32' || !process.env.NEXUS_NATIVE_TEST_EXE,
}, () => {
  const result = JSON.parse(execFileSync(process.env.NEXUS_NATIVE_TEST_EXE, ['--key-sequence-selftest'], {
    windowsHide: true, timeout: 15000,
  }));
  assert.equal(result.ok, true);
  assert.equal(result.gui, false);
  assert.ok(result.assertions >= 226);
});
