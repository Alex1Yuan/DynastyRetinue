using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.View;
using Kingmaker.Visual.CharacterSystem;

namespace DynastyRetinue
{
    /// <summary>
    /// 【实验中，默认关闭】让卫兵**不显示所穿装备的外观**，只留合成外观。
    ///
    /// ★为什么需要★
    ///   合成外观（DollLook）拼好之后，装备的护甲还会把自己那层视觉叠上来 ——
    ///   `BlueprintArmorType.m_EquipmentEntity` / `BlueprintItemEquipment.EquipmentEntity`。
    ///   于是"给全队统一外观"到头来还是被各自的毕业装备盖掉一半。
    ///   1.0.71 实测截图里就是这个样子：我们的套件 + 各人的长袍肩甲混在一起。
    ///
    /// ★为什么是这一个函数★
    ///   装备视觉只有两个应用点，而且都走同一个提取函数：
    ///       UnitEntityView.UpdateBodyEquipmentModel      —— 初次建模，遍历所有槽位
    ///       UnitEntityView.HandleEquipmentSlotUpdated    —— 换装备时实时更新
    ///   两处都调 `ExtractEquipmentEntities(slot)`，而它又转调私有的
    ///   `ExtractEquipmentEntities(ItemEntity)`。挡住私有那个，两条路一起挡。
    ///
    /// ★为什么挡私有的而不是公开的★
    ///   公开的 `(ItemSlot)` 重载会把 AugmentSlot 分流到 `ExtractAugmentVisualEntities`，
    ///   那是**义体增强**（机械臂、义眼这些）。它属于身体的一部分，不是"穿的装备"，
    ///   关掉护甲外观时不该连人家的义肢一起抹掉。私有重载正好在分流之后，粒度对。
    ///
    /// ★纯表现层★
    ///   Character 是渲染组件，不进存档也不进实体哈希，所以这个开关可以是**纯本地偏好** ——
    ///   联机时你开朋友不开，两边看到的不一样，但同步不受影响。
    /// </summary>
    [HarmonyPatch(typeof(UnitEntityView), "ExtractEquipmentEntities", new Type[] { typeof(ItemEntity) })]
    internal static class GearLookPatch
    {
        private static bool Prefix(UnitEntityView __instance, ItemEntity __0,
                                   ref IEnumerable<EquipmentEntity> __result)
        {
            try
            {
                if (!Main.Enabled) return true;
                var s = Main.Settings;
                if (s == null || !s.HideGearLook) return true;

                // ★机械触须放行★ 和上面注释里说的义体增强是同一个道理：
                //   触须属于**身体的一部分**，不是"穿的装备"，关掉护甲外观时不该连它一起抹掉。
                //   但它走的是 Body.m_Mechadendrites 槽，没被 AugmentSlot 那条分流捞走，
                //   于是掉进这个私有重载、被一并清空。
                //   ★这是「贤者没有触须」的真因★ DarkMagos 自带 Coil/Utility/Central 三条，
                //   槽也满着（所以「装不上弹道机械触须」），但视觉被我们自己关掉了 ——
                //   查了六个版本的模型，问题其实在这里。
                if (IsMechadendrite(__0)) return true;

                var u = __instance != null ? __instance.EntityData as BaseUnitEntity : null;
                if (u == null || !RetinueRegistry.IsGuard(u)) return true;

                __result = Enumerable.Empty<EquipmentEntity>();
                return false;
            }
            catch (Exception e)
            {
                Main.LogError("[外观] 屏蔽装备视觉失败，按原版走: " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// 这件装备是不是机械触须。
        ///
        /// ★按内部名判而不是列 guid★ 触须物品有 6 种以上（Coil / Central / Utility /
        ///   Manipulator / Medicae / Ballistic / Bolter…），还有帕斯卡专属变体；
        ///   列 guid 早晚漏一个，而漏了的表现是"这条触须莫名其妙不见了"——
        ///   又是一个不报错的静默失败。内部名一律带 Mechadendrite，判它更稳。
        /// ★频率★ 只在建视图/换装备时走，不是每帧。
        /// </summary>
        private static bool IsMechadendrite(ItemEntity item)
        {
            try
            {
                if (item == null || item.Blueprint == null) return false;
                string n = item.Blueprint.name;
                return !string.IsNullOrEmpty(n)
                    && n.IndexOf("Mechadendrite", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }
}
