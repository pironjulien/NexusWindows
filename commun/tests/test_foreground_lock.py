"""Jeton commun souris/premier plan (foreground_lock.py) : exclusion, imbrication par fil, attente bornée et
interruptible. Fichier de verrou propre au test, jamais le vrai ~/.nexus/foreground.lock (les agents de Julien
peuvent le tenir pendant le test)."""

from __future__ import annotations

import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import foreground_lock as fl  # noqa: E402


class ForegroundLockTest(unittest.TestCase):
    def setUp(self) -> None:
        self._dir = tempfile.TemporaryDirectory()
        self._real = fl.LOCK_FILE
        fl.LOCK_FILE = Path(self._dir.name) / "foreground.lock"

    def tearDown(self) -> None:
        fl.LOCK_FILE = self._real
        self._dir.cleanup()

    def test_never_the_real_lock_file(self) -> None:
        self.assertNotEqual(fl.LOCK_FILE, Path.home() / ".nexus" / "foreground.lock")

    def test_holder_named_while_held_then_free(self) -> None:
        self.assertIsNone(fl.holder())
        with fl.ForegroundLock("NexusTest", purpose="essai"):
            who = fl.holder()
            assert who is not None
            self.assertEqual((who["owner"], who["purpose"]), ("NexusTest", "essai"))
        self.assertIsNone(fl.holder())

    def test_two_instances_exclude_each_other_even_in_one_process(self) -> None:
        with fl.ForegroundLock("NexusA"):
            other = fl.ForegroundLock("NexusB", timeout=0.2)
            started = time.monotonic()
            self.assertFalse(other.acquire())
            self.assertGreaterEqual(time.monotonic() - started, 0.2)
            with self.assertRaises(TimeoutError) as raised:
                with fl.ForegroundLock("NexusB", timeout=0.1):
                    pass
            self.assertIn("NexusA tient la souris", str(raised.exception))

    def test_nesting_in_the_same_thread_keeps_the_token_until_the_outer_exit(self) -> None:
        with fl.ForegroundLock("NexusA"):
            with fl.ForegroundLock("NexusA", timeout=0.1):
                pass
            self.assertIsNotNone(fl.holder())  # la sortie intérieure ne rend pas le jeton
        self.assertIsNone(fl.holder())

    def test_another_thread_of_the_same_agent_waits(self) -> None:
        results: list[bool] = []
        with fl.ForegroundLock("NexusA"):
            thread = threading.Thread(target=lambda: results.append(fl.ForegroundLock("NexusA", timeout=0.2).acquire()))
            thread.start()
            thread.join(5)
        self.assertEqual(results, [False])

    def test_abort_interrupts_the_wait(self) -> None:
        class Stop(Exception):
            pass

        def abort() -> None:
            raise Stop

        with fl.ForegroundLock("NexusA"):
            started = time.monotonic()
            with self.assertRaises(Stop):
                fl.ForegroundLock("NexusB", timeout=5, abort=abort).acquire()
            self.assertLess(time.monotonic() - started, 1.0)
        self.assertIsNone(fl.holder())  # handle refermé : rien de tenu


if __name__ == "__main__":
    unittest.main()
