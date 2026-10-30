import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {execFileSync} from 'node:child_process';

const executable=process.env.NEXUS_NATIVE_TEST_EXE;
test('pointer messages restore the recipient DPI context on success, partial failure and nesting',{
  skip:process.platform!=='win32'||!executable,
},()=>{
  const assembly=path.join(path.dirname(executable),path.basename(executable).replace(/\.exe$/i,'.dll'));
  const source=["$ErrorActionPreference='Stop'",
    '$assembly=[Reflection.Assembly]::LoadFrom($env:NEXUS_POINTER_TEST_ASSEMBLY)',
    "$method=$assembly.GetType('Pointer',$true).GetMethod('RecipientContextSelfTest',[Reflection.BindingFlags]'Static,NonPublic')",
    '$method.Invoke($null,@())|ConvertTo-Json -Compress'].join('\n');
  const result=JSON.parse(execFileSync('pwsh.exe',['-NoProfile','-NonInteractive','-Command',source],{
    windowsHide:true,encoding:'utf8',timeout:15000,env:{...process.env,NEXUS_POINTER_TEST_ASSEMBLY:assembly},
  }));
  assert.equal(result.ok,true);assert.ok(result.checks>=4);assert.equal(result.gui,false);assert.equal(result.inputSent,false);
});
