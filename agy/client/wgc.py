"""Capture HWND par Windows.Graphics.Capture, dans un appoint local à durée de vie bornée.

Le pilote Direct3D et windows-capture restent hors du serveur MCP. Aucun choix par titre, aucun déplacement,
activation ou capture d'écran en repli : le HWND et sa géométrie doivent rester ceux demandés.
"""

from __future__ import annotations

import ctypes
import json
import subprocess
import sys
import threading
import time
from collections.abc import Callable
from ctypes import wintypes
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from PIL import Image

MAX_PIXELS = 67_108_864  # borne de mémoire du protocole (8192² pixels)
MAX_HEADER = 262_144
START_TIMEOUT = 16.18
READ_TIMEOUT = 5.0
_HELPER: subprocess.Popen | None = None
_LOCK = threading.Lock()


class CaptureError(RuntimeError):
    """Aucune image fiable de cette fenêtre n'a été obtenue."""


@dataclass
class Shot:
    image: Image.Image
    origin: tuple[int, int]
    method: str = "wgc"


def _drop(helper: subprocess.Popen) -> None:
    global _HELPER
    try:
        if helper.poll() is None:
            helper.kill()
    except OSError:
        pass
    try:
        helper.wait(timeout=1)
    except subprocess.TimeoutExpired:
        pass
    for pipe in (helper.stdin, helper.stdout):
        if pipe:
            try:
                pipe.close()
            except (OSError, ValueError):
                pass
    if _HELPER is helper:
        _HELPER = None


def _bounded(helper: subprocess.Popen, read: Callable[[], bytes], timeout: float) -> bytes:
    result: list[bytes] = []

    def run() -> None:
        try:
            result.append(read())
        except (OSError, ValueError):
            pass

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    thread.join(timeout)
    if thread.is_alive():
        _drop(helper)
        raise CaptureError("Appoint de capture/OCR : délai dépassé, processus arrêté.")
    return result[0] if result else b""


def _request(request: dict[str, Any], payload: bytes = b"", timeout: float = 2.0) -> tuple[dict[str, Any], bytes]:
    """Protocole privé WGC/OCR. Un retry ne concerne qu'une lecture, jamais une entrée utilisateur."""
    global _HELPER
    if len(payload) > MAX_PIXELS * 4:
        raise CaptureError("Image trop grande pour l'appoint de capture/OCR.")
    with _LOCK:
        helper: subprocess.Popen | None = None
        head = b""
        for attempt in range(2):
            fresh = _HELPER is None or _HELPER.poll() is not None
            if fresh:
                _HELPER = subprocess.Popen(
                    [sys.executable, "-B", str(Path(__file__).with_name("wgc_helper.py"))],
                    stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.DEVNULL,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
            helper = _HELPER
            assert helper is not None and helper.stdin is not None and helper.stdout is not None
            input_pipe, output_pipe = helper.stdin, helper.stdout
            head = b""
            try:

                def send() -> bytes:
                    input_pipe.write(json.dumps({**request, "payload_bytes": len(payload)}).encode() + b"\n" + payload)
                    input_pipe.flush()
                    return output_pipe.readline(MAX_HEADER + 1)

                head = _bounded(helper, send, timeout + READ_TIMEOUT + (START_TIMEOUT if fresh else 0))
            except OSError:
                pass
            if head:
                break
            _drop(helper)
            if attempt:
                raise CaptureError("Appoint de capture/OCR arrêté sans réponse.")
        assert helper is not None and helper.stdout is not None
        output_pipe = helper.stdout
        try:
            if len(head) > MAX_HEADER or not head.endswith(b"\n"):
                raise ValueError("en-tête trop long ou incomplet")
            answer = json.loads(head)
            size = answer.get("payload_bytes", 0)
            if not isinstance(size, int) or isinstance(size, bool) or not 0 <= size <= MAX_PIXELS * 4:
                raise ValueError("taille invalide")
            raw = _bounded(helper, lambda: output_pipe.read(size), READ_TIMEOUT) if size else b""
            if len(raw) != size:
                raise ValueError("image tronquée")
        except (ValueError, TypeError, AttributeError) as error:
            _drop(helper)
            raise CaptureError(f"Appoint de capture/OCR : réponse invalide ({error}).") from None
        if not answer.get("ok"):
            raise CaptureError(str(answer.get("error") or "Capture/OCR indisponible."))
        return answer, raw


def capture(hwnd: int, timeout: float = 2.0) -> Shot:
    answer, raw = _request({"op": "wgc", "hwnd": int(hwnd), "timeout": timeout}, timeout=timeout)
    width, height = answer.get("width"), answer.get("height")
    if (
        not isinstance(width, int)
        or not isinstance(height, int)
        or width <= 0
        or height <= 0
        or width * height > MAX_PIXELS
        or len(raw) != width * height * 3
    ):
        raise CaptureError("Windows.Graphics.Capture : dimensions invalides.")
    return Shot(Image.frombytes("RGB", (width, height), raw), (int(answer["left"]), int(answer["top"])))


def _geometry(hwnd: int) -> tuple[int, tuple[int, int, int, int], tuple[int, int, int, int]]:
    user = ctypes.windll.user32
    user.IsWindow.argtypes, user.IsWindow.restype = [wintypes.HWND], wintypes.BOOL
    user.IsIconic.argtypes, user.IsIconic.restype = [wintypes.HWND], wintypes.BOOL
    user.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    if not user.IsWindow(hwnd) or user.IsIconic(hwnd):
        raise CaptureError("Fenêtre fermée ou réduite : aucune capture indépendante disponible.")
    pid, outer, frame = wintypes.DWORD(), wintypes.RECT(), wintypes.RECT()
    user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    if not pid.value or not user.GetWindowRect(hwnd, ctypes.byref(outer)):
        raise CaptureError("Fenêtre disparue pendant la capture.")
    dwm = ctypes.windll.dwmapi
    dwm.DwmGetWindowAttribute.argtypes = [wintypes.HWND, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
    dwm.DwmGetWindowAttribute.restype = ctypes.c_long
    if dwm.DwmGetWindowAttribute(hwnd, 9, ctypes.byref(frame), ctypes.sizeof(frame)) != 0:
        frame = outer
    return (
        pid.value,
        (outer.left, outer.top, outer.right, outer.bottom),
        (frame.left, frame.top, frame.right, frame.bottom),
    )


def capture_inline(hwnd: int, timeout: float = 2.0) -> Shot:
    """Réservé au processus d'appoint ; l'import Direct3D ne touche jamais le serveur MCP."""
    from windows_capture import WindowsCapture

    try:
        ctypes.windll.user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4))
    except (AttributeError, OSError):
        pass
    before = _geometry(hwnd)
    images: list[Image.Image] = []
    errors: list[str] = []
    done = threading.Event()
    native = WindowsCapture(cursor_capture=False, draw_border=False, window_hwnd=hwnd)

    @native.event
    def on_frame_arrived(frame, control):
        try:
            if not images:
                if frame.width <= 0 or frame.height <= 0 or frame.width * frame.height > MAX_PIXELS:
                    raise CaptureError("Image WGC trop grande ou vide.")
                images.append(
                    Image.frombytes(
                        "RGB", (frame.width, frame.height), frame.frame_buffer.tobytes(), "raw", "BGRX", 0, 1
                    )
                )
        except Exception as error:
            errors.append(str(error))
        finally:
            control.stop()
            done.set()

    @native.event
    def on_closed():
        done.set()

    control = native.start_free_threaded()
    try:
        if not done.wait(timeout):
            raise CaptureError("Windows.Graphics.Capture : aucune image reçue dans le délai.")
    finally:
        try:
            deadline = time.monotonic() + 1.0
            while done.is_set() and not control.is_finished() and time.monotonic() < deadline:
                time.sleep(0.005)
            if done.is_set() and control.is_finished():
                control.wait()  # le callback a déjà arrêté : un second stop fuit des handles
            else:
                control.stop()
        finally:
            native.capture = None  # cycle Rust/Python invisible au GC
    if errors or not images:
        raise CaptureError(errors[0] if errors else "Capture WGC fermée sans image.")
    if _geometry(hwnd) != before:
        raise CaptureError("Fenêtre déplacée, redimensionnée ou remplacée pendant la capture ; relire.")
    _, outer, frame = before
    image = images[0]
    frame_size = (frame[2] - frame[0], frame[3] - frame[1])
    if image.size == (outer[2] - outer[0], outer[3] - outer[1]) and image.size != frame_size:
        image = image.crop((frame[0] - outer[0], frame[1] - outer[1], frame[2] - outer[0], frame[3] - outer[1]))
    if image.size != frame_size or min(frame_size) <= 0:
        raise CaptureError("Géométrie WGC différente du cadre de la cible ; aucune image approximative publiée.")
    return Shot(image, (frame[0], frame[1]))
