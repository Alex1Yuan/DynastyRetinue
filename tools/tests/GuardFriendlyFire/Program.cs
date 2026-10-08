using DynastyRetinue;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.RuleSystem.Rules.Damage;
using Kingmaker.UnitLogic.Abilities;

static class Program
{
    static int checks;
    static void Check(bool ok, string name)
    { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void Run()
    {
        DynastyRetinue.Main.Settings.GuardFriendlyFireProtection = true;
        var guard = new BaseUnitEntity { Marked = true, IsPlayerFaction = true };
        var ally = new BaseUnitEntity { IsPlayerFaction = true };
        var otherGuard = new BaseUnitEntity { Marked = true, IsPlayerFaction = true };
        var enemy = new BaseUnitEntity { Hostile = true };
        var npcAlly = new BaseUnitEntity { Faction = new Faction { Allied = true } };
        var neutral = new BaseUnitEntity();
        Check(GuardFriendlyFire.ShouldProtect(guard, ally), "Guard -> companion");
        Check(GuardFriendlyFire.ShouldProtect(guard, otherGuard), "Guard -> guard");
        Check(GuardFriendlyFire.ShouldProtect(guard, npcAlly), "Guard -> allied NPC faction");
        Check(!GuardFriendlyFire.ShouldProtect(guard, enemy), "Enemy is not protected");
        Check(!GuardFriendlyFire.ShouldProtect(guard, neutral), "Neutral is not treated as an ally");
        Check(!GuardFriendlyFire.ShouldProtect(guard, guard), "Self costs are not protected");
        Check(!GuardFriendlyFire.ShouldProtect(ally, guard), "Normal player attacks are unchanged");
        Check(!GuardFriendlyFire.ShouldProtect(enemy, guard), "Incoming enemy damage is unchanged");
        Check(!GuardFriendlyFire.ShouldProtect(null, ally), "Unknown source stays native");
        ally.Hostile = true;
        Check(!GuardFriendlyFire.ShouldProtect(guard, ally), "Hostile overrides player faction");
        ally.Hostile = false;
        guard.Brain.IsTraitor = true;
        Check(!GuardFriendlyFire.ShouldProtect(guard, ally), "Traitor guard stays native");
        guard.Brain.IsTraitor = false;
        DynastyRetinue.Main.Enabled = false;
        Check(!GuardFriendlyFire.ShouldProtect(guard, ally), "Master off");
        DynastyRetinue.Main.Enabled = true;

        var shared = new DamageData();
        var roll = new RuleRollDamage(guard, ally, shared);
        GuardFriendlyFire.ProtectRoll(roll);
        Check(roll.ResultValue == 0 && roll.Result.FinalValue == 0, "Both effective damage results zero");
        Check(roll.ResultReflected == 0, "Friendly target does not reflect the suppressed damage");
        Check(roll.ResultValueBeforeDifficulty == 0 && roll.ResultValueWithoutReduction == 0,
              "Secondary result caches zero");
        Check(roll.UIMinimumDamageValue == 0 && roll.UIMinimumDamagePercent == 0, "No minimum damage leak");
        Check(roll.NullifyInformation.HasDamageNullify, "Native nullify flag set");
        Check(ReferenceEquals(roll.Result.Source, shared) && !shared.Immune, "Shared damage payload unchanged");
        var secondTarget = new RuleRollDamage(guard, enemy, shared);
        GuardFriendlyFire.ProtectRoll(secondTarget);
        Check(secondTarget.ResultValue == 17 && secondTarget.Result.FinalValue == 17,
              "Following enemy target keeps damage with the identical payload");
        var normal = new RuleRollDamage(ally, guard, shared);
        GuardFriendlyFire.ProtectRoll(normal);
        Check(normal.ResultValue == 17 && !normal.NullifyInformation.HasDamageNullify, "Other source roll untouched");
        int count = GuardFriendlyFire.PreventedHits;
        GuardFriendlyFire.ProtectRoll(roll);
        Check(GuardFriendlyFire.PreventedHits == count, "No repeated positive-hit count");

        AbilityExecutionContext Psyker(BaseUnitEntity source, bool psychic = true) => new()
        { MaybeCaster = source, Ability = new AbilityData { Blueprint = new BlueprintAbility { IsPsykerAbility = psychic } } };
        Check(!GuardFriendlyFire.AllowPhenomena(Psyker(guard)), "Guard psychic phenomena suppressed at original caster");
        Check(GuardFriendlyFire.AllowPhenomena(Psyker(ally)), "Player psychic risk unchanged");
        Check(GuardFriendlyFire.AllowPhenomena(Psyker(enemy)), "Enemy psychic risk unchanged");
        Check(GuardFriendlyFire.AllowPhenomena(Psyker(guard, false)), "Non-psychic execution unchanged");
        Check(GuardFriendlyFire.AllowPhenomena(null), "Missing execution unchanged");

        var ricochetPrefix = typeof(GuardRicochetTargetsPatch).GetMethod("Prefix",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        void Ricochet(Kingmaker.RuleSystem.Rules.RuleCalculateOverpenetration rule) =>
            ricochetPrefix.Invoke(null, new object[] { rule });
        var ricochet = new Kingmaker.RuleSystem.Rules.RuleCalculateOverpenetration { InitiatorUnit = guard };
        Ricochet(ricochet);
        Check(ricochet.OverpenetrationDamage.IsRicochetFriendlyFireDisabled, "Enabled option excludes friendly ricochets");
        var targets = typeof(GuardRicochetTargetsPatch).GetMethod("TargetMethods",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Check(((System.Collections.Generic.IEnumerable<System.Reflection.MethodBase>)targets.Invoke(null, null)).Count() == 2,
            "Unified gate is shared by both ricochet overloads");

        DynastyRetinue.Main.Settings.GuardFriendlyFireProtection = false;
        int hitsBefore = GuardFriendlyFire.PreventedHits, psychicBefore = GuardFriendlyFire.PreventedPhenomena;
        Check(!GuardFriendlyFire.ShouldProtect(guard, ally), "Protection off restores friendly targeting policy");
        var unprotected = new RuleRollDamage(guard, ally, shared);
        GuardFriendlyFire.ProtectRoll(unprotected);
        Check(unprotected.ResultValue == 17 && unprotected.Result.FinalValue == 17
            && unprotected.ResultReflected == 2 && unprotected.UIMinimumDamageValue == 1
            && !unprotected.NullifyInformation.HasDamageNullify, "Protection off leaves native friendly damage and caches intact");
        Check(GuardFriendlyFire.AllowPhenomena(Psyker(guard)), "Protection off allows native phenomena and Perils processing");
        var unfiltered = new Kingmaker.RuleSystem.Rules.RuleCalculateOverpenetration { InitiatorUnit = guard };
        Ricochet(unfiltered);
        Check(!unfiltered.OverpenetrationDamage.IsRicochetFriendlyFireDisabled, "Protection off leaves native ricochet flag false");
        unfiltered.OverpenetrationDamage.IsRicochetFriendlyFireDisabled = true;
        Ricochet(unfiltered);
        Check(unfiltered.OverpenetrationDamage.IsRicochetFriendlyFireDisabled, "Protection off preserves protection supplied by native rules or another source");
        Check(hitsBefore == GuardFriendlyFire.PreventedHits && psychicBefore == GuardFriendlyFire.PreventedPhenomena,
            "Disabled option increments neither suppression counter");

        DynastyRetinue.Main.Settings = null;
        Check(!GuardFriendlyFire.ShouldProtect(guard, ally) && GuardFriendlyFire.AllowPhenomena(Psyker(guard)),
            "Unavailable settings leave native rules alone");
        DynastyRetinue.Main.Settings = new TestSettings { GuardFriendlyFireProtection = true };
        Check(GuardFriendlyFire.ShouldProtect(guard, ally) && !GuardFriendlyFire.AllowPhenomena(Psyker(guard)),
            "Re-enabling takes effect on the next event without rebuilding guards");
        Console.WriteLine($"{checks} production-class policy/result assertions passed. Native rule execution tested separately in game.");
    }
}

namespace DynastyRetinue
{
    static class Main { public static bool Enabled = true; public static TestSettings Settings = new(); public static void LogError(string value) => throw new Exception(value); }
    sealed class TestSettings { public bool GuardFriendlyFireProtection = false; }
    static class RetinueRegistry { public static bool IsGuard(BaseUnitEntity unit) => unit.Marked; }
}
namespace Kingmaker.EntitySystem.Entities
{
    public class BaseUnitEntity
    {
        public bool Marked, IsPlayerFaction, IsDisposed, Hostile;
        public Brain Brain = new(); public Faction Faction = new();
        public bool IsEnemy(BaseUnitEntity other) => Hostile || other.Hostile;
        public bool IsAlly(BaseUnitEntity other) => IsPlayerFaction && other.IsPlayerFaction;
    }
    public class Brain { public bool IsTraitor; }
    public class Faction { public bool Allied; public bool IsAlly(Faction other) => other.Allied; }
}
namespace Kingmaker.RuleSystem.Rules.Damage
{
    public class DamageData { public bool Immune; public bool IsRicochetFriendlyFireDisabled; }
    public readonly struct DamageValue
    {
        public readonly DamageData Source; public readonly int FinalValue, RolledValue, Reduction;
        public DamageValue(DamageData source, int final, int rolled, int reduction)
        { Source = source; FinalValue = final; RolledValue = rolled; Reduction = reduction; }
    }
    public class NullifyInformation { public bool HasDamageNullify; }
    public class RuleRollDamage
    {
        public BaseUnitEntity InitiatorUnit, TargetUnit;
        public DamageValue Result { get; private set; }
        public int ResultValue { get; private set; } = 17;
        public int ResultValueWithoutReduction { get; private set; } = 20;
        public int ResultValueBeforeDifficulty { get; private set; } = 18;
        public int ResultReflected { get; private set; } = 2;
        public int UIMinimumDamageValue { get; private set; } = 1;
        public int UIMinimumDamagePercent { get; private set; } = 5;
        public NullifyInformation NullifyInformation = new();
        public RuleRollDamage(BaseUnitEntity source, BaseUnitEntity target, DamageData data)
        { InitiatorUnit = source; TargetUnit = target; Result = new DamageValue(data, 17, 20, 3); }
        public void OnTrigger() { }
    }
}
namespace Kingmaker.RuleSystem.Rules
{
    public class RuleCalculateOverpenetration
    {
        public BaseUnitEntity InitiatorUnit;
        public DamageData OverpenetrationDamage = new();
        public void GetOrCreateRicochetTargets() { }
        public void GetOrCreateRicochetTargets(int test) { }
    }
}
namespace Kingmaker.UnitLogic.Abilities
{
    public class BlueprintAbility { public bool IsPsykerAbility; }
    public class AbilityData { public BlueprintAbility Blueprint; }
    public class AbilityExecutionContext { public BaseUnitEntity MaybeCaster; public AbilityData Ability; }
}
namespace Kingmaker.Controllers
{
    public class PsychicPhenomenaController { public void HandleExecutionProcessEnd(AbilityExecutionContext context) { } }
}
