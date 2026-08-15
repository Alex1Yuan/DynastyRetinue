using System;
using HarmonyLib;
using Kingmaker.Designers.WarhammerSurfaceCombatPrototype.PsychicPowers;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands.Base;

namespace KgdRetinue
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
                    Main.Log("[帷幕] 已拦截：卫兵 " + (u.Blueprint != null ? u.Blueprint.name : "?")
                             + " 的灵能不计入亚空间威胁");
                return false;   // 整个累积跳过
            }
            catch { return true; }   // 补丁自身出错绝不能影响原版流程
        }
    }
}
