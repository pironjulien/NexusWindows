"""Read-only comparison of the historical provider, scoped to one owned local Edge HWND."""
import ctypes
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import time


def birth_ticks(value):
    match = re.fullmatch(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)\.(\d{7})Z", value)
    if not match:
        raise ValueError("Seven decimal UTC birth required")
    instant = datetime.datetime.strptime(match[1], "%Y-%m-%dT%H:%M:%S")
    delta = instant - datetime.datetime(1601, 1, 1)
    return (delta.days * 86400 + delta.seconds) * 10_000_000 + int(match[2])


def run(spec):
    from ctypes import wintypes as wt
    repo = Path(__file__).resolve().parents[3]
    source = repo / "claude" / "src"
    sys.path.insert(0, str(source))
    from nexus_windows import snapshot, uia

    if str(Path(snapshot.__file__).resolve()) != str((source / "nexus_windows" / "snapshot.py").resolve()):
        raise ValueError("Historical source identity changed")
    if not re.fullmatch(r"Nexus simulated [0-9a-f-]{36}", spec["title"]):
        raise ValueError("Owned fixture nonce required")
    if not re.fullmatch(r"http://127\.0\.0\.1:\d+/", spec["url"]):
        raise ValueError("Exact local fixture URL required")
    target, owner = int(spec["hwnd"]), int(spec["pid"])
    expected_ticks = birth_ticks(spec["birth"])
    expected_exe = os.path.normcase(os.path.abspath(spec["executable"]))
    if hashlib.sha256(Path(expected_exe).read_bytes()).hexdigest().upper() != spec["executableSha256"].upper():
        raise ValueError("Signed executable bytes changed")
    kernel, user = ctypes.WinDLL("kernel32", use_last_error=True), ctypes.WinDLL("user32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
    kernel.OpenProcess.restype = wt.HANDLE
    kernel.CloseHandle.argtypes = [wt.HANDLE]
    kernel.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
    kernel.WaitForSingleObject.restype = wt.DWORD
    kernel.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(wt.FILETIME)] * 4
    kernel.GetProcessTimes.restype = wt.BOOL
    kernel.QueryFullProcessImageNameW.argtypes = [wt.HANDLE, wt.DWORD, wt.LPWSTR, ctypes.POINTER(wt.DWORD)]
    kernel.QueryFullProcessImageNameW.restype = wt.BOOL
    user.IsWindow.argtypes = [wt.HWND]
    user.IsWindow.restype = wt.BOOL
    user.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]
    user.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
    user.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
    handle = kernel.OpenProcess(0x100000 | 0x1000, False, owner)
    if not handle:
        raise ValueError("Owned process handle unavailable")

    def guard():
        pid = wt.DWORD()
        title, cls, executable = ctypes.create_unicode_buffer(4096), ctypes.create_unicode_buffer(256), ctypes.create_unicode_buffer(32768)
        capacity = wt.DWORD(len(executable))
        user.GetWindowThreadProcessId(target, ctypes.byref(pid))
        user.GetWindowTextW(target, title, len(title))
        user.GetClassNameW(target, cls, len(cls))
        times = [wt.FILETIME() for _ in range(4)]
        if not (user.IsWindow(target) and pid.value == owner and kernel.WaitForSingleObject(handle, 0) == 0x102
                and spec["title"] in title.value and cls.value.startswith("Chrome_WidgetWin_")
                and kernel.GetProcessTimes(handle, *[ctypes.byref(item) for item in times])
                and kernel.QueryFullProcessImageNameW(handle, 0, executable, ctypes.byref(capacity))):
            raise ValueError("Owned fixture identity unavailable or changed")
        actual_ticks = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        if actual_ticks != expected_ticks or os.path.normcase(os.path.abspath(executable.value)) != expected_exe:
            raise ValueError("Owned fixture birth or executable changed")

    reads = []
    class BoundAutomation(uia.Automation):
        def tree(self, hwnd, max_nodes=4000):
            if hwnd != target:
                raise ValueError("A different window is outside this diagnostic")
            guard()
            started = time.monotonic()
            tree = super().tree(hwnd, max_nodes=max_nodes)
            guard()
            nodes = list(tree.walk())
            docs = [node for node in nodes if node.control_type == "Document"]
            if any(spec["title"] not in node.name for node in docs):
                raise ValueError("A document outside the exact fixture was encountered")
            reads.append({"nodes": len(nodes), "documents": len(docs),
                          "notes": sum(node.name == "Notes" for node in nodes),
                          "editor": sum(node.name == "Éditeur" for node in nodes),
                          "truncated": bool(tree.truncated), "durationMs": round((time.monotonic() - started) * 1000, 3)})
            return tree

    try:
        guard()
        # Preserve the historical tree/cache/warmup implementation. These two boundaries prevent
        # popup inspection or ScrollIntoView; no global focus, input, refresh action or capture.
        snapshot.popups_of = lambda hwnd: [] if hwnd == target else (_ for _ in ()).throw(ValueError("Foreign window refused"))
        snapshot.flush_web = lambda auto, hwnd: None
        snapshot.covered_remedy = lambda hwnd: "Strictly read-only fixture diagnostic; process arguments not inspected."
        auto = BoundAutomation()
        started = time.monotonic()
        view = snapshot.build_view(auto, target, max_nodes=6000)
        guard()
        elements = view.elements
        result = {"ok": True, "hwnd": target, "pid": owner, "birth": spec["birth"], "title": spec["title"],
                  "source": str(Path(snapshot.__file__).resolve()),
                  "sourceSha256": hashlib.sha256(Path(snapshot.__file__).read_bytes()).hexdigest(),
                  "provider": "historical Automation.tree + build_view + _wait_web_content",
                  "popupReadsSuppressed": True, "publicationActionsSuppressed": True,
                  "reads": reads, "view": {"nodes": view.node_count, "listed": len(elements),
                  "documents": sum(item.node.control_type == "Document" for item in elements),
                  "notes": sum(item.node.name == "Notes" for item in elements),
                  "editor": sum(item.node.name == "Éditeur" for item in elements),
                  "webMissing": view.web_missing, "unreadable": view.unreadable, "truncated": view.truncated},
                  "durationMs": round((time.monotonic() - started) * 1000, 3), "identityRevalidated": True, "inputSent": False}
        return result
    finally:
        kernel.CloseHandle(handle)


if __name__ == "__main__":
    try:
        if sys.argv[1:] == ["--self-test"]:
            base = birth_ticks("2026-10-30T02:40:46.9512907Z")
            rejected = False
            try:
                birth_ticks("2026-10-30T02:40:46.951Z")
            except ValueError:
                rejected = True
            print(json.dumps({"ok": True, "oneTickDifference": birth_ticks("2026-10-30T02:40:46.9512908Z") - base,
                              "rejectShortBirth": rejected, "gui": False, "importsLegacyProvider": False}))
        else:
            print(json.dumps(run(json.load(sys.stdin)), ensure_ascii=True))
    except Exception as error:
        print(json.dumps({"ok": False, "stage": "legacy-fixture-read", "errorType": type(error).__name__, "inputSent": False}))
        sys.exit(1)
