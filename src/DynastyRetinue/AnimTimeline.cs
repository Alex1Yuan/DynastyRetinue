using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Visual.Animation;
using Kingmaker.Visual.Animation.Actions;
using Kingmaker.Visual.Animation.Kingmaker;

namespace DynastyRetinue
{
    /// <summary>
    /// ================= 动画时间轴（1.7.43）=================
    ///
    /// ★为什么要做这个★
    ///   作者原话：「我建议你把这些动画的 trigger 结束时间和技能本身的释放、开始、结束时间
    ///   等等用 log 记录，会比我眼睛看精确」。他说得对 —— 前面十几个版本我一直让他
    ///   用肉眼描述「摆大字」「像放不出来」「位移没了」，而这些描述分不开三种完全不同的情况：
    ///     ① 动画根本没开始      —— 句柄创建了但 StartInternal 没跑
    ///     ② 动画开始了但瞬间结束 —— 播了几毫秒，肉眼等同于没播
    ///     ③ 动画正常播完，但技能早就结束了 —— 时序错位，看着像"卡了一下"
    ///   只有把**两条时间轴放在一起**才能分开：技能的 开始→结束，动画的 创建→启动→结束。
    ///
    /// ★记什么★ 每个我们卫兵身上的动画句柄一条：
    ///     技能名 / 动作类 / 创建到启动的延迟 / 播放时长 / 技能此刻是否还在跑
    ///
    /// ★去重与限量★
    ///   按「技能|动作类」去重，每种组合只记**前三次**（同一个技能多次施放时，
    ///   偶发的异常和稳定的异常能区分开，但不会刷屏）。
    ///   全部挂在 DiagVerbose 下，默认关，正常玩家一行都看不到。
    ///
    /// ★开销★ 只在句柄真正 Start/Finish 时各一次，不是每帧；
    ///   而且第一道闸是 isinst + 字典查，非我们的单位立刻走掉。
    /// </summary>
    internal static class AnimTimeline
    {
        /// <summary>每种「技能|动作类」最多记几条。</summary>
        private const int MaxPerKind = 3;

        private sealed class Rec
        {
            internal float Created, Started;
            internal string Ability, Action;
        }

        private static readonly Dictionary<AnimationActionHandle, Rec> _live =
            new Dictionary<AnimationActionHandle, Rec>();
        private static readonly Dictionary<string, int> _count =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static bool On()
        {
            var s = Main.Settings;
            return Main.Enabled && s != null && s.DiagVerbose;
        }

        /// <summary>句柄被创建/交给 Execute 的那一刻。由 SequencedStarvationFixPatch 的 Prefix 顺带调用。</summary>
        internal static void NoteCreated(AnimationActionHandle h)
        {
            try
            {
                if (!On() || h == null) return;
                var uh = h as UnitAnimationActionHandle;
                if (uh == null || !AnimFallback.Applies(uh)) return;

                // 换图/异常会留下孤儿，攒够了整桶倒掉
                if (_live.Count > 64) _live.Clear();

                var u = AnimFallback.UnitOf(uh);
                string ab = "-";
                try
                {
                    var use = AnimFallback.CurrentUse(u);
                    if (use != null && use.Ability != null && use.Ability.Blueprint != null)
                        ab = use.Ability.Blueprint.name;
                }
                catch { }

                _live[h] = new Rec
                {
                    Created = UnityEngine.Time.realtimeSinceStartup,
                    Started = -1f,
                    Ability = ab,
                    Action  = uh.Action == null ? "null" : uh.Action.GetType().Name,
                };
            }
            catch { }
        }

        internal static void NoteStarted(AnimationActionHandle h)
        {
            try
            {
                // ★每帧路径准入★ 这两个钩子挂在**全场所有单位**的句柄上。
                //   字典为空（没有我们卫兵的句柄在跑）时，一次 int 比较就走，不做字典查。
                if (_live.Count == 0 || h == null) return;
                Rec r;
                if (!_live.TryGetValue(h, out r)) return;
                r.Started = UnityEngine.Time.realtimeSinceStartup;
            }
            catch { }
        }

        internal static void NoteFinished(AnimationActionHandle h)
        {
            try
            {
                // ★每帧路径准入★ 这两个钩子挂在**全场所有单位**的句柄上。
                //   字典为空（没有我们卫兵的句柄在跑）时，一次 int 比较就走，不做字典查。
                if (_live.Count == 0 || h == null) return;
                Rec r;
                if (!_live.TryGetValue(h, out r)) return;
                _live.Remove(h);

                string key = r.Ability + "|" + r.Action;
                int n;
                _count.TryGetValue(key, out n);
                if (n >= MaxPerKind) return;
                _count[key] = n + 1;

                float now = UnityEngine.Time.realtimeSinceStartup;
                float toStart = r.Started < 0f ? -1f : (r.Started - r.Created) * 1000f;
                float played  = r.Started < 0f ? 0f  : (now - r.Started) * 1000f;

                // 技能是不是还在跑 —— 用来判断时序错位
                string abLive = "?";
                try
                {
                    var uh = h as UnitAnimationActionHandle;
                    var u = uh != null ? AnimFallback.UnitOf(uh) : null;
                    var use = AnimFallback.CurrentUse(u);
                    abLive = use == null ? "已结束"
                           : (use.Ability != null && use.Ability.Blueprint != null
                              && use.Ability.Blueprint.name == r.Ability ? "仍在跑" : "换成别的了");
                }
                catch { }

                Main.Log("[动画时间轴] 技能=" + r.Ability + "　动作=" + r.Action
                       + "　创建→启动 " + (toStart < 0f ? "★从未启动★" : toStart.ToString("F0") + " ms")
                       + "　播放时长 " + played.ToString("F0") + " ms"
                       + "　结束时技能=" + abLive
                       + "　★怎么读★ 从未启动 ⇒ 句柄进了队列没轮上（排队饿死）；"
                       + "播放时长 <50ms ⇒ 播了等于没播，肉眼就是大字；"
                       + "「已结束」而动画才收尾 ⇒ 时序错位。");
            }
            catch { }
        }
    }

    /// <summary>句柄真正开始播的那一刻。</summary>
    [Main.DiagOnly]
    [HarmonyPatch(typeof(AnimationActionHandle), "StartInternal")]
    internal static class AnimTimelineStartPatch
    {
        private static void Postfix(AnimationActionHandle __instance) { AnimTimeline.NoteStarted(__instance); }
    }

    /// <summary>句柄收尾的那一刻 —— 这里才知道它到底播了多久。</summary>
    [Main.DiagOnly]
    [HarmonyPatch(typeof(AnimationActionHandle), "FinishInternal")]
    internal static class AnimTimelineFinishPatch
    {
        private static void Prefix(AnimationActionHandle __instance) { AnimTimeline.NoteFinished(__instance); }
    }
}
