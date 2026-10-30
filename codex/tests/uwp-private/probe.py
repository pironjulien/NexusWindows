"""Independent package/process observer and explicit controller for this unique private UWP fixture only."""
from __future__ import annotations

import argparse
import ctypes as c
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid
from ctypes import wintypes as w
import xml.etree.ElementTree as ET

NAME = re.compile(r"^Nexus\.LocalUwpQA\.[0-9a-f]{12}$")
ROOT = Path.home() / ".nexus/nexus-windows/qualification/uwp-private"
NS = "{http://schemas.microsoft.com/appx/manifest/foundation/windows10}"


def load_plan(path: Path) -> dict:
    plan = json.loads(path.read_text(encoding="utf-8-sig"))
    if plan.get("schema") != 1 or not NAME.fullmatch(plan.get("name", "")) or plan.get("publisher") != "CN=NexusPrivateQualification":
        raise ValueError("Not an owned private UWP qualification plan")
    expected = (ROOT / plan["name"] / "layout").resolve()
    if Path(plan["layout"]).resolve() != expected or not expected.is_relative_to(ROOT.resolve()):
        raise ValueError("Fixture layout is outside its exact private qualification directory")
    for field, filename, digest in [("executable", "NexusUwpFixture.exe", "exe_sha256"), ("manifest", "AppxManifest.xml", "manifest_sha256")]:
        file = Path(plan[field])
        if file.resolve() != expected / filename or hashlib.sha256(file.read_bytes()).hexdigest().upper() != plan[digest].upper():
            raise ValueError("Fixture executable/manifest path or hash changed")
    manifest = ET.parse(plan["manifest"]).getroot()
    identity, app = manifest.find(NS + "Identity"), manifest.find(NS + "Applications/" + NS + "Application")
    if identity is None or app is None or identity.get("Name") != plan["name"] or identity.get("Publisher") != plan["publisher"]:
        raise ValueError("Fixture manifest identity differs from its plan")
    if app.get("Executable") != "NexusUwpFixture.exe" or app.get("EntryPoint") != "NexusPrivateQualification.App":
        raise ValueError("Fixture is not the expected UWP entry point")
    if manifest.find(NS + "Capabilities") is not None or "Windows.FullTrustApplication" in Path(plan["manifest"]).read_text(encoding="utf-8-sig"):
        raise ValueError("Fixture acquired a capability or full-trust entry point")
    return plan


def registered(plan: dict) -> dict | None:
    script = "$ErrorActionPreference='Stop'; $p=Get-AppxPackage -Name $env:NEXUS_PRIVATE_UWP_NAME; if($p){$p|Select-Object Name,PackageFullName,PackageFamilyName,InstallLocation,IsDevelopmentMode|ConvertTo-Json -Compress}"
    result = subprocess.run(["pwsh.exe", "-NoProfile", "-NonInteractive", "-Command", script],
        env={**os.environ, "NEXUS_PRIVATE_UWP_NAME": plan["name"]}, capture_output=True, encoding="utf-8", check=True,
        creationflags=subprocess.CREATE_NO_WINDOW, timeout=15)
    if not result.stdout.strip():
        return None
    package = json.loads(result.stdout)
    if not isinstance(package, dict) or package["Name"] != plan["name"] or not package["IsDevelopmentMode"] or Path(package["InstallLocation"]).resolve() != Path(plan["layout"]).resolve():
        raise ValueError("Registered package is not this exact private development layout")
    return package


class GUID(c.Structure):
    _fields_ = [("Data1", w.DWORD), ("Data2", w.WORD), ("Data3", w.WORD), ("Data4", c.c_ubyte * 8)]

    @classmethod
    def parse(cls, value: str):
        return cls.from_buffer_copy(uuid.UUID(value).bytes_le)


class Observer:
    def __init__(self):
        self.kernel = c.WinDLL("kernel32", use_last_error=True)
        self.advapi = c.WinDLL("advapi32", use_last_error=True)
        self.psapi = c.WinDLL("psapi", use_last_error=True)
        self.ole = c.OleDLL("ole32")
        self.user = c.WinDLL("user32", use_last_error=True)
        self.dwm = c.WinDLL("dwmapi")
        self.kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]; self.kernel.OpenProcess.restype = w.HANDLE
        self.kernel.CloseHandle.argtypes = [w.HANDLE]
        self.kernel.GetPackageFullName.argtypes = [w.HANDLE, c.POINTER(c.c_uint), w.LPWSTR]; self.kernel.GetPackageFullName.restype = c.c_long
        self.kernel.QueryFullProcessImageNameW.argtypes = [w.HANDLE, w.DWORD, w.LPWSTR, c.POINTER(w.DWORD)]; self.kernel.QueryFullProcessImageNameW.restype = w.BOOL
        self.kernel.GetProcessTimes.argtypes = [w.HANDLE, c.POINTER(w.FILETIME), c.POINTER(w.FILETIME), c.POINTER(w.FILETIME), c.POINTER(w.FILETIME)]; self.kernel.GetProcessTimes.restype = w.BOOL
        self.psapi.EnumProcesses.argtypes = [c.POINTER(w.DWORD), w.DWORD, c.POINTER(w.DWORD)]; self.psapi.EnumProcesses.restype = w.BOOL
        self.advapi.OpenProcessToken.argtypes = [w.HANDLE, w.DWORD, c.POINTER(w.HANDLE)]; self.advapi.OpenProcessToken.restype = w.BOOL
        self.advapi.GetTokenInformation.argtypes = [w.HANDLE, c.c_int, c.c_void_p, w.DWORD, c.POINTER(w.DWORD)]; self.advapi.GetTokenInformation.restype = w.BOOL
        self.ole.CoInitializeEx.argtypes = [c.c_void_p, w.DWORD]; self.ole.CoInitializeEx.restype = c.c_long
        self.initialized = self.ole.CoInitializeEx(None, 0)
        self.ole.CoCreateInstance.argtypes = [c.POINTER(GUID), c.c_void_p, w.DWORD, c.POINTER(GUID), c.POINTER(c.c_void_p)]; self.ole.CoCreateInstance.restype = c.c_long
        self.pointer = c.c_void_p()
        cls, iid = GUID.parse("B1AEC16F-2383-4852-B0E9-8F0B1DC66B4D"), GUID.parse("F27C3930-8029-4AD1-94E3-3DBA417810C1")
        hr = self.ole.CoCreateInstance(c.byref(cls), None, 5, c.byref(iid), c.byref(self.pointer))
        if hr < 0:
            raise OSError(f"Package state observer unavailable: {hr & 0xffffffff:08x}")
        self.table = c.cast(self.pointer, c.POINTER(c.POINTER(c.c_void_p))).contents
        self.get_state = c.WINFUNCTYPE(c.c_long, c.c_void_p, c.c_wchar_p, c.POINTER(c.c_int))(self.table[15])

    def windows(self, processes: list[dict]) -> list[dict]:
        owned = {item["pid"] for item in processes}
        callback = c.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
        self.user.EnumWindows.argtypes = [callback, w.LPARAM]
        self.user.EnumChildWindows.argtypes = [w.HWND, callback, w.LPARAM]
        self.user.GetWindowThreadProcessId.argtypes = [w.HWND, c.POINTER(w.DWORD)]
        self.user.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, c.c_int]
        self.user.IsWindowVisible.argtypes = [w.HWND]
        self.dwm.DwmGetWindowAttribute.argtypes = [w.HWND, w.DWORD, c.c_void_p, w.DWORD]
        result = []
        def metadata(hwnd):
            pid = w.DWORD(); name = c.create_unicode_buffer(512); cloaked = w.DWORD()
            self.user.GetWindowThreadProcessId(hwnd, c.byref(pid))
            self.user.GetClassNameW(hwnd, name, len(name))
            hr = self.dwm.DwmGetWindowAttribute(hwnd, 14, c.byref(cloaked), c.sizeof(cloaked))
            return {"hwnd": int(hwnd), "pid": pid.value, "class": name.value,
                "visible": bool(self.user.IsWindowVisible(hwnd)), "cloaked": bool(cloaked.value) if hr >= 0 else None}
        def visit(hwnd, _):
            item = metadata(hwnd)
            if item["pid"] in owned:
                result.append(item)
            elif item["class"] == "ApplicationFrameWindow":
                children = []
                def child_visit(child, _unused):
                    child_item = metadata(child)
                    if child_item["pid"] in owned:
                        children.append(child_item)
                    return True
                self.user.EnumChildWindows(hwnd, callback(child_visit), 0)
                if children:
                    result.append({**item, "owned_children": children})
            return True
        self.user.EnumWindows(callback(visit), 0)
        return result

    def close(self):
        if self.pointer:
            c.WINFUNCTYPE(w.ULONG, c.c_void_p)(self.table[2])(self.pointer)
            self.pointer = None
        if self.initialized in (0, 1):
            self.ole.CoUninitialize()

    def state(self, package: str) -> int:
        value = c.c_int(-1)
        hr = self.get_state(self.pointer, package, c.byref(value))
        if hr < 0:
            raise OSError(f"GetPackageExecutionState failed: {hr & 0xffffffff:08x}")
        return value.value

    def processes(self, package: str) -> list[dict]:
        ids, size = (w.DWORD * 16384)(), w.DWORD()
        if not self.psapi.EnumProcesses(ids, c.sizeof(ids), c.byref(size)) or size.value == c.sizeof(ids):
            raise OSError("Complete process ID inventory unavailable")
        result = []
        for pid in ids[:size.value // c.sizeof(w.DWORD)]:
            handle = self.kernel.OpenProcess(0x1000, False, pid)
            if not handle:
                continue
            try:
                length = c.c_uint()
                if self.kernel.GetPackageFullName(handle, c.byref(length), None) != 122:
                    continue
                full_name = c.create_unicode_buffer(length.value)
                if self.kernel.GetPackageFullName(handle, c.byref(length), full_name) != 0 or full_name.value != package:
                    continue
                image, image_size = c.create_unicode_buffer(32768), w.DWORD(32768)
                born, exited, kernel, user = w.FILETIME(), w.FILETIME(), w.FILETIME(), w.FILETIME()
                if not self.kernel.QueryFullProcessImageNameW(handle, 0, image, c.byref(image_size)) or not self.kernel.GetProcessTimes(handle, c.byref(born), c.byref(exited), c.byref(kernel), c.byref(user)):
                    raise OSError("Owned package process identity unavailable")
                ticks = (born.dwHighDateTime << 32) | born.dwLowDateTime
                moment = dt.datetime(1601, 1, 1, tzinfo=dt.timezone.utc) + dt.timedelta(microseconds=ticks // 10)
                token, app_container, returned = w.HANDLE(), w.DWORD(), w.DWORD()
                if not self.advapi.OpenProcessToken(handle, 8, c.byref(token)):
                    raise OSError("Owned package token unavailable")
                try:
                    if not self.advapi.GetTokenInformation(token, 29, c.byref(app_container), c.sizeof(app_container), c.byref(returned)):
                        raise OSError("TokenIsAppContainer unavailable")
                finally:
                    self.kernel.CloseHandle(token)
                result.append({"pid": pid, "image": image.value, "creation_filetime": str(ticks),
                    "creation_utc": moment.strftime("%Y-%m-%dT%H:%M:%S.") + f"{ticks % 10_000_000:07d}Z",
                    "is_app_container": bool(app_container.value)})
            finally:
                self.kernel.CloseHandle(handle)
        return sorted(result, key=lambda item: item["pid"])

    def control(self, package: str, action: str):
        # These state-changing slots are bound only after --apply and the owned identity checks below.
        slot = {"suspend": 5, "resume": 6}[action]
        method = c.WINFUNCTYPE(c.c_long, c.c_void_p, c.c_wchar_p)(self.table[slot])
        hr = method(self.pointer, package)
        if hr < 0:
            raise OSError(f"Public {action} failed: {hr & 0xffffffff:08x}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--action", choices=["status", "suspend", "resume"], default="status")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--expect-pid", type=int)
    parser.add_argument("--expect-birth")
    args = parser.parse_args()
    plan = load_plan(args.plan)
    package = registered(plan)
    if package is None:
        if args.action != "status":
            raise ValueError("Owned fixture is not registered")
        print(json.dumps({"registered": False, "name": plan["name"], "state": None, "processes": [], "read_only": True})); return
    observer = Observer()
    try:
        full_name = package["PackageFullName"]
        state, processes = observer.state(full_name), observer.processes(full_name)
        result = {"registered": True, "package": package, "state": state, "processes": processes, "windows": observer.windows(processes), "read_only": args.action == "status", "control_applied": False}
        if args.action != "status":
            if not args.apply or args.expect_pid is None or not args.expect_birth:
                raise ValueError("Explicit --apply and an exact frozen PID/birth are required")
            def validate():
                load_plan(args.plan)
                actual = observer.processes(full_name)
                main_process = [item for item in actual if Path(item["image"]).resolve() == Path(plan["executable"]).resolve()]
                if len(main_process) != 1 or main_process[0]["pid"] != args.expect_pid or main_process[0]["creation_filetime"] != args.expect_birth or not main_process[0]["is_app_container"]:
                    raise ValueError("Owned AppContainer PID/birth changed; no package control is permitted")
            validate()
            if args.action == "suspend" and state != 1 or args.action == "resume" and state not in (2, 3):
                raise ValueError("Observed package state does not allow the requested control")
            observer.control(full_name, args.action)  # Once only: lost or failed calls are never retried.
            result["control_applied"] = True
            target = 3 if args.action == "suspend" else 1
            deadline, seen = time.monotonic() + 8, []
            while True:
                validate(); current = observer.state(full_name); seen.append(current)
                if current == target or time.monotonic() >= deadline:
                    break
                time.sleep(0.1618)
            result.update(state_after=current, states_observed=seen, state_verified=current == target, processes_after=observer.processes(full_name), controller_origin="private-fixture-control-not-Nexus-engine")
        print(json.dumps(result, ensure_ascii=False))
    finally:
        observer.close()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"error": type(error).__name__, "message": str(error), "no_automatic_retry": True}))
        sys.exit(2)
