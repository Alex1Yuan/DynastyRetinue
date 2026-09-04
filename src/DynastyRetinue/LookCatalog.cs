using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace DynastyRetinue
{
    /// <summary>
    /// 一套外观风格。两种做法二选一，`Unit` 优先。
    /// </summary>
    internal sealed class LookDef
    {
        public string Id;
        public string Name;
        public string NameEn;

        /// <summary>借模型：BlueprintUnit guid。为空表示不用这条路。</summary>
        public string Unit;
        /// <summary>按分型名覆盖 Unit。键是 archetypes.json 里的 name。</summary>
        public Dictionary<string, string> UnitByArchetype;

        /// <summary>拼 EE：KingmakerEquipmentEntity 蓝图 guid 列表。</summary>
        public string[] Parts;

        /// <summary>
        /// 只对这些分型开放（archetypes.json 里的 name）。为空 = 不限。
        ///
        /// ★为什么需要★ 机械教是第六条线，当前明确保留单位原版红袍/机械触须，
        ///   所以卡斯金/克里格等前五条线风格不该出现在它的格子里。
        ///   这个字段仍是通用过滤器：以后若有仅限某分型的风格，不必在 UI 写死名单。
        /// </summary>
        public string[] OnlyArchetypes;

        /// <summary>对这些分型隐藏。与 OnlyArchetypes 互补，两个都填时 Only 先判。</summary>
        public string[] NotArchetypes;

        /// <summary>
        /// 只对这些**精英单位**生效（BlueprintUnit guid，即 archetypes.json 里精英的 unit）。
        /// 为空 = 不按精英收窄。
        ///
        /// ★为什么需要比分型更细的一层★
        ///   分配表是「分型 × 列」，而**四个机械教精英共用「精英」那一列** ——
        ///   给这列配技术神甫娃娃，会把锈行者和电僧一起改掉，而它们现在的外观是对的
        ///   （自带模型 + 自带武器的动作集）。
        ///   只有教条贤者需要走娃娃（为了长出机械触须），所以这层收窄到具体单位。
        ///
        /// ★不匹配时是「回落」不是「报错」★ LookFor 返回 null = 跟随装备 =
        ///   完全不干预，也就是这些单位保持改动之前的样子。
        /// </summary>
        public string[] OnlyElites;

        /// <summary>这套风格允不允许用在某个具体单位上（精英收窄那一层）。</summary>
        public bool AllowedForUnit(string unitGuid)
        {
            if (OnlyElites == null || OnlyElites.Length == 0) return true;
            if (string.IsNullOrEmpty(unitGuid)) return false;
            for (int i = 0; i < OnlyElites.Length; i++)
                if (string.Equals(OnlyElites[i], unitGuid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>这套风格允不允许用在某个分型上。archName 为空时一律允许（拿不到分型就别拦）。</summary>
        public bool AllowedFor(string archName)
        {
            if (string.IsNullOrEmpty(archName)) return true;
            if (OnlyArchetypes != null && OnlyArchetypes.Length > 0)
            {
                for (int i = 0; i < OnlyArchetypes.Length; i++)
                    if (string.Equals(OnlyArchetypes[i], archName, StringComparison.Ordinal)) return true;
                return false;
            }
            if (NotArchetypes != null)
                for (int i = 0; i < NotArchetypes.Length; i++)
                    if (string.Equals(NotArchetypes[i], archName, StringComparison.Ordinal)) return false;
            return true;
        }

        public bool IsBorrow { get { return !string.IsNullOrEmpty(Unit) || (UnitByArchetype != null && UnitByArchetype.Count > 0); } }
        public bool IsCompose { get { return Parts != null && Parts.Length > 0; } }

        /// <summary>这个分型该借哪个单位。没配就回落到通用的 Unit。</summary>
        public string UnitFor(string archetypeName)
        {
            if (UnitByArchetype != null && !string.IsNullOrEmpty(archetypeName))
            {
                string hit;
                if (UnitByArchetype.TryGetValue(archetypeName, out hit) && !string.IsNullOrEmpty(hit)) return hit;
            }
            return Unit;
        }

        public string Display() { return L.T(Name ?? Id ?? "?"); }
    }

    /// <summary>
    /// 外观风格清单，从 looks.json 读。
    ///
    /// ★为什么进配置文件而不是写死★
    ///   风格配方（哪几件、借哪个单位）**只有在游戏里看才知道好不好** —— 会不会穿模、
    ///   斗篷和背包打不打架、配色对不对。写死在 C# 里意味着每调一件就要重编译重启；
    ///   放 JSON 里就是改一行存盘重进。玩家和其他 modder 也能加自己的。
    ///
    /// ★缺文件不是错误★
    ///   没有 looks.json 就只剩"跟随装备"一个选项 —— 也就是原版行为，
    ///   mod 其余功能完全不受影响。所以这里不抛、不拦，只记一行。
    /// </summary>
    internal static class LookCatalog
    {
        /// <summary>"跟随装备" —— 不做任何外观干预。矩阵里的空值就是它。</summary>
        public const string FollowGear = "";

        private static LookDef[] _all = new LookDef[0];
        private static bool _loaded;

        private static string Path
        {
            get { return System.IO.Path.Combine(Main.ModEntry != null ? Main.ModEntry.Path : ".", "looks.json"); }
        }

        /// <summary>
        /// 把风格按「作用域」分组，界面据此画成互相独立的几张表。
        ///
        /// ★为什么要分表★ 画笔是全局的一支，而作用域限制让一部分格子刷不上 ——
        ///   玩家看到选项、点了没反应，只能猜。分表之后每张表的调色板里
        ///   **只有这张表刷得动的风格**，不存在"点了没用"。
        ///
        /// 分组依据是数据本身（onlyArchetypes），不硬编码分型名：
        ///   · 没有 onlyArchetypes 的 → 通用组（作用于所有不排斥它的分型）
        ///   · 有 onlyArchetypes 的 → 按作用域字符串归为一组
        /// 返回的每一项是 (这组的分型下标集合, 这组能用的风格)。
        /// </summary>
        public static List<KeyValuePair<List<int>, LookDef[]>> Groups()
        {
            EnsureLoaded();
            var res = new List<KeyValuePair<List<int>, LookDef[]>>();
            var all = Archetypes.All;
            if (all == null || _all == null) return res;

            // 分型下标 -> 它能用的风格集合的签名，签名相同的归一张表
            var bySig = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var sigOrder = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                string name = all[i] != null ? all[i].Name : null;
                var ids = new List<string>();
                for (int k = 0; k < _all.Length; k++)
                    if (_all[k] != null && _all[k].AllowedFor(name)) ids.Add(_all[k].Id);
                string sig = string.Join("", ids.ToArray());
                List<int> rows;
                if (!bySig.TryGetValue(sig, out rows)) { rows = new List<int>(); bySig[sig] = rows; sigOrder.Add(sig); }
                rows.Add(i);
            }

            for (int s = 0; s < sigOrder.Count; s++)
            {
                var rows = bySig[sigOrder[s]];
                string name = all[rows[0]] != null ? all[rows[0]].Name : null;
                var looks = new List<LookDef>();
                for (int k = 0; k < _all.Length; k++)
                    if (_all[k] != null && _all[k].AllowedFor(name)) looks.Add(_all[k]);
                res.Add(new KeyValuePair<List<int>, LookDef[]>(rows, looks.ToArray()));
            }
            return res;
        }

        public static LookDef[] All { get { EnsureLoaded(); return _all; } }

        private static string[] ReadNames(Newtonsoft.Json.Linq.JArray a)
        {
            if (a == null) return null;
            var l = new List<string>(a.Count);
            foreach (var t in a) { var s = (string)t; if (!string.IsNullOrEmpty(s)) l.Add(s); }
            return l.Count > 0 ? l.ToArray() : null;
        }

        /// <summary>
        /// 某个分型能用的风格。给矩阵界面列选项用 —— 不该出现的就别列出来，
        /// 靠玩家自觉不选错是不行的（机械教格子里列着卡斯金，选了会得到一个卡迪亚人）。
        /// </summary>
        public static LookDef[] ForArchetype(string archName)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(archName) || _all == null) return _all;
            var l = new List<LookDef>(_all.Length);
            for (int i = 0; i < _all.Length; i++)
                if (_all[i] != null && _all[i].AllowedFor(archName)) l.Add(_all[i]);
            return l.ToArray();
        }

        public static LookDef Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureLoaded();
            for (int i = 0; i < _all.Length; i++)
                if (string.Equals(_all[i].Id, id, StringComparison.OrdinalIgnoreCase)) return _all[i];
            return null;
        }

        public static void Invalidate() { _loaded = false; _all = new LookDef[0]; }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            var list = new List<LookDef>();
            try
            {
                string p = Path;
                if (!System.IO.File.Exists(p))
                {
                    Main.Log("[外观] 没有 looks.json，只提供「跟随装备」一个选项。");
                    _all = list.ToArray();
                    return;
                }

                var root = JObject.Parse(System.IO.File.ReadAllText(p, System.Text.Encoding.UTF8));
                var arr = root["looks"] as JArray;
                if (arr == null) { Main.LogError("[外观] looks.json 里没有 looks 数组。"); _all = list.ToArray(); return; }

                foreach (var tok in arr)
                {
                    var o = tok as JObject;
                    if (o == null) continue;
                    var d = new LookDef
                    {
                        Id     = (string)o["id"],
                        Name   = (string)o["name"],
                        NameEn = (string)o["name_en"],
                        Unit   = (string)o["unit"],
                    };
                    if (string.IsNullOrEmpty(d.Id)) { Main.LogError("[外观] looks.json 有一条没有 id，已跳过。"); continue; }

                    var parts = o["parts"] as JArray;
                    if (parts != null)
                    {
                        var ps = new List<string>(parts.Count);
                        foreach (var t in parts) { var g = (string)t; if (!string.IsNullOrEmpty(g)) ps.Add(g); }
                        d.Parts = ps.ToArray();
                    }

                    var map = o["unitByArchetype"] as JObject;
                    if (map != null)
                    {
                        d.UnitByArchetype = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var kv in map)
                        {
                            string g = (string)kv.Value;
                            if (!string.IsNullOrEmpty(g)) d.UnitByArchetype[kv.Key] = g;
                        }
                    }

                    d.OnlyArchetypes = ReadNames(o["onlyArchetypes"] as JArray);
                    d.NotArchetypes  = ReadNames(o["notArchetypes"] as JArray);
                    d.OnlyElites     = ReadNames(o["onlyElites"] as JArray);

                    // 两种做法都没有 = 这条配不出任何东西，收进来只会让玩家选了没反应
                    if (!d.IsBorrow && !d.IsCompose)
                    { Main.LogError("[外观] 风格「" + d.Id + "」既没有 unit 也没有 parts，已跳过。"); continue; }

                    list.Add(d);
                }

                _all = list.ToArray();
                Main.Log("[外观] 载入 " + _all.Length + " 套风格：" + Describe());
            }
            catch (Exception e)
            {
                Main.LogError("[外观] 读 looks.json 失败：" + e.Message + "（只剩「跟随装备」）");
                _all = new LookDef[0];
            }
        }

        private static string Describe()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _all.Length; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(_all[i].Id).Append(_all[i].IsBorrow ? "(借模型)" : "(拼部件)");
            }
            return sb.ToString();
        }
    }
}
