using System;
using HarmonyLib;
using Kingmaker.Pathfinding;
using Kingmaker.UI.SurfaceCombatHUD;
using Pathfinding;

namespace DynastyRetinue
{
    /// <summary>
    /// 修正海战武器范围的焦点：把它从「锚点」挪回「船的真实占位」。
    ///
    /// ================== 病灶 ==================
    /// 射程环由 CombatHUDRenderer.PopulateAbilityRangeAreas(casterRect, ...) 喂给
    /// RingAreaSource，后者画的是「casterRect ⊕ 半径」的圆角矩形 —— 形状是对的，
    /// 但那个矩形**摆错了位置**：它以锚点为中心展开，而船其实是从锚点朝一侧长出去的。
    ///
    /// 实测（2026-08-22，大巡正轴向）：
    ///     船实际占位   x:250~252  z:250~255      锚点 (250,250)   中心 (251, 252.5)
    ///     casterRect   x:249~251  z:247~252                       中心 (250, 249.5)
    ///     偏差         Δx = -1.0   Δz = -3.0
    ///
    /// 偏差恰好等于 SizeRect 的中心 ((xmin+xmax)/2, (ymin+ymax)/2)：
    ///     原版 1×2 → (0, 0.5)   ⇒ 偏半格，**肉眼看不出来，所以原版从没暴露**
    ///     巡洋 2×4 → (0.5, 1.5) ⇒ 斜向实测 Δ=(0,-2)
    ///     大巡 3×6 → (1, 2.5)   ⇒ 实测 Δ=(-1,-3)
    ///
    /// 玩家看到的现象正是这个：Δx 让 3 格宽的船「左边缺一格」，
    /// Δz 是沿船轴的前后偏移（对称，所以正面不明显）。
    ///
    /// ================== 修法 ==================
    /// 进方法前把 casterRect 整体平移 +SizeRect 中心，让它盖住船真正占的格子。
    /// **尺寸一格不改**，只挪位置。
    ///
    /// ★为什么这是纯显示★
    ///   这个方法属于 CombatHUDRenderer，产物只喂给 RingAreaSource 画 HUD。
    ///   实际够不够得着走的是 WarhammerGeometryUtils.DistanceToInCells(delta, fromSize, toSize)，
    ///   那边拿 SizeRect 的**边界**去减，用的是相对锚点的偏移，本身自洽，和这里无关。
    ///   所以这个补丁不改变任何一发炮能不能打中 —— 只是让画出来的范围和真实情况对上。
    ///   （若日后证实判定也用了同一个错焦点，那要另开一处修，且必须进设置指纹。）
    ///
    /// ★用施法者自己的尺寸★
    ///   不能拿玩家座舰的 SizeRect 去修所有人的范围 —— 敌舰、鱼雷都会走这条。
    ///   从 CursorController.SelectedAbility.Caster 取，取不到就不动（宁可不修，不能修错）。
    /// </summary>
    [HarmonyPatch(typeof(CombatHUDRenderer), "PopulateAbilityRangeAreas")]
    internal static class ShipRangeFocusPatch
    {
        private static bool _warned;

        private static void Prefix(ref IntRect casterRect)
        {
            try
            {
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.ShipRangeFocusFix) return;

                var caster = Caster();
                if (caster == null) return;

                var center = ShipCenterCell(caster);
                if (center == null) return;
                var c = center.Value;

                // ★用游戏自己的带朝向中心，别自己推旋转★
                //   第一版拿 SizeRect 直接算中心，90°/270° 下必错 ——
                //   SizePathfindingHelper.GetSizeRect() 是 `GetRectForSize(entity.Size)`，
                //   只看 Size 枚举，**永远是本地的 2×4，不随朝向变**；
                //   而 casterRect 的 W/H 会跟着转（270° 时是 4×2）。拿前者去对后者，转 90° 就偏。
                //   自己推旋转公式也试过，锚点在旋转后落在哪个角并不直观，符号怎么都对不上。
                //   GetSizePositionOffset(entity) 内部走的是 (SizeRect, entity.Forward)，
                //   带朝向，是游戏自己在用的那套 —— 直接借它最稳。
                float rectCx = (casterRect.xmin + casterRect.xmax) / 2f;
                float rectCz = (casterRect.ymin + casterRect.ymax) / 2f;

                int dx = UnityEngine.Mathf.RoundToInt(c.x - rectCx);
                int dz = UnityEngine.Mathf.RoundToInt(c.y - rectCz);
                if (dx == 0 && dz == 0) return;

                casterRect = new IntRect(casterRect.xmin + dx, casterRect.ymin + dz,
                                         casterRect.xmax + dx, casterRect.ymax + dz);
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[射程焦点] 修正失败（不影响其它功能）: " + e.Message); }
            }
        }

        /// <summary>
        /// 施法者在网格上的真实中心（格坐标，**浮点**）。
        /// 走 GetSizePositionOffset —— 它内部带 entity.Forward，所以天然是旋转后的位置。
        ///
        /// ★必须是浮点，不能吸附到格子★
        ///   第一版拿 AstarPath.GetNearest() 把中心吸到最近的格子上，等于做了一次取整；
        ///   而 casterRect 在偶数宽度下中心是 x.5，两者天然差半格 —— 实测 270° 下
        ///   Δ 卡在 (-0.5, +0.5) 下不来，玩家看到的就是"整体偏向船尾半格"。
        ///   改成「锚点格坐标 + 偏移换算成的格数」，全程浮点，也不需要知道网格原点。
        /// </summary>
        internal static UnityEngine.Vector2? ShipCenterCell(Kingmaker.EntitySystem.Entities.MechanicEntity caster)
        {
            try
            {
                var node = caster.CurrentUnwalkableNode as CustomGridNodeBase;
                if (node == null) return null;

                var off = Kingmaker.Code.Enums.Helper.SizePathfindingHelper.GetSizePositionOffset(caster, true);
                float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                if (cell <= 0.001f) return null;

                return new UnityEngine.Vector2(node.XCoordinateInGrid + off.x / cell,
                                               node.ZCoordinateInGrid + off.z / cell);
            }
            catch { return null; }
        }

        /// <summary>当前正在瞄准的那个技能的施法者。拿不到就返回 null，宁可不修也不修错。</summary>
        private static Kingmaker.EntitySystem.Entities.MechanicEntity Caster()
        {
            try
            {
                var g = Kingmaker.Game.Instance;
                var cc = g != null ? g.CursorController : null;
                var ab = cc != null ? cc.SelectedAbility : null;
                return ab != null ? ab.Caster : null;
            }
            catch { return null; }
        }

        /// <summary>施法者占的锚点格。SizeRect 是相对它展开的。</summary>
        private static CustomGridNodeBase CasterNode(Kingmaker.EntitySystem.Entities.MechanicEntity caster)
        {
            try { return caster.CurrentUnwalkableNode as CustomGridNodeBase; }
            catch { return null; }
        }
    }
}
