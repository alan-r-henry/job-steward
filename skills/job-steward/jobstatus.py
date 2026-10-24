"""Show a long-running job in the JobTray taskbar indicator.

Each job is one small JSON file in the status folder (~/.job-status, or the
folder in JOB_STATUS_DIR). Any program, script or chat session can write one;
the tray (tray/JobTray.exe) shows every file as a row with a progress bar,
notices when a job finishes, fails, or its process disappears, and notifies.
This module and its command line write those files safely: atomic replace, and
an error here never breaks the job.

In Python:
    import jobstatus
    job = jobstatus.Job("moon-pictures", "Moon video: painting", total=75, unit="pictures",
                        log=r"D:\\jobs\\moon\\make.log")
    job.update(done=26, message="painting the lift-off")
    job.waiting("on hold until 6.5 GB of RAM is free")
    job.finish("75 pictures painted", output=r"D:\\jobs\\moon\\video.mp4")   # or job.fail("...")

From any shell:
    python jobstatus.py run moon-mix --title "Moon video: music mix" -- python main.py mix ...
    python jobstatus.py set moon --title "Moon video" --state running --done 26 --total 75
    python jobstatus.py set moon --pid 11024 --tail ~/ramgate-status.txt \\
        --count "D:/jobs/moon/images/*.png" --total 75 --success "mix end .* exit 0"
    python jobstatus.py done moon --message "finished"      # also: fail, clear, list

Fields (all optional except id):
    title, project, session   what it is and who started it
    state       starting | running | queued | waiting | low_ram | done | failed
                (queued: a later step, shown as "Up next"; low_ram: held until
                the PC has RAM to spare)
    done, total, unit         progress as a count ("26 of 75 pictures")
    progress    0..1, when there is no count
    message     the current step, in plain words
    eta_seconds time left, if the job knows better than the tray's estimate
    pid, pid_started          the process that lives for the whole job; if it
                              disappears before "done", the tray says so
    log, output               files the tray offers to open
    tail        a file whose last line the tray shows as the message (a log,
                ramgate's status file); "on hold" / "paused" in it reads as waiting
    count       a file pattern (one folder) the tray counts as `done`
    success     a regex: if the pid is gone and the log's end matches, it finished
    parent      the id of the main job this is a step (sub-job) of; the tray
                nests it inside that job's card
    retry, retry_cwd          a command line (and folder) that runs the job
                again: a failed row gets a Retry button. `run` records its own
                invocation, so wrapped jobs can always be retried.
    started, updated          epoch seconds (written for you)

Main jobs and sub-jobs: a main job is the whole piece of work the user asked
for ("make the moon video"); a sub-job is one step inside it that reports its
own progress ("painting: 26 of 75 pictures"). `run` sets JOB_STATUS_PARENT for
its command, so any job reported from inside it (ramgate's, another `run`, a
Job()) becomes its sub-job without being told.

Standard library only.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
from typing import Any, Optional

STATES = ("starting", "running", "queued", "waiting", "low_ram", "done", "failed")


def folder() -> str:
    return os.environ.get("JOB_STATUS_DIR") or os.path.join(os.path.expanduser("~"), ".job-status")


def _slug(job_id: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "-", str(job_id)).strip("-.").lower() or "job"


def _path(job_id: str) -> str:
    return os.path.join(folder(), _slug(job_id) + ".json")


def _read(path: str) -> Optional[dict[str, Any]]:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def _write(path: str, data: dict[str, Any]) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = f"{path}.{os.getpid()}.tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    # the tray may be reading the old file at this instant; Windows then refuses the replace
    for attempt in range(20):
        try:
            os.replace(tmp, path)
            return
        except PermissionError:
            time.sleep(0.05)
    os.remove(tmp)


def process_started(pid: int) -> Optional[float]:
    """When a process started (epoch seconds), so a reused pid isn't taken for the job."""
    if sys.platform != "win32":
        try:
            ticks = int(open(f"/proc/{pid}/stat").read().rsplit(")", 1)[1].split()[19])
            boot = next(float(line.split()[1]) for line in open("/proc/stat")
                        if line.startswith("btime"))
            return boot + ticks / os.sysconf("SC_CLK_TCK")
        except (OSError, ValueError, IndexError, StopIteration):
            return None
    import ctypes
    from ctypes import wintypes
    k32 = ctypes.windll.kernel32
    k32.OpenProcess.restype = wintypes.HANDLE
    handle = k32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        times = [wintypes.FILETIME() for _ in range(4)]
        if not k32.GetProcessTimes(handle, *[ctypes.byref(t) for t in times]):
            return None
        ft = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        return (ft - 116444736000000000) / 1e7
    finally:
        k32.CloseHandle(handle)


def report(job_id: str, **fields: Any) -> None:
    """Create or update a job; fields left out (or None) keep their old values.

    Never raises: a status display must not be able to break the work it shows.
    """
    try:
        path = _path(job_id)
        old = _read(path)
        is_new = old is None
        data = old or {"id": _slug(job_id), "started": time.time()}
        # inside a `run` wrapper, a new job is a step of the wrapper's job
        parent = os.environ.get("JOB_STATUS_PARENT")
        if is_new and parent and _slug(parent) != _slug(job_id) and "parent" not in fields:
            data["parent"] = _slug(parent)
        for key, value in fields.items():
            if value is not None:
                data[key] = value
        if fields.get("pid") and "pid_started" not in fields:
            data["pid_started"] = process_started(int(fields["pid"]))
            if is_new and data["pid_started"]:
                data["started"] = data["pid_started"]   # attached to a job already under way
        data["updated"] = time.time()
        _write(path, data)
    except Exception as err:  # noqa: BLE001 - never break the job
        print(f"jobstatus: could not report {job_id}: {err}", file=sys.stderr)


def clear(job_id: str) -> None:
    try:
        os.remove(_path(job_id))
    except OSError:
        pass


def jobs() -> list[dict[str, Any]]:
    try:
        names = sorted(n for n in os.listdir(folder()) if n.endswith(".json"))
    except OSError:
        return []
    return [d for d in (_read(os.path.join(folder(), n)) for n in names) if d]


class Job:
    """One job in the tray. `pid` defaults to this process: pass pid=None if this
    process doesn't live for the whole job (a worker that exits to be restarted),
    or the tray will say it stopped unexpectedly when it exits."""

    def __init__(self, job_id: str, title: Optional[str] = None, *, pid: Optional[int] = -1,
                 **fields: Any) -> None:
        self.id = job_id
        fields.setdefault("state", "running")
        pid = os.getpid() if pid == -1 else pid
        report(job_id, title=title, pid=pid, started=time.time(),
               project=fields.pop("project", None) or os.path.basename(os.getcwd()), **fields)

    def update(self, **fields: Any) -> None:
        report(self.id, **fields)

    def running(self, message: Optional[str] = None, **fields: Any) -> None:
        report(self.id, state="running", message=message, **fields)

    def waiting(self, message: Optional[str] = None, **fields: Any) -> None:
        report(self.id, state="waiting", message=message, **fields)

    def queued(self, message: Optional[str] = None, **fields: Any) -> None:
        """A later step that hasn't started (shown as "Up next")."""
        report(self.id, state="queued", message=message, **fields)

    def low_ram(self, message: Optional[str] = None, **fields: Any) -> None:
        """Held until the PC has RAM to spare (shown as "Waiting for RAM")."""
        report(self.id, state="low_ram", message=message, **fields)

    def finish(self, message: Optional[str] = None, **fields: Any) -> None:
        report(self.id, state="done", message=message, **fields)

    def fail(self, message: Optional[str] = None, **fields: Any) -> None:
        report(self.id, state="failed", message=message, **fields)

    def __enter__(self) -> "Job":
        return self

    def __exit__(self, kind, err, tb) -> None:
        if err is not None:
            self.fail(f"{kind.__name__}: {err}")
        elif (_read(_path(self.id)) or {}).get("state") not in ("done", "failed"):
            self.finish()


def _windows_path(path: str) -> str:
    """Git Bash leaves /d/Dev/... unconverted when it holds a wildcard."""
    m = re.match(r"^/([a-zA-Z])(/.*)?$", path) if sys.platform == "win32" else None
    return f"{m.group(1).upper()}:{m.group(2) or '/'}" if m else path


def _fields(args: argparse.Namespace) -> dict[str, Any]:
    keys = ("title", "project", "session", "state", "done", "total", "unit", "progress", "message",
            "eta_seconds", "pid", "log", "output", "tail", "count", "success", "parent",
            "retry", "retry_cwd", "order", "need_gb")
    out = {k: getattr(args, k, None) for k in keys}
    for k in ("log", "output", "tail", "count"):
        if out[k]:
            out[k] = os.path.abspath(os.path.expanduser(_windows_path(out[k])))
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    parsers = {name: sub.add_parser(name, help=text) for name, text in (
        ("set", "create or update a job"),
        ("run", "run a command as a job: running while it runs, then done or failed"),
        ("done", "mark a job finished"), ("fail", "mark a job failed"),
        ("clear", "remove a job from the tray"))}
    sub.add_parser("list", help="print every job as JSON")
    for name, p in parsers.items():
        p.add_argument("id")
        if name == "clear":
            continue
        p.add_argument("--message")
        p.add_argument("--output", help="the result, offered to open when done")
        if name in ("done", "fail"):
            continue
        p.add_argument("--title")
        p.add_argument("--project")
        p.add_argument("--session", help="which chat session or tool started it")
        p.add_argument("--parent", help="id of the main job this is a step of")
        p.add_argument("--order", type=float, help="position among its main job's steps")
        p.add_argument("--retry", help="command line that runs the job again (the tray's Retry "
                                       "button); `run` records its own")
        p.add_argument("--retry-cwd", help="folder to run --retry in")
        p.add_argument("--state", choices=STATES)
        p.add_argument("--done", type=float)
        p.add_argument("--total", type=float)
        p.add_argument("--unit", help="what is counted, e.g. pictures")
        p.add_argument("--progress", type=float, help="0..1 when there is no count")
        p.add_argument("--eta-seconds", type=float)
        p.add_argument("--pid", type=int, help="the process that lives for the whole job")
        p.add_argument("--need-gb", type=float, dest="need_gb",
                       help="free RAM (GB) the job needs to run safely; shown in the tray")
        p.add_argument("--log")
        p.add_argument("--tail", help="show this file's last line as the message")
        p.add_argument("--count", help="count files matching this pattern as done")
        p.add_argument("--success", help="regex on the log's end that means it finished")
    argv = sys.argv[1:]
    command = argv[argv.index("--") + 1:] if "--" in argv else []
    args = ap.parse_args(argv[:argv.index("--")] if "--" in argv else argv)

    if args.cmd == "list":
        print(json.dumps(jobs(), indent=1))
    elif args.cmd == "clear":
        clear(args.id)
    elif args.cmd in ("done", "fail"):
        report(args.id, state="done" if args.cmd == "done" else "failed",
               message=args.message, output=args.output)
    elif args.cmd == "set":
        report(args.id, **_fields(args))
    else:
        if not command:
            ap.error("run needs a command after --")
        command[0] = shutil.which(command[0]) or command[0]   # finds .cmd/.bat and PATHEXT too
        fields = _fields(args)
        fields.update(state="running", pid=os.getpid(), started=time.time(),
                      project=fields["project"] or os.path.basename(os.getcwd()),
                      # this very invocation, for the tray's Retry button
                      retry=fields["retry"] or subprocess.list2cmdline(
                          [sys.executable, os.path.abspath(__file__)] + sys.argv[1:]),
                      retry_cwd=fields["retry_cwd"] or os.getcwd())
        clear(args.id)   # a fresh run, not the last one's leftovers
        report(args.id, **fields)
        # under pythonw (no console, e.g. started in the background) keep the
        # command's console hidden too, instead of opening a window that can be closed
        hidden = sys.platform == "win32" and sys.stdout is None
        try:
            # jobs reported from inside the command become this job's sub-jobs
            env = dict(os.environ, JOB_STATUS_PARENT=_slug(args.id))
            code = subprocess.call(command, env=env, creationflags=0x08000000 if hidden else 0)
        except KeyboardInterrupt:
            report(args.id, state="failed", message="stopped by hand")
            return 130
        except OSError as err:
            report(args.id, state="failed", message=str(err))
            return 127
        if code == 0:
            report(args.id, state="done", message=args.message)
        else:
            report(args.id, state="failed", message=f"exited with code {code}")
        return code
    return 0


if __name__ == "__main__":
    sys.exit(main())
