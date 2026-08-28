using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Pathfinding;
using Pathfinding;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 舰炮源格：改从**船体占位**切外皮，取代原版那套「W×W 方框角点」。
    ///
    /// ================== 病根（一行）==================
    ///     原版位移 = (ceil((W−1)/2), L/2 − 1)
    ///              = GridAreaHelper 的**居中锚点** 与 WeaponSlot 假设的**角点锚点**之差
    /// W=1 时为 (0,0) —— 这就是护卫舰纯属巧合地正确、而所有大船都错的原因。
    ///
    /// 离线实测（_tmp/skin/，可重跑）：
    ///   2×4 dir=0 左舷炮 4/4 源格**整列脱离船体**；
    ///   3×6 每个正交朝向都有一侧 6/6 脱离；
    ///   斜向原版只产出 5 / 7 格，而船舷外皮是 7 / 11 格（= 2L−1）——
    ///   **比外皮还少**，所以无论怎么调 offsetFromProw / batteryWidth 都覆盖不满。
    ///   这是计数论证，不是位置论证：调参数无解，必须自建源格。
    ///
    /// ================== 定义 ==================
    ///   V    = 该弧的朝向（Fore=航向；Port=LeftN²；Starboard=RightN²）
    ///   skin = { c ∈ 船体占位 : c + V ∉ 船体占位 }          ← 沿 V 方向的最外一层
    ///   补 4-连通：斜向阶梯会出现只在对角相接的两格，补一个「离 V 更远」的角格
    ///   艏炮：整块 skin 按 offset 沿反航向后撤（照抄原版的后撤循环）
    ///   舷炮：skin 按「艏→艉」排序成链，再按 (offset, batteryWidth) 开窗
    ///
    /// ★唯一故意偏离原版之处★
    ///   开窗上界夹在 **链长** 而不是船长 L 上。正交时链长恰好 = L，无差别；
    ///   斜向链长是 2L−1，夹在 L 上会把 3×6 的 11 格外皮砍成 7 格。
    ///   护卫舰 L=2 时 2L−1 = 3 = L+1，两种夹法结果相同 ——
    ///   这正是原版这个斜向 bug 在护卫舰身上看不出来的原因。
    ///
    /// ================== 验证 ==================
    /// 护卫舰门禁 **376/376 逐格等于原版**（含行走顺序，不只是集合相同）：
    ///     8 向 × 3 弧 = 24；舷炮 offset 0–3 × width 0–4 × 8 向 × 左右舷 = 320；
    ///     艏炮 offset 0–3 × 8 向 = 32。
    /// 这是硬门禁 —— 敌舰都是 1×2，改坏它等于毁掉整个游戏。
    ///
    /// ★左右舷不能靠门禁定★
    ///   护卫舰左右对称，有两个定义都能拿满分，其中一个会把大船左右舷**整体调换**。
    ///   定舷的证据来自门禁之外：FiringArcHelper.GetValidDirections 里
    ///   Port 的射界锥八向全部以 LeftN² 为中心、Starboard 全部以 RightN² 为中心，零 mismatch。
    ///
    /// ★两条链共用★
    ///   挂在 GetFiringArcSourceNodesOffsets —— 显示链（GetRestrictedFiringArcNodes）
    ///   和判定链（IsTargetInsideRestrictedFiringArc）**唯一的共同咽喉**。
    ///   改这一处，两边同时对，不会出现「显示改了判定没改」。
    ///
    /// ★不碰原版缓存★
    ///   原版 Offsets 是 StaticCache&lt;SourceNodesKey, Vector2Int[]&gt;，返回的是**数组本体**。
    ///   Postfix 原地改它会永久污染该 key。这里用 Prefix 直接给结果并跳过原方法，
    ///   自带一份独立缓存，原版缓存一个字节都不动。
    ///
    /// ★RectCache 必须前后都清★
    ///   GridAreaHelper.GetOffsets 内部 CalcOffsets 只在 finally 里清，也就是**算完才清**。
    ///   所以这里一律走公开的 GetOffsets（它自己会清），绝不直接调那两个私有方法。
    /// </summary>
    internal static class ShipArcSources
    {
        // WeaponSlot.GetNeighbourAlongDirection
        private static readonly Vector2Int[] STEP =
        {
            new Vector2Int(0,-1), new Vector2Int(1,0), new Vector2Int(0,1), new Vector2Int(-1,0),
            new Vector2Int(1,-1), new Vector2Int(1,1), new Vector2Int(-1,1), new Vector2Int(-1,-1),
        };
        // CustomGraphHelper.LeftNeighbourDirection / RightNeighbourDirection（反射核对过）
        private static readonly int[] LeftN  = { 4, 5, 6, 7, 1, 2, 3, 0 };
        private static readonly int[] RightN = { 7, 4, 5, 6, 0, 1, 2, 3 };

        private static readonly Dictionary<long, Vector2Int[]> _cache = new Dictionary<long, Vector2Int[]>();

        private static long Key(int arc, int w, int l, int d, int off, int bw)
        {
            return ((long)arc << 40) | ((long)w << 32) | ((long)l << 24)
                 | ((long)d << 16) | ((long)(off & 0xFF) << 8) | (long)(bw & 0xFF);
        }

        /// <summary>arc: 0=Fore 1=Port 2=Starboard。拿不到船体占位就返回 null（调用方放行原版）。</summary>
        internal static Vector2Int[] Build(IntRect rect, int d, int arc, int offset, int batteryWidth)
        {
            if (d < 0 || d > 7) return null;
            int w = rect.Width, l = rect.Height;
            if (w <= 0 || l <= 0) return null;

            long k = Key(arc, w, l, d, offset, batteryWidth);
            Vector2Int[] hit;
            if (_cache.TryGetValue(k, out hit)) return hit;

            var hull = new HashSet<Vector2Int>();
            foreach (var c in GridAreaHelper.GetOffsets(rect, d)) hull.Add(c);
            if (hull.Count == 0) return null;

            Vector2Int v = arc == 0 ? STEP[d]
                         : arc == 1 ? STEP[LeftN[LeftN[d]]]
                                    : STEP[RightN[RightN[d]]];

            var skin = new HashSet<Vector2Int>();
            foreach (var c in hull) if (!hull.Contains(c + v)) skin.Add(c);
            if (skin.Count == 0) return null;

            Connect4(skin, v);

            Vector2Int[] result;
            if (arc == 0) result = Fore(skin, d, offset);
            else          result = Broadside(skin, d, l, offset, batteryWidth);

            _cache[k] = result;
            return result;
        }

        /// <summary>
        /// 补 4-连通：斜向阶梯会出现只在对角相接的两格，补一个角格。
        /// 取「离 V 更远」的那个（argmin dot，不是 max）—— 与原版口味一致。
        /// ★W≥2 时这个循环一次都不会触发★（2×4/2×5/3×6/3×7/4×8 实测全为 0 次），
        /// 它只影响 1 宽船的斜向档，而那一档必须与原版逐格相同。
        /// </summary>
        private static void Connect4(HashSet<Vector2Int> skin, Vector2Int v)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                Vector2Int add = default(Vector2Int);
                bool found = false;
                var arr = new List<Vector2Int>(skin);
                for (int i = 0; i < arr.Count && !found; i++)
                    for (int j = i + 1; j < arr.Count && !found; j++)
                    {
                        var a = arr[i]; var b = arr[j];
                        if (Math.Abs(a.x - b.x) != 1 || Math.Abs(a.y - b.y) != 1) continue;
                        var c1 = new Vector2Int(a.x, b.y);
                        var c2 = new Vector2Int(b.x, a.y);
                        if (skin.Contains(c1) || skin.Contains(c2)) continue;
                        add = (c1.x * v.x + c1.y * v.y) < (c2.x * v.x + c2.y * v.y) ? c1 : c2;
                        found = true;
                    }
                if (!found) return;
                skin.Add(add);
            }
        }

        /// <summary>艏炮：整块外皮按 offset 沿反航向后撤。后撤循环逐字照抄原版。</summary>
        private static Vector2Int[] Fore(HashSet<Vector2Int> skin, int d, int offset)
        {
            var list = new List<Vector2Int>(skin);
            if (offset <= 0) return list.ToArray();

            int aft = (d + 2) % 4 + (d / 4) * 4;      // 原版写法，等于 d 的反向
            var acc = new Vector2Int(0, 0);
            for (int i = 0; i < offset; i++)
            {
                acc += STEP[aft];
                if (WLen(acc) >= offset) break;
            }
            for (int i = 0; i < list.Count; i++) list[i] = list[i] + acc;
            return list.ToArray();
        }

        /// <summary>舷炮：外皮按「艏→艉」排成链，再按 (offset, batteryWidth) 开窗。</summary>
        private static Vector2Int[] Broadside(HashSet<Vector2Int> skin, int d, int l,
                                              int offset, int batteryWidth)
        {
            if (offset >= l) return new Vector2Int[0];       // 原版的提前返回

            var chain = new List<Vector2Int>(skin);
            var f = STEP[d];
            chain.Sort((a, b) => (b.x * f.x + b.y * f.y).CompareTo(a.x * f.x + a.y * f.y));

            int take = batteryWidth == 0 ? int.MaxValue
                     : (d < 4 ? batteryWidth : batteryWidth + 1);
            int first = offset < 0 ? 0 : offset;
            if (first >= chain.Count) return new Vector2Int[0];

            // ★这里夹在链长上，不是船长 L 上★ 见类头注：
            //   斜向链长是 2L−1，夹在 L 上会把 3×6 的 11 格砍成 7 格。
            long lastL = (long)first + take - 1;
            int last = lastL >= chain.Count - 1 ? chain.Count - 1 : (int)lastL;

            var outp = new Vector2Int[last - first + 1];
            for (int i = first; i <= last; i++) outp[i - first] = chain[i];
            return outp;
        }

        /// <summary>CustomGraphHelper.GetWarhammerLength：hi + lo/2（整数除）。</summary>
        private static int WLen(Vector2Int v)
        {
            int a = Math.Abs(v.x), b = Math.Abs(v.y);
            int hi = a > b ? a : b, lo = a > b ? b : a;
            return hi + lo / 2;
        }
    }

    /// <summary>
    /// 接管源格生成 —— 显示链与判定链**唯一的共同咽喉**。
    ///
    ///   显示 GetRestrictedFiringArcNodes → GetFiringArcNodes → GetFiringArcSourceNodes → 本方法
    ///   判定 IsTargetInsideRestrictedFiringArc → GetAdjustedSourceNodes            → 本方法
    ///
    /// 改这一处两边同时对。★不动原版 Offsets 缓存★：Prefix 直接给结果并跳过原方法，
    /// 自带独立缓存；Postfix 原地改那个数组会永久污染 StaticCache 的对应 key。
    ///
    /// ★门禁★ Width ≤ 1 一律放行原版（护卫舰和目前已知的全部敌舰）。
    ///   离线已证 W=1 时新旧逐格相同，走原版只是省一次计算、并且绝对安全。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipArcSourcePatch
    {
        private const string SlotType = "Warhammer.SpaceCombat.StarshipLogic.Weapon.WeaponSlot";

        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName(SlotType);
            return t == null ? null : AccessTools.Method(t, "GetFiringArcSourceNodesOffsets");
        }

        private static bool Prepare()
        {
            var m = TargetMethod();
            Main.Log("[射界·源格] 挂载 " + (m != null
                ? "成功 → WeaponSlot.GetFiringArcSourceNodesOffsets()（显示链与判定链共用）"
                : "失败：找不到 GetFiringArcSourceNodesOffsets —— 射界保持原版"));
            return m != null;
        }

        private static bool _warned;
        private static readonly HashSet<int> _logged = new HashSet<int>();

        private static bool Prefix(int direction, IntRect sizeRect, object firingArc,
                                   int offsetFromProw, int batteryWidth, ref Vector2Int[] __result)
        {
                if (!Main.Enabled) return true;   // 关掉 mod 就交还原版（OnToggle 不撤 Harmony 补丁）
            try
            {
                var s = Main.Settings;
                if (s == null || !s.ShipArcFix) return true;
                if (sizeRect.Width <= 1) return true;        // ★护卫舰 / 敌舰：原版一行不碰★

                // RestrictedFiringArc: Fore / Port / Starboard 是这条链上唯一会出现的三种
                int arc;
                switch (firingArc.ToString())
                {
                    case "Fore":      arc = 0; break;
                    case "Port":      arc = 1; break;
                    case "Starboard": arc = 2; break;
                    default:          return true;           // 没见过的弧，原样放行
                }

                var built = ShipArcSources.Build(sizeRect, direction, arc, offsetFromProw, batteryWidth);
                if (built == null || built.Length == 0) return true;   // 拿不到就放行原版

                __result = built;

                if (s.WatchMomentum)
                {
                    int k = (arc << 20) | (direction << 12) | (sizeRect.Width << 6) | sizeRect.Height;
                    if (_logged.Add(k))
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var c in built) sb.Append("(").Append(c.x).Append(",").Append(c.y).Append(") ");
                        Main.Log("[射界·源格] " + firingArc + "　朝向=" + direction
                               + "　船 " + sizeRect.Width + "×" + sizeRect.Height
                               + "　off=" + offsetFromProw + " bw=" + batteryWidth
                               + "　源格(" + built.Length + ")：" + sb.ToString().TrimEnd());
                        Main.FlushLog(true);
                    }
                }
                return false;    // 已给出结果，跳过原方法
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[射界·源格] 失败（射界保持原版）: " + e.Message); }
                return true;
            }
        }
    }
}
