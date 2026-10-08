using System;
using System.Collections.Generic;
using System.Globalization;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.UnitLogic.Parts;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>User-triggered deployment of existing guards. No entity creation, per-frame polling,
    /// new blueprint or shared inventory transfer. Co-op edits use the existing game command queue.</summary>
    internal static class GuardReserve
    {
        internal static Dictionary<string, object> Values
        { get { return Game.Instance != null && Game.Instance.State != null
            && Game.Instance.State.InGameSettings != null ? Game.Instance.State.InGameSettings.List : null; } }
        internal static string Snapshot { get { return GuardReserveStore.Snapshot(Values); } }
        internal static bool IsReserved(BaseUnitEntity guard)
        { return guard != null && GuardReserveStore.Contains(Values, guard.UniqueId); }

        internal static bool CanRequest(out string reason)
        {
            if (!Ready(out reason)) return false;
            if (LoadingProcess.Instance == null || LoadingProcess.Instance.IsLoadingInProcess)
            { reason = L.T("请等待地图加载完成。"); return false; }
            bool shared;
            if (!CoopState.TryGetSharedGameplayRequired(out shared) || (shared && !CoopState.IsConfirmedHost))
            { reason = L.T("合作模式只有房主可以切换卫兵出战状态。"); return false; }
            return true;
        }

        private static bool Ready(out string reason)
        {
            reason = "";
            var game = Game.Instance;
            if (!Main.Enabled || game == null || game.Player == null || game.Player.MainCharacterEntity == null
                || Main.Settings == null || Values == null)
            { reason = L.T("请先载入游戏。"); return false; }
            if (game.Player.IsInCombat || (game.TurnController != null && game.TurnController.InCombat))
            { reason = L.T("战斗中不能切换卫兵留守或出战。"); return false; }
            if (!GuardReserveStore.Valid(Values))
            { reason = L.T("当前存档的留守记录无法读取，未修改卫兵状态。"); return false; }
            return true;
        }

        internal static bool Request(string uid, bool reserved, out string result)
        {
            if (!CanRequest(out result)) return false;
            var roster = RetinueRegistry.All();
            roster.Sort((a, b) => string.CompareOrdinal(a.UniqueId, b.UniqueId));
            var targets = new List<BaseUnitEntity>();
            foreach (var guard in roster)
                if ((uid == null || guard.UniqueId == uid) && IsReserved(guard) != reserved)
                    targets.Add(guard);
            if (targets.Count == 0)
            { result = L.T("没有需要切换状态的卫兵。"); return false; }
            foreach (var guard in targets)
                if (!CanChange(guard))
                { result = L.T("卫兵仍在战斗、倒地或处理中，无法切换状态。"); return false; }
            List<Vector3> positions;
            if (!RetinueLifecycle.TryPlanDeployment(targets, reserved, out positions))
            { result = L.T("主角附近没有足够的出战位置，请移动到开阔处再试。"); return false; }
            var payload = new List<string> { Game.Instance.Player.GameId,
                RetinueLifecycle.CurrentAreaId(), reserved ? "1" : "0", targets.Count.ToString(CultureInfo.InvariantCulture),
                Snapshot };
            var anchor = Game.Instance.Player.MainCharacterEntity.Position;
            payload.Add(anchor.x.ToString("R", CultureInfo.InvariantCulture));
            payload.Add(anchor.y.ToString("R", CultureInfo.InvariantCulture));
            payload.Add(anchor.z.ToString("R", CultureInfo.InvariantCulture));
            for (int i = 0; i < targets.Count; i++)
            {
                payload.Add(targets[i].UniqueId);
                payload.Add(positions[i].x.ToString("R", CultureInfo.InvariantCulture));
                payload.Add(positions[i].y.ToString("R", CultureInfo.InvariantCulture));
                payload.Add(positions[i].z.ToString("R", CultureInfo.InvariantCulture));
            }
            payload.AddRange(CoopSettings.Capture());
            if (!CoopCommand.Send("guardreserve", payload.ToArray()))
            { result = L.T("卫兵状态指令未能入队，请稍后重试。"); return false; }
            result = L.T("已提交卫兵状态切换。");
            return true;
        }

        private static bool CanChange(BaseUnitEntity guard)
        { return guard != null && !guard.IsDisposed && !guard.WillBeDestroyed && !guard.IsInCombat
            && guard.LifeState != null && guard.LifeState.IsConscious; }

        internal static void Execute(string[] args)
        {
            string result;
            if (!Ready(out result)) { Reply(result); return; }
            int count;
            if (args == null || args.Length < 8 || (args[2] != "0" && args[2] != "1")
                || !int.TryParse(args[3], out count) || count < 1 || count > 99
                || args.Length < 8 + count * 4
                || args[0] != Game.Instance.Player.GameId || args[1] != RetinueLifecycle.CurrentAreaId())
            { Reply(L.T("卫兵状态指令已过期或无效。")); return; }
            float anchorX, anchorY, anchorZ;
            if (!string.Equals(args[4], Snapshot, StringComparison.Ordinal)
                || !Number(args[5], out anchorX) || !Number(args[6], out anchorY) || !Number(args[7], out anchorZ)
                || (Game.Instance.Player.MainCharacterEntity.Position - new Vector3(anchorX, anchorY, anchorZ)).sqrMagnitude > 0.01f)
            { Reply(L.T("卫兵状态指令已过期或无效。")); return; }
            bool reserved = args[2] == "1";
            var roster = RetinueRegistry.All();
            var byId = new Dictionary<string, BaseUnitEntity>(StringComparer.Ordinal);
            foreach (var guard in roster) byId[guard.UniqueId] = guard;
            var targets = new List<BaseUnitEntity>();
            var positions = new List<Vector3>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int at = 8 + i * 4;
                BaseUnitEntity guard;
                float x, y, z;
                if (!ids.Add(args[at]) || !byId.TryGetValue(args[at], out guard) || !CanChange(guard)
                    || !Number(args[at + 1], out x) || !Number(args[at + 2], out y) || !Number(args[at + 3], out z))
                { Reply(L.T("卫兵仍在战斗、倒地或处理中，无法切换状态。")); return; }
                targets.Add(guard); positions.Add(new Vector3(x, y, z));
            }
            Dictionary<string, object> saved;
            if (!CoopSettings.TryApplyExact(args, 8 + count * 4, out saved, out result))
            { Reply(L.T("卫兵状态指令已过期或无效。")); return; }
            Func<string> reply = null;
            try
            {
                if (reserved)
                    foreach (var guard in targets)
                        if (guard.Commands != null && !guard.Commands.InterruptAllInterruptible())
                        { reply = () => L.T("卫兵仍在战斗、倒地或处理中，无法切换状态。"); return; }
                if (!GuardReserveStore.Set(Values, ids, reserved, byId.Keys))
                { reply = () => L.T("当前存档的留守记录无法读取，未修改卫兵状态。"); return; }
                // Cache must reflect deployment before IsInGame triggers any view/animation events.
                AnimFallback.RebuildMeleeEliteRoster(roster, RetinueLifecycle.InPartyArea());
                for (int i = 0; i < targets.Count; i++)
                {
                    var guard = targets[i];
                    if (reserved || !RetinueLifecycle.InPartyArea()) Hide(guard);
                    else
                    {
                        guard.Position = positions[i]; // Place first; never activate at the old map position.
                        RetinueTest.ApplyRuntimeState(guard, Game.Instance.Player.MainCharacterEntity, true);
                    }
                }
                StuckWatch.Reset();
                reply = () => reserved ? L.F("已让 {0} 名卫兵留守。", targets.Count)
                    : L.F("已让 {0} 名卫兵出战；非地面区域将在下次登陆时归队。", targets.Count);
            }
            catch (Exception e)
            {
                Main.LogError("[卫兵留守] 切换运行时状态失败: " + e);
                reply = () => L.T("卫兵状态恢复未完成，请保存后重新读档。");
            }
            finally
            {
                CoopSettings.Restore(saved);
                if (reply != null) Reply(reply()); // UI language remains local after the host settings scope.
            }
        }

        private static bool Number(string value, out float number)
        { return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && !float.IsNaN(number) && !float.IsInfinity(number); }

        internal static void Hide(BaseUnitEntity guard)
        {
            if (guard.Commands != null && !guard.Commands.InterruptAllInterruptible())
                throw new InvalidOperationException("Guard still has a non-interruptible command");
            guard.Remove<UnitPartFollowUnit>(); // OnDetach also removes the leader's follower registration.
            if (guard.View != null && guard.View.AgentASP != null) guard.View.AgentASP.Stop();
            guard.IsInGame = false;
        }

        private static void Reply(string text)
        {
            Main.Log("[卫兵留守] " + text);
            UI.RetinueUI.ShowReserveResult(text);
        }
    }
}
