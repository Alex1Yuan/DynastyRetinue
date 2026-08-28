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

        /// <summary>三道闸的前两道。补丁入口统一走这里，别各写各的。</summary>
        internal static bool IsGateTarget(object entity)
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
                for (int i = 0; i < MeleeEliteUnits.Length; i++)
                    if (string.Equals(g, MeleeEliteUnits[i], StringComparison.OrdinalIgnoreCase))
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
        private static readonly int[] _seen = new int[3];

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
                if (!_hits.TryGetValue(name, out a)) { a = new int[3]; _hits[name] = a; }
                a[kind]++;
            }
            catch { }
        }

        /// <summary>开打时清零 —— 由 CombatWatch.Tick 调。</summary>
        internal static void ResetHits()
        {
            try { _hits.Clear(); _seen[0] = _seen[1] = _seen[2] = 0; } catch { }
        }

        /// <summary>战斗结束时汇总 —— 由 CombatWatch.Dump 调，接在总账后面。</summary>
        internal static void ReportHits(System.Text.StringBuilder sb)
        {
            try
            {
                if (sb == null) return;
                sb.AppendLine("  ── 武器判定放行次数（1.5.74 新增）──");
                sb.AppendLine(string.Format("      判定被求值：无视护甲 {0} 次 · 长剑 {1} 次 · 防御反击 {2} 次",
                              _seen[KindArmorIgnore], _seen[KindBlade], _seen[KindCounter]));
                sb.AppendLine("      ★求值 0 = 原版没走到这道判定（不是我们拒的）；"
                            + "求值 >0 而放行 0 = 走到了但被我们的闸拒了，查单位 guid。★");
                if (_hits.Count == 0)
                {
                    sb.AppendLine("      本场一次都没放行。");
                    return;
                }
                sb.AppendLine("      卫兵                          无视护甲  长剑判定  防御反击");
                foreach (var kv in _hits)
                    sb.AppendLine(string.Format("      {0,-28} {1,8} {2,9} {3,9}",
                        kv.Key.Length > 28 ? kv.Key.Substring(0, 28) : kv.Key,
                        kv.Value[KindArmorIgnore], kv.Value[KindBlade], kv.Value[KindCounter]));
                sb.AppendLine("      ★放行 ≠ 效果打出来了★ 这只证明条件判定通过。"
                            + "无视护甲要看伤害有没有变、防反要看有没有多出来的那一刀。");
            }
            catch { }
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
}
