using System;
using System.Reflection;
using Kingmaker;
using Kingmaker.Enums;

namespace DynastyRetinue
{
    /// <summary>
    /// 舰船分档切换（换大船）。
    ///
    /// 落点：Kingmaker.UnitLogic.PartUnitState.Size 的 **public setter**，vanilla 自己就做全了：
    ///     set {
    ///         m_Size = value;
    ///         Owner.UpdateSizeModifiers();                          // 数值/体积修正
    ///         EventBus.RaiseEvent(h =&gt; h.HandleUnitSizeChanged());  // 模型缩放 + 格子占位
    ///     }
    /// 所以模型和碰撞都会跟着变，不需要我们手动 cycle node blocker。
    /// MechanicEntity.Size 只是 `GetStateOptional()?.Size ?? OriginalSize` 的只读转发。
    ///
    /// ★ 存档影响，必须告知玩家 ★
    /// m_Size 是 [JsonProperty]，**会写进存档**。它是 vanilla 枚举 + vanilla 实体，
    /// 不碰"mod 蓝图进类型化字段"那条红线，卸载 mod 后存档照样打得开；
    /// 但船会**保持**在你切过去的那一档，不会自动变回护卫舰。
    /// 要还原就在切回「护卫舰」再存一次。
    ///
    /// 与「多打」的关系：StarshipChargesPatch 读的就是这个 Size，
    /// 所以切到巡洋舰之后舷炮才会多打 —— 两者是同一套判据，天然一致。
    /// </summary>
    public static class StarshipTool
    {
        /// <summary>玩家当前座舰。拿不到返回 null（不在游戏内 / 还没有船）。</summary>
        public static object PlayerShip
        {
            get
            {
                try
                {
                    return Game.Instance != null && Game.Instance.Player != null
                         ? Game.Instance.Player.PlayerShip : null;
                }
                catch { return null; }
            }
        }

        public static Size CurrentSize()
        {
            return StarshipChargesPatch.ShipSize();
        }

        /// <summary>
        /// 切换玩家座舰的分档。返回是否成功。
        /// 只接受四个舰船档位，别拿它去设 Medium 之类的步兵尺寸。
        /// </summary>
        public static bool SetSize(Size size)
        {
            if (size != Size.Raider_1x1 && size != Size.Frigate_1x2
                && size != Size.Cruiser_2x4 && size != Size.GrandCruiser_3x6)
            {
                Main.LogError("[舰船] 拒绝把座舰设成非舰船尺寸: " + size);
                return false;
            }

            var ship = PlayerShip;
            if (ship == null) { Main.LogError("[舰船] 拿不到玩家座舰（不在游戏内？）"); return false; }

            // ★ 战斗中默认不让切 ★
            // 改 Size 会改变**格子占位**（Frigate_1x2 -> Cruiser_2x4 是 2 格变 8 格）。
            // PartUnitState.Size 的 setter 只做了 UpdateSizeModifiers + RaiseEvent(HandleUnitSizeChanged)，
            // 我**没有**证据表明战斗中的节点占位/寻路网格会跟着干净地重算 ——
            // 最坏情况是船占的格子和别的单位重叠、或者卡在原本能过的位置。
            // 战斗外切换没有这个问题（进战斗时会重新布场）。
            // 真要在战斗里试，面板上勾「允许战斗中换船（有风险）」。
            try
            {
                bool inCombat = false;
                var p = ship.GetType().GetProperty("IsInCombat");
                if (p != null) inCombat = (bool)p.GetValue(ship, null);
                if (inCombat && (Main.Settings == null || !Main.Settings.ShipSwitchInCombat))
                {
                    Main.LogError("[舰船] 战斗中不切换分档 —— 格子占位会变，"
                                  + "我没有证据表明战斗内的寻路网格会跟着干净重算。"
                                  + "请退出战斗再切；确实想试就在面板勾「允许战斗中换船（有风险）」。");
                    return false;
                }
                if (inCombat) Main.Log("[舰船] ⚠ 战斗中切换分档（你已勾选允许）—— 留意船的占位是否异常。");
            }
            catch { /* 判不出来就放行，别因为探测失败挡住功能 */ }

            try
            {
                var state = GetState(ship);
                if (state == null) { Main.LogError("[舰船] 拿不到 PartUnitState，无法改分档。"); return false; }

                var p = state.GetType().GetProperty("Size",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p == null || !p.CanWrite)
                { Main.LogError("[舰船] PartUnitState.Size 没有可写属性，游戏版本可能变了。"); return false; }

                var before = CurrentSize();
                p.SetValue(state, size, null);
                var after = CurrentSize();

                // 只报一条的闸复位，方便观察新分档下的多打
                StarshipChargesPatch.ResetLog();

                if (after != size)
                {
                    Main.LogError("[舰船] 设置后读回不一致：期望 " + size + " 实际 " + after
                                  + "。可能被别处覆盖了。");
                    return false;
                }
                Main.Log("[舰船] 座舰分档 " + before + " -> " + after
                         + "  （模型与格子占位由 vanilla 的 HandleUnitSizeChanged 处理）"
                         + "  ★这一项会写进存档，切回护卫舰再存即可还原★");
                return true;
            }
            catch (Exception e) { Main.LogError("[舰船] 改分档失败: " + e); return false; }
        }

        /// <summary>
        /// 拿实体上的 PartUnitState。GetStateOptional 是扩展方法，
        /// 直接反射调 PartUnitStateExtension.GetStateOptional 最稳。
        /// </summary>
        private static object GetState(object entity)
        {
            try
            {
                var ext = HarmonyLib.AccessTools.TypeByName("Kingmaker.UnitLogic.PartUnitStateExtension");
                if (ext != null)
                {
                    foreach (var m in ext.GetMethods(BindingFlags.Static | BindingFlags.Public))
                    {
                        if (m.Name != "GetStateOptional") continue;
                        var ps = m.GetParameters();
                        if (ps.Length != 1) continue;
                        if (!ps[0].ParameterType.IsInstanceOfType(entity)) continue;
                        return m.Invoke(null, new[] { entity });
                    }
                }
            }
            catch (Exception e) { Main.LogError("[舰船] GetStateOptional 反射失败: " + e.Message); }

            // 退路：直接找 Parts 里类型名为 PartUnitState 的那个
            try
            {
                var parts = entity.GetType().GetProperty("Parts",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var pv = parts != null ? parts.GetValue(entity, null) : null;
                var en = pv as System.Collections.IEnumerable;
                if (en != null)
                    foreach (var o in en)
                        if (o != null && o.GetType().Name == "PartUnitState") return o;
            }
            catch { }
            return null;
        }
    }
}
