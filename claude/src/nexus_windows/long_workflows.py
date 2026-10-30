"""Continuation volatile d'un préfixe vérifié ; aucun objet UIA ni autorisation n'est conservé."""

from __future__ import annotations

import copy
import math
import threading
import time
import uuid
from collections.abc import Callable
from typing import Any

BUDGET_S = 30.0


def budget(value: float) -> float:
    if (
        isinstance(value, bool)
        or not isinstance(value, (float, int))
        or not math.isfinite(value)
        or not 0 < value <= BUDGET_S
    ):
        raise ValueError("time_budget must be finite and greater than 0, up to 30 seconds.")
    return float(value)


def clean(value: Any) -> Any:
    """Instantané JSON récursif : les consentements ne franchissent jamais une frontière d'appel."""
    if isinstance(value, dict):
        return {key: clean(item) for key, item in value.items() if key not in {"confirm", "accord"}}
    if isinstance(value, list):
        return [clean(item) for item in value]
    return copy.deepcopy(value)


class Continuations:
    def __init__(self, failed: Callable[[dict], None], lifetime: float = BUDGET_S):
        self._lock = threading.RLock()
        self._state: dict | None = None
        self._timer: threading.Timer | None = None
        self._failed = failed
        self._lifetime = lifetime
        self._generation = 0

    @property
    def generation(self) -> int:
        with self._lock:
            return self._generation

    def discard(self) -> None:
        with self._lock:
            self._generation += 1
            state, self._state = self._state, None
            if self._timer:
                self._timer.cancel()
                self._timer = None
        if state:
            self._failed(state)

    def put(self, state: dict, *, generation: int | None = None) -> str:
        with self._lock:
            if generation is not None and generation != self._generation:
                raise ValueError("Workflow was cancelled or superseded; no continuation was retained.")
            self.discard()
            token = uuid.uuid4().hex
            self._state = clean(state) | {
                "token": token,
                "observed": False,
                "expires": time.monotonic() + self._lifetime,
                "generation": self._generation,
            }
            self._timer = threading.Timer(self._lifetime, self._expire, args=(token,))
            self._timer.daemon = True
            self._timer.start()
            return token

    def _expire(self, token: str) -> None:
        with self._lock:
            if not self._state or self._state["token"] != token:
                return
            self.discard()

    def observed(self, hwnd: int) -> None:
        with self._lock:
            if self._state and self._state["hwnd"] == hwnd:
                self._state["observed"] = True

    def take(self, token: str | None) -> dict:
        with self._lock:
            self._generation += 1
            state, self._state = self._state, None
            if self._timer:
                self._timer.cancel()
                self._timer = None
            valid = bool(
                state and token == state["token"] and state["observed"] and time.monotonic() <= state["expires"]
            )
            if valid and state:
                state["generation"] = self._generation
        if not state:
            raise ValueError("Continuation absent, consumed or expired; no input was sent.")
        if not valid:
            self._failed(state)
            raise ValueError("Continuation requires its current token and a fresh successful look; no input was sent.")
        return state
