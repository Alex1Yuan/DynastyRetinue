using System;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 【诊断】按**渲染帧**采样舰船视图的朝向和位置，判断"跳"到底出在哪一层。
    ///
    /// ★为什么不能靠读代码★
    ///   ViewInterpolationHelper.Interpolate 里有两条互斥分支：
    ///     HasOverriddenRotatablePart → 只算炮塔，**船体不转**
    ///     否则                        → 船体按 LerpAngle 帧间插值
    ///   舰船走哪一条取决于 OverrideRotatablePart 这个 GameObject 在 prefab 上有没有，
    ///   **只有运行时知道**。读代码只能推，推错了就又改错地方（这个问题上已经错过两次）。
    ///
    /// ★判据★
    ///   模拟步长 50ms、渲染约 16ms，所以一个模拟步跨约 3 个渲染帧。
    ///     · 有插值：每渲染帧朝向变化 ≈ 单步角度 / 3
    ///     · 没插值：两帧之间原地不动，第三帧一次跳完整个单步角度
    ///   位置同理 —— "整个船挪移一下"如果是真的，会表现为单帧位移远大于邻帧。
    /// </summary>
    internal static class ShipFrameProbe
    {
        private static float _lastYaw;
        private static Vector3 _lastPos;
        private static string _lastUid;
        private static int _logged;

        /// <summary>
        /// 单渲染帧朝向变化超过这个度数就记一笔。
        ///
        /// ★这个值曾经是 5，正好卡在要抓的东西上面★
        ///   模拟步 50ms、渲染约 16ms，一个模拟步跨约 3 帧。末尾那次 12.6° 的可疑跳变
        ///   如果**被插值**，就是 4.2°/帧 —— 差一点点就够不到 5，于是一条都不记；
        ///   如果**没被插值**，是一帧 12.6°，会记。两种结果本该泾渭分明，
        ///   可 2026-08-22 那次测下来只有 1 条（还是场景加载帧），**两种情况都解释得通**，
        ///   等于白测。降到 1.5° 后，插值过的会留下一串 ~4°，没插值的会留下孤零零一个大值。
        /// </summary>
        private const float YawJump = 1.5f;
        /// <summary>单渲染帧位移超过这个米数就记一笔。MaxSpeed 5 m/s ⇒ 正常约 0.08 m/帧。</summary>
        private const float PosJump = 0.3f;
        /// <summary>一次会话最多记这么多条，防止刷屏。</summary>
        private const int MaxLogs = 250;

        public static void Tick()
        {
            try
            {
                var s = Main.Settings;
                if (s == null || !s.WatchMomentum) return;
                if (_logged >= MaxLogs) return;

                var ship = FindPlayerShipView();
                if (ship == null) { _lastUid = null; return; }

                string uid = ship.name;
                float yaw = ship.transform.eulerAngles.y;
                Vector3 pos = ship.transform.position;

                if (uid != _lastUid) { _lastUid = uid; _lastYaw = yaw; _lastPos = pos; return; }

                float dy = Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw));
                float dp = Vector3.Distance(_lastPos, pos);
                _lastYaw = yaw; _lastPos = pos;

                // ★先滤掉加载/传送帧★
                //   2026-08-22 那次会话唯一记下的一条是「朝向 0.0°　位移 50.89　dt=0ms」——
                //   船一帧挪了 50 米，而 MaxSpeed 只有 5 m/s（正常约 0.08 m/帧）。
                //   那是场景加载完成的那一帧，不是转向问题。混在结果里只会让人误判。
                //   两个判据任一成立就跳过：dt 小到不像真帧，或位移大到物理上不可能。
                float dt = Time.deltaTime;
                if (dt < 0.001f || dp > 5f) return;

                if (dy < YawJump && dp < PosJump) return;

                _logged++;
                Main.Log("[帧] 单帧变化　朝向 " + dy.ToString("F1") + "°　位移 " + dp.ToString("F2")
                         + "　(dt=" + (dt * 1000f).ToString("F0") + "ms)"
                         + (_logged == MaxLogs ? "　★已达上限，后续不再记★" : ""));
            }
            catch { }
        }

        /// <summary>找玩家座舰的视图。没在海战里就返回 null。</summary>
        private static GameObject FindPlayerShipView()
        {
            try
            {
                var game = Kingmaker.Game.Instance;
                var ship = game != null && game.Player != null ? game.Player.PlayerShip : null;
                if (ship == null) return null;
                var view = ship.View;
                return view != null && view.gameObject != null ? view.gameObject : null;
            }
            catch { return null; }
        }

        public static void Reset() { _logged = 0; _lastUid = null; }
    }
}
