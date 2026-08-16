using System;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View;

namespace KgdRetinue
{
    internal static class ShipScaleLockProbe
    {
        // 自洽判据：只读实体自身状态，不依赖任何 session-only 静态量。
        internal static void Instantiate_Postfix(PartUnitViewSettings __instance, UnitEntityView __result)
        {
            try
            {
                if (__result == null) return;
                var ship = __instance.Owner as StarshipEntity;
                if (ship == null) return;
                string bpDefault = ship.Blueprint != null && ship.Blueprint.Prefab != null
                                 ? ship.Blueprint.Prefab.AssetId : null;
                string actual = __instance.PrefabGuid;
                if (string.IsNullOrEmpty(actual)) return;
                if (string.Equals(actual, bpDefault, StringComparison.Ordinal)) return;
                __result.DisableSizeScaling = true;
            }
            catch { }
        }
    }
}
