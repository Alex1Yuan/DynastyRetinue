using System;
using System.Globalization;
using System.Text;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Enums;
using Kingmaker.UnitLogic.Commands;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.UnitLogic.FactLogic;

namespace DynastyRetinue
{
    /// <summary>
    /// 诊断包里的只读同步/回合快照。不挂 Harmony、不修改任何状态。
    /// 重点补两个历史盲区：联机报告只说“mod 不一致”却不列清单，
    /// 以及 Player.IsInCombat 已 false 时看不到 TurnController 是否仍在等 busy。
    /// </summary>
    internal static class DiagnosticSnapshot
    {
        public static void Append(StringBuilder sb)
        {
            if (sb == null) return;
            sb.AppendLine("-------- 联机 / 回合快照 --------");
            AppendNetwork(sb);
            AppendTurn(sb);
            sb.AppendLine();
        }

        private static void AppendNetwork(StringBuilder sb)
        {
            try
            {
                int tick = -1;
                try { tick = Game.Instance.RealTimeController.CurrentNetworkTick; } catch { }
                sb.AppendLine("network tick : " + tick);
                sb.AppendLine("最近发出命令 : " + CoopCommand.LastSent);
                sb.AppendLine("最近执行命令 : " + CoopCommand.LastDispatched);
                sb.AppendLine("最近不同步   : " + DesyncLogPatch.LastDesync);
                string diff = CoopSettings.DiffText();
                sb.AppendLine("双方设置核对 : " + (string.IsNullOrEmpty(diff) ? "（本会话尚未通过按钮交换设置）" : diff));
                sb.AppendLine("mod 清单     : " + CoopState.LocalModsText());

                var dlcs = Kingmaker.Networking.PhotonManager.DLC.DLCsInGame;
                var ds = new System.Collections.Generic.List<string>();
                foreach (string id in dlcs) if (!string.IsNullOrEmpty(id)) ds.Add(id);
                sb.AppendLine("会话 DLC     : " + (ds.Count == 0 ? "（无 / 当前不在已冻结的合作会话）" : string.Join(", ", ds.ToArray())));
                sb.AppendLine("机仆共同池   : " + ServitorSummon.CapabilityStatus());
                sb.AppendLine("自动传送事务 : " + CoopAutoTransaction.Status());
            }
            catch (Exception e)
            {
                sb.AppendLine("联机快照     : （读取失败: " + e.Message + "）");
            }
        }

        private static void AppendTurn(StringBuilder sb)
        {
            try
            {
                var game = Game.Instance;
                var player = game != null ? game.Player : null;
                var tc = game != null ? game.TurnController : null;
                sb.AppendLine("游戏模式     : " + (game != null ? game.CurrentMode.ToString() : "?"));
                sb.AppendLine("Player战斗   : " + (player != null ? player.IsInCombat.ToString() : "?"));
                if (tc == null)
                {
                    sb.AppendLine("TurnController: （为空）");
                    return;
                }

                sb.AppendLine("回合控制     : TurnBased=" + tc.TurnBasedModeActive
                            + " InCombat=" + tc.InCombat
                            + " EndingTurn=" + tc.EndingTurn
                            + " EndRequested=" + tc.EndTurnRequested
                            + " Round=" + tc.CombatRound);
                sb.AppendLine("当前单位     : " + UnitLabel(tc.CurrentUnit as BaseUnitEntity));
                AppendGuardMovement(sb, tc.CurrentUnit as BaseUnitEntity);

                int all = 0, combat = 0, busy = 0;
                var detail = new StringBuilder();
                foreach (var entity in tc.AllUnits)
                {
                    all++;
                    if (entity == null || !entity.IsInCombat) continue;
                    combat++;
                    if (!entity.IsBusy) continue;
                    busy++;
                    var u = entity as BaseUnitEntity;
                    detail.AppendLine("    " + UnitLabel(u)
                                    + " IsBusy=true Commands=" + entity.HasRunningOrQueuedCommands
                                    + " Jump=" + entity.IsDoingJumping);
                    if (u != null) AppendCommand(detail, u);
                }
                sb.AppendLine("回合单位     : all=" + all + " inCombat=" + combat + " busy=" + busy);
                if (busy == 0) sb.AppendLine("busy 明细    : （无）");
                else
                {
                    sb.AppendLine("busy 明细    :");
                    sb.Append(detail);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("回合快照     : （读取失败: " + e.Message + "）");
            }
        }

        private static void AppendGuardMovement(StringBuilder sb, BaseUnitEntity current)
        {
            try
            {
                var guards = RetinueRegistry.All();
                if (guards == null || guards.Count == 0)
                {
                    sb.AppendLine("卫兵移动资源 : （无在册卫兵）");
                    return;
                }

                sb.AppendLine("卫兵移动资源 : 初始=实体 WarhammerInitialAPBlue；当前/上限/已花=CombatState；UI 在 CantMove 时强制显示 0");
                foreach (var g in guards)
                {
                    if (g == null) continue;
                    string name = Safe(() => g.CharacterName);
                    string uid = Safe(() => g.UniqueId);
                    string bp = Safe(() => g.Blueprint != null ? g.Blueprint.name : "?");
                    string position = Safe(() => g.Position.x.ToString("R", CultureInfo.InvariantCulture) + ","
                                                + g.Position.y.ToString("R", CultureInfo.InvariantCulture) + ","
                                                + g.Position.z.ToString("R", CultureInfo.InvariantCulture));
                    string initial = "?", modified = "?", currentMp = "?", max = "?", spent = "?", moved = "?";
                    string cantMove = "?", sources = "（无）";
                    try
                    {
                        var cs = g.CombatState;
                        if (cs != null)
                        {
                            var stat = cs.WarhammerInitialAPBlue;
                            if (stat != null)
                            {
                                initial = stat.BaseValue.ToString(CultureInfo.InvariantCulture);
                                modified = stat.ModifiedValue.ToString(CultureInfo.InvariantCulture);
                            }
                            currentMp = cs.ActionPointsBlue.ToString("0.###", CultureInfo.InvariantCulture);
                            max = cs.ActionPointsBlueMax.ToString("0.###", CultureInfo.InvariantCulture);
                            spent = cs.ActionPointsBlueSpentThisTurn.ToString("0.###", CultureInfo.InvariantCulture);
                            moved = cs.MovedCellsThisTurn.ToString("0.###", CultureInfo.InvariantCulture);
                        }
                    }
                    catch { }
                    try
                    {
                        var flag = g.GetMechanicFeature(MechanicsFeatureType.CantMove);
                        if (flag != null)
                        {
                            cantMove = flag.Value + "(" + flag.Count + ")";
                            var names = new System.Collections.Generic.List<string>();
                            foreach (var x in flag.AssociatedBuffs.Buffs)
                            {
                                if (x == null || x.BuffInformation == null) continue;
                                string n = x.BuffInformation.Name;
                                names.Add((string.IsNullOrEmpty(n) ? "?" : n) + "×" + x.Counter);
                            }
                            if (flag.Count > 0 && g.Facts != null)
                            {
                                foreach (var fact in g.Facts.List)
                                {
                                    if (fact == null || fact.Blueprint == null) continue;
                                    var scriptable = fact.Blueprint as Kingmaker.Blueprints.BlueprintScriptableObject;
                                    if (scriptable == null || scriptable.ComponentsArray == null) continue;
                                    foreach (var raw in scriptable.ComponentsArray)
                                    {
                                        var component = raw as AddMechanicsFeature;
                                        if (component == null || component.Feature != MechanicsFeatureType.CantMove) continue;
                                        string factName = fact.Blueprint.name;
                                        string factId = fact.Blueprint.AssetGuid.ToString();
                                        names.Add((string.IsNullOrEmpty(factName) ? "?" : factName) + "[" + factId + "]");
                                    }
                                }
                            }
                            if (names.Count > 0) sources = string.Join(", ", names.ToArray());
                            else if (flag.Count > 0) sources = "（未定位；可能来自 UnitCondition 或运行时 Retain）";
                        }
                    }
                    catch { }

                    sb.AppendLine("    " + name + " uid=" + uid + " bp=" + bp
                                + " pos=" + position
                                + " IsInGame=" + Safe(() => g.IsInGame.ToString())
                                + " currentTurn=" + ReferenceEquals(g, current)
                                + " inCombat=" + Safe(() => g.IsInCombat.ToString())
                                + " 初始=" + initial + " 修正后=" + modified
                                + " 当前=" + currentMp + " 上限=" + max
                                + " 已花=" + spent + " 已走=" + moved
                                + " CantMove=" + cantMove + " 来源=" + sources);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("卫兵移动资源 : （读取失败: " + e.Message + "）");
            }
        }

        private static string UnitLabel(BaseUnitEntity u)
        {
            if (u == null) return "?";
            string name = "?", uid = "?", bp = "?";
            try { name = u.CharacterName ?? "?"; } catch { }
            try { uid = u.UniqueId ?? "?"; } catch { }
            try { bp = u.Blueprint != null ? u.Blueprint.name : "?"; } catch { }
            bool guard = false;
            try { guard = RetinueRegistry.IsGuard(u); } catch { }
            // 不对原版单位调用 GetMechanicFeature：诊断必须严格只读，不应冒险触发惰性部件访问。
            string forceAi = guard
                ? Safe(() => u.GetMechanicFeature(MechanicsFeatureType.ForceAIControl).Value.ToString())
                : "n/a";
            return name + " uid=" + uid + " bp=" + bp
                 + " guard=" + guard + " forceAI=" + forceAi;
        }

        private static void AppendCommand(StringBuilder sb, BaseUnitEntity u)
        {
            try
            {
                var commands = u.Commands;
                var cmd = commands != null ? commands.Current : null;
                int queued = commands != null && commands.Queue != null ? commands.Queue.Count : 0;
                if (cmd == null)
                {
                    sb.AppendLine("      current=(null) queue=" + queued + "（busy 可能来自 AbilityExecutionProcess / Jump）");
                    return;
                }
                sb.AppendLine("      current=" + cmd.GetType().Name
                            + " result=" + cmd.Result
                            + " started=" + cmd.IsStarted
                            + " finished=" + cmd.IsFinished
                            + " interruptible=" + cmd.IsInterruptible
                            + " cutscene=" + cmd.FromCutscene
                            + " keepAfterFight=" + cmd.DoNotInterruptAfterFight
                            + " queue=" + queued);
                var ua = cmd as UnitUseAbility;
                if (ua != null)
                {
                    string ability = "?";
                    try { ability = ua.Ability != null && ua.Ability.Blueprint != null ? ua.Ability.Blueprint.name : "?"; } catch { }
                    string process = "null";
                    try
                    {
                        var p = ua.ExecutionProcess;
                        if (p != null) process = "started=" + p.IsStarted + " ended=" + p.IsEnded + " engage=" + p.IsEngageUnit;
                    }
                    catch { process = "读取失败"; }
                    sb.AppendLine("      ability=" + ability + " process=" + process);
                }
                AppendAnimation(sb, cmd);
            }
            catch (Exception e) { sb.AppendLine("      command 读取失败: " + e.Message); }
        }

        private static void AppendAnimation(StringBuilder sb, AbstractUnitCommand cmd)
        {
            try
            {
                var a = cmd != null ? cmd.Animation : null;
                if (a == null) { sb.AppendLine("      animation=(null)"); return; }
                string action = "?";
                try { action = a.Action != null ? a.Action.GetType().Name + "/" + a.Action.name : "null"; } catch { }
                sb.AppendLine("      animation=" + action
                            + " started=" + a.IsStarted
                            + " finished=" + a.IsFinished
                            + " released=" + a.IsReleased
                            + " skipped=" + a.IsSkipped
                            + " active=" + (a.ActiveAnimation != null)
                            + " act=" + a.ActEventsCounter);
            }
            catch (Exception e) { sb.AppendLine("      animation 读取失败: " + e.Message); }
        }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "?"; } catch { return "?"; }
        }
    }
}
