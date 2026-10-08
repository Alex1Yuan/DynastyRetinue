// Isolated game services. Production GuardGrowth/Store are linked unchanged. These doubles
// deliberately contain the unsafe native activation and event paths; real Harmony intercepts them.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Progression.Features;
using Kingmaker.UnitLogic.Progression.Paths;

namespace Kingmaker
{
    public sealed class Game
    { public static Game Instance = new Game(); public State State = new State(); public TestArea CurrentlyLoadedArea; }
    public sealed class TestArea { public int CR; }
    public sealed class State { public InGameSettings InGameSettings = new InGameSettings(); }
    public sealed class InGameSettings { public Dictionary<string, object> List = new Dictionary<string, object>(); }
    public class HideFeatureInEnemyUnitInspect : BlueprintComponent { }
}
namespace Kingmaker.Blueprints
{
    public class BlueprintComponent { public bool Disabled; }
    public class DangerousComponent : BlueprintComponent { }
    public static class ResourcesLibrary
    {
        public static Dictionary<string, BlueprintFeature> All = new Dictionary<string, BlueprintFeature>();
        public static T TryGetBlueprint<T>(string id) where T : class
        { BlueprintFeature f; return All.TryGetValue(id, out f) ? f as T : null; }
    }
}
namespace Kingmaker.EntitySystem.Stats.Base
{ public enum StatType { WarhammerStrength, WarhammerAgility, WarhammerToughness, WarhammerIntelligence,
    WarhammerPerception, WarhammerWillpower, WarhammerFellowship, WarhammerBallisticSkill, WarhammerWeaponSkill, SkillLogic } }
namespace Kingmaker.UnitLogic.FactLogic
{
    public class AddStatBonus : BlueprintComponent { public int Value; public Kingmaker.EntitySystem.Stats.Base.StatType Stat; }
    public class AddProficiencies : BlueprintComponent { }
    // The observed Swarm melee template's native formulas. This service double models the
    // no-area discrepancy; production dispatch/context and eligibility are linked unchanged.
    public class MobStatManager : BlueprintComponent
    {
        public int Refreshes;
        public void HandleDifficultyChanged()
        {
            var runtime = Kingmaker.EntitySystem.EntityFactComponent.Current;
            if (runtime == null) throw new InvalidOperationException("Missing MobStatManager runtime context");
            var owner = runtime.Fact.Owner;
            var area = Kingmaker.Game.Instance.CurrentlyLoadedArea;
            int cr = area?.CR ?? 0;
            float scale = area == null ? 1f : Math.Min(cr / 15f, 1f);
            float factor = 1f + 10f * scale / 100f;
            int primary = 25 + (int)(cr * 0.65 / 5.0) * 5;
            int secondary = 20 + (int)(cr * 0.5 / 5.0) * 5;
            int toughness = 5 * (int)((27f + 0.6f * cr + (cr >= 15 ? 4.6f : 0f) + (cr >= 34 ? 8 : 0)) / 5f);
            int[] raw = { primary, secondary, toughness, 20, 20, 20, 20, secondary, primary };
            owner.DifficultyModifiers.Remove(runtime);
            owner.DifficultyModifiers.Add(runtime, raw.Select((v, i) => (int)(v * factor) - owner.BaseAttributes[i]).ToArray());
            Refreshes++;
        }
    }
}
namespace Kingmaker.Designers.Mechanics.Facts
{
    // Native formula/call shape verified against Code.dll IL. The game owns the actual modifier;
    // this double requires the same component runtime context so a bare blueprint call fails.
    public class ToughnessLogic : BlueprintComponent
    {
        public int Refreshes;
        public void HandleUnitGainPathRank(BlueprintPath ignored)
        {
            var runtime = Kingmaker.EntitySystem.EntityFactComponent.Current;
            if (runtime == null) throw new InvalidOperationException("ComponentRuntime is unavailable in current context");
            var owner = runtime.Fact.Owner;
            owner.Health.HitPoints.Modifiers.Remove(runtime);
            owner.Health.HitPoints.Modifiers.Add(runtime, (owner.Progression.CharacterLevel + 1) / 2);
            Refreshes++;
        }
    }
}
namespace Kingmaker.UnitLogic.Progression.Features
{
    public class BlueprintFeature
    {
        public string AssetGuid, name;
        public BlueprintComponent[] ComponentsArray = new BlueprintComponent[0], Inherited = new BlueprintComponent[0];
        public Prerequisites Prerequisites = new Prerequisites();
        public virtual void CollectComponents(List<BlueprintComponent> list) { list.AddRange(ComponentsArray); list.AddRange(Inherited); }
    }
    public class Prerequisites { public bool Allowed = true; public bool Meet(BaseUnitEntity unit) { return Allowed; } }
}
namespace Kingmaker.UnitLogic.Progression.Paths
{
    public class BlueprintPath : BlueprintFeature
    {
        public class RankEntry
        {
            public BlueprintFeature[] Features = new BlueprintFeature[0];
            public Kingmaker.UnitLogic.Levelup.Selections.Feature.BlueprintSelectionFeature[] Selections
                = new Kingmaker.UnitLogic.Levelup.Selections.Feature.BlueprintSelectionFeature[0];
        }
        public int Ranks;
        public RankEntry[] RankEntries;
        public RankEntry GetRankEntry(int rank) { return RankEntries[rank - 1]; }
    }
    public class BlueprintCareerPath : BlueprintPath { public bool IsAvailable = true; }
}
namespace Kingmaker.UnitLogic.Progression.Features.Advancements
{
    public class StatAdvancement : BlueprintComponent { }
    public abstract class BlueprintStatAdvancement : BlueprintFeature
    { public abstract int ValuePerRank { get; } public abstract Kingmaker.EntitySystem.Stats.Base.StatType Stat { get; } }
    public class BlueprintAttributeAdvancement : BlueprintStatAdvancement
    {
        public Kingmaker.EntitySystem.Stats.Base.StatType TestStat;
        public override int ValuePerRank { get { return 5; } }
        public override Kingmaker.EntitySystem.Stats.Base.StatType Stat { get { return TestStat; } }
    }
    public class BlueprintSkillAdvancement : BlueprintStatAdvancement
    {
        public override int ValuePerRank { get { return 10; } }
        public override Kingmaker.EntitySystem.Stats.Base.StatType Stat { get { return Kingmaker.EntitySystem.Stats.Base.StatType.SkillLogic; } }
    }
}
namespace Kingmaker.UnitLogic.Levelup.Selections
{ public enum FeatureGroup { Attribute = 1, Skill = 2, Talent = 3 } }
namespace Kingmaker.UnitLogic.Levelup.Selections.Feature
{
    public class BlueprintSelectionFeature
    {
        public Kingmaker.UnitLogic.Levelup.Selections.FeatureGroup Group;
        public BlueprintFeature[] Options = new BlueprintFeature[0]; public int MaxRank; public bool Throw;
        public IEnumerable<FeatureSelectionItem> GetSelectionItems(BaseUnitEntity unit, BlueprintPath path)
        {
            if (Throw) throw new Exception("synthetic failure");
            return Options.Select(x => new FeatureSelectionItem { Feature = x, SourceBlueprint = path, MaxRank = MaxRank });
        }
    }
    public struct FeatureSelectionItem
    {
        public BlueprintFeature Feature, SourceBlueprint; public int MaxRank;
        public bool MeetRankPrerequisites(BaseUnitEntity unit)
        { var rank = unit.Progression.Features.GetRank(Feature); return (MaxRank >= 1 || rank >= 1) ? MaxRank > rank : true; }
    }
}
namespace Kingmaker.EntitySystem
{
    public class EntityFactComponent
    {
        [ThreadStatic] public static EntityFactComponent Current;
        public Feature Fact; public BlueprintComponent SourceBlueprintComponent; public bool IsDisposed;
        public bool IsActive { get { return Fact.IsActive; } }
    }
    public class EntityFactsManager
    {
        public BaseUnitEntity Owner; public List<Feature> Items = new List<Feature>();
        public List<Feature> List { get { return Items; } }
        public BlueprintFeature GiftOnGain;
        public static readonly BlueprintFeature PathAbility = new BlueprintFeature { AssetGuid = "native-path-ability" };
        public bool Contains(BlueprintFeature bp) { return Items.Any(f => f.Blueprint == bp); }
        public T Add<T>(T fact) where T : Feature
        {
            var old = Items.FirstOrDefault(f => f.Blueprint == fact.Blueprint);
            if (old != null) { old.AddRank(); return old as T; }
            Items.Add(fact); if (!fact.SuppressActivationOnAttach) fact.Activate();
            DelegateOnFactDidAttach(fact); return fact;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DelegateOnFactDidAttach(Feature fact)
        {
            if (GiftOnGain != null && fact.Blueprint != GiftOnGain && !Contains(GiftOnGain))
                Add(new Feature(GiftOnGain, Owner));
        }
        public void PostLoad() { foreach (var f in Items.ToArray()) if (f.IsActive && f.Blueprint is BlueprintPath) f.Activate(); }
    }
}
namespace Kingmaker.EntitySystem.Entities
{
    public class BaseUnitEntity
    {
        public string UniqueId; public bool Marked; public BaseUnitEntity Master;
        public Kingmaker.EntitySystem.EntityFactsManager Facts; public PartUnitProgression Progression;
        public Health Health = new Health();
        public int[] BaseAttributes = { 25, 25, 25, 25, 25, 30, 25, 25, 20 };
        public Dictionary<Kingmaker.EntitySystem.EntityFactComponent, int[]> DifficultyModifiers
            = new Dictionary<Kingmaker.EntitySystem.EntityFactComponent, int[]>();
        public BaseUnitEntity() { Facts = new Kingmaker.EntitySystem.EntityFactsManager { Owner = this }; Progression = new PartUnitProgression(this); }
    }
    public class Health { public HP HitPoints = new HP(); }
    public class HP
    {
        public int Updates;
        public Dictionary<Kingmaker.EntitySystem.EntityFactComponent, int> Modifiers
            = new Dictionary<Kingmaker.EntitySystem.EntityFactComponent, int>();
        public void UpdateValue() { Updates++; }
    }
}
namespace Kingmaker.UnitLogic
{
    public class Feature
    {
        public BlueprintFeature Blueprint; public BaseUnitEntity Owner;
        public int Rank { get; private set; } = 1;
        public bool IsActive, SuppressActivationOnAttach, IsDisposed; public int Activations;
        public List<Kingmaker.EntitySystem.EntityFactComponent> Components = new List<Kingmaker.EntitySystem.EntityFactComponent>();
        public Feature(BlueprintFeature bp, BaseUnitEntity owner)
        {
            Blueprint = bp; Owner = owner;
            var source = new List<BlueprintComponent>(); bp.CollectComponents(source);
            foreach (var component in source)
                Components.Add(new Kingmaker.EntitySystem.EntityFactComponent { Fact = this, SourceBlueprintComponent = component });
        }
        public void CallComponentsWithRuntime<T>(Action<T, Kingmaker.EntitySystem.EntityFactComponent> action) where T : class
        {
            foreach (var runtime in Components)
            {
                var source = runtime.SourceBlueprintComponent;
                var component = source as T;
                if (source.Disabled || component == null) continue;
                var previous = Kingmaker.EntitySystem.EntityFactComponent.Current;
                try { Kingmaker.EntitySystem.EntityFactComponent.Current = runtime; action(component, runtime); }
                finally { Kingmaker.EntitySystem.EntityFactComponent.Current = previous; }
            }
        }
        public void AddSource(BlueprintPath path, BlueprintFeature source, int rank) { }
        public void AddRank() { Rank++; if (IsActive) Activate(); }
        public void Activate()
        {
            Activations++;
            IsActive = true;
            if (Blueprint is BlueprintPath && Blueprint.ComponentsArray.Any(c => c is DangerousComponent)
                && !Owner.Facts.Contains(Kingmaker.EntitySystem.EntityFactsManager.PathAbility))
                Owner.Facts.Add(new Feature(Kingmaker.EntitySystem.EntityFactsManager.PathAbility, Owner));
        }
    }
    public class FeatureCollection
    {
        private BaseUnitEntity owner;
        public FeatureCollection(BaseUnitEntity unit) { owner = unit; }
        public IEnumerable<Feature> RawFacts { get { return owner.Facts.Items; } }
        public Feature Get(BlueprintFeature bp) { return RawFacts.FirstOrDefault(x => x.Blueprint == bp); }
        public int GetRank(BlueprintFeature bp) { return Get(bp)?.Rank ?? 0; }
        public Feature Add(BlueprintFeature bp) { return owner.Facts.Add(new Feature(bp, owner)); }
    }
    public class PartUnitProgression
    {
        private int m_CharacterLevel { get; set; }
        public int CharacterLevel { get { return m_CharacterLevel; } }
        public int ExperienceLevel = 55, NativeCalls;
        public FeatureCollection Features;
        public List<Tuple<BlueprintPath, int, object>> Selections = new List<Tuple<BlueprintPath, int, object>>();
        public PartUnitProgression(BaseUnitEntity owner) { Features = new FeatureCollection(owner); }
        public int GetPathRank(BlueprintPath path) { return Features.GetRank(path); }
        public void AdvanceToNextLevel() { m_CharacterLevel = Math.Min(ExperienceLevel, Features.RawFacts.Where(f => f.Blueprint is BlueprintCareerPath).Sum(f => f.Rank) + 1); }
        public void SetTestLevel(int level) { m_CharacterLevel = level; }
        public void AddPathRank(BlueprintPath path) { NativeCalls++; AdvanceToNextLevel(); Features.Add(path); }
        public int? GetSelectedFeature(BlueprintPath path, int rank, object selection)
        { return Selections.Any(s => s.Item1 == path && s.Item2 == rank && s.Item3 == selection) ? (int?)1 : null; }
        public void AddFeatureSelection(BlueprintPath path, int rank, object selection, BlueprintFeature feature, int featureRank)
        { Selections.Add(Tuple.Create(path, rank, selection)); }
    }
}
namespace DynastyRetinue
{
    internal static class Main
    {
        public static Settings Settings = new Settings();
        public static void Log(string message) { }
        public static void LogError(string message) { }
    }
    internal class Settings { public bool GuardAttributesOnly = true, AutoLevelUp = true; }
    internal static class RetinueRegistry { public static bool IsGuard(BaseUnitEntity unit) { return unit != null && unit.Marked; } }
    internal static class Archetypes { public static int LastSeen, LastNoOption, LastPlanHits, LastFallbacks; }
    internal static class GearTool
    { public static ChainProbe.EliteDef EliteDefOf(BaseUnitEntity unit, ChainProbe.Archetype arch) { return arch.Elite; } }
    internal static class ChainProbe
    {
        internal class Archetype { public string[] GrantFeatures, PreGrant; public string[][] GrantFeaturesTier; public EliteDef Elite; }
        internal class EliteDef { public string[] PreGrant; }
    }
    internal static class BuildPlans
    {
        internal class Plan
        {
            public Dictionary<string, Dictionary<int, List<string>>> Sel = new Dictionary<string, Dictionary<int, List<string>>>();
            public List<string> Candidates(string path, int rank)
            { Dictionary<int, List<string>> ranks; List<string> values; return Sel.TryGetValue(path, out ranks) && ranks.TryGetValue(rank, out values) ? values : null; }
        }
    }
}
