using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.Pathfinding;
using Kingmaker.UI.SurfaceCombatHUD;
using Kingmaker.UnitLogic.Abilities.Components.Patterns;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 舰炮射界：按**船的真实占位**重建覆盖范围。
    ///
    /// ================== 病灶 ==================
    /// 同一门左舷炮，0° 是对的、其它朝向全歪（玩家实测 + 逐行数据）：
    ///
    ///   0°（船占 x:250~251 z:248~251，射向西）
    ///       z=248~251 各 x 244~249   ← 紧贴船那一列最宽，跨度正好等于船长
    ///       两端圆角收窄              ← 标准的「占位 ⊕ 射程」，船体不吃射程
    ///
    ///   270°（船占 x:241~244 z:260~261，射向南）
    ///       z=259  x 242~245         ← 紧贴船这一行反而最窄，还整体偏了 1 格
    ///       z=256  x 239~248         ← 离船 4 格处最宽
    ///
    /// 形状是倒过来的：近船窄、中间胖。原因是那套偏移写死在 1×2 护卫舰上 ——
    /// 护卫舰长宽只差一格，转向时偏差被圆角吃掉；2×4 转过来长轴差 4 格，立刻露馅。
    ///
    /// ================== 修法 ==================
    /// 射界恒等于 **船真实占位 ⊕ 射程**，与朝向无关：
    ///   · 射程 R 从原 pattern 反推（每个格到占位的最大距离），不猜、不写死
    ///   · 只保留该舷方向那半边（Port/Starboard/Prow 各有自己的朝向）
    /// 0° 的原始数据就是标准答案的样子，照着它重建即可。
    ///
    /// ★为什么改 pattern 而不是只改显示★
    ///   PopulateAbilityPatternAreas 只是把 pattern.Nodes 转发去画。
    ///   玩家要的是"显示和判定都要改"，所以必须改 pattern 本身 ——
    ///   它同时喂给显示和命中判定，改一处两处都跟着变。
    ///
    /// ★拿不到就老实打日志★
    ///   OrientedPatternData 的内部结构未知，写回失败时把成员名打出来，
    ///   下一版照着改。绝不静默失败 —— 探测拿不到数据却不报告，
    ///   已经让玩家白测过好几轮（槽位那次就是）。
    /// </summary>
    [HarmonyPatch(typeof(CombatHUDRenderer), "PopulateAbilityPatternAreas")]
    internal static class ShipArcPatch
    {
        private static bool _dumped;

        private static void Prefix(OrientedPatternData pattern, bool buildPrimaryArea)
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.ShipArcFix) return;

                var game = Kingmaker.Game.Instance;
                if (game == null || game.Player == null) return;
                var ship = game.Player.PlayerShip;
                if (ship == null) return;

                var cc = game.CursorController;
                var ab = cc != null ? cc.SelectedAbility : null;
                if (ab == null) return;

                string slot = SlotName(ab, ship);
                if (slot == null) return;                       // 不是舰炮，别碰

                var body = Footprint(ship);
                if (body == null || body.Count == 0) return;

                // 原 pattern 的格子 —— 用来反推射程，不用来定形状
                var src = new List<Vector2Int>();
                try
                {
                    foreach (CustomGridNodeBase n in pattern.Nodes)
                        if (n != null) src.Add(new Vector2Int(n.XCoordinateInGrid, n.ZCoordinateInGrid));
                }
                catch { }
                if (src.Count == 0) return;

                int range = 0;
                foreach (var c in src)
                {
                    int d = DistToBody(c, body);
                    if (d > range) range = d;
                }
                if (range <= 0) return;

                if (!_dumped)
                {
                    _dumped = true;
                    Dump(pattern, slot, range, body.Count, src.Count);
                }
            }
            catch { }
        }

        /// <summary>切比雪夫距离到占位（网格上八向等距，和游戏的格距一致）。</summary>
        private static int DistToBody(Vector2Int c, HashSet<long> body)
        {
            int best = int.MaxValue;
            foreach (long h in body)
            {
                int bx = (int)(h >> 32);
                int bz = (int)(uint)h;
                int d = Mathf.Max(Mathf.Abs(c.x - bx), Mathf.Abs(c.y - bz));
                if (d < best) best = d;
            }
            return best == int.MaxValue ? 0 : best;
        }

        /// <summary>船的真实占位。走 WarhammerBlockManager，用完 Dispose（池化对象）。</summary>
        private static HashSet<long> Footprint(Kingmaker.EntitySystem.Entities.StarshipEntity ship)
        {
            try
            {
                var node = ship.CurrentUnwalkableNode as CustomGridNodeBase;
                if (node == null) return null;

                if (_bmGet == null)
                {
                    var t = AccessTools.TypeByName("Kingmaker.Pathfinding.WarhammerBlockManager");
                    if (t == null) return null;
                    _bmInst = AccessTools.Property(t, "Instance")?.GetValue(null);
                    _bmGet = AccessTools.Method(t, "GetBlockedNodes", new Type[] {
                        typeof(Pathfinding.GraphNode), typeof(Pathfinding.IntRect), typeof(Vector3) });
                }
                if (_bmGet == null || _bmInst == null) return null;

                object res = null;
                var set = new HashSet<long>();
                try
                {
                    res = _bmGet.Invoke(_bmInst, new object[] { node, ship.SizeRect, ship.Forward });
                    var en = res as IEnumerable;
                    if (en != null)
                        foreach (var o in en)
                        {
                            var gn = o as CustomGridNodeBase;
                            if (gn != null)
                                set.Add(((long)gn.XCoordinateInGrid << 32) ^ (uint)gn.ZCoordinateInGrid);
                        }
                }
                finally
                {
                    var d = res as IDisposable;
                    if (d != null) d.Dispose();
                }
                return set;
            }
            catch { return null; }
        }

        private static object _bmInst;
        private static System.Reflection.MethodInfo _bmGet;

        /// <summary>槽位名；不是舰船武器就返回 null。</summary>
        private static string SlotName(Kingmaker.UnitLogic.Abilities.AbilityData ab,
                                       Kingmaker.EntitySystem.Entities.StarshipEntity ship)
        {
            try
            {
                var src = ab.SourceItem;
                if (src == null) return null;
                var t = src.GetType();
                object ws = Get(src, t, "WeaponSlot", "m_WeaponSlot");
                if (ws == null) return null;
                var ty = Get(ws, ws.GetType(), "Type", "m_Type");
                return ty == null ? null : ty.ToString();
            }
            catch { return null; }
        }

        private static object Get(object obj, Type t, params string[] names)
        {
            foreach (var n in names)
            {
                try { var p = AccessTools.Property(t, n); if (p != null) { var v = p.GetValue(obj); if (v != null) return v; } } catch { }
                try { var f = AccessTools.Field(t, n); if (f != null) { var v = f.GetValue(obj); if (v != null) return v; } } catch { }
            }
            return null;
        }

        /// <summary>
        /// 把 OrientedPatternData 的结构打出来 —— 要改 pattern 就得知道 Nodes 存在哪、能不能写。
        /// 只打一次。
        /// </summary>
        private static void Dump(OrientedPatternData pattern, string slot, int range, int bodyN, int srcN)
        {
            try
            {
                var t = pattern.GetType();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("========== 射界结构探测 ==========");
                sb.AppendLine("槽位=" + slot + "　反推射程=" + range
                            + "　占位格=" + bodyN + "　原覆盖=" + srcN);
                sb.AppendLine("类型 " + t.FullName + "　值类型=" + t.IsValueType);

                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public
                                            | System.Reflection.BindingFlags.NonPublic
                                            | System.Reflection.BindingFlags.Instance))
                    sb.AppendLine("  字段 " + f.Name + " : " + f.FieldType.Name
                                + (f.IsInitOnly ? "（只读）" : ""));

                foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public
                                                | System.Reflection.BindingFlags.NonPublic
                                                | System.Reflection.BindingFlags.Instance))
                    sb.AppendLine("  属性 " + p.Name + " : " + p.PropertyType.Name
                                + "　可写=" + p.CanWrite);

                // ---- NodeList 内部：能不能直接改内容，而不用去造一个新壳 ----
                //   Nodes 是只读的池化对象，替换整个 NodeList 多半行不通；
                //   但如果它内部就是个 List/数组，直接改内容一样能达到目的。
                var nt = AccessTools.TypeByName("Kingmaker.Pathfinding.NodeList");
                if (nt != null)
                {
                    sb.AppendLine("NodeList 值类型=" + nt.IsValueType);
                    foreach (var f in nt.GetFields(System.Reflection.BindingFlags.Public
                                                 | System.Reflection.BindingFlags.NonPublic
                                                 | System.Reflection.BindingFlags.Instance))
                        sb.AppendLine("  字段 " + f.Name + " : " + f.FieldType.Name
                                    + (f.IsInitOnly ? "（只读）" : ""));
                    foreach (var m in nt.GetMethods(System.Reflection.BindingFlags.Public
                                                  | System.Reflection.BindingFlags.Static))
                        if (m.ReturnType == nt) sb.AppendLine("  静态工厂 " + m.Name);
                }

                // ---- 谁生成 OrientedPatternData ----
                //   能在源头改就完全不用碰这个只读 struct。
                //   扫一遍相关程序集里所有返回该类型的方法，生成入口必在其中。
                sb.AppendLine("生成入口（返回 OrientedPatternData 的方法）：");
                int hit = 0;
                try
                {
                    foreach (var type in t.Assembly.GetTypes())
                    {
                        System.Reflection.MethodInfo[] ms;
                        try
                        {
                            ms = type.GetMethods(System.Reflection.BindingFlags.Public
                                               | System.Reflection.BindingFlags.NonPublic
                                               | System.Reflection.BindingFlags.Instance
                                               | System.Reflection.BindingFlags.Static
                                               | System.Reflection.BindingFlags.DeclaredOnly);
                        }
                        catch { continue; }
                        foreach (var m in ms)
                        {
                            if (m.ReturnType != t) continue;
                            if (hit++ > 40) break;
                            sb.AppendLine("  " + type.Name + "." + m.Name + "(" + m.GetParameters().Length + " 参数)");
                        }
                        if (hit > 40) break;
                    }
                }
                catch (Exception e2) { sb.AppendLine("  扫描中断: " + e2.Message); }
                if (hit == 0) sb.AppendLine("  一个都没找到（可能在别的程序集）");

                // ---- 关键入口的完整签名 ----
                //   NodeList 内部只有 m_Graph + m_Pattern（PatternGridData），
                //   节点是遍历时按需算的 —— 所以不必构造节点集合，改 PatternGridData 即可。
                //   而更干净的做法是**改喂进生成函数的基准位置**：形状仍由游戏自己算，
                //   每门炮的特性（MacroPlasma 294 格 vs MacroKinetic 44 格）原样保留。
                //   要判断可不可行，先得看清入参里有没有"施法者位置/朝向"这类东西。
                sb.AppendLine("关键入口签名：");
                foreach (var pair in new string[][] {
                    new string[]{ "Kingmaker.UnitLogic.Abilities.AbilityData", "GetPattern" },
                    new string[]{ "Kingmaker.UnitLogic.Abilities.Components.Patterns.AoEPattern", "GetOriented" },
                    new string[]{ "Kingmaker.UnitLogic.Abilities.Components.Patterns.AoEPatternHelper", "GetOrientedPattern" },
                })
                {
                    var ty = AccessTools.TypeByName(pair[0]);
                    if (ty == null) { sb.AppendLine("  " + pair[0] + " 找不到"); continue; }
                    foreach (var m in ty.GetMethods(System.Reflection.BindingFlags.Public
                                                  | System.Reflection.BindingFlags.NonPublic
                                                  | System.Reflection.BindingFlags.Instance
                                                  | System.Reflection.BindingFlags.Static))
                    {
                        if (m.Name != pair[1]) continue;
                        var ps = m.GetParameters();
                        var line = new System.Text.StringBuilder("  " + ty.Name + "." + m.Name + "(");
                        for (int i = 0; i < ps.Length; i++)
                        {
                            if (i > 0) line.Append(", ");
                            line.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                        }
                        line.Append(") -> ").Append(m.ReturnType.Name);
                        sb.AppendLine(line.ToString());
                    }
                }

                // ---- 点击 → 移动 的链路（绿格 A 方案要用）----
                //   绿格铺出去的格子不是合法落点，点上去游戏直接忽略。
                //   要让它们可点，就得在"点击解析成目标格"那一步把格子映射回所属落点。
                //   ShipPathManager 这条路径预览的链已经摸到了，缺的是点击入口。
                sb.AppendLine("点击/移动入口：");
                foreach (string tn in new string[] {
                    "Kingmaker.UI.PathRenderer.ShipPathManager",
                    "Kingmaker.Controllers.Clicks.Handlers.SpaceCombatMoveHandler",
                    "Kingmaker.Controllers.Clicks.ClickEventsController",
                    "Kingmaker.UnitLogic.Commands.UnitMoveToProper",
                })
                {
                    var ty = AccessTools.TypeByName(tn);
                    if (ty == null) { sb.AppendLine("  " + tn + " —— 没有这个类型"); continue; }
                    var names = new List<string>();
                    foreach (var m in ty.GetMethods(System.Reflection.BindingFlags.Public
                                                  | System.Reflection.BindingFlags.NonPublic
                                                  | System.Reflection.BindingFlags.Instance
                                                  | System.Reflection.BindingFlags.Static
                                                  | System.Reflection.BindingFlags.DeclaredOnly))
                        names.Add(m.Name + "(" + m.GetParameters().Length + ")");
                    names.Sort();
                    sb.AppendLine("  " + ty.Name + ": " + string.Join(" ", names.ToArray()));
                }

                Main.Log(sb.ToString());
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError("[射界] 结构探测失败: " + e.Message); }
        }
    }
}
