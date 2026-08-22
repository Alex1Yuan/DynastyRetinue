using System;
using System.Reflection;
using HarmonyLib;

namespace DynastyRetinue
{
    /// <summary>
    /// 【诊断·只读】把座舰每个武器槽的射界参数打出来。
    ///
    /// ================== 为什么需要它 ==================
    /// 之前十几个版本一直在改 AoEPattern.GetOriented，那其实是
    /// RestrictedFiringAreaComponent 用的**角度限制蒙版**（日志里恒为 93/87 格的东西），
    /// 不是舰炮射界本身。真正的链路是：
    ///
    ///     WeaponSlot（Type + OffsetFromProw + BatteryWidth + SizeRect + 朝向）
    ///         → GetFiringArcSourceNodesOffsets(...)   炮组源格相对船锚点的偏移
    ///         → GetFiringArcSourceNodes(...)          落到具体格
    ///         → FiringArcHelper.TraverseGraph(...)    展开射程
    ///         → 与角度蒙版求交、多源格 UnionWith、减去船体自身占格
    ///
    /// 而 **OffsetFromProw / BatteryWidth 正是玩家规格里的那两个量**：
    ///     「起点从船首往后挪一格」 = OffsetFromProw
    ///     「奇数取中间格、偶数取两格中点」 = BatteryWidth 的语义
    /// 所以不需要在外面硬掰坐标 —— 引擎自己就用这套参数描述炮组位置。
    ///
    /// ================== 为什么挂在这里 ==================
    /// GetRestrictedFiringArcNodes 是**实例方法**，能拿到 __instance：
    ///   · __instance.Type          槽位
    ///   · __instance.OffsetFromProw / BatteryWidth   要看的两个量
    ///   · __instance.Owner         用来确认是不是玩家座舰（静态方法拿不到这个）
    ///
    /// 而且它同时在显示与命中判定两条路径上（IsTargetInsideRestrictedFiringArc 走的是
    /// 同一套源格计算），所以在这里看到的参数就是两边共用的那一份。
    ///
    /// ★这一版只读不改★
    ///   先确认真实取值再决定改什么。上一轮的教训是"没确认这个方法真的产出你要改的东西"
    ///   就动手，白费了十几个版本和玩家很多轮测试。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipWeaponSlotProbe
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Warhammer.SpaceCombat.StarshipLogic.Weapon.WeaponSlot");
            if (t == null) return null;
            return AccessTools.Method(t, "GetRestrictedFiringArcNodes");
        }

        private static bool Prepare()
        {
            var m = TargetMethod();
            Main.Log("[炮组参数] 探针挂载 " + (m != null
                ? "成功 → WeaponSlot.GetRestrictedFiringArcNodes"
                : "失败：找不到 WeaponSlot.GetRestrictedFiringArcNodes"));
            return m != null;
        }

        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static bool _warned;

        private static void Prefix(object __instance, int range)
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;
                if (__instance == null) return;

                var t = __instance.GetType();
                object type = Get(t, __instance, "Type");
                object prow = Get(t, __instance, "OffsetFromProw");
                object width = Get(t, __instance, "BatteryWidth");
                object arc = Get(t, __instance, "FiringArc");
                object owner = Get(t, __instance, "Owner");

                // 只关心玩家座舰；敌舰的参数打出来只会混淆
                var game = Kingmaker.Game.Instance;
                var ship = game != null && game.Player != null ? game.Player.PlayerShip : null;
                bool mine = ship != null && ReferenceEquals(owner, ship);

                string key = (type ?? "?") + "|" + (mine ? "我" : "敌") + "|" + range;
                if (!_seen.Add(key)) return;

                Main.Log("[炮组参数] " + (mine ? "★座舰" : "  敌舰") + "　槽位=" + (type ?? "?")
                       + "　OffsetFromProw=" + (prow ?? "?")
                       + "　BatteryWidth=" + (width ?? "?")
                       + "　FiringArc=" + (arc ?? "?")
                       + "　射程=" + range
                       + "\n    （OffsetFromProw = 炮组离船首几格；BatteryWidth = 炮组占几格宽。"
                       + "这两个就是玩家规格里的『往后挪一格』和『奇偶取中』）");
                Main.FlushLog(true);
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[炮组参数] 读取失败: " + e.Message); }
            }
        }

        /// <summary>字段优先、属性兜底。WeaponSlot 上 Type/OffsetFromProw/BatteryWidth 是字段，
        /// FiringArc/Owner 是属性 —— 之前吃过"只查属性所以永远读不到槽位"的亏。</summary>
        private static object Get(Type t, object obj, string name)
        {
            try { var f = AccessTools.Field(t, name); if (f != null) return f.GetValue(obj); } catch { }
            try { var p = AccessTools.Property(t, name); if (p != null) return p.GetValue(obj); } catch { }
            return null;
        }
    }
}
