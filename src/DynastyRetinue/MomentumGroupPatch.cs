using System;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Controllers.Units;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;

namespace DynastyRetinue
{
    /// <summary>
    /// 卫队独立士气池。
    ///
    /// 问题：MomentumController.GetOrCreateMomentumGroupFromUnit 里
    ///     momentumGroupBlueprint = component != null ? component.MomentumGroup
    ///                            : (unit.IsPlayerFaction ? PartyGroup : DefaultEnemyGroup);
    /// 卫兵挂 PlayerFaction ⇒ 直接进 PartyGroup。后果两头都不对：
    ///   1. AbilityMomentumLogic.OnCast 从 GetGroup(caster) 扣士气 ⇒
    ///      卫兵放一次英雄壮举，从**玩家池子**里扣 75 起步
    ///      （IsCasterRestrictionPassed 只查 p.Momentum >= num，队伍士气够它就放）
    ///   2. 反过来每个卫兵回合开始也把自己的 Resolve 加进玩家池子 ——
    ///      6 个卫兵每回合白送 +30，偏 exploit
    ///
    /// 解法：让卫兵走原版自带的 PetSeparateMomentumGroup。
    /// 卫队有自己的士气条：自己攒、自己花，跟玩家队伍完全解耦。
    ///
    /// 注意这**不影响**击杀敌人给队伍加的士气 —— 那条走
    /// RulePerformMomentumChange.CreateKillEnemy(killer, victim, group)，
    /// group 是显式传入的 PartyGroup，与击杀者所在组无关。
    /// </summary>
    [HarmonyPatch(typeof(MomentumController), "GetOrCreateMomentumGroupFromUnit")]
    public static class MomentumGroupPatch
    {
        public const string SeparateGroupGuid = "a08961274b064065a3a67c37cdddc48a"; // PetSeparateMomentumGroup

        private static bool Prefix(MomentumController __instance, MechanicEntity unit, ref MomentumGroup __result)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.SeparateMomentumPool) return true;

                var u = unit as BaseUnitEntity;
                if (u == null || !RetinueRegistry.IsGuard(u)) return true;

                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintMomentumGroup>(SeparateGroupGuid);
                if (bp == null) return true;   // 解析不到就走原版逻辑，不冒险

                var groups = __instance.Groups;
                if (groups == null) return true;

                MomentumGroup found = null;
                foreach (var g in groups) { if (g != null && g.Blueprint == bp) { found = g; break; } }
                if (found == null) { found = new MomentumGroup(bp); groups.Add(found); }

                __result = found;
                return false;   // 跳过原版分组逻辑
            }
            catch { return true; }
        }

        /// <summary>拿卫队自己那个士气组（还没建就返回 null）。</summary>
        public static MomentumGroup GuardGroup()
        {
            try
            {
                var mc = Game.Instance != null && Game.Instance.TurnController != null
                       ? Game.Instance.TurnController.MomentumController : null;
                if (mc == null || mc.Groups == null) return null;
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintMomentumGroup>(SeparateGroupGuid);
                if (bp == null) return null;
                foreach (var g in mc.Groups) { if (g != null && g.Blueprint == bp) return g; }
                return null;
            }
            catch { return null; }
        }
    }
}