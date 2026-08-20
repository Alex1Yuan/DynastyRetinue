using System;
using Kingmaker;

namespace DynastyRetinue
{
    /// <summary>
    /// 用**利润因子**（Profit Factor）解锁招募上限。
    ///
    /// 设计（用户拍板）：每 15 点利润因子解锁 1 名，上限默认 6 名 ——
    /// 也就是 90 利润因子解锁全部。两个数都可在面板改。
    ///
    /// 为什么用利润因子而不是等级：它是行商浪人这个身份的核心资源，
    /// "有钱才养得起私兵"比"练级送人头"更贴设定；而且它在前中期涨得慢，
    /// 天然形成一条不需要额外做剧情门的成长曲线。
    ///
    /// ★ 取值口径 ★
    /// Game.Instance.Player.ProfitFactor.Total —— 这是原版自己用的那条：
    ///     ProfitFactorGetter.GetBaseValue():19
    ///         return Mathf.FloorToInt(Game.Instance.Player.ProfitFactor.Total);
    ///     RequirementProfitFactorMinimum.Check():23
    ///         return Game.Instance.Player.ProfitFactor.Total >= m_ProfitFactorMinimum;
    /// 注意是 **Total**（含各种加成后的总值），不是基础值 —— 与游戏内显示的数字一致。
    ///
    /// ★ 不写任何东西 ★ 全是只读查询，不碰存档。
    /// </summary>
    public static class ProfitFactorGate
    {
        /// <summary>当前利润因子（向下取整）。取不到返回 -1，调用方据此退化。</summary>
        public static int Current()
        {
            try
            {
                var p = Game.Instance != null ? Game.Instance.Player : null;
                if (p == null || p.ProfitFactor == null) return -1;
                return (int)Math.Floor(p.ProfitFactor.Total);
            }
            catch { return -1; }
        }

        /// <summary>每名卫兵需要多少利润因子。面板可调，最低 1（防除零）。</summary>
        public static int PerGuard()
        {
            try
            {
                int v = Main.Settings != null ? Main.Settings.RecruitPfPerGuard : 15;
                return v < 1 ? 1 : v;
            }
            catch { return 15; }
        }

        /// <summary>硬上限（面板可调）。</summary>
        public static int HardCap()
        {
            try
            {
                int v = Main.Settings != null ? Main.Settings.RecruitMaxGuards : 6;
                return v < 0 ? 0 : v;
            }
            catch { return 6; }
        }

        /// <summary>
        /// 当前利润因子解锁了几个名额。
        /// 取不到利润因子时**返回硬上限**而不是 0 ——
        /// 读不出数值是我们的问题，不该因此把玩家的功能锁死。
        /// </summary>
        public static int Unlocked()
        {
            int pf = Current();
            if (pf < 0) return HardCap();
            int n = pf / PerGuard();
            int cap = HardCap();
            return n > cap ? cap : n;
        }

        /// <summary>再要多少利润因子能多解锁一名。已满返回 -1。</summary>
        public static int NextThreshold()
        {
            int have = Unlocked();
            if (have >= HardCap()) return -1;
            return (have + 1) * PerGuard();
        }

        /// <summary>面板/UI 用的一行摘要。</summary>
        public static string Summary()
        {
            int pf = Current();
            if (pf < 0) return "利润因子读不到（不在游戏内？）——暂不限制";

            int un = Unlocked(), cap = HardCap(), next = NextThreshold();
            string s = "利润因子 " + pf + "　已解锁 " + un + " / " + cap + " 名";
            if (next > 0) s += "　（再到 " + next + " 解锁第 " + (un + 1) + " 名，还差 " + (next - pf) + "）";
            else s += "　（已全部解锁）";
            return s;
        }

        /// <summary>分级表，给 UI 画进度用。返回每一档需要的利润因子。</summary>
        public static int[] Thresholds()
        {
            int cap = HardCap(), per = PerGuard();
            var r = new int[cap];
            for (int i = 0; i < cap; i++) r[i] = (i + 1) * per;
            return r;
        }
    }
}
