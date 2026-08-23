# Big ships finally sit where they belong — 1.5.0

The public build is still 1.0.92. This release rolls up everything since. It took me
thirty-odd versions to get here with nothing shipped in between; the last section explains why.

No new mechanics. Everything here serves one goal: make cruisers and grand cruisers land
**where they actually are** in space combat. Firing arcs, the hull, the green tiles and the
click target used to be four different answers to the same question.

---

# 1. The big-ship geometry overhaul (the heart of 1.5.0)

## Firing arcs, rebuilt from the hull

In the 1.1.27 notes I left an open item: *large ships on diagonal headings may show weapon
ranges that don't line up*. This closes it — and the problem turned out to be worse than
I thought at the time.

Vanilla computes the "source tiles" for broadsides and prow guns (the tiles an arc fires
*from*) in the wrong place. The cause is neat: the game carries two different anchor
conventions internally. The tile-laying code anchors on the **centre**; the weapon code
assumes a **corner**. They differ by `(⌈(W−1)/2⌉, L/2−1)`. At width 1 that difference is
exactly (0,0) — so frigates are correct by coincidence. And since vanilla's player flagship
is *only ever* a frigate, and every enemy ship is 1×2, nobody ever hit this.

Put a 2×4 cruiser in and it shows immediately: all four port-broadside source tiles sit
**completely off the hull**. On a 3×6 grand cruiser, every orthogonal heading has one side
with all six tiles adrift. Diagonals are worse — vanilla produces 5 and 7 tiles where the
hull's own outer skin is 7 and 11. That's a counting problem, not a placement problem: there
simply aren't enough tiles, so no amount of parameter tweaking covers the hull. It had to be
rewritten.

Sources are now cut from the hull footprint itself — the outermost layer of tiles along the
arc's direction:

- Orthogonal headings: broadsides span the full length of the flank (4 tiles on a cruiser,
  6 on a grand cruiser); prow guns span the full beam (2 and 3).
- Diagonal headings: the stepped skin, with prow guns covering both edges of the bow's
  bounding box.

The change goes in at the one function the display path and the hit-detection path **both**
go through, so there's no way for "what you see" and "what you can shoot" to drift apart.
That shared-ness is exactly why I filed this separately last time instead of patching it in
passing — this code decides hits, not just pixels.

Frigates get a hard gate: the new code is **tile-for-tile identical to vanilla across all
376 combinations**, walk order included, not merely the same set. Every enemy ship is 1×2,
so that gate is also a guarantee that enemy arcs didn't move by a single byte.

> One deliberate divergence: the window is clamped to the length of the skin chain, not the
> length of the hull. On orthogonal headings those are equal, so nothing changes; on diagonals
> the skin is 2L−1 long, and clamping to hull length would cut a grand cruiser's 11 tiles down
> to 7. At L=2 both clamps agree — which is precisely why this diagonal bug is invisible on
> frigates.

## The hull, the green tiles and the click target are one place now

For anything bigger than a frigate, the hull model, the green movement tiles and the tile you
actually order a move to were **three different positions**.

The tiles and the cursor are both pinned to a W×W block sitting on the pathfinding anchor, and
that block doesn't rotate; the hull does. Turn the ship and they come apart. All three now
derive their offset from a single source, so they agree at every heading.

## The landing highlight no longer jumps around

Vanilla matches by exact coordinates: the tile under your cursor has to match a landing marker's
coordinates exactly, or nothing lights up at all. And markers only exist on anchors where the
ship can actually stop — a set **with holes** in it. Unreachable, out of movement range, blocked
by another ship: each leaves a gap.

I had been computing a tile coordinate arithmetically and never checking whether a marker was
actually there. Land on a hole and the whole highlight goes dark; nudge the mouse and it lights
up again somewhere else. What you saw was the highlight bouncing up and down while the mouse
moved steadily in one direction.

Now the highlight is found rather than computed: I check which rendered block actually contains
the cursor, in world coordinates, against the marker's own transform. Blocks are as wide as the
anchor spacing, so they tile the plane without gaps or overlap — at most one can contain the
cursor. There's also a small amount of hysteresis, because the input genuinely does wobble by a
few pixels when you drag slowly by hand, and a two-tile-wide block turns a few pixels of hand
tremor into a very visible jump.

The other half of that fix was a feedback loop hiding in vanilla. For a 2×4 cruiser the cursor
offset is a half-tile quadrant vector chosen by comparing against **the previous frame's**
selected node. Solve it and the steady state is genuinely non-monotonic: as the cursor slides
one way, the chosen tile goes k+1, k+1, k, k, k+2, k+2, k+1 … It only lives on the
`Cruiser_2x4` branch — grand cruisers are odd-width and return zero, frigates get zeroed by the
scale check — which is exactly why aiming a weapon (a 1×1 cursor) never jumped while moving did.

---

# 2. Space combat and star map presentation (the 1.1.x work, never published)

These landed after 1.0.92 and have been sitting on my disk since. All purely visual: no pathing
changes, no rule changes, nothing written to saves, no effect on co-op, and each can be switched
off on its own in the settings.

## Movement tiles match your ship's size

The green movement tiles used to be one tile each, whatever you flew. A cruiser's or grand
cruiser's legal landing spots are already 2–3 tiles apart — that's their movement stride — so
the tiles looked scattered and were fiddly to click.

Cruisers now draw 2×2 and grand cruisers 3×3, exactly their stride, so the tiles join up.
Frigates and raiders are one tile anyway and are unchanged.

This isn't really a new feature, it fills a gap: ground combat has always had
`ExtendMovementAreaByUnitSize` to fill out a large unit's movement area, and the space-combat
code path skipped that step entirely. This calls the game's own function rather than reinventing
it. The rule keys off **each unit's own size**, not a hardcoded ship class, so 1×1 projectiles
like torpedoes and fighters are unaffected.

## The camera can finally back off

A big ship used to fill the screen and no amount of scrolling helped.

The reason is interesting: vanilla's space-combat wheel is a **dolly zoom** — the field of view
narrows while the camera pulls back, and the two cancel out. The ship's on-screen size never
changes; only the background perspective does. The wheel was never a zoom.

Now it genuinely widens the view: fully applied at the far end, exactly vanilla at the near end,
smooth in between. Cruiser and grand cruiser are tuned separately, and the whole thing can be
switched off.

> An earlier attempt pushed the camera back instead. It looked right but had two side effects:
> health bars and other UI drifted out of place (they compute screen positions from the camera),
> and models dropped to their low-poly LOD (LOD keys off camera distance). Widening the FOV
> leaves the camera where it is, so neither happens.

## The drag outline is your actual ship

Whatever you flew, the green hologram shown while dragging a move order **looked identical every
time**, and noticeably smaller than the hull.

Ship holograms have no "pick a model for this ship class" step at all: ground units get three
tiers (blueprint override → skeleton match → default fallback), ships just use the one generic
low-poly mesh — 1552 triangles, where a cruiser hull is 75989. The scale wasn't synced either:
hologram 1.0, hull 1.515. It's your own ship's model now, at the right size. The green shading
is untouched.

## And the ship on the star map changed too

Refit into a bigger hull and the star map still showed the old one. That ship belongs to a
different view layer, with the model baked into the scene (the same generic low-poly mesh again),
completely unrelated to the setting that swaps the combat model. It now follows your current hull,
rescaled by bounding-box ratio so it doesn't suddenly change size.

---

# 3. Also fixed along the way

- Space combat had gotten laggy: one code path was reflecting over several hundred landing
  markers every frame. Gone.
- Aiming a ship weapon puts the cursor back to per-tile 1×1 instead of snapping to the 2×2
  landing grid.
- In ground combat, selecting a large unit no longer snaps its cursor ring to the **ship's**
  landing table.
- Grand cruiser movement tiles were offset by one tile (the odd-size alignment term was missing).
- Roster page: names blanked by TMP, and the count not refreshing after dismissing someone.
- The diagnostic bundle now includes space-combat and star-map state (camera parameters,
  footprint sizes, scene object structure).

---

# 4. Still open

- **The cursor ring** (the one carrying the hologram) can still sit off-centre in its block on
  big ships. It doesn't affect clicking or targeting; filed as a known issue.
- **Ships snap slightly when a turn completes.** Root cause is known: once a ship stops, its
  heading is aligned to a multiple of 45° in one go — up to 32.7° — and the whole thing happens
  inside a single 50 ms simulation step, which reads as a jump. Fixing it means touching view-layer
  interpolation, which is riskier than anything above, so it's on hold.

---

# 5. Why the version number jumped so far

1.0.92 to 1.5.0, thirty-odd versions, nothing shipped in between.

Vanilla space combat isn't very consistent internally. "Which tile is the mouse over right now"
alone is answered by **three independent chains**: one for laying the green tiles, one for the
ring and the actual order, and a third for which marker lights up. On top of that there are two
anchor conventions (centre vs corner), and a "squash 2×4 into 2×2" step that's only valid when a
hull is exactly twice as long as it is wide.

None of this surfaces in vanilla, because the player's flagship is always 1×2: at width 1 every
one of those differences collapses to zero and every spacing collapses to one, so all three chains
happen to agree.

Make the ship bigger and those dormant inconsistencies wake up one after another — and usually you
can only see the next one after fixing the last. The other half is that a lot of this can't be
verified offline. I can run 376 gate cases against the arc geometry in a test harness, but "what it
looks like on screen" only exists in-game, so a fair number of versions went on test rounds, one
version per round.

Those chains are one thing now, so it can ship.
