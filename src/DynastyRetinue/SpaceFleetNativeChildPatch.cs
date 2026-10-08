using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Mechanics.Actions;

namespace DynastyRetinue
{
    /// <summary>
    /// Keep a fleet carrier's original launch/return/fuel mechanics. Ownership must be
    /// marked before vanilla Faction.Set, whose synchronous inventory event otherwise
    /// transfers the NPC craft's equipment into the player's shared inventory.
    /// Runs only when vanilla spawns a child ship; no update or scene polling.
    /// </summary>
    [HarmonyPatch(typeof(WarhammerContextActionSpawnChildStarship),
        nameof(WarhammerContextActionSpawnChildStarship.SpawnStarship))]
    internal static class SpaceFleetNativeChildPatch
    {
        internal static bool IsReady()
        {
            var target = AccessTools.Method(typeof(WarhammerContextActionSpawnChildStarship),
                nameof(WarhammerContextActionSpawnChildStarship.SpawnStarship));
            var info = target != null ? Harmony.GetPatchInfo(target) : null;
            if (info == null) return false;
            bool beforeFaction = false, afterSpawn = false;
            foreach (var patch in info.Transpilers)
                beforeFaction |= patch.PatchMethod.DeclaringType == typeof(SpaceFleetNativeChildPatch);
            foreach (var patch in info.Postfixes)
                afterSpawn |= patch.PatchMethod.DeclaringType == typeof(SpaceFleetNativeChildPatch);
            return beforeFaction && afterSpawn;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int found = 0;
            foreach (var instruction in code)
                if (IsUnitSpawn(instruction)) found++;
            if (found != 1)
                throw new InvalidOperationException("Native child ship SpawnUnit call count changed: " + found);
            var mark = AccessTools.Method(typeof(SpaceEscortService),
                nameof(SpaceEscortService.MarkNativeChildBeforeFaction));
            foreach (var instruction in code)
            {
                yield return instruction;
                if (!IsUnitSpawn(instruction)) continue;
                // The returned child remains on the stack for vanilla's original stloc.
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Ldarg_3); // original caster
                yield return new CodeInstruction(OpCodes.Call, mark);
            }
        }

        private static bool IsUnitSpawn(CodeInstruction instruction)
        {
            var method = instruction.operand as MethodInfo;
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && method != null && method.Name == "SpawnUnit"
                && method.ReturnType == typeof(BaseUnitEntity);
        }

        private static void Postfix(BaseUnitEntity __result)
        {
            var child = __result as StarshipEntity;
            if (!Main.Enabled || !SpaceEscortService.IsNativeFleetChild(child)) return;
            SpaceEscortService.RestoreNativeChildControl(child, true);
        }
    }
}
