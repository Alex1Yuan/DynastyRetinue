using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;

namespace DynastyRetinue
{
    /// <summary>
    /// ================= 武器判定放行 =================
    ///
    /// ★要解决什么★
    ///   原版把一批好效果**焊死在特定武器上**：
    ///     · 无视护甲      → DoombringerSword_Feature 里 CheckAbilityWeaponBlueprintGetter
    ///                       写死「必须手持命运使者」
    ///     · 防御反击      → AutoStriking_PowerSword_Feature 里 ContextConditionItemInSlot
    ///                       写死「必须手持自主攻击能量剑」
    ///     · 需要长剑的技能 → CheckAbilityWeaponFamilyGetter 要 WeaponFamily.Blade，
    ///                       而静电臂铠和跨音速利刃都是 Primitive
    ///
    ///   而我们的两个近战精英**必须用自带武器** —— 换成那些剑之后模型和动作对不上
    ///   （1.5.71/1.5.72 已经栽过两次：锈行者双持姿势崩、电僧的动作集是照拳套做的）。
    ///   于是「动作正确」和「效果生效」二选一。这个补丁是第三条路：
    ///   **武器不动，只让判定对这两个精英放行。**
    ///
    /// ★为什么不改蓝图★
    ///   Family / m_Weapon 都在**蓝图**上，改了全服生效 —— 敌方电僧、路上捡到的同名武器
    ///   全都跟着变。作者明确否掉了这条（"会影响原版单位的效果吧"）。
    ///   而按物品实例加附魔（ItemEntity.AddEnchantment）改不了 Family，也解不开这两个条件。
    ///
    /// ★为什么不新建武器★
    ///   新武器 = 新 AssetId，装备会写进 party.json，卸载 mod 之后存档打不开。
    ///   那是项目红线（见 rt-save-corruption-redline），#43 存档修复工具是它的前提。
    ///
    /// ================= 范围锁死（三道闸，缺一不可）=================
    ///   ① Main.Enabled  —— 关掉 mod 立刻不生效。UMM 的 OnToggle(false) **不会** unpatch，
    ///                      所以每个补丁都得自己判，不能指望"没打补丁"。
    ///   ② IsGateTarget  —— 先 IsGuard（O(1)，只读一次 CombatGroup.Id 做前缀比较），
    ///                      再比对单位蓝图，**只有那两个近战精英过**。
    ///                      ★为什么不能只用 IsGuard★ 长剑判定不绑任何 Feature，
    ///                      只用 IsGuard 的话六条线的卫兵都会凭空满足刀刃类判定。
    ///                      ★绝不能用 RetinueRegistry.All()★ 那是全区域实体拷贝。
    ///   ③ 白名单        —— 只放行下面这三件具体的事。别的武器判定一律原样交回原版，
    ///                      不然卫兵会凭空满足**所有**武器条件，那是灾难。
    ///
    /// ★调用频率★
    ///   三处都是**事件驱动**，不是每帧：CheckCondition 每次挨打算一次，
    ///   两个 GetBaseValue 在攻击/技能判定时算。技能"能不能放"的 UI 刷新会稍频繁些，
    ///   但闸①②挡在最前面，非卫兵路径上没有任何分配。
    ///
    /// ★诊断★
    ///   不打逐条日志，只计数，战斗结束时随 CombatWatch 的总账输出一块（约 10 行）。
    ///
    /// ★出错策略★
    ///   一律 catch 后 return true（交回原版）。补丁自己崩了最多是效果没生效，
    ///   绝不能把原版的战斗判定带崩。
    /// </summary>
    internal static class WeaponGate
    {
        /// <summary>命运使者 —— 它的 Feature 挂着 ArmorIgnoreOnTargetInitiator（无视护甲）。</summary>
        internal const string DoombringerItem = "d4ec63f97e8d43329bf432ba76b9e9e3";

        /// <summary>自主攻击能量剑 —— 它的 Feature 挂着「防御触发 → 自动用武器还击」。</summary>
        internal const string AutoStrikeItem = "8c304b20633145f39577a392201d2047";

        /// <summary>
        /// ★放行名单：只有这两个近战精英★
        ///
        /// 为什么不能只用 IsGuard：
        ///   ① 无视护甲 和 ③ 防御反击 是**跟着 Feature 走**的 —— 别的卫兵没挂那两个
        ///      Feature，判定压根不会走到，所以 IsGuard 就够。
        ///   ② 长剑判定**不绑任何 Feature** —— CheckAbilityWeaponFamilyGetter 是通用的
        ///      武器族判定，任何要求刀刃类的技能都会走它。只用 IsGuard 的话，
        ///      **六条线的所有卫兵**都会凭空满足"我拿的是刀刃武器"。
        ///      其它五条线的近战本来就拿刀刃，实际影响小，但那是碰巧不是设计。
        ///
        /// 所以三处统一按单位蓝图收窄。判据用 unit guid —— 这和 EliteDef.UnitId
        /// 「兼作是不是这个精英的持久判据」是同一套办法，过图/读档都稳。
        ///
        /// 对应 archetypes.json 里机械教分型的 elites[0] / elites[1]。
        /// ★改配置时这里要跟着改★ 换了精英的 unit 就得同步，否则效果静默失效。
        /// </summary>
        private static readonly string[] MeleeEliteUnits =
        {
            "aa02b505be774674ae924f19dc17e6f6",   // DLC3_OP_Dogmatic_SicarianRuststalker_Unit 锈行猎手
            "ab131771270542b69fb7a687062b39c0",   // DLC3_OP_Xenarite_ElectroPriest_Unit       电僧
        };

        /// <summary>
        /// ★收割者技能专用的更窄名单：只有锈行猎手★
        ///
        /// 1.7.18 实测：三个开关全开之后
        ///   · 锈行猎手 —— **放出了死从天降**，动作正常
        ///   · 电僧     —— T-pose，而且卡住
        /// 这和 1.5.72 那条注释完全吻合：「电僧的动作集是照**拳套**做的（挥拳/近身放电），
        /// 给它剑连挥砍动作都没有」。锈行猎手自带跨音速利刃（刀刃类），动作对得上。
        ///
        /// ★1.7.21 又放开了★ 上面那个「只有锈行猎手接得住」的判断**站不住**：
        /// 作者随后指出锈行猎手同样 T-pose，我却因为它「放了 18 次、做了 70 个动作」
        /// 就默认它动作正常 —— 把「能动」当成了「动作对」的证据。
        /// 既然两边都 T-pose，收窄就只剩一个作用（避免电僧卡死），
        /// 代价却是**看不到电僧那边的表现**，而两边缺的片段未必是同一类。
        /// 作者要两边的数据，所以放开。等 #46 的片段替换做出来，这个名单整个就该删掉。
        /// ★代价★ 电僧可能再次卡住（上次 3 回合只做了 5 个动作）。这是知情的取舍。
        /// </summary>
        private static readonly string[] BladeAnimUnits =
        {
            "aa02b505be774674ae924f19dc17e6f6",   // 锈行猎手
            "ab131771270542b69fb7a687062b39c0",   // 电僧
        };

        /// <summary>三道闸的前两道。补丁入口统一走这里，别各写各的。</summary>
        internal static bool IsGateTarget(object entity) { return InList(entity, MeleeEliteUnits); }

        /// <summary>
        /// 收割者技能专用的闸 —— 比 IsGateTarget 更窄，只有动画接得住的那个单位。
        /// 用在：④长剑分类 / ⑦双持长剑 / 死从天降的 AoE 修复。
        /// </summary>
        internal static bool IsBladeAnimTarget(object entity) { return InList(entity, BladeAnimUnits); }

        private static bool InList(object entity, string[] units)
        {
            if (!Main.Enabled) return false;
            var u = entity as BaseUnitEntity;
            if (u == null) return false;
            // ★顺序★ IsGuard 是 O(1)（只读一次 CombatGroup.Id 做前缀比较），
            //   放在 guid 比对之前，非卫兵一次字符串比较就走掉。
            if (!RetinueRegistry.IsGuard(u)) return false;
            try
            {
                if (u.Blueprint == null) return false;
                string g = u.Blueprint.AssetGuid.ToString();
                for (int i = 0; i < units.Length; i++)
                    if (string.Equals(g, units[i], StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch { }
            return false;
        }

        // ================= 反射缓存 =================
        // CurrentEntity 和 Weapon 在原版里都是 protected/internal，编译期够不着，
        // 只能反射。PropertyInfo 按类型缓存一次，之后每次调用只是一次 GetValue ——
        // 这些都是事件驱动路径（每次攻击/挨打），不是每帧，这个开销可以接受。
        // ★但顺序有讲究★ 见各补丁里的注释：先做不要反射的判断。
        // ★缓存键必须带属性名★ 1.5.74 我只用 Type 做键，而同一个
        //   CheckAbilityWeaponBlueprintGetter 上要查两个属性（CurrentEntity 和 Weapon）：
        //   先查的把后查的挤掉，第二次命中缓存拿回错的 PropertyInfo，
        //   强转失败返回 null ⇒ 无视护甲那道闸**永远走不通**，而且静默。
        //   1.5.79 实测「本场一次都没放行」就是它。
        private static readonly System.Collections.Generic.Dictionary<string, PropertyInfo> _propCache
            = new System.Collections.Generic.Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        private static PropertyInfo Prop(Type t, string name)
        {
            PropertyInfo pi;
            string key = t.FullName + "|" + name;
            if (_propCache.TryGetValue(key, out pi)) return pi;
            pi = null;
            for (var cur = t; cur != null && pi == null; cur = cur.BaseType)
                pi = cur.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            _propCache[key] = pi;
            return pi;
        }

        /// <summary>取 PropertyGetter 的 CurrentEntity（protected）。取不到返回 null。</summary>
        internal static object CurrentEntityOf(object getter)
        {
            if (getter == null) return null;
            try
            {
                var pi = Prop(getter.GetType(), "CurrentEntity");
                return pi != null ? pi.GetValue(getter, null) : null;
            }
            catch { return null; }
        }

        /// <summary>取 CheckAbilityWeaponBlueprintGetter 的 Weapon 的 guid。取不到返回 null。</summary>
        internal static string WeaponGuidOf(object getter)
        {
            if (getter == null) return null;
            try
            {
                var pi = Prop(getter.GetType(), "Weapon");
                var w = pi != null ? pi.GetValue(getter, null) as Kingmaker.Blueprints.SimpleBlueprint : null;
                return w != null ? w.AssetGuid.ToString() : null;
            }
            catch { return null; }
        }

        // ================= 诊断计数 =================
        // ★为什么只计数不逐条打日志★
        //   这三处是攻击/挨打级别的调用，一场战斗几百次。逐条打会把日志淹掉，
        //   而且我们要回答的问题是"到底触没触发"，那是个计数问题不是流水账问题。
        //   所以照 CombatWatch 总账的做法：开打清零，结束时汇总成一块。
        //
        // ★这个计数能证明什么、不能证明什么★
        //   能：判定被放行了几次 —— 若全是 0，说明特性压根没挂上或条件没走到，
        //       那是"完全没生效"，方向就错了。
        //   不能：放行 ≠ 效果一定打出来了。无视护甲要看伤害、防反要看多出来的那一刀。
        //       计数只是第一道信号，非零之后还得看实战表现。
        private static readonly System.Collections.Generic.Dictionary<string, int[]> _hits
            = new System.Collections.Generic.Dictionary<string, int[]>(StringComparer.Ordinal);

        internal const int KindArmorIgnore = 0;
        internal const int KindBlade = 1;
        internal const int KindCounter = 2;
        /// <summary>④ 武器分类=Sword 的施法限制。★这道才是真正拦技能的★，见 WeaponClassGatePatch。</summary>
        internal const int KindSword = 3;
        /// <summary>⑤ 跳过 WarhammerEndTurn —— 森罗刃网/利刃之舞放完不再结束回合。</summary>
        internal const int KindEndTurn = 4;
        /// <summary>⑦ 双持长剑判定（利刃之舞要求两把 Sword）。1.7.17 新增。</summary>
        internal const int KindTwoSwords = 5;
        private const int KindCount = 6;

        /// <summary>
        /// 「这道判定被求值过几次」—— 与 _hits（放行几次）配对。
        ///
        /// ★为什么要两个数★ 只记放行数分不清两种失败：
        ///   · 求值 0 次 → 原版**压根没走到这道判定**（特性没挂对、或条件要别的触发时机）
        ///   · 求值 N 次、放行 0 次 → 走到了但**被我们自己的闸拒了**（guid 对不上之类）
        ///   1.5.79 三道全是 0，靠单一计数无法定位，白测一轮。
        /// 只统计"我们关心的那种"求值（白名单命中的武器 / 要求含 Blade 的族判定），
        /// 不是所有调用 —— 否则数字会大到没意义。
        /// </summary>
        private static readonly int[] _seen = new int[KindCount];

        internal static void Seen(int kind)
        {
            try { _seen[kind]++; } catch { }
        }

        internal static void Count(object entity, int kind)
        {
            try
            {
                var u = entity as BaseUnitEntity;
                if (u == null) return;
                string name = null;
                try { name = u.CharacterName; } catch { }
                if (string.IsNullOrEmpty(name)) name = "?";
                int[] a;
                if (!_hits.TryGetValue(name, out a)) { a = new int[KindCount]; _hits[name] = a; }
                a[kind]++;
            }
            catch { }
        }

        /// <summary>开打时清零 —— 由 CombatWatch.Tick 调。</summary>
        internal static void ResetHits()
        {
            try { _hits.Clear(); for (int i = 0; i < KindCount; i++) _seen[i] = 0; } catch { }
        }

        /// <summary>战斗结束时汇总 —— 由 CombatWatch.Dump 调，接在总账后面。</summary>
        internal static void ReportHits(System.Text.StringBuilder sb)
        {
            try
            {
                if (sb == null) return;
                sb.AppendLine("  ── 武器判定放行次数（1.5.74 新增）──");
                sb.AppendLine(string.Format("      判定被求值：无视护甲 {0} · 武器族 {1} · 防御反击 {2} · 长剑 {3} · 免结束回合 {4} · 双持长剑 {5}",
                              _seen[KindArmorIgnore], _seen[KindBlade], _seen[KindCounter], _seen[KindSword], _seen[KindEndTurn], _seen[KindTwoSwords]));
                sb.AppendLine("      ★求值 0 = 原版没走到这道判定（不是我们拒的）；"
                            + "求值 >0 而放行 0 = 走到了但被我们的闸拒了，查单位 guid。★");
                if (_hits.Count == 0)
                {
                    sb.AppendLine("      本场一次都没放行。");
                    return;
                }
                sb.AppendLine("      卫兵                          无视护甲  武器族  防御反击  长剑  免结束回合  双持长剑");
                foreach (var kv in _hits)
                    sb.AppendLine(string.Format("      {0,-28} {1,8} {2,7} {3,9} {4,6} {5,10} {6,9}",
                        kv.Key.Length > 28 ? kv.Key.Substring(0, 28) : kv.Key,
                        kv.Value[KindArmorIgnore], kv.Value[KindBlade], kv.Value[KindCounter], kv.Value[KindSword], kv.Value[KindEndTurn], kv.Value[KindTwoSwords]));
                sb.AppendLine("      ★放行 ≠ 效果打出来了★ 这只证明条件判定通过。"
                            + "无视护甲要看伤害有没有变、防反要看有没有多出来的那一刀。");
                ReportDeathWaltzCharge(sb);
                ReportAnimStyles(sb);
                try
                {
                    // ★无条件打印★ 就算全是 0 也要打 —— 「全 0」本身才是信息量最大的情况，
                    // 1.7.33 之前用「任一 >0」做条件，结果最该看的那种情形反而整块不显示。
                    sb.AppendLine("  ── 动画兜底（#46）──");
                    sb.AppendLine("      替换武器风格 " + AnimFallback.StyleSwapped
                                + " 次　拦下 LocoMotion 空片段 " + AnimFallback.LocoGuarded + " 次"
                                + "　补跳跃片段 " + AnimFallback.JumpFixed + " 次"
                                + "　解除动画死锁 " + AnimFallback.DeadlockBroken + " 次");
                    sb.AppendLine("      ★★解除排队饿死 " + AnimFallback.QueueUnblocked + " 次"
                                + "　保险丝 " + AnimFallback.QueueFused + " 次★★"
                                + "　★循环保护 " + AnimFallback.LoopProtected + " 次★"
                                + "　—— 这是电僧「攻击放不出来 + 卡到超时」的根因修复。"
                                + "电僧的攻击动画资源是 ExecutionMode=Sequenced（排队型），"
                                + "被森罗刃网的循环动画占着 m_CurrentAction 就永远出不了队；"
                                + "锈行猎手的同类资源是 Interrupted（打断型），所以它一直没事。");
                    sb.AppendLine("      ★补副手动作 " + AnimFallback.OffHandFallback + " 次"
                                + "　替换施法风格 " + AnimFallback.CastSwapped + " 次★"
                                + "　★★拦下异骨架片段 " + AnimFallback.ForeignClipBlocked + " 次★★"
                                + "　—— 前者治利刃之舞「一点动作都没有」（引擎实测：两个单位的动画集里"
                                + "都没有 OffHandAttack）；后者治辅助技能「没有手指他人那个动作」"
                                + "（原版会 set_IsSkipped 静默跳过）。");
                    sb.AppendLine("      ★掩体抛异常 " + AnimFallback.CoverThrew + " 次"
                                + "　解除攻击屏蔽 自己 " + AnimFallback.AttackUnblocked
                                + " / 父视图 " + AnimFallback.AttackUnblockedParent + " 次★");
                    sb.AppendLine("      ★怎么读★ 1.7.34 实测这三个全是 0 —— 说明"
                                + "「BlockAttackAnimation 卡住」那套根因判断**是错的**，标志从没卡过。"
                                + "留着这三个数是为了确认它以后也不会卡。真正的病在下面那张表。");
                    sb.AppendLine("  ── 各补丁进入/过闸/查表（1.7.34 新增）──");
                    sb.AppendLine(AnimFallback.TA.Line());
                    sb.AppendLine(AnimFallback.TC.Line());
                    sb.AppendLine(AnimFallback.TE.Line());
                    sb.AppendLine(AnimFallback.TF.Line());
                    sb.AppendLine(AnimFallback.TL.Line());
                    sb.AppendLine(AnimFallback.TJ.Line());
                    sb.AppendLine("      ★CastSpell 那行是**死代码**★ 补丁 E 挂的是 Warhammer 版，"
                                + "实测三场全 0；真正跑施法的是「CastSpell真」那行。");
                    sb.AppendLine("      ★怎么读★ 进入=0 ⇒ 这条动画链路**真的没被走过**，该查动作路由"
                                + "（比如 AnimationSet.GetAction 返回 null，攻击照常结算但完全没动画）；"
                                + "进入>0 而过闸=0 ⇒ 闸把它挡了，查单位名单；"
                                + "过闸>0 而查表=0 ⇒ 进去了但在查表前就 return 了（多半是那张表是空的）。"
                                + "★以前把「计数为 0」当成「补丁没挂上」，据此反复改挂点，全是白费 ——"
                                + "挂点已用 IL 验过是对的。★"
                                + "　注意 Jump 是 Postfix，原版正常拿到片段就静默退出，所以它的替换数为 0 是正常的。");
                    AnimFallback.StyleSwapped = 0; AnimFallback.LocoGuarded = 0; AnimFallback.JumpFixed = 0;
                    AnimFallback.DeadlockBroken = 0; AnimFallback.AttackUnblocked = 0;
                    AnimFallback.AttackUnblockedParent = 0; AnimFallback.CoverThrew = 0;
                    AnimFallback.QueueUnblocked = 0; AnimFallback.QueueFused = 0;
                    AnimFallback.OffHandFallback = 0; AnimFallback.CastSwapped = 0; AnimFallback.LoopProtected = 0;
                    AnimFallback.ForeignClipBlocked = 0;
                    AnimFallback.StyleRepaired = 0;
                    AnimFallback.TA.Reset(); AnimFallback.TC.Reset(); AnimFallback.TE.Reset();
                    AnimFallback.TF.Reset(); AnimFallback.TL.Reset(); AnimFallback.TJ.Reset();
                }
                catch { }
                try
                {
                    sb.AppendLine("      死从天降·AoE 修复：本场补 pattern " + DeathWaltzAoePatch.Applied + " 次");
                    sb.AppendLine("      ★怎么读★ 0 次 = 补丁没作用到（开关没开 / 闸挡了 / 蓝图没对上），"
                                + "查作用域；>0 次而施放仍为 0 = 补上了但 AI 的 AOE 选择器仍选不出目标，查选择器。"
                                + "这两种的修法相反。");
                    DeathWaltzAoePatch.Applied = 0;
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// 死从天降到底是「放不出来」还是「AI 不选」—— 一行日志定死。
        ///
        /// ★为什么值得单独打★ 实测「长剑」闸放行了 214/195 次、施放 0 次。
        ///   按补丁代码只有死从天降那一个实例会走到放行分支，所以那几百次全是它、
        ///   而且每次都答「有剑」。既然闸开了还不放，只剩两种可能：
        ///     A 缺充能 —— ReaperDeathWaltzFeature 的 CombatStateTrigger 会在开战时
        ///                 ApplyBuff(ReaperDeathWaltzBuffCharge)。这个 Feature 靠职业路径
        ///                 rank 15 自动给，而「是不是真给了」离线看不出来（plans.json 没选它，
        ///                 preGrant 没有它，单位蓝图 AddFacts 也没有）。
        ///     E AI 没选 —— 那就是权重问题，跟闸无关。
        ///   这两者的修法完全相反，猜错一次就是一整轮实机测试白费。
        ///
        /// ★只对持有该技能的卫兵打★ 没有这个技能的单位打出来是噪音。
        /// ★频率★ 每场战斗结束一次，最多几行。
        /// </summary>
        private static void ReportDeathWaltzCharge(System.Text.StringBuilder sb)
        {
            const string AbilityId = "6f1b7cfb48a0450cb85ce8a8879502de";  // ReaperDeathWaltzAbility
            const string FeatureId = "c3d6e29e74e440a79cb243e4e93e0501";  // ReaperDeathWaltzFeature（发充能的那个）
            const string ChargeId  = "3310b2e194e7456b88664e249e26cac6";  // ReaperDeathWaltzBuffCharge
            try
            {
                var list = RetinueRegistry.All();
                if (list == null) return;
                bool header = false;
                foreach (var g in list)
                {
                    if (g == null) continue;
                    bool hasAb = false, hasFeat = false, hasCharge = false;
                    try
                    {
                        foreach (var a in g.Abilities)
                            if (a != null && a.Blueprint != null
                             && string.Equals(a.Blueprint.AssetGuid.ToString(), AbilityId, StringComparison.OrdinalIgnoreCase))
                            { hasAb = true; break; }
                    }
                    catch { }
                    if (!hasAb) continue;
                    try
                    {
                        foreach (var f in g.Facts.List)
                        {
                            if (f == null || f.Blueprint == null) continue;
                            string id = f.Blueprint.AssetGuid.ToString();
                            if (string.Equals(id, FeatureId, StringComparison.OrdinalIgnoreCase)) hasFeat = true;
                            else if (string.Equals(id, ChargeId, StringComparison.OrdinalIgnoreCase)) hasCharge = true;
                        }
                    }
                    catch { }
                    if (!header)
                    {
                        sb.AppendLine("  ── 死从天降·充能诊断 ──");
                        header = true;
                    }
                    sb.AppendLine("      " + (g.CharacterName ?? "?")
                        + "　持有技能=是　充能特性(ReaperDeathWaltzFeature)=" + (hasFeat ? "有" : "★无★")
                        + "　充能buff(BuffCharge)=" + (hasCharge ? "有" : "★无★"));
                }
                if (header)
                    sb.AppendLine("      ★怎么读★ 两个都「有」而施放仍为 0 ⇒ 不是放不出来，是 AI 没选它，"
                                + "该往权重/脑的优先级方向查；任一为「无」⇒ 是真缺前置，"
                                + "补 preGrant 即可，和长剑闸无关。"
                                + "（战斗结束时 buff 可能已消耗，所以特性那一栏比 buff 那栏更可信）");
            }
            catch { }
        }

        /// <summary>
        /// 每个近战精英当前的**武器动画风格** —— T-pose 的第一手线索。
        ///
        /// ★为什么是这个值★ UnitAnimationManager 上有
        ///     ActiveMainHandWeaponStyle / ActiveOffHandWeaponStyle : WeaponAnimationStyle
        /// 动画片段极可能就是按这个风格查的。枚举里有
        ///     Knife=2 / Fencing=3 / Fist=17 / Mechadendrites=18 / Staff=19 …
        /// 静电臂铠大概率是 Fist，跨音速利刃大概率是 Knife 或 Fencing。
        /// 收割者那套技能的片段只存在于某几种风格下 —— 风格对不上就查不到片段，
        /// 回退绑定姿势 = T-pose。
        ///
        /// ★为什么先打这个而不是直接改★ 作者要求「查清楚再给结论」。
        ///   这一行是**可复核的事实**：两个单位的风格值到底是什么、是不是真的不同。
        ///   知道「现在是什么」才谈得上「该换成什么」；否则又是凭猜测改代码。
        ///
        /// ★全反射★ 这条链路的类型编译期一个都没引用，取不到就整段跳过 ——
        ///   绝不能因为一行诊断把战报带崩。
        /// ★频率★ 每场战斗结束一次，最多两三行。
        /// </summary>
        private static void ReportAnimStyles(System.Text.StringBuilder sb)
        {
            try
            {
                var list = RetinueRegistry.All();
                if (list == null) return;
                bool header = false;
                foreach (var g in list)
                {
                    if (g == null || !IsGateTarget(g)) continue;   // 只打两个近战精英
                    object mgr = null;
                    try
                    {
                        var pv = Prop(g.GetType(), "View");
                        object view = pv != null ? pv.GetValue(g, null) : null;
                        if (view != null)
                        {
                            var pm = Prop(view.GetType(), "AnimationManager");
                            if (pm != null) mgr = pm.GetValue(view, null);
                        }
                    }
                    catch { }
                    if (mgr == null) continue;

                    string main = StyleOf(mgr, "ActiveMainHandWeaponStyle");
                    string off  = StyleOf(mgr, "ActiveOffHandWeaponStyle");
                    if (main == null && off == null) continue;

                    if (!header)
                    {
                        sb.AppendLine("  ── 武器动画风格（T-pose 排查用）──");
                        header = true;
                    }
                    sb.AppendLine("      " + (g.CharacterName ?? "?")
                                + "　主手=" + (main ?? "?") + "　副手=" + (off ?? "?"));
                }
                if (header)
                    sb.AppendLine("      ★怎么读★ 动画片段按这个风格查。两个精英的风格若不同，"
                                + "而某个技能只在其中一种风格下有片段，另一个就会回退绑定姿势(T-pose)。"
                                + "这一行只给「现在是什么」；「该换成什么」还要看哪种风格下真有片段。");
            }
            catch { }
        }

        private static string StyleOf(object mgr, string prop)
        {
            try
            {
                var pi = Prop(mgr.GetType(), prop);
                if (pi == null) return null;
                var v = pi.GetValue(mgr, null);
                return v == null ? null : (v.ToString() + "(" + Convert.ToInt32(v) + ")");
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// ① 无视护甲：让卫兵在「你手上是不是命运使者」这一问上答"是"。
    ///
    /// ★为什么必须比对 m_Weapon★
    ///   这个 getter 全游戏很多武器特效都在用。不比对具体武器就无脑放行的话，
    ///   卫兵会同时满足所有「手持某某武器」的判定 —— 等于白拿一堆不该有的特效。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.EntitySystem.Properties.Getters.CheckAbilityWeaponBlueprintGetter),
                  "GetBaseValue")]
    internal static class WeaponBlueprintGatePatch
    {
        private static bool Prefix(
            Kingmaker.EntitySystem.Properties.Getters.CheckAbilityWeaponBlueprintGetter __instance,
            ref int __result)
        {
            try
            {
                if (!Main.Enabled) return true;
                if (__instance == null) return true;

                // ★顺序★ 先取武器再 IsGateTarget —— 这样"求值计数"能记到
                //   「原版确实在问命运使者」这件事，与"我们放没放行"分开。
                var guid = WeaponGate.WeaponGuidOf(__instance);
                if (guid == null) return true;
                // 白名单：只认命运使者这一件
                if (!string.Equals(guid, WeaponGate.DoombringerItem,
                                   StringComparison.OrdinalIgnoreCase)) return true;
                WeaponGate.Seen(WeaponGate.KindArmorIgnore);

                var who = WeaponGate.CurrentEntityOf(__instance);
                if (!WeaponGate.IsGateTarget(who)) return true;

                WeaponGate.Count(who, WeaponGate.KindArmorIgnore);
                __result = 1;      // 就当你手里拿的是命运使者
                return false;
            }
            catch { }
            return true;           // 出错交回原版
        }
    }

    /// <summary>
    /// ② 长剑判定：让卫兵在「你的武器是不是刀刃类」这一问上答"是"。
    ///
    /// ★只放行 Blade★
    ///   静电臂铠和跨音速利刃实测都是 WeaponFamily.Primitive（见 weapon_facts.txt），
    ///   所以要长剑的技能会拒。这里只在**要求里含 Blade** 时放行；
    ///   要求等离子/爆矢的判定照样走原版，卫兵不会凭空满足所有武器族。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.EntitySystem.Properties.Getters.CheckAbilityWeaponFamilyGetter),
                  "GetBaseValue")]
    internal static class WeaponFamilyGatePatch
    {
        private static bool Prefix(
            Kingmaker.EntitySystem.Properties.Getters.CheckAbilityWeaponFamilyGetter __instance,
            ref int __result)
        {
            try
            {
                if (!Main.Enabled) return true;
                if (__instance == null || __instance.Families == null) return true;

                // ★顺序★ Families 是 public 字段，判它不要反射，而且极有选择性 ——
                //   绝大多数武器族判定跟 Blade 无关，在这里就走掉了，一次反射都不用。
                bool wantsBlade = false;
                var fam = __instance.Families;
                for (int i = 0; i < fam.Length; i++)
                    if (fam[i] == Kingmaker.Enums.WeaponFamily.Blade) { wantsBlade = true; break; }
                if (!wantsBlade) return true;
                WeaponGate.Seen(WeaponGate.KindBlade);

                var who = WeaponGate.CurrentEntityOf(__instance);
                if (!WeaponGate.IsGateTarget(who)) return true;

                WeaponGate.Count(who, WeaponGate.KindBlade);
                __result = 1;
                return false;
            }
            catch { }
            return true;
        }
    }

    /// <summary>
    /// ⑦ 双持长剑判定：利刃之舞要求**两把** Sword 类武器。
    ///
    /// ★为什么补这个★ 1.7.16 我说「利刃之舞第三道闸只能改武器蓝图，会波及原版单位，不做」——
    ///   那是**过度悲观**。作者反问「我们之前不是有补丁可以绕过吗」，查完确实有：
    ///   CheckHasTwoWeaponsOfClassificationGetter 和闸② CheckAbilityWeaponFamilyGetter
    ///   是同一族 PropertyGetter，签名一模一样（Int32 GetBaseValue()），
    ///   而闸②实测求值 0 次、完全是死的 —— 现成的模式一直空在那儿。
    ///
    /// ★只放行 Sword★ 和闸④同理：别的分类照走原版，否则卫兵会凭空满足所有双持判定。
    /// ★挂在 ReaperSkillGate 下★ 收割者线的四道闸合成了一个开关 —— 作者反馈面板选项太多，
    /// 而且实测这四个从来都是一起开一起关，拆开只是增加负担。
    /// ★仍有第三道★ AbilityCustomBladeDance{UseSpecificWeaponClassification=1, Classification=Sword}
    ///   在**投放**阶段还会自己去找剑。那个类的方法是可以补的（Deliver / TriggerAttackRule
    ///   都在，不是非改蓝图不可），但 Deliver 是迭代器、补起来更绕，而且要先确定
    ///   到底哪一步在挑武器。先上这两道，看实测走到哪一步再决定要不要补第三道。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.EntitySystem.Properties.Getters.CheckHasTwoWeaponsOfClassificationGetter),
                  "GetBaseValue")]
    internal static class TwoSwordsGatePatch
    {
        private static bool Prefix(
            Kingmaker.EntitySystem.Properties.Getters.CheckHasTwoWeaponsOfClassificationGetter __instance,
            ref int __result)
        {
            try
            {
                if (!Main.Enabled) return true;
                if (__instance == null) return true;
                var s = Main.Settings;
                if (s == null || !s.ReaperSkillGate) return true;

                // 便宜的判断放前面：不是问 Sword 就走原版，一次反射都不用
                if (__instance.Classification != Kingmaker.Enums.WeaponClassification.Sword) return true;
                WeaponGate.Seen(WeaponGate.KindTwoSwords);

                var who = WeaponGate.CurrentEntityOf(__instance);
                if (!WeaponGate.IsBladeAnimTarget(who)) return true;

                WeaponGate.Count(who, WeaponGate.KindTwoSwords);
                __result = 1;      // 就当你双手都是长剑
                return false;
            }
            catch { }
            return true;
        }
    }

    /// <summary>
    /// ③ 防御反击：让卫兵在「自主攻击能量剑装在手上了吗」这一问上答"是"。
    ///
    /// ★取"谁"要跟原版一致★
    ///   原版按私有字段 IsCaster 决定看施法者还是目标：
    ///       IsCaster ? Context.MaybeOwner : Target.Entity
    ///   两边都查会出错 —— 敌人打我们的卫兵时，Target 是卫兵，
    ///   于是敌人的「我手上有某某剑吗」也会被判成真。所以必须照抄这个分支。
    ///
    /// ★Context/Target 怎么拿★
    ///   它们在 ContextCondition 上是 protected，但实现只是读一个静态的
    ///   ContextData&lt;MechanicsContext.Data&gt;.Current，我们直接读同一个静态即可，
    ///   不用对实例做反射。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Mechanics.Conditions.ContextConditionItemInSlot),
                  "CheckCondition")]
    internal static class ItemInSlotGatePatch
    {
        private static FieldInfo _isCaster;
        private static bool _isCasterLooked;

        private static bool Prefix(
            Kingmaker.UnitLogic.Mechanics.Conditions.ContextConditionItemInSlot __instance,
            ref bool __result)
        {
            try
            {
                if (!Main.Enabled) return true;
                if (__instance == null) return true;

                var item = __instance.Item;
                if (item == null) return true;
                // 白名单：只认自主攻击能量剑
                if (!string.Equals(item.AssetGuid.ToString(), WeaponGate.AutoStrikeItem,
                                   StringComparison.OrdinalIgnoreCase)) return true;
                WeaponGate.Seen(WeaponGate.KindCounter);

                if (!_isCasterLooked)
                {
                    _isCasterLooked = true;
                    _isCaster = AccessTools.Field(
                        typeof(Kingmaker.UnitLogic.Mechanics.Conditions.ContextConditionItemInSlot),
                        "IsCaster");
                }
                // 读不到就别猜 —— 交回原版，最多是效果没生效
                if (_isCaster == null) return true;
                bool isCaster = (bool)_isCaster.GetValue(__instance);

                var data = Kingmaker.ElementsSystem.ContextData
                           .ContextData<Kingmaker.UnitLogic.Mechanics.MechanicsContext.Data>.Current;
                if (data == null) return true;

                object who;
                if (isCaster)
                {
                    var ctx = data.Context;
                    who = ctx != null ? ctx.MaybeOwner : null;
                }
                else
                {
                    var tgt = data.CurrentTarget;
                    who = tgt != null ? tgt.Entity : null;
                }

                if (!WeaponGate.IsGateTarget(who)) return true;

                WeaponGate.Count(who, WeaponGate.KindCounter);
                __result = true;   // 就当剑在手上
                return false;
            }
            catch { }
            return true;
        }
    }

    /// <summary>
    /// ④ 长剑判定（真正管用的那个）：让卫兵通过「你手上有没有 Sword 类武器」的**施法限制**。
    ///
    /// ★这才是拦住技能的那道★
    ///   1.5.74 我补的是 CheckAbilityWeaponFamilyGetter（武器**族** Blade）——
    ///   1.5.80 实测求值 0 次；离线又查过：全库 41 个用它的蓝图全是武器专精和植入物，
    ///   我们这两个精英一个都没有。那是个**属性取值器**，用在 PropertyCalculator 里
    ///   算条件加成，根本不管"技能能不能放"。
    ///
    ///   真正决定能不能放的是 AbilityCasterHasWeaponOfClassification —— 它实现
    ///   IAbilityCasterRestriction，直接比 weapon.Blueprint.Classification == Classification。
    ///   WeaponClassification 枚举里有 **Sword**（刀锋舞者那类技能要的就是它），
    ///   而跨音速利刃和静电臂铠实测都是 **Classification=None** ⇒ 一律不通过。
    ///
    /// ★只放行 Sword★ 同样是白名单：要求斧/锤/狙击枪/盾的判定照走原版，
    ///   否则卫兵会凭空满足所有武器分类，能放一堆不该有的技能。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Abilities.Components.TargetCheckers.AbilityCasterHasWeaponOfClassification),
                  "IsCasterRestrictionPassed")]
    internal static class WeaponClassGatePatch
    {
        /// <summary>死从天降 ReaperDeathWaltzAbility —— ★注意中文名和内部名是错位的★
        /// 中文的「死亡华尔兹」对应的是 ReaperDesperate / ReaperUltimate 那两个。</summary>
        private const string DeathFromAbove = "6f1b7cfb48a0450cb85ce8a8879502de";

        /// <summary>
        /// 收割者终极 ReaperUltimateAbility。
        /// ★一个实例管两招★ 它和收割者绝境 ReaperDesperate_Ability(e90bd237…) 身上挂的
        ///   是**同一个** AbilityCasterHasWeaponOfClassification 组件实例(3b0892cc…)，
        ///   所以从任一个蓝图解析出来的对象是同一份，放行一次两招都通。
        /// </summary>
        private const string ReaperUltimate = "d52b8f3b44434f2798cd3a01c97fd1ed";

        /// <summary>
        /// 利刃之舞 / 森罗刃网。两者共用实例 8e7cfb5b…，森罗刃网另有一个 935ccda5…，
        /// 所以这里**把两个蓝图上的全部同类组件都收进来**（2~3 个），逐个比对。
        /// ★利刃之舞开了也未必能用★ 它另有两道我们碰不到的闸：
        ///   CheckHasTwoWeaponsOfClassificationGetter（要两把 Sword）
        ///   AbilityCustomBladeDance{Classification=Sword}（投放时自己再去找剑）
        /// 后者要改武器蓝图的 Classification，那会波及所有用这把武器的原版单位 —— 不做。
        /// 所以这一条实际主要是给**森罗刃网**开路。
        /// </summary>
        private static readonly string[] BladeDanceAbilities =
        {
            "e955823f54d24088ae1fdefe88d3684d",   // ReaperBladeDanceAbility  利刃之舞
            "8b7bcaa093224422ac66c80ffcf69f6d",   // ReaperBladeShroud_Ability 森罗刃网
        };

        private static object _deathWaltz, _reaperUlt;
        private static object[] _bladeDance = new object[0];
        private static bool _looked;

        /// <summary>
        /// 把技能蓝图上的武器分类限制组件各解析一次，缓存住 ——
        /// 之后每次判定只是几次引用比较，O(1)。
        /// 解析不到就留 null / 空数组，那样比较恒假 = 那道放行自动失效，不会误放。
        /// </summary>
        private static void EnsureResolved()
        {
            if (_looked) return;
            _looked = true;
            _deathWaltz = FindRestriction(DeathFromAbove, "死从天降");
            _reaperUlt  = FindRestriction(ReaperUltimate, "收割者终极/绝境（两招共用一个实例）");
            var list = new System.Collections.Generic.List<object>();
            for (int i = 0; i < BladeDanceAbilities.Length; i++)
                FindAllRestrictions(BladeDanceAbilities[i], "利刃之舞/森罗刃网", list);
            _bladeDance = list.ToArray();
            Main.Log("[武器判定] 利刃之舞/森罗刃网 的武器分类限制组件：收到 " + _bladeDance.Length + " 个实例");
        }

        /// <summary>收集某个技能蓝图上**全部**武器分类限制组件（可能不止一个）。</summary>
        private static void FindAllRestrictions(string guid, string label,
                                                System.Collections.Generic.List<object> into)
        {
            try
            {
                var bp = Kingmaker.Blueprints.ResourcesLibrary
                         .TryGetBlueprint<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>(guid);
                if (bp == null) { Main.Log("[武器判定] " + label + " " + guid.Substring(0, 8) + " 蓝图解析不到。"); return; }
                var cs = bp.ComponentsArray;
                if (cs == null) return;
                for (int i = 0; i < cs.Length; i++)
                    if (cs[i] is Kingmaker.UnitLogic.Abilities.Components.TargetCheckers.AbilityCasterHasWeaponOfClassification
                        && !into.Contains(cs[i]))
                        into.Add(cs[i]);
            }
            catch (Exception e) { Main.LogError("[武器判定] 收集 " + label + " 组件失败: " + e.Message); }
        }

        private static object FindRestriction(string guid, string label)
        {
            try
            {
                var bp = Kingmaker.Blueprints.ResourcesLibrary
                         .TryGetBlueprint<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>(guid);
                if (bp == null) { Main.Log("[武器判定] " + label + " 蓝图解析不到，该放行失效。"); return null; }
                var cs = bp.ComponentsArray;
                if (cs != null)
                    for (int i = 0; i < cs.Length; i++)
                        if (cs[i] is Kingmaker.UnitLogic.Abilities.Components.TargetCheckers.AbilityCasterHasWeaponOfClassification)
                        {
                            Main.Log("[武器判定] " + label + " 的武器分类限制组件：已定位");
                            return cs[i];
                        }
                Main.Log("[武器判定] " + label + " 的武器分类限制组件：没找到");
                return null;
            }
            catch (Exception e)
            { Main.LogError("[武器判定] 定位 " + label + " 组件失败: " + e.Message); return null; }
        }

        private static bool Prefix(
            Kingmaker.UnitLogic.Abilities.Components.TargetCheckers.AbilityCasterHasWeaponOfClassification __instance,
            Kingmaker.EntitySystem.Entities.MechanicEntity caster, ref bool __result)
        {
            try
            {
                if (!Main.Enabled) return true;
                if (__instance == null) return true;

                // 便宜的判断放前面：不是问 Sword 就走原版，一次反射都不用
                if (__instance.Classification != Kingmaker.Enums.WeaponClassification.Sword) return true;
                WeaponGate.Seen(WeaponGate.KindSword);

                // ★按组件实例认技能，而不是按分类★
                //   这个组件类全库有 8 个蓝图在用，其中四个是收割者技能。
                //   1.5.84 我按「分类==Sword」无脑放行，等于把四个全解锁 ——
                //   而死亡华尔兹（Desperate/Ultimate）那两个动作最花哨，
                //   引擎去取它们的长剑动作、单位动画集里没有 ⇒ 摆大字。
                //   组件是**挂在技能蓝图上的实例**，所以拿具体蓝图上的那一个
                //   做引用比较，就能精确认出「这次问的是哪一招」。
                //   引用比较是 O(1)，比任何字符串都便宜。
                //
                // ★1.7.12 加了第二条：收割者终极/绝境★
                //   作者的质疑很对：死从天降的闸早就开着、一次没放，凭什么信这条有用？
                //   区别在组件表（离线 dump 对比）：
                //     死从天降  独有 AbilityTargetIsReacheble —— 那是**跳跃攻击**的实现，
                //               贴脸时无处可跳 ⇒ AI 打分 0。它放不出来有独立解释。
                //     收割者绝境 与**战术家终极**共有 AbilitySpecialMomentumAction +
                //               AbilityMomentumLogic + ContextConditionCasterHasFact 三项，
                //               只多这一道武器闸；而战术家终极每场都放得出来。
                //   所以「开闸就能放」这个预期对终极/绝境成立，对死从天降不成立。
                //   ★一次只加一个实例★ 同时加两个，T-pose 了分不清是谁干的。
                EnsureResolved();
                bool allow = false;
                if (Main.Settings != null)
                {
                    if (!Main.Settings.ReaperSkillGate) { }
                    else if (ReferenceEquals(__instance, _deathWaltz)) allow = true;
                    else if (ReferenceEquals(__instance, _reaperUlt)) allow = true;
                    else
                        for (int i = 0; i < _bladeDance.Length; i++)
                            if (ReferenceEquals(__instance, _bladeDance[i])) { allow = true; break; }
                }
                if (allow && WeaponGate.IsBladeAnimTarget(caster))
                {
                    WeaponGate.Count(caster, WeaponGate.KindSword);
                    __result = true;
                    return false;
                }

                // ★默认关★ 见 Settings.SwordClassGate 的头注：放行会让引擎去放长剑动作，
                //   而这两个精英的动画集里没有 —— 实测摆大字 + 抽搐。
                //   计数照记（上面那行 Seen 在开关之前），这样即使关着也能知道
                //   "原版一场问了多少次"，将来要不要重开有数据可依。
                if (Main.Settings == null || !Main.Settings.SwordClassGate) return true;

                if (!WeaponGate.IsGateTarget(caster)) return true;

                WeaponGate.Count(caster, WeaponGate.KindSword);
                __result = true;   // 就当你手上是长剑
                return false;
            }
            catch { }
            return true;
        }
    }

    /// <summary>
    /// ⑤ 不因某些技能结束回合。
    ///
    /// ★为什么要这个★ 收割者线有两个技能挂着 WarhammerEndTurn：
    ///   · 森罗刃网 ReaperBladeShroud_Ability  —— 实测一直在放，放完这回合就没了
    ///   · 利刃之舞 ReaperBladeDanceAbility
    ///   而这两个精英本来输出就吃紧（辅助技占了动作的四分之三），
    ///   一个技能吃掉整个回合等于把仅有的输出窗口也砍掉。
    ///
    /// ★原版怎么结束的★ WarhammerEndTurn.OnCast 末尾：
    ///     clearMPInsteadOfEndingTurn ? SpendActionPointsAll(蓝) : RequestEndTurn()
    ///   前面还会 Buffs.Add(BuffToCaster) —— 那个 buff 多半就是「本回合已用过」的标记。
    ///   我们整个跳过 OnCast，所以标记也不加：对我们的精英来说，
    ///   这个技能变成「放完照常行动」。
    ///
    /// ★范围★ 和前面四道闸同一个 IsGateTarget：只有那两个近战精英。
    ///   别的单位（含玩家自己的收割者）一律走原版 —— 这是给卫兵的补偿，不是全局改数值。
    ///
    /// ★默认开★ 与长剑闸不同，这个**不碰动画**：技能照常播它自己的动作，
    ///   只是回合不结束。没有 T-pose 那类风险。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Abilities.Components.WarhammerEndTurn), "OnCast")]
    internal static class EndTurnSkipPatch
    {
        /// <summary>
        /// ★每回合只免一次★
        ///
        /// 作者提的风险，而且是对的：这个技能的行动点消耗我们**不知道**。
        /// 如果它本来就是 0 消耗、全靠 WarhammerEndTurn 来终结回合，
        /// 那无条件跳过 = AI 可以无限放下去 = 回合卡死。
        /// 我不能靠"它应该有消耗"来赌 —— 那种赌注输了是死循环，代价不对称。
        ///
        /// 所以只免第一次：同一个单位在同一个回合里第二次触发时照常结束回合。
        /// 最坏情况是多一次行动，不是无限。
        ///
        /// 判据用 UniqueId + TurnController.CurrentUnit 的 UniqueId ——
        /// 和 CombatWatch 认「换人了没有」是同一套办法。
        /// </summary>
        /// <summary>
        /// ★只认森罗刃网这一个技能★
        ///
        /// 1.5.95~98 我拦的是**任何** WarhammerEndTurn（只要施法者是那两个精英），
        /// 那太宽了 —— 会顺手改掉利刃之舞和别的挂了这个组件的技能。
        /// 作者明确说了利刃之舞不要加任何限制：它本来就不是每回合一次的技能，
        /// 某些条件下会刷新，动它等于破坏原版机制。
        ///
        /// 而且 WarhammerEndTurn 有 clearMPInsteadOfEndingTurn 分支 ——
        /// 对走那条分支的技能，跳过 OnCast 等于白送行动点，那是另一种破坏。
        /// 所以按技能 guid 收窄，只放行我们真正想补偿的那一个。
        /// </summary>
        private const string BladeShroud = "8b7bcaa093224422ac66c80ffcf69f6d";

        private static string _lastTurnUnit;
        private static readonly System.Collections.Generic.HashSet<string> _usedThisTurn
            = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        /// <summary>换人了就清空。查询和标记都先走它，保证两边看到同一份状态。</summary>
        private static void SyncTurn()
        {
            string turn = null;
            var tc = Kingmaker.Game.Instance != null ? Kingmaker.Game.Instance.TurnController : null;
            var cur = tc != null ? tc.CurrentUnit as BaseUnitEntity : null;
            if (cur != null) turn = cur.UniqueId;
            if (turn != _lastTurnUnit) { _lastTurnUnit = turn; _usedThisTurn.Clear(); }
        }

        /// <summary>本回合用过没有。★只读★ —— 给 IsAvailable 那道用。</summary>
        internal static bool IsUsed(BaseUnitEntity u)
        {
            try
            {
                if (u == null) return true;
                SyncTurn();
                string me = u.UniqueId;
                if (string.IsNullOrEmpty(me)) return true;   // 认不出来就当用过，保守
                return _usedThisTurn.Contains(me);
            }
            catch { return true; }
        }

        private static bool MarkUsed(BaseUnitEntity u)
        {
            try
            {
                SyncTurn();
                string me = u.UniqueId;
                if (string.IsNullOrEmpty(me)) return true;
                return !_usedThisTurn.Add(me);              // Add 成功 => 之前没用过
            }
            catch { return true; }
        }

        private static bool Prefix(Kingmaker.UnitLogic.Abilities.AbilityExecutionContext context)
        {
            try
            {
                if (!Main.Enabled) return true;
                if (Main.Settings == null || !Main.Settings.MeleeEliteNoEndTurn) return true;
                if (context == null) return true;

                // ★先按技能 guid 收窄★ 只补偿森罗刃网，别的挂了 WarhammerEndTurn 的技能
                //   一律走原版 —— 尤其是利刃之舞，作者要求不加任何限制。
                try
                {
                    var bp = context.AbilityBlueprint;
                    if (bp == null) return true;
                    if (!string.Equals(bp.AssetGuid.ToString(), BladeShroud,
                                       StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; }

                BaseUnitEntity caster = null;
                try { caster = context.MaybeCaster as BaseUnitEntity; } catch { }
                if (!WeaponGate.IsGateTarget(caster)) return true;
                if (MarkUsed(caster)) return true;              // 本回合已经免过一次

                WeaponGate.Seen(WeaponGate.KindEndTurn);
                WeaponGate.Count(caster, WeaponGate.KindEndTurn);
                return false;      // 整个跳过：不加标记 buff、不清行动点、不结束回合
            }
            catch { }
            return true;
        }
    }

    /// <summary>
    /// ⑥ 用过就不再可用 —— 配合⑤，把「免结束回合」限制成每回合一次。
    ///
    /// ★为什么要这道★ 作者提的风险：光靠⑤只是「第二次照常结束回合」，
    ///   也就是一回合仍然放得出**两次**。而这两个技能的行动点消耗我们不知道，
    ///   万一是 0 消耗，连放两次就是白拿一次输出，而且 AI 很可能真会那么干。
    ///   所以在**能不能放**这一层直接掐掉第二次。
    ///
    /// ★用 Postfix 而不是 Prefix★ 我们只**收紧**、绝不放宽：
    ///   原版说不可用就是不可用，我们只在原版说可用时把它改成不可用。
    ///   Prefix 有把不可用改成可用的可能，那是灾难。
    ///
    /// ★调用频率★ IsAvailable 是 UI 刷新级别的高频路径。所以判定顺序是
    ///   ①__result 已经是 false 就走人（绝大多数情况）→ ②技能 guid 比对（两次字符串比较）
    ///   → ③IsGateTarget。没有反射、没有分配。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Abilities.AbilityData), "IsAvailable", MethodType.Getter)]
    internal static class OncePerTurnPatch
    {
        /// <summary>森罗刃网 —— ★只有它★。利刃之舞不在此列：它本来就不是每回合一次的
        /// 技能、某些条件下会刷新，加限制等于破坏原版机制（作者明确要求）。</summary>
        private const string BladeShroud = "8b7bcaa093224422ac66c80ffcf69f6d";

        private static void Postfix(Kingmaker.UnitLogic.Abilities.AbilityData __instance, ref bool __result)
        {
            try
            {
                if (!__result) return;                         // 原版已经说不可用
                if (!Main.Enabled) return;
                if (Main.Settings == null || !Main.Settings.MeleeEliteNoEndTurn) return;
                if (__instance == null || __instance.Blueprint == null) return;

                if (!string.Equals(__instance.Blueprint.AssetGuid.ToString(), BladeShroud,
                                   StringComparison.OrdinalIgnoreCase)) return;

                var caster = __instance.Caster as BaseUnitEntity;
                if (!WeaponGate.IsGateTarget(caster)) return;

                if (EndTurnSkipPatch.IsUsed(caster)) __result = false;
            }
            catch { }
        }
    }
}
