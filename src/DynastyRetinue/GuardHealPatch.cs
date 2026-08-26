using System;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Controllers;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;                 // EventInvokerExtensions
using Kingmaker.PubSubSystem.Core;
using Kingmaker.UnitLogic.Parts;

namespace DynastyRetinue
{
    /// <summary>
    /// 让卫兵和队友一样，在非战斗状态下自动回血。
    ///
    /// ── 病灶 ────────────────────────────────────────────────────────────────
    /// HealthController.HandleUnitStartTurn（Kingmaker.Controllers/HealthController.cs:81）：
    ///     partHealth.UpdateWoundsAndTraumasOnNewTurn(isTurnBased);          ← 无条件，卫兵本来就有
    ///     if (!TurnController.IsInTurnBasedCombat()
    ///         &amp;&amp; mechanicEntity.IsInPlayerParty                              ← ★卡在这里★
    ///         &amp;&amp; !mechanicEntity.Features.DoNotReviveOutOfCombat)
    ///         partHealth.HealDamageAll();
    ///
    /// 我们的卫兵**刻意不挂** UnitPartCompanion —— 挂了就会变成玩家可直控，
    /// 那就不是 AI 卫队了。代价是它们不在 Player.Party / PartyAndPets 里，
    /// `IsInPlayerParty` 为 false，于是这条自动回血轮不到它们。
    /// 同一个原因还让战斗后的群体医疗（TryHealPartyAfterCombat 遍历 PartyAndPets）
    /// 也跳过卫兵 —— 但那条被本补丁覆盖了：非战斗状态下反正会回满。
    ///
    /// 玩家反馈的原话是「卫队回到飞船上不会自动回血和恢复状态」。
    /// 其实不限于飞船 —— 队友在任何非战斗场合都是持续回满的，只是回船时最容易注意到。
    ///
    /// ── 修法 ────────────────────────────────────────────────────────────────
    /// Postfix 同一个方法，把原版那三个条件原样重跑一遍，只把 `IsInPlayerParty`
    /// 换成 `IsGuard`。**其余判据一个不改** —— 尤其 DoNotReviveOutOfCombat
    /// 那条要保留，否则将来给卫兵配上带这个标记的单位会出现「设计上不该回血却回了」。
    ///
    /// ★为什么不改成让卫兵进 Party★ 那等于挂 UnitPartCompanion，
    ///   会让卫兵变成玩家可直控的队友，整个 mod 的前提就没了。
    ///
    /// ★为什么不自己起一个定时器回血★ 那是另造一套语义。挂在原版同一个事件上，
    ///   触发时机、回血量、与创伤系统的关系全部沿用原版，将来官方改了逻辑我们跟着变。
    ///
    /// ★沉默要能诊断★ 这个补丁的正确表现是「什么都不打印」，而那和
    ///   「事件压根没给卫兵触发过」在日志里长得一模一样 —— 这个坑本项目栽过。
    ///   所以第一次真的给卫兵回血时记一行，之后不再记。
    /// </summary>
    [HarmonyPatch(typeof(HealthController), "HandleUnitStartTurn")]
    internal static class GuardHealPatch
    {
        private static bool _logged, _seen, _warned;

        private static void Postfix(bool isTurnBased)
        {
            try
            {
                var cfg = Main.Settings;
                if (!Main.Enabled || cfg == null || !cfg.GuardAutoHeal) return;

                // 原版取的是「本次事件的主角」，我们必须取同一个实体，
                // 否则会给错的人回血。
                var me = EventInvokerExtensions.MechanicEntity;
                var u = me as BaseUnitEntity;
                if (u == null || !RetinueRegistry.IsGuard(u)) return;

                // ★两条日志必须分开★
                //   只记「真的回了血」的话，日志空白有两种可能，而它们要做的事完全不同：
                //     ① 事件压根没给卫兵触发（实体级订阅，卫兵不是 companion，有可能收不到）
                //        ⇒ 这个挂载点无效，得换做法
                //     ② 事件来了，但卫兵没受伤所以无事可做
                //        ⇒ 补丁是好的，只是没机会表现
                //   新招的卫兵恒为满血，所以②是最常见的情况 —— 分不清就会误判成①。
                if (!_seen)
                {
                    _seen = true;
                    Main.Log("[卫兵回血] 挂载点有效：HandleUnitStartTurn 确实会为卫兵触发。"
                           + "（这一行只说明钩子通了，不代表回了血 —— 满血时本来就无事可做）");
                    Main.FlushLog(true);
                }

                // ★除了阵营判据，其余条件与原版逐条一致★
                if (Game.Instance != null && Game.Instance.IsSpaceCombat) return;
                if (Kingmaker.Controllers.TurnBased.TurnController.IsInTurnBasedCombat()) return;
                if (u.Features != null && u.Features.DoNotReviveOutOfCombat) return;

                var h = u.GetHealthOptional();
                if (h == null || h.Damage <= 0) return;

                int before = h.Damage;
                h.HealDamageAll();

                if (!_logged)
                {
                    _logged = true;
                    Main.Log("[卫兵回血] 已生效：非战斗状态下卫兵与队友一样自动回满。"
                           + "（首次触发记一行，之后不再记）本次修复伤害 " + before + " 点。"
                           + "\n    原版这条判据是 IsInPlayerParty，而卫兵刻意不挂 UnitPartCompanion"
                           + "（挂了会变成玩家可直控），所以一直被挡在外面。");
                    Main.FlushLog(true);
                }
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[卫兵回血] 失败（保持原版行为）: " + e.Message); }
            }
        }
    }
}
