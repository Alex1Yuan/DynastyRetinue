# 1.5.0

First release since 1.0.92. Everything below has been sitting on my disk across thirty-odd
internal versions — see the note at the bottom for why it took that many.

No new mechanics. This release is about making cruisers and grand cruisers behave correctly
in space combat, where vanilla only ever has to handle a 1×2 frigate.

## Firing arcs

- **Broadside and prow arcs are rebuilt from the hull.** On a 2×4 cruiser all four port-broadside
  source tiles used to sit completely off the hull; on a 3×6 grand cruiser every orthogonal
  heading had one side adrift. Diagonals produced fewer tiles than the hull even has, so no
  amount of tuning could cover it.
- Arcs now come off the hull's outer skin: broadsides span the full flank (4 / 6 tiles), prow
  guns the full beam (2 / 3). Diagonal headings use the stepped skin and cover both edges of
  the bow.
- Display and hit detection share one code path, so what you see is what you can shoot.
- Frigates are untouched: tile-for-tile identical to vanilla across all 376 combinations. Every
  enemy ship is 1×2, so enemy arcs are unchanged.

> Cause: the game carries two anchor conventions internally — tiles anchor on the centre, weapons
> assume a corner. The difference is exactly zero at width 1, so frigates were correct by accident.

## Position and targeting

- The hull, the green movement tiles and the tile you actually order a move to are one place now.
  They used to drift apart as the ship turned.
- The landing highlight no longer jumps around while you drag the mouse steadily in one direction.
- Aiming a ship weapon puts the cursor back to per-tile 1×1 instead of snapping to the 2×2 grid.
- Grand cruiser movement tiles were offset by one tile.

## Presentation (done after 1.0.92, never published until now)

- **Movement tiles match your ship's size** — 2×2 for a cruiser, 3×3 for a grand cruiser, matching
  their actual movement stride, so the tiles join up instead of scattering.
- **The camera can finally back off.** Vanilla's space-combat wheel is a dolly zoom: the FOV narrows
  as the camera pulls back and the two cancel out, so the ship's on-screen size never changes. The
  wheel now genuinely widens the view.
- **The drag hologram is your actual ship**, at the right size. It used to be the same generic
  1552-triangle mesh whatever you flew.
- **The star map ship follows your current hull** instead of staying on the old one.

## Also

- Fixed space-combat lag (one path was reflecting over several hundred markers every frame).
- Ground combat: selecting a large unit no longer snaps its cursor ring to the ship's landing table.
- Roster: names blanked by TMP, and the count not refreshing after dismissing someone.
- Diagnostic bundle now includes space-combat and star-map state.

## Known issues

- The cursor ring can sit off-centre in its block on big ships. Doesn't affect clicking or targeting.
- Ships snap slightly when a turn completes: heading is aligned to a multiple of 45° in one go, up
  to 32.7°, inside a single 50 ms simulation step. Fixing it means touching view-layer interpolation.

## Why the version jumped from 1.0.92 to 1.5.0

Vanilla space combat isn't very consistent internally. "Which tile is the mouse over" alone is
answered by three independent code paths, there are two different anchor conventions, and a
"squash 2×4 into 2×2" step that only holds when a hull is exactly twice as long as it is wide.
None of it surfaces in vanilla, because at width 1 every difference collapses to zero and all
three paths happen to agree.

Make the ship bigger and those dormant inconsistencies wake up one at a time — usually you can
only see the next one after fixing the last. And a lot of it can't be checked offline: I can run
376 gate cases against the arc geometry in a harness, but "what it looks like on screen" only
exists in-game, so a good number of versions went on test rounds, one version per round.
