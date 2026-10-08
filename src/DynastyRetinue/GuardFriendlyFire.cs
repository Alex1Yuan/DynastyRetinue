using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Controllers;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.RuleSystem.Rules.Damage;
using Kingmaker.UnitLogic.Abilities;

namespace DynastyRetinue
{
    /// <summary>
    /// Event-only protection for damage caused by our player-aligned ground guards.
    /// No scene scan, permanent immunity, blueprint edit or modification of shared DamageData.
    /// Self costs and attacks by enemies or normal companions retain native behaviour.
    /// </summary>
    internal static class GuardFriendlyFire
    {
        internal static int PreventedHits { get; private set; }
        internal static int PreventedPhenomena { get; private set; }

        internal static bool Enabled => Main.Enabled && Main.Settings != null
            && Main.Settings.GuardFriendlyFireProtection;

        internal static bool IsSource(BaseUnitEntity source)
        {
            return Enabled && source != null && !source.IsDisposed
                && RetinueRegistry.IsGuard(source) && source.IsPlayerFaction
                && (source.Brain == null || !source.Brain.IsTraitor);
        }

        internal static bool ShouldProtect(BaseUnitEntity source, BaseUnitEntity target)
        {
            if (!IsSource(source) || target == null || target.IsDisposed
                || ReferenceEquals(source, target)) return false;
            // An explicit hostile relationship wins over faction membership (e.g. mind control).
            if (source.IsEnemy(target) || target.IsEnemy(source)) return false;
            return target.IsPlayerFaction || source.IsAlly(target)
                || (source.Faction != null && target.Faction != null && source.Faction.IsAlly(target.Faction));
        }

        internal static void ProtectRoll(RuleRollDamage rule)
        {
            if (rule == null || !ShouldProtect(rule.InitiatorUnit, rule.TargetUnit)) return;
            bool prevented = rule.ResultValue > 0 || rule.ResultReflected > 0;
            // DamageData can be reused by an AOE or subsequent projectile. Only the per-target
            // roll's results change; setting Damage.Immune here would also protect later enemies.
            RollAccess.Result(rule) = new DamageValue(rule.Result.Source, 0, rule.Result.RolledValue, 0);
            RollAccess.Value(rule) = 0;
            RollAccess.WithoutReduction(rule) = 0;
            RollAccess.BeforeDifficulty(rule) = 0;
            RollAccess.Reflected(rule) = 0;
            RollAccess.MinimumValue(rule) = 0;
            RollAccess.MinimumPercent(rule) = 0;
            rule.NullifyInformation.HasDamageNullify = true;
            if (prevented) PreventedHits++;
        }

        internal static bool AllowPhenomena(AbilityExecutionContext context)
        {
            // Do this at the original caster, before phenomena can redirect the effect to an
            // ally and replace its caster. Player psykers and enemy psykers still use vanilla.
            if (context?.Ability?.Blueprint == null || !context.Ability.Blueprint.IsPsykerAbility
                || !IsSource(context.MaybeCaster as BaseUnitEntity)) return true;
            PreventedPhenomena++;
            return false;
        }

        private static class RollAccess
        {
            // Resolve once. Delegates are direct field accesses on the damage-event path.
            internal static readonly AccessTools.FieldRef<RuleRollDamage, DamageValue> Result =
                AccessTools.FieldRefAccess<RuleRollDamage, DamageValue>("<Result>k__BackingField");
            internal static readonly AccessTools.FieldRef<RuleRollDamage, int> Value = Field("ResultValue");
            internal static readonly AccessTools.FieldRef<RuleRollDamage, int> WithoutReduction = Field("ResultValueWithoutReduction");
            internal static readonly AccessTools.FieldRef<RuleRollDamage, int> BeforeDifficulty = Field("ResultValueBeforeDifficulty");
            internal static readonly AccessTools.FieldRef<RuleRollDamage, int> Reflected = Field("ResultReflected");
            internal static readonly AccessTools.FieldRef<RuleRollDamage, int> MinimumValue = Field("UIMinimumDamageValue");
            internal static readonly AccessTools.FieldRef<RuleRollDamage, int> MinimumPercent = Field("UIMinimumDamagePercent");
            private static AccessTools.FieldRef<RuleRollDamage, int> Field(string name)
            { return AccessTools.FieldRefAccess<RuleRollDamage, int>("<" + name + ">k__BackingField"); }
        }
    }

    [HarmonyPatch(typeof(RuleRollDamage), nameof(RuleRollDamage.OnTrigger))]
    internal static class GuardFriendlyDamagePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(RuleRollDamage __instance)
        {
            try { GuardFriendlyFire.ProtectRoll(__instance); }
            catch (Exception e) { Main.LogError("[友伤保护] 伤害检查失败: " + e.Message); }
        }
    }

    [HarmonyPatch]
    internal static class GuardRicochetTargetsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(RuleCalculateOverpenetration)))
                if (method.Name == nameof(RuleCalculateOverpenetration.GetOrCreateRicochetTargets))
                    yield return method;
        }

        private static void Prefix(RuleCalculateOverpenetration __instance)
        {
            try
            {
                if (__instance?.OverpenetrationDamage != null
                    && GuardFriendlyFire.IsSource(__instance.InitiatorUnit))
                    __instance.OverpenetrationDamage.IsRicochetFriendlyFireDisabled = true;
            }
            catch (Exception e) { Main.LogError("[友伤保护] 跳弹检查失败: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(PsychicPhenomenaController), nameof(PsychicPhenomenaController.HandleExecutionProcessEnd))]
    internal static class GuardPsychicPhenomenaPatch
    {
        private static bool Prefix(AbilityExecutionContext context)
        {
            try { return GuardFriendlyFire.AllowPhenomena(context); }
            catch (Exception e) { Main.LogError("[友伤保护] 灵能现象检查失败: " + e.Message); return true; }
        }
    }
}
