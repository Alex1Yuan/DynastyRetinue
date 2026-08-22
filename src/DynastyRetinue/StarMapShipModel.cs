using System;
using Kingmaker.ResourceLinks;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 让星图上那条船也用当前座舰的模型。
    ///
    /// ================== 病灶 ==================
    /// 星图上的船挂在 SolarSystemVisualManager/StarSystemStarship(Clone) 下：
    ///
    ///     Visual  [StarSystemStarshipView, ...]
    ///       Ship_lp                                  ← lp = low poly
    ///         GlobalMap_Ship  scale=10.50  mesh=Ship ← 模型写死在这儿
    ///
    /// 对象名 StarSystemStarship(Clone) 不带船型，mesh 就是那个到处复用的通用 `Ship` 低模
    /// （和海战全息影像用的是同一个）。而 StarSystemStarshipView 这个类**只做位置和朝向插值**，
    /// 跟 PartUnitViewSettings.PrefabGuid 那套完全没关系 —— 所以换船模影响不到它。
    ///
    /// ================== 修法 ==================
    /// 按当前 PrefabGuid 把船模 prefab 载出来取 mesh，换到 GlobalMap_Ship 上。
    ///
    /// ★缩放必须重算★ 这是和全息影像最大的不同。
    ///   全息那边本体就在同一场景、同一坐标系，直接抄 lossyScale 就行；
    ///   星图上没有本体可抄，而通用低模和真实船模的**建模尺寸差得远**
    ///   （通用 Ship 是个小方块级别的占位，真实船模按米建）。
    ///   直接换 mesh 不动 scale，船会瞬间涨成遮住半个星系。
    ///   所以按包围盒对角线长度的比例反向缩放，保持视觉大小不变。
    ///
    /// ★为什么可以直接 new PrefabLink★
    ///   WeakResourceLink.AssetId 是 public 字段，Load() 内部就是
    ///   `BundledResourceHandle&lt;T&gt;.Request(AssetId, hold)` —— 走的是游戏自己的资源系统，
    ///   不引入任何新资源、不产生新 AssetId，也就碰不到存档那条红线。
    ///
    /// ★纯视觉★ 只改场景对象的 mesh 和 scale，不进存档、不影响联机。
    /// </summary>
    internal static class StarMapShipModel
    {
        /// <summary>已经按哪个 guid 换过了。换船模后这个值变，就会重做。</summary>
        private static string _appliedGuid;
        /// <summary>第一次接管前的原始 mesh 和缩放 —— 关掉开关要还原成它。</summary>
        private static Mesh _origMesh;
        private static Vector3 _origScale;
        private static MeshFilter _target;

        /// <summary>加载过的 mesh 缓存，避免每次进星图都走一遍资源系统。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, Mesh> _cache =
            new System.Collections.Generic.Dictionary<string, Mesh>(StringComparer.Ordinal);

        private static float _nextCheck;
        /// <summary>星图上船模不会频繁变，1 秒查一次足够。★这是每帧路径，必须节流★</summary>
        private const float CheckInterval = 1f;
        private static bool _warned;

        public static void Tick()
        {
            try
            {
                var s = Main.Settings;
                if (s == null) return;

                float now = Time.unscaledTime;
                if (now < _nextCheck) return;
                _nextCheck = now + CheckInterval;

                if (!s.StarMapShipModel) { Restore(); return; }

                var mf = FindTarget();
                if (mf == null) { _target = null; _appliedGuid = null; return; }

                string guid = CurrentPrefabGuid();
                if (string.IsNullOrEmpty(guid)) return;
                if (_appliedGuid == guid && _target == mf) return;   // 已经是对的

                Mesh mesh = LoadMesh(guid);
                if (mesh == null) return;

                // 第一次接管：记下原样，供还原
                if (_target != mf)
                {
                    _target = mf;
                    _origMesh = mf.sharedMesh;
                    _origScale = mf.transform.localScale;
                }

                Apply(mf, mesh);
                _appliedGuid = guid;
                Main.LogVerbose("[星图] 船模已换成 " + mesh.name
                              + "　缩放 " + _origScale.ToString("F2")
                              + " → " + mf.transform.localScale.ToString("F2"));
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[星图] 换船模失败（不影响其它功能）: " + e.Message); }
            }
        }

        /// <summary>
        /// 换 mesh 并按包围盒比例重算缩放，保持视觉大小不变。
        /// ★不这么做船会瞬间胀大★ —— 两个模型的建模尺寸根本不是一个量级。
        /// </summary>
        private static void Apply(MeshFilter mf, Mesh mesh)
        {
            float oldSize = _origMesh != null ? _origMesh.bounds.size.magnitude : 0f;
            float newSize = mesh.bounds.size.magnitude;
            mf.sharedMesh = mesh;
            if (oldSize > 0.0001f && newSize > 0.0001f)
                mf.transform.localScale = _origScale * (oldSize / newSize);
        }

        private static void Restore()
        {
            if (_target == null || _origMesh == null) return;
            try
            {
                _target.sharedMesh = _origMesh;
                _target.transform.localScale = _origScale;
            }
            catch { }
            _target = null; _origMesh = null; _appliedGuid = null;
        }

        /// <summary>
        /// 找星图船的 MeshFilter。★按组件找，不按路径★ ——
        /// 路径里带 (Clone) 和层级名，换个版本就可能对不上；
        /// 而 StarSystemStarshipView 是那条船独有的组件，稳得多。
        /// </summary>
        private static MeshFilter FindTarget()
        {
            try
            {
                foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                {
                    if (go == null) continue;
                    bool hit = false;
                    foreach (var c in go.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        if (c.GetType().Name.IndexOf("StarSystemStarshipView", StringComparison.Ordinal) < 0) continue;
                        hit = true; break;
                    }
                    if (!hit) continue;
                    // 子树里第一个有 mesh 的就是船体（LineMarker/Decal 那些是 Quad，靠名字排除）
                    foreach (var f in go.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (f == null || f.sharedMesh == null) continue;
                        if (f.name.IndexOf("Marker", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (f.name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        return f;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>当前座舰的船模 guid。没换过船模就用蓝图自带的。</summary>
        private static string CurrentPrefabGuid()
        {
            try
            {
                var game = Kingmaker.Game.Instance;
                var ship = game != null && game.Player != null ? game.Player.PlayerShip : null;
                if (ship == null || ship.ViewSettings == null) return null;
                return ship.ViewSettings.PrefabGuid;
            }
            catch { return null; }
        }

        /// <summary>按 guid 载入船模 prefab 并取出它的 mesh。结果缓存，只走一次资源系统。</summary>
        private static Mesh LoadMesh(string guid)
        {
            Mesh cached;
            if (_cache.TryGetValue(guid, out cached)) return cached;

            Mesh found = null;
            try
            {
                var link = new PrefabLink();
                link.AssetId = guid;
                var prefab = link.Load();
                if (prefab != null)
                {
                    foreach (var f in prefab.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (f == null || f.sharedMesh == null) continue;
                        // 取顶点最多的那个 —— 船体主模型，不是舷灯/挂件之类的小物件
                        if (found == null || f.sharedMesh.vertexCount > found.vertexCount)
                            found = f.sharedMesh;
                    }
                }
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[星图] 载入船模 " + guid + " 失败: " + e.Message); }
            }

            _cache[guid] = found;   // 失败也缓存 null，免得每秒重试
            return found;
        }
    }
}
