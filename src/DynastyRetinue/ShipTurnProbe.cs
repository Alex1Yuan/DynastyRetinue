using System;
using System.Diagnostics;
using HarmonyLib;
using Kingmaker.Mechanics.Entities;

namespace DynastyRetinue
{
    /// <summary>
    /// 【诊断】抓「舰船转向转到一半突然跳过去」的元凶。
    ///
    /// ★为什么是这个点★
    ///   朝向只有两条改法：
    ///     UpdateSlowRotation(maxAngle)  —— 每帧挪一点，平滑
    ///     SetOrientation(value)         —— m_Orientation 和 DesiredOrientation 一起直接赋值，**一帧到位**
    ///   玩家看到的"最后一节瞬移挪过去"只可能是后者。所以拦住 SetOrientation，
    ///   在**跳变幅度超过阈值**时把调用栈打出来 —— 谁干的一目了然，不用再猜。
    ///
    /// ★为什么不打日志到每一帧★
    ///   SetOrientation 是高频调用（每次朝向确认都会走），全打会瞬间灌满日志。
    ///   只有"跳变 > 阈值"才记，正常的小幅修正一条都不打。
    ///   取调用栈本身有开销，所以放在阈值判断**之后**。
    ///
    /// ★默认关闭★ 只在面板勾了「详细日志」时才工作。
    /// </summary>
    [Main.DevOnly, Main.DiagOnly]
    [HarmonyPatch(typeof(AbstractUnitEntity), "SetOrientation")]
    internal static class ShipTurnProbe
    {
        /// <summary>
        /// 多大算"跳"。
        ///
        /// ★这个值曾经是 15，把要查的东西整个滤没了★
        ///   2026-08-22 实测：真实转向是每步 6~12°，末尾那次可疑的对齐是 12.6° ——
        ///   **全都低于 15**。于是一整场海战只记下 4 条 `0.0°→135.0°` 的入场摆位，
        ///   看上去像"没抓到数据"，实际是阈值把信号当噪声扔了。
        ///   更糟的是它制造了一个假象：日志里只剩大跳，显得"只有入场才跳"。
        ///
        /// 降到 3° 的理由：要回答的问题是"145.0 到 122.4 那 22.6° 是怎么过去的" ——
        /// 若是若干小步就是平滑，若是一步就是瞬移。**只有把每一步都记下来才分得清**。
        /// 小于 3° 的是朝向确认噪声，不记。
        /// </summary>
        private const float JumpDegrees = 3f;

        /// <summary>
        /// 一次会话最多记这么多条。阈值降到 3° 后单场海战可能上千条，
        /// 会把日志尾部（诊断包只带最后 400 KB）挤掉别的信息。
        /// </summary>
        private const int MaxLogs = 250;

        private static int _logged;

        /// <summary>换一场海战 / 重开时清零，否则上限一满后面全测不了。</summary>
        public static void Reset() { _logged = 0; _last.Clear(); }

        /// <summary>
        /// 同一实体两次记录之间的最小间隔（秒）。
        /// ★这个值曾经是 0.5，是个测量缺陷★ 游戏模拟步长 50ms（20 tick/s），
        /// 0.5 秒等于**每 10 步只记 1 步** —— 于是"整个转向都在一跳一跳"会被采样成
        /// "偶尔跳一下"，两种完全不同的现象在日志里长得一样。
        /// 降到 0 = 每一步都记，才分得清是**全程分段**还是**只有末尾一次**。
        ///
        /// 那次测量的结论：正常转向是连续的 6~10°/步（平滑），**只有路径节点方向突变时
        /// 才会插进一个 36~54° 的大步** —— 所以病灶是突变那几步，不是全程。
        /// 结论拿到后阈值调回 15°，只盯真正的大跳，日志不再刷屏。
        /// </summary>
        private const float Cooldown = 0f;

        private static readonly System.Collections.Generic.Dictionary<string, float> _last =
            new System.Collections.Generic.Dictionary<string, float>(StringComparer.Ordinal);

        private static void Prefix(AbstractUnitEntity __instance, float value)
        {
            try
            {
                if (!Main.Enabled || !Main.DevMode) return;
                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;   // 复用「详细日志」开关
                if (__instance == null) return;

                // 只看舰船 —— 地面单位的朝向跳变不是我们要查的东西
                string tn = __instance.GetType().Name;
                if (tn.IndexOf("Starship", StringComparison.OrdinalIgnoreCase) < 0) return;

                float from = __instance.Orientation;
                float delta = Mathf.DeltaAngleAbs(from, value);
                if (delta < JumpDegrees) return;
                if (_logged >= MaxLogs) return;

                string key = __instance.UniqueId ?? tn;
                float now = UnityEngine.Time.realtimeSinceStartup;
                float prev;
                if (Cooldown > 0f && _last.TryGetValue(key, out prev) && (now - prev) < Cooldown) return;
                _last[key] = now;
                _logged++;

                // ★调用栈这条路已经作废★
                //   原来这里用 new StackTrace(2, false) 想抓"谁调的"，实测**一行都没打出来** ——
                //   这个 Mono 运行时下取到的帧要么数量为 0，要么 GetMethod() 全是 null。
                //   2026-08-22 的日志里 101 条 `调用栈:` 后面全是空的，包括 135° 的大跳。
                //   所以改成记**状态量**：目标朝向是多少、有没有在移动、终点是不是 45° 的整数倍。
                //   这些能直接回答"是不是末尾对齐到八方向"，比调用栈更贴近问题本身。

                // 目标朝向 —— 赋值**前**读到的是上一次的目标。
                // "目标 122.4 却被写成 135.0"和"目标本来就是 135.0"是两种完全不同的病。
                string want = "";
                try
                {
                    var f = AccessTools.PropertyGetter(typeof(AbstractUnitEntity), "DesiredOrientation");
                    if (f != null)
                    {
                        object d = f.Invoke(__instance, null);
                        if (d != null) want = "　目标=" + Convert.ToSingle(d).ToString("F1") + "°";
                    }
                }
                catch { }

                // 终点是不是 45° 的整数倍 —— 格子只有八个方向。
                // 若"跳"总是落在整数倍上，那就是末尾对齐，不是插值缺失。
                float k = value / 45f;
                bool snap45 = Math.Abs(k - (float)Math.Round(k)) < 0.01f;

                string spd = "";
                string moving = "";
                try
                {
                    var view = __instance.View;
                    var agent = view != null ? view.AgentASP : null;
                    if (agent != null)
                    {
                        float ang = agent.m_AngularSpeed;
                        object cang = AccessTools.Field(typeof(Kingmaker.View.UnitMovementAgentBase),
                                                        "m_CombatAngularSpeed")?.GetValue(agent);
                        spd = "　角速度 普通=" + ang.ToString("F0")
                            + " 战斗=" + (cang != null ? Convert.ToSingle(cang).ToString("F0") : "?")
                            + "　MaxSpeed=" + agent.MaxSpeed.ToString("F1");

                        // 在移动中转向 = 正常行进；停下后还在转 = 末尾补齐，正是要抓的那一刻
                        var pm = AccessTools.PropertyGetter(agent.GetType(), "IsReallyMoving");
                        if (pm != null)
                        {
                            object mv = pm.Invoke(agent, null);
                            if (mv != null) moving = Convert.ToBoolean(mv) ? "　移动中" : "　★已停★";
                        }
                    }
                }
                catch { }

                Main.Log("[转向] " + tn + "　" + from.ToString("F1") + "° -> " + value.ToString("F1")
                         + "°（跳 " + delta.ToString("F1") + "°）"
                         + (snap45 ? "　★落在45°整数倍★" : "") + want + moving + spd
                         + (_logged == MaxLogs ? "　※已达 " + MaxLogs + " 条上限，后续不再记※" : ""));
            }
            catch { }
        }

        /// <summary>UnityEngine.Mathf.DeltaAngle 的绝对值版本，省得每处都写 Abs。</summary>
        private static class Mathf
        {
            public static float DeltaAngleAbs(float a, float b)
            {
                return Math.Abs(UnityEngine.Mathf.DeltaAngle(a, b));
            }
        }
    }
}
