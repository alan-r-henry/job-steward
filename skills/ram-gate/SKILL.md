---
name: ram-gate
description: >-
  Run RAM-heavy local work (AI image, voice, music or video models, upscalers,
  renders, big builds) only while the PC has memory to spare: wait for free
  RAM before starting, pause when the user opens a browser or app, resume by
  itself when it is closed. Use whenever you are about to start a long or
  memory-hungry job on the user's own computer, when a job was killed or froze
  the PC for low RAM, or when the user says they need the PC or asks to "hold"
  or "pause" heavy work.
---

# ram-gate

The user works on the same PC while heavy jobs run. A job that keeps going
while they open a browser can freeze Windows. So heavy work is **gated on free
RAM**, never on a schedule or on asking: it waits, pauses and resumes by
itself, and the user just uses their PC.

The tool is `ramgate.py` next to this file (standard library only, any Python 3).

## The rules

1. **Never start a heavy job directly.** Wrap it:
   ```bash
   python <this skill>/ramgate.py run --need 4.5 --label "painter" \
       --progress-glob "out/*.png" -- python paint.py --out out ...
   ```
   It starts only after free RAM has stayed at or above `--need` GB for a
   minute, so a browser closed for a moment doesn't trigger it.
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
6. **Point the user at the status file** `~/ramgate-status.txt` (one line,
   refreshed every few seconds: ON HOLD / starting in N s / RUNNING, N done /
   PAUSED / DONE / FAILED). Set `RAMGATE_STATUS` or `--status` for another path.
7. **Tell the user** when a job is on hold and why ("on hold: 3.1 GB free, needs
   4.5; it starts by itself when RAM is free"). Never ask them to close apps
   unless they want the job sooner.

## Choosing --need

Measure, don't guess: log `ramgate.free_gb()` at load and per item, note the
lowest value, and set `--need` to the job's drop plus about 2 GB of headroom
for the user.

Measured on the owner's PC (15.2 GB RAM, RX 9060 XT 16 GB; the GPU also drives
the desktop and dwm holds ~3 GB of VRAM):

| job | start at (`--need`) | pause between items | hard stop |
|---|---|---|---|
| Z-Image-Turbo painter (fp8, 1344x768), batches of 6 per process | 4.5 GB | 2.8 GB | 2.0 GB |
| Qwen3-TTS 1.7B (plain-read loader) | 4.0 GB (load drops ~1.5 GB) | 2.5 GB | 1.5 GB |

Typical users of RAM: Brave/Chrome 1.3-2 GB, Claude desktop ~2.5 GB, Riot
Client and Overwolf ~1-2 GB together.

## Memory-mapped weights

Loading a multi-GB `.safetensors` file through `safe_open` memory-maps it, and
on a USB disk that can take free RAM down by the whole file size. Reading each
tensor with plain file reads and moving it straight to the GPU keeps only one
tensor in RAM at a time. For transformers models, a drop-in reader patched over
`transformers.modeling_utils.safe_open` does this.

## Useful commands

```bash
python ramgate.py free              # free RAM now, in GB
python ramgate.py wait --need 4.5   # block (e.g. at the top of a shell script)
```
