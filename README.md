# ram-gate

A Claude Code skill and a small Python tool for running RAM-heavy local work,
such as AI image, voice, music and video models, upscalers or renders, on a PC
that someone is also using.

- **Waits** until free RAM has stayed above the job's need for a minute before starting.
- **Pauses** when you open a browser or app. A cooperative worker saves and
  exits between items; any other tool can be stopped from outside before the
  PC freezes.
- **Resumes** by itself when the memory is free again. The job skips what it already finished.

```bash
python skills/ram-gate/ramgate.py free
python skills/ram-gate/ramgate.py run --need 4.5 --label painter \
    --progress-glob "out/*.png" -- python paint.py --out out
```

Standard library only; Windows, Linux and macOS. See
[`skills/ram-gate/SKILL.md`](skills/ram-gate/SKILL.md) for the rules Claude
follows and the measured thresholds.

## Install

As a plugin, in Claude Code:

```
/plugin marketplace add alan-r-henry/ram-gate
/plugin install ram-gate
```

Or as a personal skill for every project on this machine:

```bash
git clone https://github.com/alan-r-henry/ram-gate.git
cp -r ram-gate/skills/ram-gate ~/.claude/skills/
```

## Worker protocol

```python
import sys, ramgate

for item in todo:
    if ramgate.low(2.8):           # between items, after saving
        sys.exit(ramgate.PAUSED)   # exit code 75: ramgate waits, then restarts
    process(item)
```

A stop with code 75, a failure while RAM was low, or a kill by `--kill-below`
counts as a pause. Two pauses in a row that finish nothing count as a real
failure, so a job that simply needs more memory than the machine has doesn't loop forever.
