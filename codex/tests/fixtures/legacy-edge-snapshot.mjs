import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export async function fixtureReadOnlyChild(executable, args, request, label, env = process.env) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'], env });
    let output = '', settled = false;
    const timer = setTimeout(() => { child.kill(); finish(new Error(`${label} timed out; no input sent`)); }, 30000);
    function finish(error, value) { if (settled) return; settled = true; clearTimeout(timer); error ? reject(error) : resolve(value); }
    child.on('error', () => finish(new Error(`${label} unavailable; no input sent`)));
    child.stdin.on('error', () => finish(new Error(`${label} input unavailable; no desktop input sent`)));
    child.stderr.resume();
    child.stdout.on('data', value => {
      output += value;
      if (output.length > 2 * 1024 * 1024) { child.kill(); finish(new Error(`${label} output exceeded diagnostic boundary`)); }
    });
    child.on('close', code => {
      try {
        const value = JSON.parse(output);
        if (code || value.ok !== true) {
          const kind = /^[A-Za-z0-9]+$/.test(value.errorType ?? '') ? value.errorType : 'ReadUnavailable';
          finish(new Error(`${label} refused (${kind}); no input sent`)); return;
        }
        finish(null, value);
      } catch { finish(new Error(`${label} refused or returned no bounded result; no input sent`)); }
    });
    child.stdin.end(request === undefined ? '' : JSON.stringify(request));
  });
}

export function legacyEdgeSnapshot(python, spec) {
  return fixtureReadOnlyChild(python, ['-I', '-B', fileURLToPath(new URL('./legacy-edge-snapshot.py', import.meta.url))], spec, 'Historical fixture snapshot');
}

export function legacyEdgeSnapshotSelfTest(python) {
  return fixtureReadOnlyChild(python, ['-I', '-B', fileURLToPath(new URL('./legacy-edge-snapshot.py', import.meta.url)), '--self-test'], undefined, 'Historical fixture pure test');
}
