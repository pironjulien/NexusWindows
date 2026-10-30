"""Appoint WGC/WinRT local, arrêté sur EOF ou après une minute sans demande."""

from __future__ import annotations

import io
import json
import os
import sys
import threading

from PIL import Image

IDLE_S = float(os.environ.get("NEXUS_AGY_CAPTURE_IDLE_S", "61.8"))


def serve() -> None:
    import ocr
    import wgc

    stream, out = sys.stdin.buffer, sys.stdout.buffer
    timer: threading.Timer | None = None

    def arm() -> None:
        nonlocal timer
        if timer:
            timer.cancel()
        timer = threading.Timer(IDLE_S, lambda: os._exit(0))
        timer.daemon = True
        timer.start()

    arm()
    for head in iter(lambda: stream.readline(wgc.MAX_HEADER + 1), b""):
        if timer:
            timer.cancel()
        try:
            if len(head) > wgc.MAX_HEADER or not head.endswith(b"\n"):
                raise ValueError("en-tête invalide")
            request = json.loads(head)
            size = int(request.get("payload_bytes", 0))
            if not 0 <= size <= wgc.MAX_PIXELS * 4:
                raise ValueError("charge invalide")
            payload = stream.read(size)
            if len(payload) != size:
                return
            raw = b""
            if request.get("op") == "wgc":
                shot = wgc.capture_inline(int(request["hwnd"]), min(5.0, max(0.1, float(request.get("timeout", 2)))))
                image = shot.image.convert("RGB")
                answer = {
                    "ok": True,
                    "width": image.width,
                    "height": image.height,
                    "left": shot.origin[0],
                    "top": shot.origin[1],
                }
                raw = image.tobytes()
            elif request.get("op") == "ocr":
                with Image.open(io.BytesIO(payload)) as image:
                    result = ocr.read_inline(image)
                answer = {
                    "ok": True,
                    "status": result.status,
                    "error": result.error,
                    "truncated": result.truncated,
                    "lines": [{"text": line.text, "rect": line.rect} for line in result.lines],
                }
            else:
                raise ValueError("opération inconnue")
            out.write(json.dumps({**answer, "payload_bytes": len(raw)}).encode() + b"\n" + raw)
        except Exception as error:
            out.write(json.dumps({"ok": False, "error": f"{type(error).__name__}: {error}"[:240]}).encode() + b"\n")
        out.flush()
        arm()


if __name__ == "__main__":
    serve()
