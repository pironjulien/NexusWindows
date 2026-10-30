import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import path from 'node:path';

const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !executable ? 'Final NEXUS_NATIVE_TEST_EXE required' : false;

test('native inspection brackets a fresh tree, retains its stable frame and rejects window/content replacement', { skip }, () => {
  const assembly = path.join(path.dirname(executable), path.basename(executable).replace(/\.exe$/i, '.dll'));
  // Reflection on an internal offline test seam avoids adding any CLI mode or GUI/test flags to production.
  const script = [
    "$ErrorActionPreference='Stop'",
    "if([Environment]::Version.Major -lt 10){throw 'PowerShell on .NET 10 is required for this reflection-only test'}",
    '$assembly=[Reflection.Assembly]::LoadFrom($env:NEXUS_INSPECT_TEST_ASSEMBLY)',
    "$method=$assembly.GetType('Program',$true).GetMethod('InspectionFrameSelfTest',[Reflection.BindingFlags]'Static,NonPublic')",
    "if($null -eq $method){throw 'Final native inspection selftest absent'}",
    '$method.Invoke($null,@())|ConvertTo-Json -Compress',
  ].join('\n');
  const result = JSON.parse(execFileSync('pwsh.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    env: { ...process.env, NEXUS_INSPECT_TEST_ASSEMBLY: assembly }, windowsHide: true, encoding: 'utf8', timeout: 15000,
  }));
  assert.equal(result.ok, true);
  assert.ok(result.checks >= 10);
});
