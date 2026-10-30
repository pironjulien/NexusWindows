import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import path from 'node:path';

const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const skip = process.platform !== 'win32' || !executable ? 'Final NEXUS_NATIVE_TEST_EXE required' : false;
test('Claude Chromium focus publishes under exact identity, proves the original field and never replays accSelect', { skip }, () => {
  const assembly = path.join(path.dirname(executable), path.basename(executable).replace(/\.exe$/i, '.dll'));
  const script = [
    "$ErrorActionPreference='Stop'",
    "if([Environment]::Version.Major -lt 10){throw 'PowerShell on .NET 10 is required'}",
    '$assembly=[Reflection.Assembly]::LoadFrom($env:NEXUS_FOCUS_TEST_ASSEMBLY)',
    "$method=$assembly.GetType('ComWorker',$true).GetMethod('ChromiumFocusSelfTest',[Reflection.BindingFlags]'Static,NonPublic')",
    "if($null -eq $method){throw 'Bound Chromium focus selftest absent'}",
    '$method.Invoke($null,@())|ConvertTo-Json -Compress',
  ].join('\n');
  const result = JSON.parse(execFileSync('pwsh.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    env: { ...process.env, NEXUS_FOCUS_TEST_ASSEMBLY: assembly }, windowsHide: true, encoding: 'utf8', timeout: 15000,
  }));
  assert.equal(result.ok, true); assert.equal(result.gui, false); assert.equal(result.inputSent, false); assert.ok(result.checks >= 11);
});
test('Claude Chromium text guards 250 bounded packets and 2000 runes without a UIA proof in the rune loop', { skip }, () => {
  const assembly = path.join(path.dirname(executable), path.basename(executable).replace(/\.exe$/i, '.dll'));
  const script = [
    "$ErrorActionPreference='Stop'",
    '$assembly=[Reflection.Assembly]::LoadFrom($env:NEXUS_FOCUS_TEST_ASSEMBLY)',
    "$method=$assembly.GetType('ChromiumInput',$true).GetMethod('SimulatedPacketGuardSelfTest',[Reflection.BindingFlags]'Static,NonPublic')",
    "if($null -eq $method){throw 'Native packet guard selftest absent'}",
    '$method.Invoke($null,@())|ConvertTo-Json -Compress',
  ].join('\n');
  const result = JSON.parse(execFileSync('pwsh.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    env: { ...process.env, NEXUS_FOCUS_TEST_ASSEMBLY: assembly }, windowsHide: true, encoding: 'utf8', timeout: 15000,
  }));
  assert.equal(result.ok, true); assert.equal(result.gui, false); assert.equal(result.inputSent, false); assert.ok(result.checks >= 6);
  assert.equal(result.packets, 250); assert.equal(result.packetProofs, 250); assert.equal(result.presenceChecks, 2000);
  assert.equal(result.proofDuringRune, 0); assert.equal(result.utf16Posted, 2000);
});
