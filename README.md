# job-steward

A Claude Code skill, two small Python tools and a taskbar indicator for looking
after long and heavy jobs on a PC that someone is also using.

**See every job at a glance: JobTray.** A ring in the taskbar's notification
area fills with the main job's progress. It turns amber while a job waits and
red when one fails or its process dies without finishing. A click opens the
list: each job's progress bar, count, time left, current step, and links to
its log and result. A notification says when something finishes or fails. Any
program, script or chat session can add a job by writing one small JSON file.
The app uses about 25 MB of RAM and needs nothing installed.

**Run RAM-heavy work only while there is memory to spare: ram-gate.**

- **Waits** until free RAM has stayed above the job's need for a minute before starting.
- **Pauses** when you open a browser or app. A cooperative worker saves and
  exits between items; any other tool can be stopped from outside before the
  PC freezes.
- **Resumes** by itself when the memory is free again. The job skips what it already finished.
- Every gated job shows up in JobTray by itself.

```bash
# any long job: in the tray while it runs, then done or failed by its exit code
python skills/job-steward/jobstatus.py run render --title "Episode 2: render" \
    --count "out/*.mp4" --total 74 --unit scenes -- python render.py

# a RAM-heavy job: waits for memory, pauses and resumes, and shows in the tray
python skills/job-steward/ramgate.py run --need 4.5 --label painter \
    --progress-glob "out/*.png" --total 40 -- python paint.py --out out
```

Standard library only. The Python tools run on Windows, Linux and macOS;
JobTray is for Windows. See
[`skills/job-steward/SKILL.md`](skills/job-steward/SKILL.md) for the rules
Claude follows and the measured thresholds.

## JobTray

```bat
tray\build.cmd                         &rem builds tray\bin\JobTray.exe with Windows' own C# compiler
tray\bin\JobTray.exe --autostart on    &rem start it, and with Windows from now on
tray\bin\JobTray.exe --show            &rem open the list of the copy already running
```

Windows 11 puts new tray icons in the overflow (the ^ arrow). To keep the ring
in view, drag it onto the taskbar, or turn it on under Settings > Personalization >
Taskbar > Other system tray icons.

Right-click the icon for: Clear finished, Open status folder, Start with Windows, Exit.

### The job files

One file per job in `~/.job-status/` (or `JOB_STATUS_DIR`), named `<id>.json`.
`jobstatus.py` writes them for you; any language can write them directly
(write a temporary file, then rename it over the old one):

```json
{
  "id": "moon-painter", "title": "Moon video: painting", "project": "sleep-history",
  "state": "running", "done": 26, "total": 75, "unit": "pictures",
  "message": "painting the lift-off", "pid": 11024, "pid_started": 1791614433.07,
  "log": "D:\\jobs\\moon\\make.log", "output": "D:\\jobs\\moon\\video.mp4",
  "started": 1791610000, "updated": 1791614446
}
```

| field | meaning |
|---|---|
| `state` | `starting`, `running`, `waiting`, `done` or `failed` |
| `done`, `total`, `unit` | progress as a count; the tray estimates the time left from it |
| `progress` | 0..1, when there is no count |
| `message` | the current step |
| `tail` | a file whose last line is shown as the message ("on hold" or "paused" in it reads as waiting) |
| `count` | a file pattern in one folder, counted as `done` |
| `pid`, `pid_started` | the process that lives for the whole job; if it is gone before `done`, the row turns red ("Stopped unexpectedly") |
| `success` | a regex: if the pid is gone and the end of `log` matches it, the job counts as done |
| `log`, `output` | files the list offers to open |
| `eta_seconds` | time left, if the job knows better than the tray's estimate |
| `parent` | the id of the main job this is a step (sub-job) of |
| `retry`, `retry_cwd` | a command line (and folder) that runs the job again; a failed or stopped row gets a **Retry** button, which asks first and then runs it with no window. `jobstatus.py run` records its own. |
| `project`, `session` | who started it |

**Main jobs and sub-jobs.** A main job is the whole piece of work ("make the
moon video"). A sub-job is one step inside it that reports its own progress
("painting: 26 of 75 pictures"). Each main job is one card in the list, with
its sub-jobs indented inside it. Jobs reported from inside a `jobstatus.py
run` wrapper become its sub-jobs by themselves: the wrapper sets
`JOB_STATUS_PARENT` for its command. Otherwise, set `parent`.

Finished rows disappear after a day, and rows whose process died after three days.

## Install

As a plugin, in Claude Code:

```
/plugin marketplace add alan-r-henry/job-steward
/plugin install job-steward
```

Or as a personal skill for every project on this machine:

```bash
git clone https://github.com/alan-r-henry/job-steward.git
cp -r job-steward/skills/job-steward ~/.claude/skills/
```

## Worker protocol (ram-gate)

```python
import sys, ramgate

for item in todo:
    if ramgate.low(2.8):           # between items, after saving
        sys.exit(ramgate.PAUSED)   # exit code 75: ramgate waits, then restarts
    process(item)
```

A stop with code 75, a failure while RAM was low, or a kill by `--kill-below`
counts as a pause. A pause that finished nothing means the PC is busier than
`--need` allowed for, so the next start waits for `--step-gb` (1 GB) more free
RAM: 6.5, then 7.5, then 8.5 GB. The job waits for you to free memory instead
of failing, and progress resets the threshold. It fails only when the
threshold would pass `--max-need` (75% of installed RAM), because the job then
needs more memory than the machine can free.

What ram-gate is doing right now is also in one line of `~/ramgate-status.txt`:

```
16:40:05 painter: ON HOLD, 3.2 of 4.5 GB RAM free; starts by itself when RAM is free
16:41:12 painter: RUNNING since 16:41, 4.9 GB RAM free, 3 done
```
