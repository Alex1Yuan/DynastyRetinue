using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands.Base;

namespace DynastyRetinue
{
    /// <summary>
    /// ============ 死从天降·打击结算探针（1.7.75）============
    ///
    /// ★为什么需要它★
    ///   作者报「锈行猎手的死从天降像是从出发位置算伤害，电僧正常算落点」。
    ///   我据此做了几轮推断，但**上一版的几何探针结构上根本测不出这件事**：
    ///     DeathWaltzGeometryProbe 挂在 AbilityData.Cast 上，打 caster→context.ClickedTarget 距离；
    ///     而 Strike 蓝图 m_CastOnSelf=1 ⇒ ClickedTarget 就是施法者自己 ⇒ from==to ⇒ 恒打 0.0。
    ///   剩下的证据是人工对齐 GameLogFull.txt 的坐标，而同一时刻 ReaperBladeShroud 也在打
    ///   「Apply ability effect」（已知误报源），且采样是**格子量化**的
    ///   （GetBestShootingPosition → Caster.CurrentUnwalkableNode），0.4 格是亚格子量。
    ///   ⇒ **「0.4 格」这个数不成立。先拿到能分辨分支的读数，再决定改不改。**
    ///
    /// ★原版机制（工作流 IL 级确证，与 mod 无关）★
    ///   Strike 没有自己的动画：ContextActionJumpToTarget.RunAction
    ///     IL_0097 TryStartJump（先起跳）
    ///     IL_0182 get_CurrentAction → IL_01DD set_OverrideAnimationHandle（借句柄）
    ///     IL_01EB AddToQueue（后入队）
    ///   UnitUseAbility.StartAnimation 见 OverrideAnimationHandle 非空就早退，永不 Execute。
    ///   而 AbstractUnitCommand.Tick IL_00D4-00F2：
    ///     hasNewAct = Animation != null && Animation.ActEventsCounter > m_ActEventsCounter
    ///   新建的 Strike m_ActEventsCounter=0，借来的句柄早已 act 过（ActEventsCounter≥1）
    ///   ⇒ **Strike 第一次 Tick 就可能立刻结算**，此时人还在空中。
    ///   伤害落点是那一瞬间**实时读**的（RulePerformAttackRoll 回落 initiator.Entity.Position），
    ///   全链路没有冻结点。⇒ 「结算」和「落地」在引擎里本来就没有耦合。
    ///
    /// ★但两个单位的差异几乎肯定是我们造成的★ 三条只作用于锈行猎手的路径：
    ///   (1) 片段替换 —— 电僧 isHuman=true 被 ForeignRigClipBlockPatch 放行；
    ///       锈行猎手被替换，而且方向是 1.17s 的 Reaper 循环 → 6.00s 的 Sicarian_LoMo_Idle，
    ///       片段变长 ⇒ 句柄结束变晚 ⇒ 结算被推到**落地之后**。
    ///       ★注意这和作者的描述是反的★ —— 所以更不能凭印象改。
    ///   (2) DeathWaltzAoePatch 的落点注入（只锈行猎手）改变 AI 挑哪个落点，
    ///       让原版那条脱耦第一次变得刺眼。★注入本身不可能移动伤害★：
    ///       AbilityInfo.pattern 的唯一消费者是 AOETargetSelector.IsNodeValid。
    ///   (3) 落地挥刀（只锈行猎手），同样改的是那个借来的句柄。
    ///
    /// ★判读表★（拿到日志后照这个查，别再自由发挥）
    ///   ActEventsCounter &gt; m_ActEventsCounter 且 跳跃中 ⇒ 借用陈旧计数、飞行中结算（原版脱耦）
    ///   IsFinished 且 动作名以 Sicarian_ 开头     ⇒ **我们换的片段决定了结算时刻**（是我们的锅）
    ///   Animation==null 且 PretendActTime&gt;0      ⇒ 走 ScheduleAct 定时，与片段长度无关
    ///   已落地                                    ⇒ 结算在落点，作者报的现象不存在
    ///
    /// ★必须两个单位各拍一次，锈行猎手还要开/关收割者技能闸各拍一轮★
    ///   开关决定它走 AOETargetSelector 还是根本不放，两轮落点几何完全不同；
    ///   只拍一轮会把「AI 挑了个刁钻落点」误读成「伤害锚错了」。
    ///
    /// ★开销★ [Main.DiagOnly]：详细日志关着时**整个类不挂载**（1.7.48 的教训 ——
    ///   即使补丁体第一行 return，Harmony 的调度开销也省不掉）。
    ///   挂载时前两道闸是静态 bool；Tick 每单位每帧只对 Current 指令调一次，不是全体遍历。
    /// </summary>
    [Main.DiagOnly]
    [HarmonyPatch(typeof(AbstractUnitCommand), "Tick")]
    internal static class DeathWaltzStrikeProbe
    {
        /// <summary>ReaperDeathWaltzStrikeAbility。</summary>
        private const string StrikeGuid = "b5403e19a38d4f9199eccdfb9489bf8e";

        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // 每条指令只打一行 —— Tick 是每帧调用，不去重会刷屏
        private static readonly System.Collections.Generic.HashSet<object> _done =
            new System.Collections.Generic.HashSet<object>();

        private static FieldInfo _fActCounter;      // AbstractUnitCommand.m_ActEventsCounter（private）
        private static bool _looked, _memberWarned;

        private static void Prefix(AbstractUnitCommand __instance)
        {
            try
            {
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.DiagVerbose) return;

                var cmd = __instance as Kingmaker.UnitLogic.Commands.UnitUseAbility;
                if (cmd == null) return;

                var ab = cmd.Ability;
                var bp = ab != null ? ab.Blueprint : null;
                if (bp == null) return;
                // ★按 GUID 判，不按名字★ 名字可能被运行时改写
                string g = null;
                try { g = bp.AssetGuid.ToString(); } catch { }
                if (!string.Equals(g, StrikeGuid, StringComparison.OrdinalIgnoreCase)) return;

                if (_done.Count > 64) _done.Clear();
                if (!_done.Add(__instance)) return;              // 这条指令已经打过

                var u = cmd.Executor as BaseUnitEntity;

                // ── A 动画侧 ─────────────────────────────────────────
                var anim = cmd.Animation;
                string a;
                if (anim == null)
                {
                    a = "动画=null（走 ScheduleAct 定时）";
                }
                else
                {
                    string act = "?";
                    try { act = anim.Action != null ? anim.Action.GetType().Name + "/" + anim.Action.name : "null"; }
                    catch { }
                    a = "动作=" + act
                      + " 已完成=" + Safe(() => anim.IsFinished.ToString())
                      + " 被跳过=" + Safe(() => anim.IsSkipped.ToString())
                      + " 已释放=" + Safe(() => anim.IsReleased.ToString())
                      + " act计数=" + Safe(() => anim.ActEventsCounter.ToString());
                }

                if (!_looked)
                {
                    _looked = true;
                    for (var t = typeof(AbstractUnitCommand); t != null && _fActCounter == null; t = t.BaseType)
                        _fActCounter = t.GetField("m_ActEventsCounter", Any);
                    if (_fActCounter == null && !_memberWarned)
                    {
                        _memberWarned = true;
                        // ★读不到就明说★ 静默失败比失败本身贵 —— 本轮已因此浪费过好几版
                        Main.Log("[死从天降·结算] ★探针不完整★ 找不到 AbstractUnitCommand.m_ActEventsCounter，"
                               + "「指令已计 act」一栏会显示 ?，**该栏不能作为判据**。");
                    }
                }
                string mine = "?";
                try { if (_fActCounter != null) mine = _fActCounter.GetValue(__instance).ToString(); } catch { }

                // ── B 几何侧（★用格坐标，不用连续坐标差★ 取样本来就是格子量化的）──
                string b = "位置=?";
                try
                {
                    var p = u.Position;
                    string cell = "?";
                    try
                    {
                        var n = u.CurrentUnwalkableNode;
                        cell = "(" + n.XCoordinateInGrid + "," + n.ZCoordinateInGrid + ")";
                    }
                    catch { }
                    b = "结算点=(" + p.x.ToString("F1") + "," + p.z.ToString("F1") + ") 格" + cell
                      + DeathWaltzGeometry.Compare(u, p);
                }
                catch { }

                // ── C 跳跃侧 ─────────────────────────────────────────
                string c = "跳跃=已落地/未起跳";
                try
                {
                    var jp = u.GetOptional<Kingmaker.UnitLogic.Parts.UnitPartJump>();
                    var ck = jp != null ? jp.Active : null;
                    if (ck != null)
                        c = "★跳跃中★ 已过=" + ck.PassedTime.ToString("F2")
                          + "/" + ck.MaxTime.ToString("F2")
                          + " 阶段=" + ck.JumpPhase
                          + " 目标=(" + ck.TargetPosition.x.ToString("F1") + ","
                          + ck.TargetPosition.z.ToString("F1") + ")";
                }
                catch { }

                Main.Log("[死从天降·首次Tick] " + (u != null ? (u.CharacterName ?? "?") : "?")
                       + "　" + a + " 指令已计act=" + mine
                       + "　" + b + "　" + c
                       + "\n      ★判读★ act计数>指令已计act 且跳跃中 ⇒ 借用陈旧计数、飞行中结算（原版脱耦，不是我们的锅）；"
                       + "\n              已完成=True 且动作名以 Sicarian_ 开头 ⇒ **我们换的片段决定了结算时刻**；"
                       + "\n              动画=null 且走定时 ⇒ 与片段长度无关；"
                       + "\n              已落地 ⇒ 结算在落点，作者报的现象不存在。");
            }
            catch { }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "?"; } catch { return "?"; }
        }
    }
}
