"""Windows.Media.Ocr local sur les pixels de la cible. Le texte lu ne donne jamais un accord."""

from __future__ import annotations

import asyncio
import io
from dataclasses import dataclass, field

from PIL import Image

MAX_LINES = 120
_ENGINE = None
_UNAVAILABLE: str | None = None


@dataclass(frozen=True)
class Line:
    text: str
    rect: tuple[int, int, int, int]


@dataclass
class Result:
    lines: list[Line] = field(default_factory=list)
    status: str = "ok"
    error: str = ""
    truncated: bool = False


def read(image: Image.Image) -> Result:
    """WinRT reste dans le processus d'appoint, partagé avec WGC et libéré à son expiration."""
    try:
        try:
            from . import wgc
        except ImportError:
            import wgc
        buffer = io.BytesIO()
        image.convert("RGB").save(buffer, format="PNG")
        answer, _ = wgc._request({"op": "ocr"}, buffer.getvalue(), timeout=5.0)
        lines = []
        for row in answer["lines"]:
            rect = tuple(int(v) for v in row["rect"])
            if len(rect) != 4 or rect[2] <= 0 or rect[3] <= 0:
                raise ValueError("Rectangle OCR invalide.")
            lines.append(Line(str(row["text"]), (rect[0], rect[1], rect[2], rect[3])))
        return Result(lines, str(answer["status"]), str(answer.get("error") or ""), bool(answer.get("truncated")))
    except Exception as error:
        return Result(status="failed", error=f"{type(error).__name__}: {error}"[:240])


async def _recognize(engine, image: Image.Image):
    from winrt.windows.graphics.imaging import BitmapPixelFormat, SoftwareBitmap
    from winrt.windows.storage.streams import DataWriter

    r, g, b, a = image.convert("RGBA").split()
    writer = DataWriter()
    try:
        writer.write_bytes(Image.merge("RGBA", (b, g, r, a)).tobytes())
        bitmap = SoftwareBitmap.create_copy_from_buffer(
            writer.detach_buffer(), BitmapPixelFormat.BGRA8, image.width, image.height
        )
        try:
            return await engine.recognize_async(bitmap)
        finally:
            bitmap.close()
    finally:
        writer.close()


def read_inline(image: Image.Image) -> Result:
    """Réservé au worker synchrone : coordonnées remises à l'échelle exacte de l'image fournie."""
    global _ENGINE, _UNAVAILABLE
    if _ENGINE is None and _UNAVAILABLE is None:
        try:
            from winrt.windows.media.ocr import OcrEngine

            _ENGINE = OcrEngine.try_create_from_user_profile_languages()
            if _ENGINE is None:
                _UNAVAILABLE = "Aucune langue OCR installée dans Windows."
        except Exception as error:
            _UNAVAILABLE = f"{type(error).__name__}: {error}"[:240]
    if _ENGINE is None:
        return Result(status="unavailable", error=_UNAVAILABLE or "WinRT indisponible.")
    side = getattr(type(_ENGINE), "max_image_dimension", 0) or 10000
    scale = min(1.0, side / max(image.size))
    small = image.resize((max(1, int(image.width * scale)), max(1, int(image.height * scale)))) if scale < 1 else image
    try:
        result = asyncio.run(_recognize(_ENGINE, small))
        rows = list(result.lines)
        lines: list[Line] = []
        for row in rows[:MAX_LINES]:
            words = list(row.words)
            if not words or not row.text.strip():
                continue
            left = min(word.bounding_rect.x for word in words)
            top = min(word.bounding_rect.y for word in words)
            right = max(word.bounding_rect.x + word.bounding_rect.width for word in words)
            bottom = max(word.bounding_rect.y + word.bounding_rect.height for word in words)
            sx, sy = image.width / small.width, image.height / small.height
            rect = (round(left * sx), round(top * sy), round((right - left) * sx), round((bottom - top) * sy))
            lines.append(Line(row.text.strip(), rect))
        return Result(lines, truncated=len(rows) > MAX_LINES)
    except Exception as error:
        return Result(status="failed", error=f"{type(error).__name__}: {error}"[:240])
