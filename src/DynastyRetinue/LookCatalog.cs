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

        public static LookDef[] All { get { EnsureLoaded(); return _all; } }

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
