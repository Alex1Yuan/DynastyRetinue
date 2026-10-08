using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem;                 // GetHealthOptional 的扩展方法所在
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.UnitLogic.Parts;          // PartHealth / GetHealthOptional
using Kingmaker.Visual.Animation;
using Kingmaker.Visual.Animation.Kingmaker;

namespace DynastyRetinue
{
    /// <summary>
    /// ================= 动画片段兜底（任务 #46）=================
    ///
    /// ★为什么以前一直用开关绕★
    ///   1.5.71 双持大剑姿势崩、1.5.72 电僧配剑没有挥砍、1.7.18 长剑闸开了 T-pose ——
    ///   三次都是「关掉那个技能」了事。作者提过好几次要**替换动作**，我一直没做。
    ///   原因是不知道该补哪里。1.7.22 工作流查到 IL 级，位置确定了，所以这次做真的。
    ///
    /// ═══ 病因（confirmed，IL 级）═══
    /// 技能 → 片段是四层查表：
    ///   ① UnitAnimationType 查单位的 AnimationSet.GetAction(type)
    ///   ② **WeaponAnimationStyle 精确相等**匹 WeaponStyleSettings（m_Settings.FirstOrDefault）
    ///   ③ Single/Burst 桶 + RecoilStrength/isCorner 分桶
    ///   ④ ClipWrapper.AnimationClip
    ///
    /// 第①层 miss 是**优雅**的：CreateHandle 记一条 Error 返回 null，
    /// StartAnimation 见 null 直接 ScheduleAct() —— 不卡，只是没动作。
    ///
    /// 第②层 miss 才是 T-pose：
    ///     if (variant?.ClipWrapper?.AnimationClip == null) { GetData(handle).Invalid = true; return; }
    /// 提前 return 绕过了末尾的 StartClip/Next，Animator 停在绑定姿势。
    /// 而**近战攻击这条路一个兜底都没有** —— 只有 CastSpell 那条有 AnimationStyleEntry.Default，
    /// HandAttack 连这个字段都不存在。
    ///
    /// ★这里也纠正我自己一个错判★ 我曾说「缺片段 → 动画事件不触发 → 协程永远等 → 卡住」。
    ///   **对 HandAttack 证伪**：HandAttack.OnUpdate 是
    ///     if (GetData(handle).Invalid) { UpdateInvalid(handle); return; } else UpdateInternal(handle);
    ///   而 UpdateInvalid 是纯时间驱动、**不碰 SpeedScale**：
    ///     handle.ActEventsCounter = Clamp(FloorToInt((t-0.45f)/num), 0, cnt);
    ///     if (t >= 0.8f + num*cnt) handle.Release();      // 约 1.1 秒自动 Release
    ///   所以**近战侧缺片段只会 T-pose，不会卡**。
    ///
    /// ★但 CastSpell 侧相反（1.7.34 补）★ CastSpell.OnUpdate 在路由到 UpdateInvalid
    ///   **之前**就先写 SpeedScale。BlockAttackAnimation 卡住时它被写成 0 ⇒ 时间冻结 ⇒
    ///   连「无效片段 1.1 秒自动超时」这道保险丝也一起被冻住。
    ///   ⇒ 施法路径上，缺片段**会**卡死。电僧卡的是 UnitUseAbility，多半就是这一类。
    ///   下面 H/P1/P2 治的就是它。
    /// </summary>
    internal static class AnimFallback
    {
        /// <summary>
        /// 从动画句柄拿到单位实体。拿不到返回 null。
        ///
        /// ★1.7.28 修★ 原来这里用 view.GetType().GetProperty("EntityData", ...) 反射，
        ///   结果**每一次调用都返回 null** —— 1.7.23~1.7.27 四条动画链路全部补了、
        ///   Harmony 全部挂载成功，却一次都没触发，日志里只有一行
        ///   「[动画闸] 拿不到单位」。四个补丁共用这个函数，一起哑火。
        ///
        /// ★为什么反射会失败★ handle.Unit 的运行时类型是派生类，而派生类多半用
        ///   `new` 重新声明了 EntityData（返回更具体的实体类型）。GetProperty 同时看到
        ///   基类和派生类两个同名属性 ⇒ 抛 AmbiguousMatchException ⇒ 被 catch 吞掉 ⇒ null。
        ///   **静默失败**，而且外面看起来完全像「补丁补错了位置」。
        ///
        /// ★教训★ 编译期够得着的成员就别反射。handle.Unit 的静态类型是
        ///   AbstractUnitEntityView，EntityData 是它的公开属性，直接点出来即可 ——
        ///   既没有歧义问题，编译器还会替我们检查名字。
        ///   （反射该留给编译期真的够不着的：protected 字段、嵌套私有类型那些。）
        /// </summary>
        internal static BaseUnitEntity UnitOf(UnitAnimationActionHandle handle)
        {
            try
            {
                var view = handle != null ? handle.Unit : null;
                return view != null ? view.EntityData as BaseUnitEntity : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 只对我们的近战精英生效 —— 原版单位一律走原版。
        ///
        /// ★为什么这里要打日志★ 1.7.23~1.7.25 连补了四条动画链路（HandAttack / LocoMotion /
        ///   BuffLoop / CastSpell），Harmony 全部挂载成功，**四条一次都没触发**。
        ///   四个补丁共用这一个闸，同时哑火最可能就是闸本身没过 ——
        ///   而闸不过的时候，后面所有诊断（包括 1.7.26 那个查表实况）都看不到任何东西。
        ///   所以把闸自己变成可见的：每种失败原因只记一次，直接指出卡在哪一步。
        /// ★去重★ 每个原因一行，全局最多四行。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _gateLogged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// ★1.7.47 性能修★ 原来所有调用点都**无条件拼好长字符串再传进来**，
        /// 而这里第一件事才是判断开关 —— 等于每个非目标单位、每次判定都白分配一个长串。
        /// 而 Applies 被挂在了每帧路径上（补丁 K 的属性钩子、时间轴），
        /// 实测 1.7.45 战斗中掉到 ~14 fps、每 10 秒 16~18 次 >100ms 尖峰，比 1.7.43 差一倍多。
        /// 现在改成传「原因键 + 取详情的委托」：开关关着时连字符串都不构造。
        /// ★教训★ 日志参数的构造成本是调用方付的，跟日志开不开无关 —— 热路径上要用延迟构造。
        /// </summary>
        private static void GateNote(string key, Func<string> detail = null)
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.DiagVerbose) return;      // ★先判开关，字符串都不碰★
                if (!_gateLogged.Add(key)) return;
                Main.Log("[动画闸] 首次未通过：" + (detail != null ? detail() : key));
            }
            catch { }
        }

        /// <summary>
        /// ★全局存在性闸★ 场上有没有我们那两个近战精英。
        ///
        /// ═══ 为什么需要它 ═══
        /// 作者问：「这些检查是否只有有这两个精英的时候才会触发？」——**原来不是**。
        /// 这一批动画补丁挂的全是引擎的公共方法（Execute / UpdateInternal /
        /// SelectClip / GetAnimation / IsAvailable / get_DontReleaseOnInterrupt），
        /// 对**全场每个单位**都会进补丁体，进去之后才判断「是不是我们的卫兵」。
        /// 而那个判断要走 handle.Unit（首次访问会做一次 GetComponentInParent 层级遍历）
        /// 再比对蓝图 guid —— 对一场几十个单位的战斗，这是白付的成本。
        ///
        /// 现在改成：**先读一个静态 bool**。没有我们的精英在场时（绝大多数时候，
        /// 包括所有不带这两个精英的战斗、以及全部非战斗流程），补丁第一行就 return，
        /// 开销 = 一次静态字段读取。
        ///
        /// ★刷新方式★ 每 2 秒一次，走 RetinueRegistry.All()（本身带 10 秒缓存）。
        ///   ★绝不能每帧刷★ —— All() 会拷贝全区域实体，那是 perf-hot-paths 那条红线。
        /// ★宁可误报 true★ 拿不准时保持上一次的值：漏判会让功能静默失效（难查），
        ///   多判只是多跑几帧补丁体（无害）。
        /// </summary>
        /// <summary>
        /// ★1.7.93：事件驱动的 O(1) 存在性闸★
        ///
        /// 前两版先用 realtimeSinceStartup、再用 network tick 轮询名册，都不够好：
        ///   · 墙钟不是同步量；
        ///   · network tick 虽同步，但「每 10 个渲染帧才采样」仍让两端在不同 tick 建基准；
        ///   · RetinueRegistry.All() 根本没有缓存，每 2 秒会 ToList/HashSet 全场实体并分配。
        ///
        /// 现在只在四类明确事件更新：生成、读档/过图、单个摘牌、遣散全部。
        /// 热路径只读两个 bool，不碰 Game.Instance、不碰网络状态、不扫描实体。
        /// </summary>
        internal static bool AnyMeleeEliteActive { get { return _activeMeleeEliteIds.Count != 0; } }
        internal static bool RosterHasMeleeElite  { get { return _rosterMeleeEliteIds.Count != 0; } }

        /// <summary>
        /// 热路径身份查询：按 UID 查事件驱动集合，不再每次解析 EliteTag。
        /// GetEliteTag 内部会 Substring + Split 分配字符串和数组，只允许在生成/加载事件调用。
        /// </summary>
        internal static bool IsKnownMeleeElite(BaseUnitEntity u)
        {
            string id = StableId(u);
            return !string.IsNullOrEmpty(id) && _rosterMeleeEliteIds.Contains(id);
        }

        private static readonly System.Collections.Generic.HashSet<string> _rosterMeleeEliteIds =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.HashSet<string> _activeMeleeEliteIds =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        private static string StableId(BaseUnitEntity u)
        {
            try { return u != null ? u.UniqueId : null; }
            catch { return null; }
        }

        /// <summary>
        /// 生成入口：身份标记写好后立即登记。这里故意不要求 IsInGame —— SpawnUnit
        /// 还在 m_ToSpawn，下一模拟 tick 才入册；等它入册会错过 Faction.Set/ApplyRuntimeState
        /// 期间同步触发的动画回调。生成失败的回滚会配对调用 ForgetMeleeElite。
        /// </summary>
        internal static void ObserveMeleeElite(BaseUnitEntity u)
        {
            try
            {
                if (u == null || !WeaponGate.IsMeleeEliteForRegistration(u)) return;
                string id = StableId(u);
                if (string.IsNullOrEmpty(id)) return;
                _rosterMeleeEliteIds.Add(id);
                _activeMeleeEliteIds.Add(id);
            }
            catch { }
        }

        /// <summary>
        /// 读档/过图入口：复用 RetinueLifecycle 已经拿到的名册快照，一次重建两个集合。
        /// activeArea=false（星图/太空战/全局地图）时保留在册身份，但清空动画热路径闸。
        /// 必须在 ApplyRuntimeState 之前调用：后者设置 IsInGame 时会同步触发 View/EventBus。
        /// </summary>
        internal static void RebuildMeleeEliteRoster(
            System.Collections.Generic.IEnumerable<BaseUnitEntity> units, bool activeArea)
        {
            _rosterMeleeEliteIds.Clear();
            _activeMeleeEliteIds.Clear();
            if (units == null) return;
            try
            {
                foreach (var u in units)
                {
                    if (u == null || !WeaponGate.IsMeleeEliteForRegistration(u)) continue;
                    string id = StableId(u);
                    if (string.IsNullOrEmpty(id)) continue;
                    _rosterMeleeEliteIds.Add(id);
                    if (activeArea && !GuardReserve.IsReserved(u)) _activeMeleeEliteIds.Add(id);
                }
            }
            catch { }
        }

        internal static void ForgetMeleeElite(BaseUnitEntity u)
        {
            string id = StableId(u);
            if (string.IsNullOrEmpty(id)) return;
            _rosterMeleeEliteIds.Remove(id);
            _activeMeleeEliteIds.Remove(id);
        }

        internal static void ClearActiveMeleeElites() { _activeMeleeEliteIds.Clear(); }
        internal static void ClearMeleeEliteRoster()
        {
            _rosterMeleeEliteIds.Clear();
            _activeMeleeEliteIds.Clear();
        }

        internal static bool Applies(UnitAnimationActionHandle handle)
        {
            if (!AnyMeleeEliteActive) return false;       // ★最便宜的一道：一次静态 bool★
            if (!Main.Enabled) { GateNote("Main.Enabled=false"); return false; }
            var s = Main.Settings;
            if (s == null) { GateNote("Settings 为 null"); return false; }
            

            var u = UnitOf(handle);
            if (u == null) { GateNote("拿不到单位（handle.Unit 或 EntityData 为空）"); return false; }
            if (!WeaponGate.IsGateTarget(u))
            {
                // 这条会被大量非目标单位触发，所以只记一次并带上是谁，用来确认「补丁确实在跑」
                GateNote("not-in-list", () =>
                    "单位不在名单里（首个样本：" + (u.CharacterName ?? "?") + "）—— "
                  + "这行出现说明补丁本身在跑，只是这个单位不该管；"
                  + "若**只有**这一行而没有别的，说明我们的精英根本没走到这条动画链路。");
                return false;
            }
            return true;
        }

        // 诊断计数，由战报读走并清零
        internal static int StyleSwapped, LocoGuarded;
        private static bool _loggedSwap, _loggedLoco;

        internal static void NoteSwap(object from, object to)
        {
            StyleSwapped++;
            if (_loggedSwap) return;
            _loggedSwap = true;
            Main.Log("[动画兜底] 首次替换武器风格：" + from + " -> " + to
                   + "　（该动作里没有前者的片段，改用它真有的那个，避免 T-pose）");
        }

        /// <summary>
        /// ★查表实况★ 每种「动作 + 要的风格」只打一次：要的是什么、表里有什么。
        ///
        /// ★为什么必须有★ 作者反馈「锈行者很多技能释放时都是 T-pose」，
        ///   而我手上没有任何数据能回答「它要的风格是什么、表里有没有」——
        ///   战报里那行 ActiveMainHandWeaponStyle 是**战斗结束后**读的，出战斗就被清空，
        ///   打出来恒为 None(0)，没有诊断价值（我放错位置了）。
        ///   替换日志又只在**真的替换了**时才打，匹配成功或表为空时什么都看不到。
        ///   这一行补上那个缺口：无论替换与否都记录一次，直接对上「要什么 vs 有什么」。
        /// ★去重★ 按 动作类+要的风格 做键，一场最多几行，不会刷屏。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _seenLookup =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// ★热路径护栏（1.7.59）★ 调用方在**构造诊断数据之前**先问一句。
        ///
        /// 起因：查表那两个补丁每次调用都 `new List&lt;string&gt;()` 收集"表里有哪些风格"，
        /// 外加一次反射取当前技能名 + 字符串拼接 —— 然后 NoteLookup 进去第一件事就是
        /// 判断开关，关着就全丢掉。BuffLoop 那条一场要调 400~900 次。
        /// 这是本轮第三次发现同一类错误（前两次：GateNote 的参数拼串、
        /// IsAvailable 里的蓝图重试）。★统一的教训：诊断数据的构造成本是调用方付的，
        /// 和日志开不开无关；热路径上必须先问开关再构造。★
        /// </summary>
        internal static bool WantLookupLog()
        {
            var s = Main.Settings;
            return s != null && s.DiagVerbose;
        }

        internal static void NoteLookup(string action, object want, System.Collections.Generic.List<string> have)
        {
            try
            {
                if (Main.Settings == null || !Main.Settings.DiagVerbose) return;
                string key = action + "|" + want;
                if (!_seenLookup.Add(key)) return;
                Main.Log("[动画查表] " + action + "　要=" + want
                       + "　表里有=[" + (have == null || have.Count == 0 ? "空" : string.Join(", ", have.ToArray())) + "]"
                       + (have != null && have.Contains(want.ToString()) ? "　→ 命中" : "　→ ★没命中，会 T-pose★"));
            }
            catch { }
        }

        /// <summary>跳跃动画替换 —— 死从天降那条。</summary>
        internal static int JumpFixed;
        private static bool _loggedJump;
        internal static void NoteJump(string state)
        {
            JumpFixed++;
            if (_loggedJump) return;
            _loggedJump = true;
            Main.Log("[动画兜底] 首次补上跳跃片段（阶段=" + state + "）—— "
                   + "UnitAnimationActionJump.GetAnimation 的兜底分支没有 null 保护，"
                   + "原版在这里返回 null，跳跃三阶段任一段拿不到片段就是 T-pose。");
        }

        /// <summary>
        /// 这个片段包装器里到底有没有真的 AnimationClip。
        /// ★为什么反射★ AnimationClip 定义在 UnityEngine.AnimationModule 里，
        ///   而本工程没有引用那个程序集；为了一个判空去加引用不值得，
        ///   而且多一个程序集依赖就多一处版本风险。属性名稳定，反射足够。
        /// </summary>
        private static PropertyInfo _clipP;
        private static bool _clipLooked;
        internal static bool HasClip(object wrapper)
        {
            if (wrapper == null) return false;
            try
            {
                if (!_clipLooked)
                {
                    _clipLooked = true;
                    _clipP = wrapper.GetType().GetProperty("AnimationClip",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_clipP == null) return true;      // 查不到就当有，交回原版判断
                var c = _clipP.GetValue(wrapper, null);
                return !ReferenceEquals(c, null) && !c.Equals(null);   // Unity 的 == null 重载
            }
            catch { return true; }
        }

        /// <summary>动画死锁解除计数。★这条即使关了诊断也要打★ 它代表一次「本会卡到超时」被救回来。</summary>
        internal static int DeadlockBroken;
        private static bool _loggedDeadlock;
        internal static void NoteDeadlock()
        {
            DeadlockBroken++;
            if (_loggedDeadlock) return;
            _loggedDeadlock = true;
            Main.Log("[动画死锁] 首次解除 —— 有个动画句柄「已启动、没释放、但没有在播的动画」，"
                   + "引擎的 Update 在这种状态下第一行就 return，永远走不到 Release() ⇒ "
                   + "指令永远不结束 ⇒ 卡到超时。补一次 Release 让它正常收尾。");
        }

        internal static void NoteLoco()
        {
            LocoGuarded++;
            if (_loggedLoco) return;
            _loggedLoco = true;
            Main.Log("[动画兜底] 首次拦下 LocoMotion 的空片段 —— "
                   + "原版在这里会 NRE，然后移动动画句柄**永久损坏**（只有 ResetLocoMotion 会清），"
                   + "表现就是单位再也走不了路、每回合只能做一两件事。");
        }

        /// <summary>攻击动画屏蔽解除计数。★这条即使关了诊断也要打★ 一次 = 一次「本会卡到超时」。</summary>
        internal static int AttackUnblocked, AttackUnblockedParent;
        private static bool _loggedUnblock;
        internal static void NoteUnblock(float heldSec, bool own, bool parent)
        {
            if (own) AttackUnblocked++;
            if (parent) AttackUnblockedParent++;
            if (_loggedUnblock) return;
            _loggedUnblock = true;
            Main.Log("[攻击屏蔽] 首次解除（" + (own ? "自己的标志" : "") + (own && parent ? " + " : "")
                   + (parent ? "父视图的标志" : "") + "）—— 已卡 " + heldSec.ToString("F1") + " 秒。"
                   + "这个标志只有掩体动作会写：UpdateAnimations 置位、OnFinish 清位；"
                   + "掩体动作中途抛异常就走不到 OnFinish，标志永久留 true ⇒ "
                   + "SpeedScale 被写成 0 ⇒ 动画时间冻结 ⇒ 句柄永不 Release ⇒ 指令卡到超时。");
        }

        /// <summary>掩体动作抛异常的次数 —— 这是根因 A 的**直接证据**，不是推断。</summary>
        internal static int CoverThrew;
        private static bool _loggedCover;
        internal static void NoteCoverThrow(string ex)
        {
            CoverThrew++;
            if (_loggedCover) return;
            _loggedCover = true;
            Main.Log("[掩体异常] 首次捕获 —— UnitAnimationActionCover.UpdateAnimations 抛了 " + ex
                   + "。它自己没有 try/catch，异常被上层吞掉后直接 set_IsFinished(true)、"
                   + "**不走 FinishInternal**，而 OnFinish 只有 FinishInternal 会调 ⇒ "
                   + "BlockAttackAnimation 永久留在 true。我们在这里替它清位（不改变异常传播）。");
        }

        /// <summary>把算成 None 的武器动画风格按逻辑槽位重算回来 —— 根因 B。</summary>
        internal static int StyleRepaired;
        private static bool _loggedRepair;
        internal static void NoteRepair(object main, object off)
        {
            StyleRepaired++;
            if (_loggedRepair) return;
            _loggedRepair = true;
            Main.Log("[风格重算] 首次修正：原版算出 None(0)，按逻辑槽位重算为 主手=" + main + " 副手=" + off
                   + "　—— 原版取的是**视图当前显示**的武器（VisibleItem），收刀/换组/视图没刷新时它是 null，"
                   + "于是主副手被**一起**写成 None；而多数动作表里没有 None 条目 ⇒ 满场 T-pose。"
                   + "在源头修一次，攻击/施法/BuffLoop/跳跃/移动全都跟着好。");
        }

        /// <summary>
        /// 每个补丁**各自**的入口计数。★不能共用★
        /// ★为什么必须有★ 1.7.23~1.7.33 一直把「计数为 0」当成「补丁没被调用」，
        ///   据此反复去改挂点 —— 而工作流用 IL 证明挂点从头到尾都是对的
        ///   （GetAttackVariant 只有一个重载、唯一调用方 OnStart，Harmony 没有选错的空间）。
        ///   真正分不开的是「没进来 / 进来了没过闸 / 过了闸没查表 / 查了表没换」这四种。
        ///   这四个计数一次把它们拆开，省掉下一轮瞎猜。
        /// ★Jump 特别注意★ 它是 Postfix，第一句 HasClip(__result) 早退在 Applies 之前，
        ///   原版正常拿到片段就静默退出 —— 所以 JumpFixed==0 是**一切正常时的预期输出**，
        ///   它连弱证据都算不上。Entered 必须加在 HasClip 那句**之前**才有意义。
        /// </summary>
        internal sealed class Tally
        {
            internal int Entered, GatePassed, Looked, Swapped;
            internal readonly string Name;
            internal Tally(string n) { Name = n; }
            internal string Line()
            {
                return "      " + Name.PadRight(12) + " 进入 " + Entered + " · 过闸 " + GatePassed
                     + " · 查表 " + Looked;
            }
            internal void Reset() { Entered = GatePassed = Looked = Swapped = 0; }
        }

        internal static readonly Tally TA = new Tally("HandAttack");
        internal static readonly Tally TC = new Tally("BuffLoop");
        internal static readonly Tally TE = new Tally("CastSpell");
        internal static readonly Tally TF = new Tally("Jump");
        internal static readonly Tally TL = new Tally("LocoMotion");
        /// <summary>真正的施法动作类（补丁 J）。补丁 E 挂的 Warhammer 那个是死代码，实测三场全 0。</summary>
        internal static readonly Tally TJ = new Tally("CastSpell真");

        /// <summary>
        /// 排队饿死解除计数 —— ★这是电僧「攻击放不出来 + 卡到超时」的根因修复★
        /// 一次 = 一次「本会卡到回合超时」被救回。
        /// </summary>
        internal static int QueueUnblocked, QueueFused;
        private static bool _loggedQueue, _loggedFuse;

        internal static void NoteQueueUnblock()
        {
            QueueUnblocked++;
            if (_loggedQueue) return;
            _loggedQueue = true;
            Main.Log("[排队饿死] 首次解除 —— 电僧的攻击动画资源是 ExecutionMode=Sequenced(排队型)，"
                   + "而 AnimationManager.m_CurrentAction 被森罗刃网的循环动画占着（未 Release、"
                   + "DontReleaseOnInterrupt=false）⇒ Execute 走排队分支 Enqueue，"
                   + "**既不入 m_ActiveActions 也不 StartInternal**；而 UpdateActions 只给"
                   + "m_ActiveActions 里的句柄补 Start，排空条件又被同一个 m_CurrentAction 卡死 ⇒ "
                   + "句柄永远 IsStarted=False，指令等到回合超时。"
                   + "我们提前 Release 掉那个循环句柄，下一帧 UpdateActions 就能把它出队并启动。");
        }

        internal static void NoteQueueFuse(float heldSec)
        {
            QueueFused++;
            if (_loggedFuse) return;
            _loggedFuse = true;
            Main.Log("[排队饿死·保险丝] 首次触发 —— 循环动画占着通道且队列非空已 "
                   + heldSec.ToString("F2") + " 秒。主修（F1′）没拦住这一次，"
                   + "说明占位者不是在 Execute 那一刻就位的，是后来才接管的。");
        }

        /// <summary>
        /// ★只对**我们授予的**技能做动作替换 —— 原版自带的技能一概不碰★
        ///
        /// ═══ 为什么必须有这道闸（1.7.37 实机教训）═══
        /// 1.7.37 的补丁 J 没有技能范围，只要「请求的施法风格在表里没有」就替换。
        /// 实测后果：
        ///     [动画查表] CastSpell　要=Fly　表里有=[Grenade]　→ 没命中
        ///     [施法动作兜底] 替换施法风格：Fly -> Grenade        （本场 35 次）
        /// 而 `Fly` 正是**带位移**的那种施法风格，`Grenade` 是站着扔手雷。
        /// 于是锈行猎手自带的「利刃切割」（DLC3_OP_Dogmatic_SicarianRuststalker_Slice_Ability）
        /// 位移整个没了 —— 作者原话：「本来有位移的，现在好像完全没有了，有点像放不出来一样」。
        ///
        /// ★关键认知★ 原版对这些技能返回「没有匹配条目」并不是 bug：
        ///   OnStart 会 set_IsSkipped(true) 优雅跳过，**技能的位移/效果照常由技能逻辑跑**，
        ///   只是没有额外的施法动作。我们插进去反而把它的节奏打乱了。
        ///   ⇒ 缺动作的是**我们后加的**收割者技能，该补的也只有它们。
        ///
        /// ═══ 判据 ═══
        /// 我们授予的整条收割者线蓝图名都以 `Reaper` 开头（ReaperBladeDanceAbility /
        /// ReaperBladeShroud_Ability / ReaperBloodOath_Ability / ReaperSpringAttackAbility …），
        /// 而两个精英自带的是 `DLC3_OP_*`。前缀比对足够，也不用维护一份会过期的 guid 名单。
        /// </summary>
        private static PropertyInfo _cmdsP, _curP;
        private static bool _cmdLooked;

        /// <summary>取这个单位当前正在执行的「放技能」指令。不是放技能就返回 null。</summary>
        internal static Kingmaker.UnitLogic.Commands.UnitUseAbility CurrentUse(BaseUnitEntity u)
        {
            try
            {
                if (u == null) return null;
                if (!_cmdLooked)
                {
                    _cmdLooked = true;
                    _cmdsP = u.GetType().GetProperty("Commands",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_cmdsP == null) return null;
                var cmds = _cmdsP.GetValue(u, null);
                if (cmds == null) return null;
                if (_curP == null)
                    _curP = cmds.GetType().GetProperty("Current",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_curP == null) return null;
                return _curP.GetValue(cmds, null) as Kingmaker.UnitLogic.Commands.UnitUseAbility;
            }
            catch { return null; }
        }

        // ═══════════ 1.7.82：手势限流 —— 每回合每技能只补一次 ═══════════
        //
        // ★为什么要限流（实测数据，不是估计）★
        //   AI 回合有一道 40 秒硬墙（AiBrainController，实测 40.055 / 40.050 秒），
        //   到点 InterruptAll(cmd => true) 把没跑完的指令全砍掉。
        //   而实测回合 1 的 40 秒里，**21.3 秒是我们自己的施法动画**：
        //       ReaperBloodOath ×7          8750 ms   ← 单这一条就 8.75 秒
        //       ReaperDeathWaltz ×2         2469 ms
        //       SpringAttackMovement ×1     1566 ms
        //       杂技表演 ×1                 1561 ms
        //       Inspire/Linchpin/Strongpoint 各 ~1540 ms
        //       BladeShroud ×1              1201 ms
        //       DeathWaltzSpring ×1         1099 ms
        //   同一张表里的对照极干净 —— 没被我们补手势的技能只花 4~6 ms：
        //       MobTechpriestMagi_VoxSkullSummon    6 ms
        //       DLC3_..._Slice（单位自带）           5 ms
        //   ⇒ 「原版不撞墙」不只因为它在玩家回合，更因为**原版根本不花这 21 秒**。
        //
        // ★后果★ 杂技表演（ReaperSpringAttackAbility）的 Deliver 是个按墙钟推进的协程，
        //   要为本回合每一条死从天降 Entry 还债（走回落点→逆跳回出发点→走回起始格）。
        //   回合 1 施放时只剩 5.07 秒、需要 ≥10 秒 ⇒ 被砍在半路；
        //   回合 2 只剩一条 Entry 且免归位，3.39 秒跑完 ⇒ 正常。
        //   **这就是作者看到的「一回合坏、二回合好」。**
        //
        // ★为什么限流而不是砍回溯★ 作者明确要求保留完整还原。
        //   砍掉重复手势能省约 7.5 秒，而观感损失极小 ——
        //   鲜血誓言连放 7 次，第 2~7 次的手势是同一个动作重复七遍，没有增量信息。
        //
        // ★为什么用 (CurrentUnit, CombatRound) 当回合令牌★
        //   两者任一变化就说明换回合/换单位了。比自己数回合可靠，也不需要新挂点。
        private static object _turnUnit;
        private static int _turnRound = -1;
        private static readonly System.Collections.Generic.HashSet<string> _gestured =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static int GestureSkipped;

        /// <summary>这一回合这个技能是不是已经补过手势了。补过就返回 true（本次跳过）。</summary>
        private static bool AlreadyGesturedThisTurn(string bpName)
        {
            try
            {
                if (string.IsNullOrEmpty(bpName)) return false;
                var tc = Kingmaker.Game.Instance != null
                       ? Kingmaker.Game.Instance.TurnController : null;
                object cu = tc != null ? (object)tc.CurrentUnit : null;
                int round = tc != null ? tc.CombatRound : -1;
                if (!ReferenceEquals(cu, _turnUnit) || round != _turnRound)
                {
                    _turnUnit = cu; _turnRound = round; _gestured.Clear();
                }
                return !_gestured.Add(bpName);       // Add 返回 false = 已存在 = 补过了
            }
            catch { return false; }                  // 判不出来就放行，宁可多花时间也别丢动作
        }

        /// <summary>当前正在放的技能蓝图名。取不到返回 null。</summary>
        internal static string CurrentAbilityName(BaseUnitEntity u)
        {
            try
            {
                var use = CurrentUse(u);
                var ab = use != null ? use.Ability : null;
                var bp = ab != null ? ab.Blueprint : null;
                return bp != null ? bp.name : null;
            }
            catch { return null; }
        }

        /// <summary>补手势之前的最后一道闸：同回合同技能的重复施放不再补。</summary>
        internal static bool GestureBudgetAllows(BaseUnitEntity u)
        {
            string n = CurrentAbilityName(u);
            if (n == null) return true;
            if (!AlreadyGesturedThisTurn(n)) return true;
            GestureSkipped++;
            return false;
        }

        internal static bool IsOurGrantedAbility(BaseUnitEntity u)
        {
            try
            {
                var use = CurrentUse(u);
                if (use == null) return false;                      // 不是在放技能 —— 不碰
                var ab = use.Ability;
                var bp = ab != null ? ab.Blueprint : null;
                if (bp == null || bp.name == null) return false;
                // ★1.7.44 扩到战术专家线★ 它同样是**我们授予的**（加点方案「收割者战术家」），
                //   不是单位自带的。1.7.38 我只放行 Reaper*，把它挡在外面了 ——
                //   实测时间轴证明这就是杂技表演/扩大优势/枪炮奏鸣大字的原因：
                //     TacticianInspire     CastSpell 播放时长 21 ms
                //     TacticianLinchpin    CastSpell 播放时长  8 ms
                //     TacticianStrongpoint CastSpell 播放时长  7 ms
                //   —— 全是 set_IsSkipped(true) → Release 那条路，播了等于没播。
                //   对照组：自带攻击 1679 ms、森罗刃网 1490 ms、死从天降 1459 ms 都正常。
                //   两个精英自带的技能一律是 DLC3_OP_*，不会命中这两个前缀。
                //   ★1.7.48 补 DeathCultAssassin★ 杂技表演就是它
                //   （DeathCultAssassin_InnateAbility），同样由收割者线带来，不是单位自带。
                //   实测时间轴：锈行猎手身上播 4~10 ms（表里只有 Grenade，没命中 ⇒ 大字），
                //   电僧身上播 1585 ms（它的表里有 Directional，命中）—— 长短交替正是两个单位表不同。
                // ═══ 1.7.78：排除「自己会派生一次攻击」的技能 ═══
                //
                // ★作者报的现象★「锈行者的完成任务接打击那里好像没有攻击动画」。
                // ★工作流查实（IL + 引擎自己的 GameLogFull 时间戳）★
                //   TacticianFinishTheJobAbility 的效果是 ContextActionAttackWithFirstWeaponAbility：
                //     RunAction IL_00AA newobj UnitUseAbilityParams
                //               IL_00B1 set_IgnoreCooldown(true)
                //               IL_00B8 set_FreeAction(true)
                //               IL_00E2 Commands.AddToQueue      ← 把武器自己的打击排进指令队列
                //   也就是说它**自己会派生一次攻击**，而那次攻击要用同一个动画管理器。
                //
                //   而我们在这里给它换上的手势是「扔手雷」（表里唯一有 CastClip 的条目），
                //   实测占住动画管理器 1315 ms；原版这里是 set_IsSkipped，4~21 ms 就过。
                //   时序对得上：打击的句柄 912 ms 才启动，912+429 = 1341 ≈ 1315。
                //   ⇒ **攻击动画在排队等我造出来的手势播完**，看着就像"没有攻击动画"。
                //
                // ★为什么只排它一个，不整条线关掉★ 其余 Tactician 技能（鼓舞人心/关键战术/
                //   战术枢纽/扩大优势…）不派生攻击，手势是净收益 —— 没有它们就是 7~21 ms 的大字，
                //   那正是 1.7.44 加这条闸要治的东西。只有「自己带攻击」的会跟自己抢管理器。
                //
                // ★教训★ 「给没动作的技能补一个动作」这个策略，对**会派生其它动作**的技能不成立。
                //   补的动作不是填空白，是在跟后续动作抢一条独占资源。
                if (IsSelfChainingAttack(bp.name)) return false;

                return bp.name.StartsWith("Reaper", StringComparison.Ordinal)
                    || bp.name.StartsWith("Tactician", StringComparison.Ordinal)
                    || bp.name.StartsWith("DeathCultAssassin", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// 这个技能会不会**自己派生一次攻击指令**。这类技能不能补施法手势 ——
        /// 手势会占住动画管理器，把它自己派生的那次攻击挤到后面去。
        /// ★判据是蓝图行为，不是名字风格★ 目前已确证的只有一个；
        ///   以后若发现别的带 ContextActionAttackWithFirstWeaponAbility 的技能，加进来即可。
        /// </summary>
        private static bool IsSelfChainingAttack(string bpName)
        {
            // TacticianFinishTheJobAbility：ContextActionAttackWithFirstWeaponAbility
            //   会把武器自己的打击排进指令队列 ⇒ 手势会跟它派生的攻击抢动画管理器。
            if (bpName == "TacticianFinishTheJobAbility") return true;

            // ═══ 1.7.83：ReaperSpringAttackMovementAbility 也不能补手势 ═══
            //
            // ★这就是作者一路在报的「平移」★（工作流反编译级确证）
            //   杂技表演 = ReaperSpringAttackAbility，它的 Deliver 末段无条件排一条
            //   「免费走回本回合起点」的移动（AbilityCustomMoveToTarget，FreeAction、不花 MP、
            //   无格数上限）—— 走多远取决于这回合往前走了多远。**距离是设计如此，不该改。**
            //   但那条腿会请求一次 CastSpell，风格 = None：
            //     原版：SingleOrDefault 落空 → IsSkipped=true; Release(); RestoreLoopAnimation()
            //           ⇒ 不播手势，locomotion 层照常跑 ⇒ **正常走路**
            //     我们：把 None 改写成 Grenade/Directional ⇒ 播 1.5 秒原地施法手势、权重 1.00，
            //           而 UnitMoveToProper 同时拉着单位走 ⇒ **一边滑一边站着做手势**
            //   实测手势时长 1566/1559/1524/1579/1523/1573 ms；
            //   混合器同时采到 ElectroPriest_Spell_Direct 权重 1.00、Idle 0.00。
            //
            // ★为什么排除是安全的（这条推翻了我自己写在 JumpAndActFix.cs 里的说法）★
            //   我曾写「撤掉 CastStyleFallback ⇒ 技能整个不起播 ⇒ 1.7.67 那类卡死」。
            //   反编译不支持：IsSkipped=true + Release() 是**已完成态**，
            //   AbstractUnitCommand.Tick 的两条 Error 被 IsSkipped 抑制、flag3 立刻置真、
            //   **同一个 Tick 就走 OnAction**。代价只是观感（没手势），不是死锁。
            //   ⇒ 对「本来就该让 locomotion 播」的技能，排除不但安全，而且是唯一正确做法。
            //
            // ★教训★ 「给没动作的技能补一个动作」这条策略，对**本身就在移动**的技能是有害的：
            //   补的不是空白，是在跟移动动画抢同一条独占通道。
            //   判据不该是「它有没有施法动作」，而是「补上去会不会挤掉别的东西」。
            if (bpName == "ReaperSpringAttackMovementAbility") return true;

            return false;
        }

        /// <summary>
        /// ★探针★ 我们授予的技能请求了哪种动画类型、拿到没有。每个（技能, 类型）只记一次。
        /// 死从天降仍然 T-pose 但引擎不报 Has no animation —— 说明第①层是通的，
        /// 这行就是去定位它究竟走哪条路的。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> _seenCreate =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static void NoteCreate(BaseUnitEntity u, object type, bool got)
        {
            try
            {
                string ab = "?";
                var use = CurrentUse(u);
                if (use != null && use.Ability != null && use.Ability.Blueprint != null)
                    ab = use.Ability.Blueprint.name;
                string key = ab + "|" + type;
                if (!_seenCreate.Add(key)) return;
                Main.Log("[动画路由] " + (u.CharacterName ?? "?") + "　技能=" + ab
                       + "　请求动画类型=" + type
                       + (got ? "　→ 拿到了" : "　→ ★没拿到（CreateHandle 返回 null）★"));
            }
            catch { }
        }

        /// <summary>
        /// ★补丁 K 的作用域标志★ 只在 AnimationManager.Execute 的那一次调用内为 true。
        ///
        /// ★为什么必须限定作用域（1.7.40 实机教训）★
        /// 1.7.40 让 get_DontReleaseOnInterrupt **无条件**对 buff 循环返回 true，后果：
        ///   · Execute 的**打断分支**（IL_01F4 `if (CurrentAction != null && !DontReleaseOnInterrupt)
        ///     → MarkInterrupted + Release`）也读同一个属性 ⇒ 循环**永远不被释放**、
        ///     永久占着 m_CurrentAction。
        ///   · 实测数字直接暴露：循环保护 12568 次、替换武器风格从 373 暴涨到 3688（十倍），
        ///     正是那个循环被反复重算的表现。
        /// ⇒ 和「改了风格不还原」是同一类错误：把只该在**一次调用内**成立的谎，说成了永久成立。
        /// 现在只在 Execute 的 Prefix 里打开、Postfix 里关掉，别的读取点一律看到原值。
        /// </summary>
        internal static bool ProtectScope;

        /// <summary>循环动画被标记为「受保护」的次数（补丁 K）。</summary>
        internal static int LoopProtected;
        private static bool _loggedProtect;
        internal static void NoteLoopProtect()
        {
            LoopProtected++;
            if (_loggedProtect) return;
            _loggedProtect = true;
            Main.Log("[循环保护] 首次生效 —— 把 buff 循环动画句柄答成「受保护」，"
                   + "排队的攻击动作就走 Execute 的「直接启动」分支，"
                   + "而循环动画**原封不动继续播**。这替代了 1.7.36~1.7.39 那个 Release 做法 —— "
                   + "那个能放行队列，但杀掉循环之后没有任何东西恢复它，"
                   + "单位从回合结束起就一直站在绑定姿势里。");
        }

        /// <summary>给带位移的施法换上自己的 ForceMove 片段的次数。</summary>
        internal static int ForceMoveClipUsed;
        private static bool _loggedFmc;
        internal static void NoteForceMoveClip()
        {
            ForceMoveClipUsed++;
            if (_loggedFmc) return;
            _loggedFmc = true;
            Main.Log("[位移片段] 首次换上 —— 带位移的施法（Fly）原本被换成静止的施法手势，"
                   + "人在滑行动作却是原地施法。两个单位其实都有 ForceMove 片段，"
                   + "只是挂在 ForceMove(26) 动作下、不在施法表里，永远轮不到。这里直接取来用。");
        }

        /// <summary>拦下异骨架片段的次数 —— 回合末 bind pose 的根因修复。</summary>
        internal static int ForeignClipBlocked;
        private static bool _loggedForeign;
        internal static void NoteForeignClip(string clipName)
        {
            ForeignClipBlocked++;
            if (_loggedForeign) return;
            _loggedForeign = true;
            Main.Log("[异骨架片段] 首次拦下：" + clipName
                   + "　—— 这是收割者线（绮贝菈）的片段，为**人类骨架**制作，"
                   + "而我们这两个精英是 DLC3 机械教骨架。在错骨架上播片段，骨骼对不上，"
                   + "角色就塌回绑定姿势（大字）。实测现场：该片段权重 1.00、长 1.17、空=False，"
                   + "一切正常，引擎也不报错 —— 它根本不检查骨架兼容性。"
                   + "拦掉之后单位保持自己的待机动作，严格优于塌成大字。");
        }

        /// <summary>补上缺失的 OffHandAttack —— 利刃之舞「一点动作都没有」那条。</summary>
        internal static int OffHandFallback;
        private static bool _loggedOffHand;
        internal static void NoteOffHandFallback()
        {
            OffHandFallback++;
            if (_loggedOffHand) return;
            _loggedOffHand = true;
            Main.Log("[副手动作兜底] 首次补上 —— 这两个单位的动画集里**没有** OffHandAttack，"
                   + "而我们授予的利刃之舞（ReaperSpringAttackAbility）要用它。"
                   + "引擎原本记一条 Error 返回 null，技能照常结算但人一动不动"
                   + "（不是 T-pose，是完全没动作）。现在改用这个单位自己的主手攻击片段。");
        }

        /// <summary>
        /// 借用 ForceMove 片段当位移施法动画的次数（路线 B）。
        /// ★这条 >0 就说明位移技能终于有动作了★ —— 之前它们要么被原版跳过（大字滑行），
        /// 要么被我换成 Grenade（站着扔手雷再滑过去），两种都不对。
        /// </summary>
        internal static int ForceMoveBorrowed;
        private static bool _loggedFm;
        internal static void NoteForceMoveEntry(object style)
        {
            ForceMoveBorrowed++;
            if (_loggedFm) return;
            _loggedFm = true;
            Main.Log("[位移动画] 首次借用 —— 请求的施法风格 " + style + " 是带位移的，"
                   + "而这个单位的施法表里只有扔手雷（实测 sicarian.animations 全部 27 个片段里，"
                   + "唯一的施法动画就是 Sicarian_Spell_Granade）。"
                   + "改从它自己的 ForceMove 动作里借一个位移片段（Sicarian_Forcemove_v1~v5），"
                   + "骨骼天然匹配、不新增任何资源。★用完立刻从共享表里撤掉★。");
        }

        /// <summary>施法动作风格替换 —— 锈行猎手「没有手指他人那个动作」那条。</summary>
        internal static int CastSwapped;
        private static bool _loggedCastSwap;
        internal static void NoteCastSwap(object from, object to)
        {
            CastSwapped++;
            if (_loggedCastSwap) return;
            _loggedCastSwap = true;
            Main.Log("[施法动作兜底] 首次替换施法风格：" + from + " -> " + to
                   + "　—— 这份动画集里没有前者的条目，原版会 set_IsSkipped 直接跳过："
                   + "不报错、不 T-pose，但**完全没有施法动作**。换成它真有的那种。");
        }
    }

    // ★已删除（1.7.51）★ 补丁 A：挂 WarhammerUnitAnimationActionHandAttack.GetAttackVariant。
    //   实测连续多场「进入 87~129 · 过闸 0」—— 那些调用全是原版单位，
    //   我们这两个精英的攻击走的是 UnitAnimationActionSpecialAttack，从不经过 HandAttack。
    //   它却挂在一个每帧被全场单位走的方法上，纯白付调度成本。
    //
    // ★已删除（1.7.51）★ 补丁 F2（排队饿死保险丝）：挂 AnimationActionHandle.UpdateInternal。
    //   1.7.40 我把它的 Release 动作删了（那个会杀掉循环动画），此后它**只剩计数**，
    //   却仍然挂在「每帧每句柄、全场所有单位」这个最热的方法上，一场触发 198~227 次。
    //   主修 F1′ 已经覆盖，这个保险丝没有存在价值。

    /// <summary>
    /// 【B】护栏：LocoMotion 的空片段会 NRE，然后**永久**打死这个单位的移动动画。
    ///
    /// ═══ 为什么这个比 A 更急（confirmed）═══
    ///     // LocoMotion.UpdateCurrentClips
    ///     AnimationClipWrapper w = SelectClip(handle, forOffhand: false);
    ///     ...
    ///     else if ((bool)w.AnimationClip) { ... }        // ← w 为 null 时 NRE
    ///     else UberDebug.LogError(w, "...", w.name);     // ← 这条也 NRE
    /// NRE 被 AnimationActionHandle.UpdateInternal 的 catch 吞掉（IsFinished = true），
    /// 而致命的是**它不会自愈**：
    ///     · m_LocoMotionHandle 只在 ResetLocoMotion() 里被置 null
    ///     · TryInitLocoMotion() 只在 m_LocoMotionHandle == null 时重建
    ///   ⇒ 句柄永久损坏，单位从此走不了路。实测「3 回合只做了 5 个动作」就是它。
    ///
    /// ★触发条件是「动画状态变化」★ UpdateCurrentClips 在
    ///   InCombat 变化 或 主/副手 WeaponAnimationStyle 变化 时被调；
    ///   而森罗刃网的 ContextActionApplyBuff + PlayLoopAnimationByBuff 精确命中它。
    ///   这也解释了作者观察到的「卡在敌人回合」—— 移动动画死了之后，
    ///   任何需要它的时机（含敌人回合里的反应）都推不下去。
    ///
    /// ★兜底用什么★ 这个动作自己的 NonCombatIdle，再不行取 CombatIdle 的第一条。
    ///   都是它自带的资源，不引入任何外部依赖。
    /// ★这是存量 bug★ 不只影响收割者技能，任何让风格变化的效果都可能触发。
    ///   所以这道护栏即使不开那三个技能开关也该留着。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Visual.Animation.Kingmaker.Actions.WarhammerUnitAnimationActionLocoMotion),
                  "SelectClip")]
    internal static class LocoMotionNullGuardPatch
    {
        private static FieldInfo _idleF, _combatIdleF;
        private static bool _looked;

        /// <summary>
        /// ★1.7.36 收窄（F5）★ 加上 forOffhand 形参并直接放行副手。
        /// 工作流查到 SelectClip 的 IL_0075 有一条**合法**返回 null 的路径：
        ///   forOffhand 且 CombatIdle 条目标了 NoOffHand ⇒ 该单位本来就没有副手 idle 层。
        /// 原来的 Postfix 签名里没有这个参数，于是把这条合法 null 也一起填成了主手 idle 片段 ——
        /// 越界了。实测那 169 次里有多少是这条，现在分不出来，所以先收窄，让数字变干净。
        /// Harmony 按**参数名**绑定原方法形参，名字必须和原版一致（forOffhand）。
        /// </summary>
        private static void Postfix(object __instance, UnitAnimationActionHandle handle,
                                    bool forOffhand, ref AnimationClipWrapper __result)
        {
            try
            {
                if (!AnimFallback.AnyMeleeEliteActive) return;   // ★全局闸：一次静态 bool★
                if (forOffhand) return;                      // 副手的 null 是 vanilla 的正确返回，不该填
                AnimFallback.TL.Entered++;   // ★无条件计数：分开「没进来」和「进来了提前 return」★
                if (__result != null) return;                 // 原版拿到了，什么都不做
                if (handle == null || __instance == null) return;
                if (!AnimFallback.Applies(handle)) return;
                AnimFallback.TL.GatePassed++;

                if (!_looked)
                {
                    _looked = true;
                    var t = __instance.GetType();
                    _idleF = t.GetField("NonCombatIdle", BindingFlags.Public | BindingFlags.NonPublic
                                                       | BindingFlags.Instance);
                    _combatIdleF = t.GetField("CombatIdle", BindingFlags.Public | BindingFlags.NonPublic
                                                          | BindingFlags.Instance);
                }

                var fb = _idleF != null ? _idleF.GetValue(__instance) as AnimationClipWrapper : null;
                if (fb == null && _combatIdleF != null)
                {
                    var l = _combatIdleF.GetValue(__instance) as IList;
                    if (l != null)
                        foreach (var e in l)
                        {
                            fb = e as AnimationClipWrapper;
                            if (fb != null) break;
                        }
                }
                if (fb == null) return;                       // 连兜底都没有，交回原版（还是会 NRE，但我们尽力了）

                AnimFallback.NoteLoco();
                __result = fb;
            }
            catch { }
        }
    }

    /// <summary>
    /// 【C】循环动画兜底 —— ★这条才是森罗刃网 T-pose 的真凶★
    ///
    /// ★为什么 A 和 B 都没接住★ 1.7.23 实测：Harmony 挂载成功、两个计数却都是 0。
    ///   因为 buff 驱动的**循环动画是第三条独立链路**：
    ///     PlayLoopAnimationByBuff.TrySetAction(manager)
    ///       → UnitAnimationManager.BuffLoopAction（管理器上单独的一个句柄）
    ///       → WarhammerBuffLoopAction.GetAnimation(handle, isOffHand)
    ///           m_Animations : List&lt;AnimationEntry{LoopWrapper,EnterWrapper,ExitWrapper,Style,IsOffHand}&gt;
    ///   它既不走 HandAttack.GetAttackVariant（A 补的），也不走 LocoMotion.SelectClip（B 补的）。
    ///
    /// ★为什么表现是「连移动都 T 字型」★ 循环动画在 buff 存续期间一直占着，
    ///   不是某一次攻击缺片段。所以整个人从放完森罗刃网起就定住了。
    ///
    /// ★修法和 A 一样：改查表的键，不造返回值★
    ///   GetAnimation 的返回是嵌套类型 AnimationEntry，自己造一个要复刻四个字段、
    ///   还得保证 IsValid() 通过；改 handle.AttackWeaponStyle 则只动一个枚举，
    ///   后面的取 Loop/Enter/Exit 全由原版跑。
    /// ★只在真的没有匹配项时才动手★ 有匹配就原样返回，绝不干预正常情况。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Visual.Animation.Kingmaker.Actions.WarhammerBuffLoopAction),
                  "GetAnimation")]
    internal static class BuffLoopStyleFallbackPatch
    {
        private static FieldInfo _animsF, _styleF, _offF, _loopF;
        private static bool _looked;

        private static void Prefix(object __instance, UnitAnimationActionHandle handle, bool isOffHand, out object __state)
        {
            __state = null;
            try
            {
                if (!AnimFallback.AnyMeleeEliteActive) return;   // ★全局闸：一次静态 bool★
                AnimFallback.TC.Entered++;   // ★无条件计数：分开「没进来」和「进来了提前 return」★
                if (handle == null || __instance == null) return;
                if (!AnimFallback.Applies(handle)) return;
                AnimFallback.TC.GatePassed++;

                if (!_looked)
                {
                    _looked = true;
                    _animsF = __instance.GetType().GetField("m_Animations",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                }
                if (_animsF == null) return;

                var list = _animsF.GetValue(__instance) as IList;
                if (list == null || list.Count == 0) return;

                object cur = handle.AttackWeaponStyle;
                object cand = null; bool hit = false;
                bool wantLog = AnimFallback.WantLookupLog();      // ★先问开关，再决定要不要收集★
                var have = wantLog ? new System.Collections.Generic.List<string>() : null;
                foreach (var e in list)
                {
                    if (e == null) continue;
                    if (_styleF == null)
                    {
                        var t = e.GetType();
                        _styleF = t.GetField("Style", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _offF   = t.GetField("IsOffHand", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _loopF  = t.GetField("LoopWrapper", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (_styleF == null || _offF == null) return;

                    bool off = false;
                    try { off = (bool)_offF.GetValue(e); } catch { }
                    if (off != isOffHand) continue;             // 手不对，不是候选

                    object st = _styleF.GetValue(e);
                    if (have != null) have.Add(st != null ? st.ToString() : "?");
                    if (Equals(st, cur)) { hit = true; continue; }

                    // 记下第一个「同手、且真有循环片段」的作为备选
                    if (cand == null && _loopF != null && _loopF.GetValue(e) != null) cand = st;
                }
                AnimFallback.TC.Looked++;
                if (wantLog) AnimFallback.NoteLookup("BuffLoop" + (isOffHand ? "(副手)" : "(主手)"), cur, have);
                if (hit || cand == null) return;

                AnimFallback.NoteSwap(cur, cand);
                __state = cur;                       // ★记下原值，Postfix 立刻还原★
                handle.AttackWeaponStyle = (Kingmaker.View.Animation.WeaponAnimationStyle)cand;
            }
            catch { }
        }

        /// <summary>
        /// ★1.7.35：用完立刻还原 —— 这条是回归的元凶★
        ///
        /// 本场实测 BuffLoop 进入 702 次、全部过闸、替换 34 次，是**改写次数最多**的一条。
        /// 而 AttackWeaponStyle 全局共用，改了不还原 ⇒ 森罗刃网的循环动画一起，
        /// 这个单位的走路/平A 就一直拿着被改过的风格去查表，必然错配。
        /// 作者反馈的「锈行者走路变 T-pose」「电僧攻击打不出来」「放了森罗刃网反而好了」
        /// 三条都指向这里 —— 最后一条尤其准：重放一次循环动画会重置通道，排队的攻击才启动。
        /// 详见补丁 A 的 Postfix 说明。
        /// </summary>
        private static void Postfix(UnitAnimationActionHandle handle, object __state)
        {
            try
            {
                if (__state == null || handle == null) return;
                handle.AttackWeaponStyle = (Kingmaker.View.Animation.WeaponAnimationStyle)__state;
            }
            catch { }
        }
    }

    /// <summary>
    /// 【D】血量闸：快死了就别放烧血的技能。
    ///
    /// ★为什么需要★ 收割者线有两个技能的消耗是**生命值**（AbilityResourceWounds）：
    ///     ReaperBloodOath_Ability   鲜血誓言   实测一场放 8~11 次
    ///     ReaperBladeShroud_Ability 森罗刃网   实测一场放 1~2 次
    ///   而 AI 没有「我快死了别放了」的判断，于是**把自己烧死**（作者实机遇到）。
    ///   另外 TacticianFervourAbility（战术狂热）直接执行 ContextActionDealDamage，
    ///   不带 AbilityResourceWounds，同样必须拦截。2026-09-06 实测旧门禁在 17% HP
    ///   已拦住收割者技能，锈行猎手随后仍释放战术狂热并承受 64 点直击自伤。
    ///
    /// ★为什么补 IsAvailable★ 它是 AI 和 UI 共用的「这招现在能不能放」，
    ///   Postfix **只收紧不放宽**（原版说不行就直接返回），是最小侵入的挂点。
    ///   同一个 getter 上已经有 OncePerTurnPatch，Harmony 支持多个补丁共存。
    ///   原版 UnitUseAbility.OnAction 在实际执行前还会重读 IsAvailable，因此无需新加
    ///   施法/伤害钩子；只拒绝释放，不篡改已发生的伤害或补血。
    ///
    /// ★阈值可调★ 默认 50%。设成 0 等于关掉这道闸。
    /// ★只管我们的近战精英★ 原版收割者 NPC 该怎么自尽还怎么自尽，不干预。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Abilities.AbilityData), "IsAvailable", MethodType.Getter)]
    internal static class WoundCostHpGatePatch
    {
        private static readonly string[] WoundCostAbilities =
        {
            "590c990c1d684fd09ae883754d28a8ac",   // ReaperBloodOath_Ability   鲜血誓言
            "8b7bcaa093224422ac66c80ffcf69f6d",   // ReaperBladeShroud_Ability 森罗刃网
            "305858b91e6e4d89bff75431fa6030e6",   // TacticianFervourAbility   战术狂热（直接自伤动作）
        };


        /// <summary>
        /// ★1.7.58 重写：按蓝图实例缓存判定结果★
        ///
        /// 1.7.57 我想用「预解析成蓝图对象 + 引用比较」替掉每次的字符串分配，结果更糟：
        ///     EnsureBlueprintCache() 每次调用都进，而只有两个都解析成功才置 _woundBpsReady。
        ///     一旦 TryGetBlueprint 解析不到（类型不对/库未就绪），标志永远是 false ⇒
        ///     **每次 IsAvailable 都做两次蓝图查找**，比原来那一次字符串分配贵得多，
        ///     而且就在 AI 决策的最热路径上。实测帧率从 302~337 掉到 170~271。
        ///     ★教训★ 「懒加载 + 失败不缓存」在热路径上等于「每次都重试」，
        ///     写的时候要问一句：解析失败时这段代码会跑多少次？
        ///
        /// 现在不依赖解析：**第一次见到某个蓝图实例时算一次 guid 字符串，把 bool 存进字典**。
        /// 之后同一个蓝图来了就是一次字典查找，零分配、也不怕解析失败。
        /// 场上蓝图种类有限，字典不会膨胀；超过上限就整桶倒掉重来（幂等）。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<object, bool> _woundMatch =
            new System.Collections.Generic.Dictionary<object, bool>();

        private static bool IsWoundCostAbility(Kingmaker.Blueprints.SimpleBlueprint bp)
        {
            bool hit;
            if (_woundMatch.TryGetValue(bp, out hit)) return hit;
            hit = false;
            try
            {
                string g = bp.AssetGuid.ToString();          // ★整个生命周期里每个蓝图只算一次★
                for (int i = 0; i < WoundCostAbilities.Length; i++)
                    if (string.Equals(g, WoundCostAbilities[i], StringComparison.OrdinalIgnoreCase))
                    { hit = true; break; }
            }
            catch { }
            if (_woundMatch.Count > 512) _woundMatch.Clear();   // 换存档/换区攒多了整桶倒掉
            _woundMatch[bp] = hit;
            return hit;
        }

        private static void Postfix(Kingmaker.UnitLogic.Abilities.AbilityData __instance, ref bool __result)
        {
            try
            {
                // ═══ 1.7.57 热路径优化 ═══
                // AbilityData.IsAvailable 是 **AI 每次决策评估所有单位的所有技能** 时都会读的，
                // 是本 mod 挂过的最热的方法之一。原来这里每次调用都做：
                //     string g = __instance.Blueprint.AssetGuid.ToString();   // ★分配一个字符串★
                //     两次 OrdinalIgnoreCase 字符串比较
                // 和我在 GateNote 里犯过的是同一类错误：**在热路径上无条件分配**。
                // 现在改成：全局存在闸（静态 bool）→ 结果/开关的廉价判断 → **蓝图引用比较**。
                // 引用比较是指针比较，零分配；蓝图对象在库里是单例，可以这么比。
                if (!AnimFallback.AnyMeleeEliteActive) return;   // ★场上没我们的精英，一次 bool 就走★
                if (!__result) return;                       // 原版已经说不可用，不放宽
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || s.WoundAbilityHpFloor <= 0) return;
                var bp = __instance != null ? __instance.Blueprint : null;
                if (bp == null) return;

                if (!IsWoundCostAbility(bp)) return;

                var caster = __instance.Caster as BaseUnitEntity;
                if (!WeaponGate.IsGateTarget(caster)) return;

                var h = caster.GetHealthOptional();
                if (h == null) return;
                int hp = h.HitPointsLeft, maxHp = h.MaxHitPoints;
                if (IsBelowHpFloor(hp, maxHp, s.WoundAbilityHpFloor))
                {
                    __result = false;
                    WoundGateNote(caster, bp, hp, maxHp, s.WoundAbilityHpFloor);
                }
            }
            catch { }
        }

        /// <summary>严格低于阈值；不计临时 HP。整数比较保证恰好 50% 放行且无浮点舍入。</summary>
        internal static bool IsBelowHpFloor(int hp, int maxHp, int floorPercent)
        {
            return maxHp > 0 && floorPercent > 0
                && (long)hp * 100 < (long)maxHp * floorPercent;
        }

        /// <summary>只记一次，别每次刷新 UI 都打。</summary>
        private static bool _logged;
        private static void WoundGateNote(BaseUnitEntity u, Kingmaker.Blueprints.SimpleBlueprint bp,
            int hp, int maxHp, int floorPercent)
        {
            Blocked++;
            if (_logged) return;
            _logged = true;
            Main.Log("[血量闸] " + (u != null ? u.CharacterName : "?")
                   + " HP=" + hp + "/" + maxHp + " 低于 " + floorPercent
                   + "% 阈值，暂停自伤技能（鲜血誓言/森罗刃网/战术狂热）。"
                   + "本次被拦技能=" + bp.AssetGuid + "。本次会话只报这一条。");
        }

        internal static int Blocked;
    }

    // ★已删除（1.7.48）★ 补丁 E：挂在 WarhammerUnitAnimationActionCastSpell.GetAnimationVariant 上。
    //   实测连续四场「进入 0」—— 真正跑施法的是 UnitAnimationActionCastSpell（无 Warhammer 前缀），
    //   由补丁 J 处理。挂着的这个从头到尾是死代码，白占一个 Harmony 钩子。
    //
    // ★已删除（1.7.48）★ 补丁 F：挂在 UnitAnimationActionJump.GetAnimation 上。
    //   同样连续多场「进入 0」。_kt_callers 证明 GetAnimation 只被 UnitAnimationActionJump
    //   自己的 OnStart/StartFlyAnimation 调用，而死从天降走的是 CastSpell 不是 Jump ——
    //   我最初「死从天降=跳跃动画」那个前提本身就是错的。

    /// <summary>
    /// 【G】动画死锁破除 —— ★1.7.55 恢复：1.7.48 被我误删了★
    ///
    /// ═══ 误删经过（记下来，别再犯）═══
    /// 1.7.48 删死补丁 E 和 F 时，我用的剪切范围是「从【F】剪到【H】」，
    /// 而 G 正好夹在 F 和 H 之间，**被一起吞掉了**。当时只核对了编译通过，
    /// 没核对补丁清单少了谁 —— 而它是静默失效：没有报错，只是那类死锁不再被接住。
    /// 后果：1.7.52 实测两次卡死（锈行猎手 5.6 秒、电僧 5.9 秒），
    /// 快照与 G 的判据逐项吻合：
    ///     IsStarted=True  IsFinished=False  IsReleased=False  ActiveAnimation=null  队列=0
    /// 我一度归因到路线 B 和全局闸，其实都不是。
    /// ★教训★ 批量删代码后要核对「补丁类清单」，不能只看编译通过。
    ///
    /// ═══ 它治什么 ═══
    /// 句柄**已启动、没释放、但没有任何在播的动画**。这种状态下引擎的 Update
    /// 第一行就 return，永远走不到 Release() ⇒ 句柄永不完成 ⇒ 指令一直等到回合超时。
    /// 补一次 Release()，下一轮 UpdateActions 就能正常走 FinishInternal。
    /// ★不是绕过★ 动画本来就没在播，释放它不打断任何东西。
    /// ★作用域★ 全局闸 + Applies —— 只有我们两个近战精英。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Visual.Animation.Actions.AnimationActionHandle), "UpdateInternal",
                  new Type[] { typeof(float) })]
    internal static class AnimDeadlockBreakerPatch
    {
        private static void Postfix(Kingmaker.Visual.Animation.Actions.AnimationActionHandle __instance)
        {
            try
            {
                if (!AnimFallback.AnyMeleeEliteActive) return;   // ★全局闸：一次静态 bool★
                if (__instance == null) return;
                // ★便宜/常见判据必须先走★ 这是每个动画句柄每帧的 Postfix；绝大多数正常句柄
                //   都会在这里返回，不能先去读 NetworkingManager。
                if (!__instance.IsStarted || __instance.IsFinished || __instance.IsReleased) return;
                if (__instance.ActiveAnimation != null) return;  // 正常在播，别碰

                var h = __instance as UnitAnimationActionHandle;
                if (h == null || !AnimFallback.Applies(h)) return;

                // ★联机禁用本机动画状态救场★ 只在真正命中异常目标、紧邻 Release 前才读。
                // ActiveAnimation 属于本机表现状态；Release 会改变指令完成时刻。
                if (CoopState.SharedGameplayRequired) return;
                AnimFallback.NoteDeadlock();
                __instance.Release();
            }
            catch { }
        }
    }

    // ═══════════════ 1.7.49 清理：删掉已证伪理论留下的僵尸补丁 ═══════════════
    //
    // ★为什么删★ 作者反馈游戏变卡，并指出 1.5.21 的基线是**同一场战斗**，所以可比：
    //     1.5.21   712 / 727 / 602 / 358 帧（10 秒内）
    //     1.7.48   314 / 312 / 273 / 303   ← 关着诊断日志，仍差一倍
    // 差的这一倍全是功能性补丁的成本，而下面这几个**根本没在干活**：
    //
    //   AttackBlockOnHandAttackPatch   挂 HandAttack.UpdateInternal（每帧）   实测触发 0 次
    //   AttackBlockOnCastSpellPatch    挂 CastSpell.OnUpdate（每帧）          实测触发 0 次
    //   CoverThrowUnblockPatch         挂 Cover.UpdateAnimations              实测触发 0 次
    //   AttackBlockWatchdog（配套逻辑）                                        解除攻击屏蔽 自己 0 / 父视图 0
    //
    // 这四个都是「BlockAttackAnimation 卡在 true」那套根因的遗留。那套理论已被实测推翻
    // （连续多场「掩体抛异常 0 次 / 解除攻击屏蔽 0 次」），真因是 Sequenced 队列饿死。
    // 留着它们只有每帧的 Harmony 调度开销，一点收益都没有。
    //
    // ★教训★ 假说被推翻时，要连同它派生出的补丁一起撤 —— 我当时只更新了注释，
    //   补丁却留在热路径上继续白跑了十几个版本。
    // ═══════════════════════════════════════════════════════════════════════

    // 【P3】风格重算补丁已于 1.7.35 删除。
    // 理由：1.7.34 实机 0 次触发（零收益），而它是「全局改写武器动画风格」
    // 这一类做法 —— 补丁 A/C/E 同类的改写已被实测证明会污染走路和平A
    // （作者原话：「锈行者走路感觉都变成 t pose 移动了，原本正常的也变不正常了」）。
    // 收刀走路时风格本来就该是 None，强行改掉必然打坏移动动画。
    // 要治 T-pose 只能在**单次查表调用内**替换并立刻还原，不能留在共享状态里。

    /// <summary>
    /// 【F1′】★主修：电僧「攻击放不出来 + 卡到回合超时」的根因★
    ///
    /// ═══ 根因（逐环 IL 确证）═══
    /// 环1 指令侧　AbstractUnitCommand.StartAnimation：
    ///     IL_0009 set_HasAnimation(true)   ← **无条件写 true，在 Execute 之前**
    ///   ⇒ 快照里的 HasAnimation=True 只代表「句柄挂上了」，和有没有启动无关。
    ///     这个假信号把我误导了好几轮，写在这里免得再犯。
    ///
    /// 环2 AnimationManager.Execute 的分叉点（ExecutionMode 全库唯一读取点）：
    ///     IL_01E2 ldfld AnimationActionBase::ExecutionMode
    ///     IL_01E7 brtrue -> 0x02E8
    ///   · Interrupted(0)：AddActionHandle → Release 掉旧的 m_CurrentAction → StartInternal（**必启动**）
    ///   · Sequenced(1) ：if (m_CurrentAction == null || m_CurrentAction.DontReleaseOnInterrupt) 启动
    ///                    else if (队列 > 10) 报 Warning 丢弃
    ///                    else **Queue.Enqueue —— 唯一静默出口，不入表、不启动**
    ///   ★这条分支只看 null 和 DontReleaseOnInterrupt，**不看 IsReleased**★ —— 修法就着力在这。
    ///
    /// 环3 为什么下一帧也补不上　UpdateActions：
    ///     顶部循环只遍历 m_ActiveActions，给未启动的补 StartInternal —— 排队中的句柄不在这张表里。
    ///     排空条件是 (m_CurrentAction == null || **IsReleased** || DontReleaseOnInterrupt) && 队列 > 0。
    ///   ⇒ 占位者不 Release，队列永远不排空。死锁闭合。
    ///
    /// 环4 占位者是谁：
    ///   · DontReleaseOnInterrupt 全库只有 LocoMotion 两个 override 返回 true
    ///     ⇒ 能卡住的只有「非 LocoMotion 且未 Release 的 m_CurrentAction」
    ///   · PlayLoopAnimationByBuff.TryRequeueAction 把 buff 循环的 ExecutionMode **强制写成 1**，
    ///     再 CreateHandle + Execute 接管 m_CurrentAction；它是循环、自己不结束、
    ///     DontReleaseOnInterrupt=false ⇒ 一旦接管就永久堵死队列。
    ///   · 引擎自己也承认：get_IsBusyByLoopAnimation = CurrentAction.Action is WarhammerBuffLoopAction
    ///   · 两个精英可达的全部蓝图里，**只有森罗刃网**（ReaperBladeShroud_Caster_Buff）
    ///     挂了 PlayLoopAnimationByBuff。
    ///
    /// 环5 A/B 对照（实测资源包）：
    ///     ElectroPriest_AnimationSet_Melee / _v2   ExecutionMode=**1**（排队型）
    ///     Sicarian_Chaos_AnimationSet_Melee        ExecutionMode=**0**（打断型）
    ///   其余字段全同。★这就是为什么同样的技能线，锈行猎手平A 正常、电僧卡死★。
    ///
    /// ═══ 这也解释了作者的两条观察 ═══
    ///   「放了森罗刃网之后攻击反而打出来了」—— 重放会新建一个 loop 句柄再 Execute，
    ///     那一次把旧的 m_CurrentAction 顶掉了，排队的攻击才终于轮上。
    ///   「电僧以前平A是好的」—— 以前没有森罗刃网，就没有东西占 m_CurrentAction。
    ///
    /// ═══ 修法 ═══
    /// 在 Execute 之前，若「这次一定会被排队」的条件成立，就先把占位的循环句柄 Release 一下。
    /// 本次 Execute 仍然入队（Sequenced 分支不看 IsReleased），但**下一帧** UpdateActions
    /// 的排空条件满足 ⇒ Dequeue → AddActionHandle → StartInternal。代价一帧（~16ms）。
    ///
    /// ★为什么不污染共享状态★ Release() 只动这一个 per-unit 运行时句柄，
    ///   不碰 ScriptableObject、不写 ExecutionMode、不写 AttackWeaponStyle。
    ///   而且 Release 自带幂等（第一行 if (IsReleased) return）。
    ///   **这正是 vanilla 对锈行猎手每次平A做的同一件事**（Execute 的 Interrupted 分支 IL_0245）。
    /// ★作用域★ 四道闸层层收口，最后才是 Applies（只我们两个卫兵）。
    /// ★开销★ 前三道全是字段读 + isinst，非目标一律立刻 return。
    /// </summary>
    [HarmonyPatch]
    internal static class SequencedStarvationFixPatch
    {
        /// <summary>★必须精确定位★ UnitAnimationManager 有同名 override，DeclaredMethod 才不会挂错。</summary>
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.DeclaredMethod(
                typeof(Kingmaker.Visual.Animation.AnimationManager), "Execute",
                new Type[] { typeof(Kingmaker.Visual.Animation.Actions.AnimationActionHandle) });
        }

        private static void Prefix(Kingmaker.Visual.Animation.AnimationManager __instance,
                                   Kingmaker.Visual.Animation.Actions.AnimationActionHandle handle)
        {
            try
            {
                if (!AnimFallback.AnyMeleeEliteActive) return;   // ★全局闸：一次静态 bool★
                if (__instance == null || handle == null) return;

                // ① 只管排队型

                AnimTimeline.NoteCreated(handle);   // 时间轴：记下这个句柄进入 Execute 的时刻 —— 打断型本来就必启动，不会饿死
                var act = handle.Action;
                if (act == null ||
                    act.ExecutionMode != Kingmaker.Visual.Animation.Actions.ExecutionMode.Sequenced) return;

                // ② 占位者存在，且不受 DontReleaseOnInterrupt 保护 —— 照抄 IL_02EE/02F6
                //    ★不要加 IsReleased 判断★：Sequenced 分支根本不看它，加了会漏掉真正该救的那次
                var cur = __instance.CurrentAction;
                if (cur == null || cur.DontReleaseOnInterrupt) return;

                // ③ 收口到唯一已知的堵塞者：buff 循环动画
                if (!(cur.Action is Kingmaker.Visual.Animation.Kingmaker.Actions.WarhammerBuffLoopAction)) return;

                // ④ 最后才查是不是我们的卫兵（这一步会走 handle.Unit，最贵，放最后）
                var uh = handle as UnitAnimationActionHandle;
                if (uh == null || !AnimFallback.Applies(uh)) return;

                // ★1.7.40 改法★ 原来这里是 cur.Release()。它确实能放行队列，
                //   但**把森罗刃网的循环动画杀掉了** —— 而 PlayLoopAnimationByBuff 只在
                //   HandleUnitCommandDidStart（下一条指令开始）和 UnitAttackOfOpportunity
                //   .RestoreLoopAnimation 里恢复循环。回合结束后没有新指令，
                //   循环就再也回不来，单位一直站在绑定姿势里。
                //   作者实测：「锈行者第一回合结束之后一直在 T-pose 状态」，本场释放 15 次。
                //   现在改成把循环句柄标记为「受保护」（补丁 K），让 Execute 走
                //   IL_02F6 那条「直接启动」分支 —— 循环动画原封不动，队列照样排空。
                //   这里只留计数，用来观察这条路径被走过多少次。
                AnimFallback.ProtectScope = true;    // ★只在这一次 Execute 内★
                AnimFallback.NoteQueueUnblock();
            }
            catch { }
        }

        /// <summary>
        /// ★用完立刻关掉作用域★ 无论 Prefix 有没有打开、原方法有没有抛，都要复位 ——
        /// 漏一次就退化成 1.7.40 那个「永久受保护」的坏状态。
        /// 用 Finalizer 而不是 Postfix：原方法抛异常时 Postfix 不执行，Finalizer 执行。
        /// </summary>
        private static Exception Finalizer(Exception __exception)
        {
            AnimFallback.ProtectScope = false;
            return __exception;
        }
    }

    /// <summary>
    /// 【K】★把 buff 循环动画标记为「受保护」—— 排队饿死的正解★
    ///
    /// ═══ 为什么不能用 Release（1.7.36~1.7.39 的做法，已证明有害）═══
    /// 释放循环句柄确实能让 Sequenced 队列排空，但它**杀掉了森罗刃网的循环动画**。
    /// 而 PlayLoopAnimationByBuff 恢复循环只有两条路（_kt_callers 全库扫描）：
    ///     PlayLoopAnimationByBuff.HandleUnitCommandDidStart   —— 下一条指令开始时
    ///     UnitAttackOfOpportunity.RestoreLoopAnimation        —— 借由 TryRequeueAction
    /// 回合结束之后**没有新指令**，于是循环再也回不来，单位一直停在绑定姿势。
    /// 作者实测：「锈行猎手第一回合结束之后一直在 T-pose 状态」，同场释放 15 次。
    ///
    /// ═══ 正解 ═══
    /// Execute 的排队分支（IL_02EE/02F6）是：
    ///     if (m_CurrentAction == null || m_CurrentAction.DontReleaseOnInterrupt) 直接启动
    ///     else if (队列 > 10) 丢弃  else Enqueue
    /// 只要把占位的循环句柄答成「受保护」，排队的攻击就走**直接启动**那条，
    /// 而循环动画**原封不动继续播**。
    /// ★1.7.78 更正★ 这里原本写着「UpdateActions 的排空条件读的是同一个属性，
    ///   一处生效两处受益」—— **不成立**。ProtectScope 只在 base Execute 那一次调用内为真
    ///   （Prefix 置位、Finalizer 复位），而 UpdateActions 由 Update 驱动、跑在窗口之外，
    ///   那时补丁第一行就 return，IL_0113 读到的是 vanilla 的 false ⇒ 队列并不会因此排空。
    ///   这个「附带好处」从来没存在过。留着这条更正是因为：错的注释比没有注释更贵，
    ///   它会让后来的人（包括我自己）据此排除掉本该怀疑的方向。
    ///
    /// ★这不是绕法，是 vanilla 自己的路径★ 全库只有 LocoMotion 两个类的
    ///   DontReleaseOnInterrupt 返回 true（IL 都是 [17 2A]），
    ///   即「移动动画在场时，排队动作照常启动」。我们只是把同样的待遇给 buff 循环。
    /// ★不写任何共享状态★ 这是个 Postfix，只改这一次读取的返回值，
    ///   不碰 ScriptableObject、不碰 ExecutionMode、不碰句柄字段。
    /// ★作用域★ 只有我们两个卫兵、且只有 buff 循环这一种动作。
    /// ★开销★ 第一道闸是 isinst，非目标一次类型检查就走掉。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Visual.Animation.Actions.AnimationActionHandle),
                  "get_DontReleaseOnInterrupt")]
    internal static class BuffLoopProtectPatch
    {
        private static void Postfix(Kingmaker.Visual.Animation.Actions.AnimationActionHandle __instance,
                                    ref bool __result)
        {
            try
            {
                // ★闸的顺序按成本排★ 这个属性是每帧每单位都读的热路径。
                //   ProtectScope 是个静态 bool，绝大多数调用一次比较就走掉。
                if (!AnimFallback.ProtectScope) return;           // ★只在 Execute 那一次调用内★
                if (__result) return;                            // 原版本来就说受保护
                var uh = __instance as UnitAnimationActionHandle;
                if (uh == null) return;
                if (!(uh.Action is Kingmaker.Visual.Animation.Kingmaker.Actions.WarhammerBuffLoopAction)) return;
                if (!AnimFallback.Applies(uh)) return;           // 只我们两个卫兵
                __result = true;
                AnimFallback.NoteLoopProtect();
            }
            catch { }
        }
    }

    /// <summary>
    /// 【I】补上单位缺失的 OffHandAttack 动作 —— ★利刃之舞「一点动作都没有」的真因★
    ///
    /// ═══ 引擎自己报的（GameLogFull.txt，实测两次）═══
    ///     Cast ReaperSpringAttackAbility[caster=锈行猎手]
    ///     [Animations][Error]: /Sicarian(Clone)/Sicarian_RIG      Has no animation of type OffHandAttack
    ///     Cast ReaperSpringAttackAbility[caster=电僧]
    ///     [Animations][Error]: /ElectroPriest(Clone)/ElectroPriest_RIG Has no animation of type OffHandAttack
    /// 两个单位的动画集里**都没有** OffHandAttack(5)。CreateHandle 记一条 Error 返回 null，
    /// StartAnimation 见 null 直接 ScheduleAct —— 所以表现是「技能结算了但人一动不动」，
    /// 而不是 T-pose。作者反馈的「没有手指其他人的那个动作」就是这一类。
    ///
    /// ★为什么这两个单位没有 OffHandAttack★ 它们是原版的单手/拳套近战 NPC，
    ///   本来就没有副手攻击的设计。是**我们**授予的收割者技能线要用它。
    ///   所以这不是原版 bug，是我们加东西造成的缺口，该由我们补上。
    ///
    /// ═══ 修法 ═══
    /// 原版返回 null 时，改用同一个单位的 MainHandAttack(2) 动作建句柄。
    /// ★不是造新资源★ 用的是这个单位自己就有的主手攻击片段，只是换只手挥。
    /// 最差是动作不那么贴切，但比完全不动好 —— 而且这正是作者要的「替代动作」。
    ///
    /// ★为什么走另一个重载而不是递归★ CreateHandle(AnimationActionBase) 和
    ///   CreateHandle(UnitAnimationType, bool) 是两个不同的方法，走前者天然不会递归回本补丁。
    /// ★不污染共享状态★ 只产出一个新的 per-unit 运行时句柄，不写任何 ScriptableObject、
    ///   不改 AnimationSet、不碰 AttackWeaponStyle。
    /// ★闸的顺序★ 先判 __result==null（绝大多数调用直接走掉）、再判 type，最后才查单位。
    ///   CreateHandle 是每次技能施放时调用，不是每帧。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Visual.Animation.Kingmaker.UnitAnimationManager), "CreateHandle",
                  new Type[] { typeof(Kingmaker.Visual.Animation.Kingmaker.UnitAnimationType), typeof(bool) })]
    internal static class OffHandAttackFallbackPatch
    {
        private static void Postfix(Kingmaker.Visual.Animation.Kingmaker.UnitAnimationManager __instance,
                                    Kingmaker.Visual.Animation.Kingmaker.UnitAnimationType type,
                                    ref UnitAnimationActionHandle __result)
        {
            try
            {
                if (!AnimFallback.AnyMeleeEliteActive) return;   // ★全局闸：一次静态 bool★
                if (__instance == null) return;

                // ★1.7.38 探针：我们授予的技能到底要哪种动画、拿没拿到★
                //   死从天降仍然 T-pose，而它在游戏日志里**不报** Has no animation ——
                //   说明第①层查表是成功的，病在后面。这一行把「哪个技能 → 要哪种动画类型
                //   → 拿到没有」直接打出来，一场就能定死它走的是哪条路。
                //   ★放在所有闸之前★，因为拿到了（__result != null）的情况同样要记录 ——
                //   1.7.23 那次就是只在失败时才记，结果「命中了」和「补丁没跑」看着一样。
                try
                {
                    if (Main.Settings != null && Main.Settings.DiagVerbose)
                    {
                        var v0 = __instance.GetComponentInParent<
                            Kingmaker.View.Mechanics.Entities.AbstractUnitEntityView>();
                        var u0 = v0 != null ? v0.EntityData as BaseUnitEntity : null;
                        if (u0 != null && WeaponGate.IsGateTarget(u0)
                            && AnimFallback.IsOurGrantedAbility(u0))
                            AnimFallback.NoteCreate(u0, type, __result != null);
                    }
                }
                catch { }

                if (__result != null) return;                       // 原版拿到了，什么都不做
                if (type != Kingmaker.Visual.Animation.Kingmaker.UnitAnimationType.OffHandAttack) return;

                // 只管我们的两个近战精英
                var view = __instance.GetComponentInParent<
                    Kingmaker.View.Mechanics.Entities.AbstractUnitEntityView>();
                var u = view != null ? view.EntityData as BaseUnitEntity : null;
                if (u == null || !WeaponGate.IsGateTarget(u)) return;

                // ★1.7.38★ 只补**我们授予的**技能。原版自带技能缺动作是它自己的设计，
                //   插手会打乱它的节奏（1.7.37 就是这么把利刃切割的位移搞没的）。
                if (!AnimFallback.IsOurGrantedAbility(u)) return;

                // ★1.7.39★ 先试 MainHandAttack，拿不到再退到「近战特殊攻击」。
                //   实测这两个单位**连 MainHandAttack 都没有**（战报：HandAttack 进入 87 · 过闸 0，
                //   这条链路对它们从来不走），它们的攻击走的是 UnitAnimationActionSpecialAttack
                //   （硬解资源包确认：ElectroPriest_AnimationSet_Melee，m_AttackType=6 Melee）。
                //   1.7.38 只试主手，于是 alt 恒为 null，兜底自己也失败 —— 战报里
                //   「补副手动作 0 次」而路由日志明明写着「没拿到 OffHandAttack」，就是这个矛盾。
                Kingmaker.Visual.Animation.Kingmaker.UnitAnimationAction alt =
                    __instance.GetAction(
                        Kingmaker.Visual.Animation.Kingmaker.UnitAnimationType.MainHandAttack);
                if (alt == null)
                    alt = __instance.GetAction(
                        Kingmaker.Visual.Animation.Kingmaker.UnitAnimationSpecialAttackType.Melee);
                if (alt == null) return;                            // 两种都没有，交回原版

                __result = __instance.CreateHandle(alt) as UnitAnimationActionHandle;
                if (__result != null) AnimFallback.NoteOffHandFallback();
            }
            catch { }
        }
    }

    /// <summary>
    /// 【J】补上缺失的施法动作 —— ★锈行猎手放辅助技能「没有手指他人那个动作」的真因★
    ///
    /// ═══ 机制（IL 确证）═══
    /// 真正跑施法动画的是 `UnitAnimationActionCastSpell`（**不是** Warhammer 那个 ——
    /// 补丁 E 挂错了类，实测「进入 0」，已确认是死代码）。它的 OnStart 是：
    ///     Animations.SingleOrDefault(e =&gt; e.Style == handle.CastStyle)
    ///       找不到 → set_IsSkipped(true) / Release / RestoreLoopAnimation / ret
    ///                                     ↑ **优雅跳过：不报错、不 T-pose、但也没有任何动作**
    ///     entry.Overrides.SingleOrDefault(按 handle.AttackWeaponStyle)
    ///       取不到 → 落到 entry.Default；Default 为空才是 T-pose
    ///
    /// 作者观察：同样的辅助技能（含枪炮奏鸣），**电僧有指人的动作、锈行猎手没有**。
    /// ⇒ 两边动画集里 Animations 的 CastStyle 覆盖范围不同，锈行猎手缺它请求的那一种。
    ///
    /// ═══ 修法 ═══
    /// 请求的 CastStyle 在表里找不到时，换成这份动画集**真有**的一种，让它至少播个施法动作。
    /// ★用完立刻还原★ Prefix 设值 → 原版查完表并 StartClip → Postfix 还原。
    ///   CastStyle 和 AttackWeaponStyle 一样是句柄上的共享状态，
    ///   1.7.34 就是栽在「改了不还原」上（锈行猎手走路变 T-pose），这次一开始就按还原写。
    /// ★只在原版必然「无动作」时才介入★ 表里有匹配项就原样放行，不改变任何正常行为。
    /// ★挑哪一个★ 优先选 Default 非空的条目 —— 那种至少保证有片段可播；
    ///   一个都没有就不动手，交回原版（宁可没动作，也不制造 T-pose）。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Visual.Animation.Kingmaker.Actions.UnitAnimationActionCastSpell), "OnStart")]
    internal static class CastStyleFallbackPatch
    {
        /// <summary>当前正在放的技能名，取不到就 "?"。只用于日志去重和判读。</summary>
        private static string AbilityNameOf(UnitAnimationActionHandle handle)
        {
            try
            {
                var use = AnimFallback.CurrentUse(AnimFallback.UnitOf(handle));
                if (use != null && use.Ability != null && use.Ability.Blueprint != null)
                    return use.Ability.Blueprint.name;
            }
            catch { }
            return "?";
        }

        private static FieldInfo _animsF, _styleF, _defF, _clipF;
        private static bool _looked;

        private static void Prefix(object __instance, UnitAnimationActionHandle handle, out object __state)
        {
            __state = null;
            try
            {
                if (!AnimFallback.AnyMeleeEliteActive) return;   // ★全局闸：一次静态 bool★
                AnimFallback.TJ.Entered++;
                if (__instance == null || handle == null) return;
                if (!AnimFallback.Applies(handle)) return;

                // ★1.7.38★ 同上：只管我们授予的收割者技能。
                //   1.7.37 没这道闸，把请求 Fly（带位移）的自带技能替换成 Grenade（站着扔），
                //   利刃切割的位移直接消失。原版跳过施法动作是正常的，别管。
                if (!AnimFallback.IsOurGrantedAbility(AnimFallback.UnitOf(handle))) return;

                // ★1.7.82 手势限流★ 同一回合同一技能只补第一次。
                //   实测回合 1 的 40 秒 AI 预算里有 21.3 秒是我们的施法动画，
                //   其中鲜血誓言 ×7 就占 8.75 秒 —— 而后 6 次手势是同一个动作重复，
                //   没有增量信息，却把杂技表演的完整回溯挤出了预算。详见 AnimFallback 里的说明。
                if (!AnimFallback.GestureBudgetAllows(AnimFallback.UnitOf(handle))) return;

                AnimFallback.TJ.GatePassed++;

                if (!_looked)
                {
                    _looked = true;
                    _animsF = __instance.GetType().GetField("Animations",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_animsF == null) return;
                var list = _animsF.GetValue(__instance) as IList;
                if (list == null || list.Count == 0) return;

                object cur = handle.CastStyle;
                object cand = null; bool hit = false;
                bool wantLog = AnimFallback.WantLookupLog();      // ★先问开关，再决定要不要收集★
                var have = wantLog ? new System.Collections.Generic.List<string>() : null;
                foreach (var e in list)
                {
                    if (e == null) continue;
                    if (_styleF == null)
                    {
                        var t = e.GetType();
                        _styleF = t.GetField("Style", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _defF   = t.GetField("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_defF != null)
                            _clipF = _defF.FieldType.GetField("CastClip",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    if (_styleF == null) return;
                    object st = _styleF.GetValue(e);
                    if (have != null) have.Add(st != null ? st.ToString() : "?");
                    if (Equals(st, cur)) { hit = true; continue; }
                    // ★1.7.39 收严★ 光判 Default 非空不够 —— 那只是个容器对象，
                    //   真正的片段在 AnimationEntry.CastClip 里。1.7.38 就是只判了容器，
                    //   结果可能换到一个「有条目、但没片段」的风格上，换完照样 T-pose。
                    if (cand == null && _defF != null)
                    {
                        var def = _defF.GetValue(e);
                        if (def != null && _clipF != null && AnimFallback.HasClip(_clipF.GetValue(def))) cand = st;
                    }
                }

                // ★1.7.46 删掉「Fly 不替换」那条例外★
                //   我 1.7.44 加它是为了保护利刃切割的位移，但那是**多余的**：
                //   利刃切割是自带技能（DLC3_OP_*），白名单本来就不放行，压根轮不到这里。
                //   而它误伤了**全部**收割者技能 —— 它们请求的正是 Fly。实测对比：
                //       森罗刃网      1.7.43: 1490 ms  →  1.7.45: ★6 ms★
                //       死从天降      1.7.43: 1459 ms  →  1.7.45: ★15 / 5 / 10 ms★
                //       死从天降·打击 1.7.45: ★16 / 10 / 11 ms★
                //   6~16 ms 就是 set_IsSkipped → Release，播了等于没播 = 满场大字。
                //   对照：同一场里利刃切割的 CastSpell 15 ms（原版跳过，正常）
                //   而它自己的 UnitAnimationActionClip 播了 1498 ms —— 位移动画一直是它给的，
                //   跟我替不替换 CastStyle 无关。所以那条例外只有害处，没有作用。
                //   ★教训★ 出于「谨慎」加的额外闸，如果没有实测支撑，就是在凭想象设限。

                AnimFallback.TJ.Looked++;
                // ★带上技能名★ NoteLookup 按「动作|要的风格」去重，
                //   1.7.38 因此只留下第一个技能的那行，死从天降请求的到底是哪种风格看不到。
                if (wantLog) AnimFallback.NoteLookup("CastSpell/" + AbilityNameOf(handle), cur, have);
                if (hit || cand == null) return;

                // ═══ 1.7.50【路线 B】带位移的施法：临时借单位自己的 ForceMove 片段 ═══
                //
                // ★为什么需要它★ dump 资源包实测，锈行猎手的 sicarian.animations 里
                //   **唯一的施法动画是 Sicarian_Spell_Granade（扔手雷）**。
                //   把请求 Fly（带位移）的技能换成 Grenade，等于让它站着扔手雷再滑过去 ——
                //   1.7.37 就是这么把利刃切割的位移观感弄坏的。
                //   但同一个包里有 **Sicarian_Forcemove_v1~v5 五个位移片段**，
                //   只是原版没把它们注册成施法条目。这些片段本来就属于这个单位、
                //   骨骼天然匹配，拿来当位移施法的动作是最贴切的选择。
                //
                // ★不新增 AssetId★ 只是引用单位自己动画集里已有的 ClipWrapper。
                // ★共享状态：插入 + 立刻撤★ Animations 这张表挂在共享的 ScriptableObject 上，
                //   改了不撤会影响原版的敌方锈行猎手 —— 那是红线。所以走和风格替换同一套纪律：
                //   Prefix 插一条、原版查完表、Postfix/Finalizer 立刻移除。
                //   Unity 是单线程，OnStart 期间不会有别的单位并发读这张表。
                // ═══ 路线 B（借 ForceMove 片段当位移施法动画）已于 1.7.53 撤回 ═══
                //
                // 做法：请求 Fly 时往共享的 Animations 表里临时插一条
                //       { Style=Fly, Default.CastClip = 单位自己的 Sicarian_Forcemove_* }，
                //       原版查完表后由 Finalizer 立刻移除。
                //
                // 实测结果（1.7.52，作者原话「死从天降放了卡了 而且好像还是摆大字平移过去的」）：
                //     死从天降 CastSpell      播放 748 ms   结束时技能=仍在跑
                //     ★指令卡顿★ 锈行猎手     已跑 5.6 秒未结束      ← 新增的卡死
                //     死从天降 BuffLoopAction 播放 ★27251 ms★        ← 循环异常延长（平时 200~2400ms）
                //   引擎日志无异常，所以不是 SingleOrDefault 抛错，是别的东西被搅乱了。
                //
                // ⇒ **位移观感没修好，却引入了卡死**。锈行猎手的攻击资源是 Interrupted，
                //   本来不该饿死，这是纯新增的回归。卡死比大字严重得多，收益为负。
                //
                // ★教训★ 往引擎的共享数据结构里插自造对象，风险我低估了：
                //   1.7.50 插了个残缺对象（CastSpeedup=0 冻住、Overrides=null NRE），
                //   1.7.52 把字段补齐后仍然出问题。这类改动即使做到「用完立刻撤」，
                //   也无法保证原版在这一次调用里没有别的地方缓存/引用了它。
                //   要真做，得先搞清楚 OnStart 之后还有谁会再读这条 entry —— 我没查清就上了。
                //
                // 现状：位移技能退回「换成 Grenade（扔手雷手势）」，观感不贴切但稳定。
                // 素材是有的（Sicarian_Forcemove_v1~v5），将来若要重试，
                // 正确方向应该是找到引擎自己播 ForceMove 的入口去复用，而不是伪造 CastSpell 条目。

                // ★1.7.70★ Fly 是**带位移**的施法风格。换成静止的施法手势（电僧只有 Directional、
                //   锈行猎手只有 Grenade）会让人在滑行而动作是原地施法 —— 作者原话
                //   「平移过去了很远的距离…怎么也没播出移动的动画」。
                //   两个单位其实**都有**位移片段（ElectroPriest_ForceMove_01~05 /
                //   Sicarian_Forcemove_v1~v5），只是它们挂在 ForceMove(26) 动作下、不在施法表里，
                //   所以永远轮不到。
                //   这里给句柄打个标记，由 StartClip 那一步把**片段**换成 ForceMove ——
                //   ★不再往共享的施法表里插伪造条目★（1.7.50 那么干过，在管线上开了洞导致卡死）。
                if (IsDisplacementStyle(cur)) ForeignRigClipBlockPatch.MarkDisplacement(handle);

                AnimFallback.NoteCastSwap(cur, cand);
                __state = cur;                       // ★记下原值，Postfix 立刻还原★
                handle.CastStyle = (Kingmaker.Visual.Animation.Kingmaker.Actions.UnitAnimationActionCastSpell.CastAnimationStyle)cand;
            }
            catch { }
        }

        /// <summary>
        /// ★用 Finalizer 不用 Postfix★ 原方法抛异常时 Postfix 不执行，
        /// 而我们往**共享的** Animations 表里插了一条 —— 漏撤一次就会污染原版单位。
        /// Finalizer 是 try/finally 语义，抛不抛都跑。
        /// </summary>
        private static Exception Finalizer(UnitAnimationActionHandle handle, object __state, Exception __exception)
        {
            try
            {
                var ins = __state as InsertedEntry;
                if (ins != null)
                {
                    if (ins.List != null && ins.Entry != null) ins.List.Remove(ins.Entry);   // ★立刻撤★
                }
                else if (__state != null && handle != null)
                {
                    handle.CastStyle = (Kingmaker.Visual.Animation.Kingmaker.Actions.UnitAnimationActionCastSpell.CastAnimationStyle)__state;
                }
            }
            catch { }
            return __exception;                      // 原样抛回去，不改变异常传播
        }


        private static void TrySetFloat(Type t, object obj, string field, float v)
        {
            try
            {
                var f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(float)) f.SetValue(obj, v);
            }
            catch { }
        }

        /// <summary>给 List&lt;T&gt; 字段塞一个空列表 —— 原版会对它调 LINQ，null 会直接 NRE。</summary>
        private static void TrySetEmptyList(Type t, object obj, string field)
        {
            try
            {
                var f = t.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null || f.GetValue(obj) != null) return;
                f.SetValue(obj, Activator.CreateInstance(f.FieldType));
            }
            catch { }
        }

        /// <summary>标记：我们往共享表里插了一条，收尾时必须移除。</summary>
        private sealed class InsertedEntry
        {
            internal IList List;
            internal object Entry;
        }

        /// <summary>这个施法风格是不是「带位移」的那类。目前只认 Fly。</summary>
        private static bool IsDisplacementStyle(object style)
        {
            return style != null && style.ToString() == "Fly";
        }

        private static FieldInfo _entStyleF, _entDefF, _entCastClipF;
        private static Type _styleEntryT, _animEntryT;

        /// <summary>
        /// 造一条 { Style = 请求的风格, Default.CastClip = 单位自己的 ForceMove 片段 }。
        /// 拿不到素材就返回 null，交回原来的「换风格」路径。
        /// </summary>
        private static object TryMakeForceMoveEntry(UnitAnimationActionHandle handle, object style, IList list)
        {
            try
            {
                var mgr = handle != null ? handle.Manager : null;
                if (mgr == null) return null;

                // ★素材来源：单位自己动画集里的 ForceMove(26) 动作★ 不加载任何外部资源
                var fm = mgr.GetAction(Kingmaker.Visual.Animation.Kingmaker.UnitAnimationType.ForceMove);
                if (fm == null) return null;

                AnimationClipWrapper wrap = null;
                foreach (var w in fm.ClipWrappers)
                {
                    if (AnimFallback.HasClip(w)) { wrap = w; break; }
                }
                if (wrap == null) return null;

                if (_styleEntryT == null)
                {
                    // 从表里现有元素反推嵌套类型，比写死全名稳
                    object sample = null;
                    foreach (var e in list) { if (e != null) { sample = e; break; } }
                    if (sample == null) return null;
                    _styleEntryT = sample.GetType();
                    _entStyleF = _styleEntryT.GetField("Style", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _entDefF   = _styleEntryT.GetField("Default", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_entDefF != null)
                    {
                        _animEntryT = _entDefF.FieldType;
                        _entCastClipF = _animEntryT.GetField("CastClip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }
                if (_styleEntryT == null || _entStyleF == null || _entDefF == null
                    || _animEntryT == null || _entCastClipF == null) return null;

                var def = Activator.CreateInstance(_animEntryT);
                _entCastClipF.SetValue(def, wrap);

                // ★1.7.52 修：Activator.CreateInstance 只给默认值，缺一个就白插★
                //   实测 1.7.50「借用位移动画 11 次」但画面仍是大字 —— 插进去了却没播出来。
                //   两处默认值各自足以致命：
                //   ① CastSpeedup 默认 0 ⇒ 原版会拿它去 SetSpeed，零速 = 片段冻在第一帧，
                //      肉眼就是绑定姿势。必须给 1。
                //   ② Overrides 默认 null ⇒ 原版 OnStart 紧接着做
                //      entry.Overrides.SingleOrDefault(按 AttackWeaponStyle) —— 对 null 直接 NRE，
                //      异常被吞掉，动画根本没起来。必须给一个空列表。
                //   ★教训★ 往引擎的数据结构里塞自造对象时，要把**原版会读到的每个字段**
                //   都按合法默认值填满，不能只填自己关心的那一个。
                TrySetFloat(_animEntryT, def, "CastSpeedup", 1f);
                TrySetFloat(_animEntryT, def, "BlendToCastTime", 0.1f);
                TrySetFloat(_animEntryT, def, "BlendToLoopedTime", 0.1f);

                var ent = Activator.CreateInstance(_styleEntryT);
                _entStyleF.SetValue(ent, style);
                _entDefF.SetValue(ent, def);
                TrySetEmptyList(_styleEntryT, ent, "Overrides");
                return ent;
            }
            catch { return null; }
        }
    }
}
