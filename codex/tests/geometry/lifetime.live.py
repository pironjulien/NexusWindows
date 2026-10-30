"""Real native launch under a KILL_ON_JOB_CLOSE job; only a headless local fixture is launched."""
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


class Basic(c.Structure):
    _fields_ = [("process_time", c.c_int64), ("job_time", c.c_int64), ("flags", w.DWORD),
                ("min_ws", c.c_size_t), ("max_ws", c.c_size_t), ("active", w.DWORD),
                ("affinity", c.c_size_t), ("priority", w.DWORD), ("scheduling", w.DWORD)]


class IO(c.Structure):
    _fields_ = [(name, c.c_uint64) for name in ("ro", "wo", "oo", "rb", "wb", "ob")]


class Extended(c.Structure):
    _fields_ = [("basic", Basic), ("io", IO), ("process_memory", c.c_size_t),
                ("job_memory", c.c_size_t), ("peak_process", c.c_size_t), ("peak_job", c.c_size_t)]


def main():
    api = c.WinDLL("kernel32", use_last_error=True)
    signatures = {
        "CreateJobObjectW": ([c.c_void_p, w.LPCWSTR], w.HANDLE),
        "SetInformationJobObject": ([w.HANDLE, c.c_int, c.c_void_p, w.DWORD], w.BOOL),
        "AssignProcessToJobObject": ([w.HANDLE, w.HANDLE], w.BOOL),
        "OpenProcess": ([w.DWORD, w.BOOL, w.DWORD], w.HANDLE),
        "IsProcessInJob": ([w.HANDLE, w.HANDLE, c.POINTER(w.BOOL)], w.BOOL),
        "WaitForSingleObject": ([w.HANDLE, w.DWORD], w.DWORD),
        "TerminateProcess": ([w.HANDLE, w.UINT], w.BOOL),
        "CloseHandle": ([w.HANDLE], w.BOOL),
    }
    for name, (args, result) in signatures.items():
        fn = getattr(api, name); fn.argtypes = args; fn.restype = result
    helper = Path(os.environ["NEXUS_NATIVE_TEST_EXE"])
    fixture = Path(os.environ["NEXUS_GEOMETRY_FIXTURE"])
    policy = Path(__file__).resolve().parents[3] / "commun" / "garde-fous.json"
    job = api.CreateJobObjectW(None, None)
    assert job, c.get_last_error()
    info = Extended(); info.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    assert api.SetInformationJobObject(job, 9, c.byref(info), c.sizeof(info)), c.get_last_error()
    worker = None; child = None
    try:
        with tempfile.TemporaryDirectory(prefix="nexus-lifetime-") as directory:
            oracle = Path(directory) / "oracle.json"
            no_shell = subprocess.run([str(fixture), "--no-shell-probe", str(oracle)], capture_output=True,
                                      text=True, encoding="utf-8", timeout=10, creationflags=0x08000000, check=True)
            refusal = json.loads(no_shell.stdout)
            assert refusal == {"refused": True, "started": False}, refusal
            assert not oracle.exists(), "An unavailable shell must not fall back to a child inherited from the MCP helper"
            worker = subprocess.Popen([str(helper), "--server"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                      stderr=subprocess.PIPE, text=True, encoding="utf-8", creationflags=0x08000000)
            assert api.AssignProcessToJobObject(job, int(worker._handle)), c.get_last_error()
            request = {"requestId": "0fe52677-a2c4-49ac-9cb7-cd1f8ba16247", "operation": "launch", "windowId": 0,
                       "app": "", "target": str(fixture), "args": subprocess.list2cmdline(["--lifetime-child", str(oracle)]),
                       "policyFile": str(policy)}
            worker.stdin.write(json.dumps(request) + "\n"); worker.stdin.flush()
            for _ in range(100):
                if oracle.exists():
                    break
                if worker.poll() is not None:
                    raise AssertionError(worker.stdout.read() + worker.stderr.read())
                time.sleep(0.1)
            assert oracle.exists(), "The local application did not start"
            data = json.loads(oracle.read_text())
            assert Path(data["executable"]).resolve() == fixture.resolve(), "Only our own fixture may be inspected/killed"
            child = api.OpenProcess(0x100000 | 0x1000 | 1, False, data["pid"])
            assert child, c.get_last_error()
            inside = w.BOOL()
            assert api.IsProcessInJob(child, job, c.byref(inside)), c.get_last_error()
            assert api.WaitForSingleObject(child, 0) == 258, "Fixture must be alive before the helper job is closed"
            api.CloseHandle(job); job = None
            worker.wait(timeout=5)
            survived = api.WaitForSingleObject(child, 2000) == 258
            print(json.dumps({"fixturePid": data["pid"], "inHelperJob": bool(inside.value), "survivedJobClose": survived,
                              "missingBrokerRefusedBeforeStart": refusal["refused"] and not refusal["started"]}))
            assert survived, "A user application must outlive the MCP helper's KILL_ON_JOB_CLOSE job"
    finally:
        if child:
            if api.WaitForSingleObject(child, 0) == 258:
                api.TerminateProcess(child, 0)
            api.CloseHandle(child)
        if job:
            api.CloseHandle(job)
        if worker and worker.poll() is None:
            worker.kill(); worker.wait(timeout=5)


if __name__ == "__main__":
    main()
