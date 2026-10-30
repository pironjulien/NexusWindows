"""Steam (client Valve) : interface CEF/Chromium dans des fenêtres SDL de steamwebhelper.exe.

Mesuré le 2026-10-14 : sans commutateur, une fenêtre Steam recouverte n'est ni dessinée ni publiée (« 0 éléments sur
5 nœuds UIA », captures noires). Steam documente ses propres commutateurs (aide de steamclient64.dll) :
- `-cef-disable-occlusion` « disables Chromium window occlusion testing » : la fenêtre recouverte reste dessinée ;
- `-cef-force-accessibility` : arbre d'accessibilité publié d'emblée (--force-renderer-accessibility) ;
- `-cef-enable-debugging` « enables Chromium remote debugging, defaulting to localhost:8080 » : port DevTools sur
  127.0.0.1 (Steam ajoute --remote-allow-origins=*), qui ouvre la session Steam de l'utilisateur à tout programme local tant
  que Steam reste ouvert ainsi. Seulement par launch(app="Steam", devtools=true), avec l'accord de l'utilisateur ; jamais
  fourni par le modèle dans args (policy.CHROMIUM_DENY_SWITCHES).
Les commutateurs ne valent qu'au démarrage : Steam déjà ouvert doit être relancé (restart=true, accord de l'utilisateur :
Steam se ferme, téléchargements interrompus ; refusé si un jeu tourne).
"""

from __future__ import annotations

import ctypes
import http.client
import json
import os
import re
import socket
import subprocess
import time
import urllib.parse
import winreg
from pathlib import Path

from . import log
from . import win32 as w

STEAM_EXE = "steam.exe"
WEBHELPER = "steamwebhelper.exe"
VIEW_SWITCHES = ("-cef-disable-occlusion", "-cef-force-accessibility")
DEVTOOLS_SWITCH = "-cef-enable-debugging"
DEVTOOLS_PORT = 8080  # fixé par Steam (« defaulting to localhost:8080 »)
# Processus de Steam lui-même : tout autre descendant de steam.exe est un jeu (ou son lanceur) qu'une relance tuerait.
STEAM_PROCESSES = {
    "steam.exe", "steamwebhelper.exe", "steamservice.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe",
    "gldriverquery.exe", "gldriverquery64.exe", "vulkandriverquery.exe", "vulkandriverquery64.exe", "conhost.exe",
}  # fmt: skip
SHUTDOWN_WAIT_S = 60.0
START_WAIT_S = 90.0  # premier démarrage : mise à jour du client possible (mesuré : ~60 s)


class SteamError(RuntimeError):
    pass


def canonical_exe(path: str) -> str | None:
    """Client Steam existant, absolu et canonique ; un simple nom de fichier ne prouve pas son identité."""
    target = Path(os.path.expandvars(path))
    if not target.is_absolute():
        return None
    try:
        target = target.resolve(strict=True)
        if target.name.lower() != STEAM_EXE or not target.is_file():
            return None
        if any((target.parent / name).is_file() for name in ("steamclient64.dll", "steamui.dll")):
            return str(target)
    except OSError, RuntimeError:
        pass
    return None


def steam_exe() -> str | None:
    """Chemin de steam.exe (registre de Valve, à défaut l'emplacement par défaut)."""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            value, _ = winreg.QueryValueEx(key, "SteamExe")
        path = canonical_exe(str(value))
        if path:
            return path
    except OSError:
        pass
    default = os.path.expandvars(r"%ProgramFiles(x86)%\Steam\steam.exe")
    return canonical_exe(default)


def is_steam_exe(path: str) -> bool:
    """steam.exe du client Valve (à côté de steamclient64.dll ou steamui.dll), quel que soit le chemin donné."""
    return canonical_exe(path) is not None


def is_steam_window(hwnd: int) -> bool:
    pid = w.window_pid(hwnd)
    if w.process_name(pid).lower() != WEBHELPER:
        return False
    try:
        return pid in webhelper_pids()
    except SteamError:
        return False


def _table() -> list[tuple[int, int, str]]:
    table = w.process_table()
    if not table:
        raise SteamError("Steam process table unavailable: process ownership cannot be verified.")
    return table


def _roots(table: list[tuple[int, int, str]], exe: str | None = None) -> dict[int, Path]:
    expected = canonical_exe(exe) if exe is not None else None
    if exe is not None and not expected:
        raise SteamError("Steam executable identity is not verified: give its existing absolute client path.")
    roots: dict[int, Path] = {}
    for pid, _parent, name in table:
        if name.lower() != STEAM_EXE:
            continue
        path = canonical_exe(w.process_path(pid))
        if not path or (expected is not None and Path(path) != Path(expected)):
            raise SteamError(f"Steam process {pid} does not have the expected client path: ownership is unknown.")
        roots[pid] = Path(path)
    return roots


def steam_pids(table: list[tuple[int, int, str]] | None = None, exe: str | None = None) -> list[int]:
    return list(_roots(table if table is not None else _table(), exe))


def _helper_in_install(pid: int, root: Path) -> bool:
    path = Path(w.process_path(pid))
    if not path.is_absolute():
        return False
    try:
        path = path.resolve(strict=True)
        return path.name.lower() == WEBHELPER and path.is_file() and path.is_relative_to(root.parent)
    except OSError, RuntimeError:
        return False


def _descendants(roots: list[int], table: list[tuple[int, int, str]]) -> list[tuple[int, str]]:
    children: dict[int, list[tuple[int, str]]] = {}
    for pid, parent, name in table:
        children.setdefault(parent, []).append((pid, name))
    out: list[tuple[int, str]] = []
    todo = list(roots)
    seen = set(roots)
    while todo:
        for pid, name in children.get(todo.pop(), []):
            if pid not in seen:
                seen.add(pid)
                out.append((pid, name))
                todo.append(pid)
    return out


def games_running(table: list[tuple[int, int, str]] | None = None) -> list[str]:
    """Programmes lancés par Steam qui ne sont pas Steam lui-même (jeux, lanceurs) : une relance les fermerait."""
    table = table if table is not None else _table()
    names = {
        name
        for root_pid, root in _roots(table).items()
        for pid, name in _descendants([root_pid], table)
        if not _internal_process(pid, name, root)
    }
    return sorted(names)


def _internal_process(pid: int, name: str, root: Path) -> bool:
    if name.lower() not in STEAM_PROCESSES:
        return False
    image = Path(w.process_path(pid))
    if not image.is_absolute():
        return False
    try:
        image = image.resolve(strict=True)
        if not image.is_file() or image.name.lower() != name.lower():
            return False
        if image.is_relative_to(root.parent):
            return True
        if name.lower() == "conhost.exe":
            system = Path(os.environ.get("SystemRoot", ""))
            return system.is_absolute() and image.parent in (
                (system / "System32").resolve(),
                (system / "SysWOW64").resolve(),
            )
        if name.lower() == "steamservice.exe":
            return any(
                image == (Path(base) / "Steam" / "SteamService.exe").resolve()
                for variable in ("CommonProgramFiles", "CommonProgramFiles(x86)")
                if (base := os.environ.get(variable)) and Path(base).is_absolute()
            )
    except OSError, RuntimeError:
        pass
    return False


def webhelper_pids(table: list[tuple[int, int, str]] | None = None, exe: str | None = None) -> set[int]:
    table = table if table is not None else _table()
    return {
        pid
        for root_pid, root in _roots(table, exe).items()
        for pid, name in _descendants([root_pid], table)
        if name.lower() == WEBHELPER and _helper_in_install(pid, root)
    }


def listeners() -> list[tuple[int, str, int]]:
    """Écoutes IPv4 ET IPv6 ; une lecture impossible ne signifie jamais « port libre »."""
    from .launch import TCP_TABLE_OWNER_PID_LISTENER, _GetExtendedTcpTable

    class Tcp6Row(ctypes.Structure):
        _fields_ = [
            ("local", ctypes.c_ubyte * 16), ("scope", ctypes.c_ulong), ("port", ctypes.c_ulong),
            ("remote", ctypes.c_ubyte * 16), ("remote_scope", ctypes.c_ulong), ("remote_port", ctypes.c_ulong),
            ("state", ctypes.c_ulong), ("pid", ctypes.c_ulong),
        ]  # fmt: skip

    out: list[tuple[int, str, int]] = []
    for family in (socket.AF_INET, socket.AF_INET6):
        size = ctypes.c_ulong(0)
        status = _GetExtendedTcpTable(None, ctypes.byref(size), False, family, TCP_TABLE_OWNER_PID_LISTENER, 0)
        if status not in (0, 122):  # ERROR_INSUFFICIENT_BUFFER : taille relevée, allocation ci-dessous
            raise SteamError(f"Steam TCP listener table unavailable ({status}): port ownership is unknown.")
        buffer = ctypes.create_string_buffer(size.value + 4096)
        for _attempt in range(3):
            size = ctypes.c_ulong(len(buffer))
            status = _GetExtendedTcpTable(buffer, ctypes.byref(size), False, family, TCP_TABLE_OWNER_PID_LISTENER, 0)
            if status != 122:
                break
            buffer = ctypes.create_string_buffer(size.value + 4096)
        if status:
            raise SteamError(f"Steam TCP listener table unavailable ({status}): port ownership is unknown.")
        count = ctypes.c_ulong.from_buffer(buffer).value
        if family == socket.AF_INET:
            rows = (ctypes.c_ulong * (count * 6)).from_buffer(buffer, 4)
            for i in range(count):
                _state, address, port, _remote, _remote_port, pid = rows[i * 6 : i * 6 + 6]
                out.append((socket.ntohs(port & 0xFFFF), socket.inet_ntoa(address.to_bytes(4, "little")), pid))
        else:
            for row in (Tcp6Row * count).from_buffer(buffer, 4):
                out.append((socket.ntohs(row.port & 0xFFFF), socket.inet_ntop(family, bytes(row.local)), row.pid))
    return out


def _identity(pid: int) -> int:
    from .session import process_identity

    return process_identity(pid)


def _cdp_alive(port: int) -> bool:
    """Réponse CDP réelle, bornée, sans redirection ni proxy ; aucune entrée ni script dans Steam."""
    from . import cdp

    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=0.5)
    page: cdp.Page | None = None
    try:
        connection.request("GET", "/json/version")
        response = connection.getresponse()
        if response.status != 200:
            return False
        raw = response.read(65537)
        if len(raw) > 65536:
            return False
        metadata = json.loads(raw)
        url = metadata.get("webSocketDebuggerUrl") if isinstance(metadata, dict) else None
        if not isinstance(url, str):
            return False
        parts = urllib.parse.urlsplit(url)
        if (
            parts.scheme != "ws"
            or parts.hostname not in ("127.0.0.1", "localhost")
            or parts.port != port
            or parts.username
            or parts.password
            or parts.query
            or parts.fragment
            or not re.fullmatch(r"/devtools/(?:page|browser)/[^?#\s]+", parts.path)
        ):
            return False
        local_url = urllib.parse.urlunsplit(parts._replace(netloc=f"127.0.0.1:{port}"))
        page = cdp.Page(port, timeout_s=0.5, target={"webSocketDebuggerUrl": local_url})
        reply = page.call("Browser.getVersion")
        return bool(reply.get("protocolVersion") and reply.get("product"))
    except cdp.CDPError, http.client.HTTPException, OSError, ValueError, TypeError:
        return False
    finally:
        if page is not None:
            page.close()
        connection.close()


def debugging_port(exe: str | None = None) -> int | None:
    """Port DevTools d'un Steam déjà ouvert avec -cef-enable-debugging : écouté par son steamwebhelper, sur
    127.0.0.1 (éventuellement aussi ::1), et qui répond vraiment en DevTools. None sinon."""
    helpers = webhelper_pids(exe=exe)
    rows = listeners()
    for port, address, pid in rows:
        owners = {(addr, owner) for number, addr, owner in rows if number == port}
        if (
            port != DEVTOOLS_PORT
            or pid not in helpers
            or address != "127.0.0.1"
            or not all(addr in ("127.0.0.1", "::1") and owner == pid for addr, owner in owners)
        ):
            continue
        identity = _identity(pid)
        if identity and _cdp_alive(port):
            after = {(addr, owner) for number, addr, owner in listeners() if number == port}
            if after == owners and pid in webhelper_pids(exe=exe) and _identity(pid) == identity:
                return port
    return None


def main_window(table: list[tuple[int, int, str]] | None = None, exe: str | None = None) -> int:
    """Fenêtre Steam prouvée par son processus ; refus si plusieurs cibles restent possibles."""
    from .snapshot import is_app_window

    helpers = webhelper_pids(table, exe)
    windows = [h for h in w.enum_windows() if w.window_pid(h) in helpers and is_app_window(h)]
    titled = [h for h in windows if w.window_text(h).strip().lower() == "steam"]
    candidates = titled or windows
    if len(candidates) > 1:
        raise SteamError("Several Steam windows match: the main window is ambiguous; use windows, then look.")
    return candidates[0] if candidates else 0


def shutdown(exe: str, wait_s: float = SHUTDOWN_WAIT_S) -> None:
    """Fermeture propre (steam.exe -shutdown), attendue jusqu'à ce que steam.exe et steamwebhelper aient disparu.
    Refus si un jeu lancé par Steam tourne (il serait fermé)."""
    table = _table()
    roots = _roots(table, exe)
    games = games_running(table)
    if games:
        raise SteamError(
            f"Steam restart refused: {', '.join(games)} is running under Steam (game or launcher, it would be closed)."
        )
    if not roots:
        return
    log.event("steam", {"action": "shutdown", "reason": "restart with switches"})
    subprocess.Popen([exe, "-shutdown"], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.monotonic() + wait_s
    while time.monotonic() < deadline:
        table = _table()
        if not steam_pids(table, exe) and not any(
            name.lower() == WEBHELPER and any(_helper_in_install(pid, root) for root in roots.values())
            for pid, _parent, name in table
        ):
            time.sleep(1.0)  # verrous de Steam (fichiers, port) relâchés
            return
        time.sleep(0.5)
    raise SteamError(f"Steam did not close within {wait_s:.0f} s (confirmation window?): nothing was restarted.")


def command(exe: str, args: str, devtools: bool) -> str:
    """Ligne de commande : arguments du modèle (déjà contrôlés par la politique), puis ceux de l'outil."""
    check_args(args)
    policy_args = list(VIEW_SWITCHES) + ([DEVTOOLS_SWITCH] if devtools else [])
    return subprocess.list2cmdline([exe]) + (" " + args if args.strip() else "") + " " + " ".join(policy_args)


def check_args(args: str) -> None:
    """Steam est toujours traité comme Chromium, même si les ressources CEF vivent dans un sous-dossier."""
    from . import policy

    reason = policy.switch_refusal(args, chromium=True, browser=True)
    if reason:
        raise SteamError(f"Steam launch refused: {reason}.")
    for token in policy.split_args(args):
        key = policy.switch_key(token)
        uri_command = token.lower().removeprefix("steam://").split("/", 1)[0].split("?", 1)[0].split("#", 1)[0]
        if key in ("shutdown", "restart") or (
            token.lower().startswith("steam://") and uri_command in ("exit", "quit", "shutdown", "restart")
        ):
            raise SteamError(
                "Steam lifecycle arguments refused: use restart=true explicitly (refused while a game runs)."
            )


def port_taken_by_other(exe: str | None = None) -> str | None:
    """Le port 8080 est-il déjà pris par un autre programme ? (Steam ne pourrait pas l'ouvrir ; un autre programme
    serait pris pour le DevTools de Steam.) Nom du programme, ou None."""
    helpers = webhelper_pids(exe=exe)
    rows = [(address, pid) for port, address, pid in listeners() if port == DEVTOOLS_PORT]
    for address, pid in rows:
        if address not in ("127.0.0.1", "::1") or pid not in helpers:
            return f"{w.process_name(pid)} (pid {pid}, listening on {address})"
    if len({pid for _address, pid in rows}) > 1:
        return "several Steam processes (ambiguous port owner)"
    return None
