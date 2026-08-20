using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Root;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Progression.Paths;

namespace DynastyRetinue
{
    /// <summary>
    /// 批量试算：把 RTAutoBuilder 的**每一套**加点方案在一个临时卫兵身上跑满，
    /// 一次问清三件事 ——
    ///   1. 这套方案的链能不能把卫兵推到 55 级（有没有中途卡住）
    ///   2. 方案里的天赋 GUID 有多少能真的被卫兵选中（命中 vs 回退）
    ///   3. 最终 facts 数 —— 命中率高的方案 facts 应该更"成套"
    ///
    /// 为什么必须进游戏跑：方案是给主角/队友存的，里面可能有**队友专属天赋**
    /// （比如 Argenta 的独有天赋），普通卫兵的 Items 里根本不会出现。
    /// 这一点静态读 json 看不出来，只能实测。
    ///
    /// 每套跑完立刻销毁，不留任何痕迹。
    /// </summary>
    public static class PlanProbe
    {
        private const string Ascension  = "bcefe9c41c7841c9a99b1dbac1793025";
        private const string ProbeUnit  = "02094127ee4c402fbedbce1aff086e62";

        public static void Run()
        {
            try
            {
                var game = Game.Instance;
                var leader = game != null && game.Player != null ? game.Player.MainCharacterEntity : null;
                if (leader == null) { Main.LogError("请先进入游戏内。"); return; }
                var state = game.LoadedAreaState != null ? game.LoadedAreaState.MainState : null;
                if (state == null) { Main.LogError("LoadedAreaState.MainState 为空。"); return; }

                var plans = BuildPlans.All;
                if (plans.Count == 0) { Main.LogError("没有 RTAutoBuilder 方案，路径: " + BuildPlans.SourcePath); return; }

                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(ProbeUnit);
                if (bp == null) { Main.LogError("探测单位蓝图丢失。"); return; }

                Main.Log("========== 批量试算 " + plans.Count + " 套方案 ==========");
                Main.Log("列: 序号 | 链 | 等级 | 选择点(其中无可选项) | 命中/回退 | facts | 结论");
                Main.Log("注意: 一条 55 级链应有几十个选择点。若 选择点 很少或 空 占多数，");
                Main.Log("      说明卫兵根本没拿到天赋选择权 —— 那才是要解决的问题，命中率是次要的。");

                var rows = new List<string>();
                for (int i = 0; i < plans.Count; i++)
                {
                    var pl = plans[i];
                    BaseUnitEntity u = null;
                    try
                    {
                        string t1 = PathName(pl.First), t2 = PathName(pl.Second);
                        if (pl.First == null || pl.Second == null
                            || ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(pl.First) == null
                            || ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(pl.Second) == null)
                        { rows.Add(Pad(i) + Pad2(pl.Display) + "  链无效（career path 解析不到）"); continue; }

                        u = game.EntitySpawner.SpawnUnit(bp, leader.Position, Quaternion.identity, state);
                        if (u == null) { rows.Add(Pad(i) + Pad2(pl.Display) + "  spawn 失败"); continue; }
                        u.Faction.Set(BlueprintRoot.Instance.PlayerFaction);
                        u.CombatGroup.Id = "kgd.planprobe";
                        // 刻意不预灌经验 —— ApplyChain 的经验节流会逐级给，
                        // 否则 ExperienceLevel 一步到 55，LevelUpManager 会一次推满整条 path，
                        // 中间 rank 的选择点全部跳过（v0.2.8 实测）。

                        var arch = new ChainProbe.Archetype(pl.Display, pl.First, pl.Second, Ascension);
                        arch.PlanName = pl.Display;

                        Archetypes.ApplyChain(u, arch, 55, 3, true, true);   // 试算：允许超经验预算，目的是跑满整条链

                        int lv = u.Progression.CharacterLevel;
                        int hits = Archetypes.LastPlanHits, fb = Archetypes.LastFallbacks;
                        int seen = Archetypes.LastSeen, noOpt = Archetypes.LastNoOption;
                        int facts = 0; try { facts = u.Facts.List.Count; } catch { }
                        int total = hits + fb;
                        int pct = total > 0 ? (int)(100.0 * hits / total) : 0;

                        string verdict;
                        if (lv < 55) verdict = "卡在 lv" + lv + "，链走不满";
                        else if (pct >= 90) verdict = "可直接用";
                        else if (pct >= 60) verdict = "基本可用，有专属天赋回退";
                        else verdict = "命中率低，多半是队友专属天赋";

                        rows.Add(Pad(i) + Pad2(t1 + "->" + t2) + " lv" + lv
                                 + "  选择点" + seen + "(空" + noOpt + ")"
                                 + " 命中" + hits + "/回退" + fb
                                 + "  facts" + facts + "  " + verdict);
                    }
                    catch (Exception e) { rows.Add(Pad(i) + "异常: " + e.Message); }
                    finally { if (u != null) { try { game.EntityDestroyer.Destroy(u); } catch { } } }
                }

                foreach (var r in rows) Main.Log("  " + r);
                try { game.EntityDestroyer.Tick(); } catch { }
                Main.Log("========== 批量试算结束 ==========");
                Main.Log("说明: 命中=方案里的天赋真被选中；回退=方案指定的选项卫兵选不了，退回第一个可选项");
            }
            catch (Exception e) { Main.LogError(e); }
        }

        private static string Pad(int i) { var s = "[" + i + "]"; return s.PadRight(5); }
        private static string Pad2(string s)
        {
            if (s == null) s = "";
            if (s.Length > 26) s = s.Substring(0, 26);
            return s.PadRight(28);
        }

        private static string PathName(string guid)
        {
            if (guid == null) return "-";
            var p = ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(guid);
            return p != null ? p.name.Replace("CareerPath", "").Replace("_", "") : guid.Substring(0, 6);
        }
    }
}