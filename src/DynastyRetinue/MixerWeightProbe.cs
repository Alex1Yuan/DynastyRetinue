using System;
using System.Collections;
using System.Reflection;
using Kingmaker.EntitySystem.Entities;

namespace DynastyRetinue
{
    /// <summary>
    /// ================= 混合器权重探针（1.7.62）=================
    ///
    /// ★为什么需要它★
    ///   「回合末 bind pose」查了二十多个版本，我的探针一直只能读**句柄状态**
    ///   （IsStarted / IsFinished / ActiveAnimation），而实测拍到的是：
    ///       在播片段: CurrentAction=有  移动=有   队列=0   ActiveActions=1
    ///   —— 日志侧一切健康，画面却是标准 bind pose（玩家录像确认：双臂水平伸直）。
    ///   我据此猜过「图层权重为 0」，但**手上没有任何工具能验证它**，于是反复在
    ///   「没有片段」和「片段不对」之间打转，连修错方向好几版。
    ///
    /// ★工作流给出的确切机制（IL 级）★
    ///   MixerInfo.AddPlayable IL_0063：新 input 的权重**初始化为 0f**。
    ///   一个层的全部 input 权重都是 0 时，混合器输出就是 AnimationStream 的默认值
    ///   —— 那正是 bind pose。
    ///   ⇒ **bind pose 在这套播放图里唯一的物理来源就是权重归零**，不是遮罩。
    ///   （顺带证伪了我的头号假设：UseEmptyAvatarMask=true 的语义是"不套遮罩、
    ///     驱动全身"，是保护值；全库 77 个带该字段的蓝图里 UEAM=0 的有 0 个。）
    ///
    /// ★读哪些字段★（可访问性已由工作流逐个核实）
    ///   AnimationManager.m_Mixers        private List&lt;MixerInfo&gt;
    ///     MixerInfo.Mixer                public readonly  —— AnimationLayerMixerPlayable
    ///     MixerInfo.AvatarMask           public readonly
    ///     MixerInfo.ActiveTransformCount public readonly
    ///     MixerInfo.m_PlayableInfos      private List&lt;PlayableInfo&gt;
    ///       PlayableInfo.InputIndex      public readonly
    ///   权重：mi.Mixer.GetInputWeight(pi.InputIndex)
    ///
    /// ★自证不变式★ AvatarMask == null 必然配 ActiveTransformCount == 2147483647。
    ///   这一条如果对不上，说明我把字段读错了，整份数据作废 —— 先验它再看权重。
    ///
    /// ★开销★ 只在「诊断日志」开着、战斗中、每 2 秒、只对我们的卫兵采一次；
    ///   反射句柄全部一次性缓存。绝不进每帧路径。
    /// ★去重★ 按「单位 + 权重签名」去重，状态不变不重复打。
    /// </summary>
    internal static class MixerWeightProbe
    {
        private const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static float _next;
        private static readonly System.Collections.Generic.HashSet<string> _seen =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        // ★1.7.63 修：MixerInfo 上这几个是**字段**不是属性★
        //   1.7.62 我用 GetProperty 取，拿到 null 就把 _lookFailed 置位、整个探针静默退出，
        //   日志里一条都没有。这是本轮第 N 次「反射拿错成员种类 → 静默失效」。
        //   现在改用 GetField，并且**把失败原因打出来**——静默失败比失败本身更贵。
        private static FieldInfo _fMixers, _fPlayableInfos, _fMixer, _fAvatarMask, _fActiveCount, _fInputIndex;
        private static MethodInfo _mGetInputWeight;
        private static bool _weightIsInstance, _weightLookupFailed;
        /// <summary>本次采样成功读到了几个权重值。★0 表示"没测到"，不等于"权重是 0"★</summary>
        private static int _readOk;

        /// <summary>
        /// ★1.7.65 加的一维：正在播的**片段本身**★
        ///
        /// 上一版拿到了真权重，结果推翻了我自己的假说：回合结束后每次采样都有非零权重
        /// （[0.00 1.00 0.60] 这种），而画面仍是 bind pose。
        /// ⇒ 不是「没有东西驱动骨骼」，而是**有东西在驱动、但驱动出来的就是绑定姿势**。
        /// 只剩一种解释：正在播的片段本身是空的 / 没有骨骼曲线。
        ///
        /// PlayableInfo.m_Clip 就是那个 AnimationClip。读它的 name / length / empty
        /// 就能一眼看出是「Sicarian_LoMo_Idle 长 2.1 秒」还是「某个空片段长 0」。
        /// ★用反射★ AnimationClip 在 UnityEngine.AnimationModule 里，本工程没引用那个程序集，
        /// 为一个诊断字段去加引用不值得（多一个程序集依赖就多一处版本风险）。
        /// </summary>
        private static FieldInfo _fClip, _fWeight, _fWeightMul;
        private static PropertyInfo _pClipLen, _pClipEmpty;
        private static bool _looked, _lookFailed;

        internal static void Tick()
        {
            try
            {
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.DiagVerbose) return;
                if (_lookFailed) return;

                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now < _next) return;
                _next = now + 2f;

                bool inCombat = false;
                try
                {
                    var g = Kingmaker.Game.Instance;
                    inCombat = g != null && g.Player != null && g.Player.IsInCombat;
                }
                catch { }
                if (!inCombat) { _seen.Clear(); return; }

                var list = RetinueRegistry.All();
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var u = list[i];
                    if (u == null || !WeaponGate.IsGateTarget(u)) continue;
                    Sample(u);
                }
            }
            catch { }
        }

        private static void Sample(BaseUnitEntity u)
        {
            try
            {
                var view = u.View as Kingmaker.View.Mechanics.Entities.AbstractUnitEntityView;
                var mgr = view != null ? view.AnimationManager : null;
                if (mgr == null) return;

                if (!_looked)
                {
                    _looked = true;
                    var mt = mgr.GetType();
                    // m_Mixers 在基类 AnimationManager 上，逐层找
                    for (var t = mt; t != null && _fMixers == null; t = t.BaseType)
                        _fMixers = t.GetField("m_Mixers", Any);
                    if (_fMixers == null) { _lookFailed = true; return; }
                }
                if (_fMixers == null) return;

                var mixers = _fMixers.GetValue(mgr) as IList;
                if (mixers == null || mixers.Count == 0) return;

                var sb = new System.Text.StringBuilder();
                int layerIdx = 0;
                bool anyNonZero = false;
                _readOk = 0;

                foreach (var mi in mixers)
                {
                    if (mi == null) continue;
                    if (_fMixer == null)
                    {
                        var t = mi.GetType();
                        _fMixer         = t.GetField("Mixer", Any);
                        _fAvatarMask    = t.GetField("AvatarMask", Any);
                        _fActiveCount   = t.GetField("ActiveTransformCount", Any);
                        _fPlayableInfos = t.GetField("m_PlayableInfos", Any);
                        if (_fMixer == null || _fPlayableInfos == null)
                        {
                            _lookFailed = true;
                            Main.Log("[混合器权重] ★探针无法工作★ 在 " + t.FullName + " 上找不到："
                                   + (_fMixer == null ? " Mixer" : "")
                                   + (_fPlayableInfos == null ? " m_PlayableInfos" : "")
                                   + "　—— 成员名或种类变了，探针需要更新。★这一行的存在本身就是修复："
                                   + "上一版失败时一声不吭，害我以为是开关没开。★");
                            return;
                        }
                    }

                    object mask = null, cnt = null;
                    try { mask = _fAvatarMask != null ? _fAvatarMask.GetValue(mi) : null; } catch { }
                    try { cnt  = _fActiveCount != null ? _fActiveCount.GetValue(mi) : null; } catch { }

                    var infos = _fPlayableInfos.GetValue(mi) as IList;
                    var mixer = _fMixer.GetValue(mi);

                    sb.Append("\n      层").Append(layerIdx++)
                      .Append(" 遮罩=").Append(mask == null ? "null" : "有")
                      .Append(" 骨骼数=").Append(cnt == null ? "?" : cnt.ToString())
                      .Append(" 输入=").Append(infos == null ? 0 : infos.Count)
                      .Append(" 权重=[");

                    if (infos != null && mixer != null)
                    {
                        for (int k = 0; k < infos.Count; k++)
                        {
                            var pi = infos[k];
                            if (pi == null) continue;
                            if (_fInputIndex == null)
                            {
                                var pt = pi.GetType();
                                _fInputIndex = pt.GetField("InputIndex", Any);
                                if (_fInputIndex == null)
                                {
                                    var ppi = pt.GetProperty("InputIndex", Any);
                                    if (ppi == null)
                                        Main.Log("[混合器权重] ★PlayableInfo 上找不到 InputIndex★ 类型=" + pt.FullName
                                               + "　成员：" + string.Join(",", System.Array.ConvertAll(
                                                   pt.GetFields(Any), f => f.Name)));
                                }
                            }
                            if (_mGetInputWeight == null && !_weightLookupFailed)
                            {
                                // ★1.7.64 修：不要写死程序集名★
                                //   上一版用 Type.GetType("…, UnityEngine.AnimationModule") 找
                                //   PlayableExtensions，取不到就整列打成 "?"，而我的 anyNonZero
                                //   初值是 false —— 于是「读失败」被打印成「权重全为 0」。
                                //   差一点拿它当成结论。★没测到 ≠ 测到了是 0★
                                //   现在遍历全部已加载程序集找，并且失败时明说。
                                try
                                {
                                    // 先试实例方法（AnimationLayerMixerPlayable 可能自带）
                                    _mGetInputWeight = mixer.GetType().GetMethod("GetInputWeight",
                                        BindingFlags.Public | BindingFlags.Instance,
                                        null, new Type[] { typeof(int) }, null);
                                    _weightIsInstance = _mGetInputWeight != null;

                                    if (_mGetInputWeight == null)
                                    {
                                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                                        {
                                            var ext = asm.GetType("UnityEngine.Playables.PlayableExtensions", false);
                                            if (ext == null) continue;
                                            foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                                                if (m.Name == "GetInputWeight" && m.IsGenericMethodDefinition
                                                    && m.GetParameters().Length == 2)
                                                { _mGetInputWeight = m.MakeGenericMethod(mixer.GetType()); break; }
                                            if (_mGetInputWeight != null) break;
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    Main.Log("[混合器权重] ★取 GetInputWeight 失败★ " + e.GetType().Name + " " + e.Message);
                                }
                                if (_mGetInputWeight == null)
                                {
                                    _weightLookupFailed = true;
                                    Main.Log("[混合器权重] ★读不到权重★ 混合器类型=" + mixer.GetType().FullName
                                           + "　既没有实例方法 GetInputWeight(int)，也没找到 PlayableExtensions。"
                                           + "★在修好之前，本探针的「有非零权重」一栏无效，不要拿它下结论。★");
                                }
                            }
                            object w = null;
                            try
                            {
                                if (_mGetInputWeight != null && _fInputIndex != null)
                                {
                                    var idx = _fInputIndex.GetValue(pi);
                                    w = _weightIsInstance
                                        ? _mGetInputWeight.Invoke(mixer, new object[] { idx })
                                        : _mGetInputWeight.Invoke(null, new object[] { mixer, idx });
                                }
                            }
                            catch { }
                            if (k > 0) sb.Append(' ');
                            sb.Append(w == null ? "?" : Convert.ToSingle(w).ToString("F2"));

                            // ★片段身份★ 这才是「有东西在驱动却是 bind pose」的最后一块
                            try
                            {
                                if (_fClip == null) _fClip = pi.GetType().GetField("m_Clip", Any);
                                var clip = _fClip != null ? _fClip.GetValue(pi) : null;
                                if (clip == null) { sb.Append("(片段=null)"); }
                                else
                                {
                                    if (_pClipLen == null)
                                    {
                                        var ct = clip.GetType();
                                        _pClipLen   = ct.GetProperty("length", Any);
                                        _pClipEmpty = ct.GetProperty("empty", Any);
                                    }
                                    string nm = "?";
                                    try { nm = (clip as UnityEngine.Object) != null
                                             ? ((UnityEngine.Object)clip).name : "?"; } catch { }
                                    string len = "?", emp = "?";
                                    try { if (_pClipLen != null) len = Convert.ToSingle(_pClipLen.GetValue(clip, null)).ToString("F2"); } catch { }
                                    try { if (_pClipEmpty != null) emp = _pClipEmpty.GetValue(clip, null).ToString(); } catch { }
                                    sb.Append('(').Append(nm).Append(" 长=").Append(len)
                                      .Append(" 空=").Append(emp).Append(')');
                                }
                            }
                            catch { }
                            try
                            {
                                if (w != null)
                                {
                                    _readOk++;
                                    if (Convert.ToSingle(w) > 0.001f) anyNonZero = true;
                                }
                            }
                            catch { }
                        }
                    }
                    sb.Append(']');
                }

                string body = sb.ToString();
                string key = (u.CharacterName ?? "?") + "|" + body;
                if (!_seen.Add(key)) return;

                Main.Log("[混合器权重] " + (u.CharacterName ?? "?")
                       + (_readOk == 0
                          ? "　★权重一个都没读到 —— 本行不能作为判据★"
                          : "　★读到 " + _readOk + " 个权重，有非零=" + anyNonZero + "★")
                       + body
                       + "\n      ★怎么读★ 所有权重都是 0.00 ⇒ 混合器输出 AnimationStream 默认值 = "
                       + "**bind pose**，这就是大字的物理成因（片段在图里但没有任何一层在驱动骨骼）。"
                       + "\n      ★自证★ 遮罩=null 必然配 骨骼数=2147483647；对不上说明字段读错了，整份数据作废。");
            }
            catch { }
        }
    }
}
