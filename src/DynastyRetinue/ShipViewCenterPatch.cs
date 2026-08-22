using System;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 把大船的**渲染位置**挪回它真正占的格子上。
    ///
    /// ================== 病灶 ==================
    /// 船的视图位置：
    ///     FromMechanicsToViewPosition(entity, pos, inBattle)
    ///         => pos + GetSizePositionOffset(entity, inBattle)
    ///         => 舰船走 GetSizePositionOffset(entity.SizeRect, entity.Forward)
    ///
    /// 占位在某条**世界轴**上的跨度是偶数时，中心正好落在格顶点上，
    /// 引擎必须在相邻两格里挑一个 —— 它恒定朝世界 +x / +z 挑。
    /// 跨度是奇数时中心本来就在格心，没有平局，该轴无残差。
    ///
    /// 问题在于：**这个"挑"钉死在世界轴上，不跟着船转**，而船体是刚性旋转的。
    /// 于是船一转向，两者错开
    ///
    ///     Δ(朝向) = e − Rot(朝向)·e ,   e = (Width 偶 ? 0.5 : 0,  Height 偶 ? 0.5 : 0)
    ///
    /// 0° 时 Rot 是恒等，Δ ≡ 0 —— 这是"0° 永远正确"的**数学原因**，
    /// 不是误差小到看不出，是误差恒等于零。
    ///
    /// 同理，W 为奇数的分档横向没有平局，左右恒定正确：
    ///     护卫舰 1×2（W=1 奇）、大巡 3×6（W=3 奇）→ 横向 e=0
    ///     巡洋   2×4（W=2 偶）→ 横向进平局，90°/180° 各差一格，一眼可见
    ///
    /// ================== 证据 ==================
    /// 玩家在 HUD 上逐个朝向指认修正量（把逻辑占位画成高亮格对照船模），
    /// 换算成世界坐标后全部落在 e − Rot·e 上：
    ///
    ///     0°   不用动          → (0, 0)      ✓
    ///     90°  左舷 1 格       → (0, +1)     ✓
    ///     180° 左舷 1 + 船尾 1 → (+1, +1)    ✓
    ///     270° 船尾 1 格       → (+1, 0)     ✓
    ///     135°/225°/315°       → 各 (+0.7,+0.7)，方向与玩家所述一致（左舷/船尾/右舷）
    ///     45°  公式算出仅 0.4 格且无横向分量 → 玩家八个朝向里唯独没提 45°，是**预测**不是拟合
    ///
    /// 同一个式子还被逆向工作流为**射界**独立推导出来过一次（同源病灶，不同症状）。
    ///
    /// ★为什么前两次启用都改坏了★
    ///   前两次的判据是「模型位置 vs GetBlockedNodes 占位中心」。那是**恒等式** ——
    ///   模型位置来自 GetSizePositionOffset，占位来自 GetBlockedNodes，两者内部同源，
    ///   互证永远得 (0,0)，对"船画在哪"零信息量。
    ///   这套坐标系里真正独立的参照只有两个：**能点的落点**、和**画面上的像素**。
    ///   这一版用的正是前者（玩家对着高亮格指认），不再是我自己推的量。
    ///
    /// ★只对 Width>1 生效★
    ///   护卫舰 1×2 各朝向玩家实测正确，一行都不碰。
    ///
    /// ★这个重载只用于舰船★
    ///   GetSizePositionOffset(entity, inBattle) 里非舰船走 GetSizePositionOffsetForGroundUnit。
    ///
    /// ★视图层★ 改的是"画在哪"，不是"占哪几格"。逻辑占位、寻路、判定都不动；
    ///   FromViewToMechanicsPosition 用同一个偏移反向减回去，两边始终自洽。
    /// </summary>
    [HarmonyPatch(typeof(Kingmaker.Code.Enums.Helper.SizePathfindingHelper), "GetSizePositionOffset",
                  new Type[] { typeof(IntRect), typeof(Vector3), typeof(bool) })]
    internal static class ShipViewCenterPatch
    {
        private static bool _warned;

        /// <summary>
        /// 诊断用旁路。置 true 时本补丁完全不介入，读到的就是**原版**偏移。
        /// 有它才能在同一帧里把「原版给了多少」和「我们补了多少」分开量，
        /// 否则读出来的永远是叠加后的结果，根本看不出原版对朝向是怎么处理的。
        /// </summary>
        internal static bool Bypass;

        /// <summary>
        /// 视图偏移的修正量 Δ（世界坐标，单位：格）。
        ///
        /// ================== 公式 ==================
        ///     Δ = e − Rot(朝向向下取整到 90° 的倍数)·e
        ///     e = (Width 偶 ? 0.5 : 0,  Height 偶 ? 0.5 : 0)
        ///
        /// **e** 是半格残差：占位在某条世界轴上跨度为偶数时，中心落在格线上，
        /// 引擎必须在相邻两格里挑一个，而它恒定朝世界 +x/+z 挑。跨度为奇数时中心
        /// 本来就在格心，无平局，该轴 e = 0。
        ///
        /// **向下取整到 90°** 是关键的一环：这个平局裁决按**象限**决定，
        /// 而象限边界比朝向落后 45° —— 所以 0°/45° 同值、90°/135° 同值，依此类推。
        /// 一度用过不取整的 Δ = e − Rot(朝向)·e，四个正交档对、四个斜向档全漂
        /// （45° 会算出 0.5 格左舷偏移，而 45° 原版本来就是对的）。
        ///
        /// ================== 怎么来的 ==================
        /// 先由玩家在 HUD 上逐档指认出八个修正量（把逻辑占位画成高亮格对照船模），
        /// 再用「把射界覆盖换算到船体坐标」交叉验证 ——
        /// 同一门炮在同族朝向上必须给出完全相同的数，差值就是船位置的误差。
        /// 这个方法在 135° 上**推翻了肉眼判断**并被玩家复测确认，之后 315°、225°
        /// 两档也是先算后验、一次通过。八档全部收敛后拟合出上面这个式子，零误差。
        ///
        /// 巡洋 2×4（e = 0.5, 0.5）逐档展开，与实测表逐位相同：
        ///     0°/45°   (0,0)      90°/135°  (0,1)
        ///     180°/225° (1,1)     270°/315° (1,0)
        ///
        /// ★护卫舰不碰★ 1×2 各朝向玩家实测正确，Width<=1 直接返回零。
        /// ★大巡自动覆盖★ 3×6 的 W 是奇数 → e=(0,0.5)，式子自然给出较小的修正量，
        ///   不需要再单独测一张表。
        /// </summary>
        internal static Vector3 Delta(IntRect size, Vector3 direction, float cell)
        {
            if (size.Width <= 1) return Vector3.zero;
            if (direction.sqrMagnitude < 0.0001f || cell <= 0.001f) return Vector3.zero;

            float ex = (size.Width % 2 == 0) ? 0.5f : 0f;
            float ez = (size.Height % 2 == 0) ? 0.5f : 0f;
            if (ex == 0f && ez == 0f) return Vector3.zero;

            int bucket = Mathf.RoundToInt(
                Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg / 45f) & 7;
            int quadrantDeg = (bucket / 2) * 90;          // ★向下取整到 90°★

            var e = new Vector3(ex, 0f, ez);
            var q = Quaternion.AngleAxis(quadrantDeg, Vector3.up);
            return (e - q * e) * cell;
        }


        private static void Postfix(IntRect size, Vector3 direction, ref Vector3 __result)
        {
            try
            {
                if (Bypass) return;
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.ShipViewCenterFix) return;

                float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                var d = Delta(size, direction, cell);
                if (d == Vector3.zero) return;
                __result += d;

                if (!_logged && s.WatchMomentum)
                {
                    _logged = true;
                    int bucket = Mathf.RoundToInt(
                        Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg / 45f) & 7;
                    Main.Log("[船位] 修正生效　尺寸=" + size.Width + "×" + size.Height
                           + "　朝向档=" + (bucket * 45) + "°　象限=" + ((bucket / 2) * 90) + "°"
                           + "　e=(" + ((size.Width % 2 == 0) ? 0.5f : 0f) + ","
                           + ((size.Height % 2 == 0) ? 0.5f : 0f) + ")"
                           + "　世界 Δ=(" + (d.x / cell).ToString("F2") + ","
                           + (d.z / cell).ToString("F2") + ") 格");
                    Main.FlushLog(true);
                }
            }
            catch (Exception ex2)
            {
                if (!_warned) { _warned = true; Main.LogError("[船位] 修正失败（不影响其它功能）: " + ex2.Message); }
            }
        }

        private static bool _logged;

        /// <summary>
        /// 立刻把玩家座舰挪到修正后的位置。
        ///
        /// ★为什么需要它★
        ///   船的视图位置只在「这一 tick 有位移」时才重算：
        ///       ViewInterpolationHelper.OnUnitSimulationTickCompleted
        ///         → if (Movable.PreviousSimulationTick.HasMotion || m_ForceUpdatePosition)
        ///   静止的船整条链都不走，所以改了设置画面纹丝不动 ——
        ///   玩家反馈"船头方向拉滑块也没反应"、"我刚测的就是初始进战斗时的"，正是这个原因：
        ///   刚进战斗船还没动过，怎么拖都是死的，只能先走一格再看，测起来非常别扭。
        ///   这里直接按游戏自己的公式（Position + 偏移，偏移已含本补丁的 Postfix）写一次 transform，
        ///   下次船真的移动时游戏会算出同样的值，不会打架。
        ///
        /// ★只覆盖水平面★ y 保持原样，避免把高度/浮动动画一起按死。
        /// ★只在改设置时调★ 不挂每帧，免得跟移动插值抢 transform。
        /// </summary>
        public static void ApplyNow()
        {
            try
            {
                var game = Kingmaker.Game.Instance;
                var ship = game != null && game.Player != null ? game.Player.PlayerShip : null;
                if (ship == null || ship.View == null || ship.View.gameObject == null) return;

                var off = Kingmaker.Code.Enums.Helper.SizePathfindingHelper.GetSizePositionOffset(ship, true);
                var t = ship.View.gameObject.transform;
                Vector3 p = ship.Position + off;
                t.position = new Vector3(p.x, t.position.y, p.z);
            }
            catch { }
        }
    }
}
