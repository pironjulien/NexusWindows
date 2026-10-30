"""Mémoire des routines par application : des lots d'étapes vérifiés, rejoués par leur nom.

Repris du modèle de NexusCodex et adapté au lot d'étapes : une routine n'est enregistrée que si TOUTES ses étapes ont
été vérifiées (✓) ou trouvées déjà faites (=) — jamais une issue inconnue (~) ni un échec — et que ses cibles sont
sémantiques (noms visibles). Ni numéro [N] (la numérotation change à chaque look) ni point x,y n'est appris : au
rejeu, chaque cible est retrouvée par son nom dans l'état réel et chaque étape est revérifiée.

L'application est identifiée par son exécutable (nom, chemin, taille, date) : une mise à jour est signalée au rejeu,
qui reste possible puisque chaque étape est revérifiée ; un rejeu raté marque la routine « à revoir ».

Texte variable : `{{nom}}`, valeur donnée au rejeu par params={"nom": "…"}, seulement dans les champs de texte
(`text`, `expect`, `check.value`, `check.contains`) : jamais dans une cible, une touche ou un chemin de menu (une cible « {{b}} »
remplie en « 120,340 » deviendrait un clic par point). Un texte tapé (`type`, `set`) n'est fait QUE de modèles `{{x}}` :
tout texte fixe y est refusé à l'enregistrement. Au rejeu, un paramètre manquant ou inconnu est refusé, et les étapes
remplies repassent la règle des cibles sémantiques.
Fichiers : ~/.claude/nexus-claude/routines/<exécutable>.json (local au poste, hors OneDrive).

Miroir entre les PC de l'utilisateur : NexusWindows/claude/routines/<exécutable>.json dans le dépôt (synchronisé par OneDrive, suivi
par Git). Il ne garde que la définition des routines (étapes, empreinte, provenance, date d'enregistrement), jamais leurs
statistiques de rejeu, et JAMAIS de texte saisi en clair : une routine dont un texte (`text`, `check.value`,
`check.contains`, `expect`) n'est pas fait que de modèles `{{x}}` reste locale au poste. Aucun titre de document ni
autorisation n'y est copié. Fusion par date : pour chaque routine, la
version enregistrée (ou oubliée : pierre tombale datée) le plus récemment l'emporte, dans les deux sens ; à date
égale, l'oubli prime. Les nouveaux événements sont datés en UTC à la microseconde. Le serveur
synchronise au démarrage et réécrit le miroir de l'application à chaque routine enregistrée ou oubliée ;
une synchronisation du poste peut faire de même hors serveur.
"""

from __future__ import annotations

import copy
import json
import math
import ntpath
import os
import re
import threading
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from . import foreground, log, snapshot
from . import win32 as w

ROOT = log.ROOT / "routines"
# NexusWindows/claude (sources lancées depuis le dépôt privé, installation éditable ; .nexus-publish.toml n'est jamais
# exporté : absent d'une copie publique ou d'un paquet).
_PROJECT = Path(__file__).resolve().parents[2]


def _default_mirror() -> Path | None:
    """Miroir du dépôt : NEXUS_ROUTINES_MIRROR s'il est posé (vide : aucun) ; sinon NexusWindows/claude/routines, sauf pour un
    serveur de test isolé (NEXUS_WINDOWS_HOME), qui ne doit jamais toucher aux routines de l'utilisateur."""
    explicit = os.environ.get("NEXUS_ROUTINES_MIRROR")
    if explicit is not None:
        return Path(explicit) if explicit.strip() else None
    if os.environ.get("NEXUS_WINDOWS_HOME") or not (_PROJECT / ".nexus-publish.toml").is_file():
        return None
    return _PROJECT / "routines"


MIRROR: Path | None = _default_mirror()
SCHEMA = 1
MIRRORED_KEYS = ("steps", "fingerprint", "saved", "coverage", "verifiedTransitions")
NAME = re.compile(r"^[\w][\w .-]{0,59}$")
PARAM = re.compile(r"\{\{\s*(\w+)\s*\}\}")
_LOCK = threading.Lock()
_REVIEWS: dict[tuple[str, str, str], tuple[Any, ...]] = {}
_FAILED: set[tuple[str, str, str, str]] = set()


class RoutineError(ValueError):
    pass


def _stamp() -> str:
    return datetime.now(UTC).isoformat(timespec="microseconds").replace("+00:00", "Z")


def _stamp_order(value: str) -> datetime:
    """Dates UTC précises et anciennes dates locales sans zone : comparaison du même instant."""
    try:
        return datetime.fromisoformat(value).astimezone(UTC)
    except ValueError, OverflowError:
        return datetime.min.replace(tzinfo=UTC)


def app_of(hwnd: int) -> tuple[str, dict[str, Any]]:
    """(clé de l'application, empreinte de son exécutable) pour la fenêtre."""
    pid = foreground.app_pid(hwnd)
    name = (w.process_name(pid) or "unknown").lower()
    path = w.process_path(pid)
    fingerprint: dict[str, Any] = {"path": path}
    try:
        info = os.stat(path)
        fingerprint |= {"size": info.st_size, "mtime": int(info.st_mtime)}
    except OSError:
        pass
    return name, fingerprint


def _file(app: str) -> Path:
    return ROOT / f"{re.sub(r'[^\w.-]', '_', app)}.json"


def _load(app: str) -> dict[str, Any]:
    return _load_path(_file(app), app)


def _write(target: Path, data: dict[str, Any]) -> bool:
    """Écriture atomique (un lecteur voit l'ancienne ou la nouvelle version, jamais un morceau), seulement si le
    contenu change (le miroir est synchronisé par OneDrive et suivi par Git : aucune réécriture à l'identique)."""
    text = json.dumps(data, ensure_ascii=False, indent=1, sort_keys=True) + "\n"
    try:
        if target.read_text(encoding="utf-8") == text:
            return False
    except OSError:
        pass
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.with_suffix(f".{os.getpid()}.tmp")
    try:
        tmp.write_text(text, encoding="utf-8")
        os.replace(tmp, target)
    finally:
        tmp.unlink(missing_ok=True)
    return True


def _save(app: str, data: dict[str, Any]) -> None:
    _write(_file(app), data)


def names(app: str) -> list[str]:
    return sorted(_load(app)["routines"])


def summary(app: str) -> str:
    """Ligne d'en-tête de look : routines connues pour cette application."""
    routines = _load(app)["routines"]
    if not routines:
        return ""
    parts = []
    for name in sorted(routines):
        entry = routines[name]
        params = sorted(set(PARAM.findall(json.dumps(entry.get("steps", []), ensure_ascii=False))))
        flag = " (needs review)" if needs_review(app, name, entry) else ""
        if entry.get("coverage") == "not-exercised":
            flag += " (not exercised: saved from already-satisfied steps)"
        parts.append(f"{name}{'(' + ', '.join(params) + ')' if params else ''}{flag}")
    return f"Saved routines for {app}: {', '.join(parts)} — act(routine=…, params=…)."


TEXT_PARAM_FIELDS = ("value", "contains")  # champs de check où un {{x}} est permis (avec `text` de l'étape)
TYPING = ("type", "set")  # étapes dont `text` est tapé
STEP_FIELDS = frozenset(
    {
        "do",
        "target",
        "text",
        "keys",
        "option",
        "path",
        "direction",
        "amount",
        "to",
        "gone",
        "timeout",
        "expect",
        "check",
    }
)
CHECK_FIELDS = frozenset(
    {"target", "value", "contains", "checked", "selected", "expanded", "focused", "enabled", "exists"}
)
ACTIONS = frozenset(
    {
        "click",
        "double_click",
        "right_click",
        "type",
        "set",
        "clear",
        "key",
        "select",
        "check",
        "uncheck",
        "toggle",
        "expand",
        "collapse",
        "scroll",
        "focus",
        "menu",
        "wait",
        "drag",
        "hover",
    }
)


def clean_steps(steps: Any) -> list[dict[str, Any]]:
    """Définition seulement : aucune autorisation héritée et aucun champ inconnu qui transporterait du contenu."""
    if not isinstance(steps, list) or not steps or any(not isinstance(step, dict) for step in steps):
        raise RoutineError("A routine needs a nonempty list of step objects.")
    cleaned = []
    for index, step in enumerate(steps, 1):
        value = {key: copy.deepcopy(inner) for key, inner in step.items() if key not in ("confirm", "accord")}
        if set(value) - STEP_FIELDS:
            raise RoutineError(f"step {index}: unsupported routine fields")
        if not isinstance(value.get("do"), str) or value["do"] not in ACTIONS:
            raise RoutineError(f"step {index}: unsupported action")
        for key in ("text", "keys", "option", "path", "direction", "expect"):
            if value.get(key) is not None and not isinstance(value[key], str):
                raise RoutineError(f"step {index}: {key} must be text")
        for key in ("target", "to"):
            inner = value.get(key)
            point = (
                isinstance(inner, list)
                and len(inner) == 2
                and all(type(part) in (int, float) and math.isfinite(part) for part in inner)
            )
            if inner is not None and not isinstance(inner, str) and type(inner) is not int and not point:
                raise RoutineError(f"step {index}: invalid {key}")
        for key in ("amount", "timeout", "gone"):
            inner = value.get(key)
            valid = (
                type(inner) is bool
                if key == "gone"
                else type(inner) is int
                if key == "amount"
                else (isinstance(inner, (int, float)) and not isinstance(inner, bool) and math.isfinite(inner))
            )
            if inner is not None and not valid:
                raise RoutineError(f"step {index}: invalid {key}")
        check = value.get("check")
        if check is not None and (not isinstance(check, dict) or set(check) - CHECK_FIELDS):
            raise RoutineError(f"step {index}: invalid check fields")
        if check:
            for key, inner in check.items():
                if key == "target" and not isinstance(inner, str) and type(inner) is not int:
                    raise RoutineError(f"step {index}: invalid check.target")
                if key in ("value", "contains") and inner is not None and not isinstance(inner, str):
                    raise RoutineError(f"step {index}: check.{key} must be text")
                if key not in ("target", "value", "contains") and inner is not None and not isinstance(inner, bool):
                    raise RoutineError(f"step {index}: check.{key} must be a boolean")
        cleaned.append(value)
    return cleaned


def _template_only(value: str) -> bool:
    """Valeur faite UNIQUEMENT d'un ou plusieurs modèles {{x}} (et d'espaces) : aucun texte fixe."""
    return bool(PARAM.search(value)) and not PARAM.sub("", value).strip()


def _params_in(value: Any) -> bool:
    return bool(PARAM.search(json.dumps(value, ensure_ascii=False)))


def misplaced(steps: list[dict[str, Any]]) -> str:
    """Premier {{x}} hors des champs de texte (`text`, `expect`, `check.value`, `check.contains`), ou « » : rempli au rejeu, il
    changerait une cible, une touche ou un menu (« {{b}} » → « 120,340 » : clic par point)."""
    allowed = "allowed only in text, expect, check.value, check.contains"
    for index, step in enumerate(steps, 1):
        for key, value in step.items():
            if key in ("text", "expect"):
                if key == "text" and step.get("do") in ("key", "menu", "select") and _params_in(value):
                    return f"step {index}: {{{{x}}}} in structural text for {step['do']} ({allowed})"
                continue
            if key == "check" and isinstance(value, dict):
                for sub, inner in value.items():
                    if sub not in TEXT_PARAM_FIELDS and _params_in(inner):
                        return f"step {index}: {{{{x}}}} in check.{sub} ({allowed})"
            elif _params_in(value):
                return f"step {index}: {{{{x}}}} in {key} ({allowed})"
    return ""


def fixed_text(steps: list[dict[str, Any]]) -> str:
    """Premier texte tapé (type, set) qui contient du texte fixe, ou « » : il serait gardé en clair (et le miroir
    OneDrive/Git en recevrait une copie). Seuls des modèles {{x}} remplis au rejeu sont permis."""
    for index, step in enumerate(steps, 1):
        text = step.get("text")
        if step.get("do") in TYPING and isinstance(text, str) and text.strip() and not _template_only(text):
            return (
                f"step {index}: fixed typed text (only {{{{x}}}} templates are allowed, value given at replay through "
                "params)"
            )
    return ""


def unreusable(steps: list[dict[str, Any]]) -> str:
    """Pourquoi ce lot ne peut pas devenir une routine (cible non sémantique), ou « »."""
    for index, step in enumerate(steps, 1):
        for key in ("target", "to"):
            value = step.get(key)
            if value is None:
                continue
            if isinstance(value, (int, list)) or snapshot.parse_point(value) is not None:
                return f"step {index}: target {value!r} by number or by point (only visible names can be replayed)"
            if isinstance(value, str) and snapshot._ID.match(value):
                return f"step {index}: target {value!r} by number [N] (only visible names can be replayed)"
        check = step.get("check") or {}
        target = check.get("target")
        if (
            isinstance(target, (int, list))
            or snapshot.parse_point(target) is not None
            or (isinstance(target, str) and snapshot._ID.match(target))
        ):
            return f"step {index}: check by number or point (only visible names can be replayed)"
    return ""


def remember(
    app: str,
    name: str,
    steps: list[dict[str, Any]],
    fingerprint: dict[str, Any],
    title: str,
    *,
    verified_transitions: int | None = None,
    private_titles: set[str] | None = None,
) -> None:
    if not NAME.match(name):
        raise RoutineError(f'Routine name "{name}": letters, digits, spaces, ".", "-", 60 characters.')
    steps = clean_steps(steps)
    if verified_transitions is not None and (
        type(verified_transitions) is not int or not 0 <= verified_transitions <= len(steps)
    ):
        raise RoutineError("Invalid verified transition count.")
    reason = unreusable(steps) or misplaced(steps) or fixed_text(steps)
    if reason:
        raise RoutineError(reason)
    with _LOCK:
        data = _load(app)
        data["routines"][name] = {
            "steps": steps,
            "fingerprint": fingerprint,
            "saved": _stamp(),
            "coverage": "unqualified"
            if verified_transitions is None
            else "verified"
            if verified_transitions
            else "not-exercised",
            "verifiedTransitions": verified_transitions,
            "localOnly": bool(_title_target(steps, (private_titles or set()) | {title})),
            "runs": 0,
            "last_ok": True,
        }
        data.setdefault("forgotten", {}).pop(name, None)
        _save(app, data)
    _export_quietly(app)


def forget(app: str, name: str) -> bool:
    with _LOCK:
        data = _load(app)
        if data["routines"].pop(name, None) is None:
            return False
        # Pierre tombale datée : sans elle, le miroir d'un autre PC ferait revenir la routine à la synchronisation.
        data.setdefault("forgotten", {})[name] = _stamp()
        _save(app, data)
    _export_quietly(app)
    return True


def get(app: str, name: str) -> dict[str, Any]:
    entry = _load(app)["routines"].get(name)
    if entry is None:
        known = names(app)
        raise RoutineError(
            f'No routine "{name}" for {app}' + (f" (known: {', '.join(known)})." if known else " (none saved).")
        )
    if not isinstance(entry, dict):
        raise RoutineError(f'Routine "{name}" has an invalid definition.')
    return {**entry, "steps": clean_steps(entry.get("steps"))}


def inspect(app: str, name: str, fingerprint: dict[str, Any]) -> dict[str, Any]:
    """Definition reconsultable, sans titre de document, parametres remplis ni autorisations anterieures."""
    entry = get(app, name)
    steps = clean_steps(entry["steps"])
    reason = unreusable(steps) or misplaced(steps) or fixed_text(steps)
    if reason:
        raise RoutineError(f"Routine refused: {reason}.")
    return {
        "name": name,
        "steps": steps,
        "parameters": sorted(set(PARAM.findall(json.dumps(steps, ensure_ascii=False)))),
        "coverage": entry.get("coverage", "unqualified"),
        "verifiedTransitions": entry.get("verifiedTransitions"),
        "fingerprintChanged": changed(entry, fingerprint),
        "needsReview": needs_review(app, name, entry),
    }


def _version(entry: dict[str, Any]) -> str:
    return json.dumps({key: entry.get(key) for key in ("steps", "fingerprint", "saved")}, sort_keys=True)


def _failure_key(app: str, name: str, entry: dict[str, Any]) -> tuple[str, str, str, str]:
    return str(ROOT.resolve()), app, name, _version(entry)


def needs_review(app: str, name: str, entry: dict[str, Any]) -> bool:
    return entry.get("last_ok") is False or _failure_key(app, name, entry) in _FAILED


def _revision(entry: dict[str, Any]) -> tuple[Any, ...]:
    return (
        _version(entry),
        entry.get("runs", 0),
        entry.get("last_run"),
    )


def reviewed(app: str, name: str) -> None:
    """Une inspection explicite qualifie seulement cette version et ce dernier résultat, jamais un geste."""
    entry = get(app, name)
    _REVIEWS[(str(ROOT.resolve()), app, name)] = _revision(entry)
    _FAILED.discard(_failure_key(app, name, entry))


def require_review(app: str, name: str, entry: dict[str, Any]) -> None:
    if needs_review(app, name, entry) and _REVIEWS.get((str(ROOT.resolve()), app, name)) != _revision(entry):
        raise RoutineError(
            f'Routine "{name}" needs review: inspect it with look(routine_info="{name}") before replaying.'
        )


def _fill(value: Any, params: dict[str, str], missing: set[str]) -> Any:
    if isinstance(value, str):

        def sub(match: re.Match) -> str:
            key = match.group(1)
            if key not in params:
                missing.add(key)
                return match.group(0)
            return str(params[key])

        return PARAM.sub(sub, value)
    if isinstance(value, dict):
        return {k: _fill(v, params, missing) for k, v in value.items()}
    if isinstance(value, list):
        return [_fill(v, params, missing) for v in value]
    return value


def fill(steps: list[dict[str, Any]], params: dict[str, str] | None) -> list[dict[str, Any]]:
    """Étapes, modèles {{x}} remplacés par params ; un paramètre manquant (il serait tapé tel quel) ou inconnu (faute
    de frappe : la valeur voulue n'irait nulle part) est refusé, rien n'est exécuté."""
    params = {} if params is None else params
    if not isinstance(params, dict) or any(not isinstance(k, str) or not isinstance(v, str) for k, v in params.items()):
        raise RoutineError("Routine parameters must map names to text values.")
    expected = set(PARAM.findall(json.dumps(steps, ensure_ascii=False)))
    unknown = sorted(set(params) - expected)
    if unknown:
        wanted = f"expected: {', '.join(sorted(expected))}" if expected else "no parameter expected"
        raise RoutineError(f"Unknown parameters: {', '.join(unknown)} ({wanted}).")
    missing: set[str] = set()
    filled = _fill(steps, params, missing)
    if missing:
        raise RoutineError(f"Missing parameters: {', '.join(sorted(missing))} (params={{…}}).")
    return filled


def expand(entry: dict[str, Any], params: dict[str, str] | None) -> list[dict[str, Any]]:
    """Étapes de la routine, paramètres remplacés (fill) ; la routine (enregistrée avant ces règles, reprise du miroir
    ou modifiée à la main) est refusée si un {{x}} sort des champs de texte, ou si une étape remplie vise un numéro ou
    un point : rien n'est exécuté."""
    steps = clean_steps(entry.get("steps"))
    reason = misplaced(steps) or fixed_text(steps)
    if reason:
        raise RoutineError(f"Routine refused: {reason}.")
    filled = fill(steps, params)
    reason = unreusable(filled)
    if reason:
        raise RoutineError(f"Routine refused after filling in the parameters: {reason}.")
    return filled


def changed(entry: dict[str, Any], fingerprint: dict[str, Any]) -> bool:
    saved = entry.get("fingerprint") or {}
    normalize = lambda value: ntpath.normcase(ntpath.normpath(value)) if isinstance(value, str) and value else value
    return any(
        normalize(saved.get(k)) != normalize(fingerprint.get(k))
        for k in ("path", "size", "mtime")
        if k in saved or k in fingerprint
    )


def mark_failed(app: str, name: str, entry: dict[str, Any]) -> None:
    """Verrou de revue en RAM, établi avec la version capturée même si le disque n'est plus lisible."""
    _FAILED.add(_failure_key(app, name, entry))
    _REVIEWS.pop((str(ROOT.resolve()), app, name), None)


def record_run(app: str, name: str, ok: bool) -> None:
    with _LOCK:
        data = _load(app)
        entry = data["routines"].get(name)
        if entry is None:
            return
        key = _failure_key(app, name, entry)
        if ok:
            _FAILED.discard(key)
        else:
            # Avant toute écriture : un disque plein/verrouillé ne rend jamais un ancien succès rejouable.
            mark_failed(app, name, entry)
        entry["runs"] = int(entry.get("runs", 0)) + 1
        entry["last_ok"] = ok
        entry["last_run"] = _stamp()
        _save(app, data)


# --- Miroir entre les PC (NexusWindows/claude/routines) ------------------------------------------------------------------
def _title_target(steps: list[dict[str, Any]], titles: set[str] | None = None) -> str:
    """Titres explicitement qualifiés, ou noms de Window/Document observés : restent au poste."""
    known = {snapshot.norm(title).casefold() for title in titles or () if title.strip()}
    for index, step in enumerate(steps, 1):
        if not isinstance(step, dict):
            continue
        check = step.get("check")
        if not isinstance(check, dict):
            check = {}
        for field, selector in (
            ("target", step.get("target")),
            ("to", step.get("to")),
            ("check.target", check.get("target")),
        ):
            if not isinstance(selector, str):
                continue
            for part in re.split(r"\s*>+\s*", selector):
                roles, name = snapshot._split_role(part)
                if roles and roles <= {"Window", "Document"} or snapshot.norm(name).strip("«»\"' ").casefold() in known:
                    return f"step {index}, {field} (document/window title)"
    return ""


def plain_text(entry: dict[str, Any]) -> str:
    """Premier texte saisi en clair d'une routine (étape et champ), « » s'il n'y en a pas : un texte saisi ne passe
    dans le miroir que sous forme de modèle `{{x}}` (rempli au rejeu)."""
    if entry.get("localOnly") is True:
        return "local document/window target"
    if reason := _title_target(entry.get("steps") or []):
        return reason
    for index, step in enumerate(entry.get("steps") or [], 1):
        if not isinstance(step, dict):
            continue
        check = step.get("check")
        if not isinstance(check, dict):
            check = {}
        fields = [
            ("text", step.get("text")),
            ("expect", step.get("expect")),
            ("check.value", check.get("value")),
            ("check.contains", check.get("contains")),
        ]
        for field, value in fields:
            if isinstance(value, str) and value.strip() and not _template_only(value):
                return f"step {index}, {field}"  # texte fixe, même mêlé à un {{x}} (« IBAN FR76… {{nom}} »)
    return ""


def _definition(entry: dict[str, Any]) -> dict[str, Any]:
    if not isinstance(entry, dict) or not isinstance(entry.get("fingerprint", {}), dict):
        raise RoutineError("Invalid routine definition.")
    definition = {k: entry[k] for k in MIRRORED_KEYS if k in entry}
    definition["steps"] = clean_steps(entry.get("steps"))
    definition["fingerprint"] = {
        key: value
        for key, value in (entry.get("fingerprint") or {}).items()
        if key == "path" and isinstance(value, str) or key in ("size", "mtime") and type(value) is int
    }
    count = entry.get("verifiedTransitions")
    if type(count) is not int or not 0 <= count <= len(definition["steps"]):
        count = None
    definition["verifiedTransitions"] = count
    definition["coverage"] = "unqualified" if count is None else "verified" if count else "not-exercised"
    return definition


def _merge(local: dict[str, Any], mirror: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any], list[str]]:
    """Fusion par date d'une application : (fichier local, fichier miroir, changements au poste). Pour chaque nom, le
    plus récent de l'enregistrement et de l'oubli, de part et d'autre, l'emporte ; à égalité, l'oubli prime,
    puis le poste pour deux événements de même nature."""
    changes: list[str] = []
    routines_local = dict(local.get("routines") or {})
    forgotten_local = dict(local.get("forgotten") or {})
    routines_mirror = {}
    for name, entry in (mirror.get("routines") or {}).items():
        try:
            if isinstance(entry, dict) and entry.get("localOnly") is True:
                continue
            definition = _definition(entry)
            steps = definition["steps"]
            if (
                not NAME.fullmatch(name)
                or unreusable(steps)
                or misplaced(steps)
                or fixed_text(steps)
                or plain_text(definition)
            ):
                continue
            routines_mirror[name] = definition
        except RoutineError, TypeError:
            continue  # une definition invalide du miroir ne masque pas celles qui restent utilisables
    forgotten_mirror = dict(mirror.get("forgotten") or {})
    for name in sorted(set(routines_local) | set(forgotten_local) | set(routines_mirror) | set(forgotten_mirror)):
        candidates = []  # (date, priorité à égalité, origine) : un oubli prime sur toute définition
        if name in routines_local:
            candidates.append((str(routines_local[name].get("saved") or ""), 1, "local"))
        if name in forgotten_local:
            candidates.append((str(forgotten_local[name]), 3, "local-oubli"))
        if name in routines_mirror:
            candidates.append((str(routines_mirror[name].get("saved") or ""), 0, "miroir"))
        if name in forgotten_mirror:
            candidates.append((str(forgotten_mirror[name]), 2, "miroir-oubli"))
        stamp, _rank, origin = max(candidates, key=lambda item: (_stamp_order(item[0]), item[1]))
        if origin == "local":
            entry = routines_local[name]
            forgotten_local.pop(name, None)
            steps = entry["steps"]
            if not (plain_text(entry) or unreusable(steps) or misplaced(steps) or fixed_text(steps)):
                # Texte fixe ou ancienne définition invalide : garde locale, jamais exportée dans le miroir.
                routines_mirror[name] = _definition(entry)
                forgotten_mirror.pop(name, None)
        elif origin == "miroir":
            entry = routines_mirror[name]
            kept = routines_local.get(name) or {}
            same = str(kept.get("saved") or "") == stamp
            routines_local[name] = {
                **_definition(entry),
                "runs": kept.get("runs", 0) if same else 0,
                "last_ok": kept.get("last_ok", True) if same else True,
            }
            forgotten_local.pop(name, None)
            if not same:
                changes.append(f"{name}: version of {stamp} taken from the mirror")
        elif origin == "local-oubli":
            routines_local.pop(name, None)
            routines_mirror.pop(name, None)
            forgotten_mirror[name] = stamp
        else:  # miroir-oubli
            if routines_local.pop(name, None) is not None:
                changes.append(f"{name}: forgotten on another computer on {stamp}")
            forgotten_local[name] = stamp
    app = local.get("app") or mirror.get("app")
    new_local = {"schema": SCHEMA, "app": app, "routines": routines_local, "forgotten": forgotten_local}
    new_mirror = {"schema": SCHEMA, "app": app, "routines": routines_mirror, "forgotten": forgotten_mirror}
    return new_local, new_mirror, changes


def _load_path(path: Path, app: str) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except OSError, ValueError:
        return {"schema": SCHEMA, "app": app, "routines": {}}
    if not isinstance(data, dict) or data.get("schema") != SCHEMA or not isinstance(data.get("routines"), dict):
        return {"schema": SCHEMA, "app": app, "routines": {}}
    if data.get("app", app) != app:
        return {"schema": SCHEMA, "app": app, "routines": {}}
    cleaned = {}
    for name, entry in data["routines"].items():
        try:
            if not isinstance(name, str) or not NAME.fullmatch(name) or not isinstance(entry, dict):
                continue
            if not isinstance(entry.get("fingerprint", {}), dict) or not isinstance(entry.get("saved", ""), str):
                continue
            cleaned[name] = {
                **_definition(entry),
                **{key: entry[key] for key in ("runs", "last_ok", "last_run", "localOnly") if key in entry},
            }
        except RoutineError:
            continue
    forgotten = data.get("forgotten", {})
    if not isinstance(forgotten, dict):
        forgotten = {}
    return {
        **data,
        "routines": cleaned,
        "forgotten": {
            name: stamp
            for name, stamp in forgotten.items()
            if isinstance(name, str) and NAME.fullmatch(name) and isinstance(stamp, str)
        },
    }


def sync_app(app: str) -> list[str]:
    """Synchronise une application dans les deux sens ; renvoie les changements faits au poste."""
    if MIRROR is None:
        return []
    mirror_file = MIRROR / _file(app).name
    with _LOCK:
        local, mirror, changes = _merge(_load(app), _load_path(mirror_file, app))
        if local["routines"] or local["forgotten"] or _file(app).exists():
            _save(app, local)
        if mirror["routines"] or mirror["forgotten"] or mirror_file.exists():
            _write(mirror_file, mirror)
    return changes


def sync() -> list[str]:
    """Toutes les applications connues d'un côté ou de l'autre (démarrage du serveur, scripts/sync-routines.ps1)."""
    if MIRROR is None:
        return []
    apps: set[str] = set()
    for folder in (ROOT, MIRROR):
        for path in sorted(folder.glob("*.json")) if folder.is_dir() else []:
            app = _load_path(path, path.stem).get("app")
            apps.add(app if isinstance(app, str) and app else path.stem)
    changes: list[str] = []
    for app in sorted(apps):
        changes += [f"{app}: {change}" for change in sync_app(app)]
    return changes


def _export_quietly(app: str) -> None:
    """Après un enregistrement ou un oubli : miroir de l'application à jour. Un miroir en écriture impossible
    (OneDrive occupé) ne fait jamais échouer l'action : la synchronisation suivante rattrapera."""
    try:
        sync_app(app)
    except OSError as error:
        log.event("routines-mirror", {"app": app, "error": str(error)[:200]})


def main(argv: list[str] | None = None) -> int:
    """python -m nexus_windows.routines sync : synchronisation hors serveur (scripts/sync-routines.ps1)."""
    import sys

    args = sys.argv[1:] if argv is None else argv
    if args[:1] != ["sync"]:
        print("usage: python -m nexus_windows.routines sync")
        return 2
    if MIRROR is None:
        print("routine mirror disabled (NEXUS_ROUTINES_MIRROR empty, or isolated test server)")
        return 0
    changes = sync()
    print(f"routines synchronized with {MIRROR}")
    for change in changes or ["nothing new for this computer"]:
        print(f"  {change}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
