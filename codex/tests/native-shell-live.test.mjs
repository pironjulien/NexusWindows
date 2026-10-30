// Explicit opt-in only. This test opens the real Windows panels through Claude MCP,
// performs one neutral wait, observes them, and verifies normal foreground return.
// Run serially after the common build: NEXUS_NATIVE_SHELL_LIVE=1 and NEXUS_NATIVE_TEST_EXE.
// The independent oracle reads HWND/PID/process birth/path/AUMID/LastInput only.
// No click, text, setting change, notification contents, screenshot or raw MCP reply is logged.
import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, readdir, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const executable = process.env.NEXUS_NATIVE_TEST_EXE;
const optIn = process.env.NEXUS_NATIVE_SHELL_LIVE === '1';
const skip = process.platform !== 'win32' || !executable || !optIn
  ? 'Opt-in: set NEXUS_NATIVE_SHELL_LIVE=1 and NEXUS_NATIVE_TEST_EXE; run serially' : false;
const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

// Values emitted below always come from these fixed allowlists, never from an exception/UI substring.
const exceptionTypes = ['ActionRefusedException', 'EmergencyStopException', 'JulienActiveException', 'AfterInputException',
  'CaptureUnavailableException', 'BackgroundNotApplicableException', 'InvalidOperationException', 'ArgumentException',
  'ArgumentOutOfRangeException', 'NullReferenceException', 'TimeoutException', 'JsonException', 'Win32Exception'];
const stackMethods = [
  ...['RunPanelAct', 'RunShellAct', 'RunClaudeCompatibleAct', 'PrepareShellObservation', 'ActNative', 'AnchorNative',
    'PerformNative', 'InspectNative', 'NativeRequest', 'ResolveWindow', 'ObserveCompatibleNative', 'FinishLegacyAct',
    'CloseShellPanel', 'CloseShellPanelInOrder', 'PrepareNativeTarget', 'Elements', 'LookMemory', 'PublishNativeIndices',
    'ReadNativeCapture', 'ReadShellWindow', 'FindShellPanel', 'AwaitShellPanel', 'SendShellKeys', 'CallTool', 'CallToolLogged',
    'StructureNative', 'BoundaryNative', 'RoutineCompatibilityArgs', 'ValidateRoutineControls'].map(method => `McpServer.${method}`),
  ...['Check', 'Open', 'BeginLegacyShell', 'BindLegacyShell', 'ReadIdentity', 'ReturnForeground', 'Dispose']
    .map(method => `McpServer.NativeInteraction.${method}`),
  ...['First', 'FirstOrDefault', 'Single', 'SingleOrDefault', 'Max', 'ToDictionary', 'TryGetFirst', 'TryGetSingle']
    .map(method => `System.Linq.Enumerable.${method}`),
  'System.Linq.ThrowHelper.ThrowNoElementsException', 'System.Linq.ThrowHelper.ThrowMoreThanOneElementException',
];
const failureCategories = [
  ['foreground-required', /foreground=true|needs foreground:true/i],
  ['emergency-stop', /emergency stop|arrêt d'urgence/i],
  ['user-pause-unavailable', /presence is not known reliably|user presence|presence unavailable/i],
  ['user-active-or-interrupted', /user is typing|user took|user is using|typing or moving|Julien took/i],
  ['empty-native-sequence', /sequence contains no elements|sequence contains no matching element/i],
  ['ambiguous-native-sequence', /sequence contains more than one|ambiguous|several .* match/i],
  ['native-tree-incomplete', /incomplete native observation|no current window frame|no native|tree.*unavailable/i],
  ['identity-changed', /identity changed|process birth|application identity|process changed|process ended/i],
  ['observation-invalid', /observation.*expired|observation.*consumed|look at.*before acting|named window differs/i],
  ['panel-opening-timeout', /did not open within|panel opening timed out/i],
  ['foreground-refused', /refuses to bring|foreground.*refus|panel.*foreground|foreground.*changed/i],
  ['capture-unavailable', /blank image|capture.*unavailable|screenshot.*unavailable/i],
  ['geometry-or-publication-changed', /geometry changed|structure changed|moved or resized|frame.*changed|publication/i],
  ['missing-argument-or-null', /object reference not set|value cannot be null|key .* not present|required.*missing/i],
];
function classifyRefusal(response) {
  if (!response?.isError && !response?.error) return { isError: false, categories: [], types: [] };
  const text = (response.content ?? []).filter(block => block.type === 'text' && typeof block.text === 'string')
    .map(block => block.text).join('\n'); // In memory only; never persisted or printed.
  const categories = failureCategories.filter(([, pattern]) => pattern.test(text)).map(([category]) => category);
  return { isError: true, categories: categories.length ? categories : ['unclassified-native-refusal'],
    types: exceptionTypes.filter(type => text.includes(type)) };
}
function safeStackLine(line) {
  const types = exceptionTypes.filter(type => line.includes(type));
  const normalized = line.replaceAll('+NativeInteraction', '.NativeInteraction');
  const methods = stackMethods.filter(method => {
    const escaped = method.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return new RegExp(`(?:^|\\s)${escaped}(?:\\[|\\(|$)`).test(normalized);
  });
  return types.length || methods.length ? { types, methods } : null;
}
function panelScalars(samples) {
  const allowed = new Set(['quick settings', 'notifications', 'start', 'search']);
  const seen = new Map();
  for (const sample of samples) for (const panel of sample.panels ?? []) {
    if (!allowed.has(panel.name) || !Number.isSafeInteger(panel.hwnd) || !Number.isSafeInteger(panel.pid)) continue;
    const key = `${panel.name}|${panel.hwnd}|${panel.pid}|${panel.started}`;
    seen.set(key, { panel: panel.name, hwnd: panel.hwnd, pid: panel.pid,
      birth: /^\d{4}-\d{2}-\d{2}T[0-9:.]+Z$/.test(panel.started) ? panel.started : null,
      foregroundWitnessed: (seen.get(key)?.foregroundWitnessed ?? false) || sample.foreground === panel.hwnd });
  }
  return [...seen.values()];
}

const oracleScript = String.raw`
param([Parameter(Mandatory=$true)][string]$StopFile)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class ShellReadOracle {
  [StructLayout(LayoutKind.Sequential)] struct Rect { public int left, top, right, bottom; }
  [StructLayout(LayoutKind.Sequential)] struct InputInfo { public uint size, time; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref InputInfo info);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr window, StringBuilder name, int count);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);
  public sealed class Panel { public string name, path, app, started; public long hwnd; public uint pid; public int height; }
  public sealed class Sample { public bool ok; public long foreground; public uint lastInput; public Panel[] panels; }
  static string App(Process process, string image) {
    uint length=0;
    if(GetApplicationUserModelId(process.Handle,ref length,null)==122 && length>0) {
      var text=new StringBuilder((int)length);
      if(GetApplicationUserModelId(process.Handle,ref length,text)==0) return text.ToString();
    }
    return "process:"+image;
  }
  public static Sample Read() {
    var input=new InputInfo { size=(uint)Marshal.SizeOf(typeof(InputInfo)) };
    var sample=new Sample { ok=GetLastInputInfo(ref input),foreground=GetForegroundWindow().ToInt64(),lastInput=input.time };
    string root=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    string[] names={"quick settings","notifications","start","search"};
    string[] classes={"ControlCenterWindow","Windows.UI.Core.CoreWindow","Windows.UI.Core.CoreWindow","Windows.UI.Core.CoreWindow"};
    string[] binaries={@"System32\ShellHost.exe",@"SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe",@"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe",@"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe"};
    string[] packages={null,"Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy","Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy","MicrosoftWindows.Client.CBS_cw5n1h2txyewy"};
    var found=new List<Panel>(); var seen=new HashSet<IntPtr>(); IntPtr window=IntPtr.Zero;
    while((window=FindWindowExW(IntPtr.Zero,window,null,null))!=IntPtr.Zero && seen.Add(window)) {
      if(!IsWindowVisible(window)) continue;
      int cloaked; if(DwmGetWindowAttribute(window,14,out cloaked,sizeof(int))==0 && cloaked!=0) continue;
      var cls=new StringBuilder(256); GetClassNameW(window,cls,cls.Capacity);
      Rect rect; if(!GetWindowRect(window,out rect)) continue;
      for(int i=0;i<names.Length;i++) {
        if(cls.ToString()!=classes[i] || (i==3 ? window.ToInt64()!=sample.foreground : rect.bottom-rect.top<400)) continue;
        uint pid; GetWindowThreadProcessId(window,out pid); if(pid==0) continue;
        try { using(var process=Process.GetProcessById((int)pid)) {
          string image=process.MainModule.FileName;
          string expected=Path.GetFullPath(Path.Combine(root,binaries[i]));
          if(!string.Equals(Path.GetFullPath(image),expected,StringComparison.OrdinalIgnoreCase)) continue;
          string app=App(process,image);
          bool official=string.Equals(app,"process:"+expected,StringComparison.OrdinalIgnoreCase)
            || packages[i]!=null && app.StartsWith(packages[i]+"!",StringComparison.OrdinalIgnoreCase) && app.Length>packages[i].Length+1;
          if(!official) continue;
          found.Add(new Panel {name=names[i],hwnd=window.ToInt64(),pid=pid,path=image,app=app,
            started=process.StartTime.ToUniversalTime().ToString("O"),height=rect.bottom-rect.top});
        }} catch(ArgumentException) {} catch(InvalidOperationException) {} catch(System.ComponentModel.Win32Exception) {}
      }
    }
    sample.panels=found.ToArray();return sample;
  }
}
'@
while (-not (Test-Path -LiteralPath $StopFile)) {
  [ShellReadOracle]::Read() | ConvertTo-Json -Compress -Depth 5
  Start-Sleep -Milliseconds 50
}
`;

function startLines(command, args, env, receive, diagnostic = () => {}) {
  const child = spawn(command, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env });
  let errorLine = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', chunk => {
    errorLine += chunk;
    let newline;
    while ((newline = errorLine.indexOf('\n')) >= 0) {
      const safe = safeStackLine(errorLine.slice(0, newline)); errorLine = errorLine.slice(newline + 1);
      if (safe) diagnostic(safe);
    }
    if (errorLine.length > 16384) errorLine = ''; // Oversized/unrecognized raw data is discarded.
  });
  child.stderr.on('end', () => { const safe = safeStackLine(errorLine); errorLine = ''; if (safe) diagnostic(safe); });
  child.stdin.on('error', () => { /* A dead server is reported by its sanitized response timeout. */ });
  const ended = new Promise(resolve => { child.once('exit', code => resolve(code)); child.once('error', () => resolve(-1)); });
  const reader = readline.createInterface({ input: child.stdout });
  reader.on('line', line => { try { receive(JSON.parse(line)); } catch { /* A missing valid response produces a sanitized timeout. */ } });
  return { child, ended, reader };
}
async function endProcess(process, signalNormalEnd) {
  if (!process) return;
  await signalNormalEnd();
  await Promise.race([process.ended, new Promise(resolve => setTimeout(resolve, 1618))]);
  if (process.child.exitCode == null && !process.child.killed) { process.child.kill(); await process.ended; }
  process.reader.close();
}

test('native Claude real shell panels: neutral wait, official identity, held observation and normal return', { skip, timeout: 240000 }, async t => {
  const state = await mkdtemp(path.join(os.tmpdir(), 'nexus-shell-live-'));
  const stopFile = path.join(state, 'oracle.stop');
  const script = path.join(state, 'shell-read-oracle.ps1');
  const samples = [], sampleWaiters = [], requests = new Map(), diagnostics = [], evidence = [];
  let sequence = 0, requestId = 0, server, oracle;
  let failed = false;
  function sampleAfter(after, predicate = () => true, timeout = 10000) {
    const found = samples.find(sample => sample.sequence > after && predicate(sample));
    if (found) return Promise.resolve(found);
    return new Promise((resolve, reject) => {
      const waiter = { after, predicate, resolve, timer: setTimeout(() => {
        sampleWaiters.splice(sampleWaiters.indexOf(waiter), 1); reject(new Error('Read-only shell oracle timed out; no contents recorded'));
      }, timeout) };
      sampleWaiters.push(waiter);
    });
  }
  const freshSample = () => sampleAfter(sequence);
  async function rpc(method, params = {}) {
    const id = ++requestId;
    const pending = new Promise((resolve, reject) => {
      requests.set(id, { resolve, timer: setTimeout(() => { requests.delete(id); reject(new Error('Native shell MCP response timed out; no reply contents recorded')); }, 45000) });
    });
    server.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    const response = await pending;
    assert.equal(Boolean(response.error), false, 'MCP protocol request must succeed');
    return response.result;
  }
  async function call(args) { return rpc('tools/call', { name: 'act', arguments: args }); }
  try {
    await writeFile(script, oracleScript, 'utf8');
    oracle = startLines('powershell.exe', ['-NoProfile', '-NonInteractive', '-File', script, '-StopFile', stopFile], process.env, sample => {
      if (!Array.isArray(sample.panels)) return;
      sample.sequence = ++sequence; samples.push(sample);
      for (const waiter of [...sampleWaiters]) if (sample.sequence > waiter.after && waiter.predicate(sample)) {
        clearTimeout(waiter.timer); sampleWaiters.splice(sampleWaiters.indexOf(waiter), 1); waiter.resolve(sample);
      }
    });
    const initial = await freshSample();
    assert.equal(initial.ok, true, 'GetLastInputInfo must be readable before input is authorized');
    if (!initial.foreground || initial.panels.length > 0) {
      t.skip('Existing shell panel or no foreground: no panel was toggled; live acceptance remains unproved');
      return;
    }
    server = startLines(executable, ['--mcp', '--agent', 'claude'], { ...process.env,
      NEXUS_WINDOWS_HOME: state, NEXUS_SHARED_HOME: state, NEXUS_ROUTINES_MIRROR: '',
      NEXUS_JOURNAL_HOME: path.join(state, 'journal'), NEXUS_GARDE_FOUS: path.join(repository, 'commun', 'garde-fous.json'),
      NEXUS_WINDOWS_DEBUG_STACK: '1', // Test-only; stderr is reduced immediately to the fixed method/type allowlists.
    }, response => {
      const pending = requests.get(response.id);
      if (pending) { clearTimeout(pending.timer); requests.delete(response.id); pending.resolve(response); }
    }, entry => { if (diagnostics.length < 512) diagnostics.push(entry); });
    await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'native-shell-live-acceptance', version: '1' } });
    for (const [name, then, expectedBinary, expectedPackage] of [
      ['quick settings', 'both', String.raw`System32\ShellHost.exe`, null],
      ['notifications', 'elements', String.raw`SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe`, 'Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy'],
      ['start', 'elements', String.raw`SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe`, 'Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy'],
    ]) {
      let completed = false;
      await t.test(name, { timeout: 60000 }, async () => {
        const beforeRefusal = await freshSample();
        assert.equal(beforeRefusal.panels.length, 0, 'No pre-existing panel may be toggled');
        const refused = await call({ window: name, foreground: false, steps: [{ do: 'wait', seconds: 0.162 }], then: 'none' });
        assert.equal(refused.isError, true, 'A background panel request must fail');
        const refusalText = (refused.content ?? []).filter(block => block.type === 'text').map(block => block.text).join('\n');
        assert.equal(/foreground=true/.test(refusalText), true, 'The refusal must identify the foreground requirement');
        const afterRefusal = await freshSample();
        assert.equal(afterRefusal.lastInput, beforeRefusal.lastInput, 'Native LastInput oracle: no physical input during refusal');
        assert.equal(afterRefusal.foreground, beforeRefusal.foreground, 'Foreground unchanged during refusal');
        assert.equal(afterRefusal.panels.length, 0, 'No panel opened during refusal');

        const before = await freshSample();
        assert.equal(before.panels.length, 0, 'Live positive request starts with all panels closed');
        const diagnosticStart = diagnostics.length;
        const response = await call({ window: name, foreground: true, steps: [{ do: 'wait', seconds: 0.162 }], then, settle_ms: 162 });
        const data = response.structuredContent;
        const observation = data?.observation;
        let after = await freshSample();
        if (after.foreground !== before.foreground || after.panels.length !== 0)
          after = await sampleAfter(after.sequence, sample => sample.foreground === before.foreground && sample.panels.length === 0, 1618)
            .catch(() => samples.at(-1));
        const foregroundRestored = after.foreground === before.foreground;
        const panelClosed = after.panels.length === 0;
        const returned = ['restored', 'not-taken', 'kept-by-julien', 'none', 'refused', 'interrupted'].includes(data?.foregroundReturned)
          ? data.foregroundReturned : 'unexpected';
        const during = samples.filter(sample => sample.sequence > before.sequence);
        const shown = during.flatMap(sample => sample.panels)
          .find(panel => panel.name === name && panel.hwnd === observation?.window && panel.pid === observation?.processId && panel.started === observation?.processStartedUtc);
        const held = during.some(sample => sample.panels.some(panel => panel.name === name && panel.hwnd === observation?.window)
          && (sample.foreground === observation?.window || name === 'start' && sample.panels.some(panel => panel.name === 'search' && panel.hwnd === sample.foreground)));
        const panelsSeen = panelScalars(during);
        // Emit the independent terminal state before any result/metadata assertion can abort the test.
        // Only scalar evidence is emitted; no notification text/elements or image blocks are included.
        const proof = { panel: name, then, hwnd: Number.isSafeInteger(observation?.window) ? observation.window : null,
          pid: Number.isSafeInteger(observation?.processId) ? observation.processId : null,
          identityWitnessed: Boolean(shown), foregroundHeld: held, beforeForeground: before.foreground, afterForeground: after.foreground,
          foregroundRestored, foregroundReturned: returned, panelClosed, refusalInputUnchanged: true,
          refusal: classifyRefusal(response), nativeStack: diagnostics.slice(diagnosticStart),
          panelCountSeen: panelsSeen.length, panelsSeen };
        evidence.push(proof); t.diagnostic(JSON.stringify(proof));
        assert.equal(Boolean(response.isError), false, 'Foreground panel request must succeed; raw reply intentionally omitted');
        assert.equal(data?.status, 'success', 'Neutral wait batch must succeed');
        assert.equal(data?.steps?.length, 1, 'Exactly one neutral wait executes');
        assert.equal(data.steps[0].status, 'waited', 'Neutral wait must execute without a desktop gesture');
        assert.equal(typeof observation?.window, 'number', 'Final observation must be made while the panel is held');
        assert.equal(typeof observation?.processId, 'number', 'Observed panel PID must be real');
        assert.equal(typeof observation?.processStartedUtc, 'string', 'Observed process birth must be present');
        assert.equal(Boolean(shown), true, 'Independent oracle must witness the exact observed HWND/PID/birth while open');
        assert.equal(path.win32.normalize(shown.path).toLowerCase(), path.win32.join(process.env.SystemRoot, expectedBinary).toLowerCase(), 'Panel must use the protected official Windows executable');
        assert.equal(shown.app, observation.app, 'MCP and independent native oracle must agree on application identity');
        assert.equal(shown.app.toLowerCase() === ('process:' + shown.path).toLowerCase()
          || Boolean(expectedPackage && shown.app.toLowerCase().startsWith((expectedPackage + '!').toLowerCase())), true, 'Official process path or exact shell package family required');
        assert.equal(shown.height >= 400, true, 'Notification toast cannot stand in for a full panel');
        assert.equal(held, true, 'The official panel or its observed official Start search must actually own foreground');
        assert.equal(foregroundRestored, true, 'Independent oracle confirms original foreground return');
        assert.equal(panelClosed, true, 'Panel opened by the tool must be closed after the call');
        // CloseShellPanel sends one Escape before GiveBack; Windows can restore the original itself.
        // ReturnForeground truthfully reports not-taken when that original HWND is already active. The oracle above
        // proves the required outcome for either status; no redundant foreground gesture is required for the label.
        assert.equal(returned === 'restored' || returned === 'not-taken', true, 'Return metadata must agree with the independently restored foreground and closed panel');
        completed = true;
      });
      // A failed/interrupted request is never replayed and no later panel is opened.
      if (!completed) throw new Error('Shell acceptance stopped after the first failed panel; no subsequent shortcut sent');
      const latest = await freshSample();
      if (latest.panels.length > 0) throw new Error('Panel remained open; stopped without another shortcut or forced cleanup input');
    }
  } catch (error) {
    failed = true;
    throw error;
  } finally {
    for (const pending of requests.values()) clearTimeout(pending.timer);
    requests.clear();
    for (const waiter of sampleWaiters) clearTimeout(waiter.timer);
    await endProcess(server, async () => { server.child.stdin.end(); });
    await endProcess(oracle, async () => { await writeFile(stopFile, 'stop\n', 'utf8'); oracle.child.stdin.end(); });
    const resolved = path.resolve(state), tempRoot = path.resolve(os.tmpdir());
    if (path.dirname(resolved).toLowerCase() !== tempRoot.toLowerCase() || !path.basename(resolved).startsWith('nexus-shell-live-'))
      throw new Error('Unexpected isolated shell-test cleanup path');
    if (failed) {
      // Retain only synthetic evidence. Captures, native logs, scripts and raw replies are never kept on failure.
      // Write the proof before cleanup so even a denied/failed deletion cannot erase the failure evidence.
      const proofFile = path.join(resolved, 'shell-live-evidence.json');
      await writeFile(proofFile, JSON.stringify({ failed: true, evidence, nativeStack: diagnostics,
        panelsSeen: panelScalars(samples), limits: 'Fixed failure categories, allowlisted method/type names and native scalar identities only; no UI text or images' }, null, 2) + '\n', 'utf8');
      t.diagnostic(JSON.stringify({ syntheticEvidence: proofFile, rawRepliesInEvidence: false, capturesInEvidence: false }));
      for (const name of await readdir(resolved)) {
        if (name === 'shell-live-evidence.json') continue;
        const child = path.resolve(resolved, name);
        if (path.dirname(child).toLowerCase() !== resolved.toLowerCase()) throw new Error('Unexpected synthetic-evidence cleanup path');
        await rm(child, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
      }
    } else await rm(resolved, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 });
  }
});
