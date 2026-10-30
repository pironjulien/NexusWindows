"""Placement d'une fenêtre nommée : une commande, puis état Windows relu, jamais d'image comme preuve."""

from __future__ import annotations

import ctypes
import time
from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class Plan:
    action: str
    x: int | None = None
    y: int | None = None
    width: int | None = None
    height: int | None = None


def prepare(action: str, *, x=None, y=None, width=None, height=None, foreground=False) -> Plan:
    if action not in {"restore", "minimize", "maximize", "bottom", "front", "move", "resize"}:
        raise ValueError("Action de fenêtre inconnue : rien envoyé")
    if not isinstance(foreground, bool):
        raise ValueError("foreground doit être un booléen : rien envoyé")
    for key, value in {"x": x, "y": y, "width": width, "height": height}.items():
        if value is not None and (type(value) is not int or not -2147483648 <= value <= 2147483647):
            raise ValueError(f"{key} doit être un entier en pixels écran : rien envoyé")
    if action == "front" and not foreground:
        raise ValueError("front exige foreground:true : rien envoyé")
    if action == "move" and (x is None or y is None):
        raise ValueError("move exige x et y : rien envoyé")
    if action == "resize" and (width is None or height is None or width <= 0 or height <= 0):
        raise ValueError("resize exige width et height positifs : rien envoyé")
    if (action != "move" and (x is not None or y is not None)) or (
        action != "resize" and (width is not None or height is not None)
    ):
        raise ValueError("Coordonnées réservées à move, dimensions réservées à resize : rien envoyé")
    return Plan(action, x, y, width, height)


def reached(plan: Plan, before: dict, now: dict) -> bool:
    match plan.action:
        case "restore":
            return now["visible"] and not now["minimized"] and not now["maximized"]
        case "minimize":
            return now["minimized"]
        case "maximize":
            return now["visible"] and now["maximized"] and not now["minimized"]
        case "bottom":
            return now["bottom"]
        case "front":
            return now["visible"] and not now["minimized"] and now["foreground"]
        case "move":
            return (now["left"], now["top"], now["width"], now["height"]) == (
                plan.x,
                plan.y,
                before["width"],
                before["height"],
            )
        case "resize":
            return (now["left"], now["top"], now["width"], now["height"]) == (
                before["left"],
                before["top"],
                plan.width,
                plan.height,
            )
    return False


def application_bottom(hwnd, next_window, shell_window, desktop_window) -> bool:
    """Sous la cible : uniquement les identités système publiques, puis fin de chaîne ; aucune application masquée."""
    allowed = {identity for identity in (shell_window, desktop_window) if identity}
    seen = {hwnd}
    cursor = next_window(hwnd)
    while cursor:
        if cursor not in allowed or cursor in seen:
            return False
        seen.add(cursor)
        cursor = next_window(cursor)
    return True


class Native:
    """Appels Win32 typés 64 bits et pixels physiques, séparés pour les tests sans bureau."""

    def __init__(self, host):
        from ctypes import wintypes as w

        class Placement(ctypes.Structure):
            _fields_ = [
                ("length", w.UINT),
                ("flags", w.UINT),
                ("show", w.UINT),
                ("minimum", w.POINT),
                ("maximum", w.POINT),
                ("normal", w.RECT),
            ]

        self.host, self.w, self.Placement = host, w, Placement
        self.api = host.user32
        declarations = {
            "GetWindow": ([w.HWND, w.UINT], w.HWND),
            "GetShellWindow": ([], w.HWND),
            "GetDesktopWindow": ([], w.HWND),
            "IsZoomed": ([w.HWND], w.BOOL),
            "ShowWindowAsync": ([w.HWND, ctypes.c_int], w.BOOL),
            "SetWindowPos": ([w.HWND, w.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, w.UINT], w.BOOL),
            "GetWindowPlacement": ([w.HWND, ctypes.POINTER(Placement)], w.BOOL),
            "SetWindowPlacement": ([w.HWND, ctypes.POINTER(Placement)], w.BOOL),
            "SetThreadDpiAwarenessContext": ([ctypes.c_void_p], ctypes.c_void_p),
        }
        for name, (args, result) in declarations.items():
            call = getattr(self.api, name)
            call.argtypes, call.restype = args, result
        self.previous_dpi = self.api.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4))

    def close(self):
        if self.previous_dpi:
            self.api.SetThreadDpiAwarenessContext(self.previous_dpi)

    def identity(self, hwnd):
        if not self.api.IsWindow(hwnd):
            raise ValueError("Fenêtre disparue : rien envoyé")
        pid = self.w.DWORD()
        self.api.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        app = self.host.get_process_image_path(self.host.app_pid(hwnd))[0]
        if not pid.value or not app:
            raise ValueError("Identité de fenêtre indisponible : rien envoyé")
        protected, reason = self.host.is_protected_system_window(hwnd)
        if protected:
            raise PermissionError(f"Gestion de fenêtre interdite sur {reason}")
        return pid.value, app.casefold(), int(self.api.GetWindow(hwnd, 4) or 0)

    def read(self, hwnd):
        rect = self.w.RECT()
        if not self.api.IsWindow(hwnd) or not self.api.GetWindowRect(hwnd, ctypes.byref(rect)):
            raise RuntimeError("État de fenêtre indisponible")
        return {
            "visible": bool(self.api.IsWindowVisible(hwnd)),
            "minimized": bool(self.api.IsIconic(hwnd)),
            "maximized": bool(self.api.IsZoomed(hwnd)),
            "foreground": self.front() == hwnd,
            "bottom": application_bottom(
                hwnd,
                lambda window: int(self.api.GetWindow(window, 2) or 0),
                int(self.api.GetShellWindow() or 0),
                int(self.api.GetDesktopWindow() or 0),
            ),
            "left": rect.left,
            "top": rect.top,
            "width": rect.right - rect.left,
            "height": rect.bottom - rect.top,
        }

    def front(self):
        return int(self.api.GetForegroundWindow() or 0)

    def enabled(self, hwnd):
        return bool(self.api.IsWindowEnabled(hwnd))

    def placement(self, hwnd):
        result = self.Placement()
        result.length = ctypes.sizeof(result)
        if not self.api.GetWindowPlacement(hwnd, ctypes.byref(result)):
            raise ValueError("Placement indisponible : rien envoyé")
        return result

    def send(self, hwnd, plan, placement):
        match plan.action:
            case "restore":
                placement.show = 4  # SW_SHOWNOACTIVATE : respecte le rectangle normal sauvegardé par Windows.
                if not self.api.SetWindowPlacement(hwnd, ctypes.byref(placement)):
                    return False
                return self.api.SetWindowPos(hwnd, 1, 0, 0, 0, 0, 0x13)  # BOTTOM, NOSIZE|NOMOVE|NOACTIVATE
            case "minimize":
                return self.api.ShowWindowAsync(hwnd, 7)  # SW_SHOWMINNOACTIVE
            case "maximize":
                return self.api.ShowWindowAsync(hwnd, 3)  # SW_MAXIMIZE, rendu du premier plan sous le jeton.
            case "bottom":
                return self.api.SetWindowPos(hwnd, 1, 0, 0, 0, 0, 0x13)
            case "move":
                return self.api.SetWindowPos(hwnd, 0, plan.x, plan.y, 0, 0, 0x15)  # NOSIZE|NOZORDER|NOACTIVATE
            case "resize":
                return self.api.SetWindowPos(hwnd, 0, 0, 0, plan.width, plan.height, 0x16)  # NOMOVE|NOZORDER|NOACTIVATE
            case "front":
                return self.host._activate_window(hwnd)


def manage(host, hwnd, action, *, backend=None, **options) -> dict[str, Any]:
    plan = prepare(action, **options)
    host.emergency_stop.check()
    host.ensure_default_desktop()
    target = host.resolve_window(hwnd)
    native = backend or Native(host)
    lock = None
    sent = False
    original = 0
    mark = None
    give_back = "not-taken"
    result: dict[str, Any] = {}
    try:
        identity = native.identity(target)
        host.physical_input.start(timeout_s=1.0)
        if not host.physical_input.available() and host.physical_input._lastinput_tick() is None:
            raise RuntimeError("Présence de Julien inconnue : action reportée, rien envoyé")
        if not host.wait_for_user_idle():
            raise TimeoutError("Julien utilise son clavier ou sa souris : action reportée, rien envoyé")
        lock = host._take_foreground_token("window " + action)
        host.emergency_stop.check()
        if host.physical_input.user_idle_s() < 1.5:
            raise TimeoutError("Julien a repris la main pendant l'attente : rien envoyé")
        if native.identity(target) != identity:
            raise ValueError("Identité de fenêtre changée : rien envoyé")
        before = native.read(target)
        if not native.enabled(target):
            raise ValueError("Fenêtre désactivée (dialogue modal possible) : rien envoyé")
        if action in {"move", "resize"} and (before["minimized"] or before["maximized"]):
            raise ValueError("Restaurer la fenêtre avant move/resize : rien envoyé")
        original, mark = native.front(), host.physical_input.mark()
        already = reached(plan, before, before)
        placement = native.placement(target) if action == "restore" and not already else None
        if not already:
            host.emergency_stop.check()
            if native.identity(target) != identity:
                raise ValueError("Identité de fenêtre changée : rien envoyé")
            sent = True  # après ce point toute exception est une issue inconnue, jamais un refus préalable.
            with host.physical_input.injecting():
                accepted = native.send(target, plan, placement)
            # Même budget que close_window pour un message asynchrone : lectures seules, aucun renvoi.
            deadline = time.monotonic() + 2.0
            if native.identity(target) != identity:
                raise RuntimeError("Identité de fenêtre changée après la commande")
            now = native.read(target)
            while accepted and not reached(plan, before, now) and time.monotonic() < deadline:
                host.emergency_stop.check()
                if host.physical_input.user_touched_since(mark):
                    break
                time.sleep(0.02)
                if native.identity(target) != identity:
                    raise RuntimeError("Identité de fenêtre changée après la commande")
                now = native.read(target)
        else:
            now = before
        verified = reached(plan, before, now)
        result = {
            "success": True if verified else None,
            "verified": verified,
            "already": already,
            "performed": not already,
            "status": "already-satisfied" if already else "verified" if verified else "sent-unconfirmed",
            "action": action,
            "hwnd": target,
            "state": "minimized"
            if now["minimized"]
            else "maximized"
            if now["maximized"]
            else "open"
            if now["visible"]
            else "hidden",
            "bounds": {key: now[key] for key in ("left", "top", "width", "height")},
            **{key: now[key] for key in ("visible", "minimized", "maximized", "foreground", "bottom")},
            "foregroundTaken": original != target and now["foreground"],
            "technique": "background:win32",
        }
    except Exception as error:
        if not sent:
            raise
        result = {
            "success": None,
            "verified": False,
            "performed": True,
            "action": action,
            "hwnd": target,
            "status": "sent-unconfirmed",
            "state": "unknown",
            "error": str(error),
            "next": "Regarder l'état actuel ; ne jamais rejouer automatiquement la commande.",
        }
    finally:
        try:
            if mark is not None and host.physical_input.user_touched_since(mark):
                give_back = "kept-by-julien"
            elif action == "front" and native.front() == target:
                give_back = "kept-requested"
            elif original and original != target and native.front() == target:
                give_back = "restored" if host.restore_foreground(original) else "refused"
            if result:
                result["foregroundReturned"] = give_back
        finally:
            if lock is not None:
                lock.release()
            native.close()
    return result
