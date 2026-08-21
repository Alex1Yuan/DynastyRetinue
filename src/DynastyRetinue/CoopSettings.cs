using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace DynastyRetinue
{
    /// <summary>
    /// 把"会影响一次招募结果"的设置打包随指令发走，执行侧临时套用、跑完还原。
    ///
    /// ★为什么必须这么做★
    ///   实机抓到的第一个真实分叉就是这个：两台机器收到**同一条** kgd.recruit，
    ///   生成了**同一个** uid、同一个蓝图、同一个落点、同一个 brain、同一个名字，
    ///   但结果天差地别 ——
    ///
    ///       A（AlignExperience 默认开）  成长 lv0 -> 31   xp=42798   加点 43 个
    ///       B（AlignExperience 被关掉）  成长 lv0 -> 1    xp=0       加点  0 个
    ///
    ///   指令送到了、也一起执行了，可**两边用的规则不一样**，所以做出来的不是同一个人。
    ///   等级、属性、天赋、装备全不同，而这些都进哈希。
    ///
    /// ★为什么不逐个塞成参数★
    ///   招募一条路要读 19 个设置（生成 + 加点 + 发装备三段）。手写参数清单
    ///   一定会漏 —— 今天漏的就是 AlignExperience。而漏一个的后果不是"少个功能"，
    ///   是"看起来一起做了，其实做出两个不同的东西"，比直接报错难查得多。
    ///   用反射按名字批量抓，以后加新开关只要名字进 Keys 就行。
    ///
    /// ★为什么是"临时套用 + 还原"而不是永久同步★
    ///   设置是玩家的个人偏好，不该被联机对端改掉。这里只在**执行那一条指令的
    ///   同步调用期间**借用发起方的值，函数返回前一定还原 —— 玩家面板上看到的
    ///   始终是自己的设置。
    ///
    /// ★为什么这不违反"执行侧不许读本机设置"★
    ///   恰恰相反，这正是那条铁律的实现方式：执行侧读到的**是发起方发来的值**，
    ///   只是借了 Main.Settings 这个容器来传递，免得把 19 个值一路透传到每个函数。
    /// </summary>
    internal static class CoopSettings
    {
        /// <summary>
        /// 一次招募会读到的全部设置字段名。
        ///
        /// 来源是对 RetinueTest / GearTool 里 `Settings.xxx` 的实际扫描，
        /// 外加它们内部那几个组合判据（NoCountCap / NoPfGate / NoLevelCap /
        /// NoEliteCountCap / NoEliteUnlockGate）真正依赖的 Unlock* 原始字段 ——
        /// 组合方法本身不是字段，同步它依赖的输入才有意义。
        /// </summary>
        private static readonly string[] Keys =
        {
            // —— 生成与跟随
            "UnitAssetId", "ArchetypeIndex", "AttachFollow", "IsolateMomentum",
            "GuardsCanShootInMelee", "GuardNamePrefix",
            // —— 等级与经验（今天翻车的就在这一组）
            "AlignExperience", "AutoLevelUp", "ScaleGuardXp", "XpRatio",
            "XpCatchUp", "XpCatchUpMax", "XpCatchUpSpan",
            // —— 名额闸门（NoCountCap / NoPfGate / NoLevelCap 的原始输入）
            "UnlockTierLimits", "UnlockPfGate", "UnlockCountCap", "UnlockLevelCap",
            "RecruitUsePfGate", "RecruitPfPerGuard", "RecruitMaxGuards",
            // —— 精英闸门（NoEliteCountCap / NoEliteUnlockGate 的原始输入）
            "UnlockEliteLimit", "EliteIgnoreUnlock", "EliteLimitPerArchetype", "EliteCanBeDowned",
            // —— 装备
            "EquipGraduationGear", "GearTierOverride",
        };

        /// <summary>把当前设置打成 "名=值" 数组，直接当指令参数发走。</summary>
        public static List<string> Capture()
        {
            var outp = new List<string>(Keys.Length);
            try
            {
                var st = Main.Settings;
                if (st == null) return outp;
                var t = st.GetType();
                foreach (string k in Keys)
                {
                    var f = t.GetField(k, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;                 // 字段改名/删除 —— 跳过，不要崩
                    object v = null;
                    try { v = f.GetValue(st); } catch { continue; }
                    outp.Add(k + "=" + ToText(v));
                }
            }
            catch (Exception e) { Main.LogError("[合作] 打包设置失败：" + e.Message); }
            return outp;
        }

        /// <summary>
        /// 套用发起方的设置，返回一个"还原器"。
        /// **调用方必须用 try/finally 保证还原**，否则玩家的设置会被联机对端改掉。
        /// </summary>
        public static Dictionary<string, object> Apply(string[] args, int from)
        {
            var saved = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                var st = Main.Settings;
                if (st == null || args == null) return saved;
                var t = st.GetType();
                for (int i = from; i < args.Length; i++)
                {
                    string kv = args[i];
                    if (string.IsNullOrEmpty(kv)) continue;
                    int eq = kv.IndexOf('=');                // 只切第一个 = ，值里可以有 =
                    if (eq <= 0) continue;
                    string k = kv.Substring(0, eq);
                    string v = kv.Substring(eq + 1);
                    if (Array.IndexOf(Keys, k) < 0) continue;   // 只认白名单，别让对端写任意字段
                    var f = t.GetField(k, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;
                    object cur;
                    try { cur = f.GetValue(st); } catch { continue; }
                    object parsed;
                    if (!FromText(f.FieldType, v, out parsed)) continue;
                    saved[k] = cur;
                    try { f.SetValue(st, parsed); } catch { saved.Remove(k); }
                }
            }
            catch (Exception e) { Main.LogError("[合作] 套用设置失败：" + e.Message); }
            return saved;
        }

        /// <summary>还原 Apply 之前的值。任何情况下都要调到。</summary>
        public static void Restore(Dictionary<string, object> saved)
        {
            if (saved == null || saved.Count == 0) return;
            try
            {
                var st = Main.Settings;
                if (st == null) return;
                var t = st.GetType();
                foreach (var kv in saved)
                {
                    var f = t.GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                    if (f == null) continue;
                    try { f.SetValue(st, kv.Value); } catch { }
                }
            }
            catch (Exception e) { Main.LogError("[合作] 还原设置失败：" + e.Message); }
        }

        // ==================================================================
        // 跨机器核对设置 —— 把"指纹不一样"变成"具体哪几项不一样"
        //
        // ★为什么光有指纹不够★
        //   船体加成这类**被动规则**是 Harmony 补丁在运行时**持续读设置**算的
        //   （StarshipChargesPatch 里就是 `if (!Settings.ShipExtraShots) return;`
        //   和 `return Settings.ShipCruiserShieldPct;`），
        //   所以随指令发快照救不了它 —— 补丁每次触发都会重新读本机的值。
        //   两边不一致 = 两条船在用不同的护盾/护甲/射击数，海战必然分叉。
        //
        //   指纹能告诉玩家"不一样"，但不能告诉他"哪不一样"。83 个开关靠人肉对
        //   是不现实的。既然指令通道已经实测可用，就用它把双方的设置对发一次，
        //   直接把差异列出来。
        //
        // ★这是只读诊断，不会自动改任何人的设置★
        //   改不改、改哪边，是玩家自己的决定。
        // ==================================================================

        /// <summary>对端发来的设置（字段名 -> 值）。空 = 还没核对过。</summary>
        private static Dictionary<string, string> _remote;
        private static string _remoteWho = "";

        /// <summary>本机全部参与比对的设置（口径和指纹完全一致）。</summary>
        public static List<string> CaptureAll()
        {
            var outp = new List<string>();
            try
            {
                var st = Main.Settings;
                if (st == null) return outp;
                foreach (var f in st.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!CoopState.CountsForFingerprint(f.Name)) continue;
                    object v = null;
                    try { v = f.GetValue(st); } catch { continue; }
                    outp.Add(f.Name + "=" + ToText(v));
                }
                outp.Sort(StringComparer.Ordinal);
            }
            catch (Exception e) { Main.LogError("[合作] 打包全部设置失败：" + e.Message); }
            return outp;
        }

        /// <summary>收下对端发来的设置。</summary>
        public static void ReceiveRemote(string who, string[] args, int from)
        {
            // ★自己发的那份要丢掉★ 指令在两台机器上都执行，发起方也会收到自己那份；
            //   拿自己和自己比永远是"完全一致"，真正的差异只有对端看得到。
            string me = CoopState.LocalUserId;
            if (!string.IsNullOrEmpty(me) && string.Equals(who, me, StringComparison.Ordinal))
            {
                Main.Log("[合作] 已发出本机设置，等待对方那边显示差异。");
                return;
            }

            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = from; i < args.Length; i++)
            {
                int eq = args[i].IndexOf('=');
                if (eq > 0) d[args[i].Substring(0, eq)] = args[i].Substring(eq + 1);
            }
            _remote = d; _remoteWho = string.IsNullOrEmpty(who) ? "?" : who;
            Main.Log("[合作] 收到对端设置 " + d.Count + " 项，来自 " + _remoteWho + "。" + DiffText());
        }

        /// <summary>本机和对端的差异，给面板和日志用。没核对过返回空串。</summary>
        public static string DiffText()
        {
            if (_remote == null) return "";
            try
            {
                var mine = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in CaptureAll())
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0) mine[kv.Substring(0, eq)] = kv.Substring(eq + 1);
                }
                var sb = new System.Text.StringBuilder();
                int n = 0;
                var keys = new List<string>(mine.Keys);
                foreach (var k in _remote.Keys) if (!mine.ContainsKey(k)) keys.Add(k);
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys)
                {
                    string a, b;
                    mine.TryGetValue(k, out a); _remote.TryGetValue(k, out b);
                    if (a == b) continue;
                    n++;
                    if (n <= 12)
                        sb.Append(Environment.NewLine).Append("    ").Append(k)
                          .Append("：你=").Append(a ?? "(无)")
                          .Append("　对方=").Append(b ?? "(无)");
                }
                if (n == 0) return L.T("　—— 双方设置完全一致。");
                string more = (n > 12) ? L.F("（另有 {0} 项未列出）", n - 12) : "";
                return L.F("　★{0} 项不一致{1}：", n, more) + sb;
            }
            catch { return ""; }
        }

        // ------------------------------------------------------------------
        // ★文本化一律用 InvariantCulture★
        //   两台机器的系统区域设置可能不同。中文/德文区域下 float 的小数点是逗号，
        //   "0.5" 会解析失败或变成 5 —— 那又是一处只在特定机器上出现的分叉。
        private static string ToText(object v)
        {
            if (v == null) return "";
            if (v is bool)  return ((bool)v) ? "1" : "0";
            if (v is int)   return ((int)v).ToString(CultureInfo.InvariantCulture);
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            return v.ToString();
        }

        private static bool FromText(Type ft, string s, out object v)
        {
            v = null;
            try
            {
                if (ft == typeof(bool))   { v = (s == "1" || s == "True" || s == "true"); return true; }
                if (ft == typeof(int))    { int i;   if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return false; v = i; return true; }
                if (ft == typeof(float))  { float f; if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return false; v = f; return true; }
                if (ft == typeof(string)) { v = s; return true; }
            }
            catch { }
            return false;
        }
    }
}
