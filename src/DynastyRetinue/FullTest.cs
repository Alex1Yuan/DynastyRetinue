using System;
using System.Collections.Generic;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 一键全测 —— 把所有**能自动跑的**验证串成一条链，跑完打一份总账。
    ///
    /// 起因：作者远程操作、没有鼠标，逐个点按钮不现实。而这些检查里的绝大多数
    /// 本来就不需要人参与，只是历史上一直做成了按钮。
    ///
    /// ================= 分成两个按钮，因为破坏性完全不同 =================
    /// 【只读自检】自检 + 状态断言。**不生成任何单位、不清场。** 随便点。
    /// 【全测】    自检 + 装备矩阵 + 死亡规则 + 卸载流程。
    ///            **会清空你现有的卫兵、把船变回原样。**
    ///
    /// ★第一版把装备矩阵放进了"只读"那个，并标成"不动你现有的卫兵" —— 那是假的。★
    /// AutoTest.RunGearMatrix 开头第一句就是 RetinueRegistry.DismissAll()（AutoTest.cs:129）。
    /// 一个自称只读、实则清空存档内容的按钮，比根本没有这个按钮糟糕得多：
    /// 玩家会因为"它说只读"而放心点，然后丢掉整支卫队。已改。
    ///
    /// ================= 必须跨帧 =================
    /// SpawnUnit 是**延迟入册**的（要到下一次 Tick 才进 state），同帧读
    /// RetinueRegistry.Count 拿到的是旧值 —— 这个坑在招募 UI 上踩过一次。
    /// 所以整条链用 Deferred.NextFrames 串起来，每步之间留两帧。
    ///
    /// ================= 仍然需要人手的 =================
    /// 跑完会明确列出来，不假装全测完了：
    ///   · 卸载后读档（要退游戏、改目录名、重开）
    ///   · 发布包在干净环境安装
    ///   · 面板/窗口的视觉检查（布局、遮挡、镜头远近）
    ///   · 真实战斗里的死亡（ScriptedKill / AOE 等路径）
    /// </summary>
    public static class FullTest
    {
        private static bool _running;
        private static readonly List<string> _log = new List<string>();

        private static void Step(string s) { _log.Add(s); Main.Log("  " + s); }

        // ------------------------------------------------------------ 只读

        /// <summary>
        /// 真·只读：自检 + 状态断言。**不生成任何单位、不清场、不改存档。**
        ///
        /// ★这里刻意不调 AutoTest.RunGearMatrix★ 第一版调了，并且标成"不动你现有的卫兵" ——
        /// 那是假的：RunGearMatrix 开头第一句就是 RetinueRegistry.DismissAll()（AutoTest.cs:129），
        /// 它会**先把你现有的卫兵全清掉**再开始测。
        /// 一个自称只读、实则清空存档内容的按钮，比没有这个按钮糟糕得多。
        /// 装备矩阵归下面那个明确写着"会清场"的按钮。
        /// </summary>
        public static void RunReadOnly()
        {
            if (_running) { Main.Log("[全测] 上一轮还没跑完。"); return; }
            _running = true;
            _log.Clear();
            Main.Log("======== 一键自检（只读，不生成单位、不清场）开始 ========");
            try
            {
                SelfCheck.ForceRun();
                Step("自检：见上方 ✓/✗ 块");
                Assertions();
            }
            catch (Exception e) { Main.LogError("[全测] 异常: " + e); }
            finally
            {
                Summary("只读自检");
                _running = false;
            }
        }

        private static void Assertions()
        {
            // 精英倒地豁免：在册精英应当都挂着。这条平时只能靠"打一次看看"，
            // 但豁免本身是可读的 —— 直接查，不用真打。
            try
            {
                int elite = 0, exempt = 0;
                foreach (var g in RetinueRegistry.All())
                {
                    int ai = RetinueRegistry.ArchetypeOf(g);
                    var arch = Archetypes.Get(ai >= 0 ? ai : 0);
                    bool isElite = false;
                    try { isElite = GearTool.EliteDefOf(g, arch) != null; } catch { }
                    if (!isElite) continue;
                    elite++;
                    try { if (g.Features != null && g.Features.UnconsciousOnZeroHealth.Value) exempt++; }
                    catch { }
                }
                if (elite == 0) Step("倒地豁免：在册没有精英，跳过");
                else if (exempt == elite) Step("倒地豁免：" + elite + " 名精英全部已挂 ✓");
                else Main.LogError("  ✗ 倒地豁免：" + elite + " 名精英只有 " + exempt
                                 + " 名挂上了 —— 剩下的会永久死亡。"
                                 + "多半是这次会话新招的还没过图（豁免在 ApplyRuntimeState 里重挂）");
            }
            catch (Exception e) { Main.LogError("  ✗ 倒地豁免检查失败: " + e.Message); }

            // 命名格式：应当全是「军衔·人名」，且人名不重复
            try
            {
                var seen = new Dictionary<string, string>(StringComparer.Ordinal);
                int bad = 0, dup = 0;
                foreach (var g in RetinueRegistry.All())
                {
                    string nm = null;
                    try
                    {
                        var d = g.GetOptional<Kingmaker.UnitLogic.Parts.PartUnitDescription>();
                        nm = d != null ? d.CustomName : null;
                    }
                    catch { }
                    if (string.IsNullOrEmpty(nm)) { bad++; continue; }
                    int i = nm.IndexOf('·');
                    if (i <= 0) { bad++; continue; }
                    string person = nm.Substring(i + 1);
                    if (seen.ContainsKey(person)) dup++;
                    else seen[person] = nm;
                }
                if (bad == 0 && dup == 0) Step("命名：全部为「军衔·人名」且人名不重复 ✓");
                else Main.LogError("  ✗ 命名：格式不符 " + bad + " 个，人名重复 " + dup + " 个");
            }
            catch (Exception e) { Main.LogError("  ✗ 命名检查失败: " + e.Message); }
        }

        // ------------------------------------------------------------ 破坏性

        /// <summary>
        /// 破坏性全测。**会清空全部卫兵、把座舰还原成原样。**
        /// 顺序是刻意的：先验死亡规则（要有卫兵），最后才遣散 —— 反过来就没得测了。
        /// </summary>
        public static void RunDestructive()
        {
            if (_running) { Main.Log("[全测] 上一轮还没跑完。"); return; }
            _running = true;
            _log.Clear();
            Main.Log("======== 一键全测（含破坏性）开始 ========");
            Main.Log("  ★这一轮会清空全部卫兵并把座舰还原★ 不想要的话现在就读档回滚。");

            try
            {
                SelfCheck.ForceRun();
                Assertions();

                // 装备矩阵放在这里而不是只读那个里 —— 它开头就 DismissAll（AutoTest.cs:129）
                Step("装备矩阵：开始（★会先清场★）");
                AutoTest.RunGearMatrix();

                // 生成两名测试卫兵：一名普通、一名精英（精英要能解锁才生成得出来）
                Step("死亡规则：生成测试卫兵……");
                RetinueTest.SpawnOne(0, null, true, true);            // 强制普通
                var eliteDef = GearTool.NextElite(0);
                if (eliteDef != null) RetinueTest.SpawnOne(0, eliteDef, true);

                // SpawnUnit 延迟入册，等两帧再打
                Deferred.NextFrames(3, delegate
                {
                    try
                    {
                        Step("死亡规则：打死普通卫兵……");
                        RetinueTest.TestKill("normal");
                        Deferred.NextFrames(3, delegate
                        {
                            try
                            {
                                Step("死亡规则：打死精英……");
                                RetinueTest.TestKill("elite");
                                Deferred.NextFrames(3, Teardown);
                            }
                            catch (Exception e) { Main.LogError("[全测] 精英测试: " + e); Teardown(); }
                        });
                    }
                    catch (Exception e) { Main.LogError("[全测] 普通卫兵测试: " + e); Teardown(); }
                });
            }
            catch (Exception e) { Main.LogError("[全测] 异常: " + e); _running = false; }
        }

        /// <summary>收尾：遣散 + 还原船模 + 复查。这两步同时也是**卸载流程的前两步**。</summary>
        private static void Teardown()
        {
            try
            {
                Step("卸载流程：遣散全部……");
                RetinueRegistry.DismissAll();

                Deferred.NextFrames(3, delegate
                {
                    try
                    {
                        int left = RetinueRegistry.Count;
                        if (left == 0) Step("卸载流程：复查在册 0 ✓");
                        else Main.LogError("  ✗ 卸载流程：仍有 " + left + " 名在册 —— "
                                         + "★此时请勿存档★，先看上面的遣散日志");

                        Step("卸载流程：还原原版船模……");
                        bool ok = StarshipViewTool.RevertAll();
                        var cur = ShipDialog.Current();
                        var orig = ShipDialog.OriginalSize();
                        if (ok && cur == orig && string.IsNullOrEmpty(StarshipViewTool.CurrentPrefab))
                            Step("卸载流程：座舰已回到 " + ShipDialog.SizeName(orig) + "、船模已清 ✓");
                        else
                            Main.LogError("  ✗ 卸载流程：还原不完整 —— 分档 " + cur
                                        + "（应为 " + orig + "）　船模 "
                                        + (StarshipViewTool.CurrentPrefab ?? "已清"));
                    }
                    catch (Exception e) { Main.LogError("[全测] 收尾: " + e); }
                    finally { Summary("含破坏性"); _running = false; }
                });
            }
            catch (Exception e) { Main.LogError("[全测] 收尾异常: " + e); Summary("含破坏性"); _running = false; }
        }

        // ------------------------------------------------------------ 总账

        private static void Summary(string kind)
        {
            Main.Log("======== 一键全测（" + kind + "）结束 ========");
            Main.Log("  已自动验证：" + _log.Count + " 项，明细见上。");
            Main.Log("  ★以下仍然需要人手，本测跑不了★");
            Main.Log("    1. 卸载后读档 —— 要退游戏、把 mod 目录改名、重开，进程外的事");
            Main.Log("    2. 发布包在干净环境安装 —— 同上，且要另一个 UMM 目录");
            Main.Log("    3. 视觉检查 —— 面板布局有没有重叠、改装界面镜头远近、炮的位置");
            Main.Log("    4. 真实战斗里的死亡 —— 本测走的是 ForceTickOnUnit，"
                   + "和战斗同一条判定路径，但 ScriptedKill / AOE 这些触发方式没覆盖");
            Main.Log("  另外：破坏性那轮跑完之后座舰和卫兵都被清了，"
                   + "★别在这个状态下存盘★ 除非你本来就想清空。");
        }
    }
}
