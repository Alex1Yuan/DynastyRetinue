using System;
using HarmonyLib;
using Kingmaker.UI.SurfaceCombatHUD;
using Pathfinding;

namespace DynastyRetinue
{
    /// <summary>
    /// 【诊断】把射程范围用的 casterRect 打出来，和船的真实占位一对就知道偏在哪。
    ///
    /// ★为什么必须探测★
    ///   射程范围走 PopulateAbilityRangeAreas(casterRect, min, max, effective) →
    ///   RingAreaSource.Setup(casterRect, ...) → GridPatterns.GenerateRoundedRectangle(
    ///       inner, outer, rect.xmin, rect.ymin, rect.Width, rect.Height, ...)
    ///   最后一步算的是**绝对网格索引**（y * gridWidth + x），
    ///   所以 casterRect 必须是"船在网格上的实际占位"。
    ///   而 entity.SizeRect 读出来是 [x:0...2, y:0...5] 这种**从 0 开始的相对形状** ——
    ///   中间一定有一次"加上船的位置"的转换。填 casterRect 的地方静态搜不到，
    ///   而算法本身（RingAreaSource / GenerateRoundedRectangle）看着是对的，
    ///   所以嫌疑就落在那次转换上：是不是用了蓝图的原始尺寸（本船蓝图是 1×2 的剑级护卫舰）
    ///   而不是当前的 3×6。
    ///
    ///   一行日志就能定死：casterRect 的 xmin/ymin 是不是船的实际格坐标、
    ///   Width/Height 是不是当前分档的尺寸。
    ///
    /// ★只读★ Prefix 不改参数、不拦截，纯打日志，且挂在「详细日志」开关后面。
    /// ★节流★ 这个方法在鼠标悬停武器时每帧都会走，不节流会瞬间灌满日志。
    /// </summary>
    [HarmonyPatch(typeof(CombatHUDRenderer), "PopulateAbilityRangeAreas")]
    internal static class ShipRangeProbe
    {
        /// <summary>
        /// 已记录过的「朝向档 × 技能」组合。
        ///
        /// ★为什么不按条数封顶★
        ///   第一版是 MaxLogs=40 一刀切，结果测到第三个朝向就被截断，
        ///   玩家点了半天日志里一条没有，还得退出游戏重启才能重置 —— 白白浪费好几轮。
        ///   真正该去的重复是「同一个朝向、同一门炮」，那才是同一份数据；
        ///   换个朝向或换门炮就是新信息，不该被前面的挤掉。
        ///   按组合去重之后，测多久都不会被截断，也不会刷屏。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>面板上点「清空探针记录」时调，想重测同一个朝向就点一下。</summary>
        public static void Reset() { _seen.Clear(); }

        private static void Prefix(IntRect casterRect, int minRange, int maxRange, int effectiveRange)
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;

                // 船的真实占位 —— 对照组。
                // ★必须打格坐标，不能打世界坐标★
                //   casterRect 是格坐标，对照组打世界坐标的话两边没法直接比，
                //   只能跨记录、跨时刻去凑，而船中间可能已经移动过 —— 结论就不可信。
                //   同一条记录里给出「casterRect」和「船占哪几格」，重不重合一眼可见。
                string ship = "?";
                try
                {
                    var game = Kingmaker.Game.Instance;
                    var sh = game != null && game.Player != null ? game.Player.PlayerShip : null;
                    if (sh != null)
                    {
                        var node = sh.CurrentUnwalkableNode as Kingmaker.Pathfinding.CustomGridNodeBase;
                        string occ = "?";
                        if (node != null)
                        {
                            int ax = node.XCoordinateInGrid, az = node.ZCoordinateInGrid;
                            var r = sh.SizeRect;
                            occ = "x:" + (ax + r.xmin) + "~" + (ax + r.xmax)
                                + " z:" + (az + r.ymin) + "~" + (az + r.ymax)
                                + "（锚点 " + ax + "," + az + "）";
                        }
                        ship = "分档=" + StarshipTool.CurrentSize()
                             + "　船占 " + occ
                             + "　朝向=" + sh.Orientation.ToString("F0") + "°";
                    }
                }
                catch { }

                // 当前选中的技能 —— 没有它就不知道这条是哪门炮的
                string ability = "?";
                try
                {
                    var cc = Kingmaker.Game.Instance != null ? Kingmaker.Game.Instance.CursorController : null;
                    var ab = cc != null ? cc.SelectedAbility : null;
                    if (ab != null && ab.Blueprint != null) ability = ab.Blueprint.name;
                }
                catch { }

                // ★焦点差：基准必须是"带朝向的真实中心"★
                //   第一版拿 SizeRect 直接算中心，而 SizeRect 只看 Size 枚举、不随朝向变，
                //   所以 90°/270° 下这个 Δ 本身就是错的 —— 报出来的偏差是探针自己造的。
                //   改成和补丁同一个基准（SizePathfindingHelper.GetSizePositionOffset，带 Forward），
                //   两边口径一致，Δ 才有意义。
                string delta = "";
                try
                {
                    var g2 = Kingmaker.Game.Instance;
                    var cc2 = g2 != null ? g2.CursorController : null;
                    var ab2 = cc2 != null ? cc2.SelectedAbility : null;
                    var caster = ab2 != null ? ab2.Caster : null;
                    if (caster != null)
                    {
                        var c = ShipRangeFocusPatch.ShipCenterCell(caster);
                        if (c != null)
                        {
                            float rectCx = (casterRect.xmin + casterRect.xmax) / 2f;
                            float rectCz = (casterRect.ymin + casterRect.ymax) / 2f;
                            delta = "　★焦点差 Δx=" + (rectCx - c.Value.x).ToString("F1")
                                  + " Δz=" + (rectCz - c.Value.y).ToString("F1")
                                  + "（0,0 = 正好落在船中心）";
                        }
                    }
                }
                catch { }

                string line = "技能=" + ability
                            + "　casterRect=[x:" + casterRect.xmin + "~" + casterRect.xmax
                            + " z:" + casterRect.ymin + "~" + casterRect.ymax + "]"
                            + "（W=" + casterRect.Width + " H=" + casterRect.Height + "）"
                            + "　min=" + minRange + " eff=" + effectiveRange + " max=" + maxRange
                            + delta + "　★对照 " + ship;

                // 按「朝向档 × 技能」去重 —— 同一个朝向的同一门炮只记一次，
                // 换朝向或换炮就是新信息，不会被前面的挤掉。
                string key = OrientationBucket() + "|" + ability;
                if (!_seen.Add(key)) return;
                Main.Log("[射程] " + line);
            }
            catch { }
        }

        /// <summary>朝向归到 8 个档（每 45°一档）。同一档内的微小差异不算新数据。</summary>
        private static string OrientationBucket()
        {
            try
            {
                var g = Kingmaker.Game.Instance;
                var sh = g != null && g.Player != null ? g.Player.PlayerShip : null;
                if (sh == null) return "?";
                int b = UnityEngine.Mathf.RoundToInt(sh.Orientation / 45f) % 8;
                if (b < 0) b += 8;
                return (b * 45) + "°";
            }
            catch { return "?"; }
        }
    }
}
