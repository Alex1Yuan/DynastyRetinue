using System;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Controllers.Units;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Enums;
using Kingmaker.RuleSystem.Rules;

namespace DynastyRetinue
{
    /// <summary>
    /// 卫兵杀敌给卫队自己的池子涨士气。
    ///
    /// 原版 MomentumController.HandleUnitLeaveCombatOrBecameUnconscious 里，
    /// 敌人阵亡时的判据是 `p.Blueprint == root.PartyGroup` —— **只有玩家队伍组得分**。
    /// 独立士气池方案下卫兵就成了"只出力不进账"：杀敌喂你的池子，
    /// 自己却只能靠 Resolve 慢慢攒，大招基本放不出来。
    ///
    /// 这里在 KillEnemy 结算完之后补一笔给卫队组：击杀者是卫兵时，
    /// 按同样的增量也给卫队池加一份。**不动玩家那份**，你照拿不误。
    ///
    /// 另外两条已经被别处覆盖，这里不用管：
    ///   卫兵倒地扣士气 → DeathAndTraumasDoesNotAffectMomentum（RetinueTest 挂的）
    ///   敌方因击杀卫兵得分 → MomentumPatch 的 Prefix
    /// </summary>
    [HarmonyPatch(typeof(RulePerformMomentumChange), "OnTrigger")]
    public static class GuardKillCreditPatch
    {
        private static void Postfix(RulePerformMomentumChange __instance)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null) return;
                if (!Main.Settings.SeparateMomentumPool || !Main.Settings.GuardKillFeedsOwnPool) return;
                if (__instance.ChangeReason != MomentumChangeReason.KillEnemy) return;
                if (__instance.ResultDeltaValue <= 0) return;

                var g = __instance.ResultGroup;
                if (g == null) return;

                // 副作用修正：卫队组是"非队伍组"，原版在**玩家单位阵亡**时会给所有
                // 非队伍组发 KillEnemy（实测 "KillEnemy Vladaym -> PascalCompanion  其他组 100->117"）。
                // 队友死了给卫队涨士气显然不对，扣回去。
                var gg0 = MomentumGroupPatch.GuardGroup();
                if (gg0 != null && ReferenceEquals(g, gg0))
                {
                    var victim0 = __instance.MaybeTarget as BaseUnitEntity;
                    if (victim0 != null && victim0.IsPlayerFaction && !RetinueRegistry.IsGuard(victim0))
                    {
                        gg0.AddMomentum(-__instance.ResultDeltaValue);
                        Main.Log("[士气] 队友阵亡不该给卫队加分，已扣回 " + __instance.ResultDeltaValue);
                    }
                    return;
                }

                if (!g.IsParty) return;                           // 只补"本该进队伍池"的那份

                var killer = __instance.ConcreteInitiator as BaseUnitEntity;
                if (killer == null || !RetinueRegistry.IsGuard(killer)) return;

                var gg = MomentumGroupPatch.GuardGroup();
                if (gg == null) return;

                gg.AddMomentum(__instance.ResultDeltaValue);
                Main.Log("[士气] 卫兵击杀，卫队池 +" + __instance.ResultDeltaValue + " -> " + gg.Momentum);
            }
            catch { }
        }
    }
}