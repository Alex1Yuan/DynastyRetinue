using System;
using System.Collections.Generic;
using System.Reflection;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;

namespace DynastyRetinue
{
    /// <summary>
    /// 指令卡顿探针 —— 回答「谁卡住了、卡在哪一步、卡的是哪个技能」。
    ///
    /// ★为什么需要它★
    ///   作者反馈「电僧卡住」「卡在敌人回合」「超时才结束」，而我手上三个探针
    ///   一个都答不了这个问题：
    ///     CombatWatch  只量 AI **决策**耗时（决策完了之后的事它不管）
    ///     StuckWatch   只管非战斗的走位卡住
    ///     FrameWatch   只量帧率，不知道是谁在跑什么
    ///   于是我只能靠静态追代码去猜是哪条动画路径，连猜四次全错，白花五个版本。
    ///
    /// ★1.7.33 修：反射缓存必须按类型分开★
    ///   上一版把 _pTime/_pAbility 这些做成**单份静态字段**，谁先来就按谁的类型缓存。
    ///   实际跑起来先到的是 UnitMoveTo，于是 _pAbility 被钉成 null；
    ///   等真正卡住的 UnitUseAbility 来的时候，拿到的是**别的类型的** PropertyInfo，
    ///   GetValue 直接抛异常被 catch 吞掉 ⇒ 报告里技能名永远是「-」。
    ///   ★这就是上一轮最关键的信息缺失★：我们知道电僧卡了 5.5 秒，
    ///   却不知道它卡在哪个技能上，等于白拍一张快照。
    ///   现在按 Type 存字典，每个指令类型各存各的。
    ///
    /// ★1.7.33 增：把动画句柄的三个状态位一起拍下来★
    ///   区分两种完全不同的死法，它们的修法相反：
    ///     ActiveAnimation == null  ⇒ 片段压根没进播放图（查表失败/被抢占），
    ///                                 引擎 Update 第一行就 return，永远走不到 Release
    ///     ActiveAnimation != null  ⇒ 片段进去了但不结束（循环片段/没有 exit）
    ///   上一版我只看了 IsFinished，把这两种混为一谈，补了个只对前者有效的 Release，
    ///   而实测拍到的是后者 —— 所以没救到。
    ///
    /// ★频率与开销★
    ///   每秒最多查一次，且只在战斗中、只看我们的卫兵。
    ///   RetinueRegistry.All() 本身带 10 秒缓存（1.5.12），一秒一次可以接受 ——
    ///   但**绝不能**放进每帧路径，那是 perf-hot-paths 那条红线。
    /// ★去重★ 同一个（单位+指令+技能）只报一次，卡 30 秒也只有一行。
    /// </summary>
    internal static class CommandStallWatch
    {
        /// <summary>超过这个秒数还没结束就报。普通技能一两秒就完，5 秒足够宽松。</summary>
        private const float ThresholdSec = 5f;

        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static float _next;
        private static readonly HashSet<string> _reported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>一个指令类型对应的一组属性句柄。★按类型存，不能共用★</summary>
        private sealed class CmdProps
        {
            internal PropertyInfo Time, Finished, Started, Anim, HasAnim, Blocker, Result, Ability;
        }

        private static readonly Dictionary<Type, CmdProps> _cmdCache = new Dictionary<Type, CmdProps>();
        private static readonly Dictionary<Type, PropertyInfo[]> _animCache = new Dictionary<Type, PropertyInfo[]>();

        private static PropertyInfo _pCommands, _pCurrent;
        private static bool _lookedCommands;

        internal static void Tick()
        {
            try
            {
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.DiagVerbose) return;

                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now < _next) return;
                _next = now + 1f;                      // 每秒一次

                var game = Game.Instance;
                // Player.IsInCombat 不包含 ExCompanion 卫兵，可能先变 false；
                // #49 正是这个窗口。Player 或 TurnController 任一仍在战斗就继续拍现场。
                bool inCombat = false;
                try
                {
                    var tc = game != null ? game.TurnController : null;
                    inCombat = game != null && game.Player != null
                            && (game.Player.IsInCombat || (tc != null && tc.InCombat));
                }
                catch { }
                if (!inCombat) { _reported.Clear(); return; }

                var list = RetinueRegistry.All();
                if (list == null) return;
                foreach (var g in list)
                {
                    if (g == null) continue;
                    Inspect(g);
                }
            }
            catch { }
        }

        private static CmdProps PropsFor(Type ct)
        {
            CmdProps p;
            if (_cmdCache.TryGetValue(ct, out p)) return p;
            p = new CmdProps
            {
                Time     = ct.GetProperty("TimeSinceStart", Any),
                Finished = ct.GetProperty("IsFinished",     Any),
                Started  = ct.GetProperty("IsStarted",      Any),
                Anim     = ct.GetProperty("Animation",      Any),
                HasAnim  = ct.GetProperty("HasAnimation",   Any),
                Blocker  = ct.GetProperty("StartBlocker",   Any),
                Result   = ct.GetProperty("Result",         Any),
                Ability  = ct.GetProperty("Ability",        Any),   // 只有 UnitUseAbility 有
            };
            _cmdCache[ct] = p;
            return p;
        }

        /// <summary>动画句柄的 [IsFinished, IsReleased, IsStarted, ActiveAnimation]，同样按类型缓存。</summary>
        private static PropertyInfo[] AnimPropsFor(Type ht)
        {
            PropertyInfo[] a;
            if (_animCache.TryGetValue(ht, out a)) return a;
            a = new[]
            {
                ht.GetProperty("IsFinished",      Any),
                ht.GetProperty("IsReleased",      Any),
                ht.GetProperty("IsStarted",       Any),
                ht.GetProperty("ActiveAnimation", Any),
            };
            _animCache[ht] = a;
            return a;
        }

        private static string Get(PropertyInfo pi, object o)
        {
            try
            {
                if (pi == null || o == null) return "?";
                var v = pi.GetValue(o, null);
                return v == null ? "null" : v.ToString();
            }
            catch { return "?"; }
        }

        /// <summary>
        /// ★T-pose 现场快照★ —— 作者反馈「锈行猎手回合结束之后一直摆大字」，
        /// 而我一直靠他用眼睛描述。他说得对：该把动画的起止和技能周期打出来，比肉眼准。
        ///
        /// 判据：在战斗中、这个卫兵**没有任何指令在跑**、而动画层也是空的
        ///       （CurrentAction 为空或已结束、且移动动画句柄不在播）——
        ///       那就是「站着不动且没有动画」= 绑定姿势。
        ///
        /// ★为什么这能定位★ T-pose 有两种完全不同的成因，修法相反：
        ///   · 动画层空的        ⇒ 没有 clip 在播（该查为什么没有动作接管 idle）
        ///   · 有动作但没有片段  ⇒ clip 查表失败（该查风格/动画集）
        /// 这一行把 CurrentAction / 队列 / 移动句柄 / idle 状态一起拍下来，一次分清。
        ///
        /// ★去重★ 同一个单位、同一种状态组合只报一次，不会刷屏。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _idleReported =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static void IdleSnapshot(BaseUnitEntity u)
        {
            try
            {
                if (_pCommands == null) return;
                var cmds = _pCommands.GetValue(u, null);
                if (cmds == null || _pCurrent == null) return;
                if (_pCurrent.GetValue(cmds, null) != null) return;   // 还在执行指令，不是待机态

                var view = u.View as Kingmaker.View.Mechanics.Entities.AbstractUnitEntityView;
                var mgr = view != null ? view.AnimationManager : null;
                if (mgr == null) return;

                var cur = mgr.CurrentAction;
                bool curLive = cur != null && !cur.IsFinished && !cur.IsReleased;
                var loco = mgr.LocoMotionHandle;
                bool locoLive = loco != null && !loco.IsFinished && !loco.IsReleased;
                int active = 0;
                try { var aa = mgr.ActiveActions; if (aa != null) active = aa.Count; } catch { }
                int queued = 0;
                try { var q = mgr.SequencedActions; if (q != null) queued = q.Count; } catch { }

                // ★1.7.56 改成无条件记录★
                //   1.7.45 我加了「片段在播就 return」的判据，结果连续两版 [大字现场] 都是 0 条，
                //   而作者明确看到大字、录像里也是标准 bind pose（双臂水平伸直）。
                //   说明**我自己判断「是不是大字」的那套判据本身就是错的** ——
                //   引擎里存在「片段在播但图层权重为 0」这种状态，日志侧看是正常，画面上什么都没有。
                //   ⇒ 探针不该替我做判断。现在改成：只要「没有指令在跑」，
                //     就把动画层的完整状态原样记下来，由我事后对着画面比对。
                //   ★去重★ 按状态签名去重，状态不变就不重复打，不会刷屏。
                bool curPlaying = curLive && cur.ActiveAnimation != null;
                bool locoPlaying = locoLive && loco.ActiveAnimation != null;

                string curName = cur == null ? "null"
                    : cur.GetType().Name + "/" + (cur.Action == null ? "null" : cur.Action.GetType().Name)
                      + "(IsFinished=" + cur.IsFinished + ",IsReleased=" + cur.IsReleased + ")";
                string locoName = loco == null ? "null"
                    : "(IsFinished=" + loco.IsFinished + ",IsReleased=" + loco.IsReleased
                      + ",ActiveAnimation=" + (loco.ActiveAnimation == null ? "null" : "有") + ")";

                string key = (u.CharacterName ?? "?") + "|" + curName + "|" + locoName + "|" + active + "|" + queued;
                if (!_idleReported.Add(key)) return;

                Main.Log("[待机现场] " + (u.CharacterName ?? "?") + "　没有指令在跑，动画层状态如下"
                       + "  CurrentAction=" + curName
                       + "  移动动画句柄=" + locoName
                       + "  在播片段: CurrentAction=" + (curLive && cur.ActiveAnimation != null ? "有" : "无")
                       + " 移动=" + (locoLive && loco.ActiveAnimation != null ? "有" : "无")
                       + "  ActiveActions=" + active + " 个　队列=" + queued + " 个"
                       + "  ★怎么读★ 「在播片段」两个都是「无」⇒ 图里没东西，必然 bind pose；两个都「有」而画面仍是大字 ⇒ 片段在播但图层权重为 0（日志侧看不出来，只能靠画面）；移动句柄=null ⇒ 移动层被打死，只有 ResetLocoMotion 会重建。");
            }
            catch { }
        }

        private static void Inspect(BaseUnitEntity u)
        {
            IdleSnapshot(u);
            try
            {
                if (!_lookedCommands)
                {
                    _lookedCommands = true;
                    _pCommands = u.GetType().GetProperty("Commands", Any);
                }
                if (_pCommands == null) return;
                var cmds = _pCommands.GetValue(u, null);
                if (cmds == null) return;

                if (_pCurrent == null) _pCurrent = cmds.GetType().GetProperty("Current", Any);
                if (_pCurrent == null) return;
                var cmd = _pCurrent.GetValue(cmds, null);
                if (cmd == null) return;

                var ct = cmd.GetType();
                var p = PropsFor(ct);

                float t = 0f;
                try { if (p.Time != null) t = Convert.ToSingle(p.Time.GetValue(cmd, null)); } catch { }
                if (t < ThresholdSec) return;

                bool fin = false;
                try { if (p.Finished != null) fin = (bool)p.Finished.GetValue(cmd, null); } catch { }
                if (fin) return;

                // ── 技能名 ── 这才是上一轮缺的那一条
                string ab = "-";
                try
                {
                    if (p.Ability != null)
                    {
                        var a = p.Ability.GetValue(cmd, null);
                        if (a != null)
                        {
                            var bpPi = a.GetType().GetProperty("Blueprint", Any);
                            var b = bpPi != null
                                  ? bpPi.GetValue(a, null) as Kingmaker.Blueprints.SimpleBlueprint : null;
                            if (b != null) ab = b.name;
                        }
                    }
                }
                catch { }

                string key = (u.CharacterName ?? "?") + "|" + ct.Name + "|" + ab;
                if (!_reported.Add(key)) return;

                // ── 现场快照 ──
                string started = Get(p.Started, cmd);
                string hasAnim = Get(p.HasAnim, cmd);
                string result  = Get(p.Result,  cmd);

                string blocker = "-";
                try
                {
                    var bl = p.Blocker != null ? p.Blocker.GetValue(cmd, null) : null;
                    if (bl != null) blocker = bl.GetType().Name;
                }
                catch { }

                // ── 动画句柄：四个状态位 + 句柄自己的类型（能反推是哪条动画链路）──
                string animLine = "句柄为 null";
                try
                {
                    var h = p.Anim != null ? p.Anim.GetValue(cmd, null) : null;
                    if (h != null)
                    {
                        var ap = AnimPropsFor(h.GetType());
                        animLine = "类型=" + h.GetType().Name
                                 + "  IsFinished=" + Get(ap[0], h)
                                 + "  IsReleased=" + Get(ap[1], h)
                                 + "  IsStarted="  + Get(ap[2], h)
                                 + "  ActiveAnimation=" + (Get(ap[3], h) == "null" ? "null" : "有");

                        // ★1.7.36 删★ 这里原来是 h.GetType().GetProperty("Action", Any) ——
                        //   和 Manager 一样是**二义反射**：UnitAnimationActionHandle 用 new
                        //   重声明了协变的 Action（返回 UnitAnimationAction），基类返回
                        //   AnimationActionBase；两个 getter 都非虚，反射既不能按 vtable 槽去重、
                        //   签名又因返回类型不同而不相等 ⇒ 候选数 2 ⇒ 必抛 AmbiguousMatchException
                        //   ⇒ 被下面的 catch 吞掉 ⇒「动作类=」**永远打不出来**。
                        //   决定性快照里「请求风格=Fist」有、「动作类=」没有，就是这个原因 ——
                        //   不是 Action 为 null（派生 getter 是 castclass，对 null 返回 null 不抛）。
                        //   下面用强类型的 uh.Action 取，已覆盖。

                        // 请求的武器动画风格 —— T-pose 排查的关键输入
                        try
                        {
                            var stPi = h.GetType().GetProperty("AttackWeaponStyle", Any);
                            if (stPi != null) animLine += "  请求风格=" + Get(stPi, h);
                        }
                        catch { }

                        // ★1.7.36 修：别再反射 Manager★
                        //   1.7.35 用 h.GetType().GetProperty("Manager") 取，整段没有输出 ——
                        //   和 1.7.28 那次 EntityData 是同一个错误：编译期够得着的成员去反射，
                        //   一抛异常就被 catch 吞掉，外面看起来像「这段代码没跑」。
                        //   句柄的运行时类型日志里已经写明是 UnitAnimationActionHandle，直接转型即可。
                        //   ★而且 Manager 为 null 本身就是关键数据★ —— 没绑上管理器的句柄
                        //   根本不会被 UpdateActions 处理，那就直接解释了 IsStarted 永远为 False。
                        //   所以无论是不是 null 都要打出来，不能像上一版那样静默跳过。
                        var uh = h as Kingmaker.Visual.Animation.Kingmaker.UnitAnimationActionHandle;
                        if (uh == null)
                        {
                            animLine += "\n    ★通道★ 句柄不是 UnitAnimationActionHandle，转型失败";
                        }
                        else
                        {
                            Kingmaker.Visual.Animation.Kingmaker.UnitAnimationManager mgr = null;
                            string mgrNote;
                            try { mgr = uh.Manager; mgrNote = mgr == null ? "null" : "有"; }
                            catch (Exception e) { mgrNote = "取值抛了 " + e.GetType().Name; }
                            animLine += "\n    ★通道★ Manager=" + mgrNote;

                            if (mgr != null)
                            {
                                try
                                {
                                    var cur = mgr.CurrentAction;
                                    string curName = "null";
                                    if (cur != null)
                                    {
                                        curName = cur.GetType().Name;
                                        try { if (cur.Action != null) curName += "/" + cur.Action.GetType().Name; }
                                        catch { }
                                        try
                                        {
                                            curName += "(IsFinished=" + cur.IsFinished
                                                     + ",IsReleased=" + cur.IsReleased
                                                     + ",IsStarted=" + cur.IsStarted + ")";
                                        }
                                        catch { }
                                    }
                                    animLine += "　CurrentAction=" + curName;
                                }
                                catch (Exception e) { animLine += "　CurrentAction 读失败:" + e.GetType().Name; }

                                try
                                {
                                    var q = mgr.SequencedActions;
                                    animLine += "　队列 " + (q == null ? "null" : q.Count.ToString()) + " 个";
                                }
                                catch (Exception e) { animLine += "　队列读失败:" + e.GetType().Name; }
                            }

                            // 句柄自己的动作对象 —— 直接点名该补哪个 UnitAnimationAction 子类
                            try
                            {
                                animLine += "　本句柄动作=" + (uh.Action == null ? "null" : uh.Action.GetType().Name);
                            }
                            catch (Exception e) { animLine += "　本句柄动作读失败:" + e.GetType().Name; }

                            // ★1.7.36 新增：一击定案的三个数★
                            //   工作流把根因追到 IL 级了，但「这一次到底是不是那条路」是纯运行时事实。
                            //   这三行分别验三条假设，任何一条不符就说明根因判断错了，别再往那个方向修。
                            try
                            {
                                // ① 攻击资源是不是排队型 —— 电僧应为 Sequenced，锈行猎手应为 Interrupted
                                //    若是 Interrupted，整条「排队饿死」的推理作废
                                animLine += "\n    ★判据★ 本句柄 ExecutionMode="
                                          + (uh.Action == null ? "?" : uh.Action.ExecutionMode.ToString());
                            }
                            catch (Exception e) { animLine += "\n    ★判据★ ExecutionMode 读失败:" + e.GetType().Name; }

                            try
                            {
                                // ② 句柄在不在 m_ActiveActions 里 —— 期望 False（=还在队列里没出队）
                                //    若是 True，问题就不在队列，而在 UpdateActions 顶部循环，方向要换
                                bool inActive = false;
                                var mgr2 = uh.Manager;
                                var act2 = mgr2 != null ? mgr2.ActiveActions : null;
                                if (act2 != null)
                                {
                                    // ★用普通 for + 引用比较★ 不用 LINQ：这是诊断路径，别引入分配
                                    for (int i = 0; i < act2.Count; i++)
                                        if (ReferenceEquals(act2[i], uh)) { inActive = true; break; }
                                    animLine += "　在ActiveActions里=" + inActive + "（表长 " + act2.Count + "）";
                                }
                                else animLine += "　ActiveActions=null";
                            }
                            catch (Exception e) { animLine += "　ActiveActions 读失败:" + e.GetType().Name; }

                            try
                            {
                                // ③ 句柄的 Manager 和视图的 AnimationManager 是不是同一个实例
                                //    若为 False，Execute 会打 "created by another manager" 并静默 return
                                var v = uh.Unit;
                                var vm = v != null ? v.AnimationManager : null;
                                animLine += "　Manager与视图同一个=" + ReferenceEquals(uh.Manager, vm);
                            }
                            catch (Exception e) { animLine += "　Manager 比对失败:" + e.GetType().Name; }
                        }
                    }
                }
                catch { }

                Main.Log("[指令卡顿] " + (u.CharacterName ?? "?")
                       + "　指令=" + ct.Name + "　技能=" + ab
                       + "　已跑 " + t.ToString("F1") + " 秒未结束"
                       + "\n    IsStarted=" + started + "　HasAnimation=" + hasAnim
                       + "　Result=" + result + "　被谁挡=" + blocker
                       + "\n    动画: " + animLine
                       + "\n    ★怎么读★ ActiveAnimation=null ⇒ 片段没进播放图（查表失败或被抢占），"
                       + "该修的是查表/抢占；ActiveAnimation=有 而 IsFinished 一直 False ⇒ 片段进去了但不结束"
                       + "（循环片段或缺 exit），该修的是结束条件。"
                       + "「动作类」直接指出该补哪个 UnitAnimationAction 子类；"
                       + "「请求风格」是查表用的 WeaponAnimationStyle。");
            }
            catch { }
        }
    }
}
