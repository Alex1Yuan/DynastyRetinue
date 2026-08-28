using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 射界上下文：把「当前正在生成谁的 pattern」从上游传给下游。
    ///
    /// ★为什么需要它★
    ///   下游 AoEPattern.GetOriented(applicationNode, direction) **不带 caster、也不带 ability**，
    ///   下游要判断"这门炮装在哪个槽"只能去问 CursorController.SelectedAbility。
    ///   但那是**光标状态**：画射界时玩家正选着炮，条件成立；
    ///   真正结算命中时走的是另一条链，SelectedAbility 未必还是这门炮 ——
    ///   于是显示被改了、判定没改，玩家看到"展示的范围和实际范围不一致"。
    ///
    ///   AbilityData.GetPattern(target, casterPosition) 是显示和判定**共用**的上游，
    ///   而且手里就有 AbilityData。在这里记一笔，下游直接读，两条路就用同一份信息。
    ///
    /// ★必须在 Postfix 清掉★
    ///   留着会泄漏到下一次调用（可能是别的单位的技能），
    ///   那就变成"用 A 的槽位去改 B 的射界"，比不改更糟。
    /// </summary>
    internal static class ShipArcContext
    {
        internal static Kingmaker.UnitLogic.Abilities.AbilityData Current;
    }

    /// <summary>
    /// 舰炮射界：把生成射界用的**基准位置**改成船的真实占位中心。
    ///
    /// ================== 病灶 ==================
    /// 同一门左舷炮，只有 0° 是对的（玩家实测 + 逐行数据）：
    ///
    ///     朝向    船占位              射向  内边界  应为   垂直中心   船中心
    ///     0°      x:250~251 z:248~251  西    249     249 ✓  z 249.5    249.5 ✓
    ///     270°    x:241~244 z:260~261  南    259     259 ✓  x 243.5    242.5 +1
    ///     180°    x:225~226 z:255~258  东    228     227 +1 z 257.5    256.5 +1
    ///
    /// 0° 两项全中，其它朝向各偏一格；180° 射向和垂直方向同时偏，
    /// 内边界离船就空出两格 —— 正是玩家说的"靠船那一格打不到"。
    /// 偏差只在 0°（船的默认朝向）恰好抵消，符合"偏移量写死在 1×2 护卫舰上"这个判断。
    ///
    /// ================== 修法 ==================
    /// 射界由 AbilityData.GetPattern(target, casterPosition) 生成，
    /// **casterPosition 就是它的基准点**。不改形状、不碰只读的 OrientedPatternData，
    /// 只把这个基准换成船的真实占位中心。
    ///
    /// ★为什么用模型位置当占位中心★
    ///   探针反复验证过 `模型 − 真实占位中心 = (0,0)` ——
    ///   船的渲染位置本来就精确落在 GetBlockedNodes 给出的占位中心上。
    ///   直接取 View 的世界坐标，比再算一遍便宜得多，也不会引入第二套算法。
    ///
    /// ★为什么不重建形状★
    ///   各门炮的覆盖差别极大（MacroPlasma 294 格、MacroKinetic 44 格），
    ///   那是武器特性，不是 bug。自己重建等于把它们抹平成一个样子。
    ///
    /// ★显示和判定一起改★
    ///   GetPattern 是两者共用的出口，改这里两边同时生效 ——
    ///   这正是玩家要求的"显示和判定都要改"。也因为它影响命中判定，
    ///   开关**不进** CoopState.LocalOnly，必须参与设置指纹以保证联机一致。
    ///
    /// ★只动玩家座舰的舰炮★
    ///   地面单位、敌舰、非武器技能一概不碰 —— 任何一条不满足就原样返回。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipArcOrigin
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Kingmaker.UnitLogic.Abilities.AbilityData");
            if (t == null) return null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != "GetPattern") continue;
                var ps = m.GetParameters();
                if (ps.Length == 2 && ps[1].ParameterType == typeof(Vector3)) return m;
            }
            return null;
        }

        private static bool Prepare()
        {
            // ★挂没挂上必须说出来★
            //   上一版这里只是 return 布尔，找不到方法就静默跳过 ——
            //   于是日志里一片空白，"没生效"和"没执行"看起来一模一样，无法区分。
            //   探测失败不报告，已经让玩家白测过好几轮（槽位那次就是）。
            var m = TargetMethod();
            Main.Log("[射界] 补丁挂载 " + (m != null ? "成功 → " + m.DeclaringType.Name + "." + m.Name
                                                    : "失败：找不到 AbilityData.GetPattern(TargetWrapper, Vector3)"));
            return m != null;
        }

        private static bool _logged;
        private static bool _warned;
        private static bool _entered;

        private static void Prefix(object __instance, ref Vector3 casterPosition)
        {
                if (!Main.Enabled) return;   // 关掉 mod 就交还原版（OnToggle 不撤 Harmony 补丁）
            try
            {
                // ★把 ability 传给下游★ 下游 GetOriented 不带 ability，只能靠这里递一手。
                //   放在最前面：不管后面哪个条件提前 return，上下文都已经就位。
                ShipArcContext.Current = __instance as Kingmaker.UnitLogic.Abilities.AbilityData;

                if (!_entered)
                {
                    _entered = true;
                    Main.Log("[射界] GetPattern 首次进入（说明补丁确实在跑）");
                    Main.FlushLog(true);
                }

                var s = Main.Settings;
                if (s == null || !s.ShipArcFix) { Why("开关关着"); return; }

                var game = Kingmaker.Game.Instance;
                if (game == null || game.Player == null) { Why("拿不到 Game/Player"); return; }
                var ship = game.Player.PlayerShip;
                if (ship == null || ship.View == null || ship.View.gameObject == null)
                { Why("拿不到玩家座舰或它的 View"); return; }

                // 必须是这条船自己的技能，否则别人的射界会被我们的基准污染
                var ab = __instance as Kingmaker.UnitLogic.Abilities.AbilityData;
                if (ab == null) { Why("__instance 不是 AbilityData"); return; }
                if (!ReferenceEquals(ab.Caster, ship)) { Why("施法者不是玩家座舰"); return; }

                // 必须是舰船武器（有槽位）；船体技能、撞角之类不碰
                var src = ab.SourceItem;
                if (src == null) { Why("技能没有来源物品"); return; }
                var t = src.GetType();
                object ws = Get(src, t, "WeaponSlot", "m_WeaponSlot");
                if (ws == null) { Why("来源物品读不到 WeaponSlot（类型 " + t.Name + "）"); return; }

                Vector3 center = ship.View.gameObject.transform.position;
                Vector3 fixedPos = new Vector3(center.x, casterPosition.y, center.z);

                if (!_logged && (s.WatchMomentum))
                {
                    _logged = true;
                    float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                    if (cell <= 0.001f) cell = 1f;
                    var slot = Get(ws, ws.GetType(), "Type", "m_Type");
                    Main.Log("[射界] 基准修正　槽位=" + (slot ?? "?")
                           + "　原 casterPosition=" + casterPosition.ToString("F2")
                           + "　改为=" + fixedPos.ToString("F2")
                           + "　差=" + ((fixedPos - casterPosition) / cell).ToString("F2") + " 格"
                           + "　（差为 0 说明游戏本来就用占位中心，问题不在这儿）");
                    Main.FlushLog(true);
                }

                // ★不再改 casterPosition★
                //   实测差只有半格 (0.50,-0.50)，转成格坐标就被取整吃掉，画面纹丝不动；
                //   而真正的病灶在 AoEPattern.GetOriented 的 applicationNode（见 ShipArcAlign）。
                //   两处同时改会叠成双重偏移，所以这里只保留观测，不动值。
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[射界] 基准修正失败（射界保持原样）: " + e.Message); }
            }
        }

        /// <summary>
        /// 用完即清。留着会泄漏到下一次调用（可能是别的单位的技能），
        /// 那就成了"拿 A 的槽位去改 B 的射界"，比不改更糟。
        /// </summary>
        private static void Postfix()
        {
            ShipArcContext.Current = null;
        }

        /// <summary>
        /// 提前返回的原因，每种只报一次。
        /// 没有它就只剩"什么都没发生"，分不清是补丁没挂上、还是被某个条件挡了。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _why =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static void Why(string reason)
        {
            try
            {
                if (!_why.Add(reason)) return;
                Main.Log("[射界] 未修正：" + reason);
                Main.FlushLog(true);
            }
            catch { }
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
    }
}
