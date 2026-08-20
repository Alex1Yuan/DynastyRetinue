using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 字体覆盖检查：把会显示在**游戏世界里**的每个字，拿去问游戏实际用的字体有没有。
    ///
    /// ★为什么必须实测，不能靠判断字库等级★
    ///   原来的构建门禁判据是「必须在 GB2312 一级字库（3755 常用字）内」，
    ///   理由是"任何中文字体都不会漏掉常用字"。实机把这个判据证伪了：
    ///   `裴`(U+88F4) 明确在一级字库里，检查放行了它，游戏内名条照样是方框。
    ///   Owlcat 这套字体的子集**不是**按 GB2312 分档切的，
    ///   任何基于字库等级的推断都是猜。
    ///
    ///   而 mod 自己的设置面板用的是 Unity 内置字体（覆盖面大得多），
    ///   同一个字在面板里显示完全正常 —— 对着面板怎么看都发现不了。
    ///
    /// ★第一版会卡死游戏，这是重写后的★
    ///   第一版对每个字调 HasCharacter(c, searchFallbacks:true)。
    ///   那个调用每次都要重新走一遍 fallback 链，于是总开销是
    ///   字符数 × 字体数 × 链深度，全部同步跑在主线程上 —— 实机表现是点完就卡住。
    ///   现在改成：每个字体的字符表**只遍历一次**、连同 fallback 链一起并进一个
    ///   HashSet&lt;uint&gt;，之后全是 O(1) 集合查找。
    ///   fallback 链用 visited 集防环 —— TMP 允许 A→B→A 这种配置。
    /// </summary>
    public static class FontCheck
    {
        /// <summary>字体数量上限。防止某些场景下加载了上百个字体资产把这个检查拖垮。</summary>
        private const int MaxFonts = 40;

        public static void Run()
        {
            try
            {
                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                                     .Where(f => f != null)
                                     .GroupBy(f => f.name)          // 同名重复资产只留一个
                                     .Select(g => g.First())
                                     .Take(MaxFonts)
                                     .ToList();
                if (fonts.Count == 0)
                {
                    Main.LogError("没找到任何已加载的 TMP 字体 —— 请先进入游戏内再点。");
                    Main.FlushLog(true);
                    return;
                }

                var chars = CollectNameChars();
                Main.Log("========== 字体覆盖检查 ==========");
                Main.Log($"字体 {fonts.Count} 个，待检字符 {chars.Count} 个");

                // 一次性把每个字体（含 fallback）的字符集建好
                var sets = new List<(string Name, HashSet<uint> Set)>();
                foreach (var f in fonts)
                {
                    var set = new HashSet<uint>();
                    Collect(f, set, new HashSet<int>(), 0);
                    sets.Add((f.name, set));
                }
                Main.Log("字符集构建完成：" + string.Join(", ",
                    sets.OrderByDescending(s => s.Set.Count).Take(6).Select(s => $"{s.Name}({s.Set.Count})")));

                var none = new List<char>();
                var some = new List<string>();
                foreach (var c in chars)
                {
                    var have = sets.Where(s => s.Set.Contains(c)).Select(s => s.Name).ToList();
                    if (have.Count == 0) none.Add(c);
                    else if (have.Count < sets.Count)
                        some.Add($"{c} U+{(int)c:X4}  {have.Count}/{sets.Count}: {string.Join(",", have.Take(3))}");
                }

                if (none.Count == 0)
                    Main.Log("★没有任何字符是所有字体都缺的★（那方框可能来自名条专用的某一个字体，看下面的分档）");
                else
                {
                    Main.Log($"★★★ 所有字体都没有 = 必然方框，共 {none.Count} 个 ★★★");
                    Main.Log("    " + string.Join("  ", none.Select(c => $"{c}(U+{(int)c:X4})")));
                }

                Main.Log($"--- 只有部分字体有的字，共 {some.Count} 个（名条用哪个字体决定会不会方框）---");
                foreach (var line in some.Take(120)) Main.Log("    " + line);
                if (some.Count > 120) Main.Log($"    …… 另有 {some.Count - 120} 个未列出");

                DumpCharset(sets);

                Main.Log("========== 字体覆盖检查结束 ==========");
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError(e); Main.FlushLog(true); }
        }

        /// <summary>
        /// 把所有字体的字符集并集导出到 mod 目录下的 font_charset.txt。
        ///
        /// ★为什么必须导出，不能只报告"这几个字有问题"★
        ///   报告只覆盖**当前用到的**字。换名字时挑的新字有没有被字体收录，
        ///   报告答不了 —— 于是只能凭感觉猜，而这一轮已经猜错两次了：
        ///     · 第一次靠 GB2312 一级字库判断，`裴` 在一级字库里但字体没有；
        ///     · 第二次"修" 邵岐 时换掉了 岐，可实际缺的是 邵。
        ///   把并集导出来当白名单，构建期就能离线判定任何候选字，不用再进游戏试。
        ///
        /// 只导出 CJK 区（U+2E80–U+9FFF）—— 拉丁字母部分几万个码位没有意义，
        /// 而且名字里只可能出现汉字。
        /// </summary>
        private static void DumpCharset(List<(string Name, HashSet<uint> Set)> sets)
        {
            try
            {
                var union = new SortedSet<uint>();
                foreach (var s in sets)
                    foreach (var cp in s.Set)
                        if (cp >= 0x2E80 && cp <= 0x9FFF) union.Add(cp);

                var sb = new System.Text.StringBuilder(union.Count + 512);
                sb.AppendLine("# 游戏中文字体实测能显示的 CJK 字符并集。");
                sb.AppendLine("# 由 mod 开发区的「字体覆盖检查」导出，不要手改。");
                sb.AppendLine("# 用途：tools/check_names.py 拿它当白名单，构建期拦下会显示成方框的名字。");
                sb.AppendLine("# 判据是「字体里有没有」，不是「这字常不常用」—— 后者已经被证伪两次。");
                sb.AppendLine("# 字体来源：" + string.Join(", ",
                    sets.OrderByDescending(s => s.Set.Count).Take(8).Select(s => s.Name)));
                sb.AppendLine("# 共 " + union.Count + " 字");
                int n = 0;
                foreach (var cp in union)
                {
                    sb.Append((char)cp);
                    if (++n % 64 == 0) sb.AppendLine();
                }
                sb.AppendLine();

                string path = System.IO.Path.Combine(Main.ModEntry?.Path ?? ".", "font_charset.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                Main.Log($"★字符集已导出：{path}（{union.Count} 字）");
                Main.Log("  把它拷进仓库的 tools/ 目录，check_names.py 就能离线判定任何候选字。");
            }
            catch (Exception e) { Main.LogError("导出字符集失败: " + e.Message); }
        }

        /// <summary>
        /// 把一个字体及其 fallback 链的全部字符并进 set。
        /// visited 用 GetInstanceID 防环 —— TMP 允许 A→B→A 这种配置，
        /// 不防的话这里会无限递归。depth 是第二道保险。
        /// </summary>
        private static void Collect(TMP_FontAsset f, HashSet<uint> set, HashSet<int> visited, int depth)
        {
            if (f == null || depth > 8) return;
            if (!visited.Add(f.GetInstanceID())) return;
            try
            {
                var table = f.characterTable;
                if (table != null)
                    for (int i = 0; i < table.Count; i++)
                        if (table[i] != null) set.Add(table[i].unicode);
            }
            catch { }
            try
            {
                var fb = f.fallbackFontAssetTable;
                if (fb != null)
                    foreach (var g in fb) Collect(g, set, visited, depth + 1);
            }
            catch { }
        }

        /// <summary>
        /// 收集所有会变成 CustomName 的字符。
        /// 只收这些 —— 面板文案、装备名走的是别的字体，没有这个问题。
        /// </summary>
        private static SortedSet<char> CollectNameChars()
        {
            var set = new SortedSet<char>();
            void Eat(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                foreach (var c in s)
                    if (c > 0x2E80) set.Add(c);   // ASCII 和西文标点不用查
            }

            try
            {
                // ★同上：Get(i) 是钳制的，不能靠它返回 null 来收尾★
                var all = Archetypes.All;
                if (all != null)
                    foreach (var a in all)
                    {
                        if (a == null) continue;
                        if (a.GuardNames != null) foreach (var r in a.GuardNames) Eat(r);
                        if (a.Elites != null)
                            foreach (var e in a.Elites) { if (e == null) continue; Eat(e.Rank); Eat(e.Name); }
                    }
            }
            catch (Exception e) { Main.LogError("收集分型名字失败: " + e.Message); }

            try
            {
                if (Archetypes.GuardNamePool != null)
                    foreach (var p in Archetypes.GuardNamePool) Eat(p);
            }
            catch (Exception e) { Main.LogError("收集人名池失败: " + e.Message); }

            return set;
        }
    }
}
