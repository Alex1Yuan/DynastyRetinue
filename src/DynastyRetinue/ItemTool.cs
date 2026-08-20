using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items;
using Kingmaker.Blueprints.Items.Armors;

namespace DynastyRetinue
{
    /// <summary>
    /// 物品名录：把「中文显示名 → 物品蓝图 GUID」这张表建出来。
    ///
    /// 为什么必须在运行时做：
    ///   cd2.txt 只有蓝图的**内部名**（如 "Weapon_HeavyBolter_Improved"），
    ///   而加点方案描述里写的是**中文显示名**（"改良重型爆矢枪"）。
    ///   显示名存在蓝图自身的 m_DisplayName(LocalizedString) 里，指向 zhCN.json 的一个 key，
    ///   而 blueprints-pack.bbp 是二进制，离线读不出这个 key —— 中间那一环断了。
    ///   游戏进程里这三者都是现成的：BlueprintMechanicEntityFact.Name
    ///   （BlueprintMechanicEntityFact.cs:43）直接返回已本地化的字符串。
    ///
    /// 输入 items.tsv（离线从 cd2.txt 筛出的 2940 条物品蓝图，guid/类型/内部名）；
    /// 输出 items_zh.tsv 追加一列中文名。之后配装备就能按名字写模板了。
    /// </summary>
    public static class ItemTool
    {
        public sealed class Row
        {
            public string Guid;
            public string Type;
            public string InternalName;
            public string ZhName;
        }

        private static List<Row> _cache;

        private static string ModDir { get { return Main.ModEntry != null ? Main.ModEntry.Path : "."; } }
        private static string SourcePath { get { return Path.Combine(ModDir, "items.tsv"); } }
        private static string OutputPath { get { return Path.Combine(ModDir, "items_zh.tsv"); } }

        /// <summary>
        /// 加载 items.tsv 并逐条解析显示名。2940 条 TryGetBlueprint 会强制加载这些蓝图，
        /// 耗时几秒、内存增量不大（物品蓝图都很小）。只在点按钮时跑一次，结果缓存。
        /// </summary>
        public static List<Row> Load(bool force)
        {
            if (_cache != null && !force) return _cache;

            var src = SourcePath;
            if (!File.Exists(src))
            {
                Main.LogError("找不到 items.tsv：" + src
                              + "\n    它是离线从 cd2.txt 筛出来的物品蓝图清单，需要放进 mod 目录。");
                return null;
            }

            var rows = new List<Row>();
            int miss = 0;
            foreach (var line in File.ReadAllLines(src, Encoding.UTF8))
            {
                if (string.IsNullOrEmpty(line)) continue;
                var f = line.Split('\t');
                if (f.Length < 3) continue;

                var r = new Row { Guid = f[0], Type = f[1], InternalName = f[2] };
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintItem>(r.Guid);
                    // 解析不到多半是没启用的 DLC 蓝图 —— 记数即可，不是错误
                    if (bp == null) { miss++; r.ZhName = ""; }
                    else r.ZhName = bp.Name ?? "";
                }
                catch { r.ZhName = ""; miss++; }
                rows.Add(r);
            }

            _cache = rows;
            Main.Log("物品名录：读入 " + rows.Count + " 条，其中 " + miss + " 条解析不到（多半是未启用的 DLC）。");
            return rows;
        }

        /// <summary>拿某个 GUID 的中文名，用于面板显示。名录没加载就退回 GUID 尾号。</summary>
        public static string NameOf(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "?";
            if (_cache != null)
                foreach (var r in _cache)
                    if (string.Equals(r.Guid, guid, StringComparison.OrdinalIgnoreCase))
                        return string.IsNullOrEmpty(r.ZhName) ? r.InternalName : r.ZhName;
            // 名录没加载时不要在这里触发 2940 条蓝图加载 —— OnGUI 每帧都会调
            try
            {
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintItem>(guid);
                if (bp != null) return bp.Name;
            }
            catch { }
            return "…" + guid.Substring(Math.Max(0, guid.Length - 6));
        }

        /// <summary>把带中文名的完整名录写出去，便于离线配装备模板。</summary>
        public static void Export()
        {
            var rows = Load(true);
            if (rows == null) return;
            try
            {
                var sb = new StringBuilder();
                // ★ 加 level/rarity/slot ★ 配"按章节渐进的三套装备"要靠它们：
                // ItemLevel 是 RT 掉落门控的依据，等价于"第几章能拿到"；
                // Rarity 区分同级里的好坏；type 决定它能进哪个槽。
                sb.AppendLine("zh\tguid\ttype\tinternal\tlevel\trarity\tsubtype");
                foreach (var r in rows)
                {
                    int lv = -1; string rar = "", sub = "";
                    try
                    {
                        var bp = ResourcesLibrary.TryGetBlueprint<Kingmaker.Blueprints.Items.BlueprintItem>(r.Guid);
                        if (bp != null)
                        {
                            lv = bp.ItemLevel;
                            rar = bp.Rarity.ToString();
                            try { sub = bp.SubtypeName ?? ""; } catch { }
                        }
                    }
                    catch { }
                    sb.Append(r.ZhName).Append('\t').Append(r.Guid).Append('\t')
                      .Append(r.Type).Append('\t').Append(r.InternalName).Append('\t')
                      .Append(lv).Append('\t').Append(rar).Append('\t').Append(sub).AppendLine();
                }
                File.WriteAllText(OutputPath, sb.ToString(), new UTF8Encoding(false));
                Main.Log("已导出物品名录（含 level/rarity）-> " + OutputPath);
            }
            catch (Exception e) { Main.LogError("导出失败: " + e.Message); }
        }

        /// <summary>上次查询命中的结果 —— 面板要显示出来。
        /// v0.3.4 只把结果写进日志，面板一个字不显示，用户以为按钮没反应。</summary>
        public static readonly List<Row> LastHits = new List<Row>();
        public static string LastQuery = "";
        public static int LastHitTotal;

        /// <summary>
        /// 把所有护甲按数值排出来 —— "哪件护甲最强"离线答不了（数值在二进制蓝图包里），
        /// 只能在运行时读 BlueprintItemArmor.DamageAbsorption / DamageDeflection。
        /// 结果同时写进 armor_rank.tsv，便于配模板时挑。
        /// </summary>
        public static void RankArmor()
        {
            var rows = Load(false);
            if (rows == null) return;

            var list = new List<string>();
            var lines = new List<string>();
            foreach (var r in rows)
            {
                if (r.Type == null || r.Type.IndexOf("Armor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintItemArmor>(r.Guid);
                    if (bp == null) continue;
                    int abs = 0, def = 0;
                    try { abs = bp.DamageAbsorption; } catch { }
                    try { def = bp.DamageDeflection; } catch { }
                    string cat = "?", prof = "?";
                    try { cat = bp.Category.ToString(); } catch { }
                    try { prof = bp.ProficiencyGroup.ToString(); } catch { }

                    // 独特效果的真正落点是 AddFactToEquipmentWielder 组件
                    // （BlueprintItemEquipment.cs:55 的 Abilities 就是从它取的），
                    // 给穿戴者挂 fact/技能。Enchantments 只是其中一部分，之前只读它所以全空。
                    // 用反射扫组件，免得为几个类型去加程序集引用。
                    var eff = new List<string>();
                    try
                    {
                        foreach (var c in bp.ComponentsArray)
                        {
                            if (c == null) continue;
                            var tn = c.GetType().Name;
                            if (tn.IndexOf("AddFact", StringComparison.Ordinal) < 0
                                && tn.IndexOf("Enchant", StringComparison.Ordinal) < 0) continue;
                            string got = null;
                            foreach (var pn in new[] { "Fact", "Enchantment", "m_Fact" })
                            {
                                try
                                {
                                    var mi = c.GetType().GetProperty(pn) as System.Reflection.MemberInfo
                                          ?? c.GetType().GetField(pn, System.Reflection.BindingFlags.Instance
                                             | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                    object v = null;
                                    var pi2 = mi as System.Reflection.PropertyInfo;
                                    var fi2 = mi as System.Reflection.FieldInfo;
                                    if (pi2 != null) v = pi2.GetValue(c, null);
                                    else if (fi2 != null) v = fi2.GetValue(c);
                                    if (v == null) continue;
                                    var bpf = v as BlueprintScriptableObject;
                                    if (bpf != null)
                                    {
                                        // 解析成中文名+描述 —— 只给内部名的话看不出效果是什么，
                                        // 没法拿来跟"高 35 点吸收"权衡
                                        string zn = null, zd = null;
                                        try { zn = (bpf as Kingmaker.UnitLogic.Mechanics.Blueprints.BlueprintMechanicEntityFact).Name; } catch { }
                                        try { zd = (bpf as Kingmaker.UnitLogic.Mechanics.Blueprints.BlueprintMechanicEntityFact).Description; } catch { }
                                        if (!string.IsNullOrEmpty(zd))
                                        {
                                            zd = zd.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
                                            if (zd.Length > 90) zd = zd.Substring(0, 90) + "…";
                                        }
                                        got = (string.IsNullOrEmpty(zn) ? bpf.name : zn)
                                            + (string.IsNullOrEmpty(zd) ? "" : "：" + zd);
                                        break;
                                    }
                                }
                                catch { }
                            }
                            eff.Add(got ?? tn);
                        }
                    }
                    catch { }
                    try
                    {
                        foreach (var en in bp.Enchantments)
                            if (en != null && !string.IsNullOrEmpty(en.name)) eff.Add(en.name);
                    }
                    catch { }

                    // 物品自己的 Description 才是玩家在 tooltip 里看到的那段说明。
                    // Feature 上的描述经常是空的（169 件里只有 41 件读得出），
                    // 而且恰好几件顶级动力甲都属于读不出的那批。
                    string desc = null;
                    try { desc = bp.Description; } catch { }
                    if (!string.IsNullOrEmpty(desc))
                    {
                        desc = System.Text.RegularExpressions.Regex.Replace(desc, "<[^>]+>", "");
                        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\{[^}]*\}", "");
                        desc = desc.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Trim();
                        while (desc.Contains("  ")) desc = desc.Replace("  ", " ");
                    }

                    lines.Add(string.Format("{0:D4}\t{1:D4}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}",
                        abs, def, string.IsNullOrEmpty(r.ZhName) ? r.InternalName : r.ZhName,
                        cat, prof, r.Guid,
                        eff.Count == 0 ? "-" : string.Join(" / ", eff.ToArray()),
                        string.IsNullOrEmpty(desc) ? "-" : desc));
                }
                catch { }
            }
            lines.Sort();
            lines.Reverse();

            Main.Log("=== 护甲排名（按吸收/偏转，前 25；附魔=独特效果，需自行权衡）===");
            for (int i = 0; i < lines.Count && i < 25; i++)
            {
                var f = lines[i].Split('\t');
                Main.Log(string.Format("  吸收{0} 偏转{1}  {2,-18} [{3}]  {4}",
                    int.Parse(f[0]), int.Parse(f[1]), f[2], f[3], f[6]));
            }
            try
            {
                var sb = new StringBuilder("absorption\tdeflection\tzh\tcategory\tproficiency\tguid\teffects\tdescription\n");
                foreach (var l in lines) sb.AppendLine(l);
                File.WriteAllText(Path.Combine(ModDir, "armor_rank.tsv"), sb.ToString(), new UTF8Encoding(false));
                Main.Log("  共 " + lines.Count + " 件，完整排名（含附魔列）-> armor_rank.tsv");
            }
            catch (Exception e) { Main.LogError("写 armor_rank.tsv 失败: " + e.Message); }
        }

        /// <summary>
        /// 导出天赋/技能名录（features.tsv -> features_zh.tsv）。
        /// 用途：攻略里的关键天赋只有中文名（"专家技艺""猎手伏击"），
        /// 要把它们配进模板得先有名字→GUID 的对照。
        /// 跟物品那套同理，只能运行时做 —— 显示名在蓝图里指向 zhCN 的 key，离线接不上。
        /// </summary>
        public static void ExportFeatures()
        {
            var src = Path.Combine(ModDir, "features.tsv");
            if (!File.Exists(src)) { Main.LogError("找不到 features.tsv：" + src); return; }

            int n = 0, miss = 0;
            var sb = new StringBuilder("zh\tguid\ttype\tinternal\tdesc\n");
            foreach (var line in File.ReadAllLines(src, Encoding.UTF8))
            {
                if (string.IsNullOrEmpty(line)) continue;
                var f = line.Split('\t');
                if (f.Length < 3) continue;
                string zh = "", desc = "";
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintScriptableObject>(f[0])
                             as Kingmaker.UnitLogic.Mechanics.Blueprints.BlueprintMechanicEntityFact;
                    if (bp == null) miss++;
                    else
                    {
                        zh = bp.Name ?? "";
                        var d = bp.Description;
                        if (!string.IsNullOrEmpty(d))
                        {
                            d = System.Text.RegularExpressions.Regex.Replace(d, "<[^>]+>", "");
                            d = System.Text.RegularExpressions.Regex.Replace(d, @"\{[^}]*\}", "");
                            desc = d.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Trim();
                            if (desc.Length > 120) desc = desc.Substring(0, 120) + "…";
                        }
                    }
                }
                catch { miss++; }
                sb.Append(zh).Append('\t').Append(f[0]).Append('\t').Append(f[1]).Append('\t')
                  .Append(f[2]).Append('\t').Append(desc).AppendLine();
                n++;
            }
            try
            {
                File.WriteAllText(Path.Combine(ModDir, "features_zh.tsv"), sb.ToString(), new UTF8Encoding(false));
                Main.Log("天赋名录：" + n + " 条（" + miss + " 条解析不到）-> features_zh.tsv");
            }
            catch (Exception e) { Main.LogError("写 features_zh.tsv 失败: " + e.Message); }
        }

        /// <summary>按中文名（或内部名）子串查 GUID。加点方案里写的名字直接往里贴。</summary>
        public static void Search(string q)
        {
            LastHits.Clear(); LastQuery = q ?? ""; LastHitTotal = 0;
            if (string.IsNullOrEmpty(q)) { Main.Log("请先在输入框里填要查的物品名。"); return; }
            var rows = Load(false);
            if (rows == null) return;

            int hit = 0;
            Main.Log("=== 查物品「" + q + "」===");
            foreach (var r in rows)
            {
                bool m = (!string.IsNullOrEmpty(r.ZhName) && r.ZhName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                      || (!string.IsNullOrEmpty(r.InternalName) && r.InternalName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!m) continue;
                hit++;
                if (LastHits.Count < 20) LastHits.Add(r);
                if (hit <= 30)
                    Main.Log("  " + r.ZhName + "  [" + r.Type + "]  " + r.Guid + "  (" + r.InternalName + ")");
            }
            LastHitTotal = hit;
            if (hit == 0) Main.Log("  没找到。换个关键词试试，或先点【导出物品名录】看全表。");
            else if (hit > 30) Main.Log("  ...共 " + hit + " 条，只显示前 30 条。");
        }
    }
}
