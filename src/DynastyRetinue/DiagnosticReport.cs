using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 一键导出诊断包 —— 玩家反馈问题时点一下，把该带的信息一次凑齐。
    ///
    /// ================= 为什么不是"把 dynasty_log.txt 发过来" =================
    /// 1. **太大**：实测 3.8 MB。论坛/Discord 传不上去，玩家多半会截个图代替，
    ///    而截图恰好丢掉了最有用的上下文。
    /// 2. **含隐私**：日志里到处是 C:\Users\&lt;用户名&gt;\... 的绝对路径。
    ///    让玩家自己去打码不现实。
    /// 3. **缺关键状态**：真正决定问题性质的是"当时的设置和在册情况"，
    ///    而那些不一定在日志尾部 —— 玩家可能改完设置玩了两小时才出问题。
    ///
    /// 所以导出的是：**状态快照 + 日志尾部**，并把用户名换成 &lt;USER&gt;。
    /// 体积控制在一封邮件能带走的范围。
    /// </summary>
    public static class DiagnosticReport
    {
        /// <summary>日志尾部保留多少字节。够覆盖"出问题前后"，又不至于传不动。</summary>
        private const int TailBytes = 400 * 1024;

        public static string LastPath { get; private set; }

        public static string Export()
        {
            try
            {
                string dir = Main.ModEntry != null ? Main.ModEntry.Path : null;
                if (string.IsNullOrEmpty(dir)) { Main.LogError("[诊断包] 拿不到 mod 目录。"); return null; }

                var sb = new StringBuilder(64 * 1024);
                Header(sb);
                Integrity(sb, dir);
                SettingsDump(sb);
                RuntimeState(sb);
                LogTail(sb, dir);

                string name = "dynasty_report_" + Ver() + "_"
                            + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
                string path = Path.Combine(dir, name);
                File.WriteAllText(path, Scrub(sb.ToString()), new UTF8Encoding(false));
                LastPath = path;

                Main.Log("[诊断包] 已导出: " + path
                       + "\n  内容 = 版本/设置/在册情况/舰船状态 + 日志最后 " + (TailBytes / 1024) + " KB，"
                       + "用户名已替换成 <USER>。反馈时带上这一个文件就够了。");
                return path;
            }
            catch (Exception e) { Main.LogError("[诊断包] 导出失败: " + e); return null; }
        }

        private static string Ver()
        {
            try { return Main.ModEntry != null && Main.ModEntry.Info != null ? Main.ModEntry.Info.Version : "?"; }
            catch { return "?"; }
        }

        /// <summary>
        /// 数据文件指纹核对。**只标注，不拦截** —— 对不上照常运行。
        ///
        /// ★用途是省时间，不是防人★
        /// 别人（或用户自己）改过 archetypes.json / plans.json 之后发来一份 bug 报告，
        /// 不标出来的话，会照着原版代码去查一个根本不存在的问题，白烧几小时。
        /// 常见的合理情形也不少：用户自己调过配表忘了、两个版本的文件混在一起、
        /// 或者装的是别人二次分发的版本。
        ///
        /// 预期哈希编在 DLL 里（BuildManifest.cs，由 tools/gen_manifest.py 在 bump 时生成），
        /// 不是放一个和数据文件并排的清单 —— 否则改配表的人顺手把清单一起改了就没意义了。
        /// 当然，能重编 DLL 的人照样能绕过；这挡的是「改 JSON」这一档，不是「改 DLL」那一档。
        ///
        /// 对正常玩家零感知：只出现在诊断包里，游戏内不提示、日志里不刷。
        /// </summary>
        private static void Integrity(StringBuilder sb, string dir)
        {
            sb.AppendLine();
            sb.AppendLine("---- 数据文件 ----");
            try
            {
                if (BuildManifest.Hashes == null || BuildManifest.Hashes.Count == 0)
                {
                    sb.AppendLine("  （本次构建没有指纹信息，跳过核对）");
                    return;
                }
                if (!string.Equals(BuildManifest.Version, Ver(), StringComparison.Ordinal))
                    sb.AppendLine("  ! DLL 内记录的版本 " + BuildManifest.Version
                                + " 与 Info.json 的 " + Ver() + " 不一致");

                foreach (var kv in BuildManifest.Hashes)
                {
                    string p = Path.Combine(dir, kv.Key);
                    if (!File.Exists(p)) { sb.AppendLine("  ✗ " + kv.Key.PadRight(18) + "缺失"); continue; }
                    string actual = Sha256(p);
                    bool same = string.Equals(actual, kv.Value, StringComparison.OrdinalIgnoreCase);
                    sb.AppendLine("  " + (same ? "✓ " : "★ ") + kv.Key.PadRight(18)
                                + actual.Substring(0, 16) + "…  "
                                + (same ? "与发布版一致" : "★与发布版不符 —— 该文件被修改过★"));
                }
            }
            catch (Exception e) { sb.AppendLine("  （核对失败: " + e.Message + "）"); }
        }

        private static string Sha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var h = sha.ComputeHash(fs);
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void Header(StringBuilder sb)
        {
            sb.AppendLine("======== DynastyRetinue 诊断包 ========");
            sb.AppendLine("导出时间   : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("mod 版本   : " + Ver());
            Try(sb, "UMM 版本   : ", delegate { return UnityModManagerNet.UnityModManager.version.ToString(); });
            Try(sb, "游戏版本   : ", delegate { return Application.version; });
            Try(sb, "Unity      : ", delegate { return Application.unityVersion; });
            Try(sb, "操作系统   : ", delegate { return SystemInfo.operatingSystem; });
            Try(sb, "语言(游戏) : ", delegate { return L.Current == L.ZhCN ? "中文" : "English"; });
            Try(sb, "mod 已启用 : ", delegate { return Main.Enabled ? "是" : "否"; });
            sb.AppendLine();
        }

        /// <summary>
        /// 设置快照。**只打和默认值不同的** —— 78 个字段全打出来没人看，
        /// 而"玩家改过什么"恰恰是最有信息量的那部分。
        /// </summary>
        private static void SettingsDump(StringBuilder sb)
        {
            sb.AppendLine("-------- 设置（只列改过的）--------");
            try
            {
                var cur = Main.Settings;
                if (cur == null) { sb.AppendLine("(Settings 为 null)"); sb.AppendLine(); return; }
                var def = new Settings();
                int n = 0;
                foreach (var f in typeof(Settings).GetFields(
                             System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    object a = null, b = null;
                    try { a = f.GetValue(cur); b = f.GetValue(def); } catch { continue; }
                    string sa = a == null ? "null" : a.ToString();
                    string sbv = b == null ? "null" : b.ToString();
                    if (sa == sbv) continue;
                    sb.AppendLine("  " + f.Name.PadRight(26) + " = " + sa + "   (默认 " + sbv + ")");
                    n++;
                }
                if (n == 0) sb.AppendLine("  (全是默认值)");
            }
            catch (Exception e) { sb.AppendLine("  读取失败: " + e.Message); }
            sb.AppendLine();
        }

        private static void RuntimeState(StringBuilder sb)
        {
            sb.AppendLine("-------- 运行时状态 --------");
            Try(sb, "在册卫兵   : ", delegate { return RetinueRegistry.Count + " 名"; });
            Try(sb, "名册明细   : ", delegate { return RetinueRegistry.Describe(); });
            Try(sb, "座舰分档   : ", delegate { return StarshipTool.CurrentSize().ToString(); });
            Try(sb, "座舰原生档 : ", delegate { return ShipDialog.OriginalSize().ToString(); });
            Try(sb, "自定义船模 : ", delegate
            {
                var p = StarshipViewTool.CurrentPrefab;
                if (string.IsNullOrEmpty(p)) return "(原版)";
                var m = ShipModelCatalog.ByPrefab(p);
                return (m != null ? m.Hull : p);
            });
            Try(sb, "废料       : ", delegate { return ShipDialog.Scrap().ToString(); });
            Try(sb, "利润因子   : ", delegate { return ProfitFactorGate.Summary(); });
            Try(sb, "分型模板   : ", delegate
            {
                var a = Archetypes.All;
                var names = new string[a.Length];
                for (int i = 0; i < a.Length; i++) names[i] = a[i].Name;
                return a.Length + " 个 —— " + string.Join(" / ", names);
            });
            sb.AppendLine();
        }

        private static void LogTail(StringBuilder sb, string dir)
        {
            sb.AppendLine("-------- 日志尾部（最后 " + (TailBytes / 1024) + " KB）--------");
            try
            {
                string lp = Path.Combine(dir, "dynasty_log.txt");
                if (!File.Exists(lp)) { sb.AppendLine("(找不到 dynasty_log.txt)"); return; }
                using (var fs = new FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - TailBytes);
                    if (start > 0) sb.AppendLine("(前面 " + (start / 1024) + " KB 已省略)");
                    fs.Seek(start, SeekOrigin.Begin);
                    using (var r = new StreamReader(fs, Encoding.UTF8))
                    {
                        if (start > 0) r.ReadLine();   // 丢掉可能被截断的半行
                        sb.Append(r.ReadToEnd());
                    }
                }
            }
            catch (Exception e) { sb.AppendLine("(读日志失败: " + e.Message + ")"); }
        }

        /// <summary>
        /// 抹掉用户名和用户目录。日志里到处是绝对路径，直接发出去等于公开用户名。
        /// 两种形态都要换：完整路径，以及裸的用户名（它会出现在存档名之类的地方）。
        /// </summary>
        private static string Scrub(string s)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    s = s.Replace(home, "<HOME>");
                    s = s.Replace(home.Replace('\\', '/'), "<HOME>");
                }
                string user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                    s = s.Replace(user, "<USER>");
            }
            catch { }
            return s;
        }

        private static void Try(StringBuilder sb, string label, Func<string> f)
        {
            string v;
            try { v = f(); } catch (Exception e) { v = "(读取失败: " + e.Message + ")"; }
            sb.AppendLine(label + v);
        }
    }
}
