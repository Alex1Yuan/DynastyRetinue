using System;
using HarmonyLib;
using Kingmaker.Designers.WarhammerSurfaceCombatPrototype.PsychicPowers;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands.Base;

namespace DynastyRetinue
{
    /// <summary>
    /// 卫兵放灵能不推高帷幕（亚空间威胁）。
    ///
    /// 为什么不能像士气那样做"独立池"：
    ///   VeilThicknessCounter.m_Value => Game.Instance.LoadedAreaState.AreaVailPart.Vail
    /// 帷幕是**整个区域唯一一个值**，不按队伍/组划分，结构上没有第二条可以隔离到的通道。
    /// 而累积入口 HandleUnitCommandDidEnd 对任何在战斗中的施法者一视同仁，
    /// 卫队满编十几个人、灵能卫兵放技能又频繁，玩家的威胁条会被推得飞快。
    ///
    /// 所以退而求其次：卫兵的灵能干脆不计入。玩家自己队伍的照常累积，机制体验不变。
    /// 只拦"是不是卫兵"这一条，其余原样交回原版。
    ///
    /// ★这个 Prefix 不判断是不是灵能★
    /// 判据只有「mod 开着 + 是卫兵」，然后 return false 跳过整个累积。
    /// 原版的灵能过滤（AbilityParamsSource == PsychicPower 之类）写在被 patch 的
    /// 方法体**内部**，Prefix 在它之前就返回了 —— 所以非灵能指令走到这里也会被拦，
    /// 但那本来就不会累积帷幕，拦了无害。
    ///
    /// ★教训：日志措辞要对得起判据★
    /// 这行日志原本写的是「已拦截：卫兵 X 的**灵能**不计入亚空间威胁」，
    /// 而它对卫兵的**任何**指令（移动、开枪、挥斧）都会打。
    /// 后来排查"某个兵不开枪"时，我把这行当成了灵能指示器，
    /// 据此推出"所有分型都在放灵能"——完全错误的结论，而且查了两轮才发现。
    /// 一条描述比实际判据更强的日志，比没有日志更糟。
    /// </summary>
    [HarmonyPatch(typeof(VeilThicknessCounter), nameof(VeilThicknessCounter.HandleUnitCommandDidEnd))]
    public static class VeilPatch
    {
        private static bool Prefix(AbstractUnitCommand command)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.GuardPsykerNoVeil) return true;
                if (command == null) return true;
                var u = command.Executor as BaseUnitEntity;
                if (u == null || !RetinueRegistry.IsGuard(u)) return true;

                if (Main.Settings.WatchMomentum)
                    Main.Log("[帷幕] 跳过累积：卫兵 " + (u.Blueprint != null ? u.Blueprint.name : "?")
                             + " 的指令（★这行对卫兵的**任何**指令都会打，不代表它放了灵能★）");
                return false;   // 整个累积跳过
            }
            catch { return true; }   // 补丁自身出错绝不能影响原版流程
        }
    }
}
