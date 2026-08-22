using System;
using System.Reflection;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 舰炮射界：修掉"把船压成正方形"导致的炮组源格错位。
    ///
    /// ================== 病灶 ==================
    /// WeaponSlot 里三个求炮组**起始格**的私有静态方法，开头都有同一行：
    ///
    ///     private static Vector2Int GetForeArcStartNodeOffset(int direction, IntRect starshipRect)
    ///     {
    ///         starshipRect.ymax = starshipRect.ymin + starshipRect.Width - 1;   // ← 把船压成 Width×Width
    ///         ...
    ///         case 3: case 6:  y = starshipRect.ymax;    // ← 只有部分 direction 读 ymax
    ///
    /// 它把船的矩形压成边长为**舷宽**的正方形，Height（舰长）被丢掉。
    ///     护卫舰 1×2 → 压成 1×1，只差 1 格且船宽只有 1 格 → 看不出来
    ///     巡洋   2×4 → 压成 2×2 → 起始格差 2 格
    ///     大巡   3×6 → 压成 3×3 → 差 3 格
    /// 而 ymax 只在 direction 为 2/3/5/6 时才被读取，所以**部分朝向正常、部分朝向错**
    /// —— 正是玩家一直反馈的"0° 没问题、一转向就歪"。
    ///
    /// ================== 证据（离线可复现）==================
    /// tools/firing_arc_offsets.ps1 直接调用这些静态方法（纯计算，不需要游戏运行）：
    ///
    ///     巡洋 2×4　左舷炮　dir=2
    ///       实际源格 (0,1) (0,0) (0,-1) (0,-2)   ← 炮组挂在船体**外面**（船的 y 范围是 0~3）
    ///       去掉压缩   (0,3) (0,2) (0,1) (0,0)   ← 正好贴着左舷那一列
    ///
    /// 炮组必须长在船身上，这一点不依赖任何朝向约定。带着压缩它跑到船尾外面去了，
    /// 这就是"舷炮离船一个身位"的来源。
    ///
    /// ================== 修法 ==================
    /// 只在 **Width ≥ 2** 时接管这三个方法，用真实的 ymax 重算；
    /// Width = 1（护卫舰，含所有敌舰）走原版，一行不碰 —— 它们本来就是对的。
    ///
    /// ★为什么改这里★
    ///   源格是显示和判定的**共同上游**：
    ///     显示   AbilitySingleTargetRange.GetPatternData → AbilityData.GetRestrictedFiringArcNodes
    ///            → WeaponSlot.GetRestrictedFiringArcNodes → GetFiringArcNodes → 本方法
    ///     判定   AbilityData.CanTargetFromNode / UnitUseAbilityParams.IsDirectionCorrect
    ///            → WeaponSlot.IsTargetInsideRestrictedFiringArc → 同一套源格
    ///   改一处两边同时对，不会出现"显示改了判定没改"。
    ///
    /// ★必须清缓存★
    ///   WeaponSlot.Offsets 是 StaticCache&lt;SourceNodesKey, Vector2Int[]&gt;，
    ///   内部 ConcurrentDictionary **没有失效接口**，生命周期是整个进程。
    ///   补丁挂上之前算过的值会一直留着，热重载也不会更新 —— 必须反射清掉，
    ///   否则会出现"代码改了但画面没变"，而这种现象极难和"公式算错"区分。
    ///
    /// ★联机★ 这个改动影响命中判定，开关必须参与设置指纹（沿用 ShipArcFix）。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipFiringArcFix
    {
        private const string SlotType = "Warhammer.SpaceCombat.StarshipLogic.Weapon.WeaponSlot";
        private static readonly string[] Methods =
        {
            "GetForeArcStartNodeOffset",
            "GetPortArcStartNodeOffset",
            "GetStarboardArcStartNodeOffset",
        };

        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var list = new System.Collections.Generic.List<MethodBase>();
            var t = AccessTools.TypeByName(SlotType);
            if (t == null) return list;
            foreach (var n in Methods)
            {
                var m = AccessTools.Method(t, n);
                if (m != null) list.Add(m);
            }
            return list;
        }

        private static bool Prepare()
        {
            int n = 0;
            foreach (var m in TargetMethods()) n++;
            Main.Log("[射界·源格] 补丁挂载 " + (n == 3
                ? "成功（三个起始格方法全部接管）"
                : "不完整：只找到 " + n + "/3 个 —— 射界保持原样"));
            return n == 3;
        }

        private static bool _cleared;
        private static bool _warned;
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static bool Prefix(int direction, IntRect starshipRect,
                                   ref Vector2Int __result, MethodBase __originalMethod)
        {
            try
            {
                // ★★ 已停用 —— 去掉那行压缩是错的 ★★
                //
                //   实测（玩家 1.4.3）：0°/45°/90° 三个朝向的射界全部变坏，
                //   包括原本正确的那个。日志显示它们对应 dir=2 / dir=5 / dir=1，
                //   而 dir=1 那个分支只读 xmax、我的输出与原版逐字相同 ——
                //   坏掉的正是被我改了 ymax 的 dir=2 和 dir=5。
                //
                //   反证也成立：护卫舰 1×2 压缩后 rect 是 1×1，八向艏炮源格恒为 (0,0)；
                //   去掉压缩会让一半朝向变成 (0,1)，而原版护卫舰是**正确**的。
                //   所以那行压缩是有意为之：炮组几何定义在 W×W 的方框上，不是船体全长。
                //
                // ★教训★
                //   离线harness 拿到的数据没错，错在我给它套了一个没验证的前提：
                //   「炮组源格必须落在船体矩形内」。坐标系约定和我想的不一样，
                //   而这个前提我完全没有验证过就当成了判据。
                //   下次再看到"某个值看起来越界"，先确认那个坐标系的原点和范围到底是什么。
                return true;

#pragma warning disable 162
                var s = Main.Settings;
                if (s == null || !s.ShipArcFix) return true;

                // ★护卫舰走原版★ Width=1 时原版是对的（玩家实测确认），
                //   而所有敌舰都是 1×2 —— 这一条同时保证不波及敌人。
                if (starshipRect.Width <= 1) return true;

                ClearOffsetCache();

                int x = starshipRect.xmin;
                int y = starshipRect.ymin;
                // 下面的分支与原版逐字一致，**唯一的区别是没有那行把 ymax 压成 ymin+Width-1**。
                switch (__originalMethod.Name)
                {
                    case "GetForeArcStartNodeOffset":
                        if (direction == 1 || direction == 4) x = starshipRect.xmax;
                        else if (direction == 3 || direction == 6) y = starshipRect.ymax;
                        else if (direction == 2 || direction == 5) { x = starshipRect.xmax; y = starshipRect.ymax; }
                        break;

                    case "GetPortArcStartNodeOffset":
                        if (direction == 0 || direction == 7) x = starshipRect.xmax;
                        else if (direction == 2 || direction == 5) y = starshipRect.ymax;
                        else if (direction == 1 || direction == 4) { x = starshipRect.xmax; y = starshipRect.ymax; }
                        break;

                    case "GetStarboardArcStartNodeOffset":
                        int d = direction % 4;
                        if (d == 1) x = starshipRect.xmax;
                        else if (d == 3) y = starshipRect.ymax;
                        else if (d == 2) { x = starshipRect.xmax; y = starshipRect.ymax; }
                        break;

                    default:
                        return true;   // 没见过的方法，原样放行
                }

                __result = new Vector2Int(x, y);

                if (s.WatchMomentum)
                {
                    string key = __originalMethod.Name + "|" + direction + "|"
                               + starshipRect.Width + "x" + starshipRect.Height;
                    if (_seen.Add(key))
                    {
                        Main.Log("[射界·源格] " + __originalMethod.Name + "　dir=" + direction
                               + "　船 " + starshipRect.Width + "×" + starshipRect.Height
                               + "　起始格=(" + x + "," + y + ")"
                               + "　（原版会把 ymax 压成 " + (starshipRect.ymin + starshipRect.Width - 1)
                               + "，真实 ymax=" + starshipRect.ymax + "）");
                        Main.FlushLog(true);
                    }
                }
                return false;   // 已给出结果，跳过原方法
            }
#pragma warning restore 162
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[射界·源格] 失败（射界保持原版）: " + e.Message); }
                return true;
            }
        }

        /// <summary>
        /// 清掉 WeaponSlot.Offsets 里已经算好的源格。
        ///
        /// StaticCache 没有公开的失效接口，内部 ConcurrentDictionary 活到进程结束。
        /// 不清的话，补丁挂上之前（或热重载之前）算过的键会一直返回旧值，
        /// 表现成"改了代码画面没变"—— 和"公式写错了"看起来一模一样，极难区分。
        /// 只在第一次真正生效时清一次。
        /// </summary>
        private static void ClearOffsetCache()
        {
            if (_cleared) return;
            _cleared = true;
            try
            {
                var t = AccessTools.TypeByName(SlotType);
                var f = t != null ? AccessTools.Field(t, "Offsets") : null;
                var cache = f != null ? f.GetValue(null) : null;
                if (cache == null) { Main.Log("[射界·源格] 找不到 WeaponSlot.Offsets，跳过清缓存"); return; }

                var inner = AccessTools.Field(cache.GetType(), "m_Cache");
                var dict = inner != null ? inner.GetValue(cache) : null;
                if (dict == null) { Main.Log("[射界·源格] 找不到 StaticCache.m_Cache，跳过清缓存"); return; }

                var clear = dict.GetType().GetMethod("Clear", Type.EmptyTypes);
                if (clear == null) { Main.Log("[射界·源格] m_Cache 没有 Clear()，跳过"); return; }
                clear.Invoke(dict, null);
                Main.Log("[射界·源格] 已清空炮组源格缓存（否则旧值会一直沿用到进程结束）");
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError("[射界·源格] 清缓存失败: " + e.Message); }
        }
    }
}
