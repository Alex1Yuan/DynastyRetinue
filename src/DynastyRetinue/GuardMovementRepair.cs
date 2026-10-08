using System;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Progression.Features;

namespace DynastyRetinue
{
    /// <summary>Remove the original encounter's stationary behavior from our regular gunner.
    /// Called during recruitment and runtime restoration, never from a frame loop.</summary>
    internal static class GuardMovementRepair
    {
        internal const string GunnerUnit = "5fc80452fb6a4e2db02cd0a305715446";
        internal const string OnPositionFeature = "72ee4c980513494ea0940951a1874a80";
        internal const string OnPositionBuff = "671bb18cf9474f6e90aae4f2377cbc25";

        internal static void Repair(BaseUnitEntity unit)
        {
            if (unit == null || unit.IsDisposed || !RetinueRegistry.IsGuard(unit)) return;
            var blueprint = unit.OriginalBlueprint ?? unit.Blueprint;
            if (blueprint == null || !string.Equals(blueprint.AssetGuid.ToString(), GunnerUnit,
                                                     StringComparison.OrdinalIgnoreCase)) return;
            RetinueRegistry.GetEliteTag(unit, out int _, out int eliteIndex);
            if (eliteIndex >= 0) return;

            bool changed = false;
            var source = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(OnPositionFeature);
            if (source != null && unit.Facts.Contains(source))
            {
                // Stop the turn-start action first, so it cannot reapply the buff.
                unit.Progression.Features.Remove(source);
                changed = true;
            }

            if (unit.Buffs != null)
                foreach (var buff in unit.Buffs.RawFacts.ToArray())
                {
                    if (buff == null || buff.Blueprint == null
                        || !string.Equals(buff.Blueprint.AssetGuid.ToString(), OnPositionBuff,
                                          StringComparison.OrdinalIgnoreCase)) continue;
                    // Native removal releases only this buff's CantMove contribution and area effect.
                    // Do not reset CantMove: a separate enemy control effect may still own it.
                    buff.Remove();
                    changed = true;
                }

            if (changed) Main.Log("[卫兵移动] 已清理连射兵的原版固守位置效果 uid=" + unit.UniqueId);
        }
    }
}
