using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kingmaker;
using Kingmaker.Blueprints;

namespace DynastyRetinue
{
    /// <summary>
    /// 自检：读档后自动跑一遍**不需要任何点击**的检查，把结论写进日志。
    ///
    /// ================= 为什么要有 =================
    /// 这个 mod 的验证一直靠"玩家去面板点按钮 → 我读日志"。但很多检查本质上
    /// 只是"某个 GUID 解析得到吗""某个文件在吗""某个不变式成立吗"，
    /// 完全不需要人参与 —— 让人去点，纯属把机器该干的活推给人，
    /// 而且远程/无鼠标的时候直接卡死。
    ///
    /// 所以：读档后自动跑，一次，结果打成一个 ✓/✗ 块。
    ///
    /// ★门槛★ 只在 mod 目录下有 dynasty_selftest.flag 时跑。玩家不该看到这些噪音，
    /// 而且里面会遍历几百个蓝图，虽然只有一次，也没必要让所有人付这个成本。
    ///
    /// ★边界★ 这里**只做只读检查**：解析蓝图、读文件、算不变式。
    /// 不生成卫兵、不改存档、不动任何游戏状态 —— 自检本身绝不能成为 bug 来源。
    /// 需要真的生成单位的那些（装备能不能装上、加点命不命中）由
    /// 【一键测装备】负责，那个会造实体，必须由人显式触发。
    /// </summary>
    public static class SelfCheck
    {
        private static bool _ran;

        public static bool Armed
        {
            get
            {
                try
                {
                    return Main.ModEntry != null
                        && File.Exists(Path.Combine(Main.ModEntry.Path, "dynasty_selftest.flag"));
                }
                catch { return false; }
            }
        }

        /// <summary>手动触发，无视 flag 和"一次会话只跑一遍"。给【一键全测】用。</summary>
        public static void ForceRun()
        {
            _ran = true;
            try { Run(); }
            catch (Exception e) { Main.LogError("[自检] 自身崩了（这本身就是一条失败）: " + e); }
        }

        /// <summary>读档 / 进区域后调一次。幂等 —— 一次会话只跑一遍。</summary>
        public static void RunOnce()
        {
            if (_ran || !Armed) return;
            _ran = true;
            try { Run(); }
            catch (Exception e) { Main.LogError("[自检] 自身崩了（这本身就是一条失败）: " + e); }
        }

        private static int _pass, _fail, _warn;

        private static void Ok(string what, string detail)
        { _pass++; Main.Log("  ✓ " + what.PadRight(28) + detail); }
        private static void Bad(string what, string detail)
        { _fail++; Main.LogError("  ✗ " + what.PadRight(28) + detail); }
        private static void Warn(string what, string detail)
        { _warn++; Main.Log("  ! " + what.PadRight(28) + detail); }

        private static void Run()
        {
            _pass = _fail = _warn = 0;
            Main.Log("======== 自检开始（只读，不生成任何单位、不改存档）========");

            Files();
            Archetypes_();
            GearGuids();
            GearCoverage();
            SettingsSanity();
            ShipState();
            Report();

            Main.Log("======== 自检结束：通过 " + _pass + "　失败 " + _fail + "　提醒 " + _warn
                   + (_fail == 0 ? "　<全部通过>" : "　★有失败项，见上面的 ✗★") + " ========");
        }

        // ---------------------------------------------------------------- 文件

        private static void Files()
        {
            string dir = Main.ModEntry != null ? Main.ModEntry.Path : null;
            if (string.IsNullOrEmpty(dir)) { Bad("mod 目录", "拿不到 ModEntry.Path"); return; }

            // 运行时必需的两个数据文件。缺了 mod 名存实亡，而且失败方式很隐蔽
            // （回退内置默认，功能大幅退化但不报错）—— 所以这里显式查。
            foreach (var f in new[] { "archetypes.json", "plans.json" })
            {
                string p = Path.Combine(dir, f);
                if (File.Exists(p)) Ok("数据文件 " + f, new FileInfo(p).Length / 1024 + " KB");
                else Bad("数据文件 " + f, "缺失！mod 会退回内置默认，精英/装备表/人名池全没有");
            }

            // 发布包里不该有的东西，出现了说明打包路径错了
            foreach (var f in new[] { "Settings.xml", "dynasty_log.txt" })
                if (File.Exists(Path.Combine(dir, f)))
                    Ok("本机文件 " + f, "在（正常，发布包不含它）");

            Ok("开发区", Main.DevMode ? "可见（有 dynasty_dev.flag）" : "已隐藏（发布版形态）");
        }

        // ---------------------------------------------------------------- 分型

        private static void Archetypes_()
        {
            try
            {
                var all = Archetypes.All;
                if (all == null || all.Length == 0) { Bad("分型模板", "一个都没有"); return; }

                // ★这条最关键★ 回退到内置默认时也是"有分型"，但只有 4 条且没有精英。
                // 只查数量会漏掉那种情况，所以顺带查精英总数。
                int elites = 0;
                foreach (var a in all) if (a.Elites != null) elites += a.Elites.Length;

                var names = new List<string>();
                foreach (var a in all) names.Add(a.Name);
                string s = all.Length + " 条（" + string.Join(" / ", names.ToArray()) + "），精英 " + elites + " 名";

                if (all.Length >= 5 && elites >= 10) Ok("分型模板", s);
                else Bad("分型模板", s + "　←★多半是回退到了内置默认："
                                       + "archetypes.json 没读到，或读的时候蓝图缓存还没就绪★");

                if (Archetypes.GuardNamePool == null || Archetypes.GuardNamePool.Length < 20)
                    Warn("人名池", "只有 " + (Archetypes.GuardNamePool == null ? 0 : Archetypes.GuardNamePool.Length)
                               + " 个，卫兵会重名或退回编号");
                else Ok("人名池", Archetypes.GuardNamePool.Length + " 个");
            }
            catch (Exception e) { Bad("分型模板", "异常: " + e.Message); }
        }

        // ---------------------------------------------------------------- 装备 GUID

        /// <summary>
        /// 把所有装备候选链里的 GUID 逐个解析。
        /// ★只查"解析得到吗"，不查"装得上吗"★ —— 后者要真的造一个单位来试，
        /// 那是【一键测装备】的活。这里能抓的是"GUID 打错了 / DLC 没装"，
        /// 而那恰恰是最常见、也最容易被回退逻辑掩盖的一类错误。
        /// </summary>
        private static void GearGuids()
        {
            try
            {
                int total = 0, miss = 0;
                var missing = new List<string>();
                var all = Archetypes.All;
                foreach (var a in all)
                {
                    foreach (var chain in AllGearChains(a))
                        foreach (var g in chain.Split('|'))
                        {
                            if (string.IsNullOrEmpty(g)) continue;
                            total++;
                            object bp = null;
                            // 用非泛型重载 + 字符串：泛型那个要指定具体蓝图类型，
                            // 而这里的候选链混着武器/护甲/植入物，一个类型套不住。
                            try { bp = ResourcesLibrary.TryGetBlueprint(g); }
                            catch { }
                            if (bp == null) { miss++; if (missing.Count < 12) missing.Add(a.Name + " " + g); }
                        }
                }
                if (total == 0) { Warn("装备 GUID", "一条都没扫到（分型可能已回退默认）"); return; }
                if (miss == 0) Ok("装备 GUID", total + " 个全部解析成功");
                else Warn("装备 GUID", total + " 个里 " + miss + " 个解析不到（多半是未启用的 DLC）：\n      "
                                     + string.Join("\n      ", missing.ToArray()));
            }
            catch (Exception e) { Bad("装备 GUID", "异常: " + e.Message); }
        }

        private static IEnumerable<string> AllGearChains(ChainProbe.Archetype a)
        {
            foreach (var g in new[] { a.GearT1, a.GearT2, a.GearT3 })
                if (g != null) foreach (var s in g) if (!string.IsNullOrEmpty(s)) yield return s;
            if (a.Elites != null)
                foreach (var e in a.Elites)
                    if (e != null && e.Gear != null)
                        foreach (var s in e.Gear) if (!string.IsNullOrEmpty(s)) yield return s;
        }

        // ---------------------------------------------------------------- 档位覆盖

        /// <summary>
        /// T3 槽位空洞：某个装备**类型**在 T1/T2 配了、T3 没配。
        ///
        /// ★为什么这条必须单独查★
        /// 【一键测装备】报的「12/12、0 槽位装不上」意思是"**配表里写了的**都成功穿上了"，
        /// 它不验"配表本身全不全" —— 没配的东西当然不会报错。于是可以同时出现
        /// 「100% 通过」和「四个槽位空着」。
        ///
        /// 而这在 T3 上特别致命，因为 GearFor（GearTool.cs:595-597）返回的是
        /// **单独一档数组、不累加**，且 55 级存档恒为 T3 —— 也就是说
        /// **新招的卫兵只会拿到 gearT3，压根不经过 T1/T2**。
        /// 「删掉后档那条，前档那件靠只增不减留着」只对**逐级长大**的卫兵成立；
        /// 对直接招在 T3 的卫兵，那一格就是空的，而且没有任何提示。
        /// </summary>
        private static void GearCoverage()
        {
            try
            {
                int holes = 0;
                var detail = new List<string>();
                foreach (var a in Archetypes.All)
                {
                    var t1 = TypeCount(a.GearT1);
                    var t2 = TypeCount(a.GearT2);
                    var t3 = TypeCount(a.GearT3);
                    foreach (var kv in t1) Merge(t2, kv.Key, kv.Value);   // 前档取两者较多的那个
                    foreach (var kv in t2)
                    {
                        int has;
                        t3.TryGetValue(kv.Key, out has);
                        if (has < kv.Value)
                        {
                            holes++;
                            if (detail.Count < 8)
                                detail.Add(a.Name + " " + kv.Key + " T3=" + has + " 前档=" + kv.Value);
                        }
                    }
                }
                if (holes == 0) Ok("档位覆盖", "T3 覆盖了前档的所有装备类型，无空洞");
                else Warn("档位覆盖", holes + " 处 T3 空洞 —— ★T3 直招的卫兵这些槽会是空的★\n      "
                                    + string.Join("\n      ", detail.ToArray()));
            }
            catch (Exception e) { Warn("档位覆盖", "查不了: " + e.Message); }
        }

        private static void Merge(Dictionary<string, int> d, string k, int v)
        {
            int cur; d.TryGetValue(k, out cur);
            if (v > cur) d[k] = v;
        }

        /// <summary>把一档配表按**蓝图类型**计数 —— GearTool 是按类型定槽的，下标不代表槽位。</summary>
        private static Dictionary<string, int> TypeCount(string[] tier)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (tier == null) return d;
            foreach (var entry in tier)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var first = entry.Split('|')[0].Trim();       // 候选链只看首选
                if (string.IsNullOrEmpty(first)) continue;
                object bp = null;
                try { bp = ResourcesLibrary.TryGetBlueprint(first); } catch { }
                if (bp == null) continue;                     // 解析不到的归 GearGuids 那条管
                string t = bp.GetType().Name;
                int c; d.TryGetValue(t, out c); d[t] = c + 1;
            }
            return d;
        }

        // ---------------------------------------------------------------- 设置不变式

        private static void SettingsSanity()
        {
            var st = Main.Settings;
            if (st == null) { Bad("设置", "Settings 为 null"); return; }

            // 差价制的前提：目标档总价必须单调不减，否则"升级反而退钱"
            if (st.ShipPriceGrand < st.ShipPriceCruiser)
                Warn("船坞定价", "大巡 " + st.ShipPriceGrand + " < 巡洋 " + st.ShipPriceCruiser
                              + "，已由 ShipDialog.TotalFor 夹住，但面板上的数字是误导的");
            else Ok("船坞定价", "巡洋 " + st.ShipPriceCruiser + " / 大巡 " + st.ShipPriceGrand + "（单调）");

            if (st.RecruitMaxGuards <= 0)
                Warn("招募上限", "为 " + st.RecruitMaxGuards + "，一个都招不了");
            else Ok("招募上限", st.RecruitMaxGuards + " 名，每名 " + st.RecruitPfPerGuard + " 利润因子");

            // 这五个都是"作弊"性质的开关，发布默认应当全关。
            // ★精英那两个原来漏在这里★ 它们本来待在开发区，于是写这条检查时没想到；
            // 挪进玩家区之后，带着它们发包和带着前三个发包是同一类错误。
            bool unlocked = st.NoCountCap() || st.NoPfGate() || st.NoLevelCap()
                         || st.UnlockEliteLimit || st.EliteIgnoreUnlock;
            if (unlocked) Warn("解除限制", "有开关处于打开状态 —— 发布默认应当全关，"
                                        + "别把本机配置当成玩家的默认体验");
            else Ok("解除限制", "全关（发布默认形态）");
        }

        // ---------------------------------------------------------------- 舰船

        private static void ShipState()
        {
            try
            {
                var cur = ShipDialog.Current();
                var orig = ShipDialog.OriginalSize();
                Ok("座舰", "当前 " + ShipDialog.SizeName(cur) + "　原生 " + ShipDialog.SizeName(orig)
                         + "　船模 " + (string.IsNullOrEmpty(StarshipViewTool.CurrentPrefab) ? "原版" : "自定义"));

                // 卸载相关：这两条是玩家唯一会踩坏的地方，自检里显式提醒
                int n = RetinueRegistry.Count;
                if (n > 0 || cur != orig)
                    Warn("卸载前须知", "在册 " + n + " 名卫兵" + (cur != orig ? "、座舰已改装" : "")
                                    + " —— 禁用 mod 前需先遣散 / 还原船模再存盘");
                else Ok("卸载前须知", "无卫兵、座舰原样，可以直接禁用");
            }
            catch (Exception e) { Warn("座舰", "读不到（可能不在游戏内）: " + e.Message); }
        }

        // ---------------------------------------------------------------- 诊断包

        /// <summary>
        /// 顺便验一次诊断包：能不能生成、生成的东西里**还有没有用户名**。
        /// 这条本来要玩家自己导出再用记事本搜，属于典型的"机器该干的活"。
        /// </summary>
        private static void Report()
        {
            try
            {
                string p = DiagnosticReport.Export();
                if (string.IsNullOrEmpty(p)) { Bad("诊断包", "导出失败"); return; }

                string body = File.ReadAllText(p, Encoding.UTF8);
                string user = null;
                try { user = Environment.UserName; } catch { }
                bool leak = !string.IsNullOrEmpty(user) && user.Length >= 3
                         && body.IndexOf(user, StringComparison.OrdinalIgnoreCase) >= 0;

                if (leak) Bad("诊断包脱敏", "★里面还能搜到用户名「" + user + "」★ 不能让玩家直接发出去");
                else Ok("诊断包", new FileInfo(p).Length / 1024 + " KB，未检出用户名　" + Path.GetFileName(p));
            }
            catch (Exception e) { Bad("诊断包", "异常: " + e.Message); }
        }
    }
}
