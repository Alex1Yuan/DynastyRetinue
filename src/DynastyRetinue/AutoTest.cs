using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;

namespace DynastyRetinue
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

        /// <summary>
        /// ★已被【一键全测（会清空卫兵）】取代，面板上不再有入口★
        ///
        /// 它和 RunGearMatrix 都会把 25 组配装**各生成一遍**，而 RunGearMatrix 的日志里
        /// 本来就带属性（属性对比表就是从它的输出里解出来的），RunAll 独有的只剩 brain 一列 ——
        /// 为一列信息跑第二轮 25 次生成不划算。brain 已并进 RunGearMatrix 的每组日志。
        ///
        /// 保留函数体是因为它还写 autotest.tsv（列比 geartest.tsv 全，离线对表时有用）。
        /// 需要的时候直接从代码里调，不占面板一行、也不会被误点。
        /// </summary>
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

        /// <summary>
        /// 一键测装备：5 个分型 × T1/T2/T3 = 15 组普通卫兵，**外加全部 10 个精英**。
        ///
        /// 为什么要有它：装备档位由玩家等级推出（PlayerTier），55 级存档恒为 T3；
        /// 要验 T1/T2 得手动切面板档位再一个个招，15 组要点几十次。
        /// 这里把「切档位 → 招一个 → 记结果 → 遣散」整个循环自动化，只点一次。
        ///
        /// 精英那一趟是后加的：精英不吃档位（用各自 EliteDef.Gear 的毕业套），
        /// 以前只在【一键全测】里覆盖，于是"改精英点全测、改分型三档点这个"，
        /// 两边都得点还容易漏。现在一个按钮把 15 组普通 + 全部精英一次跑完。
        ///
        /// 结果同时写进 geartest.tsv，方便离线对着 items_zh.tsv 排查装不上的那些。
        /// </summary>
        /// <summary>刚生成的那名卫兵的 brain 蓝图名。合并自原【一键全测】。</summary>
        private static string BrainOf()
        {
            try
            {
                var list = RetinueRegistry.All();
                if (list.Count == 0) return "(没有在册卫兵)";
                var g = list[list.Count - 1];
                return (g.Brain != null && g.Brain.Blueprint != null) ? g.Brain.Blueprint.name : "(无)";
            }
            catch (Exception e) { return "(读不到: " + e.Message + ")"; }
        }

        public static void RunGearMatrix()
        {
            var game = Game.Instance;
            var leader = game != null && game.Player != null ? game.Player.MainCharacterEntity : null;
            if (leader == null) { Main.LogError("请先进入游戏内。"); return; }

            bool oldUnlockTier = Main.Settings.UnlockTierLimits;
            bool oldUnlockElite = Main.Settings.UnlockEliteLimit;
            bool oldIgnoreUnlock = Main.Settings.EliteIgnoreUnlock;
            int  oldTier = Main.Settings.GearTierOverride;
            Main.Settings.UnlockTierLimits = true;
            Main.Settings.UnlockEliteLimit = true;
            Main.Settings.EliteIgnoreUnlock = true;

            var lines = new List<string>();
            int totalFail = 0;
            try
            {
                Main.Log("================ 一键测装备开始（5 分型 × 3 档 + 全部精英）================");
                RetinueRegistry.DismissAll();

                var archs = Archetypes.All;
                for (int ai = 0; ai < archs.Length; ai++)
                {
                    var a = archs[ai];
                    for (int tier = 1; tier <= 3; tier++)
                    {
                        Main.Settings.GearTierOverride = tier;
                        GearTool.LastOk = GearTool.LastFail = GearTool.LastMiss = GearTool.LastAlready = 0;
                        GearTool.LastNames = GearTool.LastRejected = "";

                        Main.Log("---- " + a.Name + "  T" + tier + " ----");
                        BaseUnitEntity g = null;
                        try { g = RetinueTest.SpawnOne(ai, null, true, true); }   // forceNormal
                        catch (Exception e) { Main.LogError("  生成失败: " + e.Message); }

                        int want = 0;
                        try
                        {
                            var arr = tier == 1 ? a.GearT1 : tier == 2 ? a.GearT2 : a.GearT3;
                            want = arr == null ? 0 : arr.Length;
                        }
                        catch { }

                        lines.Add(string.Join("\t", new[]{
                            a.Name, "T" + tier, want.ToString(),
                            GearTool.LastOk.ToString(), GearTool.LastFail.ToString(),
                            GearTool.LastMiss.ToString(), GearTool.LastNames, GearTool.LastRejected }));
                        totalFail += GearTool.LastFail;

                        if (GearTool.LastFail > 0)
                            Main.Log("  ⚠ " + a.Name + " T" + tier + " 有 " + GearTool.LastFail
                                     + " 格装不上: " + GearTool.LastRejected);

                        // brain 原来只有【一键全测】记，而那个按钮会把 25 组再生成一遍。
                        // 为一列信息跑第二轮不划算，并到这里。
                        Main.Log("  brain: " + BrainOf());

                        try { RetinueRegistry.DismissAll(); } catch { }
                    }

                    // ★ 精英也一起测 ★
                    // 精英**不吃档位**——它们用各自 EliteDef.Gear 那份毕业套，跟 T1/T2/T3 无关。
                    // 以前精英只在【一键全测】里测、普通卫兵只在这里测，
                    // 于是改了精英装备要点全测、改了分型三档要点这个，两边都得点一次还容易漏。
                    // 现在这一个按钮把 15 组普通 + 全部精英一次跑完。
                    if (a.Elites != null)
                    {
                        for (int ei = 0; ei < a.Elites.Length; ei++)
                        {
                            var ed = a.Elites[ei];
                            if (ed == null) continue;

                            GearTool.LastOk = GearTool.LastFail = GearTool.LastMiss = GearTool.LastAlready = 0;
                            GearTool.LastNames = GearTool.LastRejected = "";

                            string tag = "精英:" + (string.IsNullOrEmpty(ed.Name) ? ("#" + ei) : ed.Name);
                            Main.Log("---- " + a.Name + "  " + tag + " ----");

                            try { RetinueTest.SpawnOne(ai, ed, true); }
                            catch (Exception e) { Main.LogError("  生成失败: " + e.Message); }

                            int wantE = ed.Gear == null ? 0 : ed.Gear.Length;
                            lines.Add(string.Join("\t", new[]{
                                a.Name, tag, wantE.ToString(),
                                GearTool.LastOk.ToString(), GearTool.LastFail.ToString(),
                                GearTool.LastMiss.ToString(), GearTool.LastNames, GearTool.LastRejected }));
                            totalFail += GearTool.LastFail;

                            if (GearTool.LastFail > 0)
                                Main.Log("  ⚠ " + tag + " 有 " + GearTool.LastFail
                                         + " 格装不上: " + GearTool.LastRejected);

                            Main.Log("  brain: " + BrainOf());

                            try { RetinueRegistry.DismissAll(); } catch { }
                        }
                    }
                }

                // 汇总表
                Main.Log("======== 装备矩阵汇总（配置数 / 装上 / 装不上 / 解析不到）========");
                foreach (var l in lines)
                {
                    var f = l.Split('\t');
                    Main.Log(string.Format("  {0,-14} {1}   配{2,2}  装上{3,2}  装不上{4,2}  缺蓝图{5,2}",
                        f[0], f[1], f[2], f[3], f[4], f[5]));
                }
                Main.Log("  合计装不上 " + totalFail + " 格。" +
                         (totalFail == 0 ? "" : " 明细见 geartest.tsv"));

                try
                {
                    var sb = new StringBuilder("archetype\ttier\tconfigured\tequipped\tfailed\tmissing\tnames\trejected\n");
                    foreach (var l in lines) sb.AppendLine(l);
                    File.WriteAllText(
                        Path.Combine(Main.ModEntry != null ? Main.ModEntry.Path : ".", "geartest.tsv"),
                        sb.ToString(), new System.Text.UTF8Encoding(false));
                    Main.Log("  -> geartest.tsv");
                }
                catch (Exception e) { Main.LogError("写 geartest.tsv 失败: " + e.Message); }
            }
            catch (Exception e) { Main.LogError("一键测装备异常: " + e); }
            finally
            {
                Main.Settings.UnlockTierLimits = oldUnlockTier;
                Main.Settings.UnlockEliteLimit = oldUnlockElite;
                Main.Settings.EliteIgnoreUnlock = oldIgnoreUnlock;
                Main.Settings.GearTierOverride = oldTier;   // 还原，别把测试档位留给玩家
                try { RetinueRegistry.DismissAll(); } catch { }
                Main.Log("================ 一键测装备结束 ================");
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
