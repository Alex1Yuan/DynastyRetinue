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
        /// <summary>连续找不到目标多少次之后拉长间隔。星图刚加载时船视图可能还没生成，
        /// 允许重试几次；但不能让「一直找不到」退化成每秒一次全场景扫描。
        ///
        /// ★退避必须在离开操舰区域时清零★ 否则代价是玩家可见的：实测进星图后
        ///   前几次未命中（船视图还没生成）就进了 5 秒退避，于是**进星图后有五到八秒
        ///   显示的是原版船模**。清零之后每次新进入都重新拿到 3 次快速重试。</summary>
        private const int MissesBeforeBackoff = 3;
        private const float BackoffInterval = 5f;
        private static int _misses;
        private static bool _warned;

        /// <summary>
        /// 状态变化时才打一行 —— 这段每秒跑一次，无条件打会刷屏。
        ///
        /// ★为什么不是 LogVerbose★ 原来成功那行用的就是 LogVerbose，于是默认设置下
        ///   这个功能在日志里**完全不可见**：船模没换成的时候，分不清是"没覆盖到"
        ///   还是"整个功能失效了"。诊断信息不该带条件（走路掉帧那轮的教训）。
        ///
        /// ★为什么按 key 去重而不是只打一次★ 只打一次的话，从"生效"变成"失效"
        ///   的那次转变就看不见了，而那恰恰是最需要知道的。
        /// </summary>
        private static string _lastSay = "";
        private static void Say(string key, string msg)
        {
            if (_lastSay == key) return;
            _lastSay = key;
            Main.Log(msg);
        }

        /// <summary>
        /// 当前区域是不是「船区域」—— 星系图 / 太空战 / 全局地图。
        /// 星图那条船只在这些区域存在，步行地图上一定找不到。
        /// 判据与 RetinueLifecycle.InPartyArea 同源（同一个 BlueprintArea 上的兄弟属性）。
        /// </summary>
        private static bool InShipArea()
        {
            try
            {
                var area = Kingmaker.Game.Instance != null
                         ? Kingmaker.Game.Instance.CurrentlyLoadedArea : null;
                return area != null && area.IsShipArea;
            }
            catch { return false; }
        }

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

                // ★区域闸门★ —— 这是 1.5.20 修的那个卡顿的根因所在。
                //
                //   星图那条船（StarSystemStarshipView）只存在于 IsShipArea：
                //   星系图 / 太空战 / 全局地图。**步行地图上它根本不存在**，
                //   可 FindTarget() 不知道这件事，照样把整个场景扫到底再返回 null，
                //   下一秒再来一遍。而 FindTarget 的代价是
                //       O(场景全部 GameObject × 每个物体的全部组件)
                //   还要对每个组件做 GetType().Name（反射 + 字符串分配）。
                //
                //   于是：每个玩家、每张步行地图、每秒一次全场景反射扫描，
                //   而且 StarMapShipModel 默认是 true —— 和有没有卫兵、
                //   有没有换过船模全都无关。三位玩家报的
                //   「一个随从都没招也卡」「关掉 mod 就高 5~10 帧」正是它。
                //
                //   ★这也是我上一轮误判的原因★ 那次的 A/B 是「遣散全部卫兵」，
                //   而这段代码跟卫兵数量毫无关系，两臂都在付同样的钱，
                //   所以差值里看不见它。我却拿一个没有对照组的绝对帧数
                //   （47fps）判了「健康」。
                if (!InShipArea()) { _target = null; _appliedGuid = null; _misses = 0; _dumped = false; Say("gate", "[星图] 当前不是操舰区域（星系图/太空战/全局地图），跳过换船模。"); return; }

                string guid = CurrentPrefabGuid();
                if (string.IsNullOrEmpty(guid)) { Say("noguid", "[星图] 取不到当前船模 guid，跳过。"); return; }

                // ★缓存判断必须在扫描之前★
                //   原来这一句写在 FindTarget() 之后（`_appliedGuid == guid && _target == mf`），
                //   等于"已经换好了"的稳定状态每秒照样全场景扫一遍才发现无事可做。
                //   _target 是 UnityEngine.Object，被销毁时 != null 为 false，
                //   所以过图后它会自动失效并触发重扫，不需要额外的失效通知。
                if (_appliedGuid == guid && _target != null) return;

                var mf = FindTarget();
                if (mf == null)
                {
                    // 找不到就退避 —— 星图刚加载时船视图可能还没生成，
                    // 但不能因此每秒重复一次全场景扫描。连续失败就把间隔拉长。
                    _target = null; _appliedGuid = null;
                    if (++_misses >= MissesBeforeBackoff)
                    {
                        _nextCheck = now + BackoffInterval;
                        DumpCandidates();   // 摊开这张图上「名字像船」的对象，认出它是什么
                        Say("miss", "[星图] 在操舰区域里没找到 StarSystemStarshipView 的船体 mesh —— "
                                  + "换船模这一步没生效。已拉长重试间隔。");
                    }
                    return;
                }
                _misses = 0;

                Mesh mesh = LoadMesh(guid);
                if (mesh == null) return;

                // 第一次接管：记下原样，供还原
                if (_target != mf)
                {
                    _target = mf;
                    _origMesh = mf.sharedMesh;
                    _origScale = mf.transform.localScale;
                    // ★材质留底也放这里★ 原来它散在 Apply 里、只在"要撑大材质槽"时才记，
                    //   于是走别的分支时 Restore 就没得还。接管点才是唯一该记原样的地方。
                    try
                    {
                        var r0 = mf.GetComponent<Renderer>();
                        _origMats = r0 != null ? r0.sharedMaterials : null;
                    }
                    catch { _origMats = null; }
                }

                Apply(mf, mesh);
                _appliedGuid = guid;
                _lastSay = "ok";   // 下次再进入 gate/miss 分支时会重新打，看得见状态翻转
                Main.Log("[星图] 船模已换成 " + mesh.name
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

            var rd = mf.GetComponent<Renderer>();
            if (rd == null) return;
            var cur = rd.sharedMaterial;

            // ★诊断无条件、且放在任何 return 之前★
            //   1.5.29 我把它写在补材质槽那段之后，而那段在「槽位够用」时 return 掉整个
            //   Apply —— 常见路径下永远打不出来，白测一轮。这是同类错误的第三次
            //  （另两次：阈值把「值小」和「没测到」混同、成功日志用了 LogVerbose）。
            try
            {
                Main.Log("[星图] 原网格=" + (_origMesh != null ? _origMesh.name : "?")
                       + "　新网格=" + mesh.name
                       + "　材质=" + (cur != null ? cur.name : "(无)")
                       + "　着色器=" + (cur != null && cur.shader != null ? cur.shader.name : "(无)"));
                // ★顶点属性对比 —— 这条决定「描边效果能不能自己补出来」★
                //   Ship_outlined 这类"为描边烘焙过"的网格，通常把平滑法线预烤进
                //   UV2 / 切线 / 顶点色某个通道，着色器靠它把轮廓往外挤。
                //   如果它比普通网格多出某个通道，那份数据**可以在运行时给任意网格算出来**
                //  （按顶点位置合并法线再写回去），是成熟做法，不是没法做。
                //   多出来的那个通道就是要补的目标；如果两边通道完全一样，
                //   那说明差别在别处，补法线这条路也就不用试了。
                Main.Log("[星图] 顶点属性对比（决定描边能否自己补出来）："
                       + "\n    原 " + VtxDesc(_origMesh)
                       + "\n    新 " + VtxDesc(mesh));
                DumpColors(_origMesh);
                Main.FlushLog(true);
            }
            catch { }

            // ---- 1) 材质槽补齐到新网格的子网格数 ----
            //   Unity 规则：有几个材质就只画几个子网格，多出来的直接不画。
            //   实测两张图的船都是「子网格 1 / 材质槽 1」，所以这段目前不触发；
            //   留着是因为换成子网格更多的船模时它就是必需的。
            try
            {
                int need = mesh.subMeshCount;
                var mats = rd.sharedMaterials;
                if (mats != null && mats.Length > 0 && need > mats.Length)
                {
                    var grown = new Material[need];
                    for (int i = 0; i < need; i++) grown[i] = mats[i < mats.Length ? i : mats.Length - 1];
                    rd.sharedMaterials = grown;
                    Main.Log("[星图] 材质槽 " + mats.Length + " -> " + need + "（新网格子网格更多，不补齐船身会缺一块）。");
                }
            }
            catch (Exception e) { Main.LogError("[星图] 补材质槽失败: " + e.Message); }

            // ---- 2) 扇区图必须连材质一起换 ----
            //
            //   实测两张图的渲染完全不同：
            //     星系图  mesh=Ship           材质=StarSystem_Ship_Hologramm  着色器=Owlcat/Unlit
            //     扇区图  mesh=Ship_outlined  材质=GlobalMap_Ship_lines       着色器=Shader Graphs/FX_GlobalmapStarship
            //
            //   星系图那个是普通无光照着色器，喂任何网格都能正常画 —— 所以那边一直是好的。
            //   扇区图那个是给 Ship_outlined 量身定做的线框特效（材质名里就写着 _lines），
            //   靠预先烘焙进网格的数据出效果。塞普通高模进去画出来是一坨缺角的实心块，
            //   **不是我们哪里写错，是那套渲染不接受任意网格**。
            //
            //   所以扇区图上把材质也换成星系图那个 —— 用的是原版自己给船配的材质，
            //   同一个游戏、同一个用途（地图上的船影），只是换张图用，不新建任何资源。
            try
            {
                if (cur == null || cur.shader == null) return;
                if (cur.shader.name.IndexOf("Owlcat/Unlit", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (_holoMat == null)
                    {
                        // ★必须存副本，不能存原版那个实例★
                        //
                        //   Material 是 UnityEngine.Object，**场景卸载时会被销毁**，
                        //   而 Unity 重载了 ==：销毁后的对象和 null 比较返回 true。
                        //   1.5.31 我直接存了原实例，日志里就出现了这样的自相矛盾：
                        //       10:36:12  已记下通用船影材质「StarSystem_Ship_Hologramm」
                        //       10:36:19  （区域卸载）
                        //       10:36:22  还没缓存到通用船影材质
                        //   缓存唯一有用的时刻（跨区域）正好是它失效的时刻。
                        //
                        //   Instantiate 出的副本归我们所有，再 DontDestroyOnLoad 让它
                        //   不随场景走。只有一份材质，开销可以忽略。
                        //   ★不动原版实例★ —— 那是别的对象还在用的东西。
                        try
                        {
                            _holoMat = UnityEngine.Object.Instantiate(cur);
                            _holoMat.name = cur.name + " (retinue copy)";
                            UnityEngine.Object.DontDestroyOnLoad(_holoMat);
                            Main.Log("[星图] 已复制一份通用船影材质「" + cur.name + "」并跨场景保留，扇区图会用它。");
                        }
                        catch (Exception e2)
                        {
                            Main.LogError("[星图] 复制船影材质失败: " + e2.Message);
                        }
                    }
                    return;                       // 本来就能正常渲染，不动
                }

                if (Main.Settings != null && !Main.Settings.StarMapShipModelSectorMat)
                {
                    Main.Log("[星图] 扇区图材质替换已被设置关闭，保持原版线框轮廓。");
                    return;
                }

                if (_holoMat == null)
                {
                    Main.Log("[星图] 本图着色器是「" + cur.shader.name + "」，只认预烘焙的 "
                           + (_origMesh != null ? _origMesh.name : "原网格")
                           + "。还没缓存到通用船影材质，本次保持原样（去一次星系图即可缓存）。");
                    return;
                }

                var slots = rd.sharedMaterials;
                var one = new Material[slots != null && slots.Length > 0 ? slots.Length : 1];
                for (int i = 0; i < one.Length; i++) one[i] = _holoMat;
                rd.sharedMaterials = one;

                // ★把本图原材质的颜色搬过来★
                //
                //   几何问题（截断）已经被换着色器解决了，但颜色还是错的：
                //   StarSystem_Ship_Hologramm 是给**星系图的深色太空背景**调的，
                //   而扇区图是**浅色羊皮纸背景** —— 同一个材质换个底色环境就发闷发脏。
                //
                //   两边各取所长：着色器用星系图那个（它能吃任意网格），
                //   颜色用扇区图自己那个（它本来就是为这张图的背景调的）。
                //   我们改的是**自己的副本**，原版实例一根汗毛都不动。
                try
                {
                    string[] names = { "_Color", "_BaseColor", "_MainColor", "_TintColor", "_EmissionColor" };
                    var moved = new System.Text.StringBuilder();
                    foreach (var n in names)
                    {
                        if (!cur.HasProperty(n) || !_holoMat.HasProperty(n)) continue;
                        var c = cur.GetColor(n);
                        _holoMat.SetColor(n, c);
                        moved.Append(n).Append('=').Append(c.ToString()).Append(' ');
                    }
                    // 渲染队列也一起对齐 —— 半透明的排序错了会被地图盖住或糊成一片
                    _holoMat.renderQueue = cur.renderQueue;

                    Main.Log("[星图] 材质已换成通用船影，并搬来本图配色："
                           + (moved.Length > 0 ? moved.ToString() : "（两边没有同名颜色属性，保持原配色）")
                           + "　渲染队列=" + cur.renderQueue);
                }
                catch (Exception e3) { Main.LogError("[星图] 搬配色失败（模型仍然正确，只是颜色是星系图那套）: " + e3.Message); }
            }
            catch (Exception e) { Main.LogError("[星图] 换材质失败: " + e.Message); }
        }

        /// <summary>
        /// 把网格的顶点色采样打出来 —— 决定「线框数据能不能自己造」。
        ///
        /// ★为什么只差这一个通道就值得挖★ 实测 Ship_outlined 与战舰高模的唯一差别
        ///   就是 Color（Normal/Tangent/TexCoord0 两边都有）。所以 FX_GlobalmapStarship
        ///   读的一定是顶点色，问题只剩「那里面装的是什么」。
        ///
        /// ★怎么读这份采样★
        ///   · 三个一组循环出现 (1,0,0)(0,1,0)(0,0,1) ⇒ **重心坐标**，标准线框画法。
        ///     补法完全确定：把新网格按三角形拆开（每个顶点只属于一个三角形），
        ///     再按顺序刷上这三个颜色。不用猜。
        ///   · 全是同一个值 / 灰阶 / 别的图案 ⇒ 是遮罩或分区标记之类，
        ///     语义得另外查，补不出来就别硬填 —— 填错不会报错，只会画出另一种难看的东西。
        /// </summary>
        private static void DumpColors(Mesh m)
        {
            try
            {
                if (m == null) return;
                if (!m.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color)) return;
                var cols = m.colors;
                if (cols == null || cols.Length == 0) { Main.Log("[星图] 原网格声明了 Color 通道但取不到数据。"); return; }

                var sb = new System.Text.StringBuilder();
                sb.Append("[星图] 原网格顶点色采样（前 12 个，用来判断是不是重心坐标）：");
                int n = Math.Min(12, cols.Length);
                for (int i = 0; i < n; i++)
                {
                    if (i % 3 == 0) sb.Append("\n    三角形 ").Append(i / 3).Append(": ");
                    var c = cols[i];
                    sb.Append('(').Append(c.r.ToString("F2")).Append(',')
                      .Append(c.g.ToString("F2")).Append(',')
                      .Append(c.b.ToString("F2")).Append(',')
                      .Append(c.a.ToString("F2")).Append(") ");
                }
                // 有多少种不同的颜色 —— 重心坐标只会有 3 种
                var distinct = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < cols.Length && distinct.Count <= 8; i++)
                    distinct.Add(cols[i].r.ToString("F2") + "," + cols[i].g.ToString("F2") + ","
                               + cols[i].b.ToString("F2") + "," + cols[i].a.ToString("F2"));
                sb.Append("\n    全网格不同颜色数：").Append(distinct.Count > 8 ? "多于 8 种" : distinct.Count.ToString())
                  .Append("　★正好 3 种且循环出现 = 重心坐标，线框可以自己造；否则语义未知，别硬填★");
                Main.Log(sb.ToString());
            }
            catch (Exception e) { Main.LogError("[星图] 读顶点色失败: " + e.Message); }
        }

        /// <summary>把一个网格带哪些顶点通道摘成一行，用来和另一个网格对比。</summary>
        private static string VtxDesc(Mesh m)
        {
            if (m == null) return "(null)";
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(m.name).Append("  顶点=").Append(m.vertexCount)
                  .Append("  可读写=").Append(m.isReadable)
                  .Append("  通道:");
                var chans = new[]
                {
                    UnityEngine.Rendering.VertexAttribute.Normal,
                    UnityEngine.Rendering.VertexAttribute.Tangent,
                    UnityEngine.Rendering.VertexAttribute.Color,
                    UnityEngine.Rendering.VertexAttribute.TexCoord0,
                    UnityEngine.Rendering.VertexAttribute.TexCoord1,
                    UnityEngine.Rendering.VertexAttribute.TexCoord2,
                    UnityEngine.Rendering.VertexAttribute.TexCoord3,
                };
                foreach (var c in chans)
                    if (m.HasVertexAttribute(c)) sb.Append(' ').Append(c.ToString());
                return sb.ToString();
            }
            catch (Exception e) { return m.name + "（读通道失败：" + e.Message + "）"; }
        }

        /// <summary>星系图那个通用船影材质（Owlcat/Unlit）。见到一次就存下来给扇区图复用。
        /// ★不新建材质★ 复用原版已有的实例，不产生新资源、不进存档。</summary>
        private static Material _holoMat;

        /// <summary>接管前的材质数组，供 Restore 还原。在接管那一刻记录。</summary>
        private static Material[] _origMats;

        private static void Restore()
        {
            if (_target == null || _origMesh == null) return;
            try
            {
                _target.sharedMesh = _origMesh;
                _target.transform.localScale = _origScale;
                // 材质槽也要还回去 —— 我们可能把它撑大过（见 Apply 里补齐子网格那段）。
                // 不还的话原版低模只有几个子网格，却挂着一串重复材质，虽然不报错但是脏的。
                if (_origMats != null)
                {
                    var r = _target.GetComponent<Renderer>();
                    if (r != null) r.sharedMaterials = _origMats;
                }
            }
            catch { }
            _target = null; _origMesh = null; _appliedGuid = null; _origMats = null;
        }

        /// <summary>
        /// 找星图船的 MeshFilter。★按组件找，不按路径★ ——
        /// 路径里带 (Clone) 和层级名，换个版本就可能对不上；
        /// 而 StarSystemStarshipView 是那条船独有的组件，稳得多。
        /// </summary>
        /// <summary>
        /// 一次性把找到的船视图子树里**全部** MeshFilter 打出来。
        ///
        /// ★为什么需要★ 日志说「船模已换成 X」，玩家却报告星图上还是原版护卫舰。
        ///   两件事同时成立只有两种可能，而它们要写的代码完全不同：
        ///     A. 可见的那个 mesh **在这个子树里**，只是 FindTarget 取了第一个、取错了
        ///        —— ShipHologramPatch 的注释记过：同一条船上既有 mesh=Ship（1552 顶点，
        ///           全息用的低模）也有 imperial_cruiser_gothic（75989 顶点，本体）。
        ///     B. 可见的那个**不在**这个子树里，是另一个对象 —— 那得另外去找。
        ///   把子树整个摊开，一眼就能分辨，不用再猜。
        ///
        /// 每个区域只打一次（_dumped 由 gate 分支清零）。
        /// </summary>
        /// <summary>
        /// 找不到目标时，把场景里**名字像船**的对象摊开一次。
        ///
        /// ★为什么需要★ 实测：星系图上 FindTarget 能找到（对象叫 GlobalMap_Ship，
        ///   父级 Visual，带 StarSystemStarshipView），换模型正常；**扇区图（导航页）上
        ///   一次都找不到** —— 那里的船显然不带这个组件，或者根本是另一套对象。
        ///   而 DumpSubtree 只在"找到了"的时候打，找不到时反而什么都没有，
        ///   于是"扇区图上到底有什么"成了盲区。这段就是补这个盲区的。
        ///
        /// ★为什么按名字而不是按组件★ 按组件筛正是失败的那条路。
        ///   名字是我们唯一还握着的线索（星系图那个叫 GlobalMap_Ship）。
        /// </summary>
        private static void DumpCandidates()
        {
            if (_dumped) return;
            _dumped = true;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[星图] 没找到目标 —— 把场景里名字带 ship 的对象摊开（用来认出这张图上的船是什么）：");
                int n = 0;
                foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                {
                    if (go == null) continue;
                    if (go.name.IndexOf("ship", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (++n > 25) { sb.Append("\n    …（超过 25 个，其余省略）"); break; }

                    sb.Append("\n    ").Append(go.name);
                    try
                    {
                        var mf = go.GetComponent<MeshFilter>();
                        if (mf != null)
                            sb.Append("  mesh=").Append(mf.sharedMesh != null ? mf.sharedMesh.name : "(null)")
                              .Append("  顶点=").Append(mf.sharedMesh != null ? mf.sharedMesh.vertexCount : -1);
                    }
                    catch { }
                    try
                    {
                        sb.Append("  在场=").Append(go.activeInHierarchy);
                        var parent = go.transform.parent;
                        sb.Append("  父级=").Append(parent != null ? parent.name : "(根)");
                    }
                    catch { }
                    // 组件类型名 —— 认出它归谁管
                    try
                    {
                        sb.Append("  组件[");
                        foreach (var c in go.GetComponents<Component>())
                            if (c != null) sb.Append(c.GetType().Name).Append(' ');
                        sb.Append(']');
                    }
                    catch { }
                }
                if (n == 0) sb.Append("\n    （一个名字带 ship 的对象都没有）");
                Main.Log(sb.ToString());
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError("[星图] 候选摊开失败: " + e.Message); }
        }

        private static bool _dumped;

        private static void DumpSubtree(GameObject root)
        {
            if (_dumped) return;
            _dumped = true;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[星图] 船视图子树里的全部 MeshFilter（用来判断可见的是哪一个）：root=")
                  .Append(root.name);
                var all = root.GetComponentsInChildren<MeshFilter>(true);
                if (all == null || all.Length == 0) sb.Append("\n    （一个都没有）");
                foreach (var f in all)
                {
                    if (f == null) continue;
                    int verts = -1;
                    try { if (f.sharedMesh != null) verts = f.sharedMesh.vertexCount; } catch { }
                    bool rend = false, act = false;
                    try { var r = f.GetComponent<Renderer>(); rend = r != null && r.enabled; } catch { }
                    try { act = f.gameObject.activeInHierarchy; } catch { }
                    sb.Append("\n    ").Append(f.name)
                      .Append("  mesh=").Append(f.sharedMesh != null ? f.sharedMesh.name : "(null)")
                      .Append("  顶点=").Append(verts)
                      .Append("  渲染器开=").Append(rend)
                      .Append("  在场=").Append(act);
                }
                sb.Append("\n    ★可见的那一个应当是「渲染器开=True 且 在场=True」的。")
                  .Append("我们换的是列表里第一个不含 Marker/Decal 的。两者不一致 = 换错了对象。");
                Main.Log(sb.ToString());
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError("[星图] 子树摊开失败: " + e.Message); }
        }

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
                    // ★先摊开再挑★ 挑错了对象的话，只看"挑中了谁"永远发现不了，
                    //   必须看见候选全集才知道可见的那个是不是根本没被考虑。
                    DumpSubtree(go);
                    // 子树里第一个有 mesh 的就是船体（LineMarker/Decal 那些是 Quad，靠名字排除）
                    foreach (var f in go.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (f == null || f.sharedMesh == null) continue;
                        if (f.name.IndexOf("Marker", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (f.name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        return f;
                    }
                }

                // ── 兜底：扇区图（导航页）的船不带 StarSystemStarshipView ──
                //
                // ★开关必须挡在这里，不能只挡材质★
                //   1.5.31~33 我把开关做成「只控制换不换材质」，于是关掉之后走的是
                //   「Gothic 网格 + 原版线框着色器」—— 那是最坏的组合，比两边都不换还糟
                //  （玩家实测：关掉开关看到的不是原版护卫舰，是截断的模型）。
                //   关掉就该是**完全不碰**：网格不换、材质不换，原样留给原版。
                if (Main.Settings != null && !Main.Settings.StarMapShipModelSectorMat) return null;
                //
                // ★实测（1.5.26 诊断）★
                //   星系图：  Visual              [StarSystemStarshipView]
                //               └ GlobalMap_Ship   mesh=Ship            1898 顶点
                //   扇区图：  GlobalmapStarship   [Highlighter UnitMultiHighlight …]
                //               └ Ship_lp
                //                   └ GlobalMap_Ship  mesh=Ship_outlined  2335 顶点
                //
                //   上面那条按组件找的路只覆盖得到星系图，扇区图上一次都匹配不上 ——
                //   玩家看到的「导航页那条船一直是原版护卫舰」就是这么来的。
                //
                // ★为什么按叶子名而不是按根名★ 两张图的叶子**同名**（GlobalMap_Ship），
                //   一条规则覆盖两处；按根名就得写两套。
                //
                // ★为什么要挑祖先★ 场景里可能不止一条船（敌对舰队、别的自由贸易者）。
                //   优先取祖先链里带 GlobalmapStarship 的那个 —— 那是玩家自己的船。
                //   实在挑不出来才退回第一个，总比不换强。
                MeshFilter fallback = null;
                foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
                {
                    if (go == null || go.name != "GlobalMap_Ship") continue;
                    var mf2 = go.GetComponent<MeshFilter>();
                    if (mf2 == null || mf2.sharedMesh == null) continue;

                    bool mine = false;
                    for (var t = go.transform; t != null; t = t.parent)
                        if (t.name.IndexOf("GlobalmapStarship", StringComparison.OrdinalIgnoreCase) >= 0)
                        { mine = true; break; }

                    if (mine) return mf2;              // 确认是玩家的船，立刻用
                    if (fallback == null) fallback = mf2;
                }
                if (fallback != null) return fallback;
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
