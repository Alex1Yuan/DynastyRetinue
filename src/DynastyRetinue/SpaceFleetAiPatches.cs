using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.AI.BehaviourTrees;
using Kingmaker.Blueprints;
using Kingmaker.Code.Enums.Helper;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.SpaceCombat.StarshipLogic.Parts;
using Kingmaker.EntitySystem.Properties;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.Utility;
using Warhammer.SpaceCombat.AI;
using Warhammer.SpaceCombat.AI.BehaviourTrees;
using Warhammer.SpaceCombat.StarshipLogic.Weapon;

namespace DynastyRetinue
{
    internal static class SpaceFleetAiProfiles
    {
        private const string SideMacroBrain = "c534f2c31ac94ac8adb0409e42674a16";
        private const string SideMacroAbility = "02ea1871911548849faa21e57eb8a928";
        private const string DorsalMacroBrain = "f0a8dca0b40b4ac99ca4a2027e0e0b88";
        private const string DorsalMacroAbility = "78a2a587f1f54b9e8d61caccc1007c97";
        private const string ProwLanceBrain = "11dcfb2c30974dfdb0cb9bf6bf49717c";
        private const string ProwLanceAbility = "cb4b4a0864f64db2a7ba8e8f6353526d";
        private const string DorsalLanceBrain = "954cee2535994732aab39aff83e0cf7a";
        private const string DorsalLanceAbility = "849c9fb22efb46c6b30611302aab9ce7";

        private static readonly Dictionary<string, AbilitySettings> Donors
            = new Dictionary<string, AbilitySettings>(StringComparer.Ordinal);
        private static readonly Dictionary<string, AbilitySettings> Overlays
            = new Dictionary<string, AbilitySettings>(StringComparer.Ordinal);
        private static bool _loaded;

        internal static AbilitySettings Get(PartUnitBrain brain, BlueprintAbility ability)
        {
            if (brain == null || ability == null) return null;
            var ship = brain.Unit as StarshipEntity;
            SpaceFleetRuntimeProfile profile;
            if (!SpaceEscortService.TryGetRuntimeProfile(ship, out profile)) return null;
            string role;
            return profile.TryGetRole(ability.AssetGuid.ToString(), out role)
                ? Overlay(role, ability) : null;
        }

        internal static bool Validate(string role, BlueprintAbility ability,
            out string failure)
        {
            failure = "";
            var setting = Overlay(role, ability);
            if (setting == null)
            { failure = "AI role donor 不可用"; return false; }
            if (setting.AbilityValue == null)
            { failure = "AI role donor 缺少 AbilityValue"; return false; }
            if (setting.AbilitySource != null)
            { failure = "AI overlay 错误继承了 donor AbilitySource"; return false; }
            return true;
        }

        private static AbilitySettings Overlay(string role, BlueprintAbility ability)
        {
            if (string.IsNullOrEmpty(role) || ability == null) return null;
            EnsureLoaded();
            string key = role + ":" + ability.AssetGuid;
            AbilitySettings result;
            if (Overlays.TryGetValue(key, out result)) return result;
            AbilitySettings donor;
            if (!Donors.TryGetValue(role, out donor) || donor == null) return null;
            result = new AbilitySettings
            {
                AbilityValue = donor.AbilityValue,
                AbilityCastSpot = donor.AbilityCastSpot,
                OptimumDistance = donor.OptimumDistance
            };
            Overlays[key] = result;
            return result;
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Donors["macro-side"] = FindSetting(SideMacroBrain, SideMacroAbility);
            Donors["macro-dorsal"] = FindSetting(DorsalMacroBrain, DorsalMacroAbility);
            Donors["lance-prow"] = FindSetting(ProwLanceBrain, ProwLanceAbility);
            Donors["lance-dorsal"] = FindSetting(DorsalLanceBrain, DorsalLanceAbility);
        }

        private static AbilitySettings FindSetting(string brainGuid, string abilityGuid)
        {
            var brain = ResourcesLibrary.TryGetBlueprint<BlueprintStarshipBrain>(brainGuid);
            var ability = ResourcesLibrary.TryGetBlueprint<BlueprintAbility>(abilityGuid);
            return brain != null && ability != null ? brain.GetAbilitySettings(ability) : null;
        }
    }

    [HarmonyPatch(typeof(PartUnitBrain), nameof(PartUnitBrain.GetAbilityValue))]
    internal static class SpaceFleetAbilityValuePatch
    {
        private static void Postfix(PartUnitBrain __instance, AbilityData ability,
            MechanicEntity target, ref int __result)
        {
            try
            {
                var setting = SpaceFleetAiProfiles.Get(__instance,
                    ability != null ? ability.Blueprint : null);
                if (setting == null || setting.AbilityValue == null) return;
                __result = setting.AbilityValue.GetValue(
                    new PropertyContext(__instance.Unit, null, target));
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(PartUnitBrainStarshipExtension),
        nameof(PartUnitBrainStarshipExtension.GetStarshipAbilitySettings))]
    internal static class SpaceFleetAbilitySettingsPatch
    {
        private static void Postfix(PartUnitBrain brain, BlueprintAbility ability,
            ref AbilitySettings __result)
        {
            try
            {
                var setting = SpaceFleetAiProfiles.Get(brain, ability);
                if (setting != null) __result = setting;
            }
            catch { }
        }
    }

    /// <summary>
    /// 原版轨迹搜索只考虑满足最低航程后的停靠点；所有候选分数不大于 0 时直接
    /// Failure，行为树会跳过舰炮规划，即使当前站位仍有合法目标也会整回合空过。
    /// 仅对本 mod 僚舰，在当前位置至少一门普通舰炮可用且缓存评分大于 0 时，复用
    /// 原版“被堵住”分支的当前位置单节点路径，让后续原版节点自行规划和执行开火。
    /// </summary>
    [HarmonyPatch(typeof(TaskNodeFindBestTrajectory), "TickInternal")]
    internal static class SpaceFleetFireInPlacePatch
    {
        private static void Postfix(Blackboard blackboard, ref Status __result)
        {
            try
            {
                if (__result != Status.Failure || blackboard == null) return;
                var context = blackboard.DecisionContext as SpaceCombatDecisionContext;
                var ship = context != null ? context.Unit as StarshipEntity : null;
                SpaceFleetRuntimeProfile profile;
                if (ship == null || context.UnitNode == null
                    || context.AbilityValueCache == null
                    || !SpaceEscortService.TryGetRuntimeProfile(ship, out profile)) return;
                var current = new ShipPath.DirectionalPathNode
                {
                    node = context.UnitNode,
                    direction = CustomGraphHelper.GuessDirection(ship.Forward),
                    canStand = true
                };
                bool canFire = false;
                foreach (var ability in ship.Abilities.RawFacts)
                {
                    var weapon = ability != null
                        ? ability.SourceItem as ItemEntityStarshipWeapon : null;
                    if (weapon == null || weapon.WeaponSlot == null || weapon.Charges <= 0
                        || !ability.Data.IsAvailable
                        || ability.Blueprint.GetComponent<AbilityDeliverStarshipShot>() == null)
                        continue;
                    if (context.AbilityValueCache.GetValue(current, ability) > 0)
                    { canFire = true; break; }
                }
                if (!canFire) return;
                var brain = ship.Brain != null
                    ? ship.Brain.Blueprint as BlueprintStarshipBrain : null;
                float score = Math.Max(0f,
                    brain != null ? brain.TrajectoryScoreMinThreshold : 0f);
                context.BestPathNode = current;
                context.BestPath.Clear();
                context.BestPath.Add(current);
                context.IsBlockedByShip = false;
                context.BestTrajectoryScore = score;
                __result = Status.Success;
                Main.LogVerbose("[海战舰队] 无更优航迹，保留当前位置执行合法舰炮计划："
                    + ship.CharacterName);
            }
            catch (Exception e)
            { Main.LogError("[海战舰队] 原地舰炮回退失败: " + e.Message); }
        }
    }

    /// <summary>
    /// 原版 AI 选舰炮目标时用 TargetWrapper，会接受目标舰任一占位格进入射界；
    /// UnitUseAbilityParams 的命令入口却只检查 Target.Point（目标中心格）。目标跨在
    /// 舷炮边界时，AI 会打印 Cast，命令却在 PartUnitCommands.CanRun 中静默拒绝。
    /// 仅对本 mod 僚舰的普通实体目标舰炮，在原中心点判定失败时改用 AI 同款判定复核。
    /// </summary>
    [HarmonyPatch(typeof(UnitUseAbilityParams), "get_IsDirectionCorrect")]
    internal static class SpaceFleetAbilityDirectionPatch
    {
        private static void Postfix(UnitUseAbilityParams __instance, ref bool __result)
        {
            try
            {
                var targetShip = __instance != null && __instance.Target != null
                    ? __instance.Target.Entity as StarshipEntity : null;
                if (__result || __instance == null || __instance.Ability == null
                    || targetShip == null) return;
                var ship = __instance.Ability.Caster as StarshipEntity;
                var weapon = __instance.Ability.SourceItem as ItemEntityStarshipWeapon;
                var slot = weapon != null ? weapon.WeaponSlot : null;
                SpaceFleetRuntimeProfile profile;
                if (Main.Settings == null || !Main.Settings.ShipArcFix
                    || !SpaceEscortService.TryGetRuntimeProfile(ship, out profile)
                    || slot == null
                    || (slot.Type != Warhammer.SpaceCombat.Blueprints.Slots.WeaponSlotType.Port
                        && slot.Type != Warhammer.SpaceCombat.Blueprints.Slots.WeaponSlotType.Starboard)
                    || __instance.Ability.Blueprint.GetComponent<AbilityDeliverStarshipShot>() == null)
                    return;
                if (!__instance.Ability.IsTargetInsideRestrictedFiringArc(__instance.Target)) return;
                __result = true;
                Main.LogVerbose("[海战舰队] 舰炮方向门禁按完整目标占位放行："
                    + ship.CharacterName + " → " + targetShip.CharacterName);
            }
            catch (Exception e)
            { Main.LogError("[海战舰队] 舰炮方向门禁复核失败: " + e.Message); }
        }
    }

    /// <summary>
    /// 多发舰炮失去目标时，先在原地重选活敌；原地不可打则把尚未执行的计划移到
    /// 剩余航线中第一个合法开火点。不重新寻路，不消耗 Charges，不影响其他炮的计划。
    /// </summary>
    [HarmonyPatch(typeof(TaskNodeDoNextAction), "ChooseTarget")]
    internal static class SpaceFleetExtraShotRetargetPatch
    {
        private static void Postfix(SpaceCombatDecisionContext context, Ability ability,
            ref TargetWrapper __result)
        {
            try
            {
                if (!Main.Enabled || context == null || ability == null
                    || context.CurrentPathNode == null) return;
                var ship = context.Unit as StarshipEntity;
                SpaceFleetRuntimeProfile profile;
                if (!SpaceEscortService.TryGetRuntimeProfile(ship, out profile)) return;
                var weapon = ability.SourceItem as ItemEntityStarshipWeapon;
                var slot = weapon != null ? weapon.WeaponSlot : null;
                if (slot == null || weapon.Charges <= 0) return;
                bool broadside = slot.Type == Warhammer.SpaceCombat.Blueprints.Slots.WeaponSlotType.Port
                    || slot.Type == Warhammer.SpaceCombat.Blueprints.Slots.WeaponSlotType.Starboard;
                if (profile.Shots(broadside) <= 0
                    || ability.Blueprint.GetComponent<AbilityDeliverStarshipShot>() == null) return;
                if (__result != null && IsLiveEnemy(ship, __result.Entity as BaseUnitEntity)) return;
                __result = null;
                if (!ability.Data.IsAvailable) return;

                var best = FindTarget(context, ability, context.CurrentPathNode);
                if (best == null)
                {
                    MoveRemainingPlans(context, ability, weapon);
                    return;
                }
                __result = best;
                Main.LogVerbose("[海战舰队] AI 剩余开火已改换目标：" + ship.CharacterName
                    + " " + slot.Type + "，剩余 charges=" + weapon.Charges);
            }
            catch (Exception e)
            { Main.LogError("[海战舰队] 额外开火改换目标失败: " + e.Message); }
        }

        private static bool IsLiveEnemy(StarshipEntity ship, BaseUnitEntity target)
        {
            return target != null && !target.IsDisposed && !target.WillBeDestroyed
                && target.LifeState.IsConscious && ship.IsEnemy(target);
        }

        private static TargetWrapper FindTarget(SpaceCombatDecisionContext context,
            Ability ability, ShipPath.DirectionalPathNode node)
        {
            var ship = context.Unit as StarshipEntity;
            var brain = ship.Brain;
            var targets = context.Enemies;
            var starshipBrain = brain != null ? brain.Blueprint as BlueprintStarshipBrain : null;
            if (starshipBrain != null) starshipBrain.TryOverrideTargets(context, ref targets);
            int bestValue = 0;
            string bestId = null;
            TargetWrapper best = null;
            foreach (var info in targets)
            {
                var target = info != null ? info.Entity as BaseUnitEntity : null;
                if (!IsLiveEnemy(ship, target)) continue;
                var candidate = new TargetWrapper(target);
                if (!ability.Data.CanTargetFromNode(node.node, null, candidate,
                    out var _, out var _, node.direction)
                    || !ability.Data.IsTargetInsideRestrictedFiringArc(candidate,
                        node.node, node.direction)) continue;
                int value = brain.GetAbilityValue(ability.Data, target);
                if (value <= 0) continue;
                string id = target.UniqueId ?? "";
                if (value < bestValue || (value == bestValue && bestId != null
                    && string.CompareOrdinal(id, bestId) >= 0)) continue;
                bestValue = value;
                bestId = id;
                best = candidate;
            }
            return best;
        }

        private static void MoveRemainingPlans(SpaceCombatDecisionContext context,
            Ability ability, ItemEntityStarshipWeapon weapon)
        {
            var path = context.BestPath;
            var plans = context.PathNodesWithAbilities;
            int currentIndex = path.IndexOf(context.CurrentPathNode);
            if (currentIndex < 0) return;
            // GetAbilityToCast 已取出当前失败项；它的 Charges 尚未被消费。
            int attempts = 1;
            int reserved = 0;
            foreach (var pair in plans)
                if (pair.Value != null)
                    foreach (var pending in pair.Value)
                        if (pending != null && ReferenceEquals(pending.SourceItem, weapon))
                        {
                            if (ReferenceEquals(pair.Key, context.CurrentPathNode)) attempts++;
                            else reserved++;
                        }
            int count = Math.Min(attempts, Math.Max(0, weapon.Charges - reserved));
            ShipPath.DirectionalPathNode destination = null;
            if (count > 0)
                for (int i = currentIndex + 1; i < path.Count; i++)
                {
                    var candidate = path[i];
                    if (candidate == null || !candidate.canStand) break;
                    if (FindTarget(context, ability, candidate) == null) continue;
                    destination = candidate;
                    break;
                }

            // 先完整准备替换列表，再提交；同门炮多种能力共享真实 Charges 上限。
            List<Ability> remaining = null;
            List<Ability> current;
            if (plans.TryGetValue(context.CurrentPathNode, out current) && current != null)
            {
                remaining = new List<Ability>(current.Count);
                foreach (var pending in current)
                    if (pending == null || !ReferenceEquals(pending.SourceItem, weapon))
                        remaining.Add(pending);
            }
            if (destination != null)
            {
                List<Ability> existing;
                plans.TryGetValue(destination, out existing);
                var next = existing != null ? new List<Ability>(existing) : new List<Ability>();
                for (int i = 0; i < count; i++) next.Add(ability);
                plans[destination] = next;
            }
            if (remaining != null && remaining.Count > 0)
                plans[context.CurrentPathNode] = remaining;
            else plans.Remove(context.CurrentPathNode);
            if (destination != null)
                Main.LogVerbose("[海战舰队] 剩余舰炮计划移至后续航点：" + context.Unit.CharacterName
                    + " " + weapon.WeaponSlot.Type + "，保留 " + count + " 次，"
                    + context.CurrentPathNode + " → " + destination);
        }
    }

    /// <summary>
    /// 原版海战 AI 每回合只把每个 Ability 规划一次，即使其舰炮还有多次 Charges。
    /// 只在本 mod 僚舰完成原版回合规划后，按该物理舰炮的逐舰 bonus 复制计划项。
    /// 这是每舰每回合、最多数个能力的有界操作，不进入逐帧路径。
    /// </summary>
    [HarmonyPatch(typeof(TaskNodeFindWhenToCastAbility), "TickInternal")]
    internal static class SpaceFleetExtraShotPlanPatch
    {
        private static void Postfix(Blackboard blackboard)
        {
            try
            {
                var context = blackboard != null
                    ? blackboard.DecisionContext as SpaceCombatDecisionContext : null;
                var ship = context != null ? context.Unit as StarshipEntity : null;
                SpaceFleetRuntimeProfile profile;
                if (!SpaceEscortService.TryGetRuntimeProfile(ship, out profile)
                    || context.PathNodesWithAbilities == null
                    || context.PathNodesWithAbilities.Count == 0) return;

                var firstPlans = new Dictionary<ItemEntityStarshipWeapon,
                    KeyValuePair<Ability, List<Ability>>>();
                var plannedCounts = new Dictionary<ItemEntityStarshipWeapon, int>();
                foreach (var pair in context.PathNodesWithAbilities)
                {
                    var planned = pair.Value;
                    if (planned == null) continue;
                    for (int i = 0; i < planned.Count; i++)
                    {
                        var ability = planned[i];
                        var weapon = ability != null
                            ? ability.SourceItem as ItemEntityStarshipWeapon : null;
                        if (weapon == null) continue;
                        int count;
                        plannedCounts.TryGetValue(weapon, out count);
                        plannedCounts[weapon] = count + 1;
                        if (!firstPlans.ContainsKey(weapon))
                            firstPlans[weapon] = new KeyValuePair<Ability, List<Ability>>(
                                ability, planned);
                    }
                }

                foreach (var pair in firstPlans)
                {
                    var weapon = pair.Key;
                    var slot = weapon != null ? weapon.WeaponSlot : null;
                    int alreadyPlanned;
                    plannedCounts.TryGetValue(weapon, out alreadyPlanned);
                    if (slot == null || weapon.Charges <= alreadyPlanned) continue;
                    bool broadside = slot.Type == Warhammer.SpaceCombat.Blueprints.Slots.WeaponSlotType.Port
                        || slot.Type == Warhammer.SpaceCombat.Blueprints.Slots.WeaponSlotType.Starboard;
                    int copies = Math.Min(profile.Shots(broadside),
                        weapon.Charges - alreadyPlanned);
                    for (int k = 0; k < copies; k++)
                        pair.Value.Value.Add(pair.Value.Key);
                    if (copies > 0)
                        Main.LogVerbose("[海战舰队] AI 额外开火计划：" + ship.CharacterName
                            + " " + slot.Type + " charges=" + weapon.Charges
                            + "，原计划 " + alreadyPlanned + " 次，追加 " + copies + " 次");
                }
            }
            catch (Exception e)
            { Main.LogError("[海战舰队] 额外开火 AI 计划扩展失败: " + e.Message); }
        }
    }
}
