using System;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.Visual.Animation;
using Kingmaker.Visual.Animation.Kingmaker;
using Kingmaker.Visual.Animation.Kingmaker.Actions;

namespace DynastyRetinue
{
    /// <summary>
    /// ============ 1.7.79：施法 Act 重入闸 + 补上缺失的跳跃动作 ============
    ///
    /// 这两个补丁治的是两件**互相独立**的事，都由同一轮工作流查实。
    /// </summary>
    internal static class JumpAndActFix
    {
        // ══════════════════════════════════════════════════════════════
        // 【方案 1】OnAction 重入闸 —— 治「伤害锚在出发点」和每场 11 条引擎红字
        // ══════════════════════════════════════════════════════════════
        //
        // ★完整因果链（工作流用引擎自己的 GameLogFull 端到端坐实，5/5 次一致）★
        //   我们的 CastStyleFallbackPatch 把锈行猎手的施法风格改写成 Grenade
        //     （它的施法表里只有 Style=8 这一条）
        //   → 播 Sicarian_Spell_Granade，而这个片段带 **2 个** AnimationClipEventAct
        //     （t=1.0187 / 1.0373，间隔 18.6ms；对照 ElectroPriest_Spell_Direct 只有 1 个）
        //   → 第 2 个 Act 让父指令 ReaperDeathWaltzAbility 二次进 OnAction
        //     （句柄计数 2 > 父指令已计 1）
        //   → 撞 OnAction IL_001E-0057 的 `CurrentActionIndex 1 >= ActionsCount 1` → 返回 Fail(1)
        //   → Tick IL_03CE 见 Fail → 引擎打「Forcing finish of UnitCommand cause of ResultType.Fail」
        //     并**强制结束父指令**、队列腾空
        //   → 排在后面的 Strike 立刻成为当前指令、拿到第一次 Tick，
        //     借来的句柄计数 2 > 它自己的 0 ⇒ **当场结算**
        //   → 此时跳跃才过 0.10/0.60 秒 ⇒ 伤害落在离出发点 0.5m、离落点 2.5m 处
        //
        // ★影响面远不止死从天降★ 同一场 11 次强制结束覆盖了
        //   TacticianInspire / Linchpin / Strongpoint / FinishTheJob / Ultimate /
        //   ReaperBloodOath / ReaperBladeShroud / DeathWaltz ——
        //   **每一个被改写成 Grenade 的锈行猎手技能都在吃两条红字 + 指令被强制结束**。
        //   伤害错锚只是最显眼的那个症状。
        //   单位隔离对照：11 次全在锈行猎手，电僧 0 次，与「替身片段带几个 Act」完全共变。
        //
        // ★为什么不能靠撤补丁修★ 撤掉 CastStyleFallback ⇒ 施法动画整个不起播
        //   （OnStart 的 SingleOrDefault 落空 → set_IsSkipped(true) + Release），
        //   那正是 1.7.67 那类「用不执行修表现」造成的卡死。
        //   真正的缺陷是**替身片段的 Act 事件数 ≠ 该技能的 ActionsCount**，不是替换本身。
        //
        // ★这个闸的语义与原版一致★ 原版 OnAction 只在 CurrentActionIndex >= ActionsCount 时
        //   返回 Success；返回过结果之后再被调用，本来就是它自己判定的错误状态。
        //   我们只是让这种重入变成无副作用空转，而不是让它去踩报错分支。
        //
        // ★开销★ 挂在 act 事件上，不是每帧路径；第一道闸是静态 bool。
        [HarmonyPatch(typeof(UnitUseAbility), "OnAction")]
        internal static class ActReentryGate
        {
            internal static int Blocked;

            /// <summary>ReaperDeathWaltzStrikeAbility —— 只对它打，一条指令一次。</summary>
            private const string StrikeGuid = "b5403e19a38d4f9199eccdfb9489bf8e";
            private static readonly System.Collections.Generic.HashSet<object> _logged =
                new System.Collections.Generic.HashSet<object>();

            /// <summary>伤害真正结算那一刻的现场。三点同框：结算点 / 出发点 / 落点。</summary>
            private static void ProbeResolve(UnitUseAbility cmd, BaseUnitEntity u)
            {
                try
                {
                    var st = Main.Settings;
                    if (st == null || !st.DiagVerbose) return;      // ★先判开关，字符串都不碰★
                    var ab = cmd.Ability;
                    var bp = ab != null ? ab.Blueprint : null;
                    if (bp == null) return;
                    string g = null;
                    try { g = bp.AssetGuid.ToString(); } catch { }
                    if (!string.Equals(g, StrikeGuid, StringComparison.OrdinalIgnoreCase)) return;

                    if (_logged.Count > 64) _logged.Clear();
                    if (!_logged.Add(cmd)) return;

                    var p = u.Position;
                    string jump = "已落地/未起跳";
                    try
                    {
                        var jp = u.GetOptional<Kingmaker.UnitLogic.Parts.UnitPartJump>();
                        var ck = jp != null ? jp.Active : null;
                        if (ck != null)
                            jump = "★仍在飞★ 已过=" + ck.PassedTime.ToString("F2")
                                 + "/" + ck.MaxTime.ToString("F2") + " 阶段=" + ck.JumpPhase;
                    }
                    catch { }

                    Main.Log("[死从天降·结算] " + (u.CharacterName ?? "?")
                           + "　★这一行是伤害真正结算的那一刻★"
                           + DeathWaltzGeometry.Compare(u, p)
                           + "　" + jump
                           + "　指令结果=" + cmd.Result
                           + "\n      ★判读★ 「更靠近落点」＝锚点正确；「更靠近出发点」＋仍在飞 ⇒ 还没修好。"
                           + "\n      ★对照★ [死从天降·首次Tick] 那一行测的是**指令首次 Tick**，不是这里。");
                }
                catch { }
            }

            private static bool Prefix(UnitUseAbility __instance, ref AbstractUnitCommand.ResultType __result)
            {
                try
                {
                    if (!Main.Enabled) return true;
                    if (!AnimFallback.AnyMeleeEliteActive) return true;   // ★O(1) 静态 bool★
                    if (__instance == null) return true;

                    // 只管我们的卫兵 —— 原版爆发武器理论上走不到这条分支，但不该赌
                    var u = __instance.Executor as BaseUnitEntity;
                    if (u == null || !WeaponGate.IsGateTarget(u)) return true;

                    // ★探针挂在这里，不挂 Tick★ OnAction 才是技能效果真正跑的地方
                    //   （AbilityEffectRunAction → WarhammerContextActionPerformAttack），
                    //   伤害落点就是**这一刻**施法者站的位置（RulePerformAttackRoll 回落
                    //   initiator.Entity.Position，全链路无冻结点）。
                    //   ★为什么必须挪★ 上一版探针挂在 Strike 指令的第一次 Tick 上，
                    //   那在「第一次 Tick 就结算」的前提下才等价；补了 Jump(38) 之后
                    //   act计数=0 ⇒ hasNewAct 恒假 ⇒ 第一次 Tick 不结算，前提没了，
                    //   而标签还写着「结算点」—— 那就是一个安静输出错误量的探针。
                    //   ★写在闸之前★ 被闸掉的那次重入也要记录，否则会漏掉真实发生过的事。
                    ProbeResolve(__instance, u);

                    var r = __instance.Result;
                    if (r == AbstractUnitCommand.ResultType.None) return true;   // 正常首次，放行

                    // 已经有结果了还被调 ⇒ 是多余的 Act 触发的重入。原样返回，别去踩报错分支。
                    Blocked++;
                    __result = r;
                    return false;
                }
                catch { }
                return true;
            }
        }


        // ══════════════════════════════════════════════════════════════
        // 【方案 4】补上缺失的 Jump(38) 动作 —— 治「位移时平移、一帧动画都不播」
        // ══════════════════════════════════════════════════════════════
        //
        // ★根因：这两个单位都没有 Jump(38)，是原版行为，不是我们弄坏的★
        //   两个 prefab 各有一个 UnitAnimationActionJump，但 m_SubType=2，
        //   经 ToAnimationType 映射成 41(HookPulled)，**不是 38(Jump)**：
        //     电僧      ElectroPriest_AnimationSet.m_Actions[9] = 'Human_AnimationSet_HookPulled'
        //     锈行猎手  Sicarian_AnimationSet.m_Actions[8]      = 'Sicarian_AnimationSet_HookPulled'
        //   ⇒ UnitPartJump.ExecuteJumpAnimationAction 里 GetAction(38) 必返回 null，
        //     随后 IL_0049-0052 **静默 ret**（一条日志都不打），跳跃期一帧不播；
        //     而位移由 UnitJumpMoveController.TickOnUnit 照常推进 ⇒ 保持当前姿势平移。
        //   锈行猎手其实也在滑，只是被还在播的手雷片段遮住，不如电僧刺眼。
        //
        // ★存档安全★ ScriptableObject.CreateInstance 出来的 UnitAnimationAction
        //   **不是 SimpleBlueprint、没有 AssetGuid**，不注册进 ResourcesLibrary、
        //   不被任何会序列化的 Fact 引用 ⇒ 不碰 AssetId 红线（同机仆那个 WarhammerCooldown）。
        //
        // ★为什么 LoopedFly=true★ 为 false 时 ExecuteJumpAnimationAction IL_0085-008A 会用
        //   fly 片段长度**改写 MaxTime/Speed**，跳跃时长和手感都会变。
        //   true 则跳过那段，MaxTime 保持 距离/5.0 不变 —— 我们只想补动作，不想改手感。
        //
        // ★1.7.68 形状的风险，所以配了看门狗★ LoopedFly=true 时 fly 片段会一直循环，
        //   直到 FinishJumpFlyAnimation；而后者要求 CurrentAction 仍是 UnitAnimationActionJump，
        //   飞行中被别的动作抢走就会变成**原地跑步无限循环**（正是 1.7.68 那次事故的形状）。
        //   看门狗只在我们自己跳跃期间武装，落地或超时就强制放行。
        //
        // ★已知的时序副作用（工作流点名，需实测）★
        //   AnimationManager.Execute 会把这个 Jump 设成 m_CurrentAction 并 Release 掉施法句柄；
        //   而 ContextActionJumpToTarget 是在 TryStartJump **之后**才读 CurrentAction 交给 Strike。
        //   ⇒ Strike 借到的可能变成 Jump 句柄（Act 计数 0、Run 片段无 Act 事件），
        //     hasNewAct 恒假，结算改由动画结束触发＝落地。**可能顺手把锚点也修好**，
        //     但这是纸面推演，实测前不能当收益算，也不能因此少做方案 1。
        [HarmonyPatch(typeof(UnitAnimationManager), "GetAction", new Type[] { typeof(UnitAnimationType) })]
        internal static class SupplyJumpAction
        {
            internal static int Supplied;

            /// <summary>每个动画管理器一份，按需建一次。</summary>
            private static readonly System.Collections.Generic.Dictionary<object, UnitAnimationActionJump> _made =
                new System.Collections.Generic.Dictionary<object, UnitAnimationActionJump>();

            private static void Postfix(UnitAnimationManager __instance, UnitAnimationType type,
                                        ref Kingmaker.Visual.Animation.Actions.AnimationActionBase __result)
            {
                try
                {
                    if (!Main.Enabled) return;
                    if (!AnimFallback.AnyMeleeEliteActive) return;       // ★O(1)★
                    if (type != UnitAnimationType.Jump) return;          // 38
                    if (__result != null) return;                        // 原版有，别插手

                    var view = __instance.View as Kingmaker.View.Mechanics.Entities.AbstractUnitEntityView;
                    var u = view != null ? view.EntityData as BaseUnitEntity : null;
                    if (u == null || !WeaponGate.IsGateTarget(u)) return;

                    UnitAnimationActionJump made;
                    if (!_made.TryGetValue(__instance, out made) || made == null)
                    {
                        var fly = ForeignRigClipBlockPatch.RunClipFor(__instance);
                        if (fly == null) return;                         // 没跑步片段就别造，维持现状
                        made = UnityEngine.ScriptableObject.CreateInstance<UnitAnimationActionJump>();
                        made.name = "DynastyRetinue_SyntheticJump";
                        Traverse.Create(made).Field("m_JumpFly").SetValue(fly);
                        Traverse.Create(made).Field("m_LoopedFly").SetValue(true);
                        Traverse.Create(made).Field("m_SubType").SetValue(0);   // 0 = Jump ⇒ Type 映射成 38
                        // m_JumpIn / m_JumpOut 留空是合法的：OnStart IL_0033 取 In 为空时
                        // 直接走 IL_005E StartFlyAnimation。
                        if (_made.Count > 32) _made.Clear();
                        _made[__instance] = made;
                        Main.Log("[跳跃动作] 为 " + (u.CharacterName ?? "?")
                               + " 补了一个 Jump(38)：飞行段用它自己的跑步片段。"
                               + "★原版这里返回 null 且静默，跳跃期一帧动画都不播 —— 那就是「平移」。★");
                    }
                    Supplied++;
                    __result = made;
                }
                catch { }
            }
        }


        /// <summary>
        /// 看门狗：合成 Jump 是 LoopedFly，飞行片段自己不会停。
        /// 正常路径由引擎的 FinishJumpFlyAnimation 收尾，但它要求 CurrentAction 仍是那个 Jump；
        /// 一旦飞行中被别的动作抢走 CurrentAction，就会变成**原地跑步无限循环**
        /// —— 1.7.68「结束回合还在一直播攻击动画」就是这个形状，所以必须兜住。
        ///
        /// ★每帧准入★ 未武装时第一行 Count==0 就走，是一次 int 比较。
        /// </summary>
        internal static class JumpWatchdog
        {
            private static readonly System.Collections.Generic.Dictionary<BaseUnitEntity, float> _armed =
                new System.Collections.Generic.Dictionary<BaseUnitEntity, float>();
            internal static int Rescued;

            private static System.Reflection.FieldInfo _fActive;
            private static bool _lookedActive, _warnedActive;

            /// <summary>
            /// 在这个管理器的活动句柄里找**我们自己造的**那个合成跳跃。
            /// 先看 CurrentAction（最常见），再扫 m_ActiveActions（被抢走时它还在里面）。
            /// ★取不到 m_ActiveActions 就明说★ 静默退化会让这道护栏看起来"有效"，
            ///   而实际什么都没做 —— 本轮已经吃过这个亏。
            /// </summary>
            private static Kingmaker.Visual.Animation.Actions.AnimationActionHandle
                FindOurJumpHandle(UnitAnimationManager mgr)
            {
                const string Ours = "DynastyRetinue_SyntheticJump";
                try
                {
                    if (mgr == null) return null;

                    var cur = mgr.CurrentAction;
                    if (cur != null && cur.Action != null && cur.Action.name == Ours) return cur;

                    if (!_lookedActive)
                    {
                        _lookedActive = true;
                        for (var t = mgr.GetType(); t != null && _fActive == null; t = t.BaseType)
                            _fActive = t.GetField("m_ActiveActions",
                                System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.Instance);
                        if (_fActive == null && !_warnedActive)
                        {
                            _warnedActive = true;
                            Main.Log("[跳跃动作] ★看门狗不完整★ 反射取不到 AnimationManager.m_ActiveActions，"
                                   + "只能看 CurrentAction。★「飞行中被抢走」那种事故仍抓不到，"
                                   + "这正是这道闸存在的理由，所以必须知道它现在残废了。★");
                        }
                    }
                    var list = _fActive != null ? _fActive.GetValue(mgr) as System.Collections.IList : null;
                    if (list != null)
                        foreach (var o in list)
                        {
                            var h = o as Kingmaker.Visual.Animation.Actions.AnimationActionHandle;
                            if (h != null && h.Action != null && h.Action.name == Ours) return h;
                        }
                }
                catch { }
                return null;
            }

            internal static void Arm(BaseUnitEntity u)
            {
                try
                {
                    if (u == null) return;
                    if (_armed.Count > 16) _armed.Clear();
                    _armed[u] = UnityEngine.Time.realtimeSinceStartup;
                }
                catch { }
            }

            internal static void Tick()
            {
                try
                {
                    if (_armed.Count == 0) return;                       // ★O(1) 早退闸★
                    float now = UnityEngine.Time.realtimeSinceStartup;
                    System.Collections.Generic.List<BaseUnitEntity> drop = null;

                    foreach (var kv in _armed)
                    {
                        var u = kv.Key;
                        bool jumping = false;
                        try
                        {
                            var jp = u != null
                                ? u.GetOptional<Kingmaker.UnitLogic.Parts.UnitPartJump>() : null;
                            jumping = jp != null && jp.Active != null;
                        }
                        catch { }
                        // 还在跳、且没超时 ⇒ 继续等
                        if (jumping && now - kv.Value < 3f) continue;

                        (drop ?? (drop = new System.Collections.Generic.List<BaseUnitEntity>())).Add(u);

                        // 落地（或超时）后循环还挂着 ⇒ 强制放行，别让它无限跑
                        try
                        {
                            var view = u != null
                                ? u.View as Kingmaker.View.Mechanics.Entities.AbstractUnitEntityView : null;
                            var mgr = view != null ? view.AnimationManager : null;
                            // ★1.7.82 修判据★ 原来判的是 mgr.CurrentAction is UnitAnimationActionJump，
                            //   而这道闸要防的事故恰恰是「**飞行中 CurrentAction 被别的动作抢走**」——
                            //   真被抢走时这个判据当场为假，它抓不到自己注释里写的那件事。
                            //   实测「救场 0 次」只说明没发生，★不能当成护栏有效★。
                            //   现在改成：按名字找**我们自己造的那个**句柄，不再要求它仍是 CurrentAction。
                            // ★三重收窄，避免重蹈两次旧事故★
                            //   (a) 只认 name == DynastyRetinue_SyntheticJump —— 绝不碰引擎自己的或攻击类句柄
                            //       （排除 1.7.68「攻击片段进循环 ⇒ 无限挥刀」）
                            //   (b) 只在确认已落地（UnitPartJump.Active == null）之后才动手，飞行中一律不碰
                            //       （排除 1.7.67「还没播就被掐 ⇒ 卡死」）
                            //   (c) 保留 3 秒超时与 _armed.Count==0 的 O(1) 早退闸
                            var cur = FindOurJumpHandle(mgr);
                            if (cur != null && !cur.IsReleased)
                            {
                                cur.Release();
                                Rescued++;
                                Main.Log("[跳跃动作] 看门狗：落地后飞行循环还挂着，已强制释放（"
                                       + (u.CharacterName ?? "?") + "）。"
                                       + "★这一行出现说明引擎的 FinishJumpFlyAnimation 没收上尾"
                                       + "——多半是飞行中 CurrentAction 被别的动作抢走了。★");
                            }
                        }
                        catch { }
                    }
                    if (drop != null) foreach (var u in drop) _armed.Remove(u);
                }
                catch { }
            }
        }

        /// <summary>跳跃一开始就武装看门狗。UnitPartJump.Jump 是跳跃的唯一入口。</summary>
        [HarmonyPatch(typeof(Kingmaker.UnitLogic.Parts.UnitPartJump), "Jump")]
        internal static class ArmWatchdog
        {
            private static void Postfix(Kingmaker.UnitLogic.Parts.UnitPartJump __instance)
            {
                try
                {
                    if (!Main.Enabled || !AnimFallback.AnyMeleeEliteActive) return;
                    var u = __instance.Owner as BaseUnitEntity;
                    if (u == null || !WeaponGate.IsGateTarget(u)) return;
                    JumpWatchdog.Arm(u);
                }
                catch { }
            }
        }
    }
}
