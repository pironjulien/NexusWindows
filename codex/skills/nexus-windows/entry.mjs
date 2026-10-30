// Portable entry shipped with the public plugin and npm package. Importing it never starts a helper.
import { fileURLToPath } from 'node:url';
import { WindowsSession, summarize } from '../../lib/session.mjs';
import { NativeUIA } from '../../lib/native.mjs';
import { emergencyStop } from '../../lib/stop.mjs';
import { helperPath, runtimeStateRoot } from '../../lib/runtime.mjs';

export { summarize };

export function createWindowsSession(sky) {
  const root = fileURLToPath(new URL('../../', import.meta.url));
  const stateRoot = runtimeStateRoot();
  const helper = helperPath(root, { stateRoot });
  if (!helper) throw new Error('NexusWindows native helper missing: run setup.ps1 or install.ps1.');
  const stop = emergencyStop();
  stop.startGuard(helper);
  return new WindowsSession(sky, { stateRoot, native: new NativeUIA(helper, { emergencyStop: stop }), emergencyStop: stop });
}
