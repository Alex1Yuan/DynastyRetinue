using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Parts;
using Warhammer.SpaceCombat.StarshipLogic.Weapon;

namespace DynastyRetinue
{
    /// <summary>
    /// 原版快速装填按每门炮每阶段一次登记，额外炮次会重复 Add 并抛异常。
    /// 只合并我方相关舰的重复记录；首次登记、清空与实际装填仍走原版。
    /// </summary>
    [HarmonyPatch(typeof(StarShipUnitPartRapidReload),
        nameof(StarShipUnitPartRapidReload.AbilityActivationNotification))]
    internal static class SpaceFleetRapidReloadPatch
    {
        private static bool Prefix(StarShipUnitPartRapidReload __instance,
            ItemEntityStarshipWeapon weapon, bool penalted,
            Dictionary<ItemEntityStarshipWeapon, bool> ___activatedWeapons)
        {
            if (!Main.Enabled || __instance == null || weapon == null
                || ___activatedWeapons == null) return true;
            var ship = __instance.Owner as StarshipEntity;
            if (!SpaceEscortService.IsOurShipSource(ship)) return true;
            bool previous;
            if (!___activatedWeapons.TryGetValue(weapon, out previous)) return true;
            // 任一次登记带惩罚就保留，结果不依赖同一阶段的事件顺序。
            ___activatedWeapons[weapon] = previous || penalted;
            return false;
        }
    }
}
