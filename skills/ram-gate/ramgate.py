"""Run RAM-heavy work only while the PC has memory to spare.

For a PC someone is also using: local AI models (image, voice, music, video),
renders and other heavy jobs wait until RAM is free, pause when the person
opens a browser or app, and resume by themselves when it is closed again.

    python ramgate.py free                                  # print free RAM in GB
    python ramgate.py wait --need 4.5                       # block until RAM is free
    python ramgate.py run --need 4.5 --label painter -- python paint.py ...
    python ramgate.py run --need 4 --kill-below 1.5 -- some-tool --args
    python ramgate.py run --need 6.5 --max-desktop-gpu 4 -- python big_gpu_job.py

`run` waits until free RAM has stayed at or above --need for --steady seconds,
then starts the command. When the command stops because RAM got low, it waits
and starts it again. The command must skip work it already finished, so a
restart resumes. A command "stops because RAM got low" when it either:

* exits with code 75 (PAUSED): the cooperative way. A worker checks
  `ramgate.low(gb)` between items (one picture, one paragraph), saves, and
  exits 75; or
* fails while free RAM was at or under --low-gb; or
* is killed by ramgate itself because free RAM fell under --kill-below. This is
  the last resort for tools that don't watch memory; the item in progress is
  lost.

A one-line status file shows what the gate is doing right now, for example
"16:40:05 painter: ON HOLD, 3.2 of 4.5 GB RAM free; starts by itself when RAM
is free" or "... RUNNING since 16:26, 4.9 GB free, 3 done". It is
~/ramgate-status.txt unless --status or the RAMGATE_STATUS variable says
otherwise.

Two low-RAM stops in a row that finish nothing count as a real failure (the job
needs more memory than the machine has free), and ramgate exits non-zero.
Progress is measured with --progress-glob (count of files matching it).

As a library:  import ramgate;  ramgate.wait_for_ram(4.5);  ramgate.run([...], 4.5)
               if ramgate.low(2.8): sys.exit(ramgate.PAUSED)   # inside a worker loop

Standard library only. Windows (GlobalMemoryStatusEx), Linux (/proc/meminfo),
and macOS (vm_stat).
"""

from __future__ import annotations

import argparse
import glob
import logging
import os
import re
import signal
import subprocess
import sys
import threading
import time
from typing import Callable, Optional, Sequence

log = logging.getLogger("ramgate")

PAUSED = 75  # exit code: the worker stopped cleanly because free RAM got low

STATUS_PATH = os.environ.get("RAMGATE_STATUS") or os.path.join(os.path.expanduser("~"),
                                                               "ramgate-status.txt")


def status(text: str) -> None:
    """Replace the one-line status file; never let it break the job."""
    try:
        tmp = STATUS_PATH + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(f"{time.strftime('%H:%M:%S')} {text}\n")
        os.replace(tmp, STATUS_PATH)
    except OSError:
        pass


def free_gb() -> float:
    """Available physical memory in GB (memory that can be used without paging)."""
    if sys.platform == "win32":
        import ctypes

        class Status(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong),
                        ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong),
                        ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

        status = Status(dwLength=ctypes.sizeof(Status))
        ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
        return status.ullAvailPhys / 2**30
    if os.path.exists("/proc/meminfo"):
        with open("/proc/meminfo") as f:
            for line in f:
                if line.startswith("MemAvailable:"):
                    return int(line.split()[1]) / 2**20
    out = subprocess.run(["vm_stat"], capture_output=True, text=True).stdout
    page = int(re.search(r"page size of (\d+)", out).group(1))
    pages = sum(int(re.search(rf"{key}:\s+(\d+)", out).group(1))
                for key in ("Pages free", "Pages inactive", "Pages speculative"))
    return pages * page / 2**30


def low(below_gb: float) -> bool:
    """For workers, between items: True when they should save and exit PAUSED."""
    return free_gb() < below_gb


def desktop_gpu_gb() -> Optional[float]:
    """Graphics memory held by the Windows desktop compositor (dwm), in GB.

    On a GPU that also drives the screen, dwm's share can grow (seen after GPU
    jobs were force-killed) and only a sign-out or restart gives it back. While
    it is high, a big GPU job doesn't fit and Windows spills it into system RAM.
    None when unknown or not on Windows. Takes about 2 s.
    """
    if sys.platform != "win32":
        return None
    try:
        row = subprocess.run(["tasklist", "/FI", "IMAGENAME eq dwm.exe", "/FO", "CSV", "/NH"],
                             capture_output=True, text=True, timeout=20).stdout
        pid = int(row.split('","')[1])
        out = subprocess.run(["typeperf", rf"\GPU Process Memory(pid_{pid}_*)\Local Usage",
                              "-sc", "1"], capture_output=True, text=True, timeout=30).stdout
        data = [line for line in out.splitlines() if re.match(r'"\d', line)][-1]
        return sum(float(v.strip('"')) for v in data.split(",")[1:] if v.strip('"')) / 2**30
    except (OSError, ValueError, IndexError, subprocess.SubprocessError):
        return None


def wait_for_ram(need_gb: float, label: str = "job", steady_s: float = 60,
                 poll_s: float = 5, max_desktop_gpu_gb: Optional[float] = None) -> None:
    """Block until free RAM has stayed at or above need_gb for steady_s seconds,
    so a browser closed for a moment doesn't start the job.

    max_desktop_gpu_gb: for big GPU jobs, also hold while dwm holds more graphics
    memory than this (checked once a minute), since the job would spill into RAM.
    """
    since: Optional[float] = None
    held = False
    gpu_checked, desktop_gpu = 0.0, None
    while True:
        free = free_gb()
        if max_desktop_gpu_gb is not None and time.time() - gpu_checked >= 60:
            gpu_checked, desktop_gpu = time.time(), desktop_gpu_gb()
        if desktop_gpu is not None and desktop_gpu > max_desktop_gpu_gb:
            since = None
            status(f"{label}: ON HOLD, the desktop holds {desktop_gpu:.1f} GB of graphics memory "
                   f"(the job fits under {max_desktop_gpu_gb:.1f}); a sign-out or PC restart "
                   f"frees it")
            if not held:
                log.info("%s: on hold, the desktop (dwm) holds %.1f GB of graphics memory and "
                         "the job fits only under %.1f GB; a sign-out or restart frees it",
                         label, desktop_gpu, max_desktop_gpu_gb)
                held = True
        elif free >= need_gb:
            since = since or time.time()
            left = steady_s - (time.time() - since)
            if left <= 0:
                if held:
                    log.info("%s: RAM is free again (%.1f GB), starting", label, free)
                status(f"{label}: STARTING, {free:.1f} GB RAM free")
                return
            status(f"{label}: RAM is free ({free:.1f} GB), starting in {left:.0f} s "
                   f"if it stays free")
        else:
            since = None
            status(f"{label}: ON HOLD, {free:.1f} of {need_gb:.1f} GB RAM free; "
                   f"starts by itself when RAM is free")
            if not held:
                log.info("%s: on hold, %.1f GB RAM free and it needs %.1f GB; it starts by "
                         "itself once other apps free some memory", label, free, need_gb)
                held = True
        time.sleep(poll_s)


def _kill_tree(proc: subprocess.Popen) -> None:
    if sys.platform == "win32":
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


def run(cmd: Sequence[str], need_gb: float, label: str = "job", steady_s: float = 60,
        low_gb: Optional[float] = None, kill_below_gb: Optional[float] = None,
        progress: Optional[Callable[[], int]] = None,
        max_desktop_gpu_gb: Optional[float] = None) -> None:
    """Run cmd when RAM allows; after a low-RAM stop, wait for RAM and run it again.

    low_gb: a failure while free RAM fell to or under this counts as caused by
    other apps (default: 55% of need_gb). kill_below_gb: kill the command (and
    its children) if free RAM falls under this. progress: returns a count of
    finished items, so a restart loop that gets nowhere is noticed.
    max_desktop_gpu_gb: see wait_for_ram.
    """
    low_gb = need_gb * 0.55 if low_gb is None else low_gb
    stalls = 0
    while True:
        wait_for_ram(need_gb, label, steady_s, max_desktop_gpu_gb=max_desktop_gpu_gb)
        before = progress() if progress else 0
        lowest = [free_gb()]
        killed = threading.Event()
        done = threading.Event()
        proc = subprocess.Popen(list(cmd), stdin=subprocess.DEVNULL,
                                start_new_session=sys.platform != "win32")

        started = time.strftime("%H:%M")
        last_status = [0.0]

        def watch() -> None:
            while not done.wait(0.5):
                free = free_gb()
                lowest[0] = min(lowest[0], free)
                if time.time() - last_status[0] >= 5:
                    last_status[0] = time.time()
                    count = f", {progress()} done" if progress else ""
                    status(f"{label}: RUNNING since {started}, {free:.1f} GB RAM free{count}")
                if kill_below_gb is not None and free < kill_below_gb and not killed.is_set():
                    log.warning("%s: %.1f GB RAM free, under %.1f GB; stopping it to keep the "
                                "PC responsive", label, free, kill_below_gb)
                    killed.set()
                    _kill_tree(proc)

        watcher = threading.Thread(target=watch, daemon=True)
        watcher.start()
        code = proc.wait()
        done.set()
        watcher.join()
        if code == 0 and not killed.is_set():
            # the lowest point is what --need must cover (plus headroom)
            log.info("%s: done; lowest free RAM while it ran was %.1f GB", label, lowest[0])
            status(f"{label}: DONE (lowest free RAM {lowest[0]:.1f} GB)")
            return
        ram_caused = code == PAUSED or killed.is_set() or lowest[0] <= low_gb
        if not ram_caused:
            status(f"{label}: FAILED (exit {code}); see its log")
            raise RuntimeError(f"{label} failed (exit {code}); lowest free RAM was "
                               f"{lowest[0]:.1f} GB, so not a memory pause")
        made = (progress() if progress else 1) - before
        stalls = 0 if made > 0 else stalls + 1
        if stalls >= 2:
            status(f"{label}: FAILED, it keeps running out of RAM without finishing anything")
            raise RuntimeError(f"{label} stopped twice for low RAM without finishing anything; "
                               "it may need more memory than this machine has free")
        log.info("%s: paused for low RAM (lowest %.1f GB free); waiting to resume", label,
                 lowest[0])
        status(f"{label}: PAUSED for low RAM; resumes by itself when RAM is free")


def main() -> int:
    global STATUS_PATH
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("free", help="print free RAM in GB")
    sub.add_parser("desktop-gpu", help="print the desktop's (dwm's) graphics memory in GB")
    w = sub.add_parser("wait", help="block until RAM is free")
    w.add_argument("--need", type=float, required=True, help="GB of free RAM required")
    w.add_argument("--steady", type=float, default=60, help="seconds it must stay free")
    w.add_argument("--label", default="job")
    r = sub.add_parser("run", help="run a command when RAM is free; pause and resume it")
    r.add_argument("--need", type=float, required=True, help="GB of free RAM to start")
    r.add_argument("--steady", type=float, default=60, help="seconds it must stay free")
    r.add_argument("--label", default="job")
    r.add_argument("--low-gb", type=float, help="failures at or under this much free RAM "
                                                "count as memory pauses (default 55%% of --need)")
    r.add_argument("--kill-below", type=float, help="kill the command if free RAM falls under this")
    r.add_argument("--progress-glob", help="files counted as finished items, e.g. 'out/*.png'")
    r.add_argument("command", nargs=argparse.REMAINDER)
    for parser in (w, r):
        parser.add_argument("--status", help=f"one-line status file (default {STATUS_PATH})")
        parser.add_argument("--max-desktop-gpu", type=float,
                            help="also hold while the desktop (dwm) holds more graphics memory "
                                 "than this, in GB (Windows; for big GPU jobs)")
    args = ap.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(asctime)s ramgate: %(message)s",
                        datefmt="%H:%M:%S")
    if getattr(args, "status", None):
        STATUS_PATH = args.status
    if args.cmd == "free":
        print(f"{free_gb():.1f}")
        return 0
    if args.cmd == "desktop-gpu":
        gpu = desktop_gpu_gb()
        print("unknown" if gpu is None else f"{gpu:.1f}")
        return 0
    if args.cmd == "wait":
        wait_for_ram(args.need, args.label, args.steady, max_desktop_gpu_gb=args.max_desktop_gpu)
        return 0
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        ap.error("run needs a command after --")
    progress = (lambda: len(glob.glob(args.progress_glob))) if args.progress_glob else None
    try:
        run(command, args.need, args.label, args.steady, args.low_gb, args.kill_below, progress,
            args.max_desktop_gpu)
    except RuntimeError as err:
        log.error("%s", err)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
