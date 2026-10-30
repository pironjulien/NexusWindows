// PowerShell sources of the tests run as script FILES (-File), never as -EncodedCommand or downloaded text: antivirus
// heuristics flag encoded command lines. Each source goes to a private temporary folder removed when the process exits.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

let folder;
export function scriptFile(source) {
  if (!folder) {
    folder = mkdtempSync(path.join(os.tmpdir(), 'nexuswindows-ps-'));
    process.once('exit', () => rmSync(folder, { recursive: true, force: true }));
  }
  const file = path.join(folder, `${randomUUID()}.ps1`);
  writeFileSync(file, '\ufeff' + source, 'utf8'); // BOM: Windows PowerShell 5.1 reads the file as UTF-8
  return file;
}

// Arguments that run `source` as a script file, after the given PowerShell options.
export const fileArguments = (source, ...options) => ['-NoProfile', '-NonInteractive', ...options, '-ExecutionPolicy', 'Bypass', '-File', scriptFile(source)];
