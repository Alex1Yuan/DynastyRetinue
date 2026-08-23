# 1.5.0

First release since 1.0.92 — everything from 1.1.x is included here as well. No new mechanics.
This release is about making cruisers and grand cruisers behave correctly in space combat, where
vanilla only ever has to handle a 1×2 frigate.

## Firing arcs

- **Broadside and prow arcs are rebuilt from the hull.** On a 2×4 cruiser all four port-broadside
  source tiles used to sit completely off the hull; on a 3×6 grand cruiser every orthogonal
  heading had one side adrift. Diagonals produced fewer tiles than the hull even has, so no
  amount of tuning could have covered it.
- Arcs now come off the hull's outer skin: broadsides span the full flank (4 tiles on a cruiser,
  6 on a grand cruiser), prow guns the full beam (2 and 3). Diagonal headings use the stepped
  skin and cover both edges of the bow.
- Display and hit detection go through the same code, so what you see is what you can shoot.
- Frigates are untouched — tile-for-tile identical to vanilla across all 376 combinations. Every
  enemy ship is 1×2, so enemy arcs are unchanged.
- Cause: the game carries two anchor conventions internally, one centre-based and one
  corner-based. The difference is exactly zero at width 1, so frigates were correct by accident.

## Position and targeting

- The hull, the green movement tiles and the tile you actually order a move to are one place now.
  They used to drift apart as the ship turned.
- The landing highlight no longer jumps around while you drag the mouse steadily in one direction.
- Aiming a ship weapon puts the cursor back to per-tile 1×1 instead of snapping to the 2×2 grid.
- Ground combat: selecting a large unit no longer snaps its cursor ring to the ship's landing table.

## Carried over from 1.1.x

- Movement tiles match your ship's size — 2×2 for a cruiser, 3×3 for a grand cruiser.
- The camera can finally back off. Vanilla's wheel is a dolly zoom that cancels itself out; it now
  genuinely widens the view.
- The drag hologram is your actual ship, at the right size.
- The star map ship follows your current hull.
- Grand cruiser movement tiles were offset by one tile.

## Also

- Fixed space-combat lag (one path was reflecting over several hundred markers every frame).
- Roster: names blanked by TMP, and the count not refreshing after dismissing someone.
- Diagnostic bundle now includes space-combat and star-map state.

## Known issues

- The cursor ring can sit off-centre in its block on big ships. Doesn't affect clicking or targeting.
- Ships snap slightly when a turn completes: heading is aligned to a multiple of 45° in one go, up
  to 32.7°, inside a single 50 ms simulation step.

## Why the version jumped from 1.0.92 to 1.5.0

Vanilla space combat isn't very consistent internally. "Which tile is the mouse over" alone is
answered by three independent code paths, there are two different anchor conventions, and a
"squash 2×4 into 2×2" step that only holds when a hull is exactly twice as long as it is wide.
None of it surfaces in vanilla, because at width 1 every difference collapses to zero and all
three paths happen to agree.

Make the ship bigger and those dormant inconsistencies wake up one at a time — usually you can
only see the next one after fixing the last. And a lot of it can't be checked offline: I can run
376 gate cases against the arc geometry in a test harness, but what it looks like on screen only
exists in-game, so a good number of versions went on test rounds, one version per round.
