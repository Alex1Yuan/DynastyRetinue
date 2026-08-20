using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 一件武器的美术挂不上，不该让**整条船**的武器都没有美术。
    ///
    /// ================= 为什么会这样（逐行）=================
    ///
    /// StarshipView.cs:93-125   SetAllEquipment()
    ///     foreach (ItemSlot equipmentSlot in hull.HullSlots.EquipmentSlots) EquipItemFromItemSlot(...);
    ///     foreach (WeaponSlot  weaponSlot   in hull.HullSlots.WeaponSlots)  EquipItemFromItemSlot(...);
    /// StarshipView.cs:140-207  EquipItemFromItemSlot(...) → EquipWeapon(...)
    /// StarshipView.cs:236-284  EquipWeapon(...)
    ///     if (weaponBP.StarshipEE == null) return;                       // 静默：这件武器没有美术资产
    ///     var d = weaponBP.StarshipEE.EEArtSlotsDescription;
    ///     if (d == null || d.Count &lt;= 0) return;                         // 静默：有资产但没挂件
    ///     foreach (var item in d) {
    ///         GameObject prefab = item.Prefab;                           // ← 可能是 null
    ///         foreach (var requiredSlots in item.RequiredSlots) {
    ///             var list = ItemSlots.FindAll(x =&gt; x.Type == requiredSlots.SlotType);
    ///             foreach (var item3 in list)
    ///                 Object.Instantiate(prefab, ...);                   // :266 ★prefab 为 null 就抛★
    ///         }
    ///     }
    ///
    /// ★ 这条链上**一个 try/catch 都没有** ★（已逐行核对）
    /// ⇒ 任何一件武器在 :266 抛异常，SetAllEquipment 的两个 foreach 就地中止，
    ///   **排在它后面的武器一件都装不上美术**。
    ///
    /// 而美术资产里确实存在名字就叫 "Prow_Empty" / "Dorsal_Empty" 的
    /// StarshipEquipmentEntity（鱼雷发射管一大批指向它们 —— 鱼雷本来就是船体开口、
    /// 没有外挂炮塔）。这类资产极容易带一个 Prefab 为空的描述项。
    ///
    /// 实测吻合：玩家两个存档，同一条船、同一版 mod、挂点诊断全 ✓，
    /// 一个能看到炮、一个**一门都看不到**（星图/舰桥/连挂点齐全的 Dictator 都没有）。
    /// 差异只剩装的武器不同 —— 正是"一件坏的拖垮全部"的形状。
    ///
    /// ================= 做法 =================
    /// 给 EquipWeapon 挂 Finalizer。Harmony 的 Finalizer 返回 null 即**吞掉异常**，
    /// 于是坏掉的那件武器只丢自己的美术，后面的照常装上。
    /// 选 EquipWeapon 而不是 SetAllEquipment，粒度才是"每件武器"；
    /// 挂在 SetAllEquipment 上的话第一件坏了后面还是全丢。
    ///
    /// 顺带把出事的武器名打出来 —— 这是唯一能指名道姓的地方，
    /// vanilla 那两条静默 return 和这次异常都不留任何痕迹。
    ///
    /// ★存档★ 纯视觉，不碰任何 [JsonProperty]。
    /// </summary>
    public static class ShipEquipArtGuard
    {
        private static int _reported;

        [HarmonyPatch]
        public static class EquipWeaponGuard
        {
            private static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("StarshipView");
                return t == null ? null : AccessTools.Method(t, "EquipWeapon");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null)
                    Main.LogError("[武器美术] 找不到 StarshipView.EquipWeapon —— "
                                + "「一件武器出错拖垮全船美术」的隔离没装上。");
                return m != null;
            }

            /// <summary>
            /// 返回 null = 吞掉异常。形参 __exception 由 Harmony 注入；
            /// weaponBP 形参名须与原方法一致才能拿到。
            /// </summary>
            private static Exception Finalizer(Exception __exception, object weaponBP)
            {
                if (__exception == null) return null;

                // 刷屏保护：SetAllEquipment 每次重建 view 都跑，坏武器每次都会抛。
                if (_reported < 6)
                {
                    _reported++;
                    string nm = "?";
                    try
                    {
                        var n = AccessTools.Field(weaponBP.GetType(), "name");
                        nm = n != null ? (n.GetValue(weaponBP) as string ?? "?") : weaponBP.ToString();
                    }
                    catch { }

                    Main.LogError("[武器美术] 装配「" + nm + "」的炮塔美术时抛异常，已拦下 —— "
                                + "**这件武器自己没有美术，但船上其余武器不受影响**。\n"
                                + "  vanilla 在 StarshipView.SetAllEquipment → EquipItemFromItemSlot → "
                                + "EquipWeapon 这条链上一个 try/catch 都没有，"
                                + "所以不拦的话它后面的武器会一件都装不上美术（表现为整条船看不到任何炮）。\n"
                                + "  异常: " + __exception.GetType().Name + ": " + __exception.Message
                                + (_reported == 6 ? "\n  （同类消息不再重复）" : ""));
                }
                return null;   // ★吞掉★
            }
        }

        /// <summary>换船/读档时复位刷屏计数，让新一轮的问题还能被看见。</summary>
        public static void ResetReport() { _reported = 0; }
    }
}
