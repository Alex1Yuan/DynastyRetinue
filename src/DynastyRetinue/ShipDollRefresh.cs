using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 换船模之后，改装界面里的船**不会跟着更新武器美术** —— 这里补上重拍。
    ///
    /// ================= 为什么会丢（逐行确认）=================
    ///
    /// ShipDollRoom.cs:80-97
    ///     private void CreateSimpleAvatar(BaseUnitEntity ship)
    ///     {
    ///         UnitEntityView unitEntityView = ship.View;
    ///         if (unitEntityView == null) { unitEntityView = ship.CreateView(); ship.AttachView(...); }
    ///         GameObject original = unitEntityView.GetComponentInChildren&lt;StarshipView&gt;()
    ///                                             .BaseRenderer.gameObject;
    ///         m_SimpleAvatar = Object.Instantiate(original, m_TargetPlaceholder, false);   // ★
    ///         ...
    ///     }
    ///
    /// ★ 改装界面里那条船不是活的 view，是世界 view 的**一次性哑拷贝** ★
    /// 拷贝发生在**打开界面那一刻**，之后再不更新。
    ///
    /// 而武器美术是 StarshipView.Start() → SetAllEquipment() 挂上去的：
    ///     StarshipView.cs:93-103
    ///         public void SetAllEquipment()
    ///         {
    ///             if (UnitEntityView == null || UnitEntityView.Data == null) return;   // ★静默返回
    ///             PartStarshipHull hull = UnitEntityView.Data.GetHull();
    ///             if (hull == null) return;                                            // ★静默返回
    ///             foreach (...) EquipItemFromItemSlot(...);
    ///         }
    ///
    /// 两件事叠在一起就是玩家看到的现象：
    ///   1. 拷贝出来的 avatar 上那个 StarshipView 组件，UnitEntityView.Data 是空的
    ///      ⇒ 它自己的 Start() 里 SetAllEquipment 第一行就 return
    ///      ⇒ **克隆体永远不会自己长出武器**，只能继承拷贝那一刻已经存在的美术。
    ///      （实测佐证：在改装界面里跑挂点几何诊断，StarshipFxLocator = 0 个；
    ///        而在太空战里跑同一个诊断 = 39 个。）
    ///   2. 换船模要重建世界 view，prefab bundle 加载要 ~2 秒，
    ///      SetAllEquipment 是在那之后才跑的。这 2 秒里拍的快照上一门炮都没有。
    ///
    /// ⇒ 「切完船立刻看改装界面」= 拍到的是没装武器的空壳。
    ///    而且因为没有任何刷新机制，**它会一直空到你关掉界面重开为止**。
    ///
    /// vanilla 唯一相关的公开方法 UpdateStarshipRenderers()（ShipDollRoom.cs:99-120）
    /// 只是把 MeshRenderer.enabled 打开、图层改成 15，**不重新拷贝**，帮不上忙。
    ///
    /// ================= 做法 =================
    /// 在 StarshipView.SetAllEquipment 的 **Postfix** 上挂钩：世界 view 刚把武器装好，
    /// 正是重拍的时机。此时若改装界面开着（m_SimpleAvatar 非空），就把旧 avatar 销毁、
    /// 再调一次私有的 CreateSimpleAvatar —— 走的是 vanilla 自己那条路，
    /// 连带 ShipDollScalePatch（也挂在 CreateSimpleAvatar 上）会再跑一遍，缩放不会丢。
    ///
    /// ★存档★ 纯场景对象操作，一个 [JsonProperty] 都不碰。
    /// </summary>
    public static class ShipDollRefresh
    {
        private static Type _roomType;
        private static FieldInfo _avatarField;
        private static MethodInfo _create;
        private static bool _resolved;
        private static bool _explained;

        private static bool Resolve()
        {
            if (_resolved) return _roomType != null && _avatarField != null && _create != null;
            _resolved = true;
            _roomType = AccessTools.TypeByName("Kingmaker.UI.DollRoom.ShipDollRoom");
            if (_roomType == null) { Main.LogError("[展示房间] 找不到 ShipDollRoom，改装界面不会自动重拍。"); return false; }
            // 逐层 DeclaredOnly ——ShipDollRoom 有基类，直接 GetField 撞上同名成员会抛
            // AmbiguousMatchException，这个坑这个项目里已经踩过两次。
            for (var t = _roomType; t != null && _avatarField == null; t = t.BaseType)
                _avatarField = t.GetField("m_SimpleAvatar",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            _create = AccessTools.Method(_roomType, "CreateSimpleAvatar");
            if (_avatarField == null || _create == null)
                Main.LogError("[展示房间] ShipDollRoom 的 m_SimpleAvatar / CreateSimpleAvatar 找不到，改装界面不会自动重拍。");
            return _avatarField != null && _create != null;
        }

        private static string _lastWhy;

        /// <summary>
        /// 记录"这次为什么没重拍"。原来四个 return 全是静默的，
        /// 日志里「没重拍」和「重拍了」长得一模一样，排查时完全瞎。
        /// 同一个原因只报一次，避免每帧刷屏。
        /// </summary>
        private static void Why(string reason)
        {
            if (reason == _lastWhy) return;
            _lastWhy = reason;
            Main.Log("[展示房间] 本次未重拍：" + reason);
        }

        /// <summary>拿当前活着的 ShipDollRoom（没开界面返回 null）。</summary>
        private static object CurrentRoom()
        {
            try
            {
                var t = AccessTools.TypeByName("Kingmaker.UI.Common.UIDollRooms");
                if (t == null) return null;
                var inst = AccessTools.Property(t, "Instance");
                object o = inst != null ? inst.GetValue(null, null) : null;
                if (o == null)
                {
                    var f = AccessTools.Field(t, "Instance");
                    o = f != null ? f.GetValue(null) : null;
                }
                if (o == null) return null;
                var rf = AccessTools.Field(t, "ShipDollRoom");
                return rf != null ? rf.GetValue(o) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 重拍改装界面里的船。界面没开、或者拿不到房间就静默不动。
        /// </summary>
        public static void Resnap()
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipDollResnap) { Why("开关关着"); return; }
                if (!Resolve()) { Why("反射解析失败"); return; }

                var room = CurrentRoom();
                if (room == null || (room is UnityEngine.Object && !(UnityEngine.Object)room))
                { Why("拿不到 ShipDollRoom（改装界面没开？）"); return; }

                var ship = Game.Instance != null && Game.Instance.Player != null
                         ? Game.Instance.Player.PlayerShip : null;
                if (ship == null) { Why("拿不到玩家座舰"); return; }

                var old = _avatarField.GetValue(room) as GameObject;
                if (old == null) { Why("m_SimpleAvatar 为空 —— 改装界面没开，没有快照要重拍"); return; }

                // BaseRenderer 不用在这里补：StarshipViewTool.HealBaseRenderer 本身就是
                // ShipDollRoom.CreateSimpleAvatar 的 Prefix，下面 Invoke 会连它一起触发。
                // （换过船模的船体原版没接这根线 —— 实测日志里换船后诊断报 "BaseRenderer: null"，
                //   而 CreateSimpleAvatar 第三行就是 ....BaseRenderer.gameObject，
                //   靠那个 Prefix 挡住才没 NRE。）
                UnityEngine.Object.Destroy(old);
                _avatarField.SetValue(room, null);
                _create.Invoke(room, new object[] { ship });

                // ★★★ 必须自己刷图层，否则重拍出来的船是隐形的 ★★★
                // UpdateStarshipRenderers() 干两件事（ShipDollRoom.cs:99-120）：
                //     obj.enabled = true;  obj.gameObject.layer = 15;   // 15 = DollRoom 层
                // 而它**全树只有两个调用点**，都在 StarshipView.cs:118/137，
                // 也就是 SetAllEquipment 的**末尾**。我们的重拍是那个方法的 Postfix + 延两帧，
                // :118 早就跑完了 ⇒ 新 avatar 停在世界层、renderer 也没启用 ⇒ 改装界面一片空。
                // 这个洞很隐蔽：旧快照是被 vanilla 刷过层的，所以"没重拍"反而看得见船，
                // "重拍了"却什么都没有 —— 症状比不修还糟。
                try
                {
                    var upd = AccessTools.Method(_roomType, "UpdateStarshipRenderers");
                    if (upd != null) upd.Invoke(room, null);
                    else Main.LogError("[展示房间] 找不到 UpdateStarshipRenderers —— 重拍出来的船可能不可见。");
                }
                catch (Exception e) { Main.LogError("[展示房间] 刷图层失败: " + e.Message); }

                if (!_explained)
                {
                    _explained = true;
                    Main.Log("[展示房间] 改装界面的船已重拍（武器美术跟上了）。"
                           + "\n  原因：ShipDollRoom.CreateSimpleAvatar 是把世界 view 的 BaseRenderer 子树"
                           + "**一次性 Instantiate** 出来的哑拷贝，打开界面那一刻拍完就再不更新；"
                           + "\n  而换船模重建 view 后，武器美术要等 prefab bundle 加载完、"
                           + "Start()→SetAllEquipment() 跑过才挂上（实测约 2 秒）。"
                           + "切完船立刻看 = 拍到空壳，而且会一直空到关掉界面重开。"
                           + "\n  现在改成在 SetAllEquipment 之后自动重拍一次。本次会话只解释这一条。");
                }
            }
            catch (Exception e) { Main.LogError("[展示房间] 重拍失败: " + e.Message); }
        }

        /// <summary>
        /// StarshipView.SetAllEquipment 的 Postfix —— 世界 view 刚装好武器，正是重拍时机。
        /// 注意这个方法在**克隆体**上也会跑（克隆体自己的 Start 会调），
        /// 但克隆体的 UnitEntityView.Data 是空的、vanilla 第一行就 return，
        /// 我们这里也会因为 PlayerShip 的 view 不是它而只重拍一次，不会递归。
        /// </summary>
        [HarmonyPatch]
        public static class ResnapPatch
        {
            private static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("StarshipView");
                return t == null ? null : AccessTools.Method(t, "SetAllEquipment");
            }

            private static bool Prepare() { return TargetMethod() != null; }

            private static void Postfix(object __instance)
            {
                try
                {
                    var c = __instance as Component;
                    if (c == null) return;
                    // 只在**世界 view**装好武器之后重拍；克隆体没有 Data，直接跳过
                    object data = null;
                    var uev = AccessTools.Property(c.GetType(), "UnitEntityView");
                    var uevObj = uev != null ? uev.GetValue(c, null) as Component : null;
                    if (uevObj == null) return;
                    var dp = AccessTools.Property(uevObj.GetType(), "Data");
                    if (dp != null) data = dp.GetValue(uevObj, null);
                    if (data == null) return;

                    object player = Game.Instance != null && Game.Instance.Player != null
                                  ? (object)Game.Instance.Player.PlayerShip : null;
                    if (player == null || !ReferenceEquals(data, player)) return;

                    // 延两帧：EquipWeapon 的 Instantiate 当帧未必全部就绪
                    Deferred.NextFrames(2, Resnap);
                }
                catch (Exception e) { Main.LogError("[展示房间] Postfix: " + e.Message); }
            }
        }
    }
}
