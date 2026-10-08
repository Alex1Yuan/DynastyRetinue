using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Persistence;
using Kingmaker.Pathfinding;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.UnitLogic;
using Pathfinding;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 区域生命周期。M2 核心。
    ///
    /// 为什么不是「每次过图重新 spawn」——那条路会产生**重复卫兵**：
    /// MainState 过图时被 StashAreaState(dispose:true) 冻存到该区域自己的 json
    /// （SceneLoader.cs:1758），回访时 UnstashAreaState 读回来，卫兵原地复活。
    ///
    /// 卫兵放 Player.CrossSceneState 长期存活，本类只负责把**读档/过图后会丢失的
    /// 运行时状态**重新挂回去。哪些会丢：
    ///   IsInGame ：SceneLoader.cs:1491 卸载时无条件重置为 Party.Contains(...)，卫兵不在队伍 ⇒ false
    ///   机制标志 ：PartMechanicFeatures 整类零 [JsonProperty]，OnPrePostLoad 强制 Initialize()
    ///   跟随关系 ：UnitPartFollowUnit / UnitPartFollowedByUnits 全类零 [JsonProperty]
    /// </summary>
    public sealed class RetinueLifecycle : IAreaHandler, IAreaLoadingStagesHandler, IPartyCombatHandler
    {
        private static RetinueLifecycle _instance;

        /// <summary>
        /// 摆位不能在 OnAreaLoadingComplete 里直接做 —— v0.1.0 的注释把顺序写反了。
        /// 实际顺序（Game.cs:1955-1973）：
        ///     RaiseEvent(OnAreaLoadingComplete)   ← 我们在这里
        ///     UpdateNavMesh()                     ← 导航图这时才 flush
        ///     UnitsPlacer.MovePartyToNavmesh()    ← 队长这时才被挪到最终位置
        /// 在事件里立刻 SnapToGrid，用的是没 flush 的图，且吸附到队长的"移动前"坐标，
        /// 结果是卫兵和队长错位。所以改成打标记，由 Main.OnUpdate 在之后的帧里消费。
        /// </summary>
        private static bool _placementPending;
        private static bool _hidePending;
        private static int _stableSinceTick = -1;
        private static Vector3 _stableLeaderPosition;
        private static string _stableAreaId;
        private const int PlacementStableTicks = 20; // 1 秒同步时间；跨过短暂的加载队列间隙
        private const int MaxSynchronizedGuards = 99; // 与 NoCountCap 下的实际硬上限一致

        public static void Subscribe()
        {
            if (_instance != null) return;
            try
            {
                _instance = new RetinueLifecycle();
                EventBus.Subscribe(_instance);
                Main.LogVerbose("区域生命周期已订阅。");
            }
            catch (Exception e) { _instance = null; Main.LogError("订阅失败: " + e); }
        }

        public static void Unsubscribe()
        {
            if (_instance == null) return;
            try { EventBus.Unsubscribe(_instance); Main.LogVerbose("区域生命周期已退订。"); }
            catch (Exception e) { Main.LogError("退订失败: " + e); }
            finally { _instance = null; ResetPlacementPending(); }
        }

        /// <summary>
        /// 当前是不是「队伍区域」。星系图 / 太空战 / 全局地图都是 IsShipArea，
        /// 原版在那些区域会把 PartyAndPets 全部 IsInGame=false 关灯
        /// （AreaEnterPoint.cs:91-94 + :151-165）。
        /// 但卫兵是 ExCompanion，**不在 PartyAndPets 里**（Player.cs:1364-1367），
        /// 原版的关灯遍历漏掉它们 —— 如果我们再把 IsInGame 置回 true，
        /// 就会出现"太空里飘着几个步兵"，甚至混进太空战的先攻序列。
        /// </summary>
        internal static bool InPartyArea()
        {
            try
            {
                var area = Game.Instance != null ? Game.Instance.CurrentlyLoadedArea : null;
                return area != null && area.IsPartyArea;
            }
            catch { return false; }
        }

        // ---------- IAreaHandler ----------

        public void OnAreaBeginUnloading()
        {
            // 只记账，不销毁 —— 销毁是 Plan B 的做法，Plan A 下卫兵要跟着 CrossSceneState 走
            try { Main.Log("[生命周期] 区域开始卸载，在册卫兵 " + RetinueRegistry.Count + " 名"); }
            catch { }
        }

        public void OnAreaDidLoad()
        {
            // 自定义 uGUI 根是 DontDestroyOnLoad，但它克隆的字体、材质和原版按钮模板
            // 属于旧场景。读档/过图后继续复用会把已销毁或已换代的 TMP 引用带进新区域，
            // 实机表现就是菜单文字重新变成实心块。这里只在区域事件里清一次，不进逐帧路径。
            try { UI.RetinueUI.ResetForAreaLoad(); }
            catch (Exception e) { Main.LogError("[UI] 区域加载清账失败: " + e.Message); }
            // 读档/过图会重建实体入册队列；上一张图的延迟招募保留项必须清账。
            try { UI.RetinueUI.ResetRecruitPending(); } catch { }
            // 自检：只在有 dynasty_selftest.flag 时跑，一次会话一遍，纯只读。
            // 放这里而不是 Main.Load —— 载入时蓝图缓存还没就绪，
            // 那正是 v0.50.0 修的那个坑（早读一次就把分型表钉死在内置默认上）。
            try { SelfCheck.RunOnce(); } catch { }

            if (!Main.Enabled) return;

            // 存档里的 m_CustomPrefabGuid 可能已失效。单机可按本机资源回落；合作中
            // 本机 DLC/bundle 状态不能决定持久字段，否则只有缺资源的一端会清掉船模 GUID。
            if (!CoopState.SharedGameplayRequired)
            {
                try { ShipModelBundleHold.ValidateAndRearm(StarshipViewTool.PlayerShip); }
                catch (Exception e) { Main.LogError("[船模] 区域加载自检失败: " + e.Message); }
            }

            // 合作中 ApplyRuntimeState 会读大量设置并改实体，不能在两端各自的加载事件里跑。
            // 延后到房主发出的 kgd.placeguards，同一 tick 临时套用房主设置后统一恢复。
            if (CoopState.SharedGameplayRequired)
            {
                try
                {
                    var list = RetinueRegistry.All();
                    AnimFallback.RebuildMeleeEliteRoster(list, InPartyArea());
                    ArmPlacement();
                }
                catch (Exception e) { Main.LogError("[生命周期] 合作恢复排队失败: " + e.Message); }
            }
            else RestoreCurrentArea(false);
        }

        /// <summary>
        /// 恢复当前区域内卫兵。区域加载和 mod 中途重新启用必须共用这一套顺序：
        /// 先重建近战精英缓存，再恢复 IsInGame/brain/装备/能力，最后（中途启用时）延迟摆位。
        ///
        /// ★为什么中途启用也必须跑完整套★ 玩家可能在 mod 关闭期间读档/过图，生命周期
        /// 事件因退订而全部错过；只重建缓存会让卫兵仍 IsInGame=false、缺运行时部件，直到下次过图。
        /// </summary>
        internal static void RestoreCurrentArea(bool schedulePlacement)
        {
            try
            {
                var list = RetinueRegistry.All();
                bool partyArea = InPartyArea();
                // 必须在任何 IsInGame / ApplyRuntimeState 之前：后两者会同步触发 View/EventBus。
                AnimFallback.RebuildMeleeEliteRoster(list, partyArea);

                // 底层总闸：无论调用者是读档、过图还是 UMM 重新启用，合作中都不允许
                // 在本机调用栈直接恢复实体。只排队，由确认房主发 placeguards 同步命令。
                if (CoopState.SharedGameplayRequired)
                {
                    ArmPlacement();
                    return;
                }

                if (list.Count == 0)
                {
                    if (schedulePlacement) ResetPlacementPending();
                    return;
                }

                if (!partyArea)
                {
                    foreach (var g in list) { try { g.IsInGame = false; } catch { } }
                    Main.Log("[生命周期] 非队伍区域（星系图/太空战/全局地图），已关灯 " + list.Count + " 名卫兵");
                    ResetPlacementPending();
                    return;
                }

                var leader = Game.Instance != null && Game.Instance.Player != null
                           ? Game.Instance.Player.MainCharacterEntity : null;
                Main.Log("[生命周期] 当前区域恢复 " + list.Count + " 名卫兵的运行时状态");
                foreach (var g in list)
                {
                    try { RetinueTest.ApplyRuntimeState(g, leader); }
                    catch (Exception e) { Main.LogError("重建失败: " + e.Message); }
                }
                if (schedulePlacement) ArmPlacement();
            }
            catch (Exception e) { Main.LogError("RestoreCurrentArea: " + e); }
        }

        // ---------- IPartyCombatHandler ----------

        /// <summary>
        /// 玩家常规队伍退出战斗时，把原版 PartyAndPets 漏掉的 ExCompanion 卫兵一起带走。
        ///
        /// 卫兵通过 UnitPartFollowUnit 跟随主角，但位于独立的 kgd.guard CombatGroup。
        /// 原版跟随 controller 会在主角入战时把该组 IsFollowingUnitInCombat 置 true；
        /// 原版 Player.UpdateIsInCombat 却只统计 PartyAndPets，不统计我们的 ExCompanion。
        /// 六名灵能卫可以因此在敌人全灭后持续互相施放 buff，让 UnitsOrder 永远非空。
        ///
        /// 这里使用原版已经确认的队伍离战下降沿，而不是自己扫描/猜测敌人是否全灭：
        /// 事件只触发一次，不增加逐帧成本，也不会误伤仍在进行的正常战斗。
        /// </summary>
        public void HandlePartyCombatStateChanged(bool inCombat)
        {
            if (inCombat || !Main.Enabled) return;
            try
            {
                var game = Game.Instance;
                var groups = game != null ? game.UnitGroups : null;
                if (groups == null) return;

                int left = 0;
                // 直接走当前 UnitGroups，不调用 RetinueRegistry.All()：离战事件不需要
                // 拷贝 CrossSceneState/MainState 的全部实体，也不会产生名册快照分配。
                for (int i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    string id = null;
                    try { id = group != null ? group.Id : null; } catch { }
                    if (string.IsNullOrEmpty(id)
                        || !id.StartsWith(RetinueRegistry.GuardTag, StringComparison.Ordinal)) continue;

                    group.IsFollowingUnitInCombat = false;
                    // 召唤物会继承施法卫兵的 CombatGroup.Id；一并带离，否则它们仍会
                    // 留在 TurnOrderQueue，战斗还是不能走到原版 ExitTb 清理。
                    for (int j = 0; j < group.Count; j++)
                    {
                        var unit = group[j];
                        try
                        {
                            if (unit == null || unit.CombatState == null || !unit.IsInCombat) continue;
                            unit.CombatState.LeaveCombat();
                            if (!unit.IsInCombat) left++;
                        }
                        catch (Exception e)
                        {
                            Main.LogError("[战斗] 卫兵组单位离战失败 "
                                + (unit != null ? unit.UniqueId : "?") + ": " + e.Message);
                        }
                    }
                }
                if (left > 0)
                    Main.Log("[战斗] 玩家队伍已离战；同步带离 " + left
                           + " 名仍在先攻表中的卫兵/召唤物，避免战后互相施放 buff。");
            }
            catch (Exception e) { Main.LogError("[战斗] 卫兵离战收尾失败: " + e.Message); }
        }

        // ---------- IAreaLoadingStagesHandler ----------

        public void OnAreaScenesLoaded() { }

        /// <summary>只打标记；真正摆位要等所有加载结束且队长位置稳定，再走同步命令。</summary>
        public void OnAreaLoadingComplete()
        {
            if (!Main.Enabled) return;
            ArmPlacement();

            // ★卡住检测必须在这里清账★
            //   StuckWatch 用 Player.RealTime 派生的同步 tick 计时，而那是**存档状态**：
            //   读一个更早的存档，tick 会倒退，它的节流判据就再也过不去（1.5.1 已让它
            //   自愈，这里是第二道）。另外 _rows 按 UniqueId 记坐标，跨区域之后那些
            //   坐标全是上一张图的，留着只会让刚过图的卫兵被误判成「一直没动」。
            try { StuckWatch.Reset(); }
            catch (Exception e) { Main.LogError("[卡住] 区域清账异常: " + e.Message); }

            // ★探针留在原地的展示用候选必须清掉★ 区域实体会进存档，
            //   而作者很可能看完模型就直接过图走人了。这里兜一道。
            if (!CoopState.SharedGameplayRequired)
            {
                try { LegendProbe.ClearShown(); }
                catch (Exception e) { Main.LogError("[传奇探针] 过图清理异常: " + e.Message); }
            }

            // 招募入口是运行时交互、不进存档，所以每次进区域都要重挂一遍。
            // 放在这里而不是 TickPending 里：它不依赖导航图，也不限于队伍区域
            //（船上那些 NPC 所在的区域不一定被 InPartyArea 认作队伍区域）。
            try { RecruitEntry.ResetForNewArea(); RecruitEntry.AttachInArea();
                  RecruitDialog.ResetForNewArea(); RecruitDialog.InjectInArea(); }
            catch (Exception e) { Main.LogError("[招募] 区域挂载异常: " + e.Message); }

            // 这两条都会改实体/装备。合作中不能由各自本机的加载事件决定；
            // 运行时恢复随 placeguards 的房主设置快照执行，机仆由同步 ExitTb 清理。
            if (!CoopState.SharedGameplayRequired)
            {
                try { RefreshGearOnAugmentUnlock(); }
                catch (Exception e) { Main.LogError("[植入物] 层级检查异常: " + e.Message); }
                try { ServitorSummon.CleanupOrphansOutsideCombat(); }
                catch (Exception e) { Main.LogError("[召唤机仆] 区域清理异常: " + e.Message); }
            }
        }

        /// <summary>
        /// 植入物层级解锁后，给**已有**卫兵补发装备。
        ///
        /// 为什么需要：装备只在招募时发一次。植入物在 archetypes.json 里写成候选链
        /// "MK2|MK1"，GearTool 会依次试到能装上为止 —— 这对**解锁之后新招**的卫兵够用，
        /// 但早期招的那批已经穿着 MK-I 了，不会自己升级。
        ///
        /// 门在哪：EquipmentRestrictionAugmentTier.CanBeEquippedBy(MechanicEntity _) 把参数丢掉，
        /// 直接问 Game.Instance.Player.PartyAugmentManager.CanEquipAugment(tier)，
        /// 而 CanEquipAugment(t) => t &lt;= m_CurrentAvailableTier。
        /// 所以这是**队伍全局**的剧情门（DLC3 无限缪斯博物馆那条线推进的），不是针对卫兵的。
        /// 我们只读 CurrentAvailableTier，不动那个限制 —— 豁免等于给玩家自己开后门。
        ///
        /// 为什么只在**层级变化时**重发，而不是每次进区域都发：
        /// 重发会覆盖玩家手动改过的装备（第二阶段的装配界面）。层级一局里最多变两次
        /// （None -> Tier1 -> Tier2），代价近乎零。
        /// </summary>
        private static void RefreshGearOnAugmentUnlock()
        {
            if (!Main.Enabled || Main.Settings == null) return;

            int cur;
            try
            {
                var pam = Game.Instance != null && Game.Instance.Player != null
                        ? Game.Instance.Player.PartyAugmentManager : null;
                if (pam == null) return;
                cur = (int)pam.CurrentAvailableTier;
            }
            catch { return; }

            if (cur == Main.Settings.LastAugmentTier) return;

            int prev = Main.Settings.LastAugmentTier;
            Main.Settings.LastAugmentTier = cur;

            // 首次运行（-1）只记录不重发：那不是"解锁了"，只是我们第一次看到。
            if (prev < 0) { Main.Log("[植入物] 当前层级 Tier" + cur + "（首次记录，不重发装备）"); return; }
            if (cur < prev) { Main.Log("[植入物] 层级回退 " + prev + " -> " + cur + "（多半是读了旧档），不重发。"); return; }

            var list = RetinueRegistry.All();
            if (list == null || list.Count == 0)
            { Main.Log("[植入物] 层级 " + prev + " -> " + cur + "，但没有在册卫兵。"); return; }

            Main.Log("[植入物] 层级解锁 " + prev + " -> " + cur + "，给 " + list.Count + " 名已有卫兵补发装备……");
            int upgraded = 0;
            foreach (var g in list)
            {
                try
                {
                    int ai = RetinueRegistry.ArchetypeOf(g);
                    var arch = Archetypes.Get(ai >= 0 ? ai : Main.Settings.ArchetypeIndex);
                    if (arch == null) continue;
                    // Equip 自带幂等：已经穿着的候选会被跳过，只有真能升级的那格会动
                    int n = GearTool.Equip(g, arch);
                    if (n > 0) upgraded++;
                }
                catch (Exception e) { Main.LogError("[植入物] 补发失败: " + e.Message); }
            }
            Main.Log("[植入物] 补发完成，" + upgraded + " 名卫兵有装备变化。");
        }

        private sealed class Placement
        {
            public BaseUnitEntity Unit;
            public string Uid;
            public Vector3 Position;
        }

        internal static void RearmPlacement() { ArmPlacement(); }
        internal static void CancelPendingPlacement() { ResetPlacementPending(); }

        internal static bool TryPlanDeployment(List<BaseUnitEntity> guards, bool reserved,
            out List<Vector3> positions)
        {
            positions = new List<Vector3>(guards.Count);
            var occupied = new HashSet<GraphNode>();
            var leader = Game.Instance.Player.MainCharacterEntity;
            foreach (var guard in guards)
            {
                Vector3 position = guard.Position;
                if (!reserved && InPartyArea()
                    && !TryPlanPosition(guard, leader.Position, occupied, out position)) return false;
                positions.Add(position);
            }
            return true;
        }

        private static void ArmPlacement()
        {
            _placementPending = InPartyArea();
            _hidePending = !_placementPending;
            _stableSinceTick = -1;
            _stableAreaId = CurrentAreaId();
        }

        private static void ResetPlacementPending()
        {
            _placementPending = false;
            _hidePending = false;
            _stableSinceTick = -1;
            _stableAreaId = null;
            _stableLeaderPosition = default(Vector3);
        }

        internal static string CurrentAreaId()
        {
            try
            {
                var area = Game.Instance != null ? Game.Instance.CurrentlyLoadedArea : null;
                return area != null ? area.AssetGuid.ToString() : "";
            }
            catch { return ""; }
        }

        private static string F(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool TryParseFloat(string value, out float result)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        /// <summary>
        /// 房主只读规划最终合法节点。返回的是 node.Vector3Position，随后原样写入同步载荷；
        /// 副机不再跑 SnapToGrid 或任何本地 navmesh 选择。
        /// </summary>
        internal static bool TryPlanPosition(BaseUnitEntity unit, Vector3 anchor, out Vector3 result)
        {
            var reserved = new HashSet<GraphNode>();
            return TryPlanPosition(unit, anchor, reserved, out result);
        }

        private static bool TryPlanPosition(BaseUnitEntity unit, Vector3 anchor,
                                            HashSet<GraphNode> reserved, out Vector3 result)
        {
            result = default(Vector3);
            if (unit == null) return false;
            CustomGridNodeBase origin;
            try { origin = anchor.GetNearestNodeXZUnwalkable(); }
            catch { return false; }
            if (origin == null) return false;

            var candidates = new List<CustomGridNodeBase> { origin };
            try { candidates.AddRange(GridAreaHelper.GetNodesSpiralAround(origin, unit.SizeRect, 12)); }
            catch { }

            foreach (var node in candidates)
            {
                if (node == null || !node.Walkable) continue;
                bool legal;
                try { legal = WarhammerBlockManager.Instance.CanUnitStandOnNode(unit.SizeRect, node); }
                catch { legal = false; }
                if (!legal) continue;

                bool overlaps = false;
                try
                {
                    using (var occupied = GridAreaHelper.GetNodes(node, unit.SizeRect, unit.Forward))
                    {
                        foreach (var n in occupied)
                            if (n == null || reserved.Contains(n)) { overlaps = true; break; }
                        if (!overlaps) foreach (var n in occupied) if (n != null) reserved.Add(n);
                    }
                }
                catch { overlaps = true; }
                if (overlaps) continue;
                result = node.Vector3Position;
                return true;
            }
            return false;
        }

        private static List<Placement> PlanAll(Vector3 anchor)
        {
            var guards = RetinueRegistry.All();
            guards.Sort((a, b) => string.CompareOrdinal(a != null ? a.UniqueId : "",
                                                        b != null ? b.UniqueId : ""));
            var result = new List<Placement>(guards.Count);
            var reserved = new HashSet<GraphNode>();
            foreach (var guard in guards)
            {
                if (guard == null || string.IsNullOrEmpty(guard.UniqueId)) continue;
                if (GuardReserve.IsReserved(guard))
                {
                    result.Add(new Placement { Unit = guard, Uid = guard.UniqueId, Position = guard.Position });
                    continue;
                }
                Vector3 target;
                if (!TryPlanPosition(guard, anchor, reserved, out target))
                {
                    Main.LogError("[合作] 无法为 " + guard.CharacterName + " 规划同步落点，本轮全队摆位取消。");
                    return null;
                }
                result.Add(new Placement { Unit = guard, Uid = guard.UniqueId, Position = target });
            }
            return result;
        }

        private static void ApplyExactPositions(List<Placement> placements)
        {
            foreach (var p in placements)
            {
                if (GuardReserve.IsReserved(p.Unit)) continue;
                Vector3 before = p.Unit.Position;
                try { p.Unit.Commands.InterruptAiCommands(); } catch { }
                try { if (p.Unit.View != null && p.Unit.View.AgentASP != null) p.Unit.View.AgentASP.Blocker.Unblock(); } catch { }
                try
                {
                    try { p.Unit.Position = p.Position; }
                    catch (Exception e)
                    {
                        // AbstractUnitEntity.Position 先写持久 m_Position，之后才跑 View/节点回调。
                        // 读回已等于目标时不能回滚，否则会把同步状态重新拆开。
                        if ((p.Unit.Position - p.Position).sqrMagnitude > 0.0001f) throw;
                        Main.LogError("[合作事务] Position 已写入，但本地后续回调异常：" + e.Message);
                    }
                    if ((p.Unit.Position - p.Position).sqrMagnitude > 0.0001f)
                        throw new InvalidOperationException("位置写入后读回不一致：" + p.Uid);
                    Main.LogVerbose("[生命周期] 同步摆位 " + p.Uid + " " + before + " -> " + p.Position);
                }
                finally
                {
                    try { if (p.Unit.View != null && p.Unit.View.AgentASP != null) p.Unit.View.AgentASP.UpdateBlocker(); } catch { }
                }
            }
        }

        private sealed class PlacementTransactionPlan
        {
            public string Area;
            public int Language;
            public int SettingsFrom;
            public string[] Payload;
            public List<Placement> Placements;
            public Vector3 LeaderPosition;
            public string ReserveSnapshot;
        }

        private sealed class HideTransactionPlan
        {
            public string Area;
            public List<BaseUnitEntity> Units;
        }


        internal static bool TryPrepareSynchronizedPlacement(
            string[] args, out object opaquePlan, out string signature, out string failure)
        {
            opaquePlan = null; signature = ""; failure = "";
            if (args == null || args.Length < 3) { failure = "placeguards 参数不足"; return false; }
            int language, count;
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out language)
                || (language != L.ZhCN && language != L.EnGB))
            { failure = "placeguards 语言无效"; return false; }
            if (!int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                || count < 0 || count > MaxSynchronizedGuards)
            { failure = "placeguards 数量无效"; return false; }
            int settingsFrom = 3 + count * 4;
            if (args.Length < settingsFrom) { failure = "placeguards 载荷不完整"; return false; }
            string area = CurrentAreaId();
            if (string.IsNullOrEmpty(area) || !string.Equals(args[0], area, StringComparison.Ordinal))
            { failure = "placeguards 区域不匹配"; return false; }

            var game = Game.Instance;
            var tc = game != null ? game.TurnController : null;
            if (game == null || game.Player == null || game.Player.MainCharacterEntity == null)
            { failure = "游戏或主角尚未就绪"; return false; }
            if (game.Player.IsInCombat || (tc != null && tc.InCombat))
            { failure = "仍在战斗"; return false; }

            List<BaseUnitEntity> roster;
            Dictionary<string, BaseUnitEntity> rosterById;
            if (!TryBuildRosterIndex(out roster, out rosterById, out failure)) return false;
            var placements = new List<Placement>(count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                int at = 3 + i * 4;
                float x, y, z;
                if (!TryParseFloat(args[at + 1], out x) || !TryParseFloat(args[at + 2], out y)
                    || !TryParseFloat(args[at + 3], out z)
                    || float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                    || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
                { failure = "placeguards 坐标解析失败"; return false; }
                string uid = args[at];
                if (string.IsNullOrEmpty(uid) || !seen.Add(uid))
                { failure = "placeguards UID 为空或重复"; return false; }
                BaseUnitEntity unit;
                if (!rosterById.TryGetValue(uid, out unit)) { failure = "找不到卫兵 " + uid; return false; }
                if (!GuardReserve.IsReserved(unit) && !unit.CanBeTurnedOn)
                { failure = "卫兵不能启用 " + uid; return false; }
                placements.Add(new Placement { Unit = unit, Uid = uid, Position = new Vector3(x, y, z) });
            }
            if (!RosterMatches(roster, seen, out failure)) return false;

            var plan = new PlacementTransactionPlan
            {
                Area = area, Language = language, SettingsFrom = settingsFrom,
                Payload = (string[])args.Clone(), Placements = placements,
                LeaderPosition = game.Player.MainCharacterEntity.Position,
                ReserveSnapshot = GuardReserve.Snapshot
            };
            if (!TryBuildPrepareSignature("placeguards", plan.Payload, settingsFrom,
                                          placements, game.Player.MainCharacterEntity,
                                          language, out signature, out failure)) return false;
            opaquePlan = plan;
            return true;
        }

        internal static bool TryPrepareSynchronizedHide(
            string[] args, out object opaquePlan, out string signature, out string failure)
        {
            opaquePlan = null; signature = ""; failure = "";
            if (args == null || args.Length < 2) { failure = "hideguards 参数不足"; return false; }
            int count;
            if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                || count < 0 || count > MaxSynchronizedGuards || args.Length != 2 + count)
            { failure = "hideguards 载荷无效"; return false; }
            string area = CurrentAreaId();
            if (string.IsNullOrEmpty(area) || !string.Equals(args[0], area, StringComparison.Ordinal))
            { failure = "hideguards 区域不匹配"; return false; }

            var game = Game.Instance;
            var tc = game != null ? game.TurnController : null;
            if (game == null || game.Player == null || game.Player.IsInCombat || (tc != null && tc.InCombat))
            { failure = "游戏未就绪或仍在战斗"; return false; }

            List<BaseUnitEntity> roster;
            Dictionary<string, BaseUnitEntity> rosterById;
            if (!TryBuildRosterIndex(out roster, out rosterById, out failure)) return false;
            var units = new List<BaseUnitEntity>(count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string uid = args[2 + i];
                if (string.IsNullOrEmpty(uid) || !seen.Add(uid))
                { failure = "hideguards UID 为空或重复"; return false; }
                BaseUnitEntity unit;
                if (!rosterById.TryGetValue(uid, out unit)) { failure = "找不到卫兵 " + uid; return false; }
                units.Add(unit);
            }
            if (!RosterMatches(roster, seen, out failure)) return false;

            var raw = new System.Text.StringBuilder("hideguards|");
            AddSignatureField(raw, area);
            foreach (var unit in units)
            {
                AddSignatureField(raw, unit.UniqueId);
                var bp = unit.OriginalBlueprint ?? unit.Blueprint;
                AddSignatureField(raw, bp != null ? bp.AssetGuid.ToString() : "");
            }
            signature = HashSignature(raw);
            opaquePlan = new HideTransactionPlan { Area = area, Units = units };
            return true;
        }

        private static bool TryBuildRosterIndex(out List<BaseUnitEntity> roster,
                                                out Dictionary<string, BaseUnitEntity> byId,
                                                out string failure)
        {
            roster = RetinueRegistry.All();
            byId = new Dictionary<string, BaseUnitEntity>(StringComparer.Ordinal);
            failure = "";
            foreach (var unit in roster)
            {
                if (unit == null || string.IsNullOrEmpty(unit.UniqueId))
                { failure = "本机名册含空单位或空 UID"; return false; }
                if (byId.ContainsKey(unit.UniqueId))
                { failure = "本机名册 UID 重复：" + unit.UniqueId; return false; }
                byId[unit.UniqueId] = unit;
            }
            return true;
        }

        private static bool RosterMatches(List<BaseUnitEntity> roster,
                                          HashSet<string> payloadIds, out string failure)
        {
            failure = "";
            if (roster == null || roster.Count != payloadIds.Count)
            { failure = "卫队 UID 数量不一致（本机 " + (roster != null ? roster.Count : 0)
                      + " / 房主 " + payloadIds.Count + "）"; return false; }
            foreach (var unit in roster)
                if (unit == null || string.IsNullOrEmpty(unit.UniqueId) || !payloadIds.Contains(unit.UniqueId))
                { failure = "卫队 UID 集不一致"; return false; }
            return true;
        }

        private static bool TryBuildPrepareSignature(string kind, string[] payload, int settingsFrom,
            List<Placement> placements, BaseUnitEntity leader, int language,
            out string signature, out string failure)
        {
            signature = ""; failure = "";
            Dictionary<string, object> saved;
            if (!CoopSettings.TryApplyExact(payload, settingsFrom, out saved, out failure)) return false;
            int savedLanguage = Main.Settings != null ? Main.Settings.Language : L.Auto;
            try
            {
                if (Main.Settings == null) { failure = "Settings 尚未就绪"; return false; }
                Main.Settings.Language = language;
                var raw = new System.Text.StringBuilder(kind + "|");
                for (int i = settingsFrom; i < payload.Length; i++) AddSignatureField(raw, payload[i]);
                AddStringArray(raw, "name-pool", Archetypes.NamePool);
                AddSignatureField(raw, leader.UniqueId);
                AddSignatureField(raw, leader.Position.x.ToString("R", CultureInfo.InvariantCulture));
                AddSignatureField(raw, leader.Position.y.ToString("R", CultureInfo.InvariantCulture));
                AddSignatureField(raw, leader.Position.z.ToString("R", CultureInfo.InvariantCulture));
                var planHashes = new Dictionary<BuildPlans.Plan, string>();
                var elitePlans = new Dictionary<ChainProbe.EliteDef, BuildPlans.Plan>();
                foreach (var p in placements)
                {
                    var unit = p.Unit;
                    var bp = unit.OriginalBlueprint ?? unit.Blueprint;
                    int archIndex = RetinueRegistry.ArchetypeOf(unit);
                    var arch = Archetypes.Get(archIndex >= 0 ? archIndex : Main.Settings.ArchetypeIndex);
                    int eliteArch, eliteIndex;
                    RetinueRegistry.GetEliteTag(unit, out eliteArch, out eliteIndex);
                    AddSignatureField(raw, arch != null ? arch.Name : "");
                    AddStringArray(raw, "arch-chain", arch != null ? arch.Chain : null);
                    AddSignatureField(raw, arch != null ? arch.PlanName ?? "" : "");
                    AddStringArray(raw, "arch-rank", arch != null
                        ? (language == L.EnGB ? arch.GuardNamesEn : arch.GuardNames) : null);
                    AddStringArray(raw, "arch-grant", arch != null ? arch.GrantFeatures : null);
                    string brainId = arch != null ? arch.BrainId : "";
                    try
                    {
                        var elite = GearTool.EliteDefOf(unit, arch);
                        if (elite != null && !string.IsNullOrEmpty(elite.BrainId)) brainId = elite.BrainId;
                    }
                    catch { }

                    AddSignatureField(raw, p.Uid);
                    AddSignatureField(raw, GuardReserve.IsReserved(unit) ? "reserve" : "deployed");
                    AddSignatureField(raw, bp != null ? bp.AssetGuid.ToString() : "");
                    AddSignatureField(raw, archIndex.ToString(CultureInfo.InvariantCulture));
                    AddSignatureField(raw, eliteArch.ToString(CultureInfo.InvariantCulture));
                    AddSignatureField(raw, eliteIndex.ToString(CultureInfo.InvariantCulture));
                    AddSignatureField(raw, unit.Brain != null ? "brain" : "no-brain");
                    AddSignatureField(raw, unit.Body != null ? "body" : "no-body");
                    AddSignatureField(raw, unit.Progression != null ? "progression" : "no-progression");
                    AddSignatureField(raw, unit.CombatState != null ? "combat" : "no-combat");
                    AddSignatureField(raw, brainId ?? "");
                    bool brainLoads = string.IsNullOrEmpty(brainId)
                        || ResourcesLibrary.TryGetBlueprint<Kingmaker.AI.Blueprints.BlueprintBrainBase>(brainId) != null;
                    AddSignatureField(raw, brainLoads ? "brain-ok" : "brain-missing");

                    var eliteDef = GearTool.EliteDefOf(unit, arch);
                    if (eliteDef != null)
                    {
                        AddSignatureField(raw, eliteDef.Name ?? "");
                        AddSignatureField(raw, language == L.EnGB ? eliteDef.RankEn ?? eliteDef.Rank ?? "" : eliteDef.Rank ?? "");
                        AddSignatureField(raw, eliteDef.PlanName ?? "");
                        AddSignatureField(raw, eliteDef.RaceId ?? "");
                        AddStringArray(raw, "elite-chain", eliteDef.Chain);
                        AddStringArray(raw, "elite-key", eliteDef.KeyTalents);
                        AddStringArray(raw, "elite-attr", eliteDef.AttrPriority);
                        AddStringArray(raw, "elite-gear", eliteDef.Gear);
                    }
                    string[] chain = eliteDef != null && eliteDef.Chain != null ? eliteDef.Chain
                                   : (arch != null ? arch.Chain : null);
                    BuildPlans.Plan effectivePlan;
                    if (eliteDef != null && elitePlans.TryGetValue(eliteDef, out effectivePlan)) { }
                    else
                    {
                        effectivePlan = ResolveEffectivePlan(eliteDef, arch, chain);
                        if (eliteDef != null) elitePlans[eliteDef] = effectivePlan;
                    }
                    if (effectivePlan == null) AddSignatureField(raw, "plan:null");
                    else
                    {
                        string planHash;
                        if (!planHashes.TryGetValue(effectivePlan, out planHash))
                        {
                            var planRaw = new System.Text.StringBuilder();
                            AddPlanSignature(planRaw, effectivePlan);
                            planHash = HashSignature(planRaw);
                            planHashes[effectivePlan] = planHash;
                        }
                        AddSignatureField(raw, planHash);
                    }
                    if (effectivePlan != null && !string.IsNullOrEmpty(effectivePlan.First)
                        && !string.IsNullOrEmpty(effectivePlan.Second))
                        chain = new[] { effectivePlan.First, effectivePlan.Second, "bcefe9c41c7841c9a99b1dbac1793025" };
                    AddBlueprintAvailability<Kingmaker.UnitLogic.Progression.Paths.BlueprintCareerPath>(raw, "chain", chain);
                    string raceId = eliteDef != null ? eliteDef.RaceId : null;
                    AddBlueprintAvailability<Kingmaker.UnitLogic.Progression.Features.BlueprintRace>(
                        raw, "race", string.IsNullOrEmpty(raceId) ? null : new[] { raceId });
                    string[] preGrant = eliteDef != null ? eliteDef.PreGrant : (arch != null ? arch.PreGrant : null);
                    AddBlueprintAvailability<Kingmaker.UnitLogic.Progression.Features.BlueprintFeature>(raw, "pre", preGrant);
                    AddBlueprintAvailability<Kingmaker.UnitLogic.Progression.Features.BlueprintFeature>(
                        raw, "grant", arch != null ? arch.GrantFeatures : null);
                    if (arch != null && arch.GrantFeaturesTier != null)
                        foreach (var tierFeatures in arch.GrantFeaturesTier)
                            AddBlueprintAvailability<Kingmaker.UnitLogic.Progression.Features.BlueprintFeature>(raw, "tier", tierFeatures);
                    if (ServitorSummon.Applies(unit))
                        AddBlueprintAvailability<Kingmaker.UnitLogic.Abilities.Blueprints.BlueprintAbility>(
                            raw, "servitor", new[] { ServitorSummon.DonorAbility });

                    string[] gear = null;
                    try { gear = GearTool.GearFor(unit, arch); } catch { }
                    if (gear != null)
                    {
                        foreach (string slot in gear)
                        {
                            AddSignatureField(raw, slot ?? "");
                            if (string.IsNullOrEmpty(slot)) continue;
                            foreach (string candidate in slot.Split('|'))
                            {
                                string id = candidate != null ? candidate.Trim() : "";
                                bool loads = !string.IsNullOrEmpty(id)
                                    && ResourcesLibrary.TryGetBlueprint<Kingmaker.Blueprints.Items.BlueprintItem>(id) != null;
                                AddSignatureField(raw, id + "=" + (loads ? "1" : "0"));
                            }
                        }
                    }
                }
                signature = HashSignature(raw);
                return true;
            }
            catch (Exception e) { failure = "构造预检签名失败：" + e.Message; return false; }
            finally
            {
                if (Main.Settings != null) Main.Settings.Language = savedLanguage;
                CoopSettings.Restore(saved);
            }
        }

        private static void AddSignatureField(System.Text.StringBuilder raw, string value)
        {
            value = value ?? "";
            raw.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        private static void AddBlueprintAvailability<T>(System.Text.StringBuilder raw, string label, string[] ids)
            where T : BlueprintScriptableObject
        {
            AddSignatureField(raw, label);
            if (ids == null) { AddSignatureField(raw, "null"); return; }
            AddSignatureField(raw, ids.Length.ToString(CultureInfo.InvariantCulture));
            foreach (string source in ids)
            {
                string id = source != null ? source.Trim() : "";
                bool available = !string.IsNullOrEmpty(id) && ResourcesLibrary.TryGetBlueprint<T>(id) != null;
                AddSignatureField(raw, id);
                AddSignatureField(raw, available ? "1" : "0");
            }
        }

        private static BuildPlans.Plan ResolveEffectivePlan(
            ChainProbe.EliteDef elite, ChainProbe.Archetype arch, string[] chain)
        {
            if (elite != null && elite.PlanSegments != null)
                return BuildPlans.Compose(elite.Name, chain, elite.PlanSegments, elite.ExcludeFeatures);
            bool ownPlan = elite != null && !string.IsNullOrEmpty(elite.PlanName);
            bool ownChain = elite != null && elite.Chain != null && elite.Chain.Length > 0;
            string name = ownPlan ? elite.PlanName : (ownChain ? null : (arch != null ? arch.PlanName : null));
            return BuildPlans.Get(name);
        }

        private static void AddPlanSignature(System.Text.StringBuilder raw, BuildPlans.Plan plan)
        {
            AddSignatureField(raw, "plan");
            if (plan == null) { AddSignatureField(raw, "null"); return; }
            AddSignatureField(raw, plan.Id ?? "");
            AddSignatureField(raw, plan.Name ?? "");
            AddSignatureField(raw, plan.First ?? "");
            AddSignatureField(raw, plan.Second ?? "");
            AddBlueprintAvailability<Kingmaker.UnitLogic.Progression.Features.BlueprintFeature>(
                raw, "home-origin", new[] { plan.Homeworld, plan.Origin });

            var paths = new List<string>(plan.Sel.Keys);
            paths.Sort(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                AddSignatureField(raw, path);
                Dictionary<int, List<string>> byRank;
                if (!plan.Sel.TryGetValue(path, out byRank) || byRank == null)
                { AddSignatureField(raw, "null"); continue; }
                var ranks = new List<int>(byRank.Keys);
                ranks.Sort();
                foreach (int rank in ranks)
                {
                    AddSignatureField(raw, rank.ToString(CultureInfo.InvariantCulture));
                    List<string> candidates;
                    if (!byRank.TryGetValue(rank, out candidates) || candidates == null)
                    { AddSignatureField(raw, "null"); continue; }
                    AddBlueprintAvailability<Kingmaker.UnitLogic.Progression.Features.BlueprintFeature>(
                        raw, "choices", candidates.ToArray());
                }
            }
        }

        private static void AddStringArray(System.Text.StringBuilder raw, string label, string[] values)
        {
            AddSignatureField(raw, label);
            if (values == null) { AddSignatureField(raw, "null"); return; }
            AddSignatureField(raw, values.Length.ToString(CultureInfo.InvariantCulture));
            foreach (string value in values) AddSignatureField(raw, value ?? "");
        }

        private static string HashSignature(System.Text.StringBuilder raw)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(raw != null ? raw.ToString() : "");
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        internal static bool CanCommitSynchronizedPlan(object opaquePlan, out string failure)
        {
            return CanCommitSynchronizedPlan(opaquePlan, true, out failure);
        }

        internal static bool CanCommitSynchronizedPlanInHandler(object opaquePlan, out string failure)
        {
            return CanCommitSynchronizedPlan(opaquePlan, false, out failure);
        }

        private static bool CanCommitSynchronizedPlan(object opaquePlan, bool checkLocalLoading,
                                                       out string failure)
        {
            failure = "";
            string area = null;
            if (opaquePlan is PlacementTransactionPlan) area = ((PlacementTransactionPlan)opaquePlan).Area;
            else if (opaquePlan is HideTransactionPlan) area = ((HideTransactionPlan)opaquePlan).Area;
            else { failure = "计划类型无效"; return false; }
            try
            {
                if (checkLocalLoading && (LoadingProcess.Instance == null || LoadingProcess.Instance.IsLoadingInProcess))
                { failure = "加载仍在进行"; return false; }
                var game = Game.Instance;
                var tc = game != null ? game.TurnController : null;
                if (game == null || game.Player == null || game.Player.IsInCombat || (tc != null && tc.InCombat))
                { failure = "游戏未就绪或已进入战斗"; return false; }
                if (!string.Equals(area, CurrentAreaId(), StringComparison.Ordinal))
                { failure = "区域已变化"; return false; }

                List<BaseUnitEntity> roster;
                Dictionary<string, BaseUnitEntity> rosterById;
                if (!TryBuildRosterIndex(out roster, out rosterById, out failure)) return false;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                if (opaquePlan is PlacementTransactionPlan)
                {
                    var placement = (PlacementTransactionPlan)opaquePlan;
                    if (!string.Equals(placement.ReserveSnapshot, GuardReserve.Snapshot, StringComparison.Ordinal))
                    { failure = "卫兵出战状态在 prepare 后已变化"; return false; }
                    var leader = game.Player.MainCharacterEntity;
                    if (leader == null || (leader.Position - placement.LeaderPosition).sqrMagnitude > 0.01f)
                    { failure = "主角在 prepare 后又发生位移"; return false; }
                    foreach (var p in placement.Placements)
                    {
                        BaseUnitEntity current;
                        if (p == null || p.Unit == null || p.Unit.IsDisposed
                            || !rosterById.TryGetValue(p.Uid, out current)
                            || !ReferenceEquals(current, p.Unit))
                        { failure = "卫兵在 prepare 后已变化"; return false; }
                        ids.Add(p.Uid);
                    }
                }
                else
                    foreach (var unit in ((HideTransactionPlan)opaquePlan).Units)
                    {
                        BaseUnitEntity current;
                        if (unit == null || unit.IsDisposed || string.IsNullOrEmpty(unit.UniqueId)
                            || !rosterById.TryGetValue(unit.UniqueId, out current)
                            || !ReferenceEquals(current, unit))
                        { failure = "卫兵在 prepare 后已变化"; return false; }
                        ids.Add(unit.UniqueId);
                    }
                if (!RosterMatches(roster, ids, out failure)) return false;
                return true;
            }
            catch (Exception e) { failure = "提交前校验失败：" + e.Message; return false; }
        }

        internal static void CommitSynchronizedPlacement(object opaquePlan)
        {
            var plan = opaquePlan as PlacementTransactionPlan;
            if (plan == null) throw new InvalidOperationException("placeguards 提交计划类型无效");
            Dictionary<string, object> saved;
            string failure;
            if (!CoopSettings.TryApplyExact(plan.Payload, plan.SettingsFrom, out saved, out failure))
                throw new InvalidOperationException("placeguards 提交前设置失效：" + failure);
            int savedLanguage = Main.Settings != null ? Main.Settings.Language : L.Auto;
            try
            {
                if (Main.Settings != null) Main.Settings.Language = plan.Language;
                var game = Game.Instance;
                var leader = game != null && game.Player != null ? game.Player.MainCharacterEntity : null;
                foreach (var p in plan.Placements) RetinueTest.ApplyRuntimeState(p.Unit, leader);
                // 机仆销毁是另一类实体事务，不能夹在摆位 commit 里做未签名的全场扫描。
                // 正常战后清理由 TurnController.ExitTb 同步边界负责；旧版遗留另走专用清理。
                ApplyExactPositions(plan.Placements);
                Main.Log("[合作事务] 同步恢复并摆位 " + plan.Placements.Count + " 名卫兵，区域=" + plan.Area);
            }
            finally
            {
                if (Main.Settings != null) Main.Settings.Language = savedLanguage;
                CoopSettings.Restore(saved);
                ResetPlacementPending();
                StuckWatch.Reset();
            }
        }

        internal static void CommitSynchronizedHide(object opaquePlan)
        {
            var plan = opaquePlan as HideTransactionPlan;
            if (plan == null) throw new InvalidOperationException("hideguards 提交计划类型无效");
            foreach (var unit in plan.Units)
            {
                try { unit.IsInGame = false; }
                catch (Exception e)
                {
                    if (unit.IsInGame) throw;
                    Main.LogError("[合作事务] IsInGame 已写入 false，但本地后续回调异常：" + e.Message);
                }
                if (unit.IsInGame) throw new InvalidOperationException("IsInGame 写入后读回仍为 true：" + unit.UniqueId);
            }
            Main.Log("[合作事务] 同步关灯 " + plan.Units.Count + " 名卫兵，区域=" + plan.Area);
            ResetPlacementPending();
            StuckWatch.Reset();
        }

        /// <summary>
        /// 等所有 LoadingProcess 结束且队长位置连续 1 秒同步时间不变。合作仅房主发命令；
        /// 单机使用同一套规划后本地执行，自动传送体验不变。
        /// </summary>
        public static void TickPending()
        {
            if (!_placementPending && !_hidePending) return;
            try
            {
                if (LoadingProcess.Instance == null || LoadingProcess.Instance.IsLoadingInProcess)
                { _stableSinceTick = -1; return; }

                var game = Game.Instance;
                if (game == null || game.Player == null) return;
                var tc = game.TurnController;
                if (game.Player.IsInCombat || (tc != null && tc.InCombat))
                { _stableSinceTick = -1; return; }
                int now = game.RealTimeController.CurrentNetworkTick;
                string area = CurrentAreaId();
                bool shared = CoopState.SharedGameplayRequired;

                // 两种模式都先要求 area 在 1 秒同步时间内不变，跨过 LoadingProcess
                // 队列之间可能出现的短暂空档；队伍区域下面还会额外检查主角坐标稳定。
                if (_stableSinceTick < 0 || !string.Equals(area, _stableAreaId, StringComparison.Ordinal))
                {
                    _stableSinceTick = now;
                    _stableAreaId = area;
                    return;
                }
                if (now - _stableSinceTick < PlacementStableTicks) return;

                if (_hidePending)
                {
                    if (shared && !CoopState.IsConfirmedHost) return;
                    var guards = RetinueRegistry.All();
                    guards.Sort((a, b) => string.CompareOrdinal(a != null ? a.UniqueId : "",
                                                                b != null ? b.UniqueId : ""));
                    if (shared)
                    {
                        var payload = new List<string> { area,
                            guards.Count.ToString(CultureInfo.InvariantCulture) };
                        foreach (var guard in guards)
                            if (guard != null && !string.IsNullOrEmpty(guard.UniqueId)) payload.Add(guard.UniqueId);
                        // 数量必须等于真正写入的 UID 数，不能把 null 项算进去。
                        payload[1] = (payload.Count - 2).ToString(CultureInfo.InvariantCulture);
                        if (CoopAutoTransaction.Begin("hideguards", payload.ToArray())) ResetPlacementPending();
                        else { ResetPlacementPending(); CoopAutoTransaction.DelayBegin("hideguards"); }
                    }
                    else
                    {
                        foreach (var guard in guards) if (guard != null) guard.IsInGame = false;
                        ResetPlacementPending();
                    }
                    return;
                }

                var leader = game.Player.MainCharacterEntity;
                if (leader == null) return;
                Vector3 pos = leader.Position;
                if ((pos - _stableLeaderPosition).sqrMagnitude > 0.01f)
                {
                    _stableSinceTick = now;
                    _stableLeaderPosition = pos;
                    return;
                }

                if (shared && !CoopState.IsConfirmedHost)
                {
                    // 加入方只等房主命令，不执行任何本地实体写。
                    return;
                }

                var placements = PlanAll(pos);
                if (placements == null)
                {
                    // 狭窄区域/临时占格可能稍后解除；保留自动摆位，每秒继续尝试。
                    _stableSinceTick = now;
                    return;
                }
                if (shared)
                {
                    var payload = new List<string> { area,
                        L.Current.ToString(CultureInfo.InvariantCulture),
                        placements.Count.ToString(CultureInfo.InvariantCulture) };
                    foreach (var p in placements)
                    {
                        payload.Add(p.Uid); payload.Add(F(p.Position.x));
                        payload.Add(F(p.Position.y)); payload.Add(F(p.Position.z));
                    }
                    payload.AddRange(CoopSettings.Capture());
                    if (CoopAutoTransaction.Begin("placeguards", payload.ToArray())) ResetPlacementPending();
                    else { ResetPlacementPending(); CoopAutoTransaction.DelayBegin("placeguards"); }
                }
                else
                {
                    ApplyExactPositions(placements);
                    foreach (var p in placements)
                        if (!GuardReserve.IsReserved(p.Unit)) RetinueTest.ReapplyBrain(p.Unit);
                    ResetPlacementPending();
                }
            }
            catch (Exception e) { Main.LogError("TickPending: " + e); ResetPlacementPending(); }
        }
    }
}
