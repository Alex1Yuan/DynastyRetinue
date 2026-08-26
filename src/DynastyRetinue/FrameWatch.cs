using System;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 帧时间监视 —— 把「好像有点卡」变成数字。
    ///
    /// ── 为什么需要 ──────────────────────────────────────────────────────────
    /// 玩家反馈「装了 mod 在地图上走路每隔几步顿一下，卸载就没有」。
    /// 我先猜是 RetinueRegistry.All() 每秒拷全部实体 —— **实测证伪**：
    /// 那一趟只过 44 个实体，代价约等于零。
    ///
    /// 然后做 A/B（遣散全部卫兵、mod 仍装着），结果是「还有点点，说不清」——
    /// 到这个量级，主观感受已经不能用作判据了。所以改成量。
    ///
    /// ── 判据 ────────────────────────────────────────────────────────────────
    /// 60 fps 下一帧 16.7 ms。超过 SpikeMs 记一次尖峰。
    /// 按 10 秒一窗汇总：这一窗有多少帧、多少次尖峰、最长一帧多少、中位帧多少。
    /// 同时记下当时有几名卫兵、在不在战斗 —— 这三组数放在一起才能比较：
    ///     5 名卫兵 vs 0 名卫兵 vs mod 关掉
    /// 三种状态的尖峰次数如果差不多，说明**不是本 mod 造成的**。
    ///
    /// ── 噪音控制 ────────────────────────────────────────────────────────────
    /// · 每帧只做一次浮点比较和一次自增，可以忽略。
    /// · 只在**有尖峰**的窗口记一行；健康状态下一行都不会有。
    /// · ★但第一窗无条件记★ —— 否则「一行都没有」既可能是「没卡」，
    ///   也可能是「监视根本没跑」。这两者在日志里长得一样，而这个坑本项目栽过三次。
    /// · 加载/过图那种必然的长帧会被记进去，看的时候按时间戳排除即可。
    /// </summary>
    internal static class FrameWatch
    {
        /// <summary>超过这个毫秒数算一次尖峰。60 fps 一帧是 16.7 ms，100 ms 是连掉 6 帧。</summary>
        private const float SpikeMs = 100f;
        private const float WindowSec = 10f;

        private static float _winStart = -1f;
        private static int _frames, _spikes;
        private static float _worst;
        private static bool _firstDone;

        // 中位数用粗桶估：精确排序要留全部样本，没必要。
        private static readonly int[] _buckets = new int[6];   // <20 <33 <50 <100 <200 >=200 ms

        public static void Tick()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (_winStart < 0f) { _winStart = now; return; }

                float ms = Time.deltaTime * 1000f;
                _frames++;
                if (ms > _worst) _worst = ms;
                if (ms >= SpikeMs) _spikes++;
                _buckets[ms < 20f ? 0 : ms < 33f ? 1 : ms < 50f ? 2 : ms < 100f ? 3 : ms < 200f ? 4 : 5]++;

                if (now - _winStart < WindowSec) return;

                bool report = _spikes > 0 || !_firstDone;
                if (report) Report(now - _winStart);

                _firstDone = true;
                _winStart = now; _frames = 0; _spikes = 0; _worst = 0f;
                Array.Clear(_buckets, 0, _buckets.Length);
            }
            catch { }
        }

        private static void Report(float span)
        {
            try
            {
                int guards = 0;
                try { guards = RetinueRegistry.Count; } catch { }
                bool combat = false;
                try { combat = Kingmaker.Game.Instance != null && Kingmaker.Game.Instance.Player != null
                             && Kingmaker.Game.Instance.Player.IsInCombat; } catch { }

                Main.Log(string.Format(
                    "[帧时间] {0:F0} 秒内 {1} 帧　尖峰(>{2:F0}ms) {3} 次　最长 {4:F0} ms　"
                    + "分布 <20:{5} <33:{6} <50:{7} <100:{8} <200:{9} 200+:{10}　"
                    + "卫兵 {11} 名　{12}",
                    span, _frames, SpikeMs, _spikes, _worst,
                    _buckets[0], _buckets[1], _buckets[2], _buckets[3], _buckets[4], _buckets[5],
                    guards, combat ? "战斗中" : "非战斗")
                    + (_firstDone ? "" : "\n    ★这是第一窗，无条件记录，只为证明监视在跑★"
                                       + "\n    往后只有出现尖峰的窗口才会记。要判断是不是本 mod 造成的，"
                                       + "\n    请对比三种状态的尖峰次数：5 名卫兵 / 0 名卫兵 / mod 关掉。"));
                Main.FlushLog(true);
            }
            catch { }
        }
    }
}
