using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Parts;

namespace DynastyRetinue
{
    /// <summary>
    /// 把招募交互挂到船上的 NPC 身上。
    ///
    /// 交互本身不进存档（UnitPartInteractions.m_Interactions 没有 [JsonProperty]，
    /// 而存档序列化器是 OptIn 的），代价是**每次进区域都得重挂一遍** —— 这正是我们要的：
    /// 卸载 mod 之后它就自然消失了，存档里没有任何东西需要解析。
    ///
    /// 目标 NPC 用蓝图名匹配（可在面板改），默认高阶顾问 —— 他是管家/总管，
    /// "帮你张罗人手"在设定上最顺，而且是固定在舰桥的非可直控 NPC。
    /// 后一点是硬约束：SurfaceMainInputLayer.cs:342-345 会把可直控的队伍成员整个跳过。
    /// </summary>
    public static class RecruitEntry
    {
        /// <summary>本次区域里已经挂过的单位，避免重复插入（AddInteraction 不去重）。</summary>
        private static readonly HashSet<string> _done = new HashSet<string>(StringComparer.Ordinal);

        public static void ResetForNewArea() { _done.Clear(); }

        /// <summary>在当前区域里找到目标 NPC 并挂上交互。返回挂载数量。</summary>
        public static int AttachInArea(bool verbose = false)
        {
            int n = 0;
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.NpcRecruitEntry) return 0;
                // ★ 两个入口不能同时挂 ★
                // 我们的 IUnitInteraction 是 IsApproach + MainPlayerPreferred，
                // 玩家点 NPC 想说话时它会跟原版对话交互一起触发 —— 表现为
                // "走过去点一下，招募窗自己弹出来了，还没点对话选项"。
                // 对话入口开着的时候，那条选项就是唯一入口，这个点击交互不再挂。
                if (Main.Settings.DialogRecruitEntry)
                {
                    if (verbose) Main.Log("[招募] 对话入口已启用，跳过点击交互（两者同挂会一点 NPC 就弹窗）。");
                    return 0;
                }
                var game = Game.Instance;
                if (game == null || game.State == null || game.State.AllBaseUnits == null) return 0;

                var keys = SplitKeys(Main.Settings.RecruitNpcKeys);
                if (keys.Count == 0) return 0;

                foreach (var u in game.State.AllBaseUnits)
                {
                    if (u == null || !u.IsInGame) continue;
                    string bp = null;
                    try { bp = u.Blueprint != null ? u.Blueprint.name : null; } catch { }
                    if (string.IsNullOrEmpty(bp)) continue;

                    bool hit = false;
                    foreach (var k in keys)
                        if (bp.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                    if (!hit) continue;

                    string uid = null;
                    try { uid = u.UniqueId; } catch { }
                    if (uid != null && _done.Contains(uid)) continue;

                    try
                    {
                        var part = u.GetOrCreate<UnitPartInteractions>();
                        if (part == null) continue;
                        // 已经挂过就不重复插（换区域时 _done 会清，但同一实体可能残留）
                        bool already = false;
                        foreach (var it in part.Interactions) if (it is RecruitInteraction) { already = true; break; }
                        if (!already) part.AddInteraction(new RecruitInteraction());
                        if (uid != null) _done.Add(uid);
                        n++;
                        if (verbose) Main.LogVerbose("[招募] 已挂到 " + bp);
                    }
                    catch (Exception e) { Main.LogError("[招募] 挂载失败 " + bp + ": " + e.Message); }
                }
                if (n > 0) Main.LogVerbose("[招募] 本区域挂载 " + n + " 个入口");
                else if (verbose) Main.LogVerbose("[招募] 没有新增挂载 —— 要么本区域没有匹配的 NPC（当前关键字: "
                                           + Main.Settings.RecruitNpcKeys + "），要么已经挂过了。");
            }
            catch (Exception e) { Main.LogError("[招募] AttachInArea 异常: " + e.Message); }
            return n;
        }

        /// <summary>列出当前区域里所有非队伍单位的蓝图名，方便你挑要挂哪个。</summary>
        public static void ListCandidates(int max = 60)
        {
            try
            {
                var game = Game.Instance;
                if (game == null || game.State == null) { Main.Log("不在游戏内。"); return; }
                var seen = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var u in game.State.AllBaseUnits)
                {
                    if (u == null || !u.IsInGame) continue;
                    try
                    {
                        if (u.IsPlayerFaction && u.IsDirectlyControllable) continue;   // 可直控的挂不上
                        string bp = u.Blueprint != null ? u.Blueprint.name : null;
                        if (string.IsNullOrEmpty(bp)) continue;
                        int c; seen.TryGetValue(bp, out c); seen[bp] = c + 1;
                    }
                    catch { }
                }
                Main.Log("=== 当前区域可挂载候选（共 " + seen.Count + " 种）===");
                int i = 0;
                foreach (var kv in seen)
                {
                    if (i++ >= max) { Main.Log("  …还有 " + (seen.Count - max) + " 种"); break; }
                    Main.Log("  " + kv.Key + (kv.Value > 1 ? "  ×" + kv.Value : ""));
                }
            }
            catch (Exception e) { Main.LogError("[招募] 列举失败: " + e.Message); }
        }

        private static List<string> SplitKeys(string s)
        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(s)) return l;
            foreach (var p in s.Split(',', ';', '|'))
            {
                var t = p.Trim();
                if (t.Length > 0) l.Add(t);
            }
            return l;
        }
    }
}
