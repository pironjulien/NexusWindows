// Procédures vérifiées, distinctes des recettes unitaires historiques. Le fichier synchronisé ne contient que
// la définition : jamais les paramètres de texte, titres de fenêtres, statistiques, confirmations ou accords.
import { readFile, writeFile, mkdir, rename, unlink } from 'node:fs/promises';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { digest, validateSelector, validateExpectation, uniqueElement } from './tree.mjs';
import { withMemoryLock } from './memory.mjs';

const PARAM = /\{\{\s*([\p{L}_][\p{L}\p{N}_]*)\s*\}\}/gu;
const NAME = /^[\p{L}\p{N}][\p{L}\p{N} ._-]{0,59}$/u;
const HASH = /^[a-f0-9]{64}$/;
const FIELDS = {
  click: ['selector', 'button', 'count'], setValue: ['selector', 'value'], typeText: ['focus', 'text'], key: ['key'],
  select: ['selector', 'option'], menu: ['path'], secondary: ['selector', 'action'], scroll: ['selector', 'scrollX', 'scrollY'],
  wait: [], hover: ['selector'],
};
const NAMED = new Set(['button', 'tab', 'menuitem', 'checkbox', 'radio', 'combobox', 'link', 'treeitem', 'listitem']);
const definitionFields = ['steps', 'revision', 'anchors', 'structureHash', 'saved', 'coverage', 'verifiedTransitions'];
const clone = value => structuredClone(value);
const object = value => value && typeof value === 'object' && !Array.isArray(value);
const parametersIn = value => [...(JSON.stringify(value) ?? '').matchAll(PARAM)].map(m => m[1]);
const templateOnly = value => parametersIn(value).length > 0 && !value.replace(PARAM, '').trim();
const keyOf = element => [element.role, element.automationId ?? '', NAMED.has(element.role) ? element.name : ''];

export function applicationKey(app) {
  if (typeof app !== 'string' || !app) throw new Error('A bound application is required');
  return app.startsWith('process:') ? 'process:' + path.win32.basename(app.slice(8)).toLowerCase() : app.toLowerCase();
}
export function routineName(name) {
  if (typeof name !== 'string' || !NAME.test(name)) throw new Error('Routine name: 1–60 letters, digits, spaces, dots, underscores or hyphens');
  return name;
}
// Empreinte sémantique portable : ni HWND, ni runtimeId, ni coordonnées, ni titre/document/valeur utilisateur.
// L'identité d'exécution exacte est toujours contrôlée par WindowsSession puis le helper juste avant l'entrée.
export function routineAnchor(step, observation) {
  const selector = step.selector ?? step.focus;
  const focusedIndex = Number(/^\s*(\d+)\s/.exec(observation.accessibility?.focused_element ?? '')?.[1]);
  const focused = step.kind === 'key' ? observation.elements.find(e => e.focused === true || e.index === focusedIndex) : null;
  if (!selector && !focused) return digest(JSON.stringify(keyOf(observation.elements[0])));
  const target = selector ? uniqueElement(observation.elements, selector) : focused;
  return digest(JSON.stringify([keyOf(target), target.ancestors.map(i => keyOf(observation.elements.find(e => e.index === i)))]));
}

export function routineSteps(steps) {
  if (!Array.isArray(steps) || !steps.length) throw new Error('A routine needs at least one step');
  return steps.map((original, index) => {
    if (!object(original)) throw new Error(`Step ${index}: invalid routine step`);
    const step = clone(original);
    // Une autorisation ne fait jamais partie d'une définition, même si le geste original en avait une.
    delete step.confirm; delete step.accord;
    if (!Object.hasOwn(FIELDS, step.kind) || Object.keys(step).some(k => !['kind', 'expect', 'timeoutMs', 'refresh', 'fallback', ...FIELDS[step.kind]].includes(k))) throw new Error(`Step ${index}: only semantic routine steps can be saved`);
    if (step.selector) validateSelector(step.selector);
    if (step.focus) validateSelector(step.focus);
    if (['click', 'secondary', 'setValue', 'select', 'scroll', 'hover'].includes(step.kind) && !step.selector) throw new Error(`Step ${index}: a semantic selector is required`);
    if (step.kind === 'typeText' && !step.focus) throw new Error(`Step ${index}: a semantic focus selector is required`);
    for (const selector of [step.selector, step.focus, step.expect?.selector]) {
      if (selector?.ancestor?.window !== undefined) throw new Error(`Step ${index}: a saved selector cannot contain a window handle`);
      for (const part of [selector, selector?.ancestor]) {
        if (part?.name !== undefined && ['window', 'document'].includes(part.role)) throw new Error(`Step ${index}: a transferable selector cannot contain a window or document title`);
      }
    }
    // Preuves naturelles exactes ; leur résultat est contrôlé par la session normale, jamais supposé.
    if (!step.expect && step.kind === 'setValue') step.expect = { selector: clone(step.selector), field: 'value', equals: step.value };
    if (!step.expect && step.kind === 'select') step.expect = { selector: clone(step.selector), field: 'value', equals: step.option };
    if (!step.expect && step.kind === 'secondary' && ['Expand', 'Collapse'].includes(step.action)) step.expect = { selector: clone(step.selector), field: 'expanded', equals: step.action === 'Expand' };
    validateExpectation(step.expect);
    for (const [field, value] of Object.entries(step)) {
      if (['text', 'value'].includes(field) && ['typeText', 'setValue'].includes(step.kind)) continue;
      if (field === 'expect') {
        for (const [part, inner] of Object.entries(value)) if (!['equals', 'includes'].includes(part) && parametersIn(inner).length) throw new Error(`Step ${index}: parameters cannot change a postcondition target or field`);
      } else if (parametersIn(value).length) throw new Error(`Step ${index}: parameters are allowed only in typed text and text postconditions`);
    }
    for (const field of step.kind === 'typeText' ? ['text'] : step.kind === 'setValue' ? ['value'] : []) {
      if (typeof step[field] !== 'string' || (step[field].trim() && !templateOnly(step[field]))) throw new Error(`Step ${index}: typed text must contain only {{parameter}} templates`);
    }
    // Les valeurs de champs peuvent être du contenu utilisateur. Seul un modèle, une remise à vide ou le libellé
    // d'une option statique sélectionnée peut les conserver dans une procédure transférable.
    if (step.expect.field === 'value') for (const field of ['equals', 'includes']) {
      const value = step.expect[field];
      if (typeof value === 'string' && value.trim() && !templateOnly(value) && !(step.kind === 'select' && value === step.option)) throw new Error(`Step ${index}: a field-value postcondition must use a text parameter`);
    }
    return step;
  });
}

export function expandRoutine(steps, params = {}) {
  const templates = routineSteps(steps);
  if (!object(params) || Object.values(params).some(v => typeof v !== 'string')) throw new Error('Routine parameters must be an object of text values');
  const wanted = new Set(parametersIn(templates));
  const unknown = Object.keys(params).filter(k => !wanted.has(k));
  const missing = [...wanted].filter(k => !Object.hasOwn(params, k));
  if (unknown.length || missing.length) throw new Error(`Routine parameters: ${missing.length ? 'missing ' + missing.join(', ') : ''}${unknown.length ? '; unknown ' + unknown.join(', ') : ''}`);
  const fill = value => typeof value === 'string' ? value.replace(PARAM, (_, name) => params[name])
    : Array.isArray(value) ? value.map(fill) : object(value) ? Object.fromEntries(Object.entries(value).map(([k, v]) => [k, fill(v)])) : value;
  return fill(templates);
}

export function routineAuthorizations(steps, authorizations = {}) {
  if (!object(authorizations)) throw new Error('authorizations must be indexed by step');
  for (const [key, value] of Object.entries(authorizations)) {
    if (!/^(0|[1-9]\d*)$/.test(key) || Number(key) >= steps.length || !object(value) || Object.keys(value).some(k => !['confirm', 'accord'].includes(k)) ||
        (value.confirm !== undefined && typeof value.confirm !== 'boolean') || (value.accord !== undefined && typeof value.accord !== 'string')) throw new Error('Invalid current-step authorization');
  }
  return steps.map((step, i) => ({ ...clone(step), ...authorizations[String(i)] }));
}

function validateEntry(entry) {
  if (!object(entry) || Object.keys(entry).some(k => ![...definitionFields, 'runs', 'lastOk', 'lastRun'].includes(k)) ||
      !(entry.revision === null || (typeof entry.revision === 'string' && HASH.test(entry.revision))) ||
      !HASH.test(entry.structureHash) || typeof entry.saved !== 'string' || !Number.isFinite(Date.parse(entry.saved)) ||
      !['verified', 'not-exercised', 'unqualified'].includes(entry.coverage) ||
      !(entry.verifiedTransitions === null || (Number.isSafeInteger(entry.verifiedTransitions) && entry.verifiedTransitions >= 0 && entry.verifiedTransitions <= entry.steps?.length)) ||
      (entry.runs !== undefined && (!Number.isSafeInteger(entry.runs) || entry.runs < 0)) ||
      (entry.lastOk !== undefined && typeof entry.lastOk !== 'boolean') ||
      (entry.lastRun !== undefined && !Number.isFinite(Date.parse(entry.lastRun)))) throw new Error('Invalid routine metadata');
  const steps = routineSteps(entry.steps);
  if (!Array.isArray(entry.anchors) || entry.anchors.length !== steps.length || entry.anchors.some(a => !HASH.test(a))) throw new Error('Invalid routine target anchors');
  if ((entry.coverage === 'verified') !== (entry.verifiedTransitions > 0) || (entry.coverage === 'not-exercised' && entry.verifiedTransitions !== 0)) throw new Error('Invalid routine proof coverage');
  return { ...clone(entry), steps };
}
function empty(appKey) { return { schema: 1, appKey, routines: Object.create(null), forgotten: Object.create(null) }; }
async function load(file, appKey) {
  try {
    const raw = await readFile(file, 'utf8');
    if (raw.length > 1024 * 1024) throw new Error('Routine file is too large');
    const data = JSON.parse(raw);
    if (!object(data) || Object.keys(data).some(k => !['schema', 'appKey', 'routines', 'forgotten'].includes(k)) || data.schema !== 1 || data.appKey !== appKey || !object(data.routines) || !object(data.forgotten)) throw new Error('Invalid routine file');
    data.routines = Object.assign(Object.create(null), data.routines); data.forgotten = Object.assign(Object.create(null), data.forgotten);
    for (const [name, entry] of Object.entries(data.routines)) { routineName(name); data.routines[name] = validateEntry(entry); }
    for (const [name, stamp] of Object.entries(data.forgotten)) if (!NAME.test(name) || !Number.isFinite(Date.parse(stamp))) throw new Error('Invalid routine deletion');
    return data;
  } catch (error) { if (error.code === 'ENOENT') return empty(appKey); throw error; }
}
async function write(file, data) {
  const json = JSON.stringify(data, null, 2) + '\n';
  if (await readFile(file, 'utf8').catch(e => { if (e.code !== 'ENOENT') throw e; return null; }) === json) return;
  await mkdir(path.dirname(file), { recursive: true });
  const tmp = file + '.' + randomUUID() + '.tmp';
  try { await writeFile(tmp, json, { flag: 'wx' }); await rename(tmp, file); }
  finally { await unlink(tmp).catch(e => { if (e.code !== 'ENOENT') throw e; }); }
}
const definition = entry => Object.fromEntries(definitionFields.map(k => [k, clone(entry[k])]));

export function defaultRoutineMirror(root, env = globalThis.process?.env ?? {}) {
  if (Object.hasOwn(env, 'NEXUS_ROUTINES_MIRROR')) return env.NEXUS_ROUTINES_MIRROR.trim() ? path.resolve(env.NEXUS_ROUTINES_MIRROR) : null;
  if (env.NODE_TEST_CONTEXT || env.NEXUS_SHARED_HOME || env.NEXUS_WINDOWS_HOME) return null;
  const project = fileURLToPath(new URL('../', import.meta.url));
  const defaultState = path.resolve(env.LOCALAPPDATA ?? '', 'NexusCodex', 'ComputerUse');
  if (!root || path.resolve(root).toLowerCase() !== defaultState.toLowerCase()) return null;
  if (existsSync(path.join(project, '.nexus-publish.toml'))) return path.join(project, 'routines');
  // Le guide installé possède le manifeste local de SON installation ; il ne découvre jamais un dépôt par recherche
  // globale. Seul le clone explicitement installé, encore marqué privé, peut recevoir le miroir transférable.
  try {
    const installed = JSON.parse(readFileSync(path.join(root, 'installation.json'), 'utf8')).SourceRoot;
    if (typeof installed === 'string' && path.isAbsolute(installed) && existsSync(path.join(installed, '.nexus-publish.toml'))) return path.join(installed, 'routines');
  } catch { /* installation publique ou manifeste absent : mémoire locale seule */ }
  return null;
}

export class RoutineMemory {
  constructor(root, { mirror = defaultRoutineMirror(root), now = () => new Date() } = {}) {
    if (!path.isAbsolute(root)) throw new Error('An absolute local state directory is required');
    if (mirror !== null && !path.isAbsolute(mirror)) throw new Error('An absolute routine mirror is required');
    this.root = path.join(root, 'routines'); this.mirror = mirror; this.now = now;
  }
  file(app, mirror = false) { return path.join(mirror ? this.mirror : this.root, digest(applicationKey(app)) + '.json'); }
  async read(app, name) { routineName(name); const data = await load(this.file(app), applicationKey(app)); return Object.hasOwn(data.routines, name) ? data.routines[name] : null; }
  async list(app) {
    const data = await load(this.file(app), applicationKey(app));
    return Object.entries(data.routines).map(([name, entry]) => ({ name, parameters: [...new Set(parametersIn(entry.steps))].sort(), steps: entry.steps.length,
      coverage: entry.coverage, verifiedTransitions: entry.verifiedTransitions, needsReview: entry.lastOk === false, runs: entry.runs ?? 0 })).sort((a, b) => a.name.localeCompare(b.name));
  }
  async save(app, name, entry) {
    routineName(name);
    const valid = validateEntry({ ...clone(entry), saved: this.now().toISOString(), runs: 0, lastOk: true });
    const file = this.file(app);
    await withMemoryLock(file, async () => { const data = await load(file, applicationKey(app)); data.routines[name] = valid; delete data.forgotten[name]; await write(file, data); });
    return this.syncQuietly(app);
  }
  async forget(app, name) {
    routineName(name); const file = this.file(app);
    const existed = await withMemoryLock(file, async () => { const data = await load(file, applicationKey(app)); const had = Object.hasOwn(data.routines, name); delete data.routines[name]; data.forgotten[name] = this.now().toISOString(); await write(file, data); return had; });
    return { forgotten: existed, ...(await this.syncQuietly(app)) };
  }
  async recordRun(app, name, ok) {
    const file = this.file(app);
    await withMemoryLock(file, async () => { const data = await load(file, applicationKey(app)); const entry = data.routines[name]; if (!entry) return;
      entry.runs = (entry.runs ?? 0) + 1; entry.lastOk = Boolean(ok); entry.lastRun = this.now().toISOString(); await write(file, data); });
  }
  async syncQuietly(app) { try { return await this.sync(app); } catch { return { mirror: 'unavailable' }; } }
  async sync(app) {
    if (!this.mirror) return { mirror: 'disabled' };
    const file = this.file(app), mirrorFile = this.file(app, true), appKey = applicationKey(app);
    return withMemoryLock(file, () => withMemoryLock(mirrorFile, async () => {
      const local = await load(file, appKey), remote = await load(mirrorFile, appKey);
      const names = new Set([...Object.keys(local.routines), ...Object.keys(local.forgotten), ...Object.keys(remote.routines), ...Object.keys(remote.forgotten)]);
      for (const name of names) {
        // À date égale l'oubli gagne : deux PC à la même milliseconde ne doivent pas ressusciter une définition.
        const candidates = [[local.routines[name]?.saved, 1, 'local'], [local.forgotten[name], 3, 'delete-local'], [remote.routines[name]?.saved, 0, 'remote'], [remote.forgotten[name], 2, 'delete-remote']].filter(([stamp]) => stamp);
        candidates.sort((a, b) => Date.parse(b[0]) - Date.parse(a[0]) || b[1] - a[1]);
        const [stamp, , winner] = candidates[0];
        if (winner.startsWith('delete')) { delete local.routines[name]; delete remote.routines[name]; local.forgotten[name] = remote.forgotten[name] = stamp; }
        else {
          const source = winner === 'local' ? local.routines[name] : remote.routines[name];
          const previous = local.routines[name];
          local.routines[name] = { ...definition(source), ...(previous?.saved === source.saved ? { runs: previous.runs ?? 0, lastOk: previous.lastOk ?? true, ...(previous.lastRun ? { lastRun: previous.lastRun } : {}) } : { runs: 0, lastOk: true }) };
          remote.routines[name] = definition(source); delete local.forgotten[name]; delete remote.forgotten[name];
        }
      }
      if (names.size) { await write(file, local); await write(mirrorFile, remote); }
      return { mirror: 'synchronized', routines: Object.keys(local.routines).length };
    }));
  }
}
