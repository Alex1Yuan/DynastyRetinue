using System;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Parts;

namespace DynastyRetinue
{
    /// <summary>
    /// 拦截 PartInventory.RestoreSharedInventory —— 这是 v0.1.0 代码审查揪出的 blocker。
    ///
    /// 原本以为 Faction.Set 之后补一句 EnsureOwn() 就能修好，实际完全无效：
    ///   PartFaction.Set  → EventBus.RaiseEvent(IUnitFactionHandler.HandleFactionChanged)  ← 同步
    ///   PartInventory.HandleFactionChanged → Setup(); RestoreSharedInventory();
    ///   RestoreSharedInventory 的四道跳过闸门（OwnerUnit==null / !HasOwnInventory /
    ///     !Faction.IsPlayer / IsPet）卫兵一道都不触发 ⇒ 整套装备被 Transfer 进
    ///     Game.Instance.Player.Inventory（随 player.json 存档）
    /// 等 EnsureOwn() 跑到时，东西已经搬完了，它只会再造一个**空集合**给卫兵 ——
    /// 净结果是两头落空：装备在玩家仓库里，卫兵背包是空的，装备件却还挂在卫兵槽位上。
    ///
    /// 另一条触发路径：PartInventory 还实现了 ICompanionStateChanged，
    /// 所以 ApplyRuntimeState 里的自愈 SetState 也会再倒一次。
    /// 直接在源头拦，两条路径一并覆盖，且零存档足迹。
    /// </summary>
    [HarmonyPatch(typeof(PartInventory), nameof(PartInventory.RestoreSharedInventory))]
    public static class InventoryPatch
    {
        private static bool Prefix(PartInventory __instance)
        {
            try
            {
                if (!Main.Enabled) return true;
                var u = __instance.ConcreteOwner as BaseUnitEntity;
                if (u == null || !RetinueRegistry.IsProtected(u)) return true;

                Main.Log("[背包] 已拦截 RestoreSharedInventory —— 保住卫兵 "
                         + (u.Blueprint != null ? u.Blueprint.name : "?") + " 的自带装备");
                return false;
            }
            catch { return true; }   // 补丁自身出错绝不能影响原版流程
        }
    }
}