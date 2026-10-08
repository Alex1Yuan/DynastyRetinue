using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

namespace DynastyRetinue
{
    /// <summary>Cold, event-driven progression for registered guards. Never constructs LevelUpManager.
    /// Native AddPathRank is NOT a safe primitive: it reactivates path components through AddRank,
    /// then broadcasts IUnitGainPathRankHandler. Both can grant abilities independently of rank.Features.
    /// New paths are native, inactive Facts (IsActive is serialized). Existing path Facts retain their
    /// activation state and all their children; only their serialized Rank changes.</summary>
    internal static class GuardGrowth
    {
        private static readonly MethodInfo SetRank = typeof(Feature).GetProperty("Rank")?.GetSetMethod(true);
        private static readonly MethodInfo SetLevel = typeof(PartUnitProgression)
            .GetProperty("m_CharacterLevel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetSetMethod(true);
        private static Dictionary<string, object> Values
        { get { return Game.Instance?.State?.InGameSettings?.List; } }

        internal static bool AttributesOnly(BaseUnitEntity guard)
        { return RetinueRegistry.IsGuard(guard) && Main.Settings.AutoLevelUp && Main.Settings.GuardAttributesOnly; }

        // Returning to full growth resumes future ranks. Bootstrap abilities deliberately skipped on
        // recruitment (origins, preGrant, gear talents, servitor) remain skipped for this guard.
        internal static bool RestrictBootstrap(BaseUnitEntity guard)
        { return RetinueRegistry.IsGuard(guard) && (AttributesOnly(guard) || GuardGrowthStore.HasHistory(Values, guard.UniqueId)); }

        internal static bool Skipped(BaseUnitEntity guard, string path, int rank)
        { return RetinueRegistry.IsGuard(guard) && GuardGrowthStore.Skipped(Values, guard.UniqueId, path, rank); }

        // These weapon permissions are marker Facts tested by equipment restrictions, with no
        // AddProficiencies component. Names alone are not a security/safety classifier.
        private static readonly HashSet<string> WeaponPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "19673fbc918241c6add6710025f67058", // Bolter
            "26376b596c474d67849613617ced7b04", // Heavy weapon
            "365ad1a4ef1b4a47be74509c33b2be3b", // Aeldari
            "5de2d70b4ac9468ca723e84c57524eae", // Xenos
            "dddb6710ce25490e98608a2c07fbfc51", // Plasma
        };

        internal static bool IsAttributeStat(StatType stat)
        {
            switch (stat)
            {
                case StatType.WarhammerStrength: case StatType.WarhammerAgility:
                case StatType.WarhammerToughness: case StatType.WarhammerIntelligence:
                case StatType.WarhammerPerception: case StatType.WarhammerWillpower:
                case StatType.WarhammerFellowship: case StatType.WarhammerBallisticSkill:
                case StatType.WarhammerWeaponSkill: return true;
                default: return false;
            }
        }

        internal static bool IsPureAttribute(BlueprintFeature feature)
        {
            if (feature == null) return false;
            var advancement = feature as BlueprintAttributeAdvancement;
            // Current career pools contain BlueprintAttributeAdvancement + StatAdvancement, while
            // older StatAdvancement<N> Facts use BlueprintFeature + AddStatBonus. Do not admit the
            // broader BlueprintStatAdvancement base: BlueprintSkillAdvancement also derives from it.
            if (feature.GetType() != typeof(BlueprintFeature)
                && feature.GetType() != typeof(BlueprintAttributeAdvancement)) return false;
            var components = new List<BlueprintComponent>();
            feature.CollectComponents(components); // Includes inherited components; never mutate them.
            bool bonus = false;
            foreach (var c in components)
            {
                if (c == null) return false;
                if (c.Disabled) continue;
                if (c.GetType() == typeof(StatAdvancement) && advancement != null
                    && advancement.ValuePerRank > 0 && IsAttributeStat(advancement.Stat))
                { bonus = true; continue; }
                var stat = c as AddStatBonus;
                if (stat == null || c.GetType() != typeof(AddStatBonus) || stat.Value <= 0 || !IsAttributeStat(stat.Stat))
                    return false; // Unknown/mixed/trigger components fail closed, even in Attribute group.
                bonus = true;
            }
            return bonus;
        }

        internal static bool IsProficiency(BlueprintFeature feature)
        {
            if (feature == null || feature.GetType() != typeof(BlueprintFeature)) return false;
            bool permission = WeaponPermissions.Contains(feature.AssetGuid.ToString());
            var components = new List<BlueprintComponent>();
            feature.CollectComponents(components);
            foreach (var c in components)
            {
                if (c == null) return false;
                if (c.Disabled) continue;
                if (c.GetType() == typeof(AddProficiencies)) { permission = true; continue; }
                if (c.GetType() == typeof(HideFeatureInEnemyUnitInspect)) continue;
                return false;
            }
            return permission;
        }

        internal static int GrantGearProficiencies(BaseUnitEntity guard, ChainProbe.Archetype arch, int tier)
        {
            if (!RestrictBootstrap(guard) || !NotificationIsolationReady()) return 0;
            if (!GuardGrowthStore.Touch(Values, guard.UniqueId)) return 0;
            var ids = new List<string>();
            if (arch.GrantFeatures != null) ids.AddRange(arch.GrantFeatures);
            if (tier >= 1 && arch.GrantFeaturesTier != null && tier <= arch.GrantFeaturesTier.Length
                && arch.GrantFeaturesTier[tier - 1] != null) ids.AddRange(arch.GrantFeaturesTier[tier - 1]);
            var elite = GearTool.EliteDefOf(guard, arch);
            var preGrant = elite != null ? elite.PreGrant : arch.PreGrant;
            if (preGrant != null) ids.AddRange(preGrant);
            return GrantProficiencies(guard, ids);
        }

        private static int GrantProficiencies(BaseUnitEntity guard, IEnumerable<string> ids)
        {
            int count = 0;
            if (ids == null) return count;
            using (new QuietFacts(guard.Facts))
                foreach (var id in ids)
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var feature = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(id.Trim());
                    if (!IsProficiency(feature) || guard.Facts.Contains(feature)) continue;
                    if (guard.Progression.Features.Add(feature) != null) count++;
                }
            return count;
        }

        internal static int Apply(BaseUnitEntity guard, string[] chain, int depth, int cap,
            BuildPlans.Plan plan, string[] priority, string[] preGrant)
        {
            if (!AttributesOnly(guard) || chain == null || SetRank == null || SetLevel == null
                || guard.Master != null || !NotificationIsolationReady()) return 0;
            if (!GuardGrowthStore.Touch(Values, guard.UniqueId))
            { Main.LogError("属性成长记录不可写，停止成长以防补授已跳过技能。"); return 0; }
            // No XP edits: bound by both the configured cap and actual earned experience, including
            // the game's own XP-table ceiling. Ignore debug allowOverBudget in this mode.
            cap = Math.Min(55, Math.Min(cap, guard.Progression.ExperienceLevel));
            int total = 0;
            GrantProficiencies(guard, preGrant);
            try
            {
            using (new QuietFacts(guard.Facts))
            {
                for (int i = 0; i < Math.Min(depth, chain.Length); i++)
                {
                    var path = ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(chain[i]);
                    if (path == null || !path.IsAvailable || path.RankEntries == null
                        || path.Ranks != path.RankEntries.Length) break;
                    while (guard.Progression.CharacterLevel < cap && total < 55)
                    {
                        int before = guard.Progression.GetPathRank(path);
                        if (before >= path.Ranks) break;
                        int rank = before + 1;
                        var entry = path.GetRankEntry(rank);
                        if (entry == null) return total;
                        // Native AdvanceToNextLevel uses SUM(all career ranks)+1, not level+1.
                        // Refuse inconsistent old progression instead of jumping over the cap.
                        int sum = guard.Progression.Features.RawFacts
                            .Where(f => f.Blueprint is BlueprintCareerPath).Sum(f => f.Rank);
                        if (sum != guard.Progression.CharacterLevel || sum + 1 > cap) return total;
                        bool consumed = GuardGrowthStore.Skipped(Values, guard.UniqueId, chain[i], rank);
                        if (!GuardGrowthStore.Mark(Values, guard.UniqueId, chain[i], rank)) return total;
                        var fact = guard.Progression.Features.Get(path);
                        if (fact == null)
                        {
                            // The native serialized IsActive=false survives load; AddFacts.OnPostLoad
                            // checks Fact.Active. Do not call Features.Add(path), which activates it.
                            fact = new Feature(path, guard) { SuppressActivationOnAttach = true };
                            if (guard.Facts.Add(fact) == null) return total;
                        }
                        else
                        {
                            // AddRank reactivates active Facts and runs their AddFacts/actions again.
                            SetRank.Invoke(fact, new object[] { rank });
                        }
                        // Same value as native AdvanceToNextLevel, with rank already committed. This
                        // ordering leaves no phantom level if attachment fails (e.g. a missing DLC).
                        SetLevel.Invoke(guard.Progression, new object[] { sum + 1 });
                        total++;
                        if (!consumed)
                        {
                            foreach (var fixedFeature in entry.Features)
                                if (IsPureAttribute(fixedFeature)
                                    || (IsProficiency(fixedFeature) && !guard.Facts.Contains(fixedFeature)))
                                    guard.Progression.Features.Add(fixedFeature)?.AddSource(path, path, rank);
                            foreach (var raw in entry.Selections)
                            {
                                var selection = raw as BlueprintSelectionFeature;
                                if (selection == null || selection.Group != FeatureGroup.Attribute
                                    || guard.Progression.GetSelectedFeature(path, rank, selection).HasValue) continue;
                                ChooseAttribute(guard, path, rank, selection, plan, priority);
                            }
                        }
                        // Keep level-based HP current without broadcasting IUnitGainPathRankHandler.
                        guard.Health.HitPoints.UpdateValue();
                    }
                    if (guard.Progression.CharacterLevel >= cap) break;
                    if (guard.Progression.GetPathRank(path) < path.Ranks) break;
                }
            }
            }
            finally
            {
                // Rank broadcasts are deliberately suppressed, but this existing passive caches a
                // level-derived HP modifier. Refresh it once after the batch, including a partially
                // completed batch, without awakening any other rank listener or inactive path.
                if (total > 0) RefreshExistingToughness(guard);
            }
            Main.Log("    只加属性: 推进 " + total + " 级；跳过的能力不会在切回完整成长后补授。");
            return total;
        }

        internal static int RefreshExistingToughness(BaseUnitEntity guard)
        {
            if (!AttributesOnly(guard)) return 0;
            int refreshed = 0;
            foreach (var fact in guard.Facts.List)
            {
                if (fact == null || !fact.IsActive || fact.IsDisposed) continue;
                // This API supplies ComponentRuntime via RequestEventContext. Calling the shared
                // blueprint delegate directly would have no Owner/Runtime context. The verified
                // native method only replaces this runtime's modifier with (CharacterLevel+1)/2.
                fact.CallComponentsWithRuntime<ToughnessLogic>((logic, runtime) =>
                {
                    if (logic.GetType() != typeof(ToughnessLogic) || runtime.IsDisposed || !runtime.IsActive) return;
                    logic.HandleUnitGainPathRank(null);
                    refreshed++;
                });
            }
            if (refreshed > 0) guard.Health.HitPoints.UpdateValue();
            return refreshed;
        }

        /// <summary>Cross-scene Facts load before CurrentlyLoadedArea is assigned. MobStatManager
        /// can therefore cache CR=0 stats until another difficulty/faction event. Normalize only
        /// its existing runtime after area restoration, for full/off/attribute guards alike.</summary>
        internal static int RefreshExistingMobStats(BaseUnitEntity guard)
        {
            if (!RetinueRegistry.IsGuard(guard) || Game.Instance?.CurrentlyLoadedArea == null) return 0;
            int refreshed = 0;
            foreach (var fact in guard.Facts.List)
            {
                if (fact == null || !fact.IsActive || fact.IsDisposed) continue;
                fact.CallComponentsWithRuntime<MobStatManager>((logic, runtime) =>
                {
                    if (logic.GetType() != typeof(MobStatManager) || runtime.IsDisposed || !runtime.IsActive) return;
                    // This exact native override only replaces its nine Difficulty modifiers.
                    // Do not invoke NPCDifficultyModifiersManager (can grant army Facts), broadcast
                    // difficulty/rank events, or Reapply the surrounding Fact.
                    logic.HandleDifficultyChanged();
                    refreshed++;
                });
            }
            if (refreshed > 0) guard.Health.HitPoints.UpdateValue();
            return refreshed;
        }

        private static void ChooseAttribute(BaseUnitEntity guard, BlueprintCareerPath path, int rank,
            BlueprintSelectionFeature selection, BuildPlans.Plan plan, string[] priority)
        {
            Archetypes.LastSeen++;
            var candidates = selection.GetSelectionItems(guard, path).Where(item =>
                IsPureAttribute(item.Feature) && item.MeetRankPrerequisites(guard)
                && (item.Feature.Prerequisites == null || item.Feature.Prerequisites.Meet(guard))).ToList();
            if (candidates.Count == 0) { Archetypes.LastNoOption++; return; }
            int chosen = -1;
            var wanted = plan?.Candidates(path.AssetGuid.ToString(), rank);
            if (wanted != null)
                foreach (var id in wanted)
                {
                    chosen = candidates.FindIndex(x => string.Equals(x.Feature.AssetGuid.ToString(), id, StringComparison.OrdinalIgnoreCase));
                    if (chosen >= 0) break;
                }
            bool hit = chosen >= 0;
            if (chosen < 0 && priority != null)
                foreach (var name in priority)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    chosen = candidates.FindIndex(x => x.Feature.name != null
                        && (x.Feature.name.IndexOf(name + "StatAdvancement", StringComparison.OrdinalIgnoreCase) >= 0
                            || x.Feature.name.IndexOf(name + "AttributeAdvancement", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (chosen >= 0) break;
                }
            var item = candidates[chosen < 0 ? 0 : chosen];
            var fact = guard.Progression.Features.Add(item.Feature);
            if (fact == null) return;
            fact.AddSource(path, item.SourceBlueprint, rank);
            guard.Progression.AddFeatureSelection(path, rank, selection, fact.Blueprint, fact.Rank);
            if (hit) Archetypes.LastPlanHits++; else Archetypes.LastFallbacks++;
        }

        // Adding even a pure attribute/marker Fact raises IEntityGainFactHandler. Existing conditional
        // talents may grant other Facts in that callback. Mute this notification only synchronously
        // inside our growth/grant transaction, for this exact guard's manager. No scanning or polling.
        [ThreadStatic] private static EntityFactsManager quietManager;
        private static bool NotificationIsolationReady()
        {
            var method = AccessTools.Method(typeof(EntityFactsManager), "DelegateOnFactDidAttach");
            var patches = method == null ? null : Harmony.GetPatchInfo(method);
            if (patches != null && patches.Prefixes.Any(p => p.PatchMethod.DeclaringType == typeof(QuietFactNotification)))
                return true;
            Main.LogError("属性成长通知隔离补丁不可用，停止授予以防触发额外能力。");
            return false;
        }
        private sealed class QuietFacts : IDisposable
        {
            private readonly EntityFactsManager previous;
            internal QuietFacts(EntityFactsManager manager) { previous = quietManager; quietManager = manager; }
            public void Dispose() { quietManager = previous; }
        }

        [HarmonyPatch(typeof(EntityFactsManager), "DelegateOnFactDidAttach")]
        private static class QuietFactNotification
        {
            private static bool Prefix(EntityFactsManager __instance)
            { return !ReferenceEquals(quietManager, __instance); }
        }
    }
}
