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
        private static readonly System.Collections.Generic.Dictionary<string, AnimationClipWrapper> _ownClip2 =
            new System.Collections.Generic.Dictionary<string, AnimationClipWrapper>(StringComparer.Ordinal);

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

        private static AnimationClipWrapper OwnClipFor(UnitAnimationActionHandle uh, AnimationActionHandle raw)
        {
            try
            {
                var mgr = uh.Manager;
                if (mgr == null) return null;
                // ★按「管理器 + 动作用途」缓存★ 同一个单位在不同用途下要拿到不同的片段
                string kind = KindOf(raw);
                string key = mgr.GetInstanceID() + "|" + kind;
                AnimationClipWrapper cached;
                if (_ownClip2.TryGetValue(key, out cached)) return cached;

                AnimationClipWrapper found = null;
                if (kind == "loop")
                {
                    // 循环动作 ⇒ 只能给待机：可循环、中性。给攻击会无限挥刀。
                    found = FirstClip(mgr.GetAction(UnitAnimationType.LocoMotion));
                }
                else if (kind == "attack")
                {
                    found = FirstClip(mgr.GetAction(UnitAnimationSpecialAttackType.Melee));
                    if (found == null) found = FirstClip(mgr.GetAction(UnitAnimationType.MainHandAttack));
                }
                else
                {
                    // 其它（含位移/施法）⇒ 先试移动，再退到待机；一律不给攻击
                    found = FirstClip(mgr.GetAction(UnitAnimationType.LocoMotion));
                }

                if (_ownClip2.Count > 64) _ownClip2.Clear();
                _ownClip2[key] = found;
                return found;
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

        private static bool Prefix(AnimationActionHandle __instance, ref AnimationClipWrapper clipWrapper)
        {
            try
            {
                if (!AnimFallback.AnyMeleeEliteActive) return true;   // ★全局闸★
                if (clipWrapper == null) return true;

                var uh = __instance as UnitAnimationActionHandle;
                if (uh == null || !AnimFallback.Applies(uh)) return true;

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
                clipWrapper = alt;                            // ★换掉，继续走原版流程★
                return true;
            }
            catch { }
            return true;               // 出错一律交回原版
        }
    }
}
