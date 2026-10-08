using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Kingmaker.Blueprints.Root.Strings.GameLog;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Mechanics.Entities;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules.Starships;
using Kingmaker.UI.Models.Log;
using Kingmaker.UI.Models.Log.CombatLog_ThreadSystem;
using Kingmaker.UI.Models.Log.CombatLog_ThreadSystem.LogThreads.Combat;
using Kingmaker.UI.Models.Log.Events;
using Kingmaker.UI.Models.Log.GameLogCntxt;
using Kingmaker.UI.Models.Log.GameLogEventInsertPatterns.MergeEvent;
using Kingmaker.UnitLogic.Enums;
using Kingmaker.Utility;

namespace DynastyRetinue
{
    // 2026-09-05: 合并舰炮日志晚于目标 Dispose，GetIcon 读取 PartFaction 抛错；
    // GameLogController 的 Scope 覆盖整批事件，不会在每条消息后清理 Source/Target。
    // 仅记录实际参与我方舰炮事件的两端，不枚举场景、不保活实体、不干预规则结算。
    internal static class SpaceFleetLogPatches
    {
        internal sealed class DisplaySnapshot
        {
            internal bool PlayerFaction;
            internal bool FleetShip;
        }

        private static readonly ConditionalWeakTable<StarshipEntity, DisplaySnapshot> Snapshots
            = new ConditionalWeakTable<StarshipEntity, DisplaySnapshot>();
        private sealed class LifeSnapshot
        {
            internal bool Dead;
            internal bool FinallyDead;
        }
        private static readonly ConditionalWeakTable<GameLogEventLifeStateChanged, LifeSnapshot> LifeSnapshots
            = new ConditionalWeakTable<GameLogEventLifeStateChanged, LifeSnapshot>();
        private static readonly MethodInfo AddMessage = AccessTools.Method(typeof(LogThreadBase), "AddMessage");
        private static readonly ConstructorInfo CopyMessage = AccessTools.Constructor(typeof(CombatLogMessage),
            new[] { typeof(CombatLogMessage), typeof(Owlcat.Runtime.UI.Tooltips.TooltipBaseTemplate),
                typeof(bool), typeof(MechanicEntity) });
        private static bool _reportedSnapshot;

        [ThreadStatic] internal static RenderScope Current;

        internal sealed class RenderScope
        {
            internal RenderScope Previous;
            internal GameLogContext.Property<IMechanicEntity> Source;
            internal GameLogContext.Property<IMechanicEntity> Target;
            internal StarshipEntity DeathUnit;
        }

        private static bool IsFleetShip(StarshipEntity ship)
        {
            if (ship == null) return false;
            DisplaySnapshot snapshot;
            return Snapshots.TryGetValue(ship, out snapshot) && snapshot.FleetShip
                || SpaceEscortService.IsOurShipSource(ship);
        }

        private static bool IsRelated(StarshipEntity source, StarshipEntity target)
        {
            return IsFleetShip(source) || IsFleetShip(target);
        }

        private static bool HasDisposedSnapshot(IMechanicEntity entity)
        {
            DisplaySnapshot snapshot;
            return entity is StarshipEntity ship && ship.IsDisposed
                && Snapshots.TryGetValue(ship, out snapshot);
        }

        private static bool NeedsScope(StarshipEntity source, StarshipEntity target)
        {
            // 同一已记录目标也可能出现在后续单条战报中；仅扩展展示，不传播 Capture 归属。
            return IsRelated(source, target) || HasDisposedSnapshot(source) || HasDisposedSnapshot(target);
        }

        private static void Capture(StarshipEntity ship)
        {
            if (ship == null || ship.IsDisposed) return;
            // 再次攻击时刷新阵营；值不包含舰船引用，销毁并释放后随弱键回收。
            var snapshot = Snapshots.GetValue(ship, _ => new DisplaySnapshot());
            snapshot.PlayerFaction = ship.IsPlayerFaction;
            snapshot.FleetShip = SpaceEscortService.IsOurShipSource(ship);
        }

        internal static bool Applies(IMechanicEntity source, IMechanicEntity target)
        {
            if (!Main.Enabled) return false;
            // 分隔符等不设 Source/Target 的消息也可能继承已记录尸体；仅保护这些已知对象。
            if (HasDisposedSnapshot(source) || HasDisposedSnapshot(target)) return true;
            return Current != null && (Current.DeathUnit != null && ReferenceEquals(source, Current.DeathUnit)
                || IsRelated(source as StarshipEntity, target as StarshipEntity));
        }

        internal static bool DisplayPlayerFaction(IMechanicEntity entity)
        {
            if (entity is StarshipEntity ship && ship.IsDisposed
                && Applies(GameLogContext.SourceEntity.Value, GameLogContext.TargetEntity.Value))
            {
                DisplaySnapshot snapshot;
                if (Snapshots.TryGetValue(ship, out snapshot))
                {
                    if (!_reportedSnapshot)
                    {
                        _reportedSnapshot = true;
                        Main.LogVerbose("[海战舰队] 战报已使用销毁前的舰船阵营快照。");
                    }
                    return snapshot.PlayerFaction;
                }
            }
            return entity.IsPlayerFaction;
        }

        private static bool IsDisposed(IMechanicEntity entity)
        {
            return entity is StarshipEntity ship && ship.IsDisposed;
        }

        [HarmonyPatch(typeof(GameLogEventsFactory), nameof(GameLogEventsFactory.Create))]
        internal static class CaptureAttack
        {
            // Factory 在 OnBeforeEventAboutToTrigger 调用，早于致命伤害和销毁。
            internal static void Prefix(RulebookEvent rule)
            {
                if (!Main.Enabled || !(rule is RuleStarshipPerformAttack attack)
                    || !IsRelated(attack.Initiator, attack.Target)) return;
                Capture(attack.Initiator);
                Capture(attack.Target);
            }
        }

        [HarmonyPatch]
        internal static class CaptureLifeState
        {
            internal static MethodBase TargetMethod()
            {
                return AccessTools.Constructor(typeof(GameLogEventLifeStateChanged),
                    new[] { typeof(AbstractUnitEntity), typeof(UnitLifeState) });
            }

            internal static void Postfix(GameLogEventLifeStateChanged __instance)
            {
                var ship = __instance.Unit as StarshipEntity;
                if (!Main.Enabled || ship == null || ship.IsDisposed) return;
                DisplaySnapshot snapshot;
                if (!Snapshots.TryGetValue(ship, out snapshot) && !IsFleetShip(ship)) return;
                Capture(ship);
                var life = ship.LifeState;
                // IsFinallyDead 会读 Owner.Features，必须在事件创建时、Owner 尚未拆除时取值。
                LifeSnapshots.Add(__instance, new LifeSnapshot
                {
                    Dead = life.IsDead, FinallyDead = life.IsDead && life.IsFinallyDead
                });
            }
        }

        [HarmonyPatch]
        internal static class MessageScope
        {
            internal static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(GameLogController), "Kingmaker.Controllers.Interfaces.IControllerTick.Tick");
            }

            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                var invoke = AccessTools.Method(typeof(GameLogEvent), "Invoke");
                var replacement = AccessTools.Method(typeof(MessageScope), nameof(InvokeScoped));
                int count = 0;
                foreach (var code in codes)
                    if (code.Calls(invoke))
                    {
                        code.opcode = OpCodes.Call;
                        code.operand = replacement;
                        count++;
                    }
                if (count != 1) throw new InvalidOperationException("GameLogController event dispatch changed: " + count);
                return codes;
            }

            // 包住实际 Invoke，而非可能已经内联的短 HandleEvent；AddMessage/Report 也在范围内。
            internal static void InvokeScoped(GameLogEvent evt, LogThreadBase logThread)
            {
                var scope = Begin(evt);
                try
                {
                    if (scope != null && RenderDisposedDeath(evt, logThread)) return;
                    evt.Invoke(logThread);
                }
                finally { End(scope); }
            }

            private static RenderScope Begin(GameLogEvent evt)
            {
                if (!Main.Enabled) return null;
                bool related = false;
                StarshipEntity deathUnit = null;
                if (evt is GameLogRuleEvent<RuleStarshipPerformAttack> single)
                    related = NeedsScope(single.Rule.Initiator, single.Rule.Target);
                else if (evt is MergeGameLogEvent<GameLogRuleEvent<RuleStarshipPerformAttack>> merged)
                {
                    var events = merged.GetEvents();
                    for (int i = 0; i < events.Count && !related; i++)
                        related = NeedsScope(events[i].Rule.Initiator, events[i].Rule.Target);
                }
                else if (evt is GameLogEventLifeStateChanged death)
                {
                    deathUnit = death.Unit as StarshipEntity;
                    DisplaySnapshot snapshot;
                    related = deathUnit != null && Snapshots.TryGetValue(deathUnit, out snapshot);
                }
                else if (evt is GameLogEventAddSeparator)
                    related = HasDisposedSnapshot(GameLogContext.SourceEntity.Value)
                        || HasDisposedSnapshot(GameLogContext.TargetEntity.Value);
                if (!related) return null;
                var scope = new RenderScope
                {
                    Previous = Current, Source = GameLogContext.SourceEntity,
                    Target = GameLogContext.TargetEntity, DeathUnit = deathUnit
                };
                Current = scope;
                GameLogContext.SourceEntity = default(GameLogContext.Property<IMechanicEntity>);
                GameLogContext.TargetEntity = default(GameLogContext.Property<IMechanicEntity>);
                return scope;
            }

            private static bool RenderDisposedDeath(GameLogEvent evt, LogThreadBase logThread)
            {
                var death = evt as GameLogEventLifeStateChanged;
                LifeSnapshot snapshot;
                if (!(logThread is UnitLifeStateChangedLogThread) || death == null
                    || !death.Unit.IsDisposed || !LifeSnapshots.TryGetValue(death, out snapshot)) return false;
                if (snapshot.Dead)
                {
                    GameLogContext.SourceEntity = death.Unit;
                    // 原版相同的本地化消息、无 tooltip 包装及 AddMessage 通知；不再读取拆除的 Part。
                    var text = snapshot.FinallyDead ? GameLogStrings.Instance.UnitDeath
                        : GameLogStrings.Instance.UnitFallsUnconscious;
                    var message = text.CreateCombatLogMessage();
                    if (message != null)
                        AddMessage.Invoke(logThread, new[] { CopyMessage.Invoke(new object[] { message, null, false, death.Unit }) });
                }
                return true;
            }

            // 不吞异常；即使原版详情构建失败，也不能污染同批的经验/废料等后续日志。
            private static void End(RenderScope scope)
            {
                if (scope == null) return;
                Current = scope.Previous;
                GameLogContext.SourceEntity = Current != null ? scope.Source : default(GameLogContext.Property<IMechanicEntity>);
                GameLogContext.TargetEntity = Current != null ? scope.Target : default(GameLogContext.Property<IMechanicEntity>);
            }
        }

        [HarmonyPatch(typeof(GameLogContext), nameof(GameLogContext.GetIcon))]
        internal static class DisposedIcon
        {
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                var getter = AccessTools.PropertyGetter(typeof(IMechanicEntity), "IsPlayerFaction");
                var replacement = AccessTools.Method(typeof(SpaceFleetLogPatches), nameof(DisplayPlayerFaction));
                int count = 0;
                foreach (var code in codes)
                    if (code.Calls(getter))
                    {
                        code.opcode = OpCodes.Call;
                        code.operand = replacement;
                        count++;
                    }
                if (count != 2) throw new InvalidOperationException("GameLogContext.GetIcon faction reads changed: " + count);
                return codes;
            }
        }

        [HarmonyPatch(typeof(ReportCombatLogManager), "ManageCombatMessageData")]
        internal static class DisposedReportHeader
        {
            internal static void Prefix(ref MechanicEntity source, ref MechanicEntity target)
            {
                if (!Applies(source, target)) return;
                // 在实际报告入口处理参数，避免 ManageTooltipHeader 提前内联绕过补丁。
                // LogHelper.GetEntityName 已对 disposed 使用蓝图名，正文可安全保留。
                // 仅放弃尸体的 Name→Blueprint[uid] 调试替换，健康一端仍走原版。
                if (IsDisposed(source)) source = null;
                if (IsDisposed(target)) target = null;
            }
        }
    }
}
