using System;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Networking.Desync;

namespace DynastyRetinue
{
    /// <summary>
    /// 把游戏的"检测到不同步"记进我们自己的日志。
    ///
    /// ★为什么需要★
    ///   今天排查了一整天不同步，但**两边都没有留下证据**：
    ///     · 我们的日志不记 —— 那是游戏的检测，我们收不到
    ///     · 游戏的落盘 dump 在零售版没启用 —— DefaultDesyncDetectionStrategy
    ///       只挂了 UIDesyncHandler（弹个对话框），SaveToFolderDesyncHandler
    ///       和 SendToRemoteDesyncHandler 都不在列表里。实测玩家机器上
    ///       `%LOCALAPPDATA%\Temp\Owlcat Games\…\Net\Desync` 目录根本不存在。
    ///   于是每次只能靠"我记得点了招募之后弹的"这种口述来定位，
    ///   而口述分不清"是招募导致的"还是"招募之后碰巧发生的"。
    ///
    /// ★这一行能解决什么★
    ///   DesyncMeta 带 tick。我们发的每条指令也会打日志，两个 tick 一对，
    ///   就能回答"不同步发生在我们动手之后几 tick"——
    ///   紧挨着 = 大概率是我们；隔了几百 tick = 多半跟我们无关。
    ///   这是把"猜"换成"看"的最小代价手段。
    ///
    /// ★为什么打在 UIDesyncHandler 而不是策略类★
    ///   RaiseDesync 是不同步**唯一**的对外出口（CompositeDesyncHandler 也只是
    ///   转发给列表里的每个 handler）。而 DefaultDesyncDetectionStrategy 里
    ///   m_DesyncHandler 是 private readonly，拿不到实例。
    ///   打在这个具体实现上最省事，而且它就是零售版唯一挂着的那个。
    ///
    /// ★只读，不改行为★ Postfix，不拦截、不改参数 —— 原版该弹的对话框照弹。
    /// </summary>
    [HarmonyPatch(typeof(UIDesyncHandler), nameof(UIDesyncHandler.RaiseDesync))]
    internal static class DesyncLogPatch
    {
        private static void Postfix(object meta)
        {
            try
            {
                // meta 是 DesyncMeta（结构体，字段名未验证）——用反射取 tick，
                // 取不到也不要紧，至少"发生了"这件事被记下来了。
                string tick = "?";
                try
                {
                    if (meta != null)
                    {
                        var t = meta.GetType();
                        var f = t.GetField("Tick") ?? t.GetField("tick");
                        if (f != null) tick = Convert.ToString(f.GetValue(meta));
                        else
                        {
                            var p = t.GetProperty("Tick");
                            if (p != null) tick = Convert.ToString(p.GetValue(meta, null));
                        }
                    }
                }
                catch { }

                int now = -1;
                try { now = Game.Instance.RealTimeController.CurrentNetworkTick; } catch { }

                Main.LogError("[合作] ★★ 游戏检测到不同步 ★★  desync tick=" + tick
                            + "  当前 tick=" + now
                            + "  本机=" + (CoopState.IsHost ? "房主" : "加入方")
                            + "  在册卫兵=" + SafeCount()
                            + "  —— 把这一行的 tick 和上面最近一条 [合作] 发出 … 的时间对一下，"
                            + "紧挨着说明多半是本 mod 的动作，隔得远则多半无关。");
                Main.FlushLog(true);   // ERROR 本来就强制 flush，这里显式一次，防止崩溃丢日志
            }
            catch { }
        }

        private static string SafeCount()
        {
            try { return RetinueRegistry.Count.ToString(); }
            catch { return "?"; }
        }
    }
}
