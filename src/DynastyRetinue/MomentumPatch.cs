using System;
using HarmonyLib;
using Kingmaker.Enums;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;

namespace DynastyRetinue
{
    /// <summary>
    /// 士气补丁 —— 堵住 v0.0.7 实战暴露的漏洞。
    ///
    /// RulePerformMomentumChange.OnTrigger 的 KillEnemy 分支：
    ///     m_ResolvesGain     = MaybeTarget.IsPlayerFaction ? ResolvesGainedForPartyMemberKill : ...
    ///     m_ResolvesGainFlat = MaybeTarget.IsPlayerFaction ? ResolvesGainedFlatForPartyMemberKill : ...
    /// 我们的卫兵挂的是 PlayerFaction，所以敌人杀掉一个卫兵，拿到的是
    /// **和杀掉一名真队友同一档**的士气奖励（ResolvesGained[3] = 1.0 倍系数 + flat）。
    ///
    /// 后果不是好看不好看的问题：卫兵血薄、死得快，等于每场战斗白送敌方一笔士气，
    /// 敌人动作更强 → 战斗真的变难。19:06 那场第一回合团灭就是这么来的。
    ///
    /// DeathAndTraumasDoesNotAffectMomentum 这个原版 feature 保护不了这条 ——
    /// 它在 MomentumController L236 只在 mechanicEntity == unit（自杀）时才拦。
    /// </summary>
    [HarmonyPatch(typeof(RulePerformMomentumChange), "OnTrigger")]
    public static class MomentumPatch
    {
        private static bool Prefix(RulePerformMomentumChange __instance)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.IsolateMomentum) return true;
                if (__instance.ChangeReason != MomentumChangeReason.KillEnemy) return true;

                var victim = __instance.MaybeTarget as BaseUnitEntity;
                if (victim == null || !RetinueTest.IsGuard(victim)) return true;

                Main.Log("[士气] 已拦截：击杀卫兵 " + (victim.Blueprint != null ? victim.Blueprint.name : "?")
                         + " 本应给敌方加士气（原版按「击杀队友」档计算），已归零");
                return false;   // 整个 OnTrigger 跳过 ⇒ 不结算、不加士气
            }
            catch { return true; }   // 补丁自身出错绝不能影响原版流程
        }

        /// <summary>自证日志：每次士气真变了，把原因/发起者/目标/增量/归属组全记下来。</summary>
        private static void Postfix(RulePerformMomentumChange __instance)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.WatchMomentum) return;
                var g = __instance.ResultGroup;
                if (g == null || __instance.ResultDeltaValue == 0) return;

                string who = Name(__instance.ConcreteInitiator);
                string tgt = __instance.MaybeTarget != null ? " -> " + Name(__instance.MaybeTarget) : "";
                string grp = g.IsParty ? "队伍" : (g.IsDefaultEnemy ? "敌方" : "其他组");
                string mark = RetinueTest.IsGuard(__instance.ConcreteInitiator as BaseUnitEntity) ? " [卫兵]" : "";

                Main.Log("[士气] " + __instance.ChangeReason + "  " + who + mark + tgt
                         + "  Δ" + (__instance.ResultDeltaValue > 0 ? "+" : "") + __instance.ResultDeltaValue
                         + "  " + grp + " " + __instance.ResultPrevValue + "->" + __instance.ResultCurrentValue);
            }
            catch { }
        }

        private static string Name(object e)
        {
            var u = e as BaseUnitEntity;
            if (u == null) return "?";
            try { return u.Blueprint != null ? u.Blueprint.name : "?"; } catch { return "?"; }
        }
    }
}