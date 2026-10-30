import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import path from 'node:path';

const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !executable ? 'Final NEXUS_NATIVE_TEST_EXE required' : false;
test('Chromium first-read warmup is bounded, uses fresh trees, respects STOP and resets misses on identity replacement', { skip }, () => {
  const assembly = path.join(path.dirname(executable), path.basename(executable).replace(/\.exe$/i, '.dll'));
  const script = [
    "$ErrorActionPreference='Stop'",
    "if([Environment]::Version.Major -lt 10){throw 'PowerShell on .NET 10 is required for this reflection-only test'}",
    '$assembly=[Reflection.Assembly]::LoadFrom($env:NEXUS_CHROMIUM_WARMUP_ASSEMBLY)',
    "$method=$assembly.GetType('ComUia',$true).GetMethod('ChromiumWarmupSelfTest',[Reflection.BindingFlags]'Static,NonPublic')",
    "if($null -eq $method){throw 'Native Chromium warmup selftest absent'}",
    '$method.Invoke($null,@())|ConvertTo-Json -Compress',
  ].join('\n');
  const result = JSON.parse(execFileSync('pwsh.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    env: { ...process.env, NEXUS_CHROMIUM_WARMUP_ASSEMBLY: assembly }, windowsHide: true, encoding: 'utf8', timeout: 15000,
  }));
  assert.equal(result.ok, true);
  assert.equal(result.gui, false);
  assert.ok(result.checks >= 24);
  assert.equal(result.waitBudgetMs, 1500);
  assert.equal(result.pollMs, 250);
  assert.equal(result.failureCooldownMs, 60000);
});
