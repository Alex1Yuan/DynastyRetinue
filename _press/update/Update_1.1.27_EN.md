# 1.1.27

Space combat and star map presentation. No new mechanics, no rule changes, nothing written to
saves, no effect on co-op. Every item can be switched off individually in the mod settings.

## Space combat

- **Movement tiles now match your ship's size** — 2×2 for a cruiser, 3×3 for a grand cruiser.
  That's their actual movement stride, so the tiles join up instead of scattering. Frigates and
  raiders are one tile and unchanged. Ground combat has always filled out a large unit's movement
  area; the space-combat path skipped that step entirely. This calls the game's own function.
- Keyed off each unit's own size rather than a hardcoded ship class, so 1×1 projectiles like
  torpedoes and fighters are unaffected.
- **The camera can finally back off.** Vanilla's space-combat wheel is a dolly zoom — the FOV
  narrows as the camera pulls back and the two cancel out, so the ship's on-screen size never
  changes. The wheel now genuinely widens the view. Cruiser and grand cruiser tune separately.
- **The drag hologram is your actual ship**, at the right size. It used to be the same generic
  1552-triangle mesh whatever you flew (a cruiser hull is 75989), scaled 1.0 against a hull
  scaled 1.515. Green shading is untouched.
- Grand cruiser movement tiles were offset by one tile (the odd-size alignment term was missing).

## Star map

- The star map ship follows your current hull instead of staying on the old one, rescaled by
  bounding-box ratio so it doesn't jump in size. It's driven by a separate view with its model
  baked into the scene, unrelated to the hull-swap setting.

## Also

- Diagnostic bundle now includes space-combat and star-map state (camera parameters, footprint
  sizes, scene object structure).

## Known issues

- Ships snap slightly when a turn completes. Heading is aligned to a multiple of 45° in one go,
  up to 32.7°, inside a single 50 ms simulation step. Fixing it means touching view-layer
  interpolation.
- Weapon ranges on large ships may not line up on diagonal headings. Vanilla's player ship is
  1×2, which squashes to 1×1 and can't drift at any angle; this only surfaces at 2×4 and 3×6.
  The same data drives hit resolution, not just the display, so it's tracked separately rather
  than patched in passing.
