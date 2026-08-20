using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.UnitLogic;          // SnapToGrid 扩展方法

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
    public sealed class RetinueLifecycle : IAreaHandler, IAreaLoadingStagesHandler
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
        private static int _pendingPlaceFrames;

        public static void Subscribe()
        {
            if (_instance != null) return;
            try
            {
                _instance = new RetinueLifecycle();
                EventBus.Subscribe(_instance);
                Main.Log("区域生命周期已订阅。");
            }
            catch (Exception e) { _instance = null; Main.LogError("订阅失败: " + e); }
        }

        public static void Unsubscribe()
        {
            if (_instance == null) return;
            try { EventBus.Unsubscribe(_instance); Main.Log("区域生命周期已退订。"); }
            catch (Exception e) { Main.LogError("退订失败: " + e); }
            finally { _instance = null; _pendingPlaceFrames = 0; }
        }

        /// <summary>
        /// 当前是不是「队伍区域」。星系图 / 太空战 / 全局地图都是 IsShipArea，
        /// 原版在那些区域会把 PartyAndPets 全部 IsInGame=false 关灯
        /// （AreaEnterPoint.cs:91-94 + :151-165）。
        /// 但卫兵是 ExCompanion，**不在 PartyAndPets 里**（Player.cs:1364-1367），
        /// 原版的关灯遍历漏掉它们 —— 如果我们再把 IsInGame 置回 true，
        /// 就会出现"太空里飘着几个步兵"，甚至混进太空战的先攻序列。
        /// </summary>
        private static bool InPartyArea()
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
            // 自检：只在有 dynasty_selftest.flag 时跑，一次会话一遍，纯只读。
            // 放这里而不是 Main.Load —— 载入时蓝图缓存还没就绪，
            // 那正是 v0.50.0 修的那个坑（早读一次就把分型表钉死在内置默认上）。
            try { SelfCheck.RunOnce(); } catch { }

            if (!Main.Enabled) return;

            // ★必须在任何早退之前★（下面有 list.Count == 0 就 return）。
            // 存档里的 m_CustomPrefabGuid 可能已失效（退了 DLC / 换了机器）：
            // 失效时 Instantiate 返回 null → CreateView 返回 null →
            // Entity.AttachToViewOnLoad:393-397 把 IsInGame = false，整条船下线。
            // 这里检出来就退回原版模型，把"整船消失"降级成"外观没换成"。
            try { ShipModelBundleHold.ValidateAndRearm(StarshipViewTool.PlayerShip); }
            catch (Exception e) { Main.LogError("[船模] 区域加载自检失败: " + e.Message); }

            try
            {
                var list = RetinueRegistry.All();
                if (list.Count == 0) return;

                if (!InPartyArea())
                {
                    // 非队伍区域：主动关灯，与原版对 PartyAndPets 的处理保持一致
                    foreach (var g in list) { try { g.IsInGame = false; } catch { } }
                    Main.Log("[生命周期] 非队伍区域（星系图/太空战/全局地图），已关灯 " + list.Count + " 名卫兵");
                    _pendingPlaceFrames = 0;
                    return;
                }

                var leader = Game.Instance.Player != null ? Game.Instance.Player.MainCharacterEntity : null;
                Main.Log("[生命周期] 区域加载完成，重建 " + list.Count + " 名卫兵的运行时状态");
                foreach (var g in list)
                {
                    try { RetinueTest.ApplyRuntimeState(g, leader); }
                    catch (Exception e) { Main.LogError("重建失败: " + e.Message); }
                }
            }
            catch (Exception e) { Main.LogError("OnAreaDidLoad: " + e); }
        }

        // ---------- IAreaLoadingStagesHandler ----------

        public void OnAreaScenesLoaded() { }

        /// <summary>只打标记，真正摆位推迟若干帧（见 _pendingPlaceFrames 注释）。</summary>
        public void OnAreaLoadingComplete()
        {
            if (!Main.Enabled) return;
            _pendingPlaceFrames = InPartyArea() ? 3 : 0;
            // 招募入口是运行时交互、不进存档，所以每次进区域都要重挂一遍。
            // 放在这里而不是 TickPending 里：它不依赖导航图，也不限于队伍区域
            //（船上那些 NPC 所在的区域不一定被 InPartyArea 认作队伍区域）。
            try { RecruitEntry.ResetForNewArea(); RecruitEntry.AttachInArea();
                  RecruitDialog.ResetForNewArea(); RecruitDialog.InjectInArea(); }
            catch (Exception e) { Main.LogError("[招募] 区域挂载异常: " + e.Message); }

            try { RefreshGearOnAugmentUnlock(); }
            catch (Exception e) { Main.LogError("[植入物] 层级检查异常: " + e.Message); }
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

        /// <summary>由 Main.OnUpdate 每帧调用，消费摆位标记。</summary>
        public static void TickPending()
        {
            if (_pendingPlaceFrames <= 0) return;
            _pendingPlaceFrames--;
            if (_pendingPlaceFrames > 0) return;   // 再等几帧，让 UpdateNavMesh + MovePartyToNavmesh 跑完

            try
            {
                if (!InPartyArea()) return;
                var leader = Game.Instance != null && Game.Instance.Player != null
                    ? Game.Instance.Player.MainCharacterEntity : null;
                if (leader == null) return;

                var list = RetinueRegistry.All();
                foreach (var g in list)
                {
                    try
                    {
                        // 从队长脚下出发再吸附。不能从旧区域残留坐标出发 ——
                        // 那可能在几百米外甚至墙里，SnapToGrid 的螺旋搜索会失控。
                        var before = g.Position;
                        try { if (g.View != null && g.View.AgentASP != null) g.View.AgentASP.Stop(); } catch { }
                        g.Position = leader.Position;
                        g.SnapToGrid();
                        Main.Log("[生命周期] 摆位 " + before + " -> " + g.Position);
                    }
                    catch (Exception e) { Main.LogError("摆位失败: " + e.Message); }
                }
            }
            catch (Exception e) { Main.LogError("TickPending: " + e); }
        }
    }
}