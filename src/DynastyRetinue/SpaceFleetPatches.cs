using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Kingmaker.Controllers.Projectiles;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules.Starships;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Abilities.Components.Base;
using Kingmaker.Utility;
using Warhammer.SpaceCombat;

namespace DynastyRetinue
{
    /// <summary>
    /// 某些原版大舰蓝图自带敌舰死亡奖励。把它改成盟军舰队后，损失我方舰船
    /// 不应反过来发敌舰战利品。只拦带专用舰队 marker 的死亡单位。
    /// </summary>
    [HarmonyPatch(typeof(SpaceCombatReward), "HandleUnitDeath")]
    internal static class SpaceFleetDeathRewardPatch
    {
        private static bool Prefix(object unit)
        {
            var ship = unit as StarshipEntity;
            return !SpaceEscortService.IsFleetOwnedShip(ship);
        }
    }

    /// <summary>
    /// 原版延迟舰炮会在弹道抵达前保留攻击规则；目标销毁后仍触发该规则，
    /// 随后访问目标的船体/护盾并抛异常。防护直接注入两种舰炮 iterator：
    /// 不触发失效目标的 Rulebook 事件，也不 yield 它的 delivery；其他 AoE 目标继续原版。
    /// </summary>
    internal static class SpaceFleetDisposedTargetPatch
    {
        private static int _skipped;
        private sealed class IteratorFields
        {
            internal FieldInfo Tracker;
            internal FieldInfo Targets;
            internal FieldInfo First;
            internal FieldInfo Last;
            internal FieldInfo Context;
            internal FieldInfo Rules;
            internal FieldInfo Caster;
            internal FieldInfo Target;
            internal FieldInfo TargetWrapper;
        }

        private sealed class TrackerProtection
        {
            internal bool Protected;
            internal bool TargetsRemoved;
        }

        private sealed class ProjectileTargetSnapshot
        {
            internal UnityEngine.Vector3 Point;
        }

        private static readonly ConditionalWeakTable<TargetWrapper, ProjectileTargetSnapshot> ProjectileTargets
            = new ConditionalWeakTable<TargetWrapper, ProjectileTargetSnapshot>();
        // Protection belongs to the launched projectile, not to a reusable logical wrapper.
        private static readonly ConditionalWeakTable<Projectile, ProjectileTargetSnapshot> ProtectedProjectiles
            = new ConditionalWeakTable<Projectile, ProjectileTargetSnapshot>();
        internal static readonly MethodInfo ProjectileTargetSetter
            = AccessTools.PropertySetter(typeof(Projectile), "Target");

        private static readonly Dictionary<Type, IteratorFields> FieldsByIteratorType
            = new Dictionary<Type, IteratorFields>();
        private static readonly ConditionalWeakTable<object, TrackerProtection> TrackerProtections
            = new ConditionalWeakTable<object, TrackerProtection>();

        internal static int SkippedCount { get { return _skipped; } }
        internal static void ResetCount() { _skipped = 0; }

        internal static bool PrepareDefaultTargets(object stateMachine)
        {
            try
            {
                if (stateMachine == null) return true;
                var fields = FieldsFor(stateMachine.GetType());
                var tracker = fields.Tracker != null
                    ? fields.Tracker.GetValue(stateMachine) : null;
                var targets = tracker != null && fields.Targets != null
                    ? fields.Targets.GetValue(tracker) as List<StarshipEntity> : null;
                if (targets == null) return true;

                bool invalid = false;
                bool protectedTarget = false;
                for (int i = 0; i < targets.Count; i++)
                {
                    invalid |= Invalid(targets[i]);
                    protectedTarget |= SpaceEscortService.IsOurShipSource(targets[i]);
                }
                var context = fields.Context != null
                    ? fields.Context.GetValue(stateMachine) as AbilityExecutionContext : null;
                var caster = fields.Caster != null
                    ? fields.Caster.GetValue(stateMachine) as StarshipEntity
                    : context != null ? context.MaybeCaster as StarshipEntity : null;
                bool badSource = UnusableSource(caster);
                bool protectedSource = SpaceEscortService.IsOurShipSource(caster);
                var wrapper = fields.TargetWrapper != null
                    ? fields.TargetWrapper.GetValue(stateMachine) as TargetWrapper : null;
                var wrapperShip = wrapper != null && wrapper.HasEntity
                    ? wrapper.Entity as StarshipEntity : null;
                bool protectedWrapper = SpaceEscortService.IsOurShipSource(wrapperShip);
                bool protectedShot = protectedSource || protectedWrapper || protectedTarget;
                if (protectedShot) ProtectTracker(tracker);
                protectedShot |= TrackerIsProtected(tracker);
                if (!protectedShot) return true;
                if (badSource) return false;

                var protection = TrackerProtections.GetOrCreateValue(tracker);
                if (invalid)
                    for (int i = targets.Count - 1; i >= 0; i--)
                        if (Invalid(targets[i]))
                        {
                            targets.RemoveAt(i);
                            protection.TargetsRemoved = true;
                            _skipped++;
                        }

                // An originally empty AoE still launches and delivers at the clicked point.
                // A burst whose targets were removed must stay stopped on later shots.
                if (targets.Count == 0) return !protection.TargetsRemoved;
                return true;
            }
            catch { return true; }
        }

        internal static TargetWrapper SnapshotProjectileTarget(
            TargetWrapper logicalTarget, object stateMachine)
        {
            try
            {
                if (logicalTarget == null || !logicalTarget.HasEntity) return logicalTarget;
                var fields = FieldsFor(stateMachine.GetType());
                var caster = fields.Caster != null
                    ? fields.Caster.GetValue(stateMachine) as StarshipEntity : null;
                var target = logicalTarget.HasEntity
                    ? logicalTarget.Entity as StarshipEntity : null;
                var tracker = fields.Tracker != null
                    ? fields.Tracker.GetValue(stateMachine) : null;
                if (!SpaceEscortService.IsOurShipSource(caster)
                    && !SpaceEscortService.IsOurShipSource(target)
                    && !TrackerIsProtected(tracker)) return logicalTarget;

                ProjectileTargetSnapshot snapshot;
                if (!ProjectileTargets.TryGetValue(logicalTarget, out snapshot))
                {
                    snapshot = new ProjectileTargetSnapshot { Point = logicalTarget.Point };
                    ProjectileTargets.Add(logicalTarget, snapshot);
                }
                else if (!Invalid(logicalTarget))
                    snapshot.Point = logicalTarget.Point;

                // Keep the entity while usable: native hull locators, void shields and
                // tracking all depend on Projectile.Target.Entity.
                return Invalid(logicalTarget) || UnusableSource(caster)
                    ? new TargetWrapper(snapshot.Point) : logicalTarget;
            }
            catch { return logicalTarget; }
        }

        internal static Projectile TrackProjectile(Projectile projectile, object stateMachine)
        {
            try
            {
                if (projectile == null || projectile.Target == null
                    || !projectile.Target.HasEntity || stateMachine == null) return projectile;
                var fields = FieldsFor(stateMachine.GetType());
                var caster = fields.Caster != null
                    ? fields.Caster.GetValue(stateMachine) as StarshipEntity : null;
                var tracker = fields.Tracker != null
                    ? fields.Tracker.GetValue(stateMachine) : null;
                if (!SpaceEscortService.IsOurShipSource(caster)
                    && !SpaceEscortService.IsOurShipSource(projectile.Target.Entity as StarshipEntity)
                    && !TrackerIsProtected(tracker)) return projectile;

                var snapshot = ProjectileTargets.GetOrCreateValue(projectile.Target);
                snapshot.Point = projectile.Target.Point;
                ProtectedProjectiles.Add(projectile, snapshot);
            }
            catch { }
            return projectile;
        }

        internal static void PrepareProjectileTarget(Projectile projectile)
        {
            try
            {
                ProjectileTargetSnapshot snapshot;
                var target = projectile != null ? projectile.Target : null;
                if (target == null || !target.HasEntity
                    || !ProtectedProjectiles.TryGetValue(projectile, out snapshot)) return;
                var caster = projectile.Launcher != null
                    ? projectile.Launcher.Entity as StarshipEntity : null;
                if (Invalid(target) || UnusableSource(caster))
                {
                    // Detach before native GetTargetPoint AND Tick's later shield access.
                    // This is a lookup for the current projectile, never a projectile scan.
                    ProjectileTargetSetter.Invoke(projectile,
                        new object[] { new TargetWrapper(snapshot.Point) });
                }
                else
                    snapshot.Point = target.Point;
            }
            catch { }
        }

        internal static RuleStarshipPerformAttack Trigger(
            RuleStarshipPerformAttack rule, object stateMachine)
        {
            if (ShouldSkip(rule, stateMachine))
            {
                DetachSkippedRule(stateMachine, rule);
                return rule;
            }
            return Rulebook.Trigger(rule);
        }

        internal static RuleStarshipPerformAttack Trigger(
            RulebookEventContext context, RuleStarshipPerformAttack rule,
            object stateMachine)
        {
            if (ShouldSkip(rule, stateMachine))
            {
                DetachSkippedRule(stateMachine, rule);
                return rule;
            }
            return context != null ? context.Trigger(rule) : Rulebook.Trigger(rule);
        }

        internal static bool PrepareImpact(object stateMachine, bool defaultShot)
        {
            try
            {
                if (stateMachine == null) return true;
                var fields = FieldsFor(stateMachine.GetType());
                if (defaultShot)
                {
                    var rules = fields.Rules != null
                        ? fields.Rules.GetValue(stateMachine)
                            as Dictionary<StarshipEntity, RuleStarshipPerformAttack>
                        : null;
                    if (rules == null) return true;
                    var defaultCaster = fields.Caster != null
                        ? fields.Caster.GetValue(stateMachine) as StarshipEntity : null;
                    var tracker = fields.Tracker != null
                        ? fields.Tracker.GetValue(stateMachine) : null;
                    if (UnusableSource(defaultCaster) && TrackerIsProtected(tracker))
                    {
                        foreach (var pair in rules)
                            DetachSkippedRule(stateMachine, pair.Value);
                        rules.Clear();
                        _skipped++;
                        return false;
                    }
                    List<StarshipEntity> remove = null;
                    foreach (var pair in rules)
                        if (ShouldSkip(pair.Value, stateMachine))
                        {
                            if (remove == null) remove = new List<StarshipEntity>();
                            remove.Add(pair.Key);
                            DetachSkippedRule(stateMachine, pair.Value);
                        }
                    if (remove == null) return true;
                    for (int i = 0; i < remove.Count; i++) rules.Remove(remove[i]);
                    return rules.Count > 0;
                }

                var context = fields.Context != null
                    ? fields.Context.GetValue(stateMachine) as AbilityExecutionContext : null;
                var caster = fields.Caster != null
                    ? fields.Caster.GetValue(stateMachine) as StarshipEntity
                    : context != null ? context.MaybeCaster as StarshipEntity : null;
                var target = fields.Target != null
                    ? fields.Target.GetValue(stateMachine) as StarshipEntity : null;
                if (!ProtectedInvalid(caster, target)) return true;
                _skipped++;
                return false;
            }
            catch { return true; }
        }

        internal static AbilityDeliveryTarget FilterDelivery(
            AbilityDeliveryTarget delivery, object stateMachine)
        {
            try
            {
                if (delivery == null) return null;
                var fields = FieldsFor(stateMachine.GetType());
                var caster = fields.Caster != null
                    ? fields.Caster.GetValue(stateMachine) as StarshipEntity
                    : null;
                bool protectedSource = SpaceEscortService.IsOurShipSource(caster);
                var logicalWrapper = fields.TargetWrapper != null
                    ? fields.TargetWrapper.GetValue(stateMachine) as TargetWrapper : null;
                var logicalTarget = logicalWrapper != null && logicalWrapper.HasEntity
                    ? logicalWrapper.Entity as StarshipEntity : null;
                bool protectedTarget = SpaceEscortService.IsOurShipSource(logicalTarget);
                var tracker = fields.Tracker != null
                    ? fields.Tracker.GetValue(stateMachine) : null;
                if (!protectedSource && !protectedTarget && !TrackerIsProtected(tracker))
                    return delivery;
                if (UnusableSource(caster)) return null;

                // Default shots deliver at the native projectile target, including empty
                // point AoEs. A surviving attack-rule target is not the effect center.
                if (fields.Rules != null)
                    return Invalid(delivery.Target) ? null : delivery;

                var target = fields.Target != null
                    ? fields.Target.GetValue(stateMachine) as StarshipEntity : null;
                return Invalid(target) ? null : delivery;
            }
            catch { return delivery; }
        }

        internal static IEnumerable<CodeInstruction> Transpile(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator,
            bool prepareTargets)
        {
            var codes = new List<CodeInstruction>(instructions);
            int end = FinalStateReset(codes);
            if (end < 0)
                throw new InvalidOperationException("找不到舰炮 iterator 结束锚点");
            Label finish = generator.DefineLabel();
            codes[end].labels.Add(finish);

            if (prepareTargets)
            {
                int dictionaryCtor = -1;
                for (int i = 0; i < codes.Count; i++)
                {
                    var ctor = codes[i].operand as ConstructorInfo;
                    if (ctor != null && ctor.DeclaringType != null
                        && ctor.DeclaringType.IsGenericType
                        && ctor.DeclaringType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                        && ctor.DeclaringType.GetGenericArguments()[0] == typeof(StarshipEntity))
                    { dictionaryCtor = i; break; }
                }
                if (dictionaryCtor <= 0)
                    throw new InvalidOperationException("找不到 DeliverDefault 目标准备锚点");
                int insertAt = dictionaryCtor - 1;
                var first = new CodeInstruction(OpCodes.Ldarg_0);
                MoveLabels(codes[insertAt], first);
                codes.InsertRange(insertAt, new[]
                {
                    first,
                    new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(SpaceFleetDisposedTargetPatch),
                            "PrepareDefaultTargets")),
                    new CodeInstruction(OpCodes.Brfalse, finish)
                });
            }

            MethodInfo snapshot = AccessTools.Method(
                typeof(SpaceFleetDisposedTargetPatch), "SnapshotProjectileTarget");
            int launcherCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                var ctor = codes[i].operand as ConstructorInfo;
                if (codes[i].opcode != OpCodes.Newobj || ctor == null
                    || ctor.DeclaringType != typeof(ProjectileLauncher)) continue;
                var parameters = ctor.GetParameters();
                if (parameters.Length != 3
                    || parameters[2].ParameterType != typeof(TargetWrapper)) continue;
                codes.InsertRange(i, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, snapshot)
                });
                launcherCount++;
                i += 2;
            }

            MethodInfo track = AccessTools.Method(
                typeof(SpaceFleetDisposedTargetPatch), "TrackProjectile");
            int launchCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                var called = codes[i].operand as MethodInfo;
                if (called == null || called.DeclaringType != typeof(ProjectileLauncher)
                    || called.Name != "Launch" || called.ReturnType != typeof(Projectile))
                    continue;
                codes.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, track)
                });
                launchCount++;
                i += 2;
            }

            MethodInfo impact = AccessTools.Method(
                typeof(SpaceFleetDisposedTargetPatch), "PrepareImpact");
            int impactCount = 0;
            for (int i = 1; i < codes.Count; i++)
            {
                var field = codes[i].operand as FieldInfo;
                if (codes[i].opcode != OpCodes.Ldfld || field == null
                    || field.DeclaringType != typeof(AbilityDeliverStarshipShot)
                    || field.Name != "ActionsOnProjectileDeliver")
                    continue;
                int insertAt = i - 1;
                var first = new CodeInstruction(OpCodes.Ldarg_0);
                MoveLabels(codes[insertAt], first);
                codes.InsertRange(insertAt, new[]
                {
                    first,
                    new CodeInstruction(prepareTargets ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0),
                    new CodeInstruction(OpCodes.Call, impact),
                    new CodeInstruction(OpCodes.Brfalse, finish)
                });
                impactCount++;
                i += 4;
            }

            MethodInfo triggerGlobal = AccessTools.Method(
                typeof(SpaceFleetDisposedTargetPatch), "Trigger",
                new[] { typeof(RuleStarshipPerformAttack), typeof(object) });
            MethodInfo triggerContext = AccessTools.Method(
                typeof(SpaceFleetDisposedTargetPatch), "Trigger",
                new[] { typeof(RulebookEventContext), typeof(RuleStarshipPerformAttack), typeof(object) });
            int triggerCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                var called = codes[i].operand as MethodInfo;
                if (called == null || called.Name != "Trigger"
                    || called.ReturnType != typeof(RuleStarshipPerformAttack))
                    continue;
                if (called.DeclaringType == typeof(Rulebook)
                    || called.DeclaringType == typeof(RulebookEventContext))
                {
                    var loadState = new CodeInstruction(OpCodes.Ldarg_0);
                    codes.Insert(i, loadState);
                    i++;
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = called.DeclaringType == typeof(Rulebook)
                        ? triggerGlobal : triggerContext;
                    triggerCount++;
                }
            }

            MethodInfo filter = AccessTools.Method(
                typeof(SpaceFleetDisposedTargetPatch), "FilterDelivery");
            int deliveryCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                var field = codes[i].operand as FieldInfo;
                if (codes[i].opcode != OpCodes.Stfld || field == null
                    || field.Name != "<>2__current"
                    || field.FieldType != typeof(AbilityDeliveryTarget)
                    || !HasProjectileSetter(codes, i))
                    continue;
                var loadState = new CodeInstruction(OpCodes.Ldarg_0);
                MoveLabels(codes[i], loadState);
                codes.InsertRange(i, new[]
                {
                    loadState,
                    new CodeInstruction(OpCodes.Call, filter)
                });
                deliveryCount++;
                i += 2;
            }
            if (launcherCount != 1 || launchCount != 1 || impactCount != 1
                || triggerCount != 2 || deliveryCount != 1)
                throw new InvalidOperationException("舰炮 iterator 锚点数量变化：launcher="
                    + launcherCount + " launch=" + launchCount + " impact=" + impactCount
                    + " trigger=" + triggerCount + " delivery=" + deliveryCount);
            return codes;
        }

        private static void ProtectTracker(object tracker)
        {
            if (tracker != null)
                TrackerProtections.GetOrCreateValue(tracker).Protected = true;
        }

        private static bool TrackerIsProtected(object tracker)
        {
            TrackerProtection protection;
            return tracker != null
                && TrackerProtections.TryGetValue(tracker, out protection)
                && protection.Protected;
        }

        private static void DetachSkippedRule(object stateMachine,
            RuleStarshipPerformAttack skipped)
        {
            if (stateMachine == null || skipped == null) return;
            try
            {
                var fields = FieldsFor(stateMachine.GetType());
                var tracker = fields.Tracker != null
                    ? fields.Tracker.GetValue(stateMachine) : null;
                if (tracker == null || fields.First == null || fields.Last == null)
                    return;

                var first = fields.First.GetValue(tracker) as RuleStarshipPerformAttack;
                var previous = (RuleStarshipPerformAttack)null;
                var current = first;
                int guard = 0;
                while (current != null && !ReferenceEquals(current, skipped) && guard++ < 256)
                {
                    previous = current;
                    current = current.NextAttackInBurst;
                }
                if (!ReferenceEquals(current, skipped)) return;

                var next = skipped.NextAttackInBurst;
                if (ReferenceEquals(next, skipped)) next = null;
                if (previous != null) previous.NextAttackInBurst = next;
                else fields.First.SetValue(tracker, next);
                if (ReferenceEquals(fields.Last.GetValue(tracker), skipped))
                    fields.Last.SetValue(tracker, previous);
                skipped.NextAttackInBurst = null;
                skipped.FirstAttackInBurst = skipped;

                var newFirst = fields.First.GetValue(tracker) as RuleStarshipPerformAttack;
                current = newFirst;
                guard = 0;
                while (current != null && guard++ < 256)
                {
                    current.FirstAttackInBurst = newFirst;
                    current = current.NextAttackInBurst;
                }
            }
            catch { }
        }

        private static bool ProtectedInvalid(StarshipEntity source,
            StarshipEntity target, bool protectedShot = false)
        {
            if (!UnusableSource(source) && !Invalid(target)) return false;
            return protectedShot || SpaceEscortService.IsOurShipSource(source)
                || SpaceEscortService.IsOurShipSource(target);
        }

        private static bool ShouldSkip(RuleStarshipPerformAttack rule, object stateMachine)
        {
            try
            {
                if (rule == null) return false;
                var fields = stateMachine != null ? FieldsFor(stateMachine.GetType()) : null;
                var tracker = fields != null && fields.Tracker != null
                    ? fields.Tracker.GetValue(stateMachine) : null;
                if (!ProtectedInvalid(rule.Initiator, rule.Target, TrackerIsProtected(tracker)))
                    return false;
                _skipped++;
                return true;
            }
            catch { return false; }
        }

        private static bool UnusableSource(StarshipEntity source)
        {
            return source == null || source.IsDisposed || source.IsDisposingNow;
        }

        private static bool Invalid(StarshipEntity target)
        {
            return target == null || target.IsDisposed || target.IsDisposingNow
                || target.WillBeDestroyed;
        }

        private static bool Invalid(TargetWrapper target)
        {
            if (target == null || !target.HasEntity) return false;
            var entity = target.Entity;
            return entity == null || entity.IsDisposed || entity.IsDisposingNow
                || entity.WillBeDestroyed;
        }

        internal static bool ValidateIteratorFields(Type stateType, bool defaultShot)
        {
            if (stateType == null) return false;
            var fields = FieldsFor(stateType);
            return fields.Context != null && fields.Tracker != null
                && fields.First != null && fields.Last != null && fields.Caster != null
                && fields.TargetWrapper != null
                && (defaultShot
                    ? fields.Targets != null && fields.Rules != null
                    : fields.Target != null);
        }

        private static IteratorFields FieldsFor(Type stateType)
        {
            IteratorFields fields;
            if (FieldsByIteratorType.TryGetValue(stateType, out fields)) return fields;
            fields = new IteratorFields
            {
                Context = AccessTools.Field(stateType, "context"),
                Tracker = AccessTools.Field(stateType, "burstTracker"),
                Rules = AccessTools.Field(stateType, "<atkRules>5__6"),
                Caster = AccessTools.Field(stateType, "<caster>5__3")
                    ?? AccessTools.Field(stateType, "<caster>5__2"),
                Target = AccessTools.Field(stateType, "<target>5__3"),
                TargetWrapper = AccessTools.Field(stateType, "targetWrapper")
            };
            if (fields.Tracker != null)
            {
                fields.Targets = AccessTools.Field(fields.Tracker.FieldType, "targets");
                fields.First = AccessTools.Field(fields.Tracker.FieldType, "firstAttack");
                fields.Last = AccessTools.Field(fields.Tracker.FieldType, "lastAttack");
            }
            FieldsByIteratorType[stateType] = fields;
            return fields;
        }

        private static int FinalStateReset(List<CodeInstruction> codes)
        {
            for (int i = codes.Count - 4; i >= 0; i--)
                if (codes[i].opcode == OpCodes.Ldarg_0
                    && codes[i + 1].opcode == OpCodes.Ldc_I4_M1
                    && codes[i + 2].opcode == OpCodes.Stfld
                    && codes[i + 2].operand is FieldInfo
                    && ((FieldInfo)codes[i + 2].operand).Name == "<>1__state")
                    return i;
            return -1;
        }

        private static bool HasProjectileSetter(List<CodeInstruction> codes, int before)
        {
            for (int i = Math.Max(0, before - 14); i < before; i++)
            {
                var method = codes[i].operand as MethodInfo;
                if (method != null && method.DeclaringType == typeof(AbilityDeliveryTarget)
                    && method.Name == "set_Projectile") return true;
            }
            return false;
        }

        private static void MoveLabels(CodeInstruction from, CodeInstruction to)
        {
            if (from.labels == null || from.labels.Count == 0) return;
            to.labels.AddRange(from.labels);
            from.labels.Clear();
        }
    }

    [HarmonyPatch(typeof(Projectile), "GetTargetPoint")]
    internal static class SpaceFleetProjectileTargetPatch
    {
        private static bool Prepare()
        {
            bool valid = SpaceFleetDisposedTargetPatch.ProjectileTargetSetter != null;
            if (!valid)
                Main.LogError("[海战舰队] Projectile.Target setter 不匹配 —— 失效弹道目标防护不可用。");
            return valid;
        }

        private static void Prefix(Projectile __instance)
        {
            SpaceFleetDisposedTargetPatch.PrepareProjectileTarget(__instance);
        }
    }

    [HarmonyPatch]
    internal static class SpaceFleetDefaultDeliveryPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.EnumeratorMoveNext(
                AccessTools.Method(typeof(AbilityDeliverStarshipShot), "DeliverDefault"));
        }

        private static bool Prepare()
        {
            var method = TargetMethod();
            bool valid = method != null
                && SpaceFleetDisposedTargetPatch.ValidateIteratorFields(method.DeclaringType, true);
            if (!valid)
                Main.LogError("[海战舰队] 舰炮 DeliverDefault iterator 或字段布局不匹配 —— 失效目标防护不可用。");
            return valid;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            return SpaceFleetDisposedTargetPatch.Transpile(instructions, generator, true);
        }
    }

    [HarmonyPatch]
    internal static class SpaceFleetLanceDeliveryPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.EnumeratorMoveNext(
                AccessTools.Method(typeof(AbilityDeliverStarshipShot), "DeliverLance"));
        }

        private static bool Prepare()
        {
            var method = TargetMethod();
            bool valid = method != null
                && SpaceFleetDisposedTargetPatch.ValidateIteratorFields(method.DeclaringType, false);
            if (!valid)
                Main.LogError("[海战舰队] 舰炮 DeliverLance iterator 或字段布局不匹配 —— 失效目标防护不可用。");
            return valid;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            return SpaceFleetDisposedTargetPatch.Transpile(instructions, generator, false);
        }
    }
}
