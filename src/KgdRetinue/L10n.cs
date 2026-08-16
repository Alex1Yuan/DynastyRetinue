using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace KgdRetinue
{
    /// <summary>
    /// 界面文本本地化。
    ///
    /// ================= 三个设计选择，都是为了少出错 =================
    ///
    /// ★1. 用中文原文当 key，不发明键名 ★
    ///   调用处只是 "招募" → L.T("招募")，纯机械改动，不用维护一张
    ///   "键名 ↔ 中文" 的对照表（那张表本身就是新的错误来源：改了文案忘了改键、
    ///   或者键名拼错导致界面上出现 ##missing##）。
    ///   查不到译文就**原样返回中文** —— 最坏情况是这一条没译，而不是空白或报错。
    ///
    /// ★2. 译文放外部 json，不编进 DLL ★
    ///   l10n_en.json 和 archetypes.json 并排。热加载，改完不用重启、不用重编译，
    ///   别人也能提交修正。文件缺失/损坏 = 全部回落中文，不影响功能。
    ///
    /// ★3. 语言默认跟随游戏，不逼玩家再设一次 ★
    ///   LocalizationManager.Instance.CurrentLocale（Locale.zhCN / enGB / ruRU …）。
    ///   装英文版游戏的人开箱即英文。设置里留一个覆盖项，应付
    ///   "游戏英文但想看中文"这种真实存在的情况。
    ///   ★用反射取★ LocalizationManager 在 LocalizationShared.dll 里，csproj 没引用，
    ///   直接 typeof 会 CS0246 —— 和 RecruitDialog 里那个 TryGetText 补丁同一个坑。
    ///
    /// ================= 带参数的文案 =================
    /// 不要拼接翻译片段（"需要 " + n + " 废料" 拆成两段译，语序一变就散架）。
    /// 用 L.F("需要 {0} 废料", n)，把**整句**交给译者，占位符随语序移动。
    /// </summary>
    public static class L
    {
        /// <summary>Auto=0 跟随游戏；1=中文；2=English。</summary>
        public const int Auto = 0, ZhCN = 1, EnGB = 2;

        private static Dictionary<string, string> _table;
        private static int _loadedFor = -1;
        private static bool _warned;

        /// <summary>当前生效的语言（已把 Auto 解析成具体值）。</summary>
        public static int Current
        {
            get
            {
                int s = Main.Settings != null ? Main.Settings.Language : Auto;
                if (s == ZhCN || s == EnGB) return s;
                return GameLocaleIsChinese() ? ZhCN : EnGB;
            }
        }

        /// <summary>
        /// 游戏当前语言是不是中文。读不到就当中文 ——
        /// 这个 mod 的原文是中文，读不到时回落到原文比回落到半吊子翻译安全。
        /// </summary>
        private static bool GameLocaleIsChinese()
        {
            try
            {
                var t = AccessTools.TypeByName("Kingmaker.Localization.LocalizationManager")
                     ?? AccessTools.TypeByName("LocalizationManager");
                if (t == null) return true;
                var instProp = AccessTools.Property(t, "Instance");
                object inst = instProp != null ? instProp.GetValue(null, null) : null;
                if (inst == null)
                {
                    var f = AccessTools.Field(t, "Instance");
                    inst = f != null ? f.GetValue(null) : null;
                }
                if (inst == null) return true;
                var locProp = AccessTools.Property(inst.GetType(), "CurrentLocale");
                object loc = locProp != null ? locProp.GetValue(inst, null) : null;
                if (loc == null) return true;
                string s = loc.ToString();
                // zhCN / zhTW 都算中文；其余（enGB/ruRU/deDE/frFR/…）走英文表
                return s.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        /// <summary>翻译一条。查不到、或当前是中文，都原样返回。</summary>
        public static string T(string zh)
        {
            if (string.IsNullOrEmpty(zh)) return zh;
            try
            {
                int cur = Current;
                if (cur == ZhCN) return zh;
                EnsureTable(cur);
                if (_table == null) return zh;
                string v;
                return _table.TryGetValue(zh, out v) && !string.IsNullOrEmpty(v) ? v : zh;
            }
            catch { return zh; }
        }

        /// <summary>
        /// 带占位符的文案。整句交给译者，{0} 随语序移动。
        /// 格式化失败（译文里占位符写错了）就退回中文原句格式化 —— 宁可没译好也不能崩。
        /// </summary>
        public static string F(string zhFormat, params object[] args)
        {
            string fmt = T(zhFormat);
            try { return string.Format(fmt, args); }
            catch
            {
                try { return string.Format(zhFormat, args); }
                catch { return zhFormat; }
            }
        }

        private static void EnsureTable(int locale)
        {
            if (_loadedFor == locale && _table != null) return;
            _loadedFor = locale;
            _table = null;
            if (locale == ZhCN) return;

            try
            {
                string dir = Main.ModEntry != null ? Main.ModEntry.Path : null;
                if (string.IsNullOrEmpty(dir)) return;
                string path = Path.Combine(dir, "l10n_en.json");
                if (!File.Exists(path))
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Main.LogError("[本地化] 找不到 " + path + " —— 界面将保持中文。"
                                    + "这不影响任何功能，只是没有译文。");
                    }
                    return;
                }
                var raw = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var obj = Newtonsoft.Json.Linq.JObject.Parse(raw);
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in obj)
                {
                    var val = kv.Value != null ? kv.Value.ToString() : null;
                    if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(val)) d[kv.Key] = val;
                }
                _table = d;
                Main.Log("[本地化] 已载入 l10n_en.json，共 " + d.Count + " 条。");
            }
            catch (Exception e)
            {
                Main.LogError("[本地化] 载入译文失败，界面保持中文: " + e.Message);
                _table = null;
            }
        }

        /// <summary>面板改了语言/改了 json 之后强制重读。</summary>
        public static void Reset() { _loadedFor = -1; _table = null; _warned = false; }

        /// <summary>
        /// 开发用：把调用过 T() 但表里没有的中文条目导出来，方便补译。
        /// 只在开发面板里触发，正常游玩不产生开销。
        /// </summary>
        private static readonly HashSet<string> _missing = new HashSet<string>(StringComparer.Ordinal);
        public static void NoteMissing(string zh) { if (!string.IsNullOrEmpty(zh)) _missing.Add(zh); }
        public static IEnumerable<string> Missing { get { return _missing; } }
    }
}
