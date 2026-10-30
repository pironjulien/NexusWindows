import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import path from 'node:path';

const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !executable ? 'Final NEXUS_NATIVE_TEST_EXE required' : false;
test('native Claude UIA append reads the same live field, refuses changed/secret/truncated values and never replays', { skip }, () => {
  const assembly = path.join(path.dirname(executable), path.basename(executable).replace(/\.exe$/i, '.dll'));
  const script = [
    "$ErrorActionPreference='Stop'",
    "if([Environment]::Version.Major -lt 10){throw 'PowerShell on .NET 10 is required for this reflection-only test'}",
    '$assembly=[Reflection.Assembly]::LoadFrom($env:NEXUS_APPEND_TEST_ASSEMBLY)',
    "$method=$assembly.GetType('ComWorker',$true).GetMethod('AppendValueSelfTest',[Reflection.BindingFlags]'Static,NonPublic')",
    "if($null -eq $method){throw 'Native UIA append selftest absent'}",
    '$method.Invoke($null,@())|ConvertTo-Json -Compress',
  ].join('\n');
  const result = JSON.parse(execFileSync('pwsh.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    env: { ...process.env, NEXUS_APPEND_TEST_ASSEMBLY: assembly }, windowsHide: true, encoding: 'utf8', timeout: 15000,
  }));
  assert.equal(result.ok, true); assert.equal(result.gui, false); assert.ok(result.checks >= 14);
});
