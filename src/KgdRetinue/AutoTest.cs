using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;

namespace KgdRetinue
{
    /// <summary>
    /// 一键全测：把每个分型的普通卫兵 + 全部精英一次生成出来，
    /// 收集加点命中率/属性/装备，写成对比表，最后自动遣散。
    ///
    /// 存在的理由：之前每验一个改动都要手点十几次（选分型 → 生成 → 再生成 → Dump → 遣散），
    /// 一轮下来五分钟，而且容易漏掉某个分型。现在一个按钮跑完。
    ///
    /// 注意它会**临时**解除数量上限和精英解锁条件，跑完恢复 —— 否则
    /// 九个精英根本生成不出来（默认 T3 上限 6 名、精英还要求该路线先有卫兵到 T3）。
    /// </summary>
    public static class AutoTest
    {
        public sealed class Row
        {
            public string Arch, Name, Unit, Plan;
            public bool IsElite;
            public int Level, Facts, SelSeen, SelHit, SelFallback;
            // 方案落实情况（真正该看的口径）：应生效多少、落实多少、缺口构成
            public int PlanTotal, PlanApplicable, PlanOk, PlanPct, MissA, MissB, MissC, Unreached;
            public string MissDetail = "";
            public string Stats = "", Gear = "", Brain = "";
        }

        private static string OutPath
        {
            get { return Path.Combine(Main.ModEntry != null ? Main.ModEntry.Path : ".", "autotest.tsv"); }
        }

        public static void RunAll()
        {
            var game = Game.Instance;
            var leader = game != null && game.Player != null ? game.Player.MainCharacterEntity : null;
            if (leader == null) { Main.LogError("请先进入游戏内。"); return; }

            // 备份并临时放开限制
            bool oldUnlockTier = Main.Settings.UnlockTierLimits;
            bool oldUnlockElite = Main.Settings.UnlockEliteLimit;
            bool oldIgnoreUnlock = Main.Settings.EliteIgnoreUnlock;
            Main.Settings.UnlockTierLimits = true;
            Main.Settings.UnlockEliteLimit = true;
            Main.Settings.EliteIgnoreUnlock = true;

            var rows = new List<Row>();
            try
            {
                Main.Log("================ 一键全测开始 ================");
                Main.Log("先清场……");
                RetinueRegistry.DismissAll();

                var archs = Archetypes.All;
                for (int ai = 0; ai < archs.Length; ai++)
                {
                    var a = archs[ai];

                    // 普通卫兵：强制 eliteOverride=null 拿不到，只能靠"精英已全部生成"来让 NextElite 返回 null。
                    // 所以顺序是先精英后普通 —— 但那样普通卫兵会被算进精英数。
                    // 干脆直接调低层：普通卫兵用分型的 unit 蓝图，精英各自指定。
                    Main.Log("---- 分型 " + ai + " " + a.Name + " ----");

                    // 精英逐个
                    if (a.Elites != null)
                        for (int ei = 0; ei < a.Elites.Length; ei++)
                        {
                            var g = RetinueTest.SpawnOne(ai, a.Elites[ei], true);
                            if (g != null) rows.Add(Collect(a, a.Elites[ei], g, true));
                        }

                    // 普通卫兵（此时该分型精英已全生成，NextElite 返回 null）
                    var n = RetinueTest.SpawnOne(ai, null, true, true);   // forceNormal
                    if (n != null) rows.Add(Collect(a, null, n, false));
                }

                Write(rows);
                Report(rows);
            }
            catch (Exception e) { Main.LogError("一键全测异常: " + e); }
            finally
            {
                Main.Settings.UnlockTierLimits = oldUnlockTier;
                Main.Settings.UnlockEliteLimit = oldUnlockElite;
                Main.Settings.EliteIgnoreUnlock = oldIgnoreUnlock;
                Main.Log("清场……");
                try { RetinueRegistry.DismissAll(); } catch { }
                Main.Log("================ 一键全测结束 ================");
            }
        }

        private static Row Collect(ChainProbe.Archetype a, ChainProbe.EliteDef ed, BaseUnitEntity g, bool elite)
        {
            var r = new Row
            {
                Arch = a.Name,
                Name = elite && ed != null ? ed.Name : "（普通）",
                IsElite = elite,
                // 跟 ApplyChain 的取用规则保持一致：自带 chain 但没有自己的 plan 的精英
                // **不**继承分型方案（否则报表会谎报一个它根本没跑的方案）
                Plan = (elite && ed != null)
                     ? (!string.IsNullOrEmpty(ed.PlanName) ? ed.PlanName
                        : ((ed.Chain != null && ed.Chain.Length > 0) ? "（无·自带链）" : a.PlanName))
                     : a.PlanName,
                // ApplyChain 把上一次统计留在这几个静态字段里
                SelSeen = Archetypes.LastSeen,
                SelHit = Archetypes.LastPlanHits,
                SelFallback = Archetypes.LastFallbacks,
            };
            var au = Archetypes.LastAudit;
            if (au != null)
            {
                r.PlanTotal = au.Total; r.PlanApplicable = au.Applicable; r.PlanOk = au.Ok;
                r.PlanPct = au.Percent; r.Unreached = au.Unreached;
                r.MissA = au.MissA; r.MissB = au.MissB; r.MissC = au.MissC;
                r.MissDetail = au.Detail;
            }
            try { r.Level = g.Progression.CharacterLevel; } catch { }
            try { r.Facts = g.Facts != null ? g.Facts.List.Count : -1; } catch { }
            try { r.Unit = g.Blueprint != null ? g.Blueprint.name : "?"; } catch { }
            try { r.Brain = g.Brain != null && g.Brain.Blueprint != null ? g.Brain.Blueprint.name : "?"; } catch { }
            try { r.Stats = RetinueTest.StatsLine(g); } catch { }
            try { r.Gear = RetinueTest.GearLine(g); } catch { }
            return r;
        }

        private static void Write(List<Row> rows)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("分型\t精英\t名称\t等级\tfacts\t方案条目\t应生效\t已生效\t达成率\t等级没到\tA未出现\tB不可选\tC没选上\t选择点\t自由选择\t方案\t单位\tbrain\t缺口\t属性\t装备");
                foreach (var r in rows)
                {
                    // 自由选择 = 方案没写、由回退决定的选择点。不是失败。
                    int free = r.SelSeen - r.SelHit;
                    sb.Append(r.Arch).Append('\t').Append(r.IsElite ? "精英" : "普通").Append('\t')
                      .Append(r.Name).Append('\t').Append(r.Level).Append('\t').Append(r.Facts).Append('\t')
                      .Append(r.PlanTotal).Append('\t').Append(r.PlanApplicable).Append('\t').Append(r.PlanOk).Append('\t')
                      .Append(r.PlanPct).Append('\t').Append(r.Unreached).Append('\t')
                      .Append(r.MissA).Append('\t').Append(r.MissB).Append('\t').Append(r.MissC).Append('\t')
                      .Append(r.SelSeen).Append('\t').Append(free).Append('\t')
                      .Append(r.Plan).Append('\t').Append(r.Unit).Append('\t')
                      .Append(r.Brain).Append('\t').Append(r.MissDetail).Append('\t')
                      .Append(r.Stats).Append('\t').Append(r.Gear).AppendLine();
                }
                File.WriteAllText(OutPath, sb.ToString(), new UTF8Encoding(false));
                Main.Log("对比表 -> " + OutPath);
            }
            catch (Exception e) { Main.LogError("写 autotest.tsv 失败: " + e.Message); }
        }

        private static void Report(List<Row> rows)
        {
            Main.Log("=== 汇总 ===");
            Main.Log("  达成率 = 方案里「等级走到了的」条目落实了多少。自由选择 = 方案没写、由回退决定的点，不算失败。");
            foreach (var r in rows)
            {
                Main.Log(string.Format("  {0,-14} {1} {2,-14} lv{3,-3} 方案 {4,3}/{5,-3} = {6,3}%   缺 A{7} B{8} C{9}  未到 {10,2}   自由选择 {11,-3} {12}",
                    r.Arch, r.IsElite ? "★" : " ", r.Name, r.Level, r.PlanOk, r.PlanApplicable, r.PlanPct,
                    r.MissA, r.MissB, r.MissC, r.Unreached, r.SelSeen - r.SelHit,
                    string.IsNullOrEmpty(r.Plan) ? "（无方案）" : r.Plan));
                if (r.MissC > 0) Main.LogError("       C 没选上（我们的 bug）: " + r.MissDetail);
                else if (!string.IsNullOrEmpty(r.MissDetail)) Main.Log("       " + r.MissDetail);
                Main.Log("       " + r.Stats);
            }
        }
    }
}
