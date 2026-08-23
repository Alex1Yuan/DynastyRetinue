using System;
using HarmonyLib;
using Kingmaker.Pathfinding;
using Kingmaker.UI.SurfaceCombatHUD;
using Kingmaker.UnitLogic.Abilities.Components.Patterns;
using Pathfinding;

namespace DynastyRetinue
{
    /// <summary>
    /// 【诊断】把武器射界（pattern）的原点和覆盖范围打出来，和船的占位一比就知道偏没偏。
    ///
    /// ★为什么从 casterRect 换到 pattern★
    ///   一开始查的是射程环 PopulateAbilityRangeAreas(casterRect, ...)，
    ///   实测 casterRect 完全正确（巡洋 2×4、大巡 3×6，和分档精确匹配）。
    ///   但日志里这些武器的 maxRange 是 **100000**，而那个方法第一行就是
    ///       if (maxRange > 0 &amp;&amp; maxRange <= 200)
    ///   —— 条件不成立，整个方法什么都不画。**射程环那条路对这些武器根本没启用。**
    ///   玩家看到的橙色轮廓来自另一条：PopulateAbilityPatternAreas(pattern, ...)，
    ///   而那个方法只是把 pattern.Nodes 转发进显示列表，一个格子都不算。
    ///
    /// ★这条和移动格性质不同★
    ///   移动格是"显示没跟上判定"，纯视觉。
    ///   而这里显示的就是 pattern 本身 —— **判定用的也是它**。
    ///   所以玩家说"右边最近那格打不到"，那是真的打不到，不是画错了。
    ///   要改就是改玩法判定，风险等级完全不一样，先测准再谈。
    ///
    /// ★判据★
    ///   打印 ApplicationNode（射界原点）和所有节点的坐标范围，对照船的占位格：
    ///     · 原点不在船首中央 → 原点选取对奇数宽度的船有偏差
    ///     · 覆盖范围整体偏向一侧 → 和玩家说的"左边缺一格"对得上
    ///
    /// ★只读★ 纯打日志，挂在「详细日志」开关后面，同内容只记一次。
    /// </summary>
    [HarmonyPatch(typeof(CombatHUDRenderer), "PopulateAbilityPatternAreas")]
    internal static class ShipPatternProbe
    {
        private static string _last = "";
        private static int _logged;
        private const int MaxLogs = 120;

        private static void Prefix(OrientedPatternData pattern, bool buildPrimaryArea)
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;
                if (_logged >= MaxLogs) return;

                var app = pattern.ApplicationNode;
                if (app == null) return;

                // 存一份给全量探针 —— 射界产生在这里，船位/绿格产生在别处，
                // 只有存下来才能在同一时刻打成一份可以直接相减的数据。
                try
                {
                    ShipTelemetry.Pattern.Clear();
                    foreach (CustomGridNodeBase nd in pattern.Nodes)
                    {
                        if (nd == null) continue;
                        ShipTelemetry.Pattern.Add(
                            new UnityEngine.Vector2Int(nd.XCoordinateInGrid, nd.ZCoordinateInGrid));
                    }
                    ShipTelemetry.PatternOrigin =
                        new UnityEngine.Vector2Int(app.XCoordinateInGrid, app.ZCoordinateInGrid);
                    ShipTelemetry.HasPattern = ShipTelemetry.Pattern.Count > 0;
                }
                catch { }

                // 覆盖范围：所有节点的格坐标包围盒 + 总数
                int minX = int.MaxValue, maxX = int.MinValue;
                int minZ = int.MaxValue, maxZ = int.MinValue, n = 0;
                try
                {
                    foreach (CustomGridNodeBase node in pattern.Nodes)
                    {
                        if (node == null) continue;
                        int x = node.XCoordinateInGrid, z = node.ZCoordinateInGrid;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (z < minZ) minZ = z;
                        if (z > maxZ) maxZ = z;
                        n++;
                    }
                }
                catch { }
                if (n == 0) return;

                // 船的占位 —— 对照组
                string ship = "?";
                try
                {
                    var game = Kingmaker.Game.Instance;
                    var sh = game != null && game.Player != null ? game.Player.PlayerShip : null;
                    if (sh != null)
                    {
                        var node = sh.CurrentUnwalkableNode as CustomGridNodeBase;
                        string anchor = node != null
                            ? "(" + node.XCoordinateInGrid + "," + node.ZCoordinateInGrid + ")" : "?";
                        ship = "分档=" + StarshipTool.CurrentSize()
                             + "　SizeRect=" + sh.SizeRect
                             + "　锚点格=" + anchor
                             + "　朝向=" + sh.Orientation.ToString("F0") + "°";
                    }
                }
                catch { }

                // 当前选中的技能名 —— ★没有它这些数据没法用★
                //   一条记录不知道是哪门炮，就无法判断"这个覆盖范围对不对"：
                //   船首炮该在前方、侧舷炮该在侧面，拿错了参照系推出来的结论全是错的。
                //   （第一版探针就吃了这个亏：把两门不同的炮混在一起比，
                //     得出"原点偏了一格"的假结论。）
                string ability = "?";
                try
                {
                    var cc = Kingmaker.Game.Instance != null ? Kingmaker.Game.Instance.CursorController : null;
                    var ab = cc != null ? cc.SelectedAbility : null;
                    if (ab != null && ab.Blueprint != null)
                        ability = ab.Blueprint.name;
                }
                catch { }

                string line = "技能=" + ability
                            + "　原点=(" + app.XCoordinateInGrid + "," + app.ZCoordinateInGrid + ")"
                            + "　覆盖 x:" + minX + "~" + maxX + " z:" + minZ + "~" + maxZ
                            + "　共" + n + "格　主区=" + buildPrimaryArea
                            + "　★对照 " + ship;

                if (line == _last) return;
                _last = line;
                _logged++;
                Main.Log("[射界] " + line + (_logged == MaxLogs ? "　※已达上限※" : ""));
            }
            catch { }
        }
    }
}
