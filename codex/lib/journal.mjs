// Journal commun des trois Nexus Windows (NexusHarness/NexusShared/journal/FORMAT.md) : une ligne par action, sans texte
// d'écran (ni texte tapé, ni libellé, ni titre). Écriture synchrone et silencieuse : un journal ne bloque jamais une
// action, y compris dans un runtime REPL sans objet global `process` (relecture de Codex du 23/10).
import { appendFileSync, mkdirSync, readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// Version de l'outil : celle de package.json (jamais écrite en dur, comme stop.mjs).
const VERSION = (() => {
  try { return JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version ?? 'unknown'; }
  catch { return 'unknown'; }
})();
const ISSUES = {
  verified: '✓', already: '=', 'already-satisfied': '=', observed: '~', 'sent-unconfirmed': '~', unmet: '~',
  unknown: '~', interrupted: '~', refused: 'refus', 'confirmation-required': 'refus', 'julien-active': 'différé',
  deferred: 'différé', stopped: 'arrêt',
};

// Actions reconnues (liste fermée) : toute autre valeur devient « invalid », jamais un texte libre (relecture de Codex).
const KINDS = new Set(['click', 'setValue', 'typeText', 'key', 'secondary', 'scroll', 'drag', 'select', 'menu', 'contextMenu',
  'activate', 'openSettings', 'openFolder', 'launch', 'close']);

const currentEnv = () => globalThis.process?.env ?? {};

// Sans `process` (REPL), LOCALAPPDATA est sa valeur par défaut de Windows : le journal reste là où la revue le lit.
export function journalRoot(env = currentEnv()) {
  const local = env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
  const base = env.NEXUS_JOURNAL_HOME || path.join(local, 'NexusJournal');
  return path.join(base, 'windows', 'codex');
}

// Application : nom de l'exécutable (« process:C:\...\mspaint.exe » → mspaint.exe) ou identifiant Store, en minuscules.
export function appName(app) {
  if (!app) return null;
  const raw = String(app).startsWith('process:') ? path.win32.basename(String(app).slice('process:'.length)) : String(app);
  return raw.toLowerCase() || null;
}

// Cause d'un échec : le début du message seulement, coupé au premier guillemet, deux-points, parenthèse ou crochet (ce
// qui suit peut venir de l'écran : noms, options, valeurs), 80 caractères au plus ; null sans erreur. Même règle que
// erreur_propre de NexusShared/journal/journal.py (jusqu'au 27/10, l'erreur n'était jamais écrite ici).
const CUT = /[«"“'’(\[:{]/;
export function erreurPropre(error) {
  if (error === null || error === undefined || error === '') return null;
  const textOf = typeof error === 'string' ? error : (error?.message ?? String(error));
  const debut = String(textOf).split(/\s+/).join(' ').split(CUT, 1)[0].replace(/^[\s.,;-]+|[\s.,;-]+$/g, '');
  return debut.slice(0, 80) || 'error';
}

// Premier plan : le fait OBSERVÉ par la session (foregroundTaken, entrée physique) quand il est connu ; sinon la
// technique (physical:…, foreground:…).
export function journalLine({ session, app, kind, status, technique, durationMs, foreground = null, error = null, now = new Date() }) {
  return {
    v: 1, ts: now.toISOString(), agent: 'codex', outil: VERSION, appel: session ?? null, app: appName(app), cadre: null,
    action: KINDS.has(kind) ? kind : 'invalid',
    technique: typeof technique === 'string' ? technique.slice(0, 60) : null, issue: ISSUES[status] ?? '~',
    // Le symbole commun ~ regroupe plusieurs issues : garder aussi le statut exact, dans une liste fermee sans
    // contenu d'ecran. Un prochain diagnostic pourra distinguer unmet, sent-unconfirmed et unknown.
    statut: Object.hasOwn(ISSUES, status) ? status : 'unknown',
    ms: Math.round(Number(durationMs) || 0),
    premier_plan: typeof foreground === 'boolean' ? foreground : /^(physical|foreground)/.test(technique ?? ''),
    erreur: erreurPropre(error),
  };
}

export function writeJournal(line, env = undefined) {
  try {
    env ??= currentEnv();
    // Sous node --test sans dossier de test : rien (les tests ne remplissent jamais le vrai journal de Julien).
    if (env.NODE_TEST_CONTEXT && !env.NEXUS_JOURNAL_HOME) return;
    const dir = journalRoot(env);
    mkdirSync(dir, { recursive: true });
    appendFileSync(path.join(dir, `${line.ts.slice(0, 7)}.jsonl`), JSON.stringify(line) + '\n', 'utf8');
  } catch { /* journal : jamais bloquant */ }
}
