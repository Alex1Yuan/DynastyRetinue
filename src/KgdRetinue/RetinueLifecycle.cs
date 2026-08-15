using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.UnitLogic;          // SnapToGrid 扩展方法

namespace KgdRetinue
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
            if (!Main.Enabled) return;
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