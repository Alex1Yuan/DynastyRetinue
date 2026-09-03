using System;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Mechanics.Actions;

namespace DynastyRetinue
{
    /// <summary>
    /// ============ 机械教卫兵：召唤机仆（1.7.75）============
    ///
    /// ★需求★ T1/T2 机械教卫兵每场战斗召唤一个**机仆**；T3/精英召唤**战斗机仆**。
    ///   种类随机，消耗 0，唯一限制是每场一次。
    ///
    /// ═══ 为什么是「借用原版能力 + 重定向召唤物」而不是自建 ═══
    /// 授予单位的 fact（含 BlueprintAbility）**会被序列化进存档**。自建蓝图 ⇒
    /// 卸载 mod 后反序列化失败、存档永久打不开（本项目的头号红线）。
    /// 所以授予的必须是原版已有的 BlueprintAbility，召唤物也必须是原版 BlueprintUnit。
    ///
    /// ★原版没有「召唤机仆」的能力★ 这是全库扫描结论，不是「没搜到」：
    ///   全包只有 26 个蓝图带 ContextActionSpawnMonster，召唤的是恶魔/僵尸/虫群/
    ///   全息投影/伺服颅骨，与 86 个 Servitor 单位求交集为空。
    ///   ⇒ 必须 Harmony 重定向召唤目标，光授予一个现成能力做不到。
    ///
    /// ★donor 选它的理由★ MobTechpriestMagi_VoxSkullSummon_Ability
    ///   · 只有 3 个组件：AbilityEffectRunAction / ContextActionSpawnMonster / SummonPoolUnits
    ///     —— 没有 WarhammerEndTurn、没有 StartCombat、没有冷却组件、没有 locator
    ///   · 反向引用 0：没有任何原版单位持有它 ⇒ 运行时改它的爆炸半径≈0
    ///   · 题材也对得上（技师召唤伺服颅骨 → 我们换成机仆）
    ///   ★代价★ 它**没有名字**（本地化键 aa36c253-… 不在 enGB.json 里），所以显示名必须我们补。
    ///
    /// ★为什么用 RunAction 前后夹住而不是只挂 get_Blueprint★
    ///   同一次 RunAction 里 get_Blueprint 被调**三次**：
    ///     IL_0064 取 Size（算寻路矩形）· IL_0181 取 Prefab（算 Corpulence）· IL_01CA 实际生成
    ///   三次返回不一致会让寻路矩形、模型体积、实际落点互相脱靶。
    ///   所以在 RunAction 的 Prefix 里**一次性抽签并锁定**，Finalizer 清掉 ——
    ///   稳定性由构造保证，不靠"随机数恰好不变"。
    /// </summary>
    internal static class ServitorSummon
    {
        /// <summary>donor：技师召唤伺服颅骨。我们把召唤物换成机仆。</summary>
        internal const string DonorAbility = "7408fdcc13c04a6bb9820e4be96b6d9e";

        /// <summary>T1/T2 用：普通机仆（非 DLC3，安全底盘）。</summary>
        private static readonly string[] ServitorPool =
        {
            "f069ae81d98f4ecba8a5bbc0b6121301",   // ServitorRandomEncounter
            "30ff1bd210ccd914886ec962206dafbc",   // Servitor
        };

        /// <summary>T3/精英用：战斗机仆（非 DLC3）。</summary>
        private static readonly string[] CombatPool =
        {
            "5ba73a4183c24e909d691acabadc1db8",   // CombatServitorMultiMelta
            "3c16f2aeebc54cf1927062857252e95f",   // CombatServitorMultiMelta_Friendly
            "889485dc6e874ccfa535c1f2f59dffa5",   // CombatServitorRandomEncounter
            "454847134b48402792be9798bf92b0ca",   // GuardianCombatServitorColonization_unit
        };

        /// <summary>DLC3 版本（味道更对）。★必须判 Prefab 能不能加载★ —— 无 DLC3 时
        /// RunAction IL_0194-01AC 不会崩，但会走默认 corpulence，结果是一个**隐形单位占着格子**。</summary>
        private static readonly string[] ServitorPoolDlc3 = { "a92cdde1068c4609b437cd5a22f8b3b0" };
        private static readonly string[] CombatPoolDlc3 =
        {
            "d74728f897ef4b0a85978e25f43ea155",   // DLC3_OP_Dogmatic_CombatServitor_Unit
            "2af759a6dba447dca72494973763d3c0",   // DLC3_OP_Xenarite_CombatServitor_Unit
        };

        // ★硬排除（不要往池子里加回去）★ 自爆/腐化/剧情/幽灵/伺服颅骨：
        //   RE_MadCombatServitor 03d80aa2 · _Melee 1b6b3452 · _wBuff 33abf95b ·
        //   ServitorChaosSelfDestruct 9513a791 · _ForgeHeart 511c60c0 · ServitorFrozen 16203cb3 ·
        //   CombatServitor_BoardedShip 9acbca8f · ServoSkullScrapCode 9cd810b2（是颅骨不是机仆）

        /// <summary>本次 RunAction 抽中的召唤物。★三次 get_Blueprint 必须拿到同一个★</summary>
        [ThreadStatic] private static BlueprintUnit _locked;

        // ★这两个成员在 Code.dll 里不是 public，只能反射★
        //   ContextAction.Context            —— protected
        //   RuleCalculateCooldown 的 set_CooldownComponent —— 非公开
        // 句柄一次性缓存；取不到就**明说**，不静默退化成"这个功能没生效"。
        private const System.Reflection.BindingFlags Any =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance;

        private static System.Reflection.PropertyInfo _pCtx, _pCdComp;
        private static bool _looked, _warned;

        private static void EnsureMembers()
        {
            if (_looked) return;
            _looked = true;
            try
            {
                for (var t = typeof(ContextActionSpawnMonster); t != null && _pCtx == null; t = t.BaseType)
                    _pCtx = t.GetProperty("Context", Any);
                _pCdComp = typeof(Kingmaker.RuleSystem.Rules.RuleCalculateCooldown)
                           .GetProperty("CooldownComponent", Any);
            }
            catch { }
            if ((_pCtx == null || _pCdComp == null) && !_warned)
            {
                _warned = true;
                Main.Log("[召唤机仆] ★挂不上★ 反射取不到："
                       + (_pCtx == null ? " ContextAction.Context" : "")
                       + (_pCdComp == null ? " RuleCalculateCooldown.CooldownComponent" : "")
                       + "　—— 成员名或可访问性变了。★这一行的存在本身就是防线："
                       + "静默失效会让功能像「AI 从来不选这个技能」，能查很久。★");
            }
        }

        /// <summary>取这次 spawn 动作的施法者。取不到一律返回 null（放行原版）。</summary>
        private static BaseUnitEntity CasterOf(ContextActionSpawnMonster act)
        {
            try
            {
                EnsureMembers();
                if (_pCtx == null || act == null) return null;
                var ctx = _pCtx.GetValue(act, null)
                          as Kingmaker.UnitLogic.Mechanics.MechanicsContext;
                return ctx != null ? ctx.MaybeCaster as BaseUnitEntity : null;
            }
            catch { return null; }
        }

        /// <summary>这个单位是不是我们要给召唤能力的机械教卫兵。</summary>
        internal static bool Applies(BaseUnitEntity u)
        {
            try
            {
                if (u == null || !Main.Enabled) return false;
                if (!RetinueRegistry.IsGuard(u)) return false;
                int ai = RetinueRegistry.ArchetypeOf(u);
                if (ai < 0) return false;
                var arch = Archetypes.Get(ai);
                // ★按名字里的 Mechanicus 判，不写死下标★ archetypes.json 的顺序可能变
                return arch != null && arch.Name != null
                    && arch.Name.IndexOf("Mechanicus", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 给这个卫兵装上召唤能力（幂等）。
        /// ★存档安全★ 加进 Abilities 的是**原版** BlueprintAbility 的 AssetGuid，
        ///   与「原版技师持有该能力时写进存档的东西」逐字节同构。卸载 mod 后
        ///   反序列化照常，能力变回它原本的样子（一个没人用的召唤伺服颅骨技能）。
        /// </summary>
        internal static void EnsureGranted(BaseUnitEntity guard)
        {
            try
            {
                if (!Applies(guard)) return;
                var bp = ResourcesLibrary.TryGetBlueprint<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>(DonorAbility);
                if (bp == null)
                {
                    if (!_donorWarned)
                    {
                        _donorWarned = true;
                        Main.Log("[召唤机仆] ★取不到 donor 蓝图 " + DonorAbility + "★ 功能不会生效。");
                    }
                    return;
                }
                EnsureDonorTuned(bp);

                foreach (var a in guard.Abilities)
                    if (a != null && a.Blueprint == bp) return;      // 已经有了
                guard.Abilities.Add(bp);
                Main.Log("[召唤机仆] 已授予 " + (guard.CharacterName ?? "?")
                       + "　★若整场没有「召唤 →」那一行，说明能力在但 AI 没选它★");
            }
            catch (Exception e) { Main.LogError("[召唤机仆] 授予失败: " + e); }
        }

        private static bool _donorWarned, _donorTuned;

        /// <summary>
        /// 运行时把 donor 调成「0 消耗」。
        /// ★为什么改蓝图是安全的★ 蓝图**从不被序列化**，存档只存 AssetGuid；
        ///   而这个 donor 的反向引用是 0（没有任何原版单位持有它），爆炸半径≈0。
        /// ★为什么不用 Params.FreeAction★ AbstractUnitCommand.IsFreeAction 是非虚 getter、
        ///   所有指令共用，补它等于站在全局路径上。改蓝图字段只影响这一个技能。
        /// </summary>
        private static void EnsureDonorTuned(Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility bp)
        {
            if (_donorTuned) return;
            _donorTuned = true;
            try
            {
                bp.IsFreeAction = true;      // AbilityData.CalculateActionPointCost: IsFreeAction ? 0 : rule
                bp.CooldownRounds = 0;       // 每场一次由 RuleCalculateCooldown 那条补丁负责
                Main.Log("[召唤机仆] donor 已调为 0 消耗（IsFreeAction=true）。"
                       + "★注意★ 该 donor 原版**没有显示名**（本地化键不在 enGB.json 里），"
                       + "技能栏上会是空白 —— 显示名与图标尚未补，见发布前待办。");
            }
            catch (Exception e) { Main.LogError("[召唤机仆] 调 donor 失败: " + e); }
        }

        /// <summary>该给普通机仆还是战斗机仆。true = 战斗机仆（T3 / 精英）。</summary>
        private static bool WantsCombatServitor(BaseUnitEntity u)
        {
            try
            {
                int ai = RetinueRegistry.ArchetypeOf(u);
                var arch = ai >= 0 ? Archetypes.Get(ai) : null;
                if (arch != null && GearTool.IsElite(u, arch)) return true;
                int lv = u.Progression != null ? u.Progression.CharacterLevel : 1;
                return lv >= 36;                        // 与 TierRank 同一套断点：36=T3
            }
            catch { return false; }
        }

        /// <summary>抽一个召唤物。DLC3 池优先，Prefab 加载不到就回落非 DLC3。</summary>
        private static BlueprintUnit Pick(BaseUnitEntity caster)
        {
            try
            {
                bool combat = WantsCombatServitor(caster);
                var dlc3 = combat ? CombatPoolDlc3 : ServitorPoolDlc3;
                var safe = combat ? CombatPool : ServitorPool;

                // 按施法者播种：同一场里重复触发拿到同一个结果，成本为零
                int seed;
                try { seed = caster.UniqueId != null ? caster.UniqueId.GetHashCode() : 0; }
                catch { seed = 0; }
                var rnd = new Random(seed ^ Environment.TickCount);

                var order = new System.Collections.Generic.List<string>();
                for (int i = 0; i < dlc3.Length; i++) order.Add(dlc3[i]);
                for (int i = 0; i < safe.Length; i++) order.Add(safe[i]);
                // 洗牌 DLC3 段和安全段各自内部，但保持 DLC3 优先
                Shuffle(order, 0, dlc3.Length, rnd);
                Shuffle(order, dlc3.Length, safe.Length, rnd);

                for (int i = 0; i < order.Count; i++)
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(order[i]);
                    if (bp == null) continue;
                    if (!PrefabLoads(bp)) continue;    // ★隐形单位占格的唯一防线★
                    return bp;
                }
            }
            catch { }
            return null;
        }

        private static void Shuffle(System.Collections.Generic.List<string> l, int from, int len, Random r)
        {
            for (int i = len - 1; i > 0; i--)
            {
                int j = r.Next(i + 1);
                var t = l[from + i]; l[from + i] = l[from + j]; l[from + j] = t;
            }
        }

        /// <summary>Prefab 能不能真的加载出来。加载不到 = 无 DLC3 或资源缺失，绝不能用。</summary>
        private static bool PrefabLoads(BlueprintUnit bp)
        {
            try
            {
                var pf = bp.Prefab;
                if (pf == null) return false;
                return pf.Load(false, false) != null;
            }
            catch { return false; }
        }

        // ═══ 在 RunAction 前后夹住，锁定本次抽签结果 ═══
        [HarmonyPatch(typeof(ContextActionSpawnMonster), "RunAction")]
        internal static class LockPick
        {
            private static void Prefix(ContextActionSpawnMonster __instance)
            {
                try
                {
                    _locked = null;
                    if (!Main.Enabled) return;
                    // Context 为 null = 编辑器 / GetCaption 路径，别碰
                    var caster = CasterOf(__instance);
                    if (caster == null || !Applies(caster)) return;
                    _locked = Pick(caster);
                    // ★这条必须无条件打★ 沉默 ≠ 成功：
                    //   看不到机仆时，要能分清「没授予」「授予了但 AI 没选」「选了但重定向失败」。
                    //   前两种在这里根本不会出现日志，所以这一行的**有无**本身就是判据。
                    if (_locked == null)
                        Main.Log("[召唤机仆] " + (caster.CharacterName ?? "?")
                               + " 抽不到可用的召唤物（Prefab 全部加载失败？）—— 本次退回原版行为（会召出伺服颅骨）。");
                    else
                        Main.Log("[召唤机仆] " + (caster.CharacterName ?? "?")
                               + " 召唤 → " + _locked.name
                               + "（" + (WantsCombatServitor(caster) ? "战斗机仆" : "机仆") + "）");
                }
                catch { _locked = null; }
            }

            /// <summary>★用 Finalizer 不用 Postfix★ 原方法抛异常时也要清，否则会污染下一次。</summary>
            private static void Finalizer() { _locked = null; }
        }

        [HarmonyPatch(typeof(ContextActionSpawnMonster), "get_Blueprint")]
        internal static class Redirect
        {
            private static void Postfix(ref BlueprintUnit __result)
            {
                try { if (_locked != null) __result = _locked; }
                catch { }
            }
        }

        // ═══ 每场战斗一次 ═══
        //
        // ★为什么挂 RuleCalculateCooldown 而不是 AbilityData.IsAvailable★
        //   IsAvailable 有 18 个调用点（含 AI 的 DecisionContext.IsUsableAbility 和 5 个 UI 槽位类），
        //   是 UI 刷新级热路径；而 RuleCalculateCooldown 全局只有一个触发点
        //   （PartAbilityCooldowns.StartCooldown），后者又只有一个调用点（UnitUseAbility.OnAction）
        //   —— 即「技能真正打出去」才跑一次。量级完全不同。
        //
        // ★WarhammerCooldown 不进存档★ 它是 BlueprintComponent 子类但**不是 SimpleBlueprint**
        //   （没有 AssetGuid），运行时 new 一个只是普通 C# 对象。
        //   存档里落的是 CooldownData 的 int+bool，key 是原版 BlueprintAbility ⇒ 不碰红线。
        //
        // ★已知边界（不是 bug，是引擎语义）★
        //   StartCooldown 开头就 `if (!Owner.IsInCombat) return;`
        //   ⇒ **战斗外施放不记冷却**，「每场一次」只在战斗内成立。
        [HarmonyPatch(typeof(Kingmaker.RuleSystem.Rules.RuleCalculateCooldown), "OnTrigger")]
        internal static class OncePerCombat
        {
            private static readonly Kingmaker.UnitLogic.FactLogic.WarhammerCooldown _cd =
                new Kingmaker.UnitLogic.FactLogic.WarhammerCooldown
                { CooldownInRounds = 0, UntilEndOfCombat = true };

            private static void Postfix(Kingmaker.RuleSystem.Rules.RuleCalculateCooldown __instance)
            {
                try
                {
                    if (!Main.Enabled || __instance == null) return;
                    var ab = __instance.Ability;
                    var bp = ab != null ? ab.Blueprint : null;
                    if (bp == null) return;
                    string g = null;
                    try { g = bp.AssetGuid.ToString(); } catch { }
                    if (!string.Equals(g, DonorAbility, StringComparison.OrdinalIgnoreCase)) return;

                    var caster = ab.Caster as BaseUnitEntity;
                    if (caster == null || !Applies(caster)) return;

                    EnsureMembers();
                    if (_pCdComp == null) return;            // 取不到就别硬来，上面已经报过了
                    __instance.Result = 0;
                    _pCdComp.SetValue(__instance, _cd, null); // 0 回合 + 直到战斗结束 = 每场一次
                }
                catch { }
            }
        }
    }
}
