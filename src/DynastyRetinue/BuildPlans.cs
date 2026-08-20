using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DynastyRetinue
{
    /// <summary>
    /// 对接 RTAutoBuilder 的加点方案。
    ///
    /// 玩家已经在那个 mod 里为主角和每个队友存了成套的"毕业方案"，
    /// 结构是 careerPathGuid -> [{FeatureGroup, Rank, Selection}]。
    /// 我们的 ApplyChain 原来是"随便选第一个可选项"，天赋完全没有配置意义；
    /// 接上之后卫兵就按玩家自己调好的路线加点。
    ///
    /// 匹配策略刻意**不依赖 FeatureGroup 的字符串↔枚举映射** ——
    /// 只用 (career path, rank) 定位到一组候选 Selection GUID，
    /// 再看哪个出现在本次 SelectionStateFeature.Items 里且可选。
    /// 同一 rank 有多个组（Attribute / Talent / Skill...）时天然各取所需，
    /// 少一次可能出错的枚举对齐。
    ///
    /// 只读，不修改 RTAutoBuilder 的任何文件。
    /// </summary>
    public static class BuildPlans
    {
        public sealed class Plan
        {
            /// <summary>稳定 id（我们自己的 plans.json 里的 "id"）。archetypes.json 用它引用，
            /// 不再靠名字前缀/子串去猜 —— 那套模糊匹配曾经让 4 个精英命中 0。</summary>
            public string Id;
            public string Name;
            /// <summary>方案指定的背景世界与起源（都是 BlueprintFeature 的 GUID）。
            /// 起源会解锁专属天赋 —— 起源不对的话，方案里那些门控选项根本不在
            /// SelectionStateFeature.Items 里，只能回退。所以升级前要先把它们授予。</summary>
            public string Homeworld;
            public string Origin;
            public string Comment;
            public string First;      // T1 career path guid
            public string Second;     // T2 career path guid
            // careerPathGuid -> rank -> 该 rank 下所有 Selection guid
            public Dictionary<string, Dictionary<int, List<string>>> Sel
                = new Dictionary<string, Dictionary<int, List<string>>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>一条选择记录的原样保留（组合方案要靠 Group 分桶）。</summary>
            public sealed class Ent
            {
                public int Rank;
                public string Guid;
                public string Group;   // Talent / Skill / Attribute / FirstCareerTalent / SecondCareerAbility ...
                /// <summary>该条目所属的 Selection 蓝图 GUID。Group 为空时用它在运行时
                /// 读 BlueprintSelectionFeature.Group —— 存档抽出来的方案没有组名字符串
                ///（伊莉耶特 36 条 Ascension 全空），只能靠这个补。</summary>
                public string SelBp;
            }
            /// <summary>careerPathGuid -> 原始条目（保序）。Sel 是它的 (rank -> guid) 索引。</summary>
            public Dictionary<string, List<Ent>> Raw
                = new Dictionary<string, List<Ent>>(StringComparer.OrdinalIgnoreCase);

            public string Display { get { return string.IsNullOrEmpty(Comment) ? Name : Name + " · " + Comment; } }

            /// <summary>取该 path+rank 下的候选 Selection GUID 列表。</summary>
            public List<string> Candidates(string pathGuid, int rank)
            {
                Dictionary<int, List<string>> byRank;
                if (pathGuid == null || !Sel.TryGetValue(pathGuid, out byRank)) return null;
                List<string> list;
                return byRank.TryGetValue(rank, out list) ? list : null;
            }
        }

        private static List<Plan> _plans;
        private static bool _tried;

        /// <summary>我们自己的方案文件 —— mod 独立发布、开新档就能用的关键。
        /// 由 ref/rt_probe/mkplans.py 在开发期从作者的 55 级存档 + RTAutoBuilder 方案离线抽取。
        /// 运行时只读它，不读任何存档，也不要求玩家装 RTAutoBuilder。</summary>
        public static string OwnPath
        {
            get
            {
                var mine = Main.ModEntry != null ? Main.ModEntry.Path : ".";
                return System.IO.Path.Combine(mine, "plans.json");
            }
        }

        public static string SourcePath
        {
            get
            {
                // ModEntry.Path 带尾部分隔符，Directory.GetParent 会返回它**自己**而不是上一级
                // （v0.2.3 实测：算出了 ...\UnityModManager\DynastyRetinue\RTAutoBuilder\...，多一层）
                var mine = Main.ModEntry != null ? Main.ModEntry.Path : ".";
                mine = mine.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                var umm = System.IO.Directory.GetParent(mine);
                var root = umm != null ? umm.FullName : ".";
                return System.IO.Path.Combine(root, "RTAutoBuilder", "AutoBuilderSettings.json");
            }
        }

        public static List<Plan> All
        {
            get
            {
                if (!_tried) { _tried = true; _plans = Load(); }
                return _plans ?? new List<Plan>();
            }
        }

        public static void Reload()
        {
            _tried = false; _plans = null;
            var a = All;
            if (a.Count == 0) Main.Log("一套方案都没载入（自带: " + OwnPath + "）");
            else
            {
                Main.Log("已载入 " + a.Count + " 套加点方案（自带 plans.json + 可选的 RTAutoBuilder）:");
                for (int i = 0; i < a.Count; i++)
                    Main.Log("  [" + i + "] " + (string.IsNullOrEmpty(a[i].Id) ? "(无id)" : a[i].Id)
                             + "  " + a[i].Display);
            }
        }

        public static Plan Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var a = All;
            // ⓪ 稳定 id ——我们自己的 plans.json 用这个引用，优先级最高
            foreach (var p in a) if (string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase)) return p;
            // ① 精确匹配显示名
            foreach (var p in a) if (string.Equals(p.Display, name, StringComparison.Ordinal)) return p;
            // ② 前缀匹配 —— 主控那几套方案的 BuildComment 很长
            //    （"国教士兵首席连射 毕业套装 枪手头盔/呢喃低语，改装工匠护甲……"），
            //    模板里写的是简称，精确匹配对不上，实测四个精英因此命中 0、全部回退。
            foreach (var p in a)
                if (p.Display != null && p.Display.StartsWith(name, StringComparison.Ordinal)) return p;
            // ③ 子串匹配（两个方向都试）——简称里可能带了 "RogueTrader · " 前缀
            foreach (var p in a)
                if (p.Display != null && p.Display.IndexOf(name, StringComparison.Ordinal) >= 0) return p;
            foreach (var p in a)
            {
                if (p.Display == null) continue;
                // 把 "作者 · " 前缀去掉再比，剩下的部分只要能互相包含就算命中
                int dot = name.IndexOf('·');
                var shortName = dot >= 0 ? name.Substring(dot + 1).Trim() : name;
                if (shortName.Length >= 3 && p.Display.IndexOf(shortName, StringComparison.Ordinal) >= 0) return p;
            }
            // ④ 退回 UnitId / 下标
            foreach (var p in a) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            int idx;
            if (int.TryParse(name, out idx) && idx >= 0 && idx < a.Count) return a[idx];
            return null;
        }

        /// <summary>
        /// 按段拼一份方案出来。
        ///
        /// 为什么需要：有些路线玩家手上没有完整点法（比如「士兵赏金」攻略只给了要点），
        /// 但组成它的两段在别的方案里都有现成的 —— Soldier 段可以抄国教士兵，
        /// Hunter 段可以抄伊莉耶特（全 RTAutoBuilder 里只有她那套有 Hunter 数据）。
        ///
        /// Ascension 段要按 FeatureGroup 分桶取：它的 FirstCareerTalent / SecondCareerTalent
        /// 是**相对职业**的，伊莉耶特的 First 是 Adept（r1 就是 Adept_TideOfExcellence），
        /// 直接照抄的话士兵起手根本点不出来。
        ///
        /// segments: pathGuid -> { "FirstCareer"/"SecondCareer"/"default" -> 方案名 }。
        ///           只有一个 "default" 就是整段照抄。
        /// 背景世界/起源取 chain[0] 那段的来源方案（T1 决定出身）。
        /// </summary>
        public static Plan Compose(string name, string[] chain,
                                   Dictionary<string, Dictionary<string, string[]>> segments,
                                   string[] exclude)
        {
            if (chain == null || chain.Length == 0 || segments == null) return null;
            var ex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exclude != null) foreach (var g in exclude) if (!string.IsNullOrEmpty(g)) ex.Add(g);

            var outPlan = new Plan { Name = name, Comment = "合成" };
            outPlan.First  = chain.Length > 0 ? chain[0] : null;
            outPlan.Second = chain.Length > 1 ? chain[1] : null;

            int dropped = 0, taken = 0;
            for (int i = 0; i < chain.Length; i++)
            {
                string path = chain[i];
                Dictionary<string, string[]> route;
                if (!segments.TryGetValue(path, out route) || route == null) continue;

                var byRank = new Dictionary<int, List<string>>();
                var raw = new List<Plan.Ent>();
                // 该段可能引用多套方案，逐套把属于自己桶的条目挑出来。
                // 一个桶给多个方案名时候选合并 —— PickOne 会逐个试，谁真能选上用谁。
                foreach (var kv in route)
                {
                    if (kv.Value == null) continue;
                    foreach (var planName in kv.Value)
                    {
                        var src = Get(planName);
                        if (src == null) { Main.LogError("    合成方案: 找不到源方案「" + planName + "」"); continue; }
                        List<Plan.Ent> ents;
                        if (!src.Raw.TryGetValue(path, out ents) || ents == null)
                        {
                            Main.LogError("    合成方案: 「" + src.Display + "」里没有 " + Archetypes.PathName(path) + " 段");
                            continue;
                        }
                        foreach (var e in ents)
                        {
                            if (BucketOf(GroupOf(e)) != kv.Key) continue;
                            if (ex.Contains(e.Guid)) { dropped++; continue; }
                            List<string> l;
                            if (!byRank.TryGetValue(e.Rank, out l)) { l = new List<string>(); byRank[e.Rank] = l; }
                            if (!l.Contains(e.Guid)) l.Add(e.Guid);
                            raw.Add(e);
                            taken++;
                        }
                        // 背景世界/起源跟着 T1 的第一个源走
                        if (i == 0 && string.IsNullOrEmpty(outPlan.Homeworld))
                        { outPlan.Homeworld = src.Homeworld; outPlan.Origin = src.Origin; }
                    }
                }
                outPlan.Sel[path] = byRank;
                outPlan.Raw[path] = raw;
            }
            Main.Log("    合成方案「" + name + "」: 取 " + taken + " 条，按 excludeFeatures 剔除 " + dropped + " 条");
            return outPlan;
        }

        /// <summary>把 FeatureGroup 归到 FirstCareer / SecondCareer / FirstOrSecondCareer / default 四个桶。
        /// FirstOrSecondCareer 单独一个桶 —— 它两边都可能合法，配置里通常会给它两个源，
        /// 候选合并后 PickOne 自然挑到能选上的那个。之前把它并进 default，
        /// 结果赏金·猎首拿到的全是 Veteran_* 天赋（士兵源的 T2），三条全点空。</summary>
        /// <summary>取一条的 FeatureGroup 名。方案里没记（存档抽出来的都没有）就
        /// 用 Selection 蓝图现查 —— BlueprintSelectionFeature.Group 是权威来源。
        /// 之前靠 RTAutoBuilder 当查表补，伊莉耶特那 36 条 Ascension 全查不到，
        /// 导致按段合成时她那一份完全进不了 FirstCareer/SecondCareer 桶。
        /// 查过的缓存起来，一次合成会问很多遍。</summary>
        private static readonly Dictionary<string, string> _grpCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string GroupOf(Plan.Ent e)
        {
            if (e == null) return "";
            if (!string.IsNullOrEmpty(e.Group)) return e.Group;
            if (string.IsNullOrEmpty(e.SelBp)) return "";
            string cached;
            if (_grpCache.TryGetValue(e.SelBp, out cached)) return cached;
            string g = "";
            try
            {
                var bp = Kingmaker.Blueprints.ResourcesLibrary.TryGetBlueprint<
                    Kingmaker.UnitLogic.Levelup.Selections.Feature.BlueprintSelectionFeature>(e.SelBp);
                if (bp != null) g = bp.Group.ToString();
            }
            catch { }
            _grpCache[e.SelBp] = g;
            return g;
        }

        /// <summary>把 FeatureGroup 归到 FirstCareer / SecondCareer / FirstOrSecondCareer / default 四个桶。</summary>
        private static string BucketOf(string group)
        {
            if (string.IsNullOrEmpty(group)) return "default";
            if (group.StartsWith("FirstOrSecond", StringComparison.Ordinal)) return "FirstOrSecondCareer";
            if (group.StartsWith("FirstCareer", StringComparison.Ordinal))  return "FirstCareer";
            if (group.StartsWith("SecondCareer", StringComparison.Ordinal)) return "SecondCareer";
            return "default";
        }

        private static List<Plan> Load()
        {
            var result = LoadOwn() ?? new List<Plan>();
            // RTAutoBuilder 是**可选**补充源：作者自己调方案时方便，别人没装也照样跑。
            var extra = LoadAutoBuilder();
            if (extra != null)
                foreach (var p in extra)
                {
                    bool dup = false;
                    foreach (var q in result)
                        if (string.Equals(q.Comment, p.Comment, StringComparison.Ordinal)) { dup = true; break; }
                    if (!dup) result.Add(p);
                }
            return result;
        }

        /// <summary>读 mod 自带的 plans.json。</summary>
        private static List<Plan> LoadOwn()
        {
            try
            {
                var path = OwnPath;
                if (!System.IO.File.Exists(path))
                {
                    Main.LogError("找不到自带方案 plans.json（" + path + "）—— 卫兵会全程回退到「第一个可选项」。");
                    return null;
                }
                var root = JObject.Parse(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                var arr = root["plans"] as JArray;
                if (arr == null) return null;
                var result = new List<Plan>();
                foreach (var b in arr)
                {
                    try
                    {
                        var p = new Plan
                        {
                            Id        = (string)b["id"],
                            Name      = (string)b["name"],
                            Comment   = (string)b["source"],
                            First     = (string)b["first"],
                            Second    = (string)b["second"],
                            Homeworld = (string)b["homeworld"],
                            Origin    = (string)b["origin"],
                        };
                        var sel = b["sel"] as JObject;
                        var grp = b["grp"] as JObject;
                        var selbp = b["selbp"] as JObject;
                        if (sel != null)
                            foreach (var kv in sel)
                            {
                                var ranks = kv.Value as JObject;
                                if (ranks == null) continue;
                                var byRank = new Dictionary<int, List<string>>();
                                var raw = new List<Plan.Ent>();
                                JObject grpRanks = null, sbRanks = null;
                                if (grp != null) grpRanks = grp[kv.Key] as JObject;
                                if (selbp != null) sbRanks = selbp[kv.Key] as JObject;
                                foreach (var rk in ranks)
                                {
                                    int rank; if (!int.TryParse(rk.Key, out rank)) continue;
                                    var list = rk.Value as JArray;
                                    if (list == null) continue;
                                    JArray gl = null, sb = null;
                                    if (grpRanks != null) gl = grpRanks[rk.Key] as JArray;
                                    if (sbRanks != null) sb = sbRanks[rk.Key] as JArray;
                                    var l = new List<string>();
                                    for (int i = 0; i < list.Count; i++)
                                    {
                                        var g = (string)list[i];
                                        if (string.IsNullOrEmpty(g)) continue;
                                        l.Add(g);
                                        raw.Add(new Plan.Ent
                                        {
                                            Rank = rank, Guid = g,
                                            Group = (gl != null && i < gl.Count) ? (string)gl[i] : null,
                                            SelBp = (sb != null && i < sb.Count) ? (string)sb[i] : null
                                        });
                                    }
                                    if (l.Count > 0) byRank[rank] = l;
                                }
                                p.Sel[kv.Key] = byRank;
                                p.Raw[kv.Key] = raw;
                            }
                        if (string.IsNullOrEmpty(p.Name)) p.Name = p.Id ?? "(无名)";
                        result.Add(p);
                    }
                    catch (Exception e) { Main.LogError("跳过一套解析失败的自带方案: " + e.Message); }
                }
                return result;
            }
            catch (Exception e) { Main.LogError("读取 plans.json 失败: " + e.Message); return null; }
        }

        internal static List<Plan> LoadAutoBuilder()
        {
            try
            {
                var path = SourcePath;
                if (!System.IO.File.Exists(path)) return null;

                var root = JObject.Parse(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                var arr = root["BuildPlans"] as JArray;
                if (arr == null) return null;

                var result = new List<Plan>();
                foreach (var b in arr)
                {
                    try
                    {
                        var p = new Plan
                        {
                            Name    = (string)b["UnitId"],
                            Comment = (string)b["BuildComment"],
                            First   = (string)b["FirstArchetype"],
                            Second  = (string)b["SecondArchetype"],
                            Homeworld = (string)b["Homeworld"],
                            Origin    = (string)b["Origin"],
                        };
                        if (string.IsNullOrEmpty(p.Name)) p.Name = "(无名)";

                        var sels = b["Selections"] as JObject;
                        if (sels != null)
                        {
                            foreach (var kv in sels)
                            {
                                var entries = kv.Value as JArray;
                                if (entries == null) continue;
                                var byRank = new Dictionary<int, List<string>>();
                                var raw = new List<Plan.Ent>();
                                foreach (var e in entries)
                                {
                                    var g = (string)e["Selection"];
                                    if (string.IsNullOrEmpty(g)) continue;
                                    int rank = 0;
                                    var rt = e["Rank"];
                                    if (rt != null) int.TryParse(rt.ToString(), out rank);
                                    List<string> l;
                                    if (!byRank.TryGetValue(rank, out l)) { l = new List<string>(); byRank[rank] = l; }
                                    l.Add(g);
                                    raw.Add(new Plan.Ent { Rank = rank, Guid = g, Group = (string)e["FeatureGroup"] });
                                }
                                p.Sel[kv.Key] = byRank;
                                p.Raw[kv.Key] = raw;
                            }
                        }
                        result.Add(p);
                    }
                    catch (Exception e) { Main.LogError("跳过一套解析失败的方案: " + e.Message); }
                }
                return result;
            }
            catch (Exception e) { Main.LogError("读取 RTAutoBuilder 方案失败: " + e.Message); return null; }
        }
    }
}