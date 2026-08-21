# `archetypes.json` — field reference (English)

The shipped `archetypes.json` documents itself through `_`-prefixed keys, but those are
Chinese-only. This file is the English equivalent. **The JSON is the single source of
truth** — this document explains it, it does not duplicate it.

Nothing here creates new blueprints. Every GUID below must be a **vanilla** blueprint;
the mod never invents AssetIds, because a mod AssetId written into a persisted typed
field makes the save unopenable without the mod.

---

## Top level

```jsonc
{
  "guardNamePool": [ ... ],   // given names shared by all lines
  "archetypes": [ ... ]       // exactly the five lines shown in the recruit panel
}
```

`guardNamePool` holds **given names only**. A recruit picks one that nobody is currently
using and is displayed as `<rank> · <given name>`. Promotion swaps the rank; the given
name stays for that guard's whole life and is released when they die or are dismissed.

---

## An archetype (one recruit line)

| Field | Meaning |
|---|---|
| `name` | Display name of the line. |
| `unit` | Blueprint for **normal** guards of this line. Decides model, base stats, innate abilities. |
| `unitFallback` | Tried in order when `unit` fails to resolve — normally because DLC3 is not installed. Four of the five lines use DLC3 blueprints, so without this the player just sees "I clicked recruit and nothing happened". |
| `brain` | AI brain override. Most vanilla guard brains have `UseOnlyListedAbilities = true`, so without an override the AI will not consider a single talent the career chain grants. |
| `plan` | RTAutoBuilder talent plan name (see `plans.json`). |
| `chain` | Career path chain, T1 → T2 → T3. `15 + 20 + 20 = 55`, the XP table maximum. |
| `grantFeatures` | Proficiencies granted **before** equipment is handed out. A missing proficiency surfaces as a bogus "slot rejected", not as a proficiency error — so order matters. |
| `gearT1` / `gearT2` / `gearT3` | Progressive gear sets for normal guards, tiered by item **Rarity** (Common → Pattern → Unique). `ItemLevel` is unusable in this game: two thirds of items report 0. |
| `guardNames` / `guardNames_en` | The three **rank titles** for this line (T1/T2/T3). Given names come from the shared pool. |
| `elites` | Elite entries for this line, see below. |

---

## An elite entry

| Field | Meaning |
|---|---|
| `unit` | Elite-only blueprint. |
| `unitFallback` | Same idea as the archetype-level one. Without it, a missing DLC3 elite silently degrades into a plain deck guard — functional, but visibly wrong. |
| `name` | Legacy display name; only used when `rank` is absent. |
| `rank` / `rank_en` | Fixed rank title. Elites use the same `<rank> · <given name>` scheme as normal guards; their rank simply never advances, because they already start at the top. |
| `plan` | Overrides the archetype's talent plan. |
| `chain` | Overrides the archetype's career chain. |
| `brain` | Overrides the archetype's brain. Worth setting when the elite is modelled on a specific NPC — that NPC's brain usually fits its ability set best. |
| `gear` | Graduation set, generated from nothing (elites only). Entries that fail to resolve are skipped silently. |
| `appearanceUnit` | **Borrow another blueprint's model** while keeping `unit`'s stats. Accepts a `a\|b\|c` fallback chain — the first GUID that resolves to a prefab wins, so a DLC-only first choice degrades to a base-game second choice instead of snapping back to the unrelated original model. Implemented by patching the `PrefabGuid` getter and never writing `m_CustomPrefabGuid`, so the save stays clean and removing the mod restores the original look with no player action. |
| `dualMelee` | Allow melee in both hands. Vanilla hard-locks anything carrying `CommonSpaceMarineFact` to "primary = ranged, secondary = melee"; this widens that rule **for this entry only**. |
| `preGrant` | Vanilla features granted **before** level-up. Needed for things a plan cannot express — e.g. psychic disciplines: without `Pyromancy_Base_Feature`, not one fire power appears in the selection pool. |
| `keyTalents` | Preferred picks for selection points the plan does not cover, instead of "take the first option". |
| `attrPriority` | Attribute preference, e.g. `["BallisticSkill", "Perception"]`. |
| `race` | Per-entity race override. Used when a plan's talents are race-gated. |
| `planSegments` | Assemble a plan per career segment: `pathGuid -> { bucket -> [plan names] }`. Buckets: `FirstCareer`, `SecondCareer`, `FirstOrSecondCareer`, `default`. Multiple sources per bucket are merged and tried in order — necessary when a guide only lists key picks. |
| `excludeFeatures` | Entries to drop when merging, e.g. companion-exclusive talents nobody else can take. |

---

## Two rules worth stating explicitly

**Elite identity lives in a tag, not in the blueprint.** It is stored in
`PartUnitDescription.CustomPetName` — a plain `[JsonProperty]` string that guards never
otherwise use. That is why several elites may share one unit blueprint. Older saves
without the tag fall back to blueprint matching.

**HP can only be measured, never inferred from a name.** `Ch05Inquisitor_Psyker_unit`
and `DLC3_DL_Inquisitor_Unit` sound like the same tier; measured, they are 1040 and 700.
Use the **Probe candidate units** button and read the numbers.

---

## `looks.json` — appearance styles

A separate file listing the appearance styles offered by the matrix in the UMM panel
(**Appearance** section). Rows are archetypes, columns are T1/T2/T3/Elite. The three
tier columns are **not** three coexisting kinds of guard: tier comes from the player's
level, so they read as "what this line looks like as the campaign progresses".

Two ways to define a style; `unit` wins if both are present:

| Field | Meaning |
|---|---|
| `id` | Key stored in the matrix. Required. |
| `name` / `name_en` | Display name. |
| `parts` | **Compose**: a list of `KingmakerEquipmentEntity` blueprint GUIDs assembled on the character-creation doll rig. Faces are borrowed from the vanilla pregen characters and picked by gender, so guards look different from one another. Downside: pieces are hand-picked and can clip. |
| `unit` | **Borrow**: a `BlueprintUnit` GUID whose whole model is used. Authentic, no clipping, and worn-gear visuals never show (baked models ignore them) — but every guard looks identical. |
| `unitByArchetype` | Per-archetype override for `unit`, keyed by the `name` in `archetypes.json`. Lets one style map e.g. the sniper line to a sniper NPC and the officer line to a commissar. |

Appearance is **presentation only**: nothing is written to the save and nothing enters
the co-op sync hash. Removing the mod, or setting the matrix back to "Follow gear",
restores the original look on the next view rebuild — no cleanup step required.

The "Show worn gear" toggle beneath the matrix only affects **compose** styles;
**held weapons are never hidden**, and borrowed-model styles ignore it entirely.
