using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace DynastyRetinue
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

        /// <summary>
        /// 当前生效的语言（已把 Auto 解析成具体值）。
        ///
        /// ★必须缓存★ 这个属性被 L.T() 每次调用都读一遍，而 IMGUI 面板每帧要跑
        /// 200 多次 L.T()。走 Auto 分支时它会调 GameLocaleIsChinese()，那里面的
        /// AccessTools.TypeByName **要遍历所有已加载程序集的所有类型**（Unity 里几万个），
        /// 而且 `??` 意味着第一次找不到还要再全扫一遍。
        /// 200 次/帧 × 全程序集扫描 = 开面板直接卡死、拖拉条再卡死。
        /// v0.58 把整个面板接进 L.T 之后就是这个下场 —— 之前只有零星几处调用，感觉不出来。
        ///
        /// 游戏语言一局之内不会变，所以缓存到 Reset() 为止就够了。
        /// </summary>
        public static int Current
        {
            get
            {
                int s = Main.Settings != null ? Main.Settings.Language : Auto;
                if (s == ZhCN || s == EnGB) return s;      // 面板显式指定：直接返回，不做任何反射
                if (_autoCache != 0) return _autoCache;    // 0 = 还没算过
                _autoCache = GameLocaleIsChinese() ? ZhCN : EnGB;
                Main.Log("[本地化] 跟随游戏语言 → " + (_autoCache == ZhCN ? "中文" : "English")
                       + "（已缓存；切换语言或改 json 后会重算）");
                return _autoCache;
            }
        }

        /// <summary>Auto 分支的解析结果缓存。0=未算过，其余同 ZhCN/EnGB。</summary>
        private static int _autoCache;

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
                if (_table.TryGetValue(zh, out v) && !string.IsNullOrEmpty(v)) return v;
                // 漏译的记下来（只在开发模式，玩家不付这个开销）。
                // 覆盖率没法靠通读源码保证 —— 只有真跑一遍界面才知道哪句没进表。
                if (Main.DevMode) _missing.Add(zh);
                return zh;
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

        /// <summary>面板改了语言/改了 json 之后强制重读译文表。</summary>
        public static void Reset() { _loadedFor = -1; _table = null; _warned = false; _autoCache = 0; }

        /// <summary>
        /// 切语言。**立刻生效，不用重启** —— 但有三处不会自己跟上，必须在这里推一把：
        ///
        ///   1. **译文表**：换语言要重读（Reset）。
        ///   2. **uGUI 窗口**：招募窗口和船坞窗口的文字是建树时写进 TMP 的，
        ///      不重绘就还是旧语言。开着就刷新，没开着不管。
        ///   3. **卫兵名字**：CustomName 是**存进存档**的，设一次就固定了。
        ///      不重命名的话，切了语言军衔还是中文 —— 而这恰恰是玩家最先看到的东西。
        ///      重命名会把人名也换成对应写法（两个池按下标对齐），
        ///      「近卫长·李霁川」→「Household Sergeant · Li Jichuan」，还是同一个人。
        ///
        /// 面板上的文字不用管：IMGUI 每帧重建，下一帧就是新语言。
        /// 对话选项也不用管：Entry.Text 是委托，LocalizedString 每次取值都查表。
        /// </summary>
        public static void Apply(int language)
        {
            try
            {
                if (Main.Settings != null) Main.Settings.Language = language;
                Reset();

                int n = 0;
                try { n = RetinueTest.RenameAll(); } catch (Exception e) { Main.LogError("[本地化] 重命名失败: " + e.Message); }

                try { if (UI.RetinueUI.IsOpen)  UI.RetinueUI.Refresh();  } catch { }
                try { if (UI.ShipYardUI.IsOpen) UI.ShipYardUI.Refresh(); } catch { }

                Main.Log("[本地化] 已切到 " + (Current == ZhCN ? "中文" : "English")
                       + "　卫兵改名 " + n + " 名　（面板和对话选项下一帧自动跟上，不用重启）");
            }
            catch (Exception e) { Main.LogError("[本地化] 切换失败: " + e); }
        }

        /// <summary>
        /// 开发用：把调用过 T() 但表里没有的中文条目导出来，方便补译。
        /// 只在开发面板里触发，正常游玩不产生开销。
        /// </summary>
        private static readonly HashSet<string> _missing = new HashSet<string>(StringComparer.Ordinal);
        public static void NoteMissing(string zh) { if (!string.IsNullOrEmpty(zh)) _missing.Add(zh); }
        public static IEnumerable<string> Missing { get { return _missing; } }
    }
}
