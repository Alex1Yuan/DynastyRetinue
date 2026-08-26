# 1.5.11

Follow-up to 1.5.0. Mostly fixes to things that were quietly not working.

## Retinue

- **Guards now heal out of combat, like your party does.** They never did before. Vanilla's
  auto-heal checks `IsInPlayerParty`, and guards deliberately aren't party members — making them
  companions would turn them into units you control directly, which defeats the point. The check
  now also accepts guards. Everything else about it is unchanged, including the "don't revive"
  exclusion.
- **The stuck-guard rescue works again.** Two separate faults:
  - Loading an earlier save broke it for the rest of the session. The timer runs off the save's
    own playtime, and the mod stays loaded across save loads, so time appeared to run backwards
    and the check never fired again. It now re-baselines instead of deadlocking, and also resets
    on area change.
  - A guard vibrating in place — pathfinding retrying against geometry — never counted as stuck,
    because the test was "did it move since last second" rather than "has it got anywhere".
    It now measures whether the guard has left a small radius, so jittering counts.

## Housekeeping

- The mod's log file is capped: it rotates at 4 MB and keeps one previous copy. It used to grow
  forever, which mattered if you turned on verbose logging for a bug report and forgot.
- Every log line now starts with a version banner, so a log you send in says which build produced it.
- Combat-speed diagnostics from 1.5.x are off by default now. If space or ground combat feels
  slow, turn on verbose logging and the numbers come back.

## Notes

Ground combat can hitch when a guard's turn starts. It was measured: guards take ~480 ms to pick
an action and enemies take ~500 ms, so it's the game's own AI, not this mod. It's more noticeable
with guards only because several of them act in a row.
