using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Visual.Animation;
using Kingmaker.Visual.Animation.Actions;
using Kingmaker.Visual.Animation.Kingmaker;

namespace DynastyRetinue
{
    /// <summary>
    /// ================= 异骨架片段拦截（1.7.66）=================
    ///
    /// ★根因（实测确证，查了二十多个版本）★
    ///   回合结束后两个近战精英呈标准 bind pose。混合器权重探针拿到的现场：
    ///       层0 遮罩=null 骨骼数=2147483647 输入=2
    ///           权重=[0.00(Sicarian_LoMo_Idle 长=6.00 空=False)
    ///                 ★1.00(Reaper_1H_Brutal_OHA_Flanking_Loop 长=1.17 空=False)★]
    ///   时间轴对齐：锈行猎手回合 19:59:51 结束 → 20:02:15 / 20:02:55 两次采样
    ///   主导片段都是那个 Reaper 循环 → 20:03:15 第二回合开始，恢复正常。
    ///
    ///   `Reaper_*` 是**收割者线（绮贝菈）的片段，为人类骨架制作**；
    ///   而锈行猎手 / 电僧是 DLC3 的机械教骨架。
    ///   **在错误的骨架上播片段，骨骼对不上，角色就塌回绑定姿势。**
    ///
    /// ★为什么之前所有假说都不成立★ 这条一次解释了全部矛盾证据：
    ///     片段有效（空=False 长=1.17）—— 片段本身没问题，只是不属于这个骨架
    ///     权重 1.00 非零        —— 确实在驱动，只是驱动的骨骼不存在
    ///     句柄健康、队列干净     —— 动画系统一切正常
    ///     引擎零报错            —— 它不检查骨架兼容性
    ///     第二回合自动恢复       —— 循环片段被新指令顶掉了
    ///   我先后猜过 BlockAttackAnimation、OverrideAnimationSet、useEmptyAvatarMask、
    ///   图层权重归零、空片段 —— 全错，因为它们都假设「动画层出了问题」，
    ///   而真相是**动画层完全正常，只是喂了一份不匹配的资源**。
    ///
    /// ★为什么它偏偏在回合末最明显★ 那是个 `_Loop` 片段：循环、自己不结束、权重 1.00。
    ///   回合中不断有新动作把它顶掉，所以只是偶尔闪一下；
    ///   回合结束后**没有新指令**，它就一直占着，大字持续整段敌方回合。
    ///
    /// ═══ 修法 ═══
    /// 这两个卫兵身上，拦掉**明显不属于它们骨架**的片段，让原版退回它自己的待机。
    /// ★为什么这是净收益★ 不拦 = bind pose（什么都看不出来）；拦掉 = 保持自己的待机动作。
    ///   后者严格更好 —— 我们并没有"失去"一个能看的动画，那个动画本来就播不出来。
    /// ★判据用片段名前缀★ 单位自己的片段是 `Sicarian_*` / `ElectroPriest_*`
    ///   （同族的 `SicarianChaos_*` 实测能正常播，骨架兼容，所以不拦）。
    ///   `Reaper_*` 是绮贝菈那套。用前缀而不是"比对动画集"，是因为前者 O(1) 且不会漏
    ///   —— 动画集里本来就没有这些片段，比对不出来。
    /// ★作用域★ 全局存在闸 + Applies，只有我们两个近战精英。
    /// ★开销★ StartClip 是事件驱动（每次动作起播一次），不是每帧；
    ///   第一道闸是静态 bool，非目标一次比较就走。
    /// </summary>
    [HarmonyPatch(typeof(AnimationActionHandle), "StartClip",
                  new Type[] { typeof(AnimationClipWrapper), typeof(ClipDurationType) })]
    internal static class ForeignRigClipBlockPatch
    {
        /// <summary>不属于这两个单位骨架的片段名前缀。实测 Reaper_* 是绮贝菈的人类骨架片段。</summary>
        private static readonly string[] ForeignPrefixes = { "Reaper_" };

        private static PropertyInfo _clipP, _pAvatar, _pIsHuman;
        private static Type _animT;
        private static MethodInfo _mGetComp;
        private static bool _looked;


        /// <summary>
        /// 这个单位的骨架是不是 Humanoid（能被 Unity 重定向）。
        /// 拿不到一律返回 true（放行，不干预）—— 见上面「拿不到就放行」的理由。
        /// ★缓存★ 按单位缓存，避免每次起播都走一遍组件查找。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<object, bool> _humanoid =
            new System.Collections.Generic.Dictionary<object, bool>();

        private static bool IsHumanoid(UnitAnimationActionHandle uh)
        {
            try
            {
                var view = uh.Unit;
                if (view == null) return true;
                bool hit;
                if (_humanoid.TryGetValue(view, out hit)) return hit;

                hit = true;                                  // 默认放行

                // ★反射★ UnityEngine.Animator 在 UnityEngine.AnimationModule 里，
                //   本工程没引用那个程序集（和 AnimationClip 同样的情况）。
                //   为一个判据去加引用不值得——多一个程序集依赖就多一处版本风险。
                if (_animT == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _animT = asm.GetType("UnityEngine.Animator", false);
                        if (_animT != null) break;
                    }
                    if (_animT == null) { _humanoid[view] = true; return true; }
                    _pAvatar = _animT.GetProperty("avatar",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (_pAvatar != null)
                        _pIsHuman = _pAvatar.PropertyType.GetProperty("isHuman",
                            BindingFlags.Public | BindingFlags.Instance);
                    _mGetComp = typeof(UnityEngine.Component)
                        .GetMethod("GetComponentInChildren", new Type[] { typeof(Type) });
                }

                if (_mGetComp != null && _pAvatar != null && _pIsHuman != null)
                {
                    var anim = _mGetComp.Invoke(view, new object[] { _animT });
                    if (anim != null)
                    {
                        var av = _pAvatar.GetValue(anim, null);
                        if (av != null && !((UnityEngine.Object)av == null))
                            hit = Convert.ToBoolean(_pIsHuman.GetValue(av, null));   // ★真正的判据★
                    }
                }
                if (_humanoid.Count > 64) _humanoid.Clear();  // 换区攒多了倒掉
                _humanoid[view] = hit;
                return hit;
            }
            catch { return true; }
        }


        /// <summary>
        /// 从这个单位**自己的**动画集里取一个可用片段，用来顶替异骨架片段。
        /// 优先近战特殊攻击（动作幅度接近技能动画），拿不到就退到移动/待机。
        /// ★不新增任何资源★ 全部来自单位自带的动作。
        /// ★缓存★ 按单位缓存一次，避免每次起播都遍历。
        /// </summary>
        /// <summary>被替换的这个动作是干什么用的 —— 决定该给它哪种替代片段。</summary>
        private static string KindOf(AnimationActionHandle raw)
        {
            try
            {
                var a = raw != null ? raw.Action : null;
                if (a == null) return "other";
                string n = a.GetType().Name;
                if (n.IndexOf("BuffLoop", StringComparison.Ordinal) >= 0) return "loop";
                if (n.IndexOf("Attack", StringComparison.Ordinal) >= 0) return "attack";
            }
            catch { }
            return "other";
        }

        /// <summary>
        /// 被补丁 J 标记为「带位移的施法」的句柄。StartClip 看到它就把片段换成 ForceMove。
        /// ★用弱引用式的有界集合★ 句柄用完不会通知我们，攒够了整桶倒掉，避免泄漏。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<object> _displacement =
            new System.Collections.Generic.HashSet<object>();

        internal static void MarkDisplacement(object handle)
        {
            try
            {
                if (handle == null) return;
                if (_displacement.Count > 32) _displacement.Clear();
                _displacement.Add(handle);
            }
            catch { }
        }


        // ═══════════════ 1.7.73：片段挑选全部改成「按角色挑」 ═══════════════
        //
        // ★作者的问题★「你的待机动作用的是战斗待机动作吗 还是什么其他的待机动作」
        //   —— 我答不上来，因为我用的是 FirstClip()：取 ClipWrappers 里第一个非空的。
        //   工作流把顺序 dump 出来，答案是**非战斗待机**：
        //     [0] ElectroPriest_LoMo_Idle_NonCombat  2.667s   ← 我一直取的是这个
        //     [1] null
        //     [2] ElectroPriest_LoMo_Run_speed_7     0.667s
        //     [3] 1H_Freehands_MHA_LoMo_Run_speed_9_In   ← ★外部人类骨架包★
        //     [4] 1H_Freehands_MHA_LoMo_Run_speed_9_Out  ← ★同上★
        //     [5] ElectroPriest_LoMo_Idle            2.000s   ← 战斗待机在这
        //   所以卫兵在战斗中站的是**休闲姿势**。这就是作者看到的「过去之后站着不动」。
        //
        // ★[3][4] 是地雷★ 它们不在 electropriest.animations 里，PPtr fileID=2 指向
        //   humananimation.animations —— 就是本文件开头那个「异骨架 = bind pose」的同一类。
        //   我原来按 "Run" 找只是**碰巧**先撞上 [2]。顺序一变就会把人类片段塞给机械教骨架。
        //
        // ★为什么这次按名字挑不算「凭名字猜语义」★
        //   前面栽过两次（useEmptyAvatarMask 读反、ForceMove 当成「位移动画」），
        //   区别在于：那两次是**从名字推断行为**；这次是**资源字段的角色已经确证**
        //   —— LocoMotionHuman.NonCombatIdle 这个字段指向的就是 `_NonCombat` 那个片段，
        //   CombatIdle 列表里三项全是 `ElectroPriest_LoMo_Idle`。名字只用来复现已知映射。

        private enum Want { CombatIdle, Run }

        /// <summary>
        /// 这个单位自己的片段族前缀（ElectroPriest / Sicarian）。
        /// ★用途★ 把外部包混进来的片段（1H_Freehands_*）挡在外面 —— 那些是人类骨架的。
        /// </summary>
        private static string FamilyOf(AnimationActionBase loco)
        {
            try
            {
                foreach (var w in loco.ClipWrappers)
                {
                    string n = ClipName(w);
                    if (string.IsNullOrEmpty(n)) continue;
                    int i = n.IndexOf('_');
                    if (i > 0) return n.Substring(0, i);
                }
            }
            catch { }
            return null;
        }

        private static string ClipName(AnimationClipWrapper w)
        {
            try
            {
                if (!AnimFallback.HasClip(w)) return null;
                return (w as UnityEngine.Object) != null ? ((UnityEngine.Object)w).name : null;
            }
            catch { return null; }
        }

        private static readonly System.Collections.Generic.Dictionary<string, AnimationClipWrapper> _pick =
            new System.Collections.Generic.Dictionary<string, AnimationClipWrapper>(StringComparer.Ordinal);

        private static AnimationClipWrapper PickLoco(UnitAnimationManager mgr, Want want)
        {
            try
            {
                if (mgr == null) return null;
                string key = mgr.GetInstanceID() + "|" + (int)want;
                AnimationClipWrapper cached;
                if (_pick.TryGetValue(key, out cached)) return cached;

                AnimationClipWrapper hit = null;
                var loco = mgr.GetAction(UnitAnimationType.LocoMotion);
                if (loco != null)
                {
                    string fam = FamilyOf(loco);
                    foreach (var w in loco.ClipWrappers)
                    {
                        string n = ClipName(w);
                        if (n == null) continue;
                        // ★族前缀闸★ 只认这个单位自己的片段，挡掉 1H_Freehands_* 那类外部人类片段
                        if (fam != null && !n.StartsWith(fam, StringComparison.Ordinal)) continue;

                        if (want == Want.CombatIdle)
                        {
                            if (n.IndexOf("Idle", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (n.IndexOf("NonCombat", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (n.IndexOf("Micro", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (n.IndexOf("Variant", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        }
                        else
                        {
                            if (n.IndexOf("Run", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            // In/Out 是过渡片段（0.37s / 0.80s），不是主体循环，别拿它当跑步
                            if (n.EndsWith("_In", StringComparison.OrdinalIgnoreCase)) continue;
                            if (n.EndsWith("_Out", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                        hit = w; break;
                    }
                }
                if (_pick.Count > 64) _pick.Clear();
                _pick[key] = hit;
                return hit;
            }
            catch { return null; }
        }

        /// <summary>
        /// 开给 JumpAndActFix 用：取这个单位自己的跑步片段。
        /// ★为什么复用而不是再写一份★ 这里有两道不能丢的闸：族前缀过滤（挡掉
        ///   1H_Freehands_* 那类外部人类骨架片段）和 _In/_Out 过渡片段排除。
        ///   另写一份迟早会漏掉其中一条。
        /// </summary>
        internal static AnimationClipWrapper RunClipFor(UnitAnimationManager mgr)
        {
            return PickLoco(mgr, Want.Run);
        }

        private static AnimationClipWrapper MeleeClip(UnitAnimationManager mgr)
        {
            try
            {
                if (mgr == null) return null;
                var w = FirstClip(mgr.GetAction(UnitAnimationSpecialAttackType.Melee));
                if (w == null) w = FirstClip(mgr.GetAction(UnitAnimationType.MainHandAttack));
                return w;
            }
            catch { return null; }
        }

        private static AnimationClipWrapper FirstClip(AnimationActionBase act)
        {
            try
            {
                if (act == null) return null;
                foreach (var w in act.ClipWrappers)
                    if (AnimFallback.HasClip(w)) return w;
            }
            catch { }
            return null;
        }


        // ═══════════════ 跳跃阶段：位移期播跑步，落地那一下播攻击 ═══════════════
        //
        // ★死从天降的真实动画时序（实测时间轴）★
        //     22:39:58  DeathWaltzAbility       CastSpell        739 ms
        //     22:39:59  DeathWaltzAbility       BuffLoopAction  1907 ms
        //     22:40:00  DeathWaltzStrikeAbility BuffLoopAction  1992 ms
        //   施法动画只活了 739ms 就被 **buff 循环顶掉**（跟片段长度无关）。
        //   剩下 2.6 秒全是 BuffLoop 在播 —— 而 BuffLoop 走 kind=="loop" 分支，
        //   拿到的是待机片段。**这才是「跑了一下就站着不动」的真正成因**，
        //   不是跑步片段太短。我上一版盯着片段长度看，方向就错了。
        //
        // ★打击段为什么没有攻击动作（工作流 IL 级确证）★
        //     ContextActionJumpToTarget.RunAction
        //       IL_0182  get_CurrentAction              ← 主技能那个 CastSpell handle
        //       IL_01DD  set_OverrideAnimationHandle    ← 整个传给 Strike
        //     UnitUseAbility.StartAnimation
        //       OverrideAnimationHandle 非空 → 直接 ret，永不 Execute
        //   即 **Strike 没有自己的动画，它复用主技能那一个 handle**；而那个 handle
        //   739ms 就死了，所以打击时无动作可播。原版绮贝菈也是这个机制，
        //   不是 mod 引入的差异（全套 DeathWaltz 变体都引用同一个 Strike 蓝图）。
        //   ⇒ 想让打击看得见，只能在**落地那一次 BuffLoop** 上给攻击片段。
        //
        // ★风险自陈★ 「攻击片段进循环动作」正是 1.7.68 那次事故的形状
        //   （作者：「结束回合时还在一直播攻击动画」）。所以这里卡得很死：
        //     · 只对**刚离开跳跃**的那一次循环生效
        //     · 每次跳跃只用一次（_landed 去重）
        //     · 1 秒窗口外一律不认
        //   实测那次 Strike 的 BuffLoop 是有限的（1992ms、「结束时技能=已结束」），
        //   不是森罗刃网那种常驻循环，所以不该无限挥刀。
        //   ★万一作者看到落地后一直挥刀，就是这个分支，砍掉它即可，其余不受影响。★

        private static readonly System.Collections.Generic.Dictionary<object, float> _jumpSeen =
            new System.Collections.Generic.Dictionary<object, float>();
        private static readonly System.Collections.Generic.HashSet<object> _landed =
            new System.Collections.Generic.HashSet<object>();

        /// <summary>这个单位此刻是不是在跳跃途中。UnitPartJump 只在跳跃时才挂上。</summary>
        private static bool IsJumping(BaseUnitEntity u)
        {
            try
            {
                if (u == null) return false;
                var jp = u.GetOptional<Kingmaker.UnitLogic.Parts.UnitPartJump>();
                return jp != null && jp.Active != null;
            }
            catch { return false; }
        }

        /// <summary>0=不在跳　1=跳跃中　2=刚落地（本次跳跃还没用过落地攻击）</summary>
        private static int JumpPhase(BaseUnitEntity u)
        {
            try
            {
                if (u == null) return 0;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (IsJumping(u))
                {
                    if (_jumpSeen.Count > 32) { _jumpSeen.Clear(); _landed.Clear(); }
                    _jumpSeen[u] = now;
                    _landed.Remove(u);          // 新一次跳跃，落地额度重置
                    return 1;
                }
                float t;
                if (!_jumpSeen.TryGetValue(u, out t)) return 0;
                if (now - t > 1f) { _jumpSeen.Remove(u); _landed.Remove(u); return 0; }
                if (_landed.Contains(u)) return 0;
                return 2;
            }
            catch { return 0; }
        }


        /// <summary>
        /// 从这个单位**自己的**动画集里取一个可用片段，用来顶替异骨架片段。
        /// ★1.7.69 的结论仍然成立：替代片段必须匹配被替换动作的用途★
        ///   固定优先级（攻击排第一）的后果作者当场看到过：
        ///     · 锈行猎手回合结束**无限循环攻击动画**（攻击片段进了 buff 循环）
        ///     · 电僧平移很远却没有移动动画（位移也被塞了攻击片段）
        /// ★1.7.73 在此之上加了「跳跃阶段」这一维★ —— 见上面的时序分析。
        /// </summary>
        private static AnimationClipWrapper OwnClipFor(UnitAnimationActionHandle uh, AnimationActionHandle raw)
        {
            try
            {
                var mgr = uh.Manager;
                if (mgr == null) return null;
                string kind = KindOf(raw);

                if (kind == "attack") return MeleeClip(mgr);

                // 循环 / 其它（含位移施法）
                // ★1.7.74 去掉了「位移途中 ⇒ 跑」★ 见 Prefix 里那段说明：
                //   跳跃开始的时刻和循环动作起播的时刻对不齐，换了反而更乱。
                // ★保留「落地 ⇒ 挥刀」★ 这条是作者报过的缺陷（「攻击动画我也没看出来」），
                //   实测已生效（日志 [替代片段] 用途=loop → 换上 Sicarian_Spec_Melee_v1，
                //   作者也确认「然后出攻击」）。它不依赖跳跃**开始**的时刻，
                //   只依赖跳跃**已经结束**——那个判据在 StartClip 时刻是准的。
                var u = AnimFallback.UnitOf(uh);
                if (kind == "loop" && JumpPhase(u) == 2)
                {
                    var mel = MeleeClip(mgr);
                    if (mel != null) { _landed.Add(u); return mel; }  // 落地 ⇒ 挥刀（每跳一次）
                }
                // 站定 ⇒ ★战斗★待机（1.7.72 之前一直错拿非战斗待机）
                return PickLoco(mgr, Want.CombatIdle);
            }
            catch { return null; }
        }


        /// <summary>
        /// 打一次「换上去的是哪个片段」。
        /// ★为什么补这条★ 之前只打了「拦下的是谁」，作者问「待机用的是战斗待机吗」时
        ///   我手上没有任何证据能回答 —— 日志缺了输出侧这一半。
        ///   ★教训★ 只记录输入不记录输出的探针，等于没法验证自己的修复。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _subLogged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static void NoteSub(string kind, string clip)
        {
            try
            {
                var st = Main.Settings;
                if (st == null || !st.DiagVerbose) return;      // ★先判开关，字符串都不碰★
                string k = kind + "|" + (clip ?? "null");
                if (!_subLogged.Add(k)) return;
                Main.Log("[替代片段] 用途=" + kind + " → 换上 " + (clip ?? "(没找到)"));
            }
            catch { }
        }

        private static bool Prefix(AnimationActionHandle __instance, ref AnimationClipWrapper clipWrapper)
        {
            try
            {
                if (!AnimFallback.AnyMeleeEliteActive) return true;   // ★全局闸★
                if (clipWrapper == null) return true;

                var uh = __instance as UnitAnimationActionHandle;
                if (uh == null || !AnimFallback.Applies(uh)) return true;

                // ═══ 1.7.71：撤回「换上 ForceMove 片段」（1.7.70 引入）═══
                //
                // ★错在哪★ 我把 ForceMove 想当然读成「位移动画」，实际它是
                //   **「被强制位移」** —— 被击退 / 被拖拽时的踉跄、倒地动作，是**被动**的。
                //   用它顶替主动跃击，结果就是作者看到的：
                //   「锈行者死从天降变成了一个倒地动作」「电僧也是一个倒地动作然后平移过去」。
                //   而作者明确说**上一版本来还挺好的** —— 我把一个可接受的状态改坏了。
                //
                // ★教训（本轮第 N 次同一类）★ 只凭**资源名**推断用途就动手，没验证语义。
                //   前面同样栽过：useEmptyAvatarMask 按字面读成「空遮罩」（实际是「不套遮罩」）。
                //   名字是作者写给自己看的，不是 API 契约。
                //
                // ═══ 1.7.72：改用**跑步**片段（作者的建议，比我原来的想法好得多）═══
                //
                // 作者原话：「电僧这个主要是之前最后结束之前很多动作也变成了滑行，
                //            哪怕是变成跑动呢」。
                // 说得对：滑行时播**跑步**动作，看着就是「跑过去」，是自然的；
                // 而我选的 ForceMove 是**被击退时的踉跄**，方向完全反了，成了倒地滑行。
                //
                // 素材两边都有：ElectroPriest_LoMo_Run_speed_7 / Sicarian_LoMo_Run。
                // ★按名字挑 Run★ LocoMotion 动作里既有 Idle 也有 Run/Walk，
                //   取第一个会拿到 Idle（站着滑行，等于没改）。所以要按名字筛。
                // ★退路★ 找不到 Run 就什么都不做，退回补丁 J 的风格替换 —— 不制造新问题。
                // ═══ 1.7.74：位移期不再换片段（作者拍板收敛）═══
                //
                // ★为什么放弃★ 不是"做不到"，是**代价与收益不成比例**。
                //   实测下来位移期的观感取决于「哪个动作拿到哪个片段」，而那个对齐
                //   取决于**跳跃何时开始**——一个在 StartClip 这一刻还看不到的东西：
                //     · CastSpell 起播时 UnitPartJump 还没挂上 ⇒ 判不出在位移
                //     · 覆盖滑行那 1.3 秒的 buff 循环，往往在跳跃开始**之前**就起播了
                //   要修对就得加**每帧阶段监视 + 跳跃开始/结束时重新 StartClip 换片段**。
                //   而重复调 StartClip 会不会在混合器里**叠加 playable**（探针拍到过
                //   「层0 输入=2」，说明同层确实能并存多个）——★我没有验证过★。
                //   在没验证之前上这个改动，就是本轮反复犯的那个错：凭推断改，然后改坏。
                //
                // ★收益侧本来就小★ 待机已修好（作者：「战斗待机倒是没问题了」），
                //   落地攻击也出来了。剩下的只是滑行时姿势不对——**观感损失，不是功能损失**。
                //
                // ★片段本身没问题，别再往这个方向查★ 实测 LoopTime：
                //     Sicarian_LoMo_Run              True   0.7667s
                //     ElectroPriest_LoMo_Run_speed_7 True   0.6667s
                //   跑步片段是循环的。我一度推断「0.77s 塞进 6s 循环会定格」——**错的**。
                //   作者看到的那段静止是另一个循环拿到了待机片段，不是跑步定格。
                //
                // 现在退回补丁 J 的风格替换（站立施法手势 + 滑行）＝ 1.7.71 的状态。
                if (_displacement.Contains(__instance)) _displacement.Remove(__instance);

                if (!_looked)
                {
                    _looked = true;
                    _clipP = clipWrapper.GetType().GetProperty("AnimationClip",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_clipP == null) return true;

                var clip = _clipP.GetValue(clipWrapper, null) as UnityEngine.Object;
                if (clip == null) return true;
                string nm = clip.name;
                if (string.IsNullOrEmpty(nm)) return true;

                bool foreign = false;
                for (int i = 0; i < ForeignPrefixes.Length; i++)
                    if (nm.StartsWith(ForeignPrefixes[i], StringComparison.Ordinal)) { foreign = true; break; }
                if (!foreign) return true;

                // ★1.7.67 关键收窄：按骨架类型判，别一刀切★
                //   离线查出两个单位的骨架不同：
                //     电僧      Avatar=ElectroPriest_RIGAvatar，m_Human=True（Humanoid，444 个 Transform）
                //     锈行猎手  Avatar 在外部依赖包，只有 80 个 Transform，多半是 Generic
                //   而 Unity **能在 Humanoid ↔ Humanoid 之间自动重定向动画**。
                //   ⇒ 绮贝菈的 Reaper_* 片段在电僧身上很可能本来就能正常播，
                //     这正好解释作者一路观察到的「电僧基本 OK、锈行猎手到处大字」。
                //   1.7.66 那版一刀切会**误伤电僧**，把它本来能看的动画也拦掉。
                //   现在运行时直接问 Animator.avatar.isHuman：
                //     isHuman=true  ⇒ 能重定向，放行（不干预原版）
                //     isHuman=false ⇒ 无法重定向，拦掉，让它保持自己的待机
                //   ★拿不到就放行★ 判不出来时宁可不干预 —— 误拦会让能看的动画消失，
                //     而漏拦只是维持现状（大字），前者是新增损失。
                if (IsHumanoid(uh)) return true;

                // ═══ 1.7.68：改「拦掉」为「换成它自己的片段」═══
                //
                // ★1.7.67 的教训★ 我直接 return false 不起播。大字确实没了，但换来了卡死：
                //   作者实测「锈行猎手放鲜血誓言卡住」「电僧某个技能之后卡到超时」
                //   「死从天降没播攻击动画，直接移动过去」。
                //   原因很直接：片段没起播 ⇒ 句柄永远拿不到 ActiveAnimation ⇒ 指令一直等。
                //   **这正是我们前面花十几个版本治的那种死锁，我自己又造了一个。**
                //   ★通用教训：不要用「不执行」去修表现问题。★
                //   引擎的流程假设这一步会产出一个在播的片段；跳过它就是在管线上开个洞。
                //
                // 现在改成把 wrapper 换成**这个单位自己动画集里的**一个片段：
                //   片段照常起播 → 句柄正常完成 → 指令正常结束，不卡；
                //   而且播的是它自己骨架的动作，不会塌成 bind pose。
                // ★退路★ 找不到替代片段时**放行原版**（宁可大字也不卡死）——
                //   大字是观感损失，卡死是功能损失，后者严重得多。
                // ★1.7.69 按动作用途选替代片段，不能一律用攻击★
                //   1.7.68 我用固定优先级（近战攻击排第一），后果作者当场看到：
                //     · 锈行猎手回合结束还在**无限循环攻击动画** ——
                //       被替换的是 buff 循环动作，我塞了个攻击片段进去，它就一直循环
                //     · 电僧**平移很远却没有移动动画** —— 位移动作也被塞了攻击片段
                //   ⇒ 替代片段必须**匹配被替换动作的用途**。
                //   ★默认退到待机而不是攻击★：待机天然可循环、观感中性；
                //     攻击片段一旦进了循环动作就是灾难（上一版实证）。
                var alt = OwnClipFor(uh, __instance);
                if (alt == null) return true;                 // 没得换就别动

                AnimFallback.NoteForeignClip(nm);
                NoteSub(KindOf(__instance), ClipName(alt));   // ★换上去的是谁——之前没打，作者一问我答不上来★
                clipWrapper = alt;                            // ★换掉，继续走原版流程★
                return true;
            }
            catch { }
            return true;               // 出错一律交回原版
        }
    }
}
