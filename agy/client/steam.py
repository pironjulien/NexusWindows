"""Steam CEF : réutilisation, démarrage accessible et redémarrage propre explicitement demandé.

Le lancement n'atteste que la fenêtre native relue. DevTools est séparé, facultatif et prouvé ; aucune session
utilisateur n'est enregistrée comme session appartenant à l'agent, aucun processus n'est arrêté de force.
"""

from __future__ import annotations

import ctypes
import json
import os
import re
import socket
import struct
import subprocess
import time
import winreg
from collections.abc import Callable
from ctypes import wintypes
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urlsplit

try:
    from . import cdp_wheel
except ImportError:
    import cdp_wheel

VIEW_SWITCHES = ("-cef-disable-occlusion", "-cef-force-accessibility")
DEVTOOLS_SWITCH = "-cef-enable-debugging"
DEVTOOLS_PORT = 8080  # Port imposé par Steam, pas un port choisi par l'agent.
SHUTDOWN_WAIT_S, START_WAIT_S = 60.0, 90.0
FOCUS_SETTLE_S = 1.0  # Même délai de restitution tardive que le lancement générique AGY.
STEAM_PROCESSES = frozenset(
    {
        "steam.exe",
        "steamwebhelper.exe",
        "steamservice.exe",
        "steamerrorreporter.exe",
        "steamerrorreporter64.exe",
        "gldriverquery.exe",
        "gldriverquery64.exe",
        "vulkandriverquery.exe",
        "vulkandriverquery64.exe",
        "conhost.exe",
    }
)
CONTROL_SWITCHES = frozenset(
    {"load-extension", "remote-allow-origins", "disable-web-security", "inspect", "inspect-brk", "inspect-port"}
)
Window = dict[str, Any]
Process = tuple[int, int, str]


class SteamError(RuntimeError):
    """Refus ou résultat Steam inconnu ; ne jamais rejouer automatiquement."""


def is_alias(value: str) -> bool:
    return value.strip().casefold() in {"steam", "steam.exe"}


def executable() -> str:
    """Chemin Valve du compte courant, puis installation standard existante (jamais PATH)."""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            value = str(winreg.QueryValueEx(key, "SteamExe")[0]).strip().strip('"')
        candidate = os.path.expandvars(value).replace("/", os.sep)
        canonical = canonical_exe(candidate)
        if canonical:
            return canonical
    except OSError:
        pass
    candidate = str(Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Steam" / "steam.exe")
    canonical = canonical_exe(candidate)
    if canonical:
        return canonical
    raise SteamError("Steam introuvable : aucun exécutable Valve vérifié dans le registre ou l'installation standard.")


def canonical_exe(value: str) -> str | None:
    path = Path(value)
    if not path.is_absolute():
        return None
    try:
        path = path.resolve(strict=True)
        if (
            path.name.casefold() == "steam.exe"
            and path.is_file()
            and any((path.parent / name).is_file() for name in ("steamclient64.dll", "steamui.dll"))
        ):
            return str(path)
    except (OSError, RuntimeError):
        pass
    return None


def is_target(value: str) -> bool:
    return canonical_exe(value) is not None


def check_args(args: list[str]) -> None:
    """Arguments Steam ordinaires conservés ; options de contrôle et neutraliseurs réservés à l'outil."""
    for value in args:
        switch = re.split("[=:]", value.casefold().lstrip("-/"), maxsplit=1)[0]
        parsed = urlsplit(unquote(value))
        uri_action = (parsed.netloc or parsed.path.lstrip("/").split("/", 1)[0]).casefold()
        closing_uri = parsed.scheme.casefold() == "steam" and uri_action in {
            "exit",
            "quit",
            "shutdown",
            "restart",
        }
        if (
            closing_uri
            or value == "--"
            or switch in {"single-argument", "shutdown", "restart"} | CONTROL_SWITCHES
            or (
                value.startswith(("-", "/"))
                and any(part in switch for part in ("debug", "devtools", "profile", "user-data-dir"))
            )
        ):
            raise PermissionError(
                f"Argument Steam « {value} » refusé : debug/profil/arrêt réservés aux options explicites de launch."
            )


class _ProcessEntry(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("pid", wintypes.DWORD),
        ("heap", ctypes.c_size_t),
        ("module", wintypes.DWORD),
        ("threads", wintypes.DWORD),
        ("parent", wintypes.DWORD),
        ("priority", ctypes.c_long),
        ("flags", wintypes.DWORD),
        ("name", ctypes.c_wchar * 260),
    ]


def process_table() -> list[Process]:
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_ProcessEntry)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateToolhelp32Snapshot(2, 0)
    if not handle or handle == wintypes.HANDLE(-1).value:
        raise SteamError("Table des processus illisible : aucun lancement ni arrêt Steam autorisé.")
    try:
        entry = _ProcessEntry()
        entry.dwSize = ctypes.sizeof(entry)
        if not kernel.Process32FirstW(handle, ctypes.byref(entry)):
            raise SteamError("Table des processus vide ou illisible : état Steam inconnu.")
        rows = []
        while True:
            rows.append((int(entry.pid), int(entry.parent), entry.name.casefold()))
            if not kernel.Process32NextW(handle, ctypes.byref(entry)):
                if ctypes.get_last_error() != 18:  # ERROR_NO_MORE_FILES
                    raise SteamError("Table des processus incomplète : état Steam inconnu.")
                return rows
    finally:
        kernel.CloseHandle(handle)


def descendants(roots: set[int], rows: list[Process]) -> set[int]:
    found = set(roots)
    while True:
        more = {pid for pid, parent, _ in rows if parent in found} - found
        if not more:
            return found
        found.update(more)


def _same_path(left: str, right: str) -> bool:
    if not left or not right:
        return False
    try:
        return Path(left).resolve(strict=True) == Path(right).resolve(strict=True)
    except (OSError, RuntimeError):
        return False


def roots(exe: str, rows: list[Process], image: Callable[[int], str]) -> set[int]:
    candidates = {pid for pid, _, name in rows if name.casefold() == "steam.exe"}
    owned = {pid for pid in candidates if _same_path(image(pid), exe)}
    if candidates - owned:
        raise SteamError(
            "Un processus Steam appartient à un autre chemin ou reste illisible : arrêter ici ; si une commande a déjà été transmise, regarder avant toute reprise."
        )
    return owned


def helpers(exe: str, owned: set[int], rows: list[Process], image: Callable[[int], str]) -> set[int]:
    tree = descendants(owned, rows) - owned
    folder = Path(exe).parent.resolve()
    found = set()
    for pid, _, name in rows:
        if pid not in tree or name.casefold() != "steamwebhelper.exe":
            continue
        path = Path(image(pid))
        if (
            path.is_absolute()
            and path.is_file()
            and path.resolve().is_relative_to(folder)
            and path.name.casefold() == name.casefold()
        ):
            found.add(pid)
    return found


def _trusted_child(exe: str, name: str, path: str) -> bool:
    candidate = Path(path)
    if not candidate.is_absolute():
        return False
    try:
        candidate = candidate.resolve(strict=True)
        if not candidate.is_file() or candidate.name.casefold() != name.casefold():
            return False
        if candidate.is_relative_to(Path(exe).resolve(strict=True).parent):
            return True
        if name.casefold() == "steamservice.exe":
            service_folders = {
                Path(value).resolve() / "Steam"
                for key in ("CommonProgramFiles", "CommonProgramFiles(x86)")
                if (value := os.environ.get(key))
            }
            if candidate.parent in service_folders:
                return True
        system = Path(os.environ.get("SystemRoot", r"C:\Windows")).resolve()
        return name.casefold() == "conhost.exe" and candidate.parent in {system / "System32", system / "SysWOW64"}
    except (OSError, RuntimeError):
        return False


def games_running(exe: str, owned: set[int], rows: list[Process], image: Callable[[int], str]) -> list[str]:
    tree = descendants(owned, rows) - owned
    return sorted(
        {
            name
            for pid, _, name in rows
            if pid in tree and (name.casefold() not in STEAM_PROCESSES or not _trusted_child(exe, name, image(pid)))
        }
    )


def _remaining_helpers(exe: str, rows: list[Process], image: Callable[[int], str]) -> set[int]:
    """Inclut les helpers de la même installation devenus orphelins pendant l'arrêt."""
    found = set()
    for pid, _, name in rows:
        if name.casefold() != "steamwebhelper.exe":
            continue
        path = image(pid)
        if not path:
            raise SteamError(
                "~ image d'un helper Steam illisible pendant l'arrêt : fin non prouvée, aucun redémarrage."
            )
        if _trusted_child(exe, name, path):
            found.add(pid)
    return found


def _app_window(hwnd: int) -> bool:
    user = ctypes.WinDLL("user32")
    user.GetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int]
    user.GetWindowLongPtrW.restype = ctypes.c_ssize_t
    user.GetWindow.argtypes = [wintypes.HWND, wintypes.UINT]
    user.GetWindow.restype = wintypes.HWND
    style = int(user.GetWindowLongPtrW(hwnd, -20))
    return bool(style & 0x40000) or not bool(style & 0x80 or user.GetWindow(hwnd, 4))  # APPWINDOW / TOOLWINDOW / owner


def main_window(windows: list[Window], helper_pids: set[int]) -> Window | None:
    candidates = [
        w
        for w in windows
        if w.get("process_id") in helper_pids and str(w.get("title") or "").strip() and _app_window(int(w["hwnd"]))
    ]
    preferred = [w for w in candidates if str(w["title"]).strip().casefold() == "steam"]
    selected = preferred or candidates
    if len(selected) > 1:
        raise SteamError(
            "~ plusieurs fenêtres Steam possibles : aucun HWND choisi ; regarder avant toute commande ou reprise."
        )
    return selected[0] if len(selected) == 1 else None


def listeners(port: int) -> list[tuple[str, int]]:
    """Toutes les écoutes IPv4/IPv6 du port ; erreur de lecture = refus, jamais port réputé libre."""
    api = ctypes.WinDLL("iphlpapi")
    found = []
    for family, row_size in ((socket.AF_INET, 24), (socket.AF_INET6, 56)):
        size = wintypes.DWORD()
        status = api.GetExtendedTcpTable(None, ctypes.byref(size), False, family, 3, 0)
        if status not in (0, 122) or not size.value:
            raise SteamError("Table TCP illisible : DevTools non autorisé.")
        buffer = ctypes.create_string_buffer(size.value)
        if api.GetExtendedTcpTable(buffer, ctypes.byref(size), False, family, 3, 0):
            raise SteamError("Table TCP instable ou illisible : DevTools non autorisé.")
        count = struct.unpack_from("<I", buffer.raw)[0]
        if 4 + count * row_size > len(buffer):
            raise SteamError("Table TCP tronquée : DevTools non autorisé.")
        for index in range(count):
            row = buffer.raw[4 + index * row_size : 4 + (index + 1) * row_size]
            port_offset, pid_offset = (8, 20) if family == socket.AF_INET else (20, 52)
            local_port = struct.unpack_from("!H", row, port_offset)[0]
            if local_port == port:
                address = socket.inet_ntop(family, row[4:8] if family == socket.AF_INET else row[:16])
                found.append((address, struct.unpack_from("<I", row, pid_offset)[0]))
    return found


def _cdp_alive(port: int) -> bool:
    """Poignée de main et réponse CDP bornées, sans script ni entrée ; jamais de redirection/proxy."""
    try:
        targets = json.loads(cdp_wheel.fetch_json_list(port, timeout=0.5).decode("utf-8"))
        if not isinstance(targets, list):
            return False
        for target in targets:
            url = target.get("webSocketDebuggerUrl") if isinstance(target, dict) else None
            if not isinstance(url, str) or not re.fullmatch(
                rf"ws://127\.0\.0\.1:{port}/devtools/(?:page|browser)/[^?#\s]+", url
            ):
                continue
            page = cdp_wheel._Page(url, timeout=0.5)
            try:
                reply = page.call("Browser.getVersion")
                return bool(reply.get("protocolVersion") and reply.get("product"))
            finally:
                page.close()
    except (OSError, ValueError, cdp_wheel.websocket.WebSocketException):
        pass
    return False


def process_creation(pid: int) -> int:
    """Date de création native : un PID recyclé ne conserve aucune preuve Steam/CDP."""
    native = ctypes.WinDLL("kernel32", use_last_error=True)
    native.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    native.OpenProcess.restype = wintypes.HANDLE
    native.GetProcessTimes.argtypes = [wintypes.HANDLE, *([ctypes.POINTER(wintypes.FILETIME)] * 4)]
    native.GetProcessTimes.restype = wintypes.BOOL
    native.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = native.OpenProcess(0x1000, False, pid)
    if not handle:
        raise SteamError("Instance de processus Steam illisible : aucune identité de fenêtre/CDP prouvée.")
    try:
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not native.GetProcessTimes(
            handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)
        ):
            raise SteamError("Date de création Steam illisible : identité de fenêtre/CDP inconnue.")
        return (created.dwHighDateTime << 32) | created.dwLowDateTime
    finally:
        native.CloseHandle(handle)


def window_identity(window: Window) -> tuple[int, int, int]:
    return int(window["hwnd"]), int(window["process_id"]), process_creation(int(window["process_id"]))


def debugging_port(helper_pids: set[int]) -> int | None:
    rows = listeners(DEVTOOLS_PORT)
    if any(address not in {"127.0.0.1", "::1"} or pid not in helper_pids for address, pid in rows):
        raise SteamError("Port Steam DevTools occupé par un autre processus ou exposé hors loopback : refus.")
    if not rows or not any(address == "127.0.0.1" for address, _ in rows):
        return None
    instances = {pid: process_creation(pid) for _, pid in rows}
    if not _cdp_alive(DEVTOOLS_PORT) or listeners(DEVTOOLS_PORT) != rows:
        return None
    return DEVTOOLS_PORT if all(process_creation(pid) == creation for pid, creation in instances.items()) else None


def _start(exe: str, argv: list[str]) -> None:
    info = subprocess.STARTUPINFO()
    info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    info.wShowWindow = 4  # SW_SHOWNOACTIVATE
    subprocess.Popen(
        [exe, *argv],
        cwd=str(Path(exe).parent),
        startupinfo=info,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        close_fds=True,
    )


def focus_guard(
    initial: int,
    foreground: Callable[[], tuple[int, int]],
    user_touched: Callable[[], bool],
    hand_back: Callable[[int, int], bool],
) -> Callable[[set[int]], bool]:
    """Ne rend que le premier plan pris par Steam ; un nouveau choix humain reste prioritaire."""
    previous = initial

    def restore(steam_pids: set[int]) -> bool:
        nonlocal previous
        hwnd, pid = foreground()
        if not hwnd or hwnd == previous:
            return False
        if user_touched() or pid not in steam_pids:
            previous = hwnd
            return False
        return hand_back(hwnd, previous) if previous else False

    return restore


def _shutdown(
    exe: str, image: Callable[[int], str], check: Callable[[], None], restore_focus: Callable[[set[int]], bool]
) -> bool:
    rows = process_table()
    owned = roots(exe, rows, image)
    games = games_running(exe, owned, rows, image)
    if games:
        raise SteamError("Redémarrage Steam refusé : jeu ou lanceur descendant actif (" + ", ".join(games) + ").")
    tracked = _remaining_helpers(exe, rows, image)
    check()
    _start(exe, ["-shutdown"])  # Un seul arrêt propre ; aucun kill, terminate ou deuxième essai.
    deadline = time.monotonic() + SHUTDOWN_WAIT_S
    restored = False
    while time.monotonic() < deadline:
        check()
        rows = process_table()
        active = {pid for pid, _, _ in rows}
        tracked.update(_remaining_helpers(exe, rows, image))
        owned = roots(exe, rows, image)
        restored = restore_focus(owned | tracked.intersection(active)) or restored
        if not owned and not tracked.intersection(active):
            return restored
        time.sleep(0.15)
    raise SteamError(
        "~ arrêt Steam envoyé une seule fois, fin non prouvée : aucun redémarrage. Regarder avant toute reprise."
    )


def launch(
    exe: str,
    args: list[str],
    *,
    restart: bool,
    devtools: bool,
    windows: Callable[[], list[Window]],
    image: Callable[[int], str],
    check: Callable[[], None],
    restore_focus: Callable[[set[int]], bool],
) -> dict[str, Any]:
    """Préconditions/args gardés par open_path ; pas de privilège CDP caché ni d'activation du premier plan."""
    check_args(args)
    rows = process_table()
    owned = roots(exe, rows, image)
    helper_pids = helpers(exe, owned, rows, image)
    port = debugging_port(helper_pids) if devtools else None
    if devtools and owned and not restart and port is None:
        raise SteamError(
            "Steam est déjà lancé sans DevTools prouvé. restart=true exige l'ordre explicite de Julien ; téléchargements interrompus, jeux/lanceurs refusés."
        )
    reused = bool(owned) and not restart
    seen = main_window(windows(), helper_pids) if reused else None
    stolen = False
    if owned and restart:
        stolen = _shutdown(exe, image, check, restore_focus)
    if not reused:
        # Contrôle du port à nouveau APRÈS l'arrêt et AVANT le nouveau lancement ; une course reste un refus.
        if devtools and listeners(DEVTOOLS_PORT):
            raise SteamError("Port Steam DevTools encore occupé : aucun nouveau lancement.")
        check()
        _start(exe, [*args, *VIEW_SWITCHES, *([DEVTOOLS_SWITCH] if devtools else [])])
    elif args:
        check()
        _start(exe, args)  # Commande explicite envoyée UNE fois, jamais depuis la boucle d'observation.
    if reused and seen is None and not args:
        check()
        _start(exe, ["steam://open/main"])
    deadline = time.monotonic() + START_WAIT_S
    while time.monotonic() < deadline:
        check()
        rows = process_table()
        active_roots = roots(exe, rows, image)
        helper_pids = helpers(exe, active_roots, rows, image)
        stolen = restore_focus(active_roots | helper_pids) or stolen
        seen = main_window(windows(), helper_pids)
        if seen:
            break
        time.sleep(0.15)
    identity = window_identity(seen) if seen else None
    settle = min(deadline, time.monotonic() + FOCUS_SETTLE_S)
    while seen and time.monotonic() < settle:
        check()
        rows = process_table()
        active_roots = roots(exe, rows, image)
        helper_pids = helpers(exe, active_roots, rows, image)
        stolen = restore_focus(active_roots | helper_pids) or stolen
        time.sleep(0.02)
    if seen:
        rows = process_table()
        helper_pids = helpers(exe, roots(exe, rows, image), rows, image)
        final = main_window(windows(), helper_pids)
        seen = final if final and window_identity(final) == identity else None
    if devtools:
        port = debugging_port(helper_pids)  # Aucune preuve CDP héritée du processus arrêté.
        while seen and not port and not reused and time.monotonic() < deadline:
            check()
            time.sleep(0.15)
            rows = process_table()
            active_roots = roots(exe, rows, image)
            helper_pids = helpers(exe, active_roots, rows, image)
            stolen = restore_focus(active_roots | helper_pids) or stolen
            current = main_window(windows(), helper_pids)
            if not current or window_identity(current) != identity:
                seen = None
                break
            port = debugging_port(helper_pids)
        if seen:
            rows = process_table()
            helper_pids = helpers(exe, roots(exe, rows, image), rows, image)
            final = main_window(windows(), helper_pids)
            seen = final if final and window_identity(final) == identity else None
    verified = bool(seen) and (not devtools or bool(port))
    result: dict[str, Any] = {
        "success": True if verified else None,
        "verified": True if verified else None,
        "path": exe,
        "application": "steam.exe",
        "reused": reused,
        "restarted": bool(owned) and restart,
        "steam": "instance existante réutilisée ; flags inchangés"
        if reused
        else "flags anti-occlusion et accessibilité CEF demandés ; look vérifie le contenu réellement publié",
        "devtools_requested": devtools,
        "devtools_input_registered": False,
    }
    if seen:
        result.update(
            hwnd=seen["hwnd"],
            title=seen["title"],
            process_id=seen["process_id"],
            verified_by="fenêtre Steam unique d'un steamwebhelper descendant de l'exécutable Valve relue après restitution du premier plan",
        )
    else:
        result["message"] = (
            "~ lancement Steam demandé, aucune fenêtre Steam unique relue ; mise à jour/connexion possible. Regarder avant toute reprise."
        )
    if devtools and port:
        result.update(
            devtools_port=port,
            devtools_verified=True,
            devtools_input_note="endpoint Steam prouvé ; aucun canal de saisie/clic CDP enregistré. La molette exige encore ses propres preuves strictes fenêtre/page/viewport, sinon refus avant entrée.",
        )
    elif devtools:
        result.update(devtools_verified=None)
        if seen:
            result["message"] = (
                "~ fenêtre Steam relue, mais endpoint DevTools non prouvé ; ne pas redémarrer/rejouer automatiquement."
            )
    if stolen:
        result["focus_stolen_restored"] = True
    if args:
        result.update(
            arguments_sent=True,
            arguments_verified=None,
            arguments_note="arguments transmis une seule fois ; effet du jeu ou de la page non vérifié par la fenêtre Steam",
        )
    return result
