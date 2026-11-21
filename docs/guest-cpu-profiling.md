<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Guest CPU profiling

Use this when a title is slow and the GPU is mostly idle.

## 1. Find the busy guest thread

Run with `SHARPEMU_PROFILE_PERFORMANCE=1` and read these lines for the slow scene:

- `[PERF][GPU_BUSY] busy_pct=...`: a low value means the GPU waits for work.
- `[PERF][RENDER] IdleNoQueuedWork=...`: a high value means the render thread waits for guest submissions.
- `[PERF][GUEST_SCHED] name='...' run_ms=...`: a guest thread with `run_ms` near the window length
  (5000 for a 5 s window) is the limit. Its `run_avg_ms` is close to the frame time.

## 2. Split guest code from HLE calls

```text
SHARPEMU_PROFILE_GUEST_RIP=1
SHARPEMU_PROFILE_GUEST_RIP_THREAD=<thread name>
SHARPEMU_PROFILE_GUEST_RIP_LIVE=1
SHARPEMU_PROFILE_GUEST_RIP_REPORT_S=10
```

`[PERF][GUEST] thread_split` gives running/parked for the thread. "Parked" includes the time spent inside
HLE imports. `top_wait` names the import each sample was in. Imports that take the leaf or trivial-leaf
dispatch path do not record their name and are counted as `<idle-or-scheduler>`.

`SHARPEMU_PROFILE_GUEST_RIP_LIVE=1` writes each report as it is made. Without it, the reports are written at
normal exit only, and a forced kill or a crash during shutdown loses them.

## 3. Time each import

`SHARPEMU_PROFILE_IMPORT_TIME=1` prints, every 5 seconds, the 20 imports with the most total time:

```text
[PERF][IMPORT_TIME] sceAgcCreatePrimState calls=423772 total_ms=711,1 avg_us=1,68 reads=3390176 read_ms=86,5 writes=4237720 write_ms=406,1
```

`reads` and `writes` count guest memory accesses through `PhysicalVirtualMemory.TryRead`/`TryWrite` made during
the import. A managed guest write costs much more than a read, because it has to coordinate with GPU memory
tracking, so an export that writes a structure field by field is a good candidate for one combined write.
Blocking imports (`sceKernelWaitSema`, `sceAudioOutOutput`) show long totals that are waiting, not work.

The timers slow the run down (about 20% in This War of Mine); compare FPS with the switch off.

## Example: This War of Mine, 2026-11-21

Main menu scene, RX 9070 XT, Ryzen 7 7800X3D: 25 FPS with the GPU 16% busy. The guest `Renderer` thread ran
100% of the time, 97% of it inside libSceAgc HLE exports (about 1,300 draws per frame). Per frame:
`sceAgcSetCxRegIndirectPatchAddRegisters` about 17,000 calls, `memcpy` about 31,000, `sceAgcCreatePrimState`
about 3,900 with ten guest writes each.
