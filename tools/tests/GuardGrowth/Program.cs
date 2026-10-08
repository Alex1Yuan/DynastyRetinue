using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DynastyRetinue;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats.Base;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.FactLogic;
using Kingmaker.UnitLogic.Levelup.Selections;
using Kingmaker.UnitLogic.Levelup.Selections.Feature;
using Kingmaker.UnitLogic.Progression.Features;
using Kingmaker.UnitLogic.Progression.Features.Advancements;
using Kingmaker.UnitLogic.Progression.Paths;

internal static class Program
{
    private static int checks, serial;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; }
    private static string Id() { return (++serial).ToString("x32"); }
    private static T Bp<T>(T bp) where T : BlueprintFeature
    { if (bp.AssetGuid == null) bp.AssetGuid = Id(); ResourcesLibrary.All[bp.AssetGuid] = bp; return bp; }
    private static BlueprintFeature Attr(string name, StatType stat)
    { return Bp(new BlueprintFeature { name = name, ComponentsArray = new BlueprintComponent[] { new AddStatBonus { Stat = stat, Value = 5 } } }); }
    private static BlueprintAttributeAdvancement CareerAttr(string id, string name, StatType stat)
    { return Bp(new BlueprintAttributeAdvancement { AssetGuid = id, name = name, TestStat = stat,
        ComponentsArray = new BlueprintComponent[] { new StatAdvancement() } }); }
    private static BlueprintFeature Skill(string name)
    { return Bp(new BlueprintFeature { name = name, ComponentsArray = new BlueprintComponent[] { new DangerousComponent() } }); }
    private static BaseUnitEntity Guard()
    { return new BaseUnitEntity { UniqueId = "guard-" + Id(), Marked = true }; }
    private static BlueprintCareerPath Path(int count, params BlueprintSelectionFeature[] selections)
    {
        var path = Bp(new BlueprintCareerPath { Ranks = count, name = "dangerous-path",
            ComponentsArray = new BlueprintComponent[] { new DangerousComponent() } });
        path.RankEntries = Enumerable.Range(0, count).Select(x => new BlueprintPath.RankEntry { Selections = selections }).ToArray();
        return path;
    }
    private static BlueprintSelectionFeature Selection(FeatureGroup group, params BlueprintFeature[] options)
    { return new BlueprintSelectionFeature { Group = group, Options = options, MaxRank = 100 }; }
    private static int Grow(BaseUnitEntity g, BlueprintCareerPath p, int cap = 55, BuildPlans.Plan plan = null,
        string[] priority = null, string[] pre = null)
    { return GuardGrowth.Apply(g, new[] { p.AssetGuid }, 1, cap, plan, priority, pre); }

    // Never allow an unhandled test exception to launch Windows JIT debugging.
    private static int Main()
    {
        try { Run(); Console.WriteLine("GuardGrowth production-class/Harmony regression: " + checks + " passed"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run()
    {
        new Harmony("test.guardgrowth").PatchAll(Assembly.GetExecutingAssembly());
        var strength = Attr("StrengthStatAdvancement1", StatType.WarhammerStrength);
        var agility = Attr("AgilityStatAdvancement1", StatType.WarhammerAgility);
        var active = Skill("active"); var trigger = Skill("trigger");
        var mixed = Attr("looks-like-an-attribute", StatType.WarhammerStrength);
        mixed.ComponentsArray = mixed.ComponentsArray.Concat(new BlueprintComponent[] { new DangerousComponent() }).ToArray();
        var skillBonus = Attr("skill-bonus", StatType.SkillLogic);
        Check(GuardGrowth.IsPureAttribute(strength), "Pure characteristic accepted");
        Check(!GuardGrowth.IsPureAttribute(mixed), "Mixed trigger rejected");
        Check(!GuardGrowth.IsPureAttribute(skillBonus), "Non-characteristic skill bonus rejected");
        Check(!GuardGrowth.IsPureAttribute(active), "Active ability rejected");
        var inherited = Bp(new BlueprintFeature { Inherited = new BlueprintComponent[] { new DangerousComponent() }, ComponentsArray = strength.ComponentsArray });
        Check(!GuardGrowth.IsPureAttribute(inherited), "Inherited trigger rejected");
        var zero = Attr("negative", StatType.WarhammerStrength); ((AddStatBonus)zero.ComponentsArray[0]).Value = -5;
        Check(!GuardGrowth.IsPureAttribute(zero), "Negative/non-improvement rejected");

        // Regression for the native failure: these exact GUIDs/types/components occur in the
        // Fighter Attribute pool in blueprints-pack.bbp; the original legacy-only predicate
        // rejected EVERY one of them, so 15/35/55 levels added no characteristic points.
        var careerStrength = CareerAttr("43305eb5a6a1412a81e0d423798fcbdc", "StrengthAttributeAdvancement", StatType.WarhammerStrength);
        var careerWeapon = CareerAttr("1cff3a42d0ea4695988833f55e0867ae", "WeaponSkillAttributeAdvancement", StatType.WarhammerWeaponSkill);
        var careerSkill = Bp(new BlueprintSkillAdvancement { name = "LogicSkillsAdvancement",
            ComponentsArray = new BlueprintComponent[] { new StatAdvancement() } });
        Check(GuardGrowth.IsPureAttribute(careerStrength) && GuardGrowth.IsPureAttribute(careerWeapon), "Native career attribute type/component accepted");
        Check(!GuardGrowth.IsPureAttribute(careerSkill), "Native skill advancement excluded despite shared component");
        var mixedCareer = CareerAttr(Id(), "mixed-career", StatType.WarhammerStrength);
        mixedCareer.ComponentsArray = new BlueprintComponent[] { new StatAdvancement(), new DangerousComponent() };
        Check(!GuardGrowth.IsPureAttribute(mixedCareer), "Native characteristic with trigger rejected");
        var missingComponent = CareerAttr(Id(), "missing-component", StatType.WarhammerStrength);
        missingComponent.ComponentsArray = new BlueprintComponent[0];
        Check(!GuardGrowth.IsPureAttribute(missingComponent), "Attribute blueprint with no effect is not accepted");
        var misplacedComponent = Bp(new BlueprintFeature { ComponentsArray = new BlueprintComponent[] { new StatAdvancement() } });
        Check(!GuardGrowth.IsPureAttribute(misplacedComponent), "StatAdvancement requires matching attribute blueprint");
        var careerSelection = Selection(FeatureGroup.Attribute, careerWeapon, careerStrength, careerSkill, mixedCareer);
        careerSelection.MaxRank = 2;
        var career = Path(6, careerSelection);
        var nativeGuard = Guard();
        var nativePlan = new BuildPlans.Plan();
        nativePlan.Sel[career.AssetGuid] = new Dictionary<int, List<string>> { { 1, new List<string> { careerStrength.AssetGuid } } };
        Check(Grow(nativeGuard, career, 3, nativePlan, new[] { "Strength" }) == 3, "Native pool progresses levels");
        Check(nativeGuard.Progression.Features.Get(careerStrength)?.Rank == 2
            && nativeGuard.Progression.Features.Get(careerWeapon)?.Rank == 1,
            "Native plan and AttributeAdvancement preference are honored, then rank cap selects another attribute");
        Check(nativeGuard.Progression.Features.RawFacts.Where(f => f.Blueprint is BlueprintAttributeAdvancement)
            .Sum(f => f.Rank * ((BlueprintAttributeAdvancement)f.Blueprint).ValuePerRank) == 15,
            "Native attribute selections contribute 15 characteristic points, not just levels");
        Check(!nativeGuard.Facts.Contains(careerSkill) && !nativeGuard.Facts.Contains(mixedCareer), "Native pool never grants skill or mixed feature");
        Check(Grow(nativeGuard, career, 6) == 3 && nativeGuard.Progression.Features.Get(careerWeapon).Rank == 2
            && nativeGuard.Progression.Features.Get(careerStrength).Rank == 2,
            "Native per-selection max rank prevents overgrant after candidates exhausted");
        Check(Grow(nativeGuard, career, 6) == 0 && nativeGuard.Progression.Features.RawFacts
            .Where(f => f.Blueprint is BlueprintAttributeAdvancement).Sum(f => f.Rank) == 4,
            "Native repeat does not re-add characteristic ranks");

        var armor = Bp(new BlueprintFeature { ComponentsArray = new BlueprintComponent[] { new AddProficiencies(), new HideFeatureInEnemyUnitInspect() } });
        var weapon = Bp(new BlueprintFeature { AssetGuid = "26376b596c474d67849613617ced7b04", ComponentsArray = new BlueprintComponent[] { new HideFeatureInEnemyUnitInspect() } });
        Check(GuardGrowth.IsProficiency(armor), "Armor permission retained");
        Check(GuardGrowth.IsProficiency(weapon), "Weapon marker permission retained");
        var fakePermission = Bp(new BlueprintFeature { name = "HeavyWeaponProficiency", ComponentsArray = new BlueprintComponent[] { new HideFeatureInEnemyUnitInspect() } });
        Check(!GuardGrowth.IsProficiency(fakePermission), "Permission names do not bypass checks");
        var unsafeArmor = Bp(new BlueprintFeature { ComponentsArray = new BlueprintComponent[] { new AddProficiencies(), new DangerousComponent() } });
        Check(!GuardGrowth.IsProficiency(unsafeArmor), "Mixed proficiency/trigger rejected");

        var attrs = Selection(FeatureGroup.Attribute, active, mixed, skillBonus, strength, agility);
        var talents = Selection(FeatureGroup.Talent, trigger, strength);
        var skills = Selection(FeatureGroup.Skill, skillBonus);
        var p = Path(5, attrs, talents, skills);
        foreach (var e in p.RankEntries) e.Features = new[] { active, trigger, armor };
        var plan = new BuildPlans.Plan();
        plan.Sel[p.AssetGuid] = new Dictionary<int, List<string>> { { 1, new List<string> { active.AssetGuid, agility.AssetGuid } } };
        var g = Guard(); g.Progression.ExperienceLevel = 3;
        var old = g.Progression.Features.Add(Skill("veteran-existing"));
        var oldCount = g.Facts.Items.Count;
        g.Facts.GiftOnGain = Skill("conditional-gain-fact-passive");
        Check(Grow(g, p, 4, plan, new[] { "Strength" }, new[] { active.AssetGuid, weapon.AssetGuid }) == 3,
            "Growth bounded by earned XP");
        Check(g.Progression.CharacterLevel == 3 && g.Progression.GetPathRank(p) == 3, "Level and rank agree");
        Check(g.Progression.Features.Get(agility).Rank == 1 && g.Progression.Features.Get(strength).Rank == 2,
            "Plan wins then attribute preference applies");
        Check(g.Progression.NativeCalls == 0, "Never invokes unsafe AddPathRank");
        Check(!g.Progression.Features.Get(p).IsActive, "New career is inactive despite dangerous components");
        Check(!g.Facts.Contains(active) && !g.Facts.Contains(trigger) && !g.Facts.Contains(skillBonus), "No skill/trigger facts granted");
        Check(!g.Facts.Contains(g.Facts.GiftOnGain), "Real Harmony prefix isolates nested fact-gain grants");
        Check(g.Facts.Items.Contains(old) && old.Rank == 1, "Old facts preserved");
        Check(g.Facts.Contains(weapon) && g.Progression.Features.Get(armor).Rank == 1, "Required proficiencies retained idempotently");
        Check(g.Health.HitPoints.Updates == 3, "HP refreshed without rank event");
        Check(g.Progression.Selections.Count == 3, "Only selected attributes have native selection history");
        int count = g.Facts.Items.Count, strengthRank = g.Progression.Features.Get(strength).Rank;
        Check(Grow(g, p, 4, plan) == 0 && g.Facts.Items.Count == count && g.Progression.Features.Get(strength).Rank == strengthRank,
            "Repeated event does not add attributes again");
        g.Progression.ExperienceLevel = 55;
        Check(Grow(g, p, 4) == 1 && g.Progression.CharacterLevel == 4, "Configured level cap enforced");
        Check(Grow(g, p, 2) == 0 && g.Progression.CharacterLevel == 4, "Lower cap never removes old growth");
        Check(GuardGrowth.Skipped(g, p.AssetGuid, 1) && !GuardGrowth.Skipped(g, p.AssetGuid, 5), "Exact consumed ranks journaled");

        // P2: Sound Constitution caches an HP modifier until its specific rank callback runs.
        // UpdateValue alone left 8 HP after 15->35, then cold-load recalculated it to 18.
        var toughness = new ToughnessLogic();
        var constitution = Bp(new BlueprintFeature { AssetGuid = "e31f7f8497b444eb9566969b403b6070",
            name = "SoundConstitution_Feature", ComponentsArray = new BlueprintComponent[] { toughness, new DangerousComponent() } });
        var hpGuard = Guard(); var hpPath = Path(55);
        var constitutionFact = hpGuard.Progression.Features.Add(constitution);
        var constitutionRuntime = constitutionFact.Components[0];
        int activations = constitutionFact.Activations;
        hpGuard.Facts.GiftOnGain = Skill("no-rank-broadcast-or-new-facts");
        Check(Grow(hpGuard, hpPath, 15) == 15 && hpGuard.Health.HitPoints.Modifiers[constitutionRuntime] == 8,
            "Existing Sound Constitution refreshes to 8 HP at level 15");
        Check(Grow(hpGuard, hpPath, 35) == 20 && hpGuard.Health.HitPoints.Modifiers[constitutionRuntime] == 18,
            "Existing Sound Constitution refreshes 8->18 HP immediately at level 35");
        Check(toughness.Refreshes == 2, "Existing ToughnessLogic refreshed once per growth batch, not per rank");
        Check(Grow(hpGuard, hpPath, 55) == 20 && hpGuard.Health.HitPoints.Modifiers[constitutionRuntime] == 28,
            "Existing Sound Constitution reaches 28 HP at level 55");
        Check(hpGuard.Progression.NativeCalls == 0 && !hpGuard.Facts.Contains(hpGuard.Facts.GiftOnGain)
            && constitutionFact.Activations == activations && constitutionFact.Rank == 1,
            "Targeted HP refresh broadcasts no rank events and never reactivates or regrants old fact");
        Check(Grow(hpGuard, hpPath, 55) == 0 && toughness.Refreshes == 3,
            "No-growth repeat does not rerun ToughnessLogic");
        Check(GuardGrowth.RefreshExistingToughness(hpGuard) == 1 && hpGuard.Health.HitPoints.Modifiers.Count == 1
            && hpGuard.Health.HitPoints.Modifiers[constitutionRuntime] == 28,
            "Reflectable refresh replaces its own modifier without stacking");
        constitutionFact.CallComponentsWithRuntime<ToughnessLogic>((logic, runtime) => logic.HandleUnitGainPathRank(null));
        Check(hpGuard.Health.HitPoints.Modifiers[constitutionRuntime] == 28, "Postload-equivalent native callback leaves same HP modifier");
        Check(!GuardGrowth.IsPureAttribute(constitution), "ToughnessLogic remains excluded from newly granted pure-attribute facts");
        var noToughness = Guard();
        Check(GuardGrowth.RefreshExistingToughness(noToughness) == 0 && noToughness.Facts.Items.Count == 0,
            "Refresh never creates a missing ToughnessLogic fact");
        constitutionFact.IsActive = false;
        Check(GuardGrowth.RefreshExistingToughness(hpGuard) == 0, "Inactive fact not awakened for HP refresh");
        constitutionFact.IsActive = true; toughness.Disabled = true;
        Check(GuardGrowth.RefreshExistingToughness(hpGuard) == 0, "Disabled ToughnessLogic skipped");
        toughness.Disabled = false; constitutionRuntime.IsDisposed = true;
        Check(GuardGrowth.RefreshExistingToughness(hpGuard) == 0, "Disposed runtime skipped");
        constitutionRuntime.IsDisposed = false; hpGuard.Marked = false;
        Check(GuardGrowth.RefreshExistingToughness(hpGuard) == 0, "Non-guard HP modifiers untouched");
        hpGuard.Marked = true;
        var partial = Guard(); var partialToughness = new ToughnessLogic();
        var partialFact = partial.Progression.Features.Add(Bp(new BlueprintFeature { ComponentsArray = new BlueprintComponent[] { partialToughness } }));
        var partialPath = Path(3); partialPath.RankEntries[1] = null;
        Check(Grow(partial, partialPath, 3) == 1 && partialToughness.Refreshes == 1
            && partial.Health.HitPoints.Modifiers[partialFact.Components[0]] == 1,
            "Early return after successful rank still refreshes cached HP once");

        // The real cold/fresh sample has identical career/equipment Facts, but MobStatManager's
        // nine cached Difficulty modifiers were calculated with no CurrentlyLoadedArea at load.
        var mobGuard = Guard(); var mobLogic = new MobStatManager();
        var mobFact = mobGuard.Progression.Features.Add(Bp(new BlueprintFeature { AssetGuid = "ae9bfd6223f64a81b3fb4bc5b964d044",
            name = "TroopMobFeature", ComponentsArray = new BlueprintComponent[] { mobLogic, new DangerousComponent() } }));
        var mobRuntime = mobFact.Components[0];
        int[] coldMob = { 2, -3, 2, -3, -3, -8, -3, -3, 7 };
        int[] loadedMob = { 8, 2, 19, -3, -3, -8, -3, 2, 13 };
        int[] careerAndGear = { 30, 0, 0, 0, 0, 10, 0, 0, 15 };
        int[] expectedCold = { 57, 22, 27, 22, 22, 32, 22, 22, 42 };
        int[] expectedLoaded = { 63, 27, 44, 22, 22, 32, 22, 27, 48 };
        Func<int[]> mobStats = () => mobGuard.BaseAttributes.Select((v, i) => v + mobGuard.DifficultyModifiers[mobRuntime][i] + careerAndGear[i]).ToArray();
        Game.Instance.CurrentlyLoadedArea = null;
        mobFact.CallComponentsWithRuntime<MobStatManager>((logic, runtime) => logic.HandleDifficultyChanged());
        Check(mobGuard.DifficultyModifiers[mobRuntime].SequenceEqual(coldMob) && mobStats().SequenceEqual(expectedCold),
            "Fixture reproduces all nine observed cold-load values from no-area native formulas");
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 0 && mobLogic.Refreshes == 1,
            "Refresh waits for area readiness rather than caching CR0 again");
        Game.Instance.CurrentlyLoadedArea = new TestArea { CR = 15 };
        int mobActivations = mobFact.Activations;
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 1 && mobGuard.DifficultyModifiers[mobRuntime].SequenceEqual(loadedMob)
            && mobStats().SequenceEqual(expectedLoaded), "Targeted refresh restores all nine observed fresh values");
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 1 && mobGuard.DifficultyModifiers.Count == 1
            && mobStats().SequenceEqual(expectedLoaded), "Repeated Mob refresh replaces only its own modifiers");
        DynastyRetinue.Main.Settings.GuardAttributesOnly = false;
        mobGuard.DifficultyModifiers[mobRuntime] = coldMob;
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 1 && mobStats().SequenceEqual(expectedLoaded),
            "Existing full-growth guard gets the same native cache correction");
        DynastyRetinue.Main.Settings.AutoLevelUp = false;
        mobGuard.DifficultyModifiers[mobRuntime] = coldMob;
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 1 && mobStats().SequenceEqual(expectedLoaded),
            "AutoLevelUp off does not disable native stat restoration");
        Check(mobFact.Activations == mobActivations && mobGuard.Facts.Items.Count == 1 && mobFact.Rank == 1
            && careerAndGear.SequenceEqual(new[] { 30, 0, 0, 0, 0, 10, 0, 0, 15 }),
            "Mob correction does not reapply facts, grant skills, or touch career/equipment bonuses");
        DynastyRetinue.Main.Settings.GuardAttributesOnly = true; DynastyRetinue.Main.Settings.AutoLevelUp = true;
        mobFact.IsActive = false;
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 0, "Inactive Mob fact is not activated");
        mobFact.IsActive = true; mobLogic.Disabled = true;
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 0, "Disabled Mob component skipped");
        mobLogic.Disabled = false; mobRuntime.IsDisposed = true;
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 0, "Disposed Mob runtime skipped");
        mobRuntime.IsDisposed = false; mobGuard.Marked = false;
        Check(GuardGrowth.RefreshExistingMobStats(mobGuard) == 0, "Ordinary enemies and companions receive no correction");
        Check(GuardGrowth.RefreshExistingMobStats(Guard()) == 0, "Units without MobStatManager get no new Fact");

        // Stored native inactive flag and journal survive a save/load simulation. Native AddFacts
        // only restores children on PostLoad for active paths; exercise the same dangerous hook.
        var saved = Kingmaker.Game.Instance.State.InGameSettings.List;
        var encoded = Newtonsoft.Json.JsonConvert.SerializeObject(saved);
        Kingmaker.Game.Instance.State.InGameSettings.List = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(encoded);
        g.Facts.PostLoad();
        Check(!g.Facts.Contains(EntityFactsManager.PathAbility), "Inactive path does not grant on load");
        Check(GuardGrowth.Skipped(g, p.AssetGuid, 1), "Skipped history survives JSON reload");
        DynastyRetinue.Main.Settings.GuardAttributesOnly = false;
        Check(GuardGrowth.RestrictBootstrap(g), "Full mode does not catch up skipped bootstrap talents");
        // Native subsequent full rank does not activate an already inactive path.
        g.Progression.AddPathRank(p);
        Check(g.Progression.GetPathRank(p) == 5 && !g.Facts.Contains(EntityFactsManager.PathAbility), "Future full rank keeps skipped career bootstrap inactive");
        Check(GuardGrowth.Skipped(g, p.AssetGuid, 4) && !GuardGrowth.Skipped(g, p.AssetGuid, 5), "Future full rank is not excluded by journal");
        var freshFull = Guard();
        Check(!GuardGrowth.RestrictBootstrap(freshFull), "Original complete mode bootstrap unchanged for fresh guard");
        freshFull.Progression.AddPathRank(p);
        Check(freshFull.Facts.Contains(EntityFactsManager.PathAbility), "Full native path still activates normally");

        DynastyRetinue.Main.Settings.GuardAttributesOnly = true;
        var oldPath = freshFull.Progression.Features.Get(p);
        int oldActivations = oldPath.Activations;
        var veteranFacts = freshFull.Facts.Items.ToArray();
        Check(Grow(freshFull, p, 3) == 2 && oldPath.IsActive && oldPath.Activations == oldActivations,
            "Existing active career advances without deactivation/reactivation");
        Check(veteranFacts.All(x => freshFull.Facts.Items.Contains(x)), "All veteran fact identities retained");
        var elite = Guard();
        var arch = new ChainProbe.Archetype { GrantFeatures = new[] { active.AssetGuid, armor.AssetGuid }, PreGrant = new[] { unsafeArmor.AssetGuid },
            GrantFeaturesTier = new[] { new[] { trigger.AssetGuid }, new[] { weapon.AssetGuid } },
            Elite = new ChainProbe.EliteDef { PreGrant = new[] { active.AssetGuid, weapon.AssetGuid } } };
        var retained = elite.Progression.Features.Add(trigger);
        Check(GuardGrowth.GrantGearProficiencies(elite, arch, 2) == 2, "Gear and elite preGrant preserve permissions only");
        Check(elite.Facts.Items.Contains(retained) && !elite.Facts.Contains(active), "Tier switch never deletes veteran facts or grants abilities");
        Check(GuardGrowth.GrantGearProficiencies(elite, arch, 2) == 0, "Gear grants repeat safely");
        Check(GuardGrowth.RestrictBootstrap(elite), "Standalone gear call records bootstrap restriction");

        var enemy = Guard(); enemy.Marked = false;
        Check(Grow(enemy, p) == 0 && enemy.Facts.Items.Count == 0, "Ordinary enemies/companions unaffected");
        DynastyRetinue.Main.Settings.AutoLevelUp = false;
        var stopped = Guard(); Check(Grow(stopped, p) == 0 && stopped.Facts.Items.Count == 0, "AutoLevelUp false stops chain");
        DynastyRetinue.Main.Settings.AutoLevelUp = true;
        var pet = Guard(); pet.Master = Guard(); Check(Grow(pet, p) == 0, "Master-level proxy cannot loop growth");
        var giant = Path(70); var high = Guard(); high.Progression.ExperienceLevel = 90;
        Check(Grow(high, giant, 100) == 55 && high.Progression.CharacterLevel == 55, "Hard ceiling bounded at 55");
        var missing = Path(2); missing.IsAvailable = false;
        Check(Grow(Guard(), missing) == 0, "Missing DLC path skipped safely");
        var malformed = Path(3); malformed.RankEntries = new BlueprintPath.RankEntry[1];
        Check(Grow(Guard(), malformed) == 0, "Invalid path rank table rejected");
        var mismatch = Guard(); mismatch.Progression.SetTestLevel(4);
        Check(Grow(mismatch, p) == 0 && mismatch.Facts.Items.Count == 0, "Inconsistent legacy level/rank cannot overrun cap");

        var bad = Guard(); var values = Kingmaker.Game.Instance.State.InGameSettings.List;
        values[GuardGrowthStore.Prefix + bad.UniqueId] = "{broken";
        Check(Grow(bad, p) == 0 && bad.Facts.Items.Count == 0, "Corrupt journal stops growth");
        Check(GuardGrowth.Skipped(bad, p.AssetGuid, 1), "Corrupt journal blocks catch-up");
        Check(!GuardGrowthStore.Mark(values, bad.UniqueId, p.AssetGuid, 1), "Corrupt journal is never overwritten");
        var prior = new Dictionary<string, object>();
        Check(!GuardGrowthStore.HasHistory(prior, g.UniqueId), "Earlier save does not inherit later process state");
        Check(!GuardGrowthStore.Skipped(prior, g.UniqueId, p.AssetGuid, 1), "Earlier save retains unconsumed rank");
        Check(!GuardGrowthStore.Mark(prior, g.UniqueId, p.AssetGuid, 56), "Journal enforces rank cap");

        // Restore the notification scope even when selection lookup throws.
        var throwing = Path(1, new BlueprintSelectionFeature { Group = FeatureGroup.Attribute, Throw = true });
        bool caught = false; try { Grow(Guard(), throwing); } catch { caught = true; }
        Check(caught, "Synthetic selection exception was exercised");
        var normal = Guard(); normal.Facts.GiftOnGain = Skill("after-scope"); normal.Progression.Features.Add(strength);
        Check(normal.Facts.Contains(normal.Facts.GiftOnGain), "Notification isolation disposed on exception");
        var other = Guard(); other.Facts.GiftOnGain = Skill("different-unit");
        Check(Grow(other, Path(1)) == 1, "Subsequent transaction can continue");
        Check(strength.ComponentsArray.Length == 1 && p.ComponentsArray.Length == 1, "Shared blueprint components unchanged");
        new Harmony("test.guardgrowth").UnpatchAll("test.guardgrowth");
        var noPatch = Guard();
        Check(Grow(noPatch, p) == 0 && noPatch.Facts.Items.Count == 0, "Missing notification patch fails closed");
    }
}
