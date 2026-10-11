---
name: job-steward
description: >-
  Look after long or heavy jobs on the user's own PC. (1) Show every job that
  runs for more than a minute or so (renders, downloads, model runs, builds,
  test suites, batch scripts) in the JobTray taskbar list, so the user can see
  progress at any time and hears when it finishes, fails, or its process dies.
  (2) Run RAM-heavy work (AI image, voice, music or video models, upscalers,
  renders) only while the PC has memory to spare: wait for free RAM, pause when
  the user opens a browser or app, resume by itself. Use whenever you start
  background or long-running work, when the user asks how a job is going, when
  a job was killed or froze the PC for low RAM, or when they ask to "hold" or
  "pause" heavy work.
---

# job-steward

Two tools, next to this file, standard library only:

- `jobstatus.py` puts a job in the **JobTray** taskbar list (any job, any length).
- `ramgate.py` runs **RAM-heavy** jobs only while memory is free. Every gated
  job also appears in the tray by itself, with its state and count.

## Show every long job in the tray

The user can't watch a terminal or this chat all day; the tray icon (a ring
that fills with the main job's progress, amber while waiting, red when
something failed or died) is how they follow work. So whenever you start
anything that runs longer than about a minute, in the background or detached,
put it in the list, from any chat session, in any project:

```bash
# simplest: wrap the command; running while it runs, then done or failed by exit code
python <this skill>/jobstatus.py run render-ep2 --title "Episode 2: render" \
    --total 74 --unit scenes --count "out/scenes/*.mp4" --log out/render.log -- python render.py

# attach to something already running (its pid makes a silent death show up red)
python <this skill>/jobstatus.py set moon --title "Moon video" --pid 11024 \
    --tail out/make.log --count "images/*.png" --total 75 --unit pictures

python <this skill>/jobstatus.py done moon --message "uploaded"   # also: fail, clear, list
```

In Python: `job = jobstatus.Job("id", "Title", total=75, unit="pictures")`, then
`job.update(done=26, message="...")`, `job.waiting(...)`, `job.finish(...)`,
`job.fail(...)`, or `with jobstatus.Job(...) as job:` to finish or fail by itself.

- **Main job and sub-jobs**: the main job is the whole piece of work the user
  asked for ("make the moon video"); sub-jobs are its steps that report their
  own progress ("painting: 26 of 75"). The tray shows each main job as one
  card with its sub-jobs nested inside. Anything reported from inside a
  `jobstatus.py run` wrapper (ram-gate rows included) becomes its sub-job by
  itself (via `JOB_STATUS_PARENT`); otherwise pass `--parent <main id>`.
- **Retry**: a failed or stopped row gets a Retry button when the job has a
  `retry` command line. `run` records its own; for anything else pass
  `--retry "<command line>" --retry-cwd <folder>`. Make jobs resumable, so a
  retry skips finished work.
- **ids**: short and unique per job (`<project>-<step>`); reuse one to update it.
- **title**: what the user would call it ("Moon video: painting"), not a command line.
- **pid**: a process that lives for the *whole* job (a wrapper, the main
  script), never a worker that exits to be restarted, or the tray reports it
  as stopped unexpectedly. `run` and `Job()` record their own process.
- **message**: the current step in plain words; or `--tail <file>` to show a
  log's last line. Text with "on hold", "paused" or "waiting" shows as waiting.
- **count/total/unit** give the bar and an ETA; `--progress 0.4` when there's no count.
- **output**: the result (a video, a folder); the tray offers to open it when done.
- **session**: optional, which chat or tool started it, when several run at once.
- Files live in `~/.job-status/` (or `JOB_STATUS_DIR`); finished rows leave
  after a day. Don't use AppData for this folder: the Claude app's Bash runs
  inside its package, where AppData is redirected.
- To show the user the list: `JobTray.exe --show` (see the repo's `tray/`).
- **Detached runs** (that must outlive the chat): start them with
  `pythonw jobstatus.py run ... -- <command>` through WMI
  (`Invoke-CimMethod Win32_Process Create` from the PowerShell tool). Under
  pythonw the command gets no window: a blank console window is easy to close
  by mistake, and closing it ends the job.

## RAM-heavy jobs: ram-gate

The user works on the same PC while heavy jobs run. A job that keeps going
while they open a browser can freeze Windows. So heavy work is **gated on free
RAM**, never on a schedule or on asking: it waits, pauses and resumes by
itself, and the user just uses their PC.

### The rules

1. **Never start a heavy job directly.** Wrap it:
   ```bash
   python <this skill>/ramgate.py run --need 4.5 --label "painter" \
       --progress-glob "out/*.png" --total 40 --job-id book-painter \
       -- python paint.py --out out ...
   ```
   It starts only after free RAM has stayed at or above `--need` GB for a
   minute, so a browser closed for a moment doesn't trigger it. A low-RAM
   stop that finished nothing raises the next start's threshold by `--step-gb`
   (1 GB), so a busy PC makes the job wait longer instead of failing. It fails
   only past `--max-need` (75% of RAM). On 2026-10-10 the old rule (fail after
   two such stops) killed the moon painter three times while the user had
   Claude and Brave open.
2. **Jobs must be resumable.** A restart has to skip finished items (pictures,
   audio chunks, frames). If a tool isn't, write its outputs one item at a time
   and skip existing files, or add `--limit N` style batching first.
3. **Pause cooperatively when you write the worker.** Between items:
   ```python
   import ramgate, sys
   if ramgate.low(2.8):          # save first; nothing half-done
       sys.exit(ramgate.PAUSED)  # 75: ramgate waits and restarts
   ```
   Keep a hard watchdog inside long items too (kill own process from outside
   with `taskkill /F /PID` at ~2 GB free; `TerminateProcess` from inside a GPU
   process can leave it alive).
4. **Tools you can't change:** add `--kill-below 1.5`. ramgate kills the whole
   process tree when RAM gets critical; the item in progress is lost, but the
   PC stays responsive and the job resumes later.
5. **One GPU job at a time.** Two big GPU jobs together oversubscribe VRAM and
   spill into system RAM (on AMD/ROCm on Windows this also corrupted copies).
6. **The tray shows it.** Each gated job is a row (its `--job-id`, default the
   label): waiting with the reason, running with its count, paused, done or
   failed. With `--total`, a run that ends short of it (one batch of many)
   stays "running". The same line also goes to `~/ramgate-status.txt`
   (`RAMGATE_STATUS` or `--status` for another path).
7. **Tell the user** when a job is on hold and why ("on hold: 3.1 GB free, needs
   4.5; it starts by itself when RAM is free"). Never ask them to close apps
   unless they want the job sooner.

### Choosing --need

Measure, don't guess: sample `ramgate.free_gb()` continuously (every ~0.5 s,
in a thread) through load and through whole items, note the lowest value, and
set `--need` to the job's drop plus about 2 GB of headroom for the user.
Free RAM logged only between items misses the peak inside an item.

Measured on the owner's PC (15.2 GB RAM, RX 9060 XT 16 GB; the GPU also drives
the desktop and dwm normally holds 0.2-3 GB of VRAM):

| job | start at (`--need`) | pause between items | hard stop | `--max-desktop-gpu` |
|---|---|---|---|---|
| Z-Image-Turbo painter (fp8, 1344x768, memory-efficient attention), batches of 20 per process | 4.0 GB (takes ~1.4 GB at the low point; before efficient attention it took ~4.5 GB and needed 6.5) | 2.8 GB | 2.0 GB | 7 GB (VRAM peak ~7.5 GB) |
| Qwen3-TTS 1.7B (plain-read loader) | 4.0 GB (load drops ~1.5 GB) | 2.5 GB | 1.5 GB | not needed (~4 GB VRAM) |

Typical users of RAM: Brave/Chrome 1.3-2 GB, Claude desktop ~2.5 GB, Riot
Client and Overwolf ~1-2 GB together.

### The desktop's graphics memory (big GPU jobs on Windows)

When the GPU also drives the screen, the desktop compositor (dwm) holds some
of its memory. After a day of force-killed GPU jobs on the owner's PC, dwm
held ~8 GB of the 16 GB card; the painter (~11 GB peak) no longer fit, Windows
spilled it into system RAM, and it died on its first picture every time. A
graphics-driver reset (Win + Ctrl + Shift + B) did **not** free it; a PC
restart took dwm back to 0.2 GB.

So for big GPU jobs add `--max-desktop-gpu <GB>` (card size minus the job's
VRAM peak, minus ~1 GB): the gate also holds while dwm holds more than that,
and the status file says so. `python ramgate.py desktop-gpu` prints it. When a
job is held for this, ask the user to sign out or restart the PC; don't do it
for them.

### GPU memory that lands in system RAM

On Windows, when a job's GPU memory gets tight the graphics driver quietly
moves part of it into system RAM. It shows up in no process's memory, only as
free RAM falling, and comes back when the job frees it. On the owner's PC the
painter's attention step built the whole attention matrix (PyTorch's fallback,
because memory-efficient attention is switched off as "experimental" on AMD),
and ~2.3 GB went to system RAM during every picture. Setting
`TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL=1` cut the job's RAM use from ~4.5 to
~1.4 GB and made it ~10x faster, with the same pictures. When free RAM falls
while a GPU job's own working set doesn't grow, look for big GPU temporaries
(attention, unfused ops) before anything else.

### Memory-mapped weights

Loading a multi-GB `.safetensors` file through `safe_open` memory-maps it, and
on a USB disk that can take free RAM down by the whole file size. Reading each
tensor with plain file reads and moving it straight to the GPU keeps only one
tensor in RAM at a time. For transformers models, a drop-in reader patched over
`transformers.modeling_utils.safe_open` does this.

### Useful commands

```bash
python ramgate.py free              # free RAM now, in GB
python ramgate.py wait --need 4.5   # block (e.g. at the top of a shell script)
python ramgate.py desktop-gpu       # graphics memory the desktop (dwm) holds, in GB
```
