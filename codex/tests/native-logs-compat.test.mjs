import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';

test('Claude logs preserve legacy history and native techniques without typed data', {
  skip: process.platform !== 'win32' || !process.env.NEXUS_NATIVE_TEST_EXE,
}, () => {
  const result = JSON.parse(execFileSync(process.env.NEXUS_NATIVE_TEST_EXE, ['--mcp-logs-selftest'], {
    windowsHide: true, timeout: 15000, encoding: 'utf8',
  }));
  for (const field of ['ok', 'history', 'readonly', 'typedTextOmitted', 'techniques']) assert.equal(result[field], true);
});
