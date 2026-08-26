# 1.5.12

Performance fix, on top of 1.5.11.

## Fixed

- **Periodic frame stutter while walking around, out of combat.** A background check looking for
  stuck guards was scanning **every entity in the area** once per second — props, lights and
  interactables, not just units — and it did so before checking whether you had any guards at all.
  So even a fresh install with nobody recruited paid the cost. The allocation churn is what showed
  up as a hitch every few steps. The list is now cached for ten seconds and rebuilt on area change.

## Not the same thing

Some players see a brief hitch when it becomes a guard's turn in combat. That one was measured:
guards take about 480 ms to choose an action and enemies about 500 ms — so it's the base game's AI,
not this mod. It stands out with guards only because several of them act one after another.

---

Also in 1.5.11, if you skipped it:

- Guards heal out of combat like your party does. They never did before — vanilla's auto-heal
  checks party membership, and guards deliberately aren't party members, because making them
  companions would turn them into units you control directly.
- Stuck-guard rescue works again: loading an earlier save used to disable it for the rest of the
  session, and a guard vibrating in place never counted as stuck.
- The log file rotates at 4 MB instead of growing forever, and records which build produced it.
