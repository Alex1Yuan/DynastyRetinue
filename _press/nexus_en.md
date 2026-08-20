# Dynasty Retinue & Refit

**A personal retinue that fights for you, and a voidship you can actually rebuild.**

---

## What it does

Rogue Traders command a dynasty. You should not be walking into a firefight with
five companions and nothing else.

This mod gives you a **personal guard** — AI-controlled soldiers who join your party,
grow along real career paths, equip real gear, and fight entire battles on their own.
And it lets you **refit your voidship**: frigate → cruiser → grand cruiser, with the
hull silhouette actually changing.

Everything is built from **vanilla blueprints**. No custom units, no custom AI brains,
no invented items.

---

## The retinue

**Five archetypes, three career tiers each.**

| Archetype | Role |
|---|---|
| Melee | Closes distance, holds the line |
| Sniper | Marks targets, kills from range |
| Suppression | Heavy stubber / autocannon / heavy bolter, sustained fire |
| Psyker | Warp powers, executions |
| Officer | Buffs, commands, keeps the squad moving |

Guards **level with you** (80% of your XP by default, with an optional catch-up curve
so a late recruit isn't dead weight), advance through **real Owlcat career paths**, and
are **equipped automatically** by tier — including implants, which upgrade as your
story-gated implant tier rises.

**Ten elites.** Each archetype has two, unlocked once you've grown a guard to tier 3 on
that line. They're built from named vanilla units — an Arbites squad leader, a Battle
Sister, an Inquisitorial psyker — with their own titles: *Ironwall Warden*, *The Silent Eye*,
*Wrathgun Master*, *Sacred Flame*, *Warp Arbiter*, *Iron Law Commissar*, and more.

**They can die.** Regular guards die permanently. Elites get a downed-instead-of-dead
reprieve. Recruitment slots are gated by **Profit Factor**, so your retinue grows as your
dynasty does.

**The camera stays put.** With five extra units acting each round, a camera that chases
every one of them is nauseating. By default it doesn't — you watch the fight from where
you want. (Toggleable.)

---

## The voidship

Spend salvage to refit your ship. **The hull model actually changes** — eleven vanilla
ship models are available, from Sword-class frigates to a Chaos battlecruiser.

| | Cruiser (2×4) | Grand Cruiser (3×6) |
|---|---|---|
| Shield capacity | +50% | +100% |
| Armour (damage reduction) | +50% | +100% |
| Ramming distance | speed × 100% | speed × 200% |
| Weapon range | +3 | broadside +3 / prow +5 |
| Shots per turn | broadside +1 | broadside +2 / prow +1 |

All values are sliders in the panel. Refits are **refundable** — revert and get your
salvage back at the same rate.

**Bug fix included:** vanilla reads `ArmourFore` for all four facings in the ship
management screen. This mod takes that display over and shows the real per-facing values
from the same source the damage calculation uses.

---

## Requirements

- **Unity Mod Manager 0.23.0+**
- **No DLC required.** Four of the five archetypes use unit templates from DLC3
  (Dark Legacy); without it they fall back to base-game templates. Careers, gear, AI and
  progression are unaffected — only the model and innate abilities change to match the
  corresponding elite. The panel tells you when this happens.
- One elite (Sacred Flame) and one tier-2 career path (Pyre Executioner, DLC1) are
  unavailable without their DLC.

---

## Installation

> **Upgrading from a pre-1.0 build?** The folder used to be called `KgdRetinue`.
> Extracting the new version only *adds* a `DynastyRetinue` folder — it does not replace
> the old one, because UMM loads by "does this subfolder contain Info.json", not by name.
> **Delete or move the old folder out** of the UnityModManager directory. Renaming it is
> not enough. Your saved guards are unaffected.

1. Install Unity Mod Manager (0.23.0 or newer)
2. Extract the archive into your UnityModManager folder
3. Launch, open the UMM panel (`Ctrl+F10`), find **Dynasty Retinue & Refit**
4. Load a save — most features need an active game

---

## ⚠️ Before you uninstall

Guards are **persistent entities** written into your save's `party.json`. So is a refitted
ship. Turning the mod off doesn't delete them — it leaves them in the save while removing
the code that handles them.

**Two minutes of cleanup:**

1. UMM panel → top of the page → **Dismiss All**, confirm the log says
   `復查在册 0 / roster verified 0, cleanup complete`
2. If you refitted the ship: **Ship** section → **Revert to Original Hull**
3. **Save the game**
4. *Then* disable the mod

Step 3 matters — the first two only happen in memory.

### What happens if you don't — all three paths tested

| | Save opens | Consequence | Fixable |
|---|---|---|---|
| Cleaned up, then uninstalled | ✅ | Nothing left behind | — |
| Uninstalled without cleaning | ✅ **still opens** | Guards become frozen, uncontrollable NPCs; a refitted ship renders as **solid magenta** in the management screen | ⚠️ No way to fix while uninstalled — but reinstalling fixes it completely |
| Made the mistake, then recovered | ✅ | **Fully recoverable** | ✅ Reinstall → dismiss + revert → save |

**Your save is never corrupted.** Everything this mod writes to a save is a plain string
or a vanilla enum — never a mod-authored blueprint in a typed field, which is the one
thing that actually makes a Rogue Trader save unopenable.

Skipping the cleanup leaves a mess you can't clear *while the mod is gone*, but it is
fully reversible: reinstall, clean up properly, uninstall.

---

## Compatibility

Tested alongside **ToyBox** and **RTAutoBuilder**. All Harmony patches are gated on
"is this one of my guards" and fall through to vanilla on any exception.

If you use RTAutoBuilder, its build plans are picked up automatically as extra
progression templates.

---

## Full feature list

**The retinue**

- Five archetypes × three career tiers, using real vanilla career paths
- Ten elites, two per line, unlocked by growing a tier-3 guard on that line
- **Regular guards die permanently.** Elites get downed instead of killed at 0 HP
- Recruitment slots gated by **Profit Factor**, with a tier table showing how far you
  are from the next slot
- XP follows the Rogue Trader (80% by default), with an optional **catch-up curve** —
  the further behind a guard is, the faster it gains, but it never overtakes you
- **Automatic gear by tier**, including implants, which upgrade as your story-gated
  implant tier rises
- The gear tables are JSON (`archetypes.json`) — **edit them yourself**. Entries that
  can't be resolved are silently skipped

**In combat**

- **The camera doesn't chase your guards.** Five extra units acting each round means a
  camera that follows every one of them is nauseating. Off by default; your own party's
  camera work is untouched. Toggleable
- Guards have their **own momentum pool**, separate from your party's
- Kills are correctly credited to the guard, and your party's momentum gets what it should
- A guard dying does **not** grant the enemy momentum under vanilla's "ally killed" tier
- Guard actions **don't accumulate warp veil** — five AI units spamming abilities every
  round would make the veil spiral under vanilla rules
- A guard's own equipment is **never dumped into your stash** by the shared-inventory system

**The voidship**

- Eleven vanilla hull models, and the silhouette actually changes
- Six bonus categories (shields / armour / range / ramming / shots per turn), all on sliders
- Refits are **refundable** at the same rate
- **Missing weapon mounts are synthesised.** Some hulls have no prow socket — without
  this, lances and torpedoes "fire from the void" (the art can't attach, so it fires
  from the origin)
- **Mount contention arbitration.** When two weapons of the same class want the same
  socket, vanilla destroys-then-rebuilds and the winner depends on iteration order.
  This orders them lance > nova cannon > macrobattery > torpedo, so the prow reliably
  shows the lance
- Fixes vanilla reading `ArmourFore` for all four facings in the ship management screen

**Quality of life**

- Three recruitment entry points — click an NPC, an injected dialogue option, or open the
  window from the panel — each independently toggleable
- English and Chinese, following your game language, switchable in-panel without a restart
- **One-click diagnostic bundle**: version, your changed settings, roster state, ship
  state and the log tail packed into a single file with the username stripped. Far
  smaller and far more complete than sending a raw log
- Destructive actions (Dismiss All, Revert Hull) require **two clicks** to confirm

---

## Language

English and Chinese. Follows your game language automatically; switchable in the panel
without restarting.

---

## License

MIT. You may modify and redistribute — including modpacks, translations, and forks —
as long as you keep the attribution and state where it came from.

Source: https://github.com/Alex1Yuan/DynastyRetinue
