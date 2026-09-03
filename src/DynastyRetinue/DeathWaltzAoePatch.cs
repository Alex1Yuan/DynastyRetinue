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
    /// 【几何对照表】死从天降**父技能**施放时的出发点与落点。
    ///
    /// ★上一版这个探针为什么是废的★
    ///   它挂在 AbilityData.Cast 上，打 caster → context.ClickedTarget 的距离。
    ///   但 Strike 蓝图 m_CastOnSelf=1 ⇒ ClickedTarget 就是施法者自己
    ///   ⇒ from == to ⇒ **打出来的「两点距离」结构上恒为 0.0**。
    ///   我却拿它当过「拍到了 0.4 格」的依据 —— 那个数从来就不存在。
    ///   ★教训★ 探针上线前要先问一句「它在被测对象上真的能取到两个不同的值吗」。
    ///
    /// ★现在的职责★ 只做一件事：在**父技能**（ReaperDeathWaltzAbility）施放时，
    ///   把「出发点」和「落点」按施法者记下来，供 DeathWaltzStrikeProbe 在打击真正
    ///   结算的那一刻做三点比对（结算点 / 出发点 / 落点）。
    ///   单独一个点没有意义，必须三点同框才能分辨「锚在出发点」还是「飞行中结算」。
    ///
    /// ★用格坐标比，不用连续坐标★ 取样链路本来就是格子量化的
    ///   （AbilityTargetsInPattern → GetBestShootingPosition → Caster.CurrentUnwalkableNode），
    ///   连续坐标的零点几格差属于噪声，不构成判据。
    internal static class DeathWaltzGeometry
    {
        private struct Shot
        {
            public UnityEngine.Vector3 From, To;
            public int FromX, FromZ, ToX, ToZ;
            public bool HasCells;
        }

        private static readonly System.Collections.Generic.Dictionary<object, Shot> _shots =
            new System.Collections.Generic.Dictionary<object, Shot>();

        /// <summary>
        /// 每个施法者的施放序号。★为什么需要它★
        /// 死从天降会来回跳（A→B 然后 B→A），而这里是「按施法者一个槽、后写覆盖前写」。
        /// 若第二跳的结算比对到了第一跳的记录，就会打出「更靠近出发点」——
        /// 那是**配对错位**，不是真的锚错。1.7.81 起把序号一起打出来，
        /// 几何行和结算行的序号对不上就说明配对错了，这一行作废。
        /// ★教训（本轮第四次同类）★ 探针不能只输出结论，还得输出「这条结论比对的是哪一次」。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<object, int> _seq =
            new System.Collections.Generic.Dictionary<object, int>();

        internal static int SeqOf(object caster)
        {
            int n; return (caster != null && _seq.TryGetValue(caster, out n)) ? n : -1;
        }

        internal static void Record(Kingmaker.EntitySystem.Entities.BaseUnitEntity caster,
                                    UnityEngine.Vector3 from, UnityEngine.Vector3 to)
        {
            try
            {
                if (caster == null) return;
                if (_shots.Count > 32) _shots.Clear();       // 有界，句柄用完不会通知我们
                var sh = new Shot { From = from, To = to };
                try
                {
                    var n = caster.CurrentUnwalkableNode;
                    sh.FromX = n.XCoordinateInGrid; sh.FromZ = n.ZCoordinateInGrid;
                    sh.HasCells = true;
                }
                catch { }
                int seqNow; _seq.TryGetValue(caster, out seqNow);
                _seq[caster] = seqNow + 1;
                _shots[caster] = sh;
            }
            catch { }
        }

        /// <summary>结算点相对「出发点 / 落点」的两个距离。没记录过就返回空串。</summary>
        internal static string Compare(Kingmaker.EntitySystem.Entities.BaseUnitEntity caster,
                                       UnityEngine.Vector3 resolveAt)
        {
            try
            {
                Shot sh;
                if (caster == null || !_shots.TryGetValue(caster, out sh)) return "　（没记到本次施放的出发点）";
                float dFrom = UnityEngine.Vector3.Distance(resolveAt, sh.From);
                float dTo   = UnityEngine.Vector3.Distance(resolveAt, sh.To);
                return "　[第" + SeqOf(caster) + "跳]　距出发点=" + dFrom.ToString("F1")
                     + " 距落点=" + dTo.ToString("F1")
                     + "　出发=(" + sh.From.x.ToString("F1") + "," + sh.From.z.ToString("F1") + ")"
                     + " 落点=(" + sh.To.x.ToString("F1") + "," + sh.To.z.ToString("F1") + ")"
                     // ★1.7.82 去掉那句判语★ 它按「外跳」写死了，而杂技表演的**逆跳腿**
                     //   目的地本来就是该 Entry 的出发点 —— 落在那里是**正确**行为，
                     //   套用外跳的判语会把正常结果误报成异常。本轮已因此虚惊一次。
                     // ★教训（第五次同类）★ 判语是「结论」，而结论依赖上下文；
                     //   探针只在能确定上下文时才配给结论，否则就只报事实。
                     + (dFrom < dTo
                        ? "　【离出发点更近】外跳腿⇒异常；杂技表演的逆跳腿⇒正常（终点本就是出发点）"
                        : "　【离落点更近】外跳腿⇒正常");
            }
            catch { return ""; }
        }
    }

    /// <summary>
    /// 在**父技能**施放时记下出发点与落点。只记录，不下结论 ——
    /// 结论由 DeathWaltzStrikeProbe 在打击结算那一刻给出。
    /// </summary>
    [Main.DiagOnly]
    [HarmonyPatch(typeof(Kingmaker.UnitLogic.Abilities.AbilityData), "Cast",
                  new Type[] { typeof(Kingmaker.UnitLogic.Abilities.AbilityExecutionContext) })]
    internal static class DeathWaltzGeometryProbe
    {
        /// <summary>ReaperDeathWaltzAbility（父技能，带位移的那个）。</summary>
        private const string DeathWaltzGuid = "6f1b7cfb48a0450cb85ce8a8879502de";

        private static void Prefix(Kingmaker.UnitLogic.Abilities.AbilityData __instance,
                                   Kingmaker.UnitLogic.Abilities.AbilityExecutionContext context)
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.DiagVerbose) return;
                var bp = __instance != null ? __instance.Blueprint : null;
                if (bp == null) return;
                string g = null;
                try { g = bp.AssetGuid.ToString(); } catch { }
                // ★按 GUID 判★ 旧版用 name.IndexOf("DeathWaltz")，会把 Strike / Spring /
                //   Ultimate 等一整族都匹配进来，记录互相覆盖。
                if (!string.Equals(g, DeathWaltzGuid, StringComparison.OrdinalIgnoreCase)) return;

                var caster = __instance.Caster as Kingmaker.EntitySystem.Entities.BaseUnitEntity;
                if (caster == null || !WeaponGate.IsGateTarget(caster)) return;

                var from = caster.Position;
                var to = (context != null && context.ClickedTarget != null) ? context.ClickedTarget.Point : from;
                DeathWaltzGeometry.Record(caster, from, to);

                Main.Log("[死从天降·几何] " + (caster.CharacterName ?? "?")
                       + "　出发点=(" + from.x.ToString("F1") + "," + from.z.ToString("F1") + ")"
                       + "　落点=(" + to.x.ToString("F1") + "," + to.z.ToString("F1") + ")"
                       + "　跳跃距离=" + UnityEngine.Vector3.Distance(from, to).ToString("F1")
                       + "　[第" + DeathWaltzGeometry.SeqOf(caster) + "跳]"
                       + "　★这一行只是记录，结论看稍后的 [死从天降·结算]★");
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
