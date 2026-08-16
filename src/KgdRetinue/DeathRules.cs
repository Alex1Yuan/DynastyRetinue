using System;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Mechanics.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.PubSubSystem.Core.Interfaces;

namespace KgdRetinue
{
    /// <summary>
    /// 卫兵的死亡规则：**普通卫兵永久死亡，精英倒地可救**。
    ///
    /// ================= 原版是怎么判的（逐行确认）=================
    /// Kingmaker.Controllers.Units.UnitLifeController.CalculateLifeState:
    ///     if (LifeState.ScriptedKill)                    return Dead;
    ///     if (Health.HitPointsLeft &gt; 0)                  return Conscious;
    ///     if (!Features.UnconsciousOnZeroHealth
    ///         &amp;&amp; !IsActiveCompanionUnit(unit))           return Dead;
    ///     return Unconscious;
    ///
    /// IsActiveCompanionUnit:
    ///     companion != null ? companion.State != CompanionState.ExCompanion : false
    ///
    /// ★ 关键 ★ 卫兵是 **ExCompanion**（这是它们不进 PartyAndPets 的同一个原因），
    /// 所以 IsActiveCompanionUnit 恒为 false ⇒ **现状下所有卫兵（含精英）倒血就直接 Dead**。
    /// 也就是说"普通卫兵永久死亡"原版已经做到了，不需要我们加任何东西；
    /// 真正要做的是**反过来给精英开倒地**。
    ///
    /// ================= 做法 =================
    /// 精英：Features.UnconsciousOnZeroHealth.Retain()
    ///   —— 这是原版自己的用法（CommandIgnoreCombat.cs:60-61 等处同款）。
    ///   ★ PartMechanicFeatures 整类零 [JsonProperty]，OnPrePostLoad 强制 Initialize()，
    ///     所以 Retain 是**纯运行时**的，每次过图/读档都要重挂 ——
    ///     挂在 ApplyRuntimeState 里，和 IsInGame / 跟随关系同批处理。★
    ///
    /// 普通卫兵：什么都不加（保持 Dead），但死了要**从名册里摘掉** ——
    ///   否则尸体会一直留在 CrossSceneState 里，占着名额、还会被过图逻辑反复处理。
    ///
    /// ★ 存档 ★ 只调 Retain（运行时计数器）和名册摘除，不写任何 [JsonProperty]。
    /// </summary>
    public sealed class DeathRules : IUnitDeathHandler, ISubscriber
    {
        private static DeathRules _instance;

        public static void Subscribe()
        {
            if (_instance != null) return;
            try
            {
                _instance = new DeathRules();
                EventBus.Subscribe(_instance);
                Main.Log("[死亡规则] 已订阅。普通卫兵永久死亡（原版行为），精英倒地可救。");
            }
            catch (Exception e) { _instance = null; Main.LogError("[死亡规则] 订阅失败: " + e); }
        }

        public static void Unsubscribe()
        {
            if (_instance == null) return;
            try { EventBus.Unsubscribe(_instance); }
            catch (Exception e) { Main.LogError("[死亡规则] 退订失败: " + e); }
            finally { _instance = null; }
        }

        /// <summary>
        /// 给一名卫兵挂上/取消倒地豁免。ApplyRuntimeState 每次过图都会调。
        /// 幂等：Retain 是计数器，重复调会一直加，所以先看 Value 再决定。
        /// </summary>
        public static void ApplyLifeRule(BaseUnitEntity g, bool isElite)
        {
            if (g == null) return;
            try
            {
                if (Main.Settings == null || !Main.Settings.EliteCanBeDowned) return;
                var f = g.Features;
                if (f == null || f.UnconsciousOnZeroHealth == null) return;

                bool has = f.UnconsciousOnZeroHealth.Value;
                if (isElite && !has)
                {
                    f.UnconsciousOnZeroHealth.Retain();
                    Main.Log("[死亡规则] " + Name(g) + " 获得倒地豁免（精英，0 血时进入昏迷而非死亡）");
                }
                else if (!isElite && has)
                {
                    // 普通卫兵不该有 —— 只有我们自己会加，所以能安全释放
                    f.UnconsciousOnZeroHealth.Release();
                    Main.Log("[死亡规则] " + Name(g) + " 取消倒地豁免（普通卫兵按永久死亡）");
                }
            }
            catch (Exception e) { Main.LogError("[死亡规则] 挂载失败: " + e.Message); }
        }

        // ---------- IUnitDeathHandler ----------

        public void HandleUnitDeath(AbstractUnitEntity unitEntity)
        {
            try
            {
                if (!Main.Enabled) return;
                var g = unitEntity as BaseUnitEntity;
                if (g == null || !RetinueRegistry.IsGuard(g)) return;

                int ai = RetinueRegistry.ArchetypeOf(g);
                var arch = Archetypes.Get(ai >= 0 ? ai : 0);
                bool isElite = false;
                try { isElite = GearTool.EliteDefOf(g, arch) != null; } catch { }

                if (isElite)
                {
                    // 精英走倒地流程，这里不该被调到（除非豁免没挂上或被 ScriptedKill 强杀）。
                    // 报一条，方便发现"以为开了倒地其实没生效"。
                    Main.LogError("[死亡规则] ★精英 " + Name(g) + " 真的死了★ —— "
                                  + "倒地豁免没生效或被剧本强杀。它已从名册移除，"
                                  + "如果这不是预期，请把这条连同上下文发给作者。");
                }
                else
                {
                    Main.Log("[死亡规则] " + Name(g) + " 阵亡（普通卫兵永久死亡），已从名册移除。");
                }

                // 两种情况都要摘名册：尸体留在 CrossSceneState 里会占名额、
                // 还会被每次过图的重建逻辑反复处理。
                try { RetinueRegistry.RemoveOne(g); }
                catch (Exception e) { Main.LogError("[死亡规则] 摘名册失败: " + e.Message); }
            }
            catch (Exception e) { Main.LogError("[死亡规则] HandleUnitDeath: " + e); }
        }

        private static string Name(BaseUnitEntity g)
        {
            try
            {
                var d = g.GetOptional<Kingmaker.UnitLogic.Parts.PartUnitDescription>();
                if (d != null && !string.IsNullOrEmpty(d.CustomName)) return d.CustomName;
            }
            catch { }
            try { return g.Blueprint != null ? g.Blueprint.CharacterName : "?"; }
            catch { return "?"; }
        }
    }
}
