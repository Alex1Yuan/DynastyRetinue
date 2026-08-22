using System;
using System.Text;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 【诊断】把当前相机的缩放参数打出来。
    ///
    /// ★为什么需要★
    ///   相机有两套互斥的缩放：
    ///     FovMin/FovMax/FovDefault        —— 靠改视野角
    ///     PhysicalZoomMin/PhysicalZoomMax —— 靠真的挪远近，由 EnablePhysicalZoom 决定用不用
    ///   再加上 CameraRig 自己的 ZoomMin/ZoomMax。海战里到底哪一套在起作用、
    ///   当前值是多少，不看是不知道的 —— 而"拉高相机"要改的正是其中一个。
    ///   猜错的话改了没反应，还会以为是补丁没生效。
    /// </summary>
    internal static class CameraProbe
    {
        public static string Dump()
        {
            var sb = new StringBuilder();
            try
            {
                var rig = Kingmaker.View.CameraRig.Instance;
                if (rig == null) return "拿不到 CameraRig（不在游戏里？）";

                sb.Append("[相机] ZoomMin=").Append(rig.ZoomMin.ToString("F2"))
                  .Append("  ZoomMax=").Append(rig.ZoomMax.ToString("F2"));

                var z = rig.CameraZoom;
                if (z == null) { sb.Append("　CameraZoom 为 null"); return sb.ToString(); }

                sb.Append(Environment.NewLine)
                  .Append("       Fov  min=").Append(z.FovMin.ToString("F2"))
                  .Append(" max=").Append(z.FovMax.ToString("F2"))
                  .Append(" default=").Append(z.FovDefault.ToString("F2"))
                  .Append(Environment.NewLine)
                  .Append("       物理缩放 启用=").Append(z.EnablePhysicalZoom)
                  .Append(" min=").Append(z.PhysicalZoomMin.ToString("F2"))
                  .Append(" max=").Append(z.PhysicalZoomMax.ToString("F2"))
                  .Append(Environment.NewLine)
                  .Append("       ZoomLength=").Append(z.ZoomLength.ToString("F2"))
                  .Append(" 当前归一化位置=").Append(z.CurrentNormalizePosition.ToString("F3"));

                // ★最关键的一行★
                //   TickZoom() 的第一句是 `if (m_ZoomRoutine != null || ZoomLock || RecordLock) return;`
                //   只要这三个里有一个成立，整个缩放逻辑一开头就返回 ——
                //   FovMax / PhysicalZoomMin 算都不会算，改它们自然毫无反应。
                //   这能一次性解释"值确实写进去了，可画面纹丝不动"。
                sb.Append(Environment.NewLine)
                  .Append("       ★ZoomLock=").Append(z.ZoomLock)
                  .Append("　RecordLock=").Append(z.RecordLock);
                try
                {
                    var f = HarmonyLib.AccessTools.Field(typeof(Kingmaker.View.CameraZoom), "m_ZoomRoutine");
                    if (f != null)
                        sb.Append("　m_ZoomRoutine=").Append(f.GetValue(z) != null ? "在跑" : "null");
                }
                catch { }
                sb.Append("　—— 三者任一成立，TickZoom 就整个不执行★");

                // VcMain 的实际镜头值 —— 海战真正用的那个虚拟相机
                try
                {
                    var vc = GameObject.Find("VcMain");
                    sb.Append(Environment.NewLine).Append("       VcMain=").Append(vc != null ? "在" : "找不到");
                    if (vc != null)
                    {
                        foreach (var c in vc.GetComponents<Component>())
                        {
                            if (c == null) continue;
                            string tn = c.GetType().Name;
                            if (tn.IndexOf("Cinemachine", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            sb.Append("　组件=").Append(tn);
                            // m_Lens 是 struct 字段，里面的 FieldOfView 才是真正决定视野的值
                            var lf = c.GetType().GetField("m_Lens");
                            if (lf != null)
                            {
                                object lens = lf.GetValue(c);
                                var ff = lens != null ? lens.GetType().GetField("FieldOfView") : null;
                                if (ff != null)
                                    sb.Append("　lens.FieldOfView=")
                                      .Append(Convert.ToSingle(ff.GetValue(lens)).ToString("F1"));
                            }
                            break;
                        }
                    }
                }
                catch { }
                // PlayerScrollPosition 只有 setter，读不了 —— 用归一化位置代替

                // 相机实际高度 —— 最直观的那个数
                try
                {
                    var cam = Camera.main;
                    if (cam != null)
                        sb.Append(Environment.NewLine)
                          .Append("       主相机 世界高度 y=").Append(cam.transform.position.y.ToString("F2"))
                          .Append("  fov=").Append(cam.fieldOfView.ToString("F2"));
                }
                catch { }

                // 当前座舰分档 —— 分档自动调高要按它来
                try { sb.Append(Environment.NewLine).Append("       当前座舰分档=").Append(StarshipTool.CurrentSize()); }
                catch { }

                // ★场里到底有几个相机★
                //   玩家反馈"滚轮只缩放背景、船一直那么大"，而场景顶层确实有一个
                //   BackgroundCameraSpaceCombatComposer。如果背景和船分属不同相机，
                //   那改 CameraZoom（只作用于 VcMain 那一路）就只能动到其中一边 ——
                //   这会完全解释"值改进去了、高度也变了，可船看着没变"。
                //   cullingMask 是判据：船在哪个 layer，就归哪个相机管。
                try
                {
                    sb.Append(Environment.NewLine).Append("-------- 场内相机 --------");
                    var cams = UnityEngine.Object.FindObjectsOfType<Camera>();
                    foreach (var c in cams)
                    {
                        if (c == null) continue;
                        sb.Append(Environment.NewLine)
                          .Append("  ").Append(c.enabled ? "● " : "○ ").Append(c.name)
                          .Append("　depth=").Append(c.depth.ToString("F0"))
                          .Append("　fov=").Append(c.fieldOfView.ToString("F1"))
                          .Append("　y=").Append(c.transform.position.y.ToString("F2"))
                          .Append("　cullingMask=0x").Append(c.cullingMask.ToString("X"))
                          .Append("　clear=").Append(c.clearFlags)
                          .Append(c == Camera.main ? "　★main★" : "");
                    }
                }
                catch { }

                // 船在哪个 layer —— 和上面的 cullingMask 一对，就知道谁在画它
                try
                {
                    var game = Kingmaker.Game.Instance;
                    var ship = game != null && game.Player != null ? game.Player.PlayerShip : null;
                    var v = ship != null ? ship.View : null;
                    if (v != null && v.gameObject != null)
                    {
                        int layer = v.gameObject.layer;
                        sb.Append(Environment.NewLine)
                          .Append("  座舰 layer=").Append(layer)
                          .Append("(").Append(LayerMask.LayerToName(layer)).Append(")")
                          .Append("　对应位=0x").Append((1 << layer).ToString("X"))
                          .Append("　—— 哪个相机的 cullingMask 含这一位，船就归谁画");
                    }
                }
                catch { }

                // ★挖真正能"拉开距离"的那个钮★
                //   玩家要的是"滚轮真的改变相机和战场的距离"，不是靠 FOV 骗眼睛。
                //   Cinemachine 里决定这个的是 Body 组件：
                //     · CinemachineFramingTransposer 有 m_CameraDistance —— 字面意义的相机到目标距离
                //     · CinemachineTransposer 有 m_FollowOffset —— 跟随偏移向量
                //   Body 组件通常挂在虚拟相机的**隐藏子对象**上（Cinemachine 自己建的），
                //   所以要整棵子树翻，还得包含未激活的。
                //   拿到组件名、字段名和当前值，才谈得上改哪个、改多少。
                try
                {
                    sb.Append(Environment.NewLine)
                      .Append("-------- Cinemachine 组件（找 m_CameraDistance / m_FollowOffset）--------");
                    foreach (string rootName in new[] { "VcMain", "CameraRig" })
                    {
                        var root = GameObject.Find(rootName);
                        if (root == null)
                        {
                            sb.Append(Environment.NewLine).Append("  ").Append(rootName).Append(" 找不到");
                            continue;
                        }
                        sb.Append(Environment.NewLine).Append("  【").Append(rootName).Append("】");
                        DumpCm(root.transform, 0, sb);
                    }
                }
                catch { }
            }
            catch (Exception e) { return "读相机失败: " + e.Message; }
            return sb.ToString();
        }

        /// <summary>
        /// 递归打印子树里所有 Cinemachine 组件的数值字段。
        /// 只打 float / Vector3 —— 那才是能调的量；其余（曲线、引用）看了也没用。
        /// </summary>
        private static void DumpCm(Transform t, int depth, StringBuilder sb)
        {
            if (t == null || depth > 4) return;
            try
            {
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    string tn = c.GetType().Name;
                    if (tn.IndexOf("Cinemachine", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    sb.Append(Environment.NewLine).Append("    ").Append(new string(' ', depth * 2))
                      .Append(t.name).Append(" → ").Append(tn);

                    foreach (var f in c.GetType().GetFields(System.Reflection.BindingFlags.Public
                                                          | System.Reflection.BindingFlags.Instance))
                    {
                        try
                        {
                            if (f.FieldType == typeof(float))
                                sb.Append("　").Append(f.Name).Append("=")
                                  .Append(((float)f.GetValue(c)).ToString("F2"));
                            else if (f.FieldType == typeof(Vector3))
                                sb.Append("　").Append(f.Name).Append("=")
                                  .Append(((Vector3)f.GetValue(c)).ToString("F2"));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            for (int i = 0; i < t.childCount; i++) DumpCm(t.GetChild(i), depth + 1, sb);
        }
    }
}
