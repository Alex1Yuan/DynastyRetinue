# Dynasty Retinue & Refit

[中文](README.md) · [Release notes](CHANGELOG.md) · [Build from source](BUILDING.md) · [Download](https://www.nexusmods.com/warhammer40kroguetrader/mods/566)

Bring your dynasty into the field and into the void. Dynasty Retinue & Refit adds persistent AI ground guards, an AI escort fleet, and scrap-funded flagship refits to **Warhammer 40,000: Rogue Trader**.

Version **1.8.5** adds an optional **Guard friendly-fire protection** switch, off by default. Enabling it blocks allied guards' direct health damage to allies and suppresses their automatic psychic phenomena and Perils. An **Attributes only** growth mode is also available. It also fixes cached native attributes after loading and updates existing Sound Constitution bonuses during simplified growth.

An English narrated overview has been prepared for this release: **4 minutes 52.75 seconds, 4K at 60 fps, with 52 caption cues**. The completed render passed full audio/video decode checks. Its narration describes the protection switch as optional and off by default.

## Build a ground retinue

Recruit six archetypes, each with three progression tiers, plus **14 elite choices** across the roster. Guards follow the party and take their own combat turns under AI control.

| Archetype | Elite choices |
| --- | --- |
| Melee | Ironwall · Vanguard Captain; Bedrock · Arch-Militant |
| Sniper | The Silent Eye; Bounty · Headhunter |
| Gunner / Suppress | Wrath · Master Gunner; Sacred Flame · Purifier Sister |
| Psyker | Warp Arbiter; Pyre Executioner |
| Officer | Edict · Psyker Officer; Iron Law · Commissar |
| Mechanicus | Rust · Sicarian Ruststalker; Volt · Electro-Priest; Silent · High Magos; Heretical Forge · Heretek Magos |

The available tiers follow your protagonist's progression: tier two opens at level 16 and tier three at level 36. Default career caps are 15, 35 and 55. Elite choices have their own recruitment limits and unlock conditions; settings let you adjust the restrictions.

By default, each **15 Profit Factor unlocks one ground recruitment slot**, up to six slots at 90 PF. This is an eligibility check, not a deduction from your Profit Factor. Ground slots are separate from the escort fleet's budget and capacity.

Guards retain their names, experience, learned abilities and equipment in the game save. Names combine a personal name with a rank that changes as the guard advances. XP scaling and catch-up are configurable. Ordinary guards can die permanently; by default, elites can be downed and rescued. Morale and camera options give you more control over how allied turns affect the party and presentation.

## Mechanicus paths and servitors

The Mechanicus elite catalogue is divided into **Dogmatic** and **Heretek** recruitment groups with distinct builds and equipment:

- Dogmatic: Sicarian Ruststalker and High Magos.
- Heretek: Electro-Priest and Heretek Magos.

The branch combines ranged ordinary guards with melee specialists and Magos builds. Mechanicus guards recruited with full growth can receive a **once-per-battle servitor summon**. Lower-tier guards summon servitors; tier-three guards and elites use combat servitors. The mod removes its tracked summons after combat.

New Attributes-only recruits do not receive this added summon. Existing learned abilities are retained, and deliberately skipped initial grants are not restored by switching back to full growth.

## Choose how guards grow

The Progression section offers three configurations:

| Setting | Behaviour |
| --- | --- |
| Auto growth on, Attributes only off | **Default.** Apply the configured full career build as the guard advances. |
| Auto growth on, Attributes only on | Grant characteristic advances and equipment proficiencies while skipping new career abilities and other talents. |
| Auto growth off | Stop further career advancement. |

Attributes only is not a respec: already learned abilities, innate template abilities and equipment effects remain. Skipped ranks are not backfilled when full growth resumes. Select the mode **before recruiting** for a fully simplified new guard.

Automatic equipment issuance uses the configured tier or elite loadout and creates the issued gear without taking items from your stash. It can be disabled. Direct individual guard equipment editing is not currently implemented; loadouts are configurable through the supplied data.

In 1.8.5, native NPC-derived stat modifiers refresh after area restoration and when a guard returns from reserve. This applies in every growth mode. Existing Sound Constitution bonuses also update immediately after Attributes-only advancement.

## Manage the roster and appearance

Use Retinue Management to rename guards, dismiss them, or switch individual guards or the whole roster between **Reserve** and **Deploy**.

Reserve keeps the same guard, name, level and equipment while removing them from the field. Reserved guards still occupy recruitment and elite slots. The arrangement is saved with the game; loading an older save restores that save's arrangement. On the ground, deployed guards regroup near the protagonist. A deployment ordered outside a ground area takes effect on the next landing. Switching is unavailable during combat, while a guard is downed, or while their current action cannot be interrupted.

Appearance presets are separate from career builds. Assign looks by archetype, tier and elite category, including equipment-based appearances and configured uniform/model presets. A different look does not grant a different build. Availability depends on the installed game content and the supplied `looks.json` definitions.

The earlier regular-gunner movement fix is included: the mod removes that template's encounter-specific stationary effect from its own guards. Independent immobilization effects still apply.

## Optional guard friendly-fire protection — off by default

Enable **Guard friendly-fire protection** under **Combat & Behaviour** to turn on the combined protection. It is **off by default**. When enabled, player-aligned, non-traitor mod guards cannot directly remove health from friendly party members, other guards or allies through the protected damage pipeline. This includes burst and area damage that remains attributed to the guard. With this switch enabled, their ricochets exclude non-enemy targets.

When the same switch is enabled, these guards' psychic casts also skip automatic psychic phenomena and Perils of the Warp, including phenomena that would redirect to another target. Turning it off restores the native handling of these events and damage. The separate option preventing guard casts from increasing veil degradation remains independent. Player and enemy psykers retain their normal risks.

When protection is enabled, these limits still apply:

- Enemy damage, player-character attacks and a guard's own ability health costs retain their normal rules.
- The guard must remain the recorded damage source. Environmental chain reactions that acquire a different source are outside the guarantee.
- Non-damage status effects are outside the health-damage protection.

This passive combat setting must match on all co-op peers. The existing settings fingerprint and comparison report identify differences; they do not automatically change another player's setting. The 1.8.5 protections and restoration work use gameplay events; they add no per-frame scene scan.

## Refit your flagship

Visit the shipyard through the bridge advisor or the mod's ship controls. Spend scrap to move from the original flagship size to cruiser or grand-cruiser configurations, with configurable shields, armour, ramming distance and weapon firing bonuses.

Default total refit prices are **500 scrap for a cruiser** and **1,000 scrap for a grand cruiser**. Upgrading from cruiser to grand cruiser charges the remaining 500; downgrading refunds the price difference. Restoring the original ship is available through the same system.

The calibrated flagship options are the **Imperial Gothic cruiser** and an **enlarged Imperial Dictator hull used for the grand-cruiser refit**. The latter is a mod refit category, not a claim that the native Dictator model is a grand cruiser. Other experimental models are not equivalent to the calibrated shipyard choices. Larger-hull support includes weapon mounts, firing-arc and movement-preview adjustments.

Flagship refits and escort recruitment use separate management screens and separate hull catalogues.

## Recruit an escort fleet

Open **Retinue Management → Escort Fleet** to preview and recruit AI ships. Choose a class, then a hull; selecting a preview does not recruit or charge for a ship. The detail view shows the model, original equipment, slots and native abilities.

| Escort hull | Fleet category | Capacity | Default hull PF |
| --- | --- | ---: | ---: |
| Sword-class Frigate | Frigate | 1 | 10 |
| Prow Lance Frigate | Frigate | 1 | 10 |
| Imperial Gothic-class Cruiser | Cruiser | 2 | 20 |
| Pirate Dictator-class Cruiser | Cruiser | 2 | 20 |
| Imperial Universe-class Mass Conveyor | Grand cruiser | 3 | 30 |
| Chaos Battlecruiser | Grand cruiser | 3 | 30 |

Fleet capacity is **six**: one ship from each category uses all six points. Both frigate variants use the native Sword model, with different weapon layouts. The Universe escort uses its native transport hull, separately from the enlarged Dictator flagship refit.

Escorts retain their hull's native equipment and AI. The Pirate Dictator retains its low-health retreat behaviour. The Dictator and Chaos carrier hulls retain their native strike craft; strike craft are not part of the selectable refit catalogue.

### PF budget and refits

Fleet PF is **reserved budget**, not PF subtracted from the base game. The budget limit is your current Profit Factor. Hulls, refits and enhancements contribute to the total, independently of ground recruitment slots.

Default additional costs are:

- 2 PF per supported slot changed from the original loadout.
- 2 PF per extra shot, per affected weapon.
- 1 PF per extra range cell, per affected weapon.

Rates are configurable. If the fleet goes over budget because PF falls or rates increase, the existing fleet can still deploy. Dismissal, cost reductions and same-cost replacements remain available; cost-increasing edits must fit the current budget.

Each ship can be renamed, dismissed and refitted separately. Supported refits include selected AI-supported weapons and four main component types: **plasma drives, void shield generators, augur arrays and armour plating**. Candidates must fit the real slot and have been recorded as acquired; loading also records suitable items currently in inventory or on the flagship. Refits do not consume or transfer your inventory items. Restore Original remains available. Torpedoes, strike craft and hidden slots are outside selectable refits.

Cruisers and grand cruisers can receive extra shots and range within the supported limits. The interface displays the affected weapon count and PF cost. Hull previews are cached still images and are released when the window closes.

## Installation, saves and compatibility

Install with **Unity Mod Manager 0.23.0 or later**. Open UMM with **Ctrl+F10**, load a save, then open Retinue Management from the mod panel or the bridge advisor. English and Chinese interface text are included. Keep the four supplied JSON files beside the DLL. When upgrading from the old `KgdRetinue` installation, remove that old mod folder from UMM so only one installation loads.

Some unit templates, equipment and career paths depend on installed DLC. Configured fallback templates are used where available; not every choice has a fallback. Check the recruitment interface for availability.

Guards and their state are persistent. Reserve arrangements, growth decisions, escort roster/names/loadouts/enhancements and acquired ship-equipment records belong to the **individual game save**. Flagship refits are saved too. Save after making changes; loading an earlier save restores its earlier state. Mod preferences such as growth switches and PF rates remain in mod settings. The update archive excludes personal `Settings.xml`.

Co-op uses host-controlled guard and fleet transactions, including the progression option; passive combat settings such as the protection switch must match across peers. **Two-client validation has not been completed for this update.** The recorded native baseline covers single-player damage with protection enabled, twenty growth profiles, cold restart, existing HP bonuses and deployment restoration. The added optional gate has 36 production-class isolated checks. An actual-game main-menu smoke test confirmed default-off behavior with an older settings file missing the field, on/off persistence, unchanged veil settings, and complete English/Chinese help text. No save was loaded for that smoke test; it does not constitute a new combat run. Settings were restored afterward and original save hashes remained unchanged. The earlier progression-help and disabled Attributes-only UI checks cover the separate growth settings. Historical escort testing does not yet establish complete strike-craft attacks/return, carrier combat-save restoration, every muzzle position, or carrier end-of-battle cleanup coverage. Report problems with an exported diagnostic report and a save from before the issue.

Before disabling or uninstalling the mod, or removing DLC used by recruited units:

1. Dismiss all ground guards, including reserved guards.
2. Leave space combat and dismiss recruited escorts.
3. Restore the flagship to its original configuration.
4. **Save the game**, then disable or remove the mod.

Disabling the mod does not dismiss persistent entities. Keep a backup save; loading a pre-cleanup save restores the old retinue and refit state. Export Diagnostics in the UMM panel provides a compact report for troubleshooting.
