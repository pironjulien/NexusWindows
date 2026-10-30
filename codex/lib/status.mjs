// État du produit pour l'appli Nexus (contrat « mises à jour hors conteneur » du 26/10/2026) : le serveur écrit à son
// démarrage `~\.nexus\status\windows-codex.json` {product, version, latest, state, checked_at, message}. Le serveur est
// lancé par Codex (`npx -y nexus-windows-codex@latest`) : rien à déclencher, la nouvelle version sert au prochain
// démarrage de Codex. `latest` vient du registre npm, au plus une fois par jour ; un échec garde la valeur précédente et
// le dit dans `message`. Jamais bloquant : une erreur ici n'empêche pas le serveur de démarrer.
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import https from 'node:https';
import path from 'node:path';
import { sharedDirectory } from './stop.mjs';

export const PRODUCT = 'windows-codex';
const PACKAGE = 'nexus-windows-codex';
const DAY_MS = 24 * 60 * 60 * 1000;

// node:https sans connexion persistante : avec fetch, process.exit pendant qu'une connexion du pool restait ouverte
// faisait planter Node sous Windows (« Assertion failed: !(handle->flags & UV_HANDLE_CLOSING) », mesuré le 26/10).
function npmLatest() {
  return new Promise((resolve, reject) => {
    const request = https.get(`https://registry.npmjs.org/${PACKAGE}/latest`, { agent: false, timeout: 5000, headers: { accept: 'application/json' } }, response => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', chunk => { if (body.length < 1_000_000) body += chunk; });
      response.on('end', () => {
        if (response.statusCode !== 200) return reject(new Error(`npm registry answered ${response.statusCode}`));
        try {
          const { version } = JSON.parse(body);
          if (typeof version === 'string') resolve(version); else reject(new Error('npm registry gave no version'));
        } catch (error) { reject(error); }
      });
    });
    request.on('timeout', () => request.destroy(new Error('npm registry did not answer within 5 s')));
    request.on('error', reject);
  });
}

// -1, 0 ou 1 (x.y.z numériques ; une préversion compte comme sa version).
export function compareVersions(a, b) {
  const parts = v => String(v).split('-')[0].split('.').map(n => Number.parseInt(n, 10) || 0);
  const [x, y] = [parts(a), parts(b)];
  for (let i = 0; i < 3; i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) < (y[i] ?? 0) ? -1 : 1;
  return 0;
}

export async function writeStatus({ version, env = process.env, latest = npmLatest, now = () => new Date() } = {}) {
  const directory = path.join(sharedDirectory(env), 'status');
  const file = path.join(directory, `${PRODUCT}.json`);
  let previous = {};
  try { previous = JSON.parse(await readFile(file, 'utf8')); } catch { /* premier démarrage */ }
  const at = now();
  // NEXUS_NO_UPDATE_CHECK=1 : aucune requête réseau, seule la version locale est écrite.
  const offline = env.NEXUS_NO_UPDATE_CHECK === '1';
  const fresh = offline || (previous.latest && Date.parse(previous.checked_at) > at.getTime() - DAY_MS);
  let found = fresh && !offline ? previous.latest : null, checkedAt = fresh ? previous.checked_at : previous.checked_at ?? null, failure = null;
  await mkdir(directory, { recursive: true });
  // Sous Windows, rename échoue (EPERM, EBUSY, EACCES) tant qu'un lecteur tient le fichier (l'appli Nexus, un autre
  // serveur Codex) : nouvel essai, et le fichier temporaire est toujours retiré (il en restait des dizaines, 27/10).
  const save = async status => {
    const temporary = `${file}.${process.pid}.tmp`;
    try {
      await writeFile(temporary, JSON.stringify(status, null, 2));
      for (let attempt = 0; ; attempt++) {
        try { await rename(temporary, file); break; }
        catch (error) {
          if (attempt >= 4 || !['EPERM', 'EBUSY', 'EACCES'].includes(error.code)) throw error;
          await new Promise(resolve => setTimeout(resolve, 50 * (attempt + 1)));
        }
      }
    } finally {
      await rm(temporary, { force: true });
    }
    return status;
  };
  if (!fresh) {
    // Écrit d'abord sans attendre le registre : un serveur arrêté aussitôt laisse quand même sa version.
    await save({ product: PRODUCT, version, latest: previous.latest ?? null, state: 'checking', checked_at: checkedAt,
      message: `Running ${version}; checking the latest version.` });
    try { found = await latest(); checkedAt = at.toISOString(); }
    catch (error) { found = previous.latest ?? null; failure = error.message; }
  }
  // Valeur du jour plus ancienne que la version qui tourne (publiée depuis le contrôle) : celle-ci est la dernière connue.
  if (found && compareVersions(found, version) < 0) found = version;
  const newer = found && compareVersions(found, version) > 0;
  const message = offline ? `Running ${version}; latest version check disabled (NEXUS_NO_UPDATE_CHECK).`
    : failure ? `Latest version not checked (${failure}); running ${version}.`
    : newer ? `Version ${found} is available: Codex uses it at its next start.`
    : `Up to date (${version}).`;
  // « idle » : le serveur tourne ; un contrôle de version manqué ne l'empêche pas (dit dans message).
  const status = { product: PRODUCT, version, latest: found, state: 'idle', checked_at: checkedAt, message };
  return save(status);
}
