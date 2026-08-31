using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.UnitLogic.Abilities;

namespace DynastyRetinue
{
    /// <summary>
    /// 让 AI 能给「死从天降」选出目标。★默认关★
    ///
    /// ═══ 病因（源码级，工作流查证）═══
    ///
    /// Kingmaker.AI.AbilityInfo.GetAbilityTargetSelector():
    ///     if (isGrenadeTypeAOE || isCharge || ability.IsAOE) return new AOETargetSelector(this);
    ///     return new SingleTargetSelector(this);
    ///
    /// 死从天降三个条件全不成立 ⇒ 拿到 SingleTargetSelector。而：
    ///   · SingleTargetSelector 的候选只来自 DecisionContext.GetAvailableTargets()，
    ///     那里每条分支产出的都是**单位实体**，没有任何一条产出空地格子。
    ///   · 死从天降却是**点目标**技能（CanTargetPoint=true / CanTargetEnemies=false），
    ///     而且挂着 AbilityTargetIsReachable{CheckNodeOnUnitOccupation=true}
    ///     —— 要求目标格**没有活单位站着**。
    ///   于是每个单位候选都被这道限制否掉，SelectTarget 返回 null，
    ///   TaskNodeSelectAbilityTarget 记一条 AI 日志然后静默跳过。
    ///
    /// ★所以它不是「打分低」，是压根选不出目标。★
    /// 这解释了为什么 1.7.x 把武器闸放行 204/147/770 次，施放仍然恒为 0。
    ///
    /// ═══ 为什么三个条件不成立 ═══
    /// isGrenadeTypeAOE 要求 `CanTargetPoint && GetPatternSettings() != null`。
    /// 而 GetPatternSettings() 只看**本体蓝图**上的 IAbilityAoEPatternProvider，
    /// 死从天降的 AbilityTargetsInPattern 却挂在**子技能** ReaperDeathWaltzStrikeAbility
    /// (b5403e19…) 上 —— 本体只有 AbilityEffectRunAction → ContextActionJumpToTarget。
    /// isCharge=false（没有 AbilityCustomCharge）、IsAOE=false（同样查不到 pattern）。
    ///
    /// ═══ 修法：把子技能的 pattern 补给 AbilityInfo ═══
    /// 补上之后**后面全是原版逻辑**：AOETargetSelector 的散弹分支会
    ///   ScanPatternNodesAroundTarget() 枚举每个敌人周围 patternBounds 内的候选格
    ///   → IsNodeValid() 过 Walkable / 射程 / 覆盖数
    ///   → SelectTarget() 按「pattern 覆盖到几个敌人」打分，越多越近越优
    /// 也就是作者猜「AI 不会挑周围敌人最多的落点」—— 那套代码**是有的**，
    /// 死从天降只是没资格用到它。我们不自己写打分，只补一张入场券。
    ///
    /// ═══ 为什么安全 ═══
    ///   · AbilityInfo 是 **AI 专用类**，只有 AI 决策会 new 它；玩家操作和技能执行都不经过。
    ///   · 闸是 WeaponGate.IsGateTarget(caster)：Main.Enabled → IsGuard O(1) → 近战精英白名单。
    ///     原版单位 100% 不受影响，哪怕场上有原版收割者 NPC。
    ///   · 不碰任何蓝图、不新建 AssetId、不进存档 —— 卸载 mod 即完全消失。
    ///   · 全程 try/catch：任何一步取不到，就原样返回 = 原版行为，不会半改不改。
    ///
    /// ═══ 为什么全用反射 ═══
    /// AbilityTargetsInPattern 这个类在离线反射环境里加载不了
    /// （Mono 对接口默认方法抛 TypeLoadException），所以它的成员名**没法离线验证**。
    /// 与其赌名字，不如反射 + 找不到就记一条日志并退回原版。
    /// AbilityInfo 那边的字段名是 dump 出来实证过的，但为了统一也走反射。
    /// ★开销★ AbilityInfo 每次决策每个技能构造一次，不在每帧路径；
    /// 且第一道闸 ReferenceEquals(蓝图) 是 O(1)，绝大多数调用一行就返回。
    /// </summary>
    /// <summary>
    /// 【探针】死从天降的伤害到底以哪里为中心 —— 落点还是出发点。
    ///
    /// ★为什么要它★ 作者观察：「锈行猎手的死从天降好像是从出发位置算的伤害？
    /// 而不是落点？电僧好像是正常算落点的？」
    /// 而我手上的证据只是可疑的几何（一次施放里，某个受击单位距落点约 3~4 格，
    /// 而我自己的日志写着「图案外延=1」），但那两条 Apply 未必都属于这个技能，
    /// 也没有施法者跳跃前的坐标 —— **不足以定案**。
    /// 这一轮我已经三次凭一行日志下错结论（BlockAttackAnimation、路线 B、死从天降伤害），
    /// 所以这次先拿判据。
    ///
    /// ★怎么读★ 同一行里同时给出「受击点到落点」和「受击点到出发点」两个距离：
    ///   离落点近   ⇒ 正常，以落点为中心（原版设计）
    ///   离出发点近 ⇒ 图案锚在了施法者原位，多半与我们注入的 pattern 有关
    /// 两个精英各放一次就能对比出差异。
    ///
    /// ★开销★ 只在**我们的卫兵施放死从天降**时打，一次施放一行；走诊断日志开关。
    /// </summary>
    // ★1.7.61 修挂点★ 1.7.60 我写的是 Cast(TargetWrapper)，而实际签名是
    //   Cast(AbilityExecutionContext)。Harmony 直接 FAIL：
    //     [Harmony] FAIL DeathWaltzGeometryProbe —— Undefined target method
    //   这正是记忆里 rt-verify-patch-targets-offline 那条规则的原样重犯：
    //   **改补丁前先离线 dump 目标方法的真实签名**，别凭印象写。
    //   好在 mod 自己的 Harmony 报告有「失败清单」，没有静默 —— 这个报告值得保留。
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Abilities.AbilityData), "Cast",
                  new Type[] { typeof(Kingmaker.UnitLogic.Abilities.AbilityExecutionContext) })]
    internal static class DeathWaltzGeometryProbe
    {
        private static void Prefix(Kingmaker.UnitLogic.Abilities.AbilityData __instance,
                                   Kingmaker.UnitLogic.Abilities.AbilityExecutionContext context)
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.DiagVerbose) return;
                var bp = __instance != null ? __instance.Blueprint : null;
                if (bp == null || bp.name == null) return;
                if (bp.name.IndexOf("DeathWaltz", StringComparison.Ordinal) < 0) return;

                var caster = __instance.Caster as Kingmaker.EntitySystem.Entities.BaseUnitEntity;
                if (caster == null || !WeaponGate.IsGateTarget(caster)) return;

                var from = caster.Position;
                var to = (context != null && context.ClickedTarget != null) ? context.ClickedTarget.Point : from;
                Main.Log("[死从天降·几何] " + (caster.CharacterName ?? "?") + "　技能=" + bp.name
                       + "　出发点=(" + from.x.ToString("F1") + "," + from.z.ToString("F1") + ")"
                       + "　落点=(" + to.x.ToString("F1") + "," + to.z.ToString("F1") + ")"
                       + "　两点距离=" + UnityEngine.Vector3.Distance(from, to).ToString("F1")
                       + "　★怎么读★ 稍后的伤害若集中在出发点附近 ⇒ 图案锚错了；"
                       + "集中在落点附近 ⇒ 正常。对照游戏日志里同一时刻的 "
                       + "「Apply ability effect to [Target: unit … (x,y,z)]」坐标。");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Kingmaker.AI.AbilityInfo), MethodType.Constructor, new Type[] { typeof(AbilityData) })]
    internal static class DeathWaltzAoePatch
    {
        private const string DeathWaltz = "6f1b7cfb48a0450cb85ce8a8879502de";
        private const string StrikeSub  = "b5403e19a38d4f9199eccdfb9489bf8e";   // ReaperDeathWaltzStrikeAbility

        private static bool _resolved;
        private static object _provider;          // 子技能上的 AbilityTargetsInPattern 实例
        private static object _pattern;           // provider.Pattern
        private static object _targets;           // provider.Targets（TargetType）
        private static object _bounds;            // pattern.Bounds（IntRect）
        private static int _ext;                  // patternBounds 的最大外延，用来算 effectiveRange
        private static Kingmaker.Blueprints.BlueprintScriptableObject _waltzBp;

        /// <summary>失败只记一次，别每次决策刷屏。</summary>
        private static bool _warned;
        private static void Warn(string why)
        {
            if (_warned) return;
            _warned = true;
            Main.Log("[死从天降] 补 pattern 失败（" + why + "）—— 保持原版行为，该技能仍不会被 AI 选中。");
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                _waltzBp = Kingmaker.Blueprints.ResourcesLibrary
                    .TryGetBlueprint<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>(DeathWaltz);
                var sub = Kingmaker.Blueprints.ResourcesLibrary
                    .TryGetBlueprint<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>(StrikeSub);
                if (_waltzBp == null || sub == null) { Warn("蓝图解析不到"); return; }

                var cs = sub.ComponentsArray;
                if (cs != null)
                    for (int i = 0; i < cs.Length; i++)
                        if (cs[i] != null && cs[i].GetType().Name == "AbilityTargetsInPattern")
                        { _provider = cs[i]; break; }
                if (_provider == null) { Warn("子技能上找不到 AbilityTargetsInPattern"); return; }

                _pattern = Get(_provider, "Pattern");
                _targets = Get(_provider, "Targets");
                if (_pattern == null) { Warn("provider.Pattern 取不到"); return; }

                _bounds = Get(_pattern, "Bounds");
                if (_bounds == null) { Warn("pattern.Bounds 取不到"); return; }

                // effectiveRange = max(minRange, maxRange - 图案最大外延)，抄原版 isGrenadeTypeAOE 分支
                int xmin = AsInt(Get(_bounds, "xmin")), xmax = AsInt(Get(_bounds, "xmax"));
                int ymin = AsInt(Get(_bounds, "ymin")), ymax = AsInt(Get(_bounds, "ymax"));
                _ext = Math.Max(Math.Max(xmax, ymax), Math.Max(-xmin, -ymin));

                Main.Log("[死从天降] pattern 已从子技能取到，AI 目标选择器将走 AOE 分支。图案外延=" + _ext);
            }
            catch (Exception e) { Warn(e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>字段优先、属性兜底 —— 反编译看到的是字段，但不同版本可能是属性。</summary>
        private static object Get(object o, string name)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) return p.GetValue(o, null);
            }
            catch { }
            return null;
        }

        private static void Set(object o, string name, object v)
        {
            if (o == null || v == null) return;
            try
            {
                var f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) { f.SetValue(o, v); return; }
                var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(o, v, null);
            }
            catch { }
        }

        private static int AsInt(object o) { try { return o == null ? 0 : Convert.ToInt32(o); } catch { return 0; } }

        /// <summary>
        /// 真正把 pattern 补上去的次数 —— 由 WeaponGate 的战报读走并清零。
        ///
        /// ★为什么必须有这个★ 启动时那行「pattern 已取到」只证明**解析成功**，
        ///   不证明它真的作用到了卫兵的技能上。万一死从天降还是不放，
        ///   「补了 0 次」和「补了 300 次」指向完全相反的下一步：
        ///     0 次   ⇒ 闸把它挡了，或蓝图引用比较没中 —— 查作用域
        ///     >0 次  ⇒ 补上了但 AI 的 AOE 选择器仍选不出目标 —— 查选择器
        ///   没有这个数就只能猜，而猜错一次就是一整轮实机测试白费。
        /// </summary>
        internal static int Applied;

        private static void Postfix(object __instance, AbilityData ability)
        {
            try
            {
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.ReaperSkillGate) return;
                if (__instance == null || ability == null) return;

                Resolve();
                if (_provider == null || _pattern == null) return;

                // ★最便宜的判据放最前★ 引用比较，不分配、不查表
                if (!ReferenceEquals(ability.Blueprint, _waltzBp)) return;

                // ★作用域★ 用更窄的 IsBladeAnimTarget —— 只有锈行猎手。
                // 1.7.18 实测电僧放这招会 T-pose 并卡住（它的动作集是照拳套做的）。
                if (!WeaponGate.IsBladeAnimTarget(ability.Caster)) return;

                Set(__instance, "patternProvider",    _provider);
                Set(__instance, "pattern",            _pattern);
                if (_targets != null) Set(__instance, "aoeIntendedTargets", _targets);
                Set(__instance, "isGrenadeTypeAOE",   true);
                Set(__instance, "patternBounds",      _bounds);

                int minR = AsInt(Get(__instance, "minRange"));
                int maxR = AsInt(Get(__instance, "maxRange"));
                Set(__instance, "effectiveRange", Math.Max(minR, maxR - _ext));
                Applied++;
            }
            catch (Exception e) { Warn("Postfix " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
