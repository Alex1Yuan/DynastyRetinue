using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Controllers.Units;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Levelup;
using Kingmaker.UnitLogic.Levelup.Selections;
using Kingmaker.UnitLogic.Levelup.Selections.Feature;
using Kingmaker.UnitLogic.Progression.Features;
using Kingmaker.UnitLogic.Progression.Paths;

namespace DynastyRetinue
{
    /// <summary>
    /// 分型 + 阶卫门槛。
    ///
    /// v0.0.6 实测结论（dynasty_log 18:49-18:50）：
    ///   起点 lv0 hp39 facts13
    ///   T1 → lv15 hp78  facts23
    ///   T2 → lv35 hp110 facts33
    ///   T3 → lv55 hp200 facts38~42
    /// 血量 5.1 倍、facts 三倍，链是真的能串，且四个分型 facts 数各不相同。
    ///
    /// 另：LevelUpManager(autoCommit:true) 会一次吃掉整条 path 的多个 rank，
    /// 不是一级一调 —— 15 级只用了 2 次调用。所以循环条件必须看 rank 而不是次数。
    /// </summary>
    public static class Archetypes
    {
        // ---------- 分型模板：可外部自定义 ----------
        // 文件放在 mod 目录下的 archetypes.json，首次运行会自动写一份默认模板出来。
        // 解析失败 / 文件缺失 一律回退到内置默认值，不会因为改坏文件就起不来。
        private static ChainProbe.Archetype[] _loaded;
        private static bool _tried;

        public static string TemplatePath
        {
            get { return System.IO.Path.Combine(Main.ModEntry != null ? Main.ModEntry.Path : ".", "archetypes.json"); }
        }

        /// <summary>上一次 ApplyChain 的加点命中统计（批量试算取用）。</summary>
        /// <summary>上一次 ApplyChain 的加点统计。
        /// Seen=遇到的 SelectionStateFeature 总数；NoOption=CanSelectAny 为 false（无可选项）；
        /// PlanHits=按方案命中；Fallbacks=退回第一个可选项。
        /// Seen 远小于 rank 数 ⇒ 大部分 rank 根本没有选择项；
        /// NoOption 占多数 ⇒ 卫兵的候选池是空的，天赋压根没发出去。</summary>
        public static int LastSeen, LastNoOption, LastPlanHits, LastFallbacks;

        /// <summary>
        /// 一次 ApplyChain 之后的方案落实情况。
        ///
        /// 存在的理由：autotest 那列"命中率 = 命中/遇到的选择点"是错的分母 —— 方案本来
        /// 就只指定一部分选择点，剩下的属于自由选择，不该算失败。实测 磐石·首席战士
        /// 86 见/77 中显示 89%，而方案里 77 条其实**全中**。要看的是方案覆盖率。
        /// </summary>
        public sealed class PlanAudit
        {
            public int Total;      // 链上、方案写了的条目总数
            public int Ok;         // 已生效
            public int Unreached;  // D：该 rank 没走到（等级/经验不够）—— 不算失败
            /// <summary>同 rank 的备选：兄弟条目已落地，这条只是没被挑中。
            /// 按段合成时一个槽位会从多个源各取一个候选，只能落一个 —— 那是设计冗余不是缺失。</summary>
            public int Alt;
            public int MissA, MissB, MissC;
            public string Detail = "";
            /// <summary>应生效的条目数（扣掉没走到的 rank）。</summary>
            public int Applicable { get { return Total - Unreached - Alt; } }
            /// <summary>方案覆盖率：应生效的里面落实了多少。</summary>
            public int Percent { get { return Applicable > 0 ? (int)(100.0 * Ok / Applicable) : 0; } }
        }
        public static PlanAudit LastAudit = new PlanAudit();

        /// <summary>
        /// 卫兵的人名池（archetypes.json 根级 guardNamePool，五条线共用）。
        /// 招募时从里面挑一个**当前没人用**的，之后跟这名卫兵一辈子；
        /// 晋升只换军衔前缀，人名不动。死了/遣散了这个名字就重新可用 ——
        /// 那是刻意的：这个人没了，名字可以有新人继承。
        /// </summary>
        public static string[] GuardNamePool;
        /// <summary>英文人名池。为空则回落中文池。</summary>
        public static string[] GuardNamePoolEn;

        /// <summary>
        /// 把一个人名换成当前语言的对应写法。换不了就原样返回。
        ///
        /// ★两个池按下标对齐★ guardNamePool[i] 和 guardNamePool_en[i] 是同一个人
        /// （凯尔顿 ↔ Kelton、沈砚舟 ↔ Shen Yanzhou）。所以切语言时不该给卫兵
        /// **重新抽一个名字** —— 那等于换了个人 —— 而是把他自己的名字换种写法。
        /// 「近卫长·李霁川」切成英文应该是「Household Sergeant · Li Jichuan」，
        /// 不是随机变成另一个人。
        /// </summary>
        public static string TranslatePerson(string person)
        {
            if (string.IsNullOrEmpty(person)) return person;
            var zh = GuardNamePool; var en = GuardNamePoolEn;
            if (zh == null || en == null || zh.Length != en.Length) return person;
            bool wantEn = (L.Current == L.EnGB);
            var from = wantEn ? zh : en;
            var to   = wantEn ? en : zh;
            for (int i = 0; i < from.Length; i++)
                if (string.Equals(from[i], person, StringComparison.Ordinal)) return to[i];
            return person;   // 不在池里（玩家手改的、或池子换过）—— 不动
        }

        /// <summary>按当前界面语言取人名池。英文池为空时回落中文池 —— 有名字总比没有强。</summary>
        public static string[] NamePool
        {
            get
            {
                if (L.Current == L.EnGB && GuardNamePoolEn != null && GuardNamePoolEn.Length > 0)
                    return GuardNamePoolEn;
                return GuardNamePool;
            }
        }

        /// <summary>
        /// 上一次加载失败是不是**暂时性**的（蓝图还没就绪）。是就不缓存失败，下次访问重试。
        ///
        /// ★这是本机真实发生过的一次事故★（dynasty_log.txt 16:18:15，v0.5.5）：
        ///     分型「近战 Melee」里的 career path 解析不到: 974496d72fbe...
        ///     …四条全部解析不到…
        ///     archetypes.json 里没有一条有效分型，回退默认。
        /// 那些 GUID 到今天都没改过、现在照样能载入 —— 所以不是数据错，是**时序**：
        /// 有人在蓝图缓存就绪之前先读了 Archetypes.All。
        ///
        /// 而旧写法是 `if (!_tried) { _tried = true; _loaded = LoadTemplate(); }` ——
        /// **_tried 在知道结果之前就置了 true**，于是一次过早访问会让整个会话
        /// 永久退回内置 4 分型（无精英、无装备表、无人名池），再也不重试。
        /// 玩家看到的是"mod 装了但什么都不对"，而日志里那四行 ERROR 早就滚没了。
        /// </summary>
        private static bool _transientFail;

        public static ChainProbe.Archetype[] All
        {
            get
            {
                if (!_tried || _transientFail)
                {
                    _loaded = LoadTemplate();
                    // 成功、或失败但属于"文件缺失/格式错"这类**不会自愈**的，才封盘。
                    // 蓝图解析不到属于会自愈的，留着下次重试。
                    _tried = (_loaded != null) || !_transientFail;
                }
                return _loaded ?? ChainProbe.Archetypes;
            }
        }

        /// <summary>面板上的「重载模板」按钮用 —— 改完 json 不用重启游戏。</summary>
        public static void Reload()
        {
            _tried = false; _loaded = null; _transientFail = false;
            var a = All;
            Main.Log("分型模板已重载：" + a.Length + " 个 —— " + string.Join(" / ", a.Select(x => x.Name).ToArray()));
        }

        private static ChainProbe.Archetype[] LoadTemplate()
        {
            _transientFail = false;
            try
            {
                var path = TemplatePath;
                if (!System.IO.File.Exists(path)) { WriteDefaultTemplate(path); return null; }

                var json = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                var arr = root["archetypes"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null || arr.Count == 0) { Main.LogError("archetypes.json 里没有 archetypes 数组，回退默认。"); return null; }

                // 卫兵的人名池（根级，五条线共用）。取不到就留空，ApplyName 会退回编号式命名。
                try { GuardNamePool = ReadGuidList(root["guardNamePool"]); }
                catch { GuardNamePool = null; }
                // 英文人名池。中文池里那 100 个虽然写成汉字，多半是西方名的音译
                //（凯尔顿=Kelton、洛克哈特=Lockhart…），英文语境下直接用汉字会很突兀。
                try { GuardNamePoolEn = ReadGuidList(root["guardNamePool_en"]); }
                catch { GuardNamePoolEn = null; }

                var list = new List<ChainProbe.Archetype>();
                foreach (var item in arr)
                {
                    var name = (string)item["name"];
                    var chainTok = item["chain"] as Newtonsoft.Json.Linq.JArray;
                    if (string.IsNullOrEmpty(name) || chainTok == null || chainTok.Count == 0)
                    { Main.LogError("跳过一条格式不对的分型（缺 name 或 chain）。"); continue; }

                    var chain = new List<string>();
                    bool bad = false;
                    foreach (var g in chainTok)
                    {
                        var s = (string)g;
                        if (string.IsNullOrEmpty(s)) { bad = true; break; }
                        // 只校验能不能解析成 career path —— 解析不到就整条跳过，
                        // 免得运行时才发现 GUID 打错
                        if (ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(s) == null)
                        { Main.LogError("分型「" + name + "」里的 career path 解析不到: " + s); bad = true; break; }
                        chain.Add(s);
                    }
                    if (bad) continue;
                    var a = new ChainProbe.Archetype(name, chain.ToArray());
                    a.PlanName = (string)item["plan"];   // 可选：绑定的 RTAutoBuilder 加点方案
                    a.UnitId   = (string)item["unit"];    // 可选：该分型专用的单位蓝图
                    // 可选：unit 解析不到时的备选链（DLC 缺失兜底，见 ChainProbe.UnitFallback）
                    a.UnitFallback = ReadGuidList(item["unitFallback"]);
                    a.BrainId  = (string)item["brain"];   // 可选：覆盖 AI brain
                    a.EliteUnitId = (string)item["eliteUnit"];  // 可选：精英专用蓝图（兼作精英身份判据）
                    a.EliteName   = (string)item["eliteName"];  // 可选：精英专属名字
                    // 可选：毕业装备。这里**不**校验能否解析 —— 未启用 DLC 的装备
                    // 不该导致整条分型作废，装备时再逐件跳过。
                    a.Gear       = ReadGuidList(item["gear"]);
                    a.PlayerGear = ReadGuidList(item["playerGear"]);
                    a.GearT1     = ReadGuidList(item["gearT1"]);
                    a.GearT2     = ReadGuidList(item["gearT2"]);
                    a.GearT3     = ReadGuidList(item["gearT3"]);
                    a.GuardNames = ReadGuidList(item["guardNames"]);   // 复用同一个字符串数组读法
                    a.GuardNamesEn = ReadGuidList(item["guardNames_en"]);
                    a.GrantFeatures = ReadGuidList(item["grantFeatures"]);
                    a.PreGrant      = ReadGuidList(item["preGrant"]);

                    // 多精英：一个分型可以有若干个顶级人物，各有自己的单位/名字/装备/链。
                    // 旧的单精英字段（eliteUnit/eliteName/gear）会被合成成只有一个元素的列表，
                    // 这样下游代码只需要处理列表这一种形态。
                    var elitesTok = item["elites"] as Newtonsoft.Json.Linq.JArray;
                    if (elitesTok != null && elitesTok.Count > 0)
                    {
                        var el = new List<ChainProbe.EliteDef>();
                        foreach (var e in elitesTok)
                        {
                            var def = new ChainProbe.EliteDef
                            {
                                UnitId   = (string)e["unit"],
                                UnitFallback = ReadGuidList(e["unitFallback"]),
                                BrainId  = (string)e["brain"],   // 可选：不填沿用分型的
                                Name     = (string)e["name"],
                                Rank     = (string)e["rank"],
                                RankEn   = (string)e["rank_en"],
                                PlanName = (string)e["plan"],
                                Gear     = ReadGuidList(e["gear"]),
                                Chain    = ReadGuidList(e["chain"]),
                                KeyTalents   = ReadGuidList(e["keyTalents"]),
                                AttrPriority = ReadGuidList(e["attrPriority"]),
                                RaceId       = (string)e["race"],
                                PreGrant     = ReadGuidList(e["preGrant"]),
                                PlanSegments = ReadPlanSegments(e["planSegments"]),
                                ExcludeFeatures = ReadGuidList(e["excludeFeatures"]),
                            };
                            if (!string.IsNullOrEmpty(def.UnitId)) el.Add(def);
                        }
                        if (el.Count > 0) a.Elites = el.ToArray();
                    }
                    if (a.Elites == null && !string.IsNullOrEmpty(a.EliteUnitId))
                        a.Elites = new[] { new ChainProbe.EliteDef {
                            UnitId = a.EliteUnitId, Name = a.EliteName, Gear = a.Gear } };

                    list.Add(a);
                }

                if (list.Count == 0)
                {
                    // ★区分两种"没有有效分型"★
                    // json 结构是好的、条目也在，却一条都没通过 —— 那几乎一定是
                    // career path 解析不到，而那是**会自愈**的（蓝图缓存还没就绪）。
                    // 标成暂时性失败，下次访问重试，别把整个会话钉死在内置默认上。
                    _transientFail = arr.Count > 0;
                    Main.LogError("archetypes.json 里没有一条有效分型，本次回退默认。"
                                + (_transientFail
                                   ? "　<注意：多半是蓝图缓存还没就绪，下次访问会自动重试；"
                                     + "如果读档之后仍然是这条，才是 GUID 真的错了>"
                                   : ""));
                    return null;
                }
                Main.Log("已从 archetypes.json 载入 " + list.Count + " 个分型。");
                return list.ToArray();
            }
            catch (Exception e) { Main.LogError("读取 archetypes.json 失败，回退默认: " + e.Message); return null; }
        }

        /// <summary>
        /// 面板【装配】：把一件装备加进某分型的 playerGear，并写回 archetypes.json。
        /// 写回而不是只改内存 —— 否则重载模板/重启游戏就丢了。
        /// </summary>
        public static void AddPlayerGear(int archIndex, string guid)
        {
            var a = Get(archIndex);
            if (a == null || string.IsNullOrEmpty(guid)) return;
            var l = new List<string>(a.PlayerGear ?? new string[0]);
            foreach (var s in l)
                if (string.Equals(s, guid, StringComparison.OrdinalIgnoreCase))
                { Main.Log("已经装配过了: " + ItemTool.NameOf(guid)); return; }
            l.Add(guid);
            a.PlayerGear = l.ToArray();
            if (SavePlayerGear(a.Name, a.PlayerGear))
                Main.Log("已装配到「" + a.Name + "」: " + ItemTool.NameOf(guid) + "（共 " + l.Count + " 件）");
        }

        /// <summary>面板【移除】。</summary>
        public static void RemovePlayerGear(int archIndex, string guid)
        {
            var a = Get(archIndex);
            if (a == null || a.PlayerGear == null) return;
            var l = new List<string>();
            foreach (var s in a.PlayerGear)
                if (!string.Equals(s, guid, StringComparison.OrdinalIgnoreCase)) l.Add(s);
            a.PlayerGear = l.Count > 0 ? l.ToArray() : null;
            if (SavePlayerGear(a.Name, a.PlayerGear))
                Main.Log("已从「" + a.Name + "」移除: " + ItemTool.NameOf(guid) + "（剩 " + l.Count + " 件）");
        }

        /// <summary>
        /// 把某分型的 playerGear 写回 archetypes.json。
        /// 只动这一个字段 —— 用 JObject 局部替换而不是整份重新序列化，
        /// 免得把用户手写的注释字段（"_来源" 之类）和字段顺序冲掉。
        /// </summary>
        private static bool SavePlayerGear(string archName, string[] gear)
        {
            try
            {
                var path = TemplatePath;
                if (!System.IO.File.Exists(path)) { Main.LogError("archetypes.json 不存在，无法保存装配。"); return false; }

                var root = Newtonsoft.Json.Linq.JObject.Parse(
                    System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                var arr = root["archetypes"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null) { Main.LogError("archetypes.json 里没有 archetypes 数组。"); return false; }

                foreach (var item in arr)
                {
                    if (!string.Equals((string)item["name"], archName, StringComparison.Ordinal)) continue;
                    var o = item as Newtonsoft.Json.Linq.JObject;
                    if (o == null) continue;
                    if (gear == null || gear.Length == 0) o.Remove("playerGear");
                    else o["playerGear"] = new Newtonsoft.Json.Linq.JArray(gear);
                    System.IO.File.WriteAllText(path, root.ToString(), new System.Text.UTF8Encoding(false));
                    return true;
                }
                Main.LogError("archetypes.json 里找不到分型「" + archName + "」，装配未保存。");
                return false;
            }
            catch (Exception e) { Main.LogError("保存装配失败: " + e.Message); return false; }
        }

        /// <summary>
        /// 读一个 GUID 列表。每个元素可以是：
        ///   "guid"                 —— 单件
        ///   ["guidA","guidB",...]  —— 同一格的候选，装备时依次尝试到能装上为止
        /// 后者在内部拼成 "guidA|guidB"，由 GearTool 拆开。
        /// 空数组/非数组一律返回 null。
        /// </summary>
        /// <summary>
        /// 解析 planSegments：pathGuid -> 方案名（整段照抄），或 pathGuid -> { 桶: 方案名 或 [方案名…] }。
        /// 单个字符串归一成 { "default": [名字] }；桶值可以是字符串或数组。
        /// </summary>
        private static Dictionary<string, Dictionary<string, string[]>> ReadPlanSegments(Newtonsoft.Json.Linq.JToken tok)
        {
            var obj = tok as Newtonsoft.Json.Linq.JObject;
            if (obj == null) return null;
            var res = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in obj)
            {
                if (kv.Key.StartsWith("_")) continue;         // 注释键
                var inner = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                var sub = kv.Value as Newtonsoft.Json.Linq.JObject;
                if (sub != null)
                {
                    foreach (var s in sub)
                    {
                        if (s.Key.StartsWith("_")) continue;
                        var names = ReadNameList(s.Value);
                        if (names != null && names.Length > 0) inner[s.Key] = names;
                    }
                }
                else
                {
                    var names = ReadNameList(kv.Value);
                    if (names != null && names.Length > 0) inner["default"] = names;
                }
                if (inner.Count > 0) res[kv.Key] = inner;
            }
            return res.Count > 0 ? res : null;
        }

        /// <summary>一个值可能是 "名字" 或 ["名字1","名字2"]，都收成数组。</summary>
        private static string[] ReadNameList(Newtonsoft.Json.Linq.JToken tok)
        {
            var arr = tok as Newtonsoft.Json.Linq.JArray;
            if (arr != null)
            {
                var l = new List<string>();
                foreach (var t in arr) { var v = (string)t; if (!string.IsNullOrEmpty(v)) l.Add(v); }
                return l.ToArray();
            }
            var one = (string)tok;
            return string.IsNullOrEmpty(one) ? null : new[] { one };
        }

        private static string[] ReadGuidList(Newtonsoft.Json.Linq.JToken tok)
        {
            var arr = tok as Newtonsoft.Json.Linq.JArray;
            if (arr == null) return null;
            var l = new List<string>();
            foreach (var g in arr)
            {
                var inner = g as Newtonsoft.Json.Linq.JArray;
                if (inner != null)
                {
                    var alts = new List<string>();
                    foreach (var x in inner)
                    {
                        var s2 = (string)x;
                        if (!string.IsNullOrEmpty(s2)) alts.Add(s2);
                    }
                    if (alts.Count > 0) l.Add(string.Join("|", alts.ToArray()));
                    continue;
                }
                var s = (string)g;
                if (!string.IsNullOrEmpty(s)) l.Add(s);
            }
            return l.Count > 0 ? l.ToArray() : null;
        }

        /// <summary>
        /// 从 RTAutoBuilder 一键导入：每套加点方案变成一个分型。
        /// 链取方案自己的 FirstArchetype -> SecondArchetype -> Ascension(T3)。
        /// 会覆盖 archetypes.json，覆盖前自动备份成 archetypes.json.bak。
        /// </summary>
        public static void ImportFromAutoBuilder()
        {
            try
            {
                // ★这个门原来是假的★ 判据曾是 `BuildPlans.All.Count == 0`，
                // 但 BuildPlans.All = mod 自带的 18 套 plans.json + 可选的 RTAutoBuilder 方案，
                // 所以对**任何**玩家都不成立 —— 没装 RTAutoBuilder 的人照样会执行到下面的
                // WriteAllText，把配表覆盖成一份丢掉 unit/brain/elites/gear 的残缺版。
                // 现在只看 RTAutoBuilder 自己那一份，"没装就什么都不会发生"这个本意才真正成立。
                var external = BuildPlans.LoadAutoBuilder();
                if (external == null || external.Count == 0)
                {
                    Main.LogError("没找到 RTAutoBuilder 方案，已取消导入（archetypes.json 未改动）。"
                                + "\n    检查路径: " + BuildPlans.SourcePath);
                    return;
                }
                var plans = BuildPlans.All;
                if (plans.Count == 0)
                {
                    Main.LogError("没有任何可导入的加点方案。");
                    return;
                }

                const string Ascension = "bcefe9c41c7841c9a99b1dbac1793025";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_来源\": \"由 RTAutoBuilder 的 AutoBuilderSettings.json 导入；plan 字段绑定该方案的天赋选择。\",");
                sb.AppendLine("  \"_说明\": \"chain 取方案自己的 FirstArchetype -> SecondArchetype -> Ascension(T3)，可手改。\",");
                sb.AppendLine("  \"archetypes\": [");

                int n = 0;
                for (int i = 0; i < plans.Count; i++)
                {
                    var pl = plans[i];
                    if (string.IsNullOrEmpty(pl.First) || string.IsNullOrEmpty(pl.Second)) continue;
                    if (ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(pl.First) == null) continue;
                    if (ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(pl.Second) == null) continue;

                    if (n > 0) sb.AppendLine(",");
                    string disp = pl.Display.Replace("\"", "'").Replace("\\", "/");
                    string nm = disp.Length > 44 ? disp.Substring(0, 44) : disp;
                    sb.Append("    { \"name\": \"").Append(nm)
                      .Append("\", \"plan\": \"").Append(disp)
                      .Append("\", \"chain\": [\"").Append(pl.First).Append("\", \"").Append(pl.Second)
                      .Append("\", \"").Append(Ascension).Append("\"] }");
                    n++;
                }
                sb.AppendLine();
                sb.AppendLine("  ]");
                sb.AppendLine("}");

                var path = TemplatePath;
                if (System.IO.File.Exists(path))
                {
                    // ★备份必须防得住"点第二次"★
                    // 原来是 File.Copy(path, path + ".bak", true) —— 单槽且 overwrite=true。
                    // 第一次点：把完好的配表备份到 .bak；第二次点：用**已经被毁的**配表
                    // 覆盖掉那份备份，于是唯一的救命稻草没了。
                    // 而这次导入只写 name/plan/chain，unitId / GearT1-T3 / elites /
                    // guardNamePool 一个都不写 —— 丢的是整个 40KB 配表。
                    //
                    // 所以分两层：.orig 是第一次导入前的原件，**永不覆盖**；
                    // 另外每次再留一份带时间戳的，方便回退到任意一次导入之前。
                    string pristine = path + ".orig";
                    if (!System.IO.File.Exists(pristine))
                    {
                        System.IO.File.Copy(path, pristine, false);
                        Main.Log("★已保存原始模板 archetypes.json.orig（此后不再覆盖）★");
                    }
                    string stamped = path + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bak";
                    System.IO.File.Copy(path, stamped, false);
                    Main.Log("本次导入前的模板已备份为 " + System.IO.Path.GetFileName(stamped));
                    Main.Log("★注意★ 导入只写 name/plan/chain —— 精英定义、装备表、人名池、"
                           + "单位蓝图都不会被写回。要保留它们请从上面的备份手工合并。");
                }
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                Main.Log("已导入 " + n + " 个分型（共 " + plans.Count + " 套方案）。");
                Reload();
            }
            catch (Exception e) { Main.LogError("导入失败: " + e); }
        }

        private static void WriteDefaultTemplate(string path)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_说明\": \"卫兵分型模板。chain 是 T1->T2->T3 的 career path AssetId，按顺序消费等级。\",");
                sb.AppendLine("  \"_等级\": \"T1 每条 15 级，T2/T3 每条 20 级，三段串满 = 55 级（XPTable 上限）。\",");
                sb.AppendLine("  \"_注意\": \"只能填原版 career path 的 AssetId；解析不到的会被跳过并回退默认。\",");
                sb.AppendLine("  \"_可用的T1\": \"Soldier 06f4f78a9c1a472b85cd79a9a142153d / Fighter 974496d72fbe4329b438ee15cf004bd2 / Adept 1529e5a0e7844bf3bb8d0cc0501264d4 / Leader 33725d84e95e4323ac46d8fbf899b250 / Reaper(DLC1) dd6948ee596346a69733d0bb107c2f42\",");
                sb.AppendLine("  \"_可用的T2\": \"Veteran 651684417def4c258c72ba91f481b817 / Vanguard fec9cd09f11b4615b7a17f441350d2d4 / Hunter 6f276e8a8e2c4a548504ae39d2a7f22a / Assassin 7b90955673a54136be9c11743943fdfe / Tactician 604fa184d7d944c8ae5965f9700782b5 / Strategist a31b390cabe7464fbfd0e1ba53c4112f / Master(DLC2) 21b0fc8cfbe940ecbef0114d5d27b44a / Executioner(DLC1) d6c0498a227040c891e4e2703eb55c13\",");
                sb.AppendLine("  \"_可用的T3\": \"Ascension bcefe9c41c7841c9a99b1dbac1793025\",");
                sb.AppendLine("  \"archetypes\": [");
                var d = ChainProbe.Archetypes;
                for (int i = 0; i < d.Length; i++)
                {
                    sb.Append("    { \"name\": \"").Append(d[i].Name).Append("\", \"chain\": [");
                    for (int k = 0; k < d[i].Chain.Length; k++)
                    {
                        sb.Append("\"").Append(d[i].Chain[k]).Append("\"");
                        if (k < d[i].Chain.Length - 1) sb.Append(", ");
                    }
                    sb.Append("] }");
                    if (i < d.Length - 1) sb.Append(",");
                    sb.AppendLine();
                }
                sb.AppendLine("  ]");
                sb.AppendLine("}");
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                Main.Log("已写出默认分型模板: " + path);
            }
            catch (Exception e) { Main.LogError("写默认模板失败: " + e.Message); }
        }

        public static ChainProbe.Archetype Get(int index)
        {
            var a = All;
            if (a.Length == 0) return null;
            if (index < 0) index = 0;
            if (index >= a.Length) index = a.Length - 1;
            return a[index];
        }

        // ---------- 阶卫门槛 ----------
        // 玩家职业阶位由等级推出。XPTable 断点：idx15=6000, idx16=8005, idx35=56454, idx36=60357
        // 即 15/35 就是 T1→T2、T2→T3 的分界，跟 path 的 Ranks(15/20/20) 完全吻合。
        public static int PlayerTier(BaseUnitEntity leader)
        {
            if (leader == null) return 1;
            int lv = leader.Progression != null ? leader.Progression.CharacterLevel : 1;
            if (lv >= 36) return 3;
            if (lv >= 16) return 2;
            return 1;
        }

        /// <summary>卫兵等级上限：跟玩家同阶，不能越阶。</summary>
        public static int GuardLevelCap(int tier)
        {
            if (tier >= 3) return 55;
            if (tier == 2) return 35;
            return 15;
        }

        /// <summary>卫兵数量上限。</summary>
        public static int GuardCountCap(int tier)
        {
            // 内置默认 T1=2 / T2=4 / T3=6，这三个数是我定的，不是游戏限制。
            // ★曾经有个 GuardCapOverride 字符串字段可以覆盖它，v0.49.0 删了★
            // 理由：它和 RecruitMaxGuards（有滑条、进 UI）职责重叠，
            // 两个上限来源迟早会打架。现在上限只有一处：招募区那根滑条。
            if (tier >= 3) return 6;
            if (tier == 2) return 4;
            return 2;
        }

        /// <summary>该阶位能用到 career 链的第几段（1=只 T1, 2=到 T2, 3=全链）。</summary>
        public static int ChainDepth(int tier)
        {
            if (tier >= 3) return 3;
            if (tier == 2) return 2;
            return 1;
        }

        /// <summary>
        /// 按分型链给卫兵升级，升到 levelCap 或经验耗尽为止。
        ///
        /// 这里**不再调用 CanUpgradePath** —— v0.0.5 探针证明它只是 UI 判定
        /// （全代码库仅 2 个调用点，CareerPathVM.cs:614 和 ShipProgressionVM.cs:74），
        /// LevelUpManager / ApplyCareerPath 都不看它。v0.0.6 里 RetinueTest 还留着这个
        /// 自锁，导致卫兵明明有 68639 经验（够 37 级）却卡在 15 级。
        /// </summary>
        public static int ApplyChain(BaseUnitEntity guard, ChainProbe.Archetype arch, int levelCap, int chainDepth, bool throttleXp = true, bool allowOverBudget = false, string[] chainOverride = null, string planOverride = null, string[] keyTalents = null, string[] attrPriority = null, string[] preGrant = null, BuildPlans.Plan planDirect = null)
        {
            int total = 0;
            // 统计先清空 —— 下面两处早退时消费者读到的必须是干净的 0，
            // 否则 autotest 会把上一个卫兵的成绩当成这一个的。
            ResetFallbackLog(); _seenItems.Clear(); _whyBlocked.Clear();
            LastSeen = 0; LastNoOption = 0; LastPlanHits = 0; LastFallbacks = 0;
            LastAudit = new PlanAudit();
            if (guard == null || arch == null) return 0;

            // 已到上限就一步都别走。v0.1.5 实测：满 55 级后 while 里的
            // CharacterLevel < levelCap 拦不住 LevelUpManager 继续吃 rank
            //（rank 6->8->11->15 而等级恒为 55），白白消耗天赋选择、污染卫兵。
            if (guard.Progression.CharacterLevel >= levelCap) return 0;

            // 绑定的 RTAutoBuilder 加点方案（没绑就退回"第一个可选项"）
            // ★ 精英要用它自己的方案 ★ v0.5.7 实测的 bug：EliteDef.PlanName 加了却没接进来，
            //   结果火杖行刑者（该用主控的「火杖战士行刑者」）跑的是海因里希的刺客方案，
            //   84 个选择点里 48 个回退 —— 回退就是乱点，属性自然对不上。
            // ★ 声明了自己职业链的精英，不再静默继承分型的方案 ★
            //   v0.8.0 回归：赏金·猎首（chain=Soldier->Hunter、只给 keyTalents、**没有** plan）
            //   继承了分型「狙击 Sniper」的 "Yrliet · DLC3"，于是下面的
            //   ★★方案决定路线★★ 把它的链改写成 Adept->Hunter —— 两个狙击精英
            //   变成同一个人（autotest.tsv 里 85 见/51 中，两行数字一模一样）；
            //   顺带把灵族的背景/起源（CraftworldHomeworld / RangerOccupation）发给了一个人类。
            //
            //   规则：「声明 chain」= 我自己掌管这条路线。
            //     - 自带 plan          → 用自己的 plan（哪怕它要改写自己的 chain，也照改，
            //                            那正是校正机制存在的意义）
            //     - 自带 chain、没 plan → 不给方案，走 keyTalents/attrPriority 回退
            //     - 都没有             → 照旧继承分型的 plan（普通卫兵走这条）
            bool planIsOwn  = !string.IsNullOrEmpty(planOverride);
            bool chainIsOwn = (chainOverride != null && chainOverride.Length > 0);
            BuildPlans.Plan plan;
            if (planDirect != null)
            {
                // 按段合成的方案（planSegments）—— 已经是成品，不再查表
                plan = planDirect;
            }
            else
            {
                string planName = planIsOwn ? planOverride : (chainIsOwn ? null : arch.PlanName);
                if (!planIsOwn && chainIsOwn && !string.IsNullOrEmpty(arch.PlanName))
                    Main.Log("    自带职业链、无自有方案 —— 不继承分型方案「" + arch.PlanName
                             + "」（继承会连职业链一起被改写），改走 关键天赋/属性优先 回退。");
                plan = BuildPlans.Get(planName);
            }

            // ★ 升级之前先把方案的家园世界/背景、以及额外前置授予 ★
            // 起源解锁专属天赋（比如 Yrliet 的 RangerOccupation 是灵族游侠）。
            // 起源不对的话，方案里那些门控选项根本不出现在 SelectionStateFeature.Items 里，
            // 只能回退 —— 实测寂静之眼 85 个选择点回退 34 个，多半就是这个。
            // 两者都是原版 BlueprintFeature，不产生新 AssetId。
            //
            // v0.8.1 修：StartGame_Pregen_* 出厂就自带一套家园世界+背景
            //（它的 FeatureList 上挂 ApplyCareerPath 组件，已在 ChargenPath_ForPregens 上替它选好），
            // 之前只判 Facts.Contains 然后直接 Add，于是变成"两个家园世界 + 两个背景"。
            // 不只是显示难看：BlueprintSelectionFeature.GetSelectionItems 会把 unit.Facts 里
            // 所有 AddFeaturesToLevelUp 并进候选池，旧背景会一直往后面的选择点里掺东西。
            if (plan != null)
            {
                GrantChargen(guard, plan.Homeworld, FeatureGroup.ChargenHomeworld, "家园世界");
                GrantChargen(guard, plan.Origin,    FeatureGroup.ChargenOccupation, "背景/起源");
            }
            // 额外前置：方案存不下、但不给就整段天赋都不出现的东西，目前是灵能学派。
            // Pyromancy_Base_Feature / Biomancy_Base_Feature 各带 9 个 AddFeaturesToLevelUp，
            // 不授予的话火系/生物系灵能一条都进不了候选池（实测火杖行刑者 7 条全灭）。
            if (preGrant != null)
                foreach (var pg in preGrant) GrantPlain(guard, pg, "前置");
            int planHits = 0, fallbacks = 0, seen = 0, noOpt = 0;

            // 精英可以覆盖分型的链
            var chain = (chainOverride != null && chainOverride.Length > 0) ? chainOverride : arch.Chain;

            // ★★ 方案决定路线 ★★
            // 主因就在这里：chain 和 plan 原本是两个互不相干的参数，
            // 一旦某段职业链的 GUID 不在方案的 3 个 key 里，Candidates() 返回 null，
            // **那一整段 100% 回退**。而 T3(Ascension) 全角色通用所以永远命中 ——
            // 这解释了实测里"84点命中36"这类数字：48 = T1(22)+T2(26) 全灭、36 = T3 全中。
            // 绑定方案时以方案自己的 First/Second/Ascension 为准。
            if (plan != null && !string.IsNullOrEmpty(plan.First) && !string.IsNullOrEmpty(plan.Second))
            {
                const string Ascension = "bcefe9c41c7841c9a99b1dbac1793025";
                var want = new[] { plan.First, plan.Second, Ascension };
                bool same = chain.Length == 3
                            && string.Equals(chain[0], want[0], StringComparison.OrdinalIgnoreCase)
                            && string.Equals(chain[1], want[1], StringComparison.OrdinalIgnoreCase);
                if (!same)
                {
                    Main.Log("    ★ 职业链按方案校正: " + PathName(chain.Length > 0 ? chain[0] : null) + "->" + PathName(chain.Length > 1 ? chain[1] : null)
                             + "  =>  " + PathName(want[0]) + "->" + PathName(want[1])
                             + "（方案=" + (planIsOwn ? "本单位自有" : "分型继承")
                             + "；不校正的话这两段的选择点会 100% 回退）");
                    chain = want;
                }
            }
            else if (plan != null)
            {
                // 方案在但缺 First/Second —— 至少提示哪些段对不上
                for (int i = 0; i < chain.Length; i++)
                    if (!plan.Sel.ContainsKey(chain[i]))
                        Main.LogError("    方案里没有这条 path: " + PathName(chain[i]) + " —— 该段将全部回退");
            }

            // 每条 path 实际走到的 rank —— 收尾核对要靠它区分"没点上"和"等级压根没到"。
            // 普通卫兵只到 38 级，方案却写到 55 级；不记这个，没走到的 rank 会被
            // 当成"可选却没选"报成我们的 bug（实测 23-24 条全是这么来的）。
            var reachedRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int segments = Math.Min(chainDepth, chain.Length);
            for (int i = 0; i < segments; i++)
            {
                var p = ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(chain[i]);
                if (p == null) { Main.LogError("career path 缺失: " + chain[i]); continue; }
                string pathGuid = chain[i];

                int guard1 = 0, stuck = 0, lastLv = guard.Progression.CharacterLevel;
                // 记下真实经验，节流期间会临时压低，结束必须还原
                int realXp = guard.Progression.Experience;
                while (guard1 < 60
                       && guard.Progression.CharacterLevel < levelCap
                       && guard.Progression.GetPathRank(p) < p.Ranks)
                {
                    // ★ 经验节流必须在 while 判据之前生效 ★
                    //   v0.2.9 把它放在循环体内，导致第一轮跑完 CanLevelUp 就为 false，
                    //   循环再也进不去，全部卡在 lv1。
                    //
                    // 原理（v0.2.8 实测）：LevelUpManager 内部
                    //   GetMaxAvailablePathRank() = rank + ExperienceLevel - CharacterLevel
                    // 经验头顶越高，一次调用推进的 rank 越多，中间 rank 的选择点全被跳过。
                    // 把经验精确卡在"刚好够下一级"，头顶只剩 1，就只能逐 rank 走。
                    //
                    // 真实卫兵的经验是打仗挣的、AdvanceExperienceTo 单调不减压不下来，
                    // 所以用反射直接写 Experience；写不了就退回原行为（会批量推进，但不崩）。
                    int lvNow = guard.Progression.CharacterLevel;
                    if (throttleXp)
                    {
                        try
                        {
                            var xpTable = Game.Instance.BlueprintRoot.Progression.XPTable;
                            int need = xpTable.GetBonus(lvNow + 1);
                            if (need > realXp && !allowOverBudget) break;   // 真的没经验了
                            SetExperience(guard, need);
                        }
                        catch (Exception xe) { Main.LogError("经验节流: " + xe.Message); }
                    }
                    else if (!guard.Progression.CanLevelUp) break;

                    try
                    {
                        int _rkB = guard.Progression.GetPathRank(p);
                        int _lvB = guard.Progression.CharacterLevel;

                        using (var mgr = new LevelUpManager(guard, p, true, guard.Progression.CharacterLevel + 1))
                        {
                            int _selCount = 0;
                            try { _selCount = mgr.Selections.Count; } catch { }
                            if (guard1 < 6)
                                Main.Log("      #" + guard1 + " 目标等级=" + (_lvB + 1)
                                         + "  rank " + _rkB + "  lv " + _lvB
                                         + "  Selections=" + _selCount
                                         + "  ExpLv=" + guard.Progression.ExperienceLevel);

                            foreach (var sel in mgr.Selections)
                            {
                                var f = sel as SelectionStateFeature;
                                if (f == null) continue;
                                seen++;
                                int cnt = 0;
                                try { cnt = f.Items.Count; } catch { }
                                RecordSeen(f, pathGuid, guard, plan);
                                if (!f.CanSelectAny) { noOpt++; if (noOpt <= 3) Main.Log("      [无可选项] rank" + f.PathRank + " Items=" + cnt); continue; }
                                PickOne(f, plan, pathGuid, ref planHits, ref fallbacks, keyTalents, attrPriority);
                            }
                        }
                        if (guard1 < 6)
                            Main.Log("      #" + guard1 + " 之后 rank " + _rkB + "->" + guard.Progression.GetPathRank(p)
                                     + "  lv " + _lvB + "->" + guard.Progression.CharacterLevel);

                        // 这一级没有选择点 ⇒ LevelUpManager 不会推进 rank（推进是选择的副作用）。
                        // 手动补一格，否则整条 path 会永远卡在这里。
                        if (guard.Progression.GetPathRank(p) == _rkB && guard.Progression.GetPathRank(p) < p.Ranks)
                        {
                            if (ForceAdvanceRank(guard, p) && guard1 < 6)
                                Main.Log("      #" + guard1 + " 无选择点，手动推进 rank -> " + guard.Progression.GetPathRank(p)
                                         + "  lv=" + guard.Progression.CharacterLevel);
                        }
                    }
                    catch (Exception e) { Main.Log("    升级中断: " + e.GetType().Name + " " + e.Message); break; }

                    guard1++; total++;
                    // 等级没涨：可能顶到 XPTable 上限，也可能这一级的选项全都选不了。
                    // v0.1.6 用 stuck>=1 太激进 —— 一次提交失败就永久卡住（实测先锋卡 lv44）。
                    if (guard.Progression.CharacterLevel == lastLv) { stuck++; if (stuck >= 3) break; }
                    else { stuck = 0; lastLv = guard.Progression.CharacterLevel; }
                    if (guard.Progression.CharacterLevel >= levelCap) break;
                }
                // 还原真实经验 —— 节流期间被临时压低了
                if (throttleXp)
                {
                    try
                    {
                        int cur = guard.Progression.Experience;
                        SetExperience(guard, Math.Max(realXp, cur));
                    }
                    catch { }
                }

                int rankNow = guard.Progression.GetPathRank(p);
                reachedRank[pathGuid] = rankNow;
                string why = "";
                if (rankNow < p.Ranks && guard.Progression.CharacterLevel < levelCap)
                {
                    // 这条 path 还有 rank、等级也没到上限，却停下来了 —— 把真因钉死
                    bool canLv = false;
                    try { canLv = guard.Progression.CanLevelUp; } catch { }
                    why = "  [停在此: CanLevelUp=" + canLv + " xp=" + guard.Progression.Experience
                          + (canLv ? " ← 经验够但升不上去，说明这一级的选项提交失败" : " ← 经验不够下一级") + "]";
                }
                Main.Log("    " + p.name + " (Tier" + p.Tier + ") rank=" + rankNow
                         + "/" + p.Ranks + " lv=" + guard.Progression.CharacterLevel + why);

                // 等级已经顶到上限，后面的段不用再走
                if (guard.Progression.CharacterLevel >= levelCap) break;
            }
            LastSeen = seen; LastNoOption = noOpt; LastPlanHits = planHits; LastFallbacks = fallbacks;
            LateGrant(guard, plan, chain, chainDepth);
            LastAudit = VerifyPlan(guard, plan, chain, chainDepth, reachedRank);
            Main.Log("    加点: 选择点 " + seen + " 个（无可选项 " + noOpt + "）"
                     + "，按方案命中 " + planHits + "，回退 " + fallbacks
                     + (plan != null ? "   方案=" + plan.Display : "   （未绑定方案）"));
            return total;
        }

        /// <summary>
        /// 选一个天赋 / 属性 / 技能。
        /// 优先用 RTAutoBuilder 方案里为 (career path, rank) 记录的选择，
        /// 找不到或不可选就退回"第一个可选项"。
        ///
        /// 刻意只用 (path, rank) 定位候选、再看哪个 GUID 出现在本次 Items 里 ——
        /// 不做 FeatureGroup 的字符串↔枚举对齐，少一处会出错的地方。
        /// 同一 rank 有多个组（Attribute / Talent / Skill…）时天然各取所需。
        /// </summary>
        /// <summary>
        /// 直接写 Experience。PartUnitProgression.Experience 是 { get; private set; }，
        /// AdvanceExperienceTo 只能加不能减，而节流需要把经验精确压到某一级的门槛。
        /// 反射失败就退回 AdvanceExperienceTo（只能升，节流会失效但不会崩）。
        /// </summary>
        /// <summary>
        /// 把经验直接顶到某个等级所需的量。精英专用 ——
        /// 精英是"毕业形态"，生成时就该到上限，不该像普通卫兵那样从主角经验×比例起步
        /// （主角 85799×0.8≈68639 只够 38 级，实测精英出生卡在 38）。
        /// 直接写字段而不是 AdvanceExperienceTo，因为后者内部走 GainExperience，
        /// 会被 XpPatch 再缩放一次，落点对不上。
        /// </summary>
        public static void GrantXpForLevel(BaseUnitEntity u, int level)
        {
            try
            {
                if (u == null || level < 1) return;
                var table = Game.Instance.BlueprintRoot.Progression.XPTable;
                if (table == null) return;
                int need = table.GetBonus(level);
                if (u.Progression.Experience >= need) return;
                SetExperience(u, need);
                Main.Log("  精英经验: -> " + need + "（够 " + level + " 级）");
            }
            catch (Exception e) { Main.LogError("设精英经验失败: " + e.Message); }
        }

        private static void SetExperience(BaseUnitEntity u, int value)
        {
            if (value < 0) value = 0;
            try
            {
                var pi = typeof(Kingmaker.UnitLogic.PartUnitProgression).GetProperty("Experience");
                var setter = pi != null ? pi.GetSetMethod(true) : null;
                if (setter != null) { setter.Invoke(u.Progression, new object[] { value }); return; }
            }
            catch { }
            try { if (u.Progression.Experience < value) u.Progression.AdvanceExperienceTo(value, false); } catch { }
        }

        /// <summary>
        /// 手动推进一个 rank。
        ///
        /// v0.3.1 实测：LevelUpManager 的 rank 前进是**做出选择时的副作用** ——
        /// ApplySelections 里 `AdvancePathRankTo(unit, selection.Path, i)`，i 就是该选择的 rank。
        /// 所以没有选择点的 rank（比如 T1 的 rank 3）压根不会前进，卡在那不动，
        /// 三条 path 各走两三级就 stuck 跳出，合计只到 lv14。
        ///
        /// 这里照抄 LevelUpManager.AddPathRank 的内部实现（那三行都是 public API）：
        ///   AddPathRank + 把该 rank 的 Features 加上并登记来源。
        /// </summary>
        private static bool ForceAdvanceRank(BaseUnitEntity guard, BlueprintCareerPath p)
        {
            try
            {
                int num = guard.Progression.GetPathRank(p) + 1;
                if (num > p.Ranks) return false;
                var entry = p.GetRankEntry(num);
                if (entry == null) return false;

                guard.Progression.AddPathRank(p);
                // RankEntry.Features 是 ReferenceArrayProxy（结构体），不能跟 null 比
                foreach (var feature in entry.Features)
                {
                    if (feature == null) continue;
                    var f = guard.Progression.Features.Add(feature);
                    if (f != null) f.AddSource(p, p, num);
                }
                return true;
            }
            catch (Exception e) { Main.LogError("手动推进 rank 失败: " + e.Message); return false; }
        }

        /// <summary>本次 ApplyChain 过程中，所有出现过的候选项：路线#rank#guid -> 是否曾可选。
        /// 必须带 rank —— 只用 guid 当 key 的话，一条天赋在 rank3 出现过，
        /// 就会让方案里 rank17 的同一条也算"出现过"，把"等级没到"误报成"可选却没选"。</summary>
        private static readonly Dictionary<string, bool> _seenItems =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static string SeenKey(string path, int rank, string guid)
        { return path + "#" + rank + "#" + guid; }

        /// <summary>方案里写了、但在**选择当场**不可选的条目 -> 当时的前置求值结果。
        /// 必须当场记：等收尾核对时卫兵已经 55 级了，等级类前置一律通过，
        /// 而实际卡住的时刻角色可能才 33 级 —— PsyRating4 的门槛正好在 34/39 这种边界上。</summary>
        private static readonly Dictionary<string, string> _whyBlocked =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 逐条核对方案：方案里写的每一条，卫兵身上到底有没有？没有的话是为什么？
        ///
        /// 三类原因指向完全不同的问题：
        ///   A 从未出现在候选里 —— rank 对不上、职业链不对、或种族/起源门控（灵族那种）
        ///   B 出现过但一直不可选 —— 前置天赋没点，属于方案内部的顺序依赖
        ///   C 出现过、可选、却没选上 —— 我们的 bug
        /// </summary>
        /// <summary>职业链 GUID -> 可读名，日志用。</summary>
        /// <summary>记录本次出现过的候选项，供收尾核对判断"从未出现 / 出现但不可选"。</summary>
        private static void RecordSeen(SelectionStateFeature f, string pathGuid,
                                       BaseUnitEntity guard, BuildPlans.Plan plan)
        {
            try
            {
                List<string> wanted = null;
                if (plan != null) wanted = plan.Candidates(pathGuid, f.PathRank);
                for (int i = 0; i < f.Items.Count; i++)
                {
                    var it = f.Items[i];
                    if (it.Feature == null) continue;
                    string raw = it.Feature.AssetGuid.ToString();
                    string g = SeenKey(pathGuid, f.PathRank, raw);
                    bool can = false;
                    try { can = f.CanSelect(it); } catch { }
                    bool old;
                    if (_seenItems.TryGetValue(g, out old)) { if (can && !old) _seenItems[g] = true; }
                    else _seenItems[g] = can;

                    // 方案要这一条、但当场选不了 —— 趁现在把前置逐条求值记下来。
                    // 只对方案里写了的条目做，避免给每个候选项都跑一遍反射。
                    if (!can && wanted != null && !_whyBlocked.ContainsKey(g)
                        && wanted.Contains(raw, StringComparer.OrdinalIgnoreCase))
                    {
                        string why = PrereqDiag.Explain(guard, it.Feature as BlueprintFeature);
                        _whyBlocked[g] = "（当时 lv" + guard.Progression.CharacterLevel + "）"
                                       + (string.IsNullOrEmpty(why) ? "无前置组件 —— 卡点不在 Prerequisites" : why);
                    }
                }
            }
            catch { }
        }

        /// <summary>职业链 GUID -> 可读名，日志用。</summary>
        internal static string PathName(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "(空)";
            try
            {
                var p = ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(guid);
                if (p != null) return p.name.Replace("CareerPath", "").Replace("_", "");
            }
            catch { }
            return guid.Substring(Math.Max(0, guid.Length - 6));
        }

        /// <summary>
        /// 补选：升完级之后，把方案要了但当时没点上、而**现在**前置已经满足的条目补授。
        ///
        /// 为什么需要：等级类前置卡在边界上。实测 PsyRating4（特级灵能者）要求
        /// 「39级 或 (34级 且 合法灵能者)」，而它在方案里的位置是 T2 rank 19 ——
        /// 那个选项弹出来时角色才 33 级，两条分支都差一点，于是永远选不上。
        /// 等整条链跑完卫兵是 55 级，39 级那条早就满足了。
        ///
        /// 这不是绕过门槛：只补**此刻确实合格**的条目，求值不出来的一律不补。
        /// 全是方案里已有的原版蓝图，不产生新 AssetId。
        /// </summary>
        private static void LateGrant(BaseUnitEntity guard, BuildPlans.Plan plan, string[] chain, int chainDepth)
        {
            if (plan == null || guard == null) return;
            try
            {
                int n = 0;
                int segs = Math.Min(chainDepth, chain.Length);
                for (int i = 0; i < segs; i++)
                {
                    Dictionary<int, List<string>> byRank;
                    if (!plan.Sel.TryGetValue(chain[i], out byRank)) continue;
                    foreach (var kv in byRank)
                        foreach (var g in kv.Value)
                        {
                            BlueprintFeature bp = null;
                            try { bp = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(g); } catch { }
                            if (bp == null) continue;
                            bool has = false;
                            try { has = guard.Facts.Contains(bp); } catch { }
                            if (has) continue;
                            if (!PrereqDiag.AllMet(guard, bp)) continue;
                            try
                            {
                                guard.Progression.Features.Add(bp);
                                n++;
                                Main.Log("    补选: " + BpName(bp) + "@" + PathName(chain[i]) + " r" + kv.Key
                                         + "（当时等级不够选不上，现在 lv" + guard.Progression.CharacterLevel + " 前置已满足）");
                            }
                            catch (Exception e) { Main.LogError("    补选失败 " + BpName(bp) + ": " + e.Message); }
                        }
                }
                if (n > 0) Main.Log("    补选合计 " + n + " 条");
            }
            catch (Exception e) { Main.LogError("补选异常: " + e.Message); }
        }

        private static PlanAudit VerifyPlan(BaseUnitEntity guard, BuildPlans.Plan plan, string[] chain, int chainDepth,
                                            Dictionary<string, int> reachedRank)
        {
            var audit = new PlanAudit();
            if (plan == null || guard == null) return audit;
            try
            {
                int total = 0, ok = 0;
                var missA = new List<string>();   // 从未出现
                var missB = new List<string>();   // 出现过但不可选
                var missC = new List<string>();   // 可选却没选上
                var missD = new List<string>();   // 该 rank 压根没走到
                var missAlt = new List<string>(); // 同 rank 的备选，兄弟已落地 ⇒ 不算缺

                int segs = Math.Min(chainDepth, chain.Length);
                for (int i = 0; i < segs; i++)
                {
                    Dictionary<int, List<string>> byRank;
                    if (!plan.Sel.TryGetValue(chain[i], out byRank)) continue;
                    int reached;
                    if (reachedRank == null || !reachedRank.TryGetValue(chain[i], out reached)) reached = 0;
                    foreach (var kv in byRank)
                    {
                        // 同一 rank 下如果已经有兄弟条目落地了，剩下没落的多半是**备选**而非缺失。
                        // 按段合成时一个槽位会从多个源各取一个候选（FirstOrSecondCareer 桶就是这么配的），
                        // PickOne 挑走能点的那个，另一个必然永远进不了候选池 —— 那是设计冗余，不是失败。
                        bool anyLanded = false;
                        foreach (var g0 in kv.Value)
                        {
                            try
                            {
                                var b0 = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(g0);
                                if (b0 != null && guard.Facts.Contains(b0)) { anyLanded = true; break; }
                            }
                            catch { }
                        }
                        foreach (var g in kv.Value)
                        {
                            total++;
                            BlueprintFeature bp = null;
                            try { bp = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(g); } catch { }
                            string nm = bp == null ? ("(解析不到 " + g.Substring(Math.Max(0, g.Length - 6)) + ")")
                                      : (string.IsNullOrEmpty(bp.Name) ? bp.name : bp.Name);
                            string where = "@" + PathName(chain[i]) + " r" + kv.Key;

                            bool has = false;
                            try { has = bp != null && guard.Facts.Contains(bp); } catch { }
                            if (has) { ok++; continue; }

                            // D 必须排在 A/B/C 前面：没走到的 rank 谈不上"没选上"
                            if (kv.Key > reached) { missD.Add(nm + where); continue; }

                            bool seen, selectable = false;
                            seen = _seenItems.TryGetValue(SeenKey(chain[i], kv.Key, g), out selectable);
                            if (!seen)
                            {
                                // 同 rank 已有兄弟落地 ⇒ 这条是没被选中的备选，不算缺
                                if (anyLanded && kv.Value.Count > 1) missAlt.Add(nm + where);
                                else missA.Add(nm + where);
                            }
                            else if (!selectable)
                            {
                                missB.Add(nm + where);
                                // 打的是**当场**记下来的求值结果，不是现在（55级）重算的
                                if (missB.Count <= 6)
                                {
                                    string why;
                                    if (_whyBlocked.TryGetValue(SeenKey(chain[i], kv.Key, g), out why))
                                        Main.Log("        └ " + nm + where + " 前置: " + why);
                                }
                            }
                            else missC.Add(nm + where);
                        }
                    }
                }

                audit.Total = total; audit.Ok = ok; audit.Unreached = missD.Count; audit.Alt = missAlt.Count;
                audit.MissA = missA.Count; audit.MissB = missB.Count; audit.MissC = missC.Count;

                Main.Log("    方案核对: 共 " + total + " 条，应生效 " + audit.Applicable
                         + "（等级没到 " + missD.Count + " 条不算），已生效 " + ok
                         + " = " + audit.Percent + "%"
                         + "（从未出现 " + missA.Count + " / 出现但不可选 " + missB.Count + " / 可选却没选 " + missC.Count + "）");
                if (missA.Count > 0)
                    Main.Log("      A 从未出现在候选里（职业链不符/种族起源门控/缺前置特性）: " + Join(missA, 8));
                if (missB.Count > 0)
                    Main.Log("      B 出现过但一直不可选（前置没点）: " + Join(missB, 8));
                if (missD.Count > 0)
                    Main.Log("      D 等级没到该 rank（不是失败，是经验/等级上限）: " + Join(missD, 6));
                if (missAlt.Count > 0)
                    Main.Log("      备选未用（同 rank 已落一条，多源合成的正常冗余）: " + Join(missAlt, 6));
                if (missC.Count > 0)
                    Main.LogError("      C 可选却没选上 —— 这是我们的 bug: " + Join(missC, 8));

                audit.Detail = (missA.Count > 0 ? "A:" + Join(missA, 4) + " " : "")
                             + (missB.Count > 0 ? "B:" + Join(missB, 3) + " " : "")
                             + (missC.Count > 0 ? "C:" + Join(missC, 4) : "");
            }
            catch (Exception e) { Main.LogError("方案核对失败: " + e.Message); }
            return audit;
        }

        /// <summary>ChargenPath_ForPregens —— 原版蓝图，只读。
        /// StartGame_Pregen_* 的家园世界/背景就是在这条 path 上选出来的，
        /// 选择记录留在 Progression.m_Selections 里，可以按它精确找回旧的那一条。</summary>
        private const string ChargenPathForPregens = "68eaf96bad9748739ca44fedc7b5c7c4";

        private static string BpName(BlueprintFeature f)
        {
            if (f == null) return "(null)";
            return string.IsNullOrEmpty(f.Name) ? f.name : f.Name;
        }

        /// <summary>
        /// 授予一个家园世界 / 背景，并顶掉模板出厂自带的同组那一个。
        ///
        /// 认组是按 FeatureSelectionData.Selection.Group 来的，不靠"在不在候选池里"反推 ——
        /// SpaceMarine_Occupation 同时挂在家园世界池和背景池里，反推会认错组、删错东西。
        /// </summary>
        /// <summary>
        /// 这个特性会不会给单位提供外观（衣服/身体部件）。
        ///
        /// AddKingmakerEquipmentEntity.OnDeactivate 会把自己那份 EquipmentEntityLink
        /// 从 avatar 上摘掉，OnActivateOrPostLoad 再把新的加回来。
        /// 所以"换背景"时如果换入的那个没有实际外观实体（_NoKEE 变体就是这样：
        /// 组件在、m_EquipmentEntity 为空），就会**摘了不补** —— 卫兵直接没模型。
        /// v0.9.1 实测：铁壁/磐石/寂静之眼三个精英全中。
        /// </summary>
        private static bool HasVisual(BlueprintFeature f)
        {
            if (f == null) return false;
            try
            {
                foreach (var c in f.GetComponents<Kingmaker.UnitLogic.FactLogic.AddKingmakerEquipmentEntity>())
                    if (c != null && c.EquipmentEntity != null) return true;
            }
            catch { }
            return false;
        }

        private static void GrantChargen(BaseUnitEntity guard, string guid, FeatureGroup group, string label)
        {
            if (string.IsNullOrEmpty(guid)) return;
            try
            {
                var want = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(guid);
                if (want == null) { Main.Log("    " + label + "解析不到: " + guid); return; }
                if (guard.Facts.Contains(want)) return;          // 已经就是这个，不动

                var path = ResourcesLibrary.TryGetBlueprint<BlueprintPath>(ChargenPathForPregens);
                if (path != null)
                {
                    // 先落地成 List 再改：ReplaceFeature 会写 m_Selections，边枚举边改会抛。
                    var sels = guard.Progression.GetSelectionsByPath(path).ToList();
                    foreach (var s in sels)
                    {
                        if (s.Selection == null || s.Selection.Group != group) continue;
                        if (s.Feature == null || s.Feature == want) continue;
                        // ★ 换掉会不会把模型弄没 ★
                        // 旧的提供外观、新的不提供 —— 删了就补不回来，卫兵变透明人。
                        // 这种情况宁可留着两个背景（只是显示重复 + 天赋池多一份），
                        // 也不能让卫兵没模型。家园世界不带外观，永远走替换。
                        if (HasVisual(s.Feature) && !HasVisual(want))
                        {
                            guard.Progression.Features.Add(want);
                            Main.Log("    追加" + label + ": " + BpName(want)
                                     + "（保留原有的「" + BpName(s.Feature) + "」——它提供角色外观，"
                                     + "换入的这个没有，删了会丢模型）");
                            return;
                        }
                        // 原版 API（PartUnitProgression.ReplaceFeature）：
                        // 删旧 Feature + 加新 Feature + 改写 m_Selections 里那条记录，
                        // 三件事一起做，状态不会残缺。全是已有蓝图，不产生新 AssetId。
                        guard.Progression.ReplaceFeature(s.Feature, want);
                        Main.Log("    替换" + label + ": " + BpName(s.Feature) + " -> " + BpName(want));
                        return;
                    }
                }
                // DLC3_DL_Guard_* 这些普通卫兵单位出厂没有家园世界/背景，走这条：直接加。
                guard.Progression.Features.Add(want);
                Main.Log("    授予" + label + ": " + BpName(want));
            }
            catch (Exception e) { Main.LogError("    授予" + label + "失败 " + guid + ": " + e.Message); }
        }

        /// <summary>授予一个普通前置特性（灵能学派之类），已有就跳过。</summary>
        private static void GrantPlain(BaseUnitEntity guard, string guid, string label)
        {
            if (string.IsNullOrEmpty(guid)) return;
            try
            {
                var f = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(guid);
                if (f == null) { Main.Log("    " + label + "解析不到: " + guid); return; }
                if (guard.Facts.Contains(f)) return;
                guard.Progression.Features.Add(f);
                Main.Log("    授予" + label + ": " + BpName(f));
            }
            catch (Exception e) { Main.LogError("    授予" + label + "失败 " + guid + ": " + e.Message); }
        }

        private static string Join(List<string> l, int max)
        {
            if (l.Count <= max) return string.Join("、", l.ToArray());
            return string.Join("、", l.GetRange(0, max).ToArray()) + " …等 " + l.Count + " 条";
        }

        private static void PickOne(SelectionStateFeature f, BuildPlans.Plan plan, string pathGuid,
                                    ref int planHits, ref int fallbacks,
                                    string[] keyTalents = null, string[] attrPriority = null)
        {
            try
            {
                var items = f.Items;
                if (items.Count == 0) return;

                if (plan != null)
                {
                    var cands = plan.Candidates(pathGuid, f.PathRank);
                    if (cands != null)
                    {
                        for (int ci = 0; ci < cands.Count; ci++)
                        {
                            for (int ii = 0; ii < items.Count; ii++)
                            {
                                var it = items[ii];
                                if (it.Feature == null) continue;
                                if (!string.Equals(it.Feature.AssetGuid.ToString(), cands[ci], StringComparison.OrdinalIgnoreCase)) continue;
                                if (!f.CanSelect(it)) continue;
                                f.Select(it); planHits++; return;
                            }
                        }
                    }
                }

                // ── 回退：不再"取第一个"，而是按优先级挑 ──
                // 这样"只给了关键天赋、没有完整点法"的攻略（比如士兵赏金）也能用，
                // 同时其他分型那 3-13% 的回退也会挑到更合适的东西。

                // ① 关键天赋列表
                if (keyTalents != null)
                    for (int k = 0; k < keyTalents.Length; k++)
                        for (int ii = 0; ii < items.Count; ii++)
                        {
                            var it = items[ii];
                            if (it.Feature == null || !f.CanSelect(it)) continue;
                            if (string.Equals(it.Feature.AssetGuid.ToString(), keyTalents[k], StringComparison.OrdinalIgnoreCase))
                            { f.Select(it); fallbacks++; LogFallback(f, it, "关键天赋"); return; }
                        }

                // ② 属性优先级 —— 属性天赋的内部名有固定形状 <属性>StatAdvancement<N>
                //    （如 BallisticSkillStatAdvancement1），按声明顺序匹配
                if (attrPriority != null)
                    for (int k = 0; k < attrPriority.Length; k++)
                    {
                        string want = attrPriority[k] + "StatAdvancement";
                        for (int ii = 0; ii < items.Count; ii++)
                        {
                            var it = items[ii];
                            if (it.Feature == null || !f.CanSelect(it)) continue;
                            if (it.Feature.name != null
                                && it.Feature.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                            { f.Select(it); fallbacks++; LogFallback(f, it, "属性优先"); return; }
                        }
                    }

                // ③ 实在没有偏好才取第一个
                for (int ii = 0; ii < items.Count; ii++)
                {
                    var it = items[ii];
                    if (!f.CanSelect(it)) continue;
                    f.Select(it); fallbacks++; LogFallback(f, it, "无偏好");
                    return;
                }
            }
            catch (Exception e) { Main.LogError("选天赋失败: " + e.Message); }
        }

        /// <summary>回退明细 —— 只打前若干条，用来定位"为什么不到 100%"。</summary>
        private static int _fbLogged;
        public static void ResetFallbackLog() { _fbLogged = 0; }
        private static void LogFallback(SelectionStateFeature f, Kingmaker.UnitLogic.Levelup.Selections.Feature.FeatureSelectionItem it, string why)
        {
            if (_fbLogged >= 8) return;
            _fbLogged++;
            try
            {
                string picked = it.Feature != null
                    ? (string.IsNullOrEmpty(it.Feature.Name) ? it.Feature.name : it.Feature.Name) : "?";
                var others = new List<string>();
                for (int i = 0; i < f.Items.Count && others.Count < 5; i++)
                {
                    var x = f.Items[i];
                    if (x.Feature == null) continue;
                    others.Add((string.IsNullOrEmpty(x.Feature.Name) ? x.Feature.name : x.Feature.Name)
                               + (f.CanSelect(x) ? "" : "(不可选)"));
                }
                Main.Log("      [回退/" + why + "] rank" + f.PathRank + " 选了「" + picked
                         + "」  可选项: " + string.Join(", ", others.ToArray()));
            }
            catch { }
        }
    }


    /// <summary>
    /// 士气自证：轮询队伍士气组，一变就记一笔，同时带上卫兵状态快照。
    /// 不打 Harmony —— MomentumGroup.Momentum 是 public get，直接读。
    /// 目的：证明卫兵受伤/倒地时队伍士气**没有**变化。
    /// </summary>
    public static class MomentumWatch
    {
        private static int _lastParty = int.MinValue;
        private static bool _wasInCombat;

        public static void Tick()
        {
            try
            {
                var tc = Game.Instance != null ? Game.Instance.TurnController : null;
                if (tc == null || tc.MomentumController == null) return;

                var groups = tc.MomentumController.Groups;
                if (groups == null || groups.Count == 0)
                {
                    if (_wasInCombat) { Main.Log("[士气] 战斗结束，停止监视"); _wasInCombat = false; _lastParty = int.MinValue; }
                    return;
                }

                MomentumGroup party = null;
                foreach (var g in groups) { if (g != null && g.IsParty) { party = g; break; } }
                if (party == null) return;

                if (!_wasInCombat) { Main.Log("[士气] 进入战斗，起始队伍士气=" + party.Momentum); _wasInCombat = true; _lastParty = party.Momentum; return; }

                if (party.Momentum != _lastParty)
                {
                    Main.Log("[士气] 队伍 " + _lastParty + " -> " + party.Momentum + "   卫兵: " + RetinueTest.GuardStates());
                    _lastParty = party.Momentum;
                }
            }
            catch { /* 监视失败不能影响主流程 */ }
        }
    }
}
