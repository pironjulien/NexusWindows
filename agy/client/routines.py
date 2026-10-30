"""Procédures vérifiées d'Antigravity ; état local, définitions transférables seules dans le miroir.

Le fichier est une donnée non fiable : il est validé à la lecture et n'autorise jamais un geste. Le moteur retrouve
les cibles et applique ses garde-fous au rejeu. Aucun paramètre rempli, titre de document ni accord n'est enregistré.
"""

from __future__ import annotations

import copy
import hashlib
import json
import os
import re
import threading
import time
import uuid
from contextlib import contextmanager
from pathlib import Path
from typing import Any

ROOT = Path(os.environ.get("NEXUS_AGY_ROUTINES_HOME") or Path.home() / ".nexusagy" / "routines")
_PROJECT = Path(__file__).resolve().parents[1]
_explicit = os.environ.get("NEXUS_AGY_ROUTINES_MIRROR", os.environ.get("NEXUS_ROUTINES_MIRROR"))
MIRROR = (
    (Path(_explicit) if _explicit and _explicit.strip() else None)
    if _explicit is not None
    else (
        _PROJECT / "routines"
        if (_PROJECT / ".nexus-publish.toml").is_file() and not os.environ.get("NEXUS_WINDOWS_HOME")
        else None
    )
)
SCHEMA = 1
ENGINE = "antigravity"
PARAM = re.compile(r"\{\{\s*([A-Za-z_][A-Za-z_0-9]*)\s*\}\}")
NAME = re.compile(r"^[\w][\w .-]{0,59}$")
_INDEX = re.compile(r"^(?:(?:#|\[)\s*\d+\s*\]?|\d+|(?:@|coords:|x=)?\s*-?\d+\s*[,x]\s*-?\d+)$")
_LOCK = threading.RLock()
_SESSION_REVIEW: set[tuple[str, str]] = set()
_CONTINUATIONS: dict[str, dict[str, Any]] = {}
TYPING = {"type", "paste", "type_text", "set", "set_value"}
ACTIONS = TYPING | {
    "click",
    "left_click",
    "double_click",
    "right_click",
    "clear",
    "check",
    "uncheck",
    "select",
    "select_option",
    "expand",
    "collapse",
    "menu",
    "key",
    "press_key",
    "hotkey",
    "scroll",
    "hover",
    "move",
    "mouse_move",
    "wait",
    "sleep",
    "focus",
}
STEP_FIELDS = {
    "action",
    "target",
    "text",
    "value",
    "option",
    "key",
    "keys",
    "path",
    "expect",
    "method",
    "clear_first",
    "submit",
    "seconds",
    "timeout",
    "gone",
    "delta_x",
    "delta_y",
}
EXPECT_FIELDS = {
    "element",
    "target",
    "value",
    "contains",
    "checked",
    "selected",
    "enabled",
    "expanded",
    "focused",
    "exists",
    "title",
    "title_contains",
    "timeout",
}
AUTH_FIELDS = {"confirm", "accord", "approved", "authorizations"}


class RoutineError(ValueError):
    """Définition inutilisable ; aucune entrée Windows n'a été envoyée par ce module."""


def _stamp() -> str:
    return f"{time.time_ns():020d}"


def _name(name: str) -> str:
    if not isinstance(name, str) or not NAME.fullmatch(name):
        raise RoutineError("Nom de procédure invalide (1–60 lettres, chiffres, espaces, points ou tirets).")
    return name


def _file(app: str, root: Path | None = None) -> Path:
    if not isinstance(app, str) or not re.fullmatch(r"[\w .-]{1,200}", app) or app in {".", ".."} or app != app.strip():
        raise RoutineError("Identité d'application invalide.")
    return (ROOT if root is None else root) / (app.lower() + ".json")


def _has_param(value: Any) -> bool:
    return bool(PARAM.search(json.dumps(value, ensure_ascii=False)))


def template_only(value: str) -> bool:
    return bool(PARAM.search(value)) and not PARAM.sub("", value).strip()


def _label(value: Any) -> bool:
    return (
        isinstance(value, str) and bool(value.strip()) and not _INDEX.fullmatch(value.strip()) and not _has_param(value)
    )


def validate_steps(steps: Any, *, strip_authorizations: bool = False) -> list[dict[str, Any]]:
    """Format sémantique fermé, identique avant apprentissage et après import depuis un fichier/miroir."""
    if not isinstance(steps, list) or not steps:
        raise RoutineError("Une procédure contient au moins une étape.")
    result = copy.deepcopy(steps)
    for index, step in enumerate(result):
        if not isinstance(step, dict):
            raise RoutineError(f"Étape {index} : objet requis.")
        if strip_authorizations:
            for key in AUTH_FIELDS:
                step.pop(key, None)
        unknown = set(step) - STEP_FIELDS
        if unknown:
            raise RoutineError(f"Étape {index} : champs non réutilisables ({', '.join(sorted(unknown))}).")
        action = str(step.get("action", "")).lower().replace("-", "_").strip()
        if action not in ACTIONS:
            raise RoutineError(f"Étape {index} : action non réutilisable.")
        step["action"] = action
        if action in {"select", "select_option"}:
            option = step.get("option") or step.get("value") or step.get("text")
            if not isinstance(option, str) or not option.strip() or _has_param(option):
                raise RoutineError(f"Étape {index} : option d'interface statique requise.")
            step["option"] = option
            step.pop("text", None)
            step.pop("value", None)
        if "target" in step and not _label(step["target"]):
            raise RoutineError(f"Étape {index} : cible nominative exacte requise, sans index, point ni paramètre.")
        if action not in {"wait", "sleep", "menu", "key", "press_key", "hotkey"} and not step.get("target"):
            raise RoutineError(f"Étape {index} : cible nominative explicite requise.")
        for key, value in step.items():
            if key == "expect":
                if not isinstance(value, dict) or not value or set(value) - EXPECT_FIELDS:
                    raise RoutineError(f"Étape {index} : postcondition invalide.")
                for field, content in value.items():
                    if field in {"element", "target"} and not _label(content):
                        raise RoutineError(f"Étape {index} : cible de postcondition non réutilisable.")
                    if field not in {"value", "contains"} and _has_param(content):
                        raise RoutineError(f"Étape {index} : paramètre interdit dans expect.{field}.")
            elif key in {"text", "value"} and action in TYPING:
                if not isinstance(value, str) or (value.strip() and not template_only(value)):
                    raise RoutineError(f"Étape {index} : texte saisi uniquement sous forme de modèles {{{{x}}}}.")
            elif key == "text" and action in {"wait", "sleep"}:
                if not isinstance(value, str):
                    raise RoutineError(f"Étape {index} : texte attendu invalide.")
            elif key in {"text", "value"}:
                raise RoutineError(f"Étape {index} : {key} ne s'applique pas à cette action.")
            elif _has_param(value):
                raise RoutineError(f"Étape {index} : paramètre interdit dans {key}.")
    # Refus des valeurs non JSON/NaN : une définition exportée doit rester portable.
    try:
        json.dumps(result, allow_nan=False)
    except (TypeError, ValueError) as error:
        raise RoutineError("Définition non JSON.") from error
    return result


def fill(steps: list[dict[str, Any]], params: dict[str, str] | None) -> list[dict[str, Any]]:
    params = {} if params is None else params
    if not isinstance(params, dict) or any(not isinstance(k, str) or not isinstance(v, str) for k, v in params.items()):
        raise RoutineError("params doit être un objet de chaînes de texte.")
    expected = set(PARAM.findall(json.dumps(steps, ensure_ascii=False)))
    missing, unknown = expected - set(params), set(params) - expected
    if missing or unknown:
        raise RoutineError(
            f"Paramètres manquants : {', '.join(sorted(missing)) or 'aucun'} ; inconnus : "
            f"{', '.join(sorted(unknown)) or 'aucun'}."
        )

    def replace(value: Any) -> Any:
        if isinstance(value, str):
            return PARAM.sub(lambda match: params[match.group(1)], value)
        if isinstance(value, list):
            return [replace(v) for v in value]
        if isinstance(value, dict):
            return {k: replace(v) for k, v in value.items()}
        return value

    return replace(steps)


def authorizations_for(value: Any, count: int) -> dict[int, dict[str, Any]]:
    """Autorisations de CET appel seulement, jamais reprises d'une définition ou d'un historique."""
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise RoutineError("authorizations doit être un objet indexé à partir de 0.")
    result: dict[int, dict[str, Any]] = {}
    for key, auth in value.items():
        if not isinstance(key, str) or not key.isdecimal() or str(int(key)) != key or not 0 <= int(key) < count:
            raise RoutineError("Indice d'autorisation invalide.")
        if not isinstance(auth, dict) or not auth or set(auth) - {"confirm", "accord"}:
            raise RoutineError("Une autorisation actuelle ne contient que confirm et accord.")
        if "confirm" in auth and not isinstance(auth["confirm"], bool):
            raise RoutineError("confirm doit être un booléen.")
        if "accord" in auth and (not isinstance(auth["accord"], str) or not auth["accord"].strip()):
            raise RoutineError("accord doit être un jeton non vide.")
        result[int(key)] = copy.deepcopy(auth)
    return result


def _empty(app: str) -> dict[str, Any]:
    return {"schema": SCHEMA, "engine": ENGINE, "app": app, "routines": {}, "forgotten": {}}


def _validate_anchors(anchors: Any, count: int) -> None:
    if not isinstance(anchors, list) or len(anchors) != count:
        raise RoutineError("Ancres de procédure invalides.")
    for entry in anchors:
        if not isinstance(entry, dict) or set(entry) - {"target", "expect", "focused", "post"}:
            raise RoutineError("Ancres de procédure invalides.")
        for node in entry.values():
            if not isinstance(node, dict) or set(node) != {"type", "id", "class", "framework", "name_hash", "parents"}:
                raise RoutineError("Identité d'ancre invalide.")
            if any(not isinstance(node[key], str) for key in ("type", "id", "class", "framework", "name_hash")):
                raise RoutineError("Identité d'ancre non textuelle.")
            if node["name_hash"] and not re.fullmatch(r"[0-9a-f]{64}", node["name_hash"]):
                raise RoutineError("Empreinte de libellé invalide ; aucun nom dynamique n'est transférable.")
            if not isinstance(node["parents"], list) or len(node["parents"]) > 256:
                raise RoutineError("Contexte d'ancre invalide.")
            for parent in node["parents"]:
                if (
                    not isinstance(parent, dict)
                    or set(parent) != {"type", "id", "class", "framework"}
                    or any(not isinstance(v, str) for v in parent.values())
                ):
                    raise RoutineError("Contexte d'ancre non transférable.")


def _read(path: Path, app: str) -> dict[str, Any]:
    try:
        if path.stat().st_size > 4 * 1024 * 1024:
            raise RoutineError("Fichier de procédures trop volumineux.")
        data = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return _empty(app)
    except (OSError, ValueError) as error:
        raise RoutineError("Fichier de procédures illisible ; aucune définition n'est remplacée.") from error
    if (
        not isinstance(data, dict)
        or data.get("schema") != SCHEMA
        or data.get("engine") != ENGINE
        or data.get("app") != app
    ):
        raise RoutineError("Schéma ou application de procédures incompatible.")
    if not isinstance(data.get("routines"), dict) or not isinstance(data.get("forgotten", {}), dict):
        raise RoutineError("Inventaire de procédures invalide.")
    for name, entry in data["routines"].items():
        _name(name)
        if (
            not isinstance(entry, dict)
            or not isinstance(entry.get("fingerprint"), dict)
            or not isinstance(entry.get("saved"), str)
        ):
            raise RoutineError("Métadonnées de procédure invalides.")
        if not re.fullmatch(r"[0-9]{20}", entry["saved"]):
            raise RoutineError("Date de définition invalide.")
        validate_steps(entry.get("steps"))
        _validate_anchors(entry.get("anchors"), len(entry["steps"]))
    for name, stamp in data.get("forgotten", {}).items():
        _name(name)
        if not isinstance(stamp, str) or not re.fullmatch(r"[0-9]{20}", stamp):
            raise RoutineError("Oubli de procédure invalide.")
    return data


def _write(path: Path, data: dict[str, Any]) -> None:
    text = json.dumps(data, ensure_ascii=False, indent=1, sort_keys=True, allow_nan=False) + "\n"
    try:
        if path.read_text(encoding="utf-8") == text:
            return
    except FileNotFoundError:
        pass
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        temporary.write_text(text, encoding="utf-8")
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


@contextmanager
def _locked(app: str):
    """Un verrou de fichier par application exclut deux serveurs locaux sans effacer un verrou vivant."""
    path = _file(app).with_suffix(".lock")
    path.parent.mkdir(parents=True, exist_ok=True)
    with _LOCK, path.open("a+b") as stream:
        stream.seek(0)
        if not stream.read(1):
            stream.write(b"\0")
            stream.flush()
        stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt

                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl

                fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            raise RoutineError("Procédures occupées par un autre serveur ; aucune écriture.") from error
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                import msvcrt

                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl

                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def _definition(entry: dict[str, Any]) -> dict[str, Any]:
    out = {
        k: copy.deepcopy(entry[k])
        for k in ("steps", "anchors", "saved", "coverage", "verifiedTransitions")
        if k in entry
    }
    out["fingerprint"] = {k: v for k, v in entry["fingerprint"].items() if k in {"app", "size", "mtime"}}
    return out


def transferable(entry: dict[str, Any]) -> bool:
    """Aucun texte saisi/attendu fixe ni titre utilisateur ne quitte le stockage local."""
    if any(
        node.get("type") in {"Window", "Document"} and node.get("name_hash")
        for anchor in entry["anchors"]
        for node in anchor.values()
    ):
        return False
    for step in entry["steps"]:
        fields = [step.get("text"), step.get("value") if step["action"] in TYPING else None]
        expect = step.get("expect") or {}
        fields += [expect.get(k) for k in ("value", "contains", "title", "title_contains")]
        if any(isinstance(v, str) and v.strip() and not template_only(v) for v in fields):
            return False
    return True


def _merge(local: dict[str, Any], remote: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    left, right = copy.deepcopy(local), copy.deepcopy(remote)
    for name in set(left["routines"]) | set(right["routines"]) | set(left["forgotten"]) | set(right["forgotten"]):
        candidates = []
        for priority, data, key, origin in (
            (2, left, "routines", "local"),
            (4, left, "forgotten", "forget"),
            (1, right, "routines", "remote"),
            (3, right, "forgotten", "remote-forget"),
        ):
            if name in data[key]:
                stamp = data[key][name]["saved"] if key == "routines" else data[key][name]
                candidates.append((stamp, priority, origin))
        stamp, _, origin = max(candidates)
        if origin in {"forget", "remote-forget"}:
            for data in (left, right):
                data["routines"].pop(name, None)
                data["forgotten"][name] = stamp
        elif origin == "local":
            if transferable(left["routines"][name]):
                right["routines"][name] = _definition(left["routines"][name])
                right["forgotten"].pop(name, None)
            else:
                right["routines"].pop(name, None)
                # Retirer l'ancienne définition partagée sans oublier la nouvelle définition locale privée.
                right["forgotten"][name] = f"{max(0, int(stamp) - 1):020d}"
        else:
            entry = right["routines"][name]
            if not transferable(entry):
                raise RoutineError("Miroir contenant du texte privé : import refusé.")
            kept = left["routines"].get(name) or {}
            if kept.get("saved") != stamp:
                left["routines"][name] = {**_definition(entry), "runs": 0, "last_ok": None, "needs_review": True}
            left["forgotten"].pop(name, None)
    return left, right


def sync_app(app: str) -> None:
    if MIRROR is None:
        return
    with _locked(app):
        local, remote = _merge(_read(_file(app), app), _read(_file(app, MIRROR), app))
        if local["routines"] or local["forgotten"] or _file(app).exists():
            _write(_file(app), local)
        if remote["routines"] or remote["forgotten"] or _file(app, MIRROR).exists():
            _write(_file(app, MIRROR), remote)


def names(app: str) -> list[str]:
    return sorted(_read(_file(app), app)["routines"])


def get(app: str, name: str) -> dict[str, Any]:
    entry = _read(_file(app), app)["routines"].get(_name(name))
    if entry is None:
        raise RoutineError(f"Procédure {name!r} absente de cette application.")
    result = copy.deepcopy(entry)
    if (app, name) in _SESSION_REVIEW:
        result["needs_review"] = True
        result.pop("reviewed", None)
    return result


def inventory(app: str) -> list[dict[str, Any]]:
    data = _read(_file(app), app)
    return [
        {
            "name": name,
            "steps": len(entry["steps"]),
            "parameters": sorted(set(PARAM.findall(json.dumps(entry["steps"])))),
            "coverage": entry.get("coverage", "unqualified"),
            "verifiedTransitions": entry.get("verifiedTransitions"),
            "needsReview": bool(entry.get("needs_review")) or (app, name) in _SESSION_REVIEW,
            "runs": entry.get("runs", 0),
        }
        for name, entry in sorted(data["routines"].items())
    ]


def remember(
    app: str, name: str, steps: list[dict[str, Any]], fingerprint: dict[str, Any], anchors: list[Any], transitions: int
) -> None:
    _name(name)
    template = validate_steps(steps, strip_authorizations=True)
    _validate_anchors(anchors, len(template))
    if not isinstance(transitions, int) or isinstance(transitions, bool) or not 0 <= transitions <= len(template):
        raise RoutineError("Preuves de procédure invalides.")
    with _locked(app):
        data = _read(_file(app), app)
        data["routines"][name] = {
            "steps": template,
            "anchors": copy.deepcopy(anchors),
            "fingerprint": copy.deepcopy(fingerprint),
            "saved": _stamp(),
            "runs": 0,
            "last_ok": transitions > 0,
            "needs_review": False,
            "coverage": "verified" if transitions else "not-exercised",
            "verifiedTransitions": transitions,
        }
        data["forgotten"].pop(name, None)
        _write(_file(app), data)
        _SESSION_REVIEW.discard((app, name))
    sync_app(app)


def forget(app: str, name: str) -> bool:
    _name(name)
    with _locked(app):
        data = _read(_file(app), app)
        found = data["routines"].pop(name, None) is not None
        data["forgotten"][name] = _stamp()
        _write(_file(app), data)
        _SESSION_REVIEW.discard((app, name))
    sync_app(app)
    return found


def changed(entry: dict[str, Any], fingerprint: dict[str, Any]) -> bool:
    old = entry["fingerprint"]
    return any(old.get(k) != fingerprint.get(k) for k in ("app", "size", "mtime") if k in old or k in fingerprint) or (
        bool(old.get("path")) and os.path.normcase(old["path"]) != os.path.normcase(str(fingerprint.get("path") or ""))
    )


def definition_digest(entry: dict[str, Any]) -> str:
    return hashlib.sha256(json.dumps(_definition(entry), sort_keys=True, ensure_ascii=False).encode()).hexdigest()


def inspected(app: str, name: str) -> None:
    with _locked(app):
        data = _read(_file(app), app)
        entry = data["routines"].get(_name(name))
        if entry is None:
            raise RoutineError("Procédure absente.")
        entry["reviewed"] = definition_digest(entry)
        _write(_file(app), data)
        _SESSION_REVIEW.discard((app, name))


def needs_inspection(entry: dict[str, Any]) -> bool:
    return bool(entry.get("needs_review")) and entry.get("reviewed") != definition_digest(entry)


def record_run(app: str, name: str, ok: bool) -> None:
    _SESSION_REVIEW.add((app, name))  # avant le disque : un ancien succès n'autorise jamais une reprise
    with _locked(app):
        data = _read(_file(app), app)
        entry = data["routines"].get(_name(name))
        if entry is None:
            return
        entry["runs"] = int(entry.get("runs", 0)) + 1
        entry["last_ok"], entry["needs_review"] = ok, not ok
        entry.pop("reviewed", None)
        _write(_file(app), data)
        if ok:
            _SESSION_REVIEW.discard((app, name))


def discard_continuations(hwnd: int) -> None:
    """Une nouvelle exécution sur la fenêtre rend son ancienne continuation inutilisable."""
    with _LOCK:
        for token, state in list(_CONTINUATIONS.items()):
            if state["hwnd"] == hwnd:
                del _CONTINUATIONS[token]


def pause(hwnd: int, state: dict[str, Any]) -> str:
    """Préfixe vérifié seulement, en RAM : aucun texte courant ni accord n'est écrit sur disque."""
    with _LOCK:
        discard_continuations(hwnd)
        token = uuid.uuid4().hex
        _CONTINUATIONS[token] = {**state, "hwnd": hwnd, "observed": False}
        return token


def observed(hwnd: int) -> None:
    """Un regard postérieur à la pause est nécessaire pour autoriser la continuation explicite."""
    with _LOCK:
        for state in _CONTINUATIONS.values():
            if state["hwnd"] == hwnd:
                state["observed"] = True


def cancel_continuation(token: str | None) -> None:
    """Un arrêt utilisateur invalide le jeton ; le lever ne réautorise jamais la reprise."""
    with _LOCK:
        state = _CONTINUATIONS.pop(token, None) if isinstance(token, str) else None
    if state is not None and state["routine"]:
        try:
            record_run(state["app"], state["routine"], False)
        except (OSError, ValueError):
            pass  # record_run a déjà armé _SESSION_REVIEW avant le disque ; ne pas masquer l'arrêt d'urgence


def take_continuation(token: str | None, hwnd: int) -> dict[str, Any]:
    with _LOCK:
        state = _CONTINUATIONS.get(token) if isinstance(token, str) else None
        if state is None or state["hwnd"] != hwnd:
            raise RoutineError("Continuation absente, périmée ou destinée à une autre fenêtre.")
        if not state["observed"]:
            raise RoutineError("Un look frais est requis après la pause avant de continuer.")
        assert isinstance(token, str)
        return _CONTINUATIONS.pop(token)  # usage unique, même si la revalidation suivante échoue
