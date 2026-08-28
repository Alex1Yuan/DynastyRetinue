# 1.5.12

Small internal cleanup on top of 1.5.11. **No performance fix** — see below.

## About the frame stutter some people report

A few players have reported the screen hitching every few steps while walking around, and that
removing the mod stops it. I instrumented the mod and measured it rather than guessing. Three
things were ruled out, in order:

- The mod's per-frame code. With the mod fully loaded but every guard dismissed, frame times are
  normal (~47 fps, under 2 long frames per 10 seconds on my machine).
- A background scan I initially blamed. It turned out to walk 44 entities, not the thousands I
  had assumed — far too cheap to matter. I cached it anyway, but it was not the cause.
- The mod's per-guard patches. Call counters show them running 0–6 times a second while walking.

What's left is the engine simulating the extra units: each guard is a full AI character that has
to be pathed, animated and drawn. On my machine five guards cost roughly 7 fps and about doubled
the number of long frames. That isn't something the mod can optimise away.

**If it bothers you, lower the guard cap in the settings** — the cost scales with headcount, so
three guards costs roughly half of six.

One caveat on those figures: where you are standing affects frame rate more than the guards do,
so treat them as an order of magnitude rather than exact numbers.

## Also, not the same thing

Some players see a brief hitch when it becomes a guard's turn *in combat*. That was measured
separately: guards take about 480 ms to choose an action and enemies about 500 ms — so it's the
base game's AI, not this mod.

---

If you skipped 1.5.11:

- Guards heal out of combat like your party does. They never did before — vanilla's auto-heal
  checks party membership, and guards deliberately aren't party members, because making them
  companions would turn them into units you control directly.
- Stuck-guard rescue works again: loading an earlier save used to disable it for the rest of the
  session, and a guard vibrating in place never counted as stuck.
- The log file rotates at 4 MB instead of growing forever, and records which build produced it.
