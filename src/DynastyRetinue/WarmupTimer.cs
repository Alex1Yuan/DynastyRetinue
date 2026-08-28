using System;
using System.Diagnostics;

namespace DynastyRetinue
{
    /// <summary>
    /// 量「我们的补丁在头几次调用里花了多少毫秒」。
    ///
    /// ★为什么需要这个而不是 A/B 对照★
    ///   玩家实测：海战里**第一次点到某个炮**会卡一下，之后就不卡了。
    ///   这是一次性预热代价，而预热代价**做不了同场对照**——等你关掉 mod 再点一次，
    ///   那已经不是第一次了，那一臂无论如何都不会卡，于是"不卡"什么都证明不了。
    ///   （我原本设计的正是这个无效对照，玩家指出来的。）
    ///
    ///   要分对照就得每一臂都重启游戏，麻烦且容易引入别的差异。
    ///   直接量自己便宜得多：如果我们头几次调用总共 3 ms，而那一帧长 200 ms，
    ///   那 197 ms 是原版的，不用再争；反过来如果我们占了 150 ms，那就是我们的活。
    ///
    /// ★只量前 N 次★ 预热之后每次调用都是热路径，量它没有意义，
    ///   而且计时本身（Stopwatch + 字典）在热路径上也是成本。
    /// </summary>
    internal static class WarmupTimer
    {
        private const int Samples = 20;

        private sealed class Slot
        {
            public int N;
            public double TotalMs;
            public double WorstMs;
            public bool Reported;
        }

        private static readonly System.Collections.Generic.Dictionary<string, Slot> _slots
            = new System.Collections.Generic.Dictionary<string, Slot>(StringComparer.Ordinal);

        /// <summary>
        /// 计时一次调用。返回 Stopwatch 供 Stop 用；超过采样数后返回 null（零开销）。
        /// 用法：var sw = WarmupTimer.Begin("射界·源格"); try { ... } finally { WarmupTimer.End("射界·源格", sw); }
        /// </summary>
        public static Stopwatch Begin(string key)
        {
            Slot s;
            if (_slots.TryGetValue(key, out s) && s.N >= Samples) return null;   // 预热完了，别再量
            return Stopwatch.StartNew();
        }

        public static void End(string key, Stopwatch sw)
        {
            if (sw == null) return;
            try
            {
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;

                Slot s;
                if (!_slots.TryGetValue(key, out s)) { s = new Slot(); _slots[key] = s; }
                s.N++;
                s.TotalMs += ms;
                if (ms > s.WorstMs) s.WorstMs = ms;

                // ★第一次单独报★ 预热代价几乎全在第一次里，混进平均值会被稀释看不见
                if (s.N == 1)
                    Main.Log("[预热] " + key + " 首次调用 " + ms.ToString("F1") + " ms");

                if (s.N == Samples && !s.Reported)
                {
                    s.Reported = true;
                    Main.Log("[预热] " + key + " 前 " + Samples + " 次合计 "
                           + s.TotalMs.ToString("F1") + " ms，最长单次 " + s.WorstMs.ToString("F1") + " ms"
                           + "\n    ★怎么读★ 拿它和同一时刻 [帧时间] 那一窗的「最长」比："
                           + "我们只占其中一小部分 ⇒ 卡顿是原版的；占了大半 ⇒ 是我们的。");
                    Main.FlushLog(true);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 量「点一下舰炮」这个操作的**整条链**耗时（原版 + 我们的补丁一起）。
    ///
    /// ★为什么量最外层而不是拆开量我们自己★
    ///   玩家实测：第一次点到某个炮会卡一下，之后不卡；而且**不是开炮时卡，是点击时卡**。
    ///   点击触发的就是这个方法（射界的计算与显示）。
    ///
    ///   先量总数是更省的一步：如果整条链只有几毫秒，那几百毫秒的卡顿根本不在这里
    ///  （多半是特效 prefab 加载 / 着色器编译），我们和原版都不用再查；
    ///   只有当它确实很大时，才值得付出改造补丁内部的代价去拆分归属。
    ///
    /// ★为什么不做 A/B★ 预热代价**做不了同场对照** —— 关掉 mod 再点一次时
    ///   已经不是第一次了，那一臂无论如何都不会卡，于是"不卡"什么都证明不了。
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(Kingmaker.UI.SurfaceCombatHUD.CombatHUDRenderer),
                             "PopulateAbilityPatternAreas")]
    internal static class ArcClickTimer
    {
        private const string Key = "点炮→射界显示（整条链）";
        [System.ThreadStatic] private static Stopwatch _sw;

        private static void Prefix()
        {
            // ★这里刻意不查 Main.Enabled★ 关掉 mod 时更需要这个数 ——
            //   那正是「原版自己要多久」的基线，是我们唯一拿得到的对照。
            _sw = WarmupTimer.Begin(Key);
        }

        private static void Postfix()
        {
            WarmupTimer.End(Key, _sw);
            _sw = null;
        }
    }
}
