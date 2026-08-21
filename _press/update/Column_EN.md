# Dynasty Retinue — Appearance Update

> Three screenshots, inserted where marked.

---

This update does one thing: **it separates how a guard looks from what a guard is.**

Until now, picking a unit blueprint meant satisfying two demands at once — the stats had to be right *and* the model had to make sense. Those two pull against each other more often than you'd think. The clearest case was the Sniper line's elite, the Bounty Hunter. The model I wanted was a Footfall sniper, but that blueprint has 96 base HP while the other elite on the same line has 256 — a 2.7× gap on two guards who are supposed to be peers. The only unit with the right durability was a dual-blade melee cyber-assassin. So the Bounty Hunter stood in the sniper line holding two swords, because the alternative was an elite made of paper.

That trade-off is gone.

---

## Two approaches, each good at something different

### Borrow a model

Use another vanilla unit's model wholesale.

**〔Image 1: Krieg squad〕**

Five guards wearing the DLC3 Death Korps models. Note that they are **completely identical** — same respirator, same greatcoat, same pack, same stance. That is inherent to borrowing: the whole prefab comes across, individual variation included (which is to say, none).

That's the cost. What you get is zero clipping risk, absolute uniformity, and one useful side effect: **borrowed models never show worn equipment**. Those models are pre-baked, so gear visuals don't apply to them at all. If you want a disciplined, faceless death corps, this is the least work.

A borrowed style can also map a different model per line. Pick "Krieg" and you get:

```
Melee    → Watchmaster      Sniper  → Sniper
Suppress → Flamer           Psyker  → Engineer      Officer → Commissar
```

You don't configure that layer — choosing the style does it.

### Assemble from parts

Rebuild the guard out of character-creator components.

**〔Image 2: Kasrkin squad〕**

Also five guards, but look closely and **every face is different**. Heads, hair, brows and scars are borrowed from the five vanilla pregen characters, matched by gender and picked deterministically from each guard's unique ID — so a given guard has the same face every time you load.

The cost is that pieces are hand-picked and can clip. What you get in exchange is variation, fine control, and a choice about **whether worn equipment shows on top**. The shot above has gear visuals off, so the squad reads as uniform. Turn it on and each guard displays whatever they're actually wearing, which dilutes the shared look by roughly half.

Either way, **held weapons are never hidden**.

---

## Setting it up

**〔Image 3: Appearance tab〕**

There's a new **Appearance** tab in the retinue window (the same grid also lives in the UMM panel — edit either one).

It works like a paint brush: pick a style on the top row, then click cells to apply it. Click a row header to fill that row, a column header to fill that column, or "Apply to all" for everything. Cells matching the current brush are highlighted, so you can see at a glance what a click would change.

Five rows are the lines; four columns are T1 / T2 / T3 / Elite. Those three tier columns are **not three kinds of guard that coexist** — tier follows your own character's level, so read them as "what this line looks like as the campaign progresses". Want the melee line in Kasrkin early and Krieg later? Two cells.

Everything defaults to **Follow gear**, which is vanilla behaviour. **Installing this changes nothing until you decide it should.**

---

## Three things worth knowing

**Appearance is never written to your save.** The whole system only intercepts the moment a model is built; it doesn't touch a single persisted field. Uninstall the mod, or set the grid back to Follow gear, and the original look returns the next time that model is created. There is no cleanup step.

**Co-op appearance is deliberately not synced.** Because none of it enters the sync hash, two machines with different settings *cannot* desync over it. So you can run Krieg while your co-op partner runs vanilla looks, and neither of you affects the other.

**The style list is yours to edit.** `looks.json` defines each style as a set of vanilla asset IDs — change them, delete them, add your own. Hit "Reload styles" in the panel and it applies immediately, no restart.
