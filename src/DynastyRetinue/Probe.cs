using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Root;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Levelup;
using Kingmaker.UnitLogic.Levelup.Components;
using Kingmaker.UnitLogic.Progression.Paths;

namespace DynastyRetinue
{
    /// <summary>
    /// 批量探测：一次性回答两个问题 ——
    ///   A. 各候选单位的运行时属性（brain / 等级 / 装备 / faction）
    ///   B. 各条 career path 在该单位上的等级天花板（CanUpgradePath 何时 false）
    /// 因为 blueprints-pack.bbp 是二进制、蓝图内容读不到，只能这样实测。
    /// 探测完立刻销毁，不留痕迹。
    /// </summary>
    public static class Probe
    {
        // 候选单位：名字 -> AssetId
        public static readonly (string Name, string Id)[] Candidates =
        {
            ("Guard_Ranged_Ally",   "02094127ee4c402fbedbce1aff086e62"),
            ("Guard_Melee_Ally",    "270b3e09cf424209b126b236f1655108"),
            ("Guard_Sniper",        "36e39788d1c648c8a75fbf36371f35f9"),
            ("Arbites_Shield",      "9df28442ff2e440cb051ed2403dc347e"),
            ("Arbites_Sniper",      "8104e86cc05c42bfbb2c11c70332ef06"),
            ("Sororitas_HBolter",   "5fc80452fb6a4e2db02cd0a305715446"),
            ("Sororitas_HFlamer",   "3e32b7ca9b3644c284ba80ed026c3b86"),
            ("Sororitas_Melta",     "2cf75c27e6d34681ab623101b0be1135"),
            ("Inquisitor",          "5bc8b3a8fb834977a3692a2325aff0f6"),
            ("Guard_Melee",         "96d02f9489e04ca3b6e5333ae2d7ccd8"),
            ("Guard_Ranged",        "9f3aa4b001fe44abba41117d588410ac"),
            ("NavisNobilite_Lasgun","89e72b4763584b20bba3eb274002e92a"),
            ("OfficersDeckGuard",   "1fb60c0ef5fe459980c34a271dfad088"),
            ("Guard_Serjant_Male",  "64140ab047ed4371a9bf17a6e418d55e"),
            // ---- v0.2.2 扩充：按 brain 类型找配套单位 ----
            // 天赋链决定"能做什么"，brain 决定"实际会做什么"。近战 build 挂在
            // Ranged_Brain 的单位上，AI 照样站着放枪 —— 所以分型必须配对到合适的蓝图。
            // 单位→brain 的映射只能实测，units.tsv 里没有这一列。
            ("Guard_Male",              "e4d4808bbd6a49dea7dad4ac847bdf70"),
            ("OfficersDeckGuard2",      "563ec63f082f413db7618b2e95bc7f36"),
            ("OfficersDeckGuardAstro",  "1df7ad7561ca47a08a2c2b0a8152871e"),
            ("OfficersDeckGuard_Baton", "7fbab4211226436a91c04233734e5b25"),
            ("OfficersDeckGuardMelee",  "0abae138381842f4891e534ec540fdc8"),
            ("NavisNobiliteLasgun_Guard","d579009206214520b044753a18c16e73"),
            ("NavisNobiliteShotgun_Guard","c42ea0a06cca4670882f218424f5ceb3"),
            // 机械教
            ("Xenarite_ElectroPriest",  "ab131771270542b69fb7a687062b39c0"),
            ("Opticon22_TechPriest",    "f9de0ceff42349a2867936158a859390"),
            ("CombatServitorMelta_Friendly","3c16f2aeebc54cf1927062857252e95f"),
            ("GuardianCombatServitor",  "454847134b48402792be9798bf92b0ca"),
            // 灵能
            ("StartGame_Pregen_Psyker", "89450677ca1d4b93a28c81f7afadf77c"),
            ("AdeptPsykerQA_lvl4",      "0fc6013c7f6a4a0890f2a90056cf8c3b"),
            ("FighterPsykerQA_lvl15",   "18369fc65433450e98f175c7dae89f5e"),
            // 狙击 / 政委 / 灵族
            ("OP_Pirate_Sniper",        "ac89b3a945be44bfab0ee64d50781e33"),
            ("Commissar_lv29_Pregen",   "829aa2bfee6c4190aac0d74d957b7129"),
            ("Eldar_StormGuardianMelee","32b0ca6bbd284e309f44aa1684c9d15d"),
            ("Eldar_GuardianDefender",  "10d4223a1dab4deeb8cc044203038023"),
            // 阿贝拉德先锋（不同等级，看 brain 是否为近战）
            ("Abelard_Vanguard_lv38",   "abdab891e05f4f5b8010cf8c3d11d8c5"),
            ("Abelard_Vanguard_lv45",   "edb5f57636ab4776b4e2215c34772c10"),
        };

        /// <summary>只探属性，不试 career path。快。</summary>
        public static void ProbeUnits()
        {
            Run(false);
        }

        /// <summary>属性 + 逐条 career path 试天花板。慢，但一次问清。</summary>
        public static void ProbeUnitsAndPaths()
        {
            Run(true);
        }

        private static void Run(bool tryPaths)
        {
            try
            {
                var game = Game.Instance;
                var leader = game?.Player?.MainCharacterEntity;
                if (leader == null) { Main.LogError("请先进入游戏内。"); return; }
                var state = game.LoadedAreaState?.MainState;
                if (state == null) { Main.LogError("LoadedAreaState.MainState 为空。"); return; }

                int rtXp = leader.Progression.Experience;
                Main.Log("========== 批量探测开始 ==========");
                Main.Log($"主角经验 = {rtXp}，候选 {Candidates.Length} 个，试 path = {tryPaths}");

                var paths = tryPaths ? CollectCareerPaths() : new List<BlueprintCareerPath>();
                if (tryPaths) Main.Log($"找到 {paths.Count} 条 career path");

                foreach (var c in Candidates)
                {
                    BaseUnitEntity u = null;
                    try
                    {
                        var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(c.Id);
                        if (bp == null) { Main.Log($"[{c.Name}] 蓝图找不到: {c.Id}"); continue; }

                        u = game.EntitySpawner.SpawnUnit(bp, leader.Position, Quaternion.identity, state);
                        if (u == null) { Main.Log($"[{c.Name}] spawn 返回 null"); continue; }
                        u.Faction.Set(BlueprintRoot.Instance.PlayerFaction);
                        u.CombatGroup.Id = "kgd.probe";

                        var lim = u.OriginalBlueprint?.GetComponent<CharacterLevelLimit>();
                        Main.Log(
                            $"[{c.Name}] {c.Id}\n" +
                            $"    brain={u.Brain?.Blueprint?.name ?? "无"}\n" +
                            $"    {DescribeBrain(u)}\n" +
                            $"    基础等级={u.Progression?.CharacterLevel} 基础经验={u.Progression?.Experience}\n" +
                            $"    CharacterLevelLimit={(lim == null ? "无" : lim.LevelLimit.ToString())}\n" +
                            $"    武器={DescribeWeapons(u)}");

                        // path 探测另起全新单位，这里先销毁当前这个再测
                    }
                    catch (Exception e) { Main.LogError($"[{c.Name}] 探测异常: {e.Message}"); }
                    finally
                    {
                        if (u != null) { try { game.EntityDestroyer.Destroy(u); } catch { } }
                    }
                    if (tryPaths) ProbePaths(c.Name, c.Id, rtXp, paths, leader, state);
                }
                Main.Log("========== 批量探测结束 ==========");
            }
            catch (Exception e) { Main.LogError(e); }
        }

        /// <summary>
        /// 对一个候选单位逐条试 career path，找等级天花板。
        /// 每条 path 都用**全新生成的单位**测，避免前一条的改动污染后一条。
        /// </summary>
        private static void ProbePaths(string name, string assetId, int rtXp,
                                       List<BlueprintCareerPath> paths,
                                       BaseUnitEntity leader, Kingmaker.EntitySystem.SceneEntitiesState state)
        {
            var game = Game.Instance;
            var results = new List<string>();

            foreach (var p in paths)
            {
                BaseUnitEntity u = null;
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(assetId);
                    if (bp == null) { results.Add($"{p.name}=蓝图丢失"); continue; }
                    u = game.EntitySpawner.SpawnUnit(bp, leader.Position, Quaternion.identity, state);
                    if (u == null) { results.Add($"{p.name}=spawn失败"); continue; }
                    u.Faction.Set(BlueprintRoot.Instance.PlayerFaction);
                    u.CombatGroup.Id = "kgd.probe";

                    u.Progression.AdvanceExperienceTo(rtXp, false);

                    // CanUpgradePath 全代码库只有 2 个调用点，都在 UI；LevelUpManager 不调它。
                    // 所以它是"按钮能不能点"的判定，不是引擎约束 —— 只记录，不阻断。
                    bool uiSaysOk = u.Progression.CanUpgradePath(p);

                    int before = u.Progression.CharacterLevel;
                    int n = 0, stuck = 0, lastLv = before;
                    int rankBefore = u.Progression.GetPathRank(p);
                    while (n < 60 && u.Progression.CanLevelUp)
                    {
                        using (var mgr = new LevelUpManager(u, p, true, u.Progression.CharacterLevel + 1))
                        {
                            // 必须处理必选项，否则升级提交不了 —— 这是上一版的 bug
                            foreach (var sel in mgr.Selections)
                            {
                                var f = sel as Kingmaker.UnitLogic.Levelup.Selections.Feature.SelectionStateFeature;
                                if (f != null && f.CanSelectAny)
                                {
                                    var pick = f.Items.FirstOrDefault(x => f.CanSelect(x));
                                    if (pick != null) f.Select(pick);
                                }
                            }
                        }
                        n++;
                        // 连续 3 轮等级不涨就判定卡住，避免空转满 60 次
                        if (u.Progression.CharacterLevel == lastLv) { stuck++; if (stuck >= 3) break; }
                        else { stuck = 0; lastLv = u.Progression.CharacterLevel; }
                    }
                    results.Add($"{p.name}:{(uiSaysOk ? "" : "[UI否]")} lv{before}->{u.Progression.CharacterLevel} rank{rankBefore}->{u.Progression.GetPathRank(p)}/{p.Ranks}");
                }
                catch (Exception e) { results.Add($"{p.name}=异常:{e.GetType().Name}"); }
                finally { if (u != null) { try { game.EntityDestroyer.Destroy(u); } catch { } } }
            }
            Main.Log($"    [{name}] path: " + string.Join(" | ", results));
        }
        /// <summary>
        /// 原版全部 19 条 BlueprintCareerPath（从 bp_index.tsv 导出）。
        /// 排除 StarshipCareerPath —— 它是舰船专用，走 StarshipXPTable，与步兵无关。
        /// Tier1 Ranks=15 / Tier2 Ranks=20 / Tier3 Ranks=20，15+20+20=55 = XP 表上限。
        /// </summary>
        public static readonly (string Name, string Id)[] AllPaths =
        {
            ("Soldier(T1)",        "06f4f78a9c1a472b85cd79a9a142153d"),
            ("Fighter(T1)",        "974496d72fbe4329b438ee15cf004bd2"),
            ("Adept(T1)",          "1529e5a0e7844bf3bb8d0cc0501264d4"),
            ("Leader(T1)",         "33725d84e95e4323ac46d8fbf899b250"),
            ("Reaper(T1,DLC1)",    "dd6948ee596346a69733d0bb107c2f42"),
            ("Veteran(T2)",        "651684417def4c258c72ba91f481b817"),
            ("Vanguard(T2)",       "fec9cd09f11b4615b7a17f441350d2d4"),
            ("Hunter(T2)",         "6f276e8a8e2c4a548504ae39d2a7f22a"),
            ("Assassin(T2)",       "7b90955673a54136be9c11743943fdfe"),
            ("Tactician(T2)",      "604fa184d7d944c8ae5965f9700782b5"),
            ("Strategist(T2)",     "a31b390cabe7464fbfd0e1ba53c4112f"),
            ("Master(T2,DLC2)",    "21b0fc8cfbe940ecbef0114d5d27b44a"),
            ("Executioner(T2,DLC1)","d6c0498a227040c891e4e2703eb55c13"),
            ("Ascension(T3)",      "bcefe9c41c7841c9a99b1dbac1793025"),
            ("Ascension_Fake(T3)", "296e1508e4dd4c82b331758bb469599a"),
            ("Test1(T1)",          "513d6c16955944aaa7b78762a8754e89"),
            ("Test2(T1)",          "bd52fb9c5b02412097908deed9a1b3c5"),
        };

        private static List<BlueprintCareerPath> CollectCareerPaths()
        {
            var list = new List<BlueprintCareerPath>();
            foreach (var e in AllPaths)
            {
                var p = ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(e.Id);
                if (p != null) list.Add(p);
                else Main.Log($"  career path 缺失: {e.Name} {e.Id}");
            }
            return list;
        }
        /// <summary>
        /// brain 会不会用我们练出来的技能 —— 这才是分型能不能落地的关键。
        ///
        /// BlueprintBrain.GetCustomAbilitySettings（BlueprintBrain.cs）：
        ///     if (m_UseOnlyListedAbilities &amp;&amp; !AbilityPriorityOrder.Order.Any(o =&gt; o.Abilities.Contains(ability)))
        ///         return AbilitySettings.UnplayableSetting;
        /// 即：该 flag 为 true 时，**不在 AbilityPriorityOrder 里的技能一律判定为不可用**，
        /// career 链练出来的东西全部白练。为 false 时未列出的技能仍会被考虑，只是优先级靠后。
        ///
        /// 该字段是 [SerializeField] private，只能反射读。
        /// </summary>
        private static string DescribeBrain(BaseUnitEntity u)
        {
            try
            {
                var bp = u.Brain?.Blueprint as Kingmaker.AI.Blueprints.BlueprintBrain;
                if (bp == null) return "(非 BlueprintBrain 或无 brain)";

                bool onlyListed = false; string flagState = "?";
                try
                {
                    var fi = HarmonyLib.AccessTools.Field(typeof(Kingmaker.AI.Blueprints.BlueprintBrain), "m_UseOnlyListedAbilities");
                    if (fi != null) { onlyListed = (bool)fi.GetValue(bp); flagState = onlyListed.ToString(); }
                }
                catch { }

                int listed = 0;
                try { if (bp.AbilityPriorityOrder.Order != null) listed = bp.AbilityPriorityOrder.Order.Length; } catch { }
                int moveInf = 0;
                try { if (bp.MovementInfluentAbilities != null) moveInf = bp.MovementInfluentAbilities.Length; } catch { }
                string melee = "?";
                try { melee = bp.MeleeBrainType.ToString(); } catch { }

                string verdict = (flagState == "?") ? "未知"
                                 : (onlyListed ? "★只用列表内技能 —— 练的天赋会白费" : "会考虑未列出的技能 —— 天赋能用上");
                return "[UseOnlyListed=" + flagState + " 列表" + listed + "条 移动相关" + moveInf
                       + " 近战型=" + melee + "  " + verdict + "]";
            }
            catch (Exception e) { return "(brain 读取失败: " + e.GetType().Name + ")"; }
        }
        private static string DescribeWeapons(BaseUnitEntity u)
        {
            try
            {
                var body = u.Body;
                if (body == null) return "无 Body";
                var names = new List<string>();
                foreach (var slot in body.EquipmentSlots)
                {
                    var it = slot?.MaybeItem;
                    if (it != null) names.Add(it.Blueprint?.name ?? "?");
                }
                return names.Count == 0 ? "空" : string.Join(", ", names);
            }
            catch (Exception e) { return "读取失败:" + e.GetType().Name; }
        }
    }
}
