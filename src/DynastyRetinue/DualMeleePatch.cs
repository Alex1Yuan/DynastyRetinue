using System;
using HarmonyLib;
using Kingmaker.Blueprints.Root;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.Items.Slots;

namespace DynastyRetinue
{
    /// <summary>
    /// 让**我们的**阿斯塔特卫兵能双手都拿近战。
    ///
    /// ★原版为什么不行★（HandSlot.IsItemSupported，一手复核过）
    ///     if (owner.Facts.Contains(BlueprintWarhammerRoot.Instance.CommonSpaceMarineFact))
    ///     {
    ///         if ( IsPrimaryHand &amp;&amp; weapon.Blueprint.IsMelee)  return false;   // 主手禁近战
    ///         if (!IsPrimaryHand &amp;&amp; weapon.Blueprint.IsRanged) return false;   // 副手禁远程
    ///     }
    ///   凡是带阿斯塔特标记的单位，被硬锁成「主手远程 + 副手近战」——
    ///   所以佐拉尔是爆矢手枪(主) + 链锯剑(副)，双近战放不进去。
    ///
    ///   ★这跟武器是不是双手武器无关★ 九件阿斯塔特近战武器全是单手。
    ///   我一开始就是拿"武器都是单手"论证"能双持"的，那个推理错了 ——
    ///   拦人的是**槽位**，不是武器。
    ///
    /// ★作用域必须尽可能窄★
    ///   只在三件事同时成立时放行：
    ///     ① 这是本 mod 的卫兵（RetinueRegistry.IsGuard）
    ///     ② 它的精英定义声明了 dualMelee
    ///     ③ 原版这次拒绝的理由**恰好**是上面那两行之一
    ///   其余拒绝理由（双手武器占位冲突、盾牌位置、机械触手…）全部原样交还。
    ///   主角、Ulfar、敌方阿斯塔特一概不受影响。
    ///
    /// ★存档影响★
    ///   放行之后玩家可能配出一个原版规则不允许的组合。物品和槽位都是原版的，
    ///   存档能正常读；但那个配置按原版规则算非法。原版一般不在读档时重新校验，
    ///   所以大概率没事 —— 这一条我没法打包票，页面上要写清楚。
    /// </summary>
    [HarmonyPatch(typeof(HandSlot), nameof(HandSlot.IsItemSupported))]
    internal static class DualMeleePatch
    {
        private static void Postfix(HandSlot __instance, ItemEntity item, ref bool __result)
        {
            // 原版已经允许 —— 什么都不做。这个补丁只做"放宽"，绝不收紧。
            if (__result) return;
            try
            {
                if (!Main.Enabled || Main.Settings == null) return;

                var owner = __instance.Owner as BaseUnitEntity;
                if (owner == null || !RetinueRegistry.IsGuard(owner)) return;

                var w = item as ItemEntityWeapon;
                if (w == null || w.Blueprint == null) return;

                // ② 只有声明了 dualMelee 的精英才放开
                if (!DualMeleeAllowed(owner)) return;

                // ③ 确认拒绝理由确实是阿斯塔特手位限制，而不是别的
                bool isMarine;
                try { isMarine = owner.Facts.Contains(BlueprintWarhammerRoot.Instance.CommonSpaceMarineFact); }
                catch { return; }
                if (!isMarine) return;

                bool blockedByMarineRule =
                    ( __instance.IsPrimaryHand && w.Blueprint.IsMelee) ||
                    (!__instance.IsPrimaryHand && w.Blueprint.IsRanged);
                if (!blockedByMarineRule) return;

                // ★还要排掉"双手武器占位"这条★
                //   原版在阿斯塔特分支之后还判了一次配对槽：一把双手武器
                //   会占掉两只手。若这次拒绝其实也踩了那一条，放行就会造出
                //   原版根本不允许的状态（两只手各拿一把双手武器）。
                //   保守起见：只要这把或配对槽里那把是双手武器，就不放行。
                try
                {
                    if (w.Blueprint.IsTwoHanded) return;
                    var pair = __instance.PairSlot;
                    if (pair != null && pair.MaybeItem is ItemEntityWeapon pw
                        && pw.Blueprint != null && pw.Blueprint.IsTwoHanded) return;
                }
                catch { return; }

                __result = true;
            }
            catch { /* 出任何岔子都保持原版结论 */ }
        }

        /// <summary>这名卫兵所属的精英定义有没有声明 dualMelee。</summary>
        private static bool DualMeleeAllowed(BaseUnitEntity g)
        {
            try
            {
                int ai = RetinueRegistry.ArchetypeOf(g);
                var arch = Archetypes.Get(ai);
                if (arch == null) return false;
                var def = GearTool.EliteDefOf(g, arch);
                return def != null && def.DualMelee;
            }
            catch { return false; }
        }
    }
}
