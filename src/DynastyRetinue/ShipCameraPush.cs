using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Enums;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 海战里给大船加宽视野角，让它不至于顶满屏幕。
    ///
    /// ================== 为什么是视野角，而不是把相机推远 ==================
    /// 1.1.19～1.1.22 用的是"在 CinemachineBrain.LateUpdate 之后把相机沿视线推远"。
    /// 画面效果确实达到了，但带来两个真实副作用（玩家实测）：
    ///   ① **血条等 UI 错位** —— 那些 UI 在自己的 LateUpdate 里按相机位置算屏幕坐标。
    ///      我们在 Brain 之后才动相机，早于我们跑的 UI 拿到的是旧位置，两边对不上。
    ///   ② **模型掉到低模** —— LOD 按相机到物体的**距离**切档。相机真退远了，LOD 就降级。
    ///      这不是 bug，是推远相机的必然代价。
    ///
    /// 换成视野角就同时躲开这两条：
    ///   · 相机**位置不变** ⇒ LOD 判据不变，模型不降级
    ///   · 改在 Brain **读取之前**（Prefix）⇒ Brain 把新视野写进 MainCamera，
    ///     之后所有 UI 读到的都是同一个值，不存在"谁先谁后"的错位
    ///
    /// ★为什么不是改 CameraZoom.FovMax★
    ///   试过，没效果。那个值要经 TickZoom 算成 lens.FieldOfView 再经 Brain 转给相机，
    ///   中间隔了两层。这里直接写虚拟相机的镜头，是链路上离结果最近、又还在 Brain 上游的点。
    ///
    /// ★纯视觉★ 每帧重写，不存任何值；关掉或离开海战下一帧即复原，不可能漏到地面。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipCameraPush
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Cinemachine.CinemachineBrain");
            return t == null ? null : AccessTools.Method(t, "LateUpdate");
        }

        private static bool Prepare()
        {
            bool ok = TargetMethod() != null;
            if (!ok) Main.LogError("[相机] 找不到 Cinemachine.CinemachineBrain.LateUpdate，"
                                 + "海战视野角不可用（不影响其它功能）。");
            return ok;
        }

        // 反射句柄缓存 —— 这是每帧路径，不能每帧 GetField
        private static GameObject _vcGo;
        private static Component _vcam;
        private static FieldInfo _lensField;
        private static FieldInfo _fovField;
        private static bool _warned;

        private static void Prefix()
        {
            try
            {
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.CamPushEnabled) return;
                if (!InSpaceCombat()) { _vcGo = null; _vcam = null; return; }

                float add = PushFor(StarshipTool.CurrentSize(), s);
                if (add <= 0.01f) return;

                // 接到滚轮上：CurrentNormalizePosition 由滚轮驱动，0=最远、1=最近。
                // 最远端给满，最近端归零（完全是原版画面）。
                float baseFov;
                try
                {
                    var rig = Kingmaker.View.CameraRig.Instance;
                    var zoom = rig != null ? rig.CameraZoom : null;
                    if (zoom == null) return;
                    float t = Mathf.Clamp01(zoom.CurrentNormalizePosition);
                    add *= 1f - t;
                    if (add <= 0.01f) return;
                    // 以原版这一帧本该用的视野为基准往上加，不是写死一个绝对值 ——
                    // 这样玩家滚轮带来的 FOV 变化仍然完整保留。
                    baseFov = Mathf.Lerp(zoom.FovMax, zoom.FovMin, t);
                }
                catch { return; }

                if (!Resolve()) return;
                object lens = _lensField.GetValue(_vcam);
                if (lens == null) return;
                _fovField.SetValue(lens, baseFov + add);
                _lensField.SetValue(_vcam, lens);   // m_Lens 是 struct，必须写回
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[相机] 视野角调整失败: " + e.Message); }
            }
        }

        /// <summary>拿到 VcMain 上虚拟相机的 m_Lens.FieldOfView 字段句柄，缓存住。</summary>
        private static bool Resolve()
        {
            if (_vcam != null && _lensField != null && _fovField != null && _vcGo != null) return true;
            try
            {
                _vcGo = GameObject.Find("VcMain");
                if (_vcGo == null) return false;
                foreach (var c in _vcGo.GetComponents<Component>())
                {
                    if (c == null) continue;
                    if (c.GetType().Name.IndexOf("CinemachineVirtualCamera", StringComparison.Ordinal) < 0) continue;
                    var lf = c.GetType().GetField("m_Lens");
                    if (lf == null) continue;
                    object lens = lf.GetValue(c);
                    var ff = lens != null ? lens.GetType().GetField("FieldOfView") : null;
                    if (ff == null) continue;
                    _vcam = c; _lensField = lf; _fovField = ff;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static float PushFor(Size size, Settings s)
        {
            if (size == Size.Cruiser_2x4) return s.CamPushCruiser;
            if (size == Size.GrandCruiser_3x6) return s.CamPushGrand;
            return 0f;
        }

        private static bool InSpaceCombat()
        {
            try
            {
                var g = Kingmaker.Game.Instance;
                return g != null && g.CurrentMode == Kingmaker.GameModes.GameModeType.SpaceCombat;
            }
            catch { return false; }
        }
    }
}
