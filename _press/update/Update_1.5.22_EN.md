# 1.5.22

## Fixed: stuttering with the mod installed

If the picture hitched every second or so while you moved around — especially when panning the
camera, and especially at higher frame rates — that was us. Sorry, and thanks for the reports.

The cause was in the star map ship model feature. To find your ship it walked every object in the
scene and inspected each one's components. Expensive, but it was only ever supposed to run on the
star map.

It had no check for whether you were actually on the star map. So it ran everywhere, once a second,
including on foot. And since the star map ship doesn't exist in a walking area, it never found
anything to stop on — it scanned the whole scene, came back empty, and started over a second later.

It now exits immediately outside the star map, caches the ship once it finds it, and backs off
instead of retrying at full cost.

**Measured, same save, walking around:**

| | frame rate | guards | long frames (>100ms) per 10s |
|---|---|---|---|
| before | 24–37 fps | 0 | 7–14, in every single window |
| after | 36–38 fps | 5 | 0–3, most windows clean |

Note the guard count went *up* between those two rows. This never had anything to do with guards —
which is why "no guards hired and still stuttering" was the report that cracked it.

Two other things that follow, in case they match what you saw:

- It was on by default, so everyone paid it, whether or not they ever changed a ship model.
- Higher frame rates made it *worse*. A fixed-length hitch eats a much bigger share of the gap
  between 120 fps frames than between 40 fps ones.

## Also

The star map ship model swap now reports what it's doing in the log instead of staying silent
unless verbose logging was on. If it ever fails to apply, that's now visible rather than something
you'd have to guess at.

---

I got this wrong the first time. I had tested "guards dismissed" against "guards hired" and
concluded the mod was fine — but both sides of that test were paying the same hidden cost, so it
cancelled out, and I never measured against the mod being switched off. Several of you kept
reporting it anyway. That's what got it found.
