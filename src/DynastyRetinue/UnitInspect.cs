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
        /// <summary>
        /// 按**游戏内显示名**在全部单位蓝图里搜。
        ///
        /// ★为什么需要它★
        ///   区域一览只能看当前区域。而要找的东西常常在别处 ——
        ///   比如收藏库那位死亡守望连长「佐拉尔」：全游戏 3069 个单位里
        ///   没有任何一个蓝图名带 deathwatch 或 zoral，他一定叫别的名字。
        ///   离线也走不通：本地化 key 能查到（0baca466-…），
        ///   但蓝图正文在 .bbp 里是压缩的，反查不到是谁引用了它。
        ///   那就换个方向 —— 把全部单位挨个加载，问它们各自的显示名。
        ///
        /// ★为什么要外部索引文件★
        ///   运行时枚举全部蓝图没有现成的公开 API。
        ///   units_all.tsv 由 tools/dump_unit_index.py 从游戏自带的
        ///   cheatdata.json 导出（那是权威索引，每条都带类型）。
        ///   文件不在发布包里，只有开发机上有 —— 这是纯调研工具。
        ///
        /// ★代价★ 会真的加载 3069 个蓝图，几秒到十几秒，期间游戏会卡住。
        ///   只在开发区、只在需要时点。
        /// </summary>
        public static void SearchByDisplayName(string keyword)
        {
            try
            {
                keyword = (keyword ?? "").Trim();
                if (keyword.Length == 0) { Main.LogError("请先填关键词。"); Main.FlushLog(true); return; }

                string path = System.IO.Path.Combine(Main.ModEntry?.Path ?? ".", "units_all.tsv");
                if (!System.IO.File.Exists(path))
                {
                    Main.LogError("找不到 units_all.tsv —— 跑 tools/dump_unit_index.py 生成它。");
                    Main.FlushLog(true); return;
                }

                var lines = System.IO.File.ReadAllLines(path);
                Main.Log("========== 按显示名搜索单位 ==========");
                Main.Log($"索引 {lines.Length} 个单位，关键词「{keyword}」　（要逐个加载蓝图，会卡几秒）");

                int hit = 0, bad = 0;
                foreach (var line in lines)
                {
                    var t = line.Split('	');
                    if (t.Length < 2) continue;
                    object raw = null;
                    try { raw = Kingmaker.Blueprints.ResourcesLibrary.TryGetBlueprint(t[1]); } catch { bad++; continue; }
                    var bp = raw as Kingmaker.Blueprints.BlueprintUnit;
                    if (bp == null) { bad++; continue; }
                    string cn = null;
                    try { cn = bp.CharacterName; } catch { }
                    if (string.IsNullOrEmpty(cn)) continue;
                    if (cn.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hit++;
                    string size = "?";
                    try { size = bp.Size.ToString(); } catch { }
                    Main.Log($"  ★ {cn,-20} {t[0],-46} {t[1]}  {size}");
                    if (hit >= 40) { Main.Log("  （命中过多，已截断）"); break; }
                }
                Main.Log($"命中 {hit} 个；解析失败/非单位 {bad} 个");
                Main.Log("========== 搜索结束 ==========");
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError(e); Main.FlushLog(true); }
        }

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
