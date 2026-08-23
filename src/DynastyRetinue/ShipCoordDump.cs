using System;
using System.Text;
using HarmonyLib;
using Kingmaker.Pathfinding;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 【诊断】海战坐标全量导出 —— 一次把所有相关坐标打出来，不用一轮轮猜。
    ///
    /// ★为什么要全量★
    ///   船位偏移这个问题上，我已经错了三轮：先是"偏移不够"、再是"取整误差"、
    ///   再是"覆盖过头"。每轮都只拿到一两个数字，靠推理补其余的，推错就得玩家重测一次。
    ///   而真正需要的信息其实很少变：逻辑坐标、视图坐标、两者之差。
    ///   **差值就是实际生效的偏移** —— 有了它就不用去猜 GetSizePositionOffset 内部干了什么。
    ///
    /// ★关键的一行是「视图 − 逻辑」★
    ///   船的渲染位置 = 逻辑位置 + 偏移。把两个坐标都打出来，差值直接可读，
    ///   再和「船该在的位置」一比，要补多少格一目了然。
    /// </summary>
    internal static class ShipCoordDump
    {
        public static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== 海战坐标全量 ==========");

            try
            {
                var game = Kingmaker.Game.Instance;
                var ship = game != null && game.Player != null ? game.Player.PlayerShip : null;
                if (ship == null) { sb.AppendLine("拿不到 PlayerShip（不在海战里？）"); Flush(sb); return; }

                float cell = GraphParamsMechanicsCache.GridCellSize;
                sb.AppendLine("格边长 = " + cell.ToString("F3"));

                // ---------- 逻辑 ----------
                sb.AppendLine();
                sb.AppendLine("-------- 船（逻辑）--------");
                var node = ship.CurrentUnwalkableNode as CustomGridNodeBase;
                int ax = 0, az = 0;
                if (node != null)
                {
                    ax = node.XCoordinateInGrid; az = node.ZCoordinateInGrid;
                    sb.AppendLine("  锚点格 = (" + ax + ", " + az + ")");
                    sb.AppendLine("  锚点格世界坐标 = " + ((Vector3)node.position).ToString("F3"));
                }
                else sb.AppendLine("  拿不到 CurrentUnwalkableNode");

                var r = ship.SizeRect;
                sb.AppendLine("  Size = " + ship.Size + "　SizeRect = [x:" + r.xmin + "~" + r.xmax
                            + " y:" + r.ymin + "~" + r.ymax + "]（W=" + r.Width + " H=" + r.Height + "）");
                sb.AppendLine("  Position（逻辑世界坐标）= " + ship.Position.ToString("F3"));
                sb.AppendLine("  朝向 = " + ship.Orientation.ToString("F1") + "°");
                try { sb.AppendLine("  Forward = " + ship.Forward.ToString("F3")); } catch { }
                sb.AppendLine("  按 SizeRect 展开的占位格（**未旋转**）= x:" + (ax + r.xmin) + "~" + (ax + r.xmax)
                            + " z:" + (az + r.ymin) + "~" + (az + r.ymax));

                // ---------- 视图 ----------
                sb.AppendLine();
                sb.AppendLine("-------- 船（视图）--------");
                var view = ship.View;
                if (view != null && view.gameObject != null)
                {
                    var t = view.gameObject.transform;
                    sb.AppendLine("  view.position = " + t.position.ToString("F3"));
                    sb.AppendLine("  view.rotation.eulerY = " + t.eulerAngles.y.ToString("F1") + "°");
                    sb.AppendLine("  view.lossyScale = " + t.lossyScale.ToString("F3"));

                    // ★这一行是关键★ 视图 − 逻辑 = 实际生效的偏移
                    Vector3 diff = t.position - ship.Position;
                    sb.AppendLine("  ★视图 − 逻辑 = " + diff.ToString("F3")
                                + "　换算成格 = (" + (diff.x / cell).ToString("F2")
                                + ", " + (diff.z / cell).ToString("F2") + ")★");
                }
                else sb.AppendLine("  拿不到 View");

                // 游戏自己算的偏移 —— 和上面那个差值对比，能看出中途还有没有别的东西在动
                try
                {
                    var off = Kingmaker.Code.Enums.Helper.SizePathfindingHelper.GetSizePositionOffset(ship, true);
                    sb.AppendLine("  GetSizePositionOffset(ship,true) = " + off.ToString("F3")
                                + "　换算成格 = (" + (off.x / cell).ToString("F2")
                                + ", " + (off.z / cell).ToString("F2") + ")");
                }
                catch (Exception e) { sb.AppendLine("  读偏移失败: " + e.Message); }

                // ---------- 底座 ----------
                sb.AppendLine();
                sb.AppendLine("-------- 底座 / 选中标记 --------");
                try
                {
                    foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                    {
                        if (go == null) continue;
                        if (go.name.IndexOf("StarshipUnitMark", StringComparison.Ordinal) < 0) continue;
                        var t2 = go.transform;
                        sb.AppendLine("  " + go.name + "　pos = " + t2.position.ToString("F3")
                                    + "　scale = " + t2.lossyScale.ToString("F2"));
                        // 底座相对逻辑位置的偏移 —— 底座画在哪，才是"游戏认为船该在哪"
                        Vector3 d2 = t2.position - ship.Position;
                        sb.AppendLine("      相对逻辑位置 = " + d2.ToString("F3")
                                    + "　格 = (" + (d2.x / cell).ToString("F2")
                                    + ", " + (d2.z / cell).ToString("F2") + ")");
                        break;
                    }
                }
                catch { }

                // ---------- 武器槽 ----------
                sb.AppendLine();
                sb.AppendLine("-------- 武器槽 --------");
                WeaponSlots(ship, sb);

                // ---------- 当前选中技能 ----------
                sb.AppendLine();
                sb.AppendLine("-------- 当前选中技能 --------");
                try
                {
                    var cc = game.CursorController;
                    var ab = cc != null ? cc.SelectedAbility : null;
                    if (ab == null) sb.AppendLine("  没有选中技能（要看射程/射界，先悬停一门炮再点这个按钮）");
                    else
                    {
                        sb.AppendLine("  技能 = " + (ab.Blueprint != null ? ab.Blueprint.name : "?"));
                        try { sb.AppendLine("  施法者 = " + (ab.Caster != null ? ab.Caster.ToString() : "?")); } catch { }
                        try
                        {
                            var src = ab.SourceItem;
                            sb.AppendLine("  来源物品 = " + (src != null ? src.Blueprint.name : "无"));
                            if (src != null) sb.AppendLine("  来源槽位 = " + SlotTypeOf(src));
                        }
                        catch { }

                        // ★射界判定入口的线索★
                        //   射界「显示」走 PopulateAbilityPatternAreas，这个已经摸清了；
                        //   但「哪些格子能选」是另一条路，静态搜不到，反编译源也没有。
                        //   Owlcat 的惯例是把这类限制做成蓝图组件（AbilityTargetRestriction 之类），
                        //   所以把技能和武器蓝图上挂的组件类型名全列出来 —— 带 Arc/Slot/Restriction
                        //   字样的那个就是要找的东西，比continue猜方法名快得多。
                        try
                        {
                            if (ab.Blueprint != null)
                            {
                                sb.AppendLine("  技能蓝图类型 = " + ab.Blueprint.GetType().Name);
                                Components(ab.Blueprint, "  技能组件", sb);
                            }
                            var src2 = ab.SourceItem;
                            if (src2 != null && src2.Blueprint != null)
                            {
                                sb.AppendLine("  武器蓝图类型 = " + src2.Blueprint.GetType().Name);
                                Components(src2.Blueprint, "  武器组件", sb);
                            }
                        }
                        catch (Exception e2) { sb.AppendLine("  读组件失败: " + e2.Message); }
                    }
                }
                catch { }
            }
            catch (Exception e) { sb.AppendLine("导出失败: " + e.Message); }

            sb.AppendLine("========== 结束 ==========");
            Flush(sb);
        }

        /// <summary>列出船上所有武器槽：槽型（Prow/Port/Starboard/Dorsal/Keel）+ 装的什么。</summary>
        private static void WeaponSlots(Kingmaker.EntitySystem.Entities.StarshipEntity ship, StringBuilder sb)
        {
            try
            {
                var hull = AccessTools.Property(ship.GetType(), "Hull")?.GetValue(ship);
                if (hull == null) { sb.AppendLine("  拿不到 Hull"); return; }

                var slotsProp = AccessTools.Property(hull.GetType(), "WeaponSlots")
                             ?? AccessTools.Property(hull.GetType(), "Weapons");
                var slots = slotsProp?.GetValue(hull) as System.Collections.IEnumerable;
                if (slots == null)
                {
                    // 退一步：把 Hull 上所有属性名列出来，下一轮就知道该读哪个
                    sb.Append("  没有 WeaponSlots/Weapons，Hull 上的属性有：");
                    foreach (var p in hull.GetType().GetProperties()) sb.Append(p.Name).Append(' ');
                    sb.AppendLine();
                    return;
                }

                int n = 0;
                foreach (var slot in slots)
                {
                    if (slot == null) continue;
                    n++;
                    string type = "?";
                    try { type = AccessTools.Property(slot.GetType(), "Type")?.GetValue(slot)?.ToString() ?? "?"; }
                    catch { }
                    string item = "空";
                    try
                    {
                        var it = AccessTools.Property(slot.GetType(), "MaybeItem")?.GetValue(slot);
                        if (it != null)
                        {
                            var bp = AccessTools.Property(it.GetType(), "Blueprint")?.GetValue(it);
                            item = bp != null ? bp.ToString() : it.ToString();
                        }
                    }
                    catch { }
                    sb.AppendLine("  槽 " + n + "：类型 = " + type + "　装备 = " + item);
                }
                if (n == 0) sb.AppendLine("  一个槽都没读到");
            }
            catch (Exception e) { sb.AppendLine("  读武器槽失败: " + e.Message); }
        }

        /// <summary>
        /// 列出一个蓝图上挂的所有组件类型名。
        /// 全走反射：ComponentsArray 的元素类型在不同版本里换过位置，硬引用容易一升级就断，
        /// 而这段只是诊断，宁可读不到也不该把整个导出带崩。
        /// </summary>
        private static void Components(object bp, string label, StringBuilder sb)
        {
            try
            {
                var arr = AccessTools.Property(bp.GetType(), "ComponentsArray")?.GetValue(bp)
                            as System.Collections.IEnumerable;
                if (arr == null) { sb.AppendLine(label + " = 读不到 ComponentsArray"); return; }

                var names = new System.Collections.Generic.List<string>();
                foreach (var c in arr) if (c != null) names.Add(c.GetType().Name);
                sb.AppendLine(label + "（" + names.Count + "）= "
                            + (names.Count == 0 ? "无" : string.Join("， ", names.ToArray())));
            }
            catch (Exception e) { sb.AppendLine(label + " 读取失败: " + e.Message); }
        }

        private static string SlotTypeOf(object item)
        {
            try
            {
                var ws = AccessTools.Property(item.GetType(), "WeaponSlot")?.GetValue(item);
                if (ws == null) return "（不是舰船武器）";
                return AccessTools.Property(ws.GetType(), "Type")?.GetValue(ws)?.ToString() ?? "?";
            }
            catch { return "?"; }
        }

        private static void Flush(StringBuilder sb)
        {
            Main.Log(sb.ToString());
            Main.FlushLog(true);
        }
    }
}
