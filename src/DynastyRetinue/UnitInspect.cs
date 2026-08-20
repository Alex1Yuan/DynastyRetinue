using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kingmaker;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities.Base;   // Entity / GetHealthOptional 的来源
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Parts;

namespace DynastyRetinue
{
    /// <summary>
    /// 列出当前区域里的全部单位：蓝图名 + GUID + 中文显示名 + 体型 + 血量。
    ///
    /// ★为什么需要它★
    ///   要找某个游戏里见到的单位（比如收藏库那位死亡守望队长「佐拉尔」），
    ///   靠离线提取的 units.tsv 反查是不可靠的，今天连续吃了两次亏：
    ///     · 在 units.tsv 里搜 deathwatch 一无所获，就下结论"游戏里没有" ——
    ///       实际蓝图包里有整条任务线（可救可杀）和展品对话，那份表本身不完整；
    ///     · 中文译名和英文蓝图名经常毫无关系，"佐拉尔"按 Zoral/Zorah/Sorel
    ///       三种拼法在 84 万个标识符里都是 0 命中。
    ///   走到它面前点一下，游戏自己会告诉你它是谁 —— 这是唯一不用猜的路径。
    ///
    /// ★为什么不做"读取鼠标下的单位"★
    ///   那需要射线检测或者拿悬停态，都是没验证过的 API，而收益完全一样。
    ///   列全区域还多回答一个问题：这屋里还有什么。
    ///
    /// 只读。不生成、不修改、不销毁任何东西。
    /// </summary>
    public static class UnitInspect
    {
        /// <summary>一次最多打多少行 —— 有些区域上百个单位，全打出来日志没法看。</summary>
        private const int MaxRows = 120;

        /// <summary>
        /// <paramref name="filter"/> 为空则列全部；否则对蓝图名和显示名做**不分大小写的包含匹配**，
        /// 所以中英文关键词都能用（「守望」「Deathwatch」「Spacemarine」都行）。
        /// </summary>
        public static void Run(string filter)
        {
            try
            {
                filter = (filter ?? "").Trim();
                var rows = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int total = 0;

                foreach (var st in RetinueRegistry.AllStates())
                {
                    List<Entity> snapshot;
                    try { snapshot = st.AllEntityData != null ? st.AllEntityData.ToList() : null; }
                    catch { continue; }
                    if (snapshot == null) continue;

                    foreach (var e in snapshot)
                    {
                        var u = e as BaseUnitEntity;
                        if (u == null) continue;
                        string uid;
                        try { uid = u.UniqueId; } catch { continue; }
                        if (uid == null || !seen.Add(uid)) continue;
                        total++;

                        string bpName = "?", guid = "?", shown = "?", size = "?", hp = "?";
                        try
                        {
                            var bp = u.OriginalBlueprint ?? u.Blueprint;
                            if (bp != null)
                            {
                                bpName = bp.name;
                                guid = bp.AssetGuid.ToString();
                                try { size = bp.Size.ToString(); } catch { }
                                // CharacterName 是本地化串 —— 这才是玩家在游戏里看到的那个名字，
                                // 也是"佐拉尔"这类译名唯一能对上的地方。
                                try { shown = bp.CharacterName; } catch { }
                            }
                        }
                        catch { }
                        try
                        {
                            var d = u.GetOptional<PartUnitDescription>();
                            if (d != null && !string.IsNullOrEmpty(d.CustomName)) shown = d.CustomName;
                        }
                        catch { }
                        try
                        {
                            var h = u.GetHealthOptional();
                            if (h != null) hp = h.HitPointsLeft + "/" + h.MaxHitPoints;
                        }
                        catch { }

                        if (filter.Length > 0
                            && bpName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                            && (shown ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        rows.Add($"  {shown,-22} {bpName,-46} {guid}  {size,-10} hp={hp}");
                    }
                }

                Main.Log("========== 区域单位一览 ==========");
                Main.Log(filter.Length > 0
                    ? $"区域内共 {total} 个单位，匹配「{filter}」的 {rows.Count} 个"
                    : $"区域内共 {total} 个单位");
                foreach (var r in rows.Take(MaxRows)) Main.Log(r);
                if (rows.Count > MaxRows)
                    Main.Log($"  …… 另有 {rows.Count - MaxRows} 个未列出（用关键词过滤缩小范围）");
                Main.Log("========== 区域单位一览结束 ==========");
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError(e); Main.FlushLog(true); }
        }
    }
}
