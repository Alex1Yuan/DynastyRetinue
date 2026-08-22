using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 【诊断】海战里一次性把三件事的现场证据都取回来：相机高度、移动格、落点全息影像。
    ///
    /// ★为什么是运行时遍历，不是读代码★
    ///   这三样都在**视图层**，而视图层的东西"叫什么名字、挂在谁身上、用的哪个 mesh"
    ///   是 prefab 里的数据，不在 C# 里。静态反编译只能看到
    ///   `m_PointerCellDecal`、`TrySetGhosted` 这种符号，看不到它们运行时**实际长什么样**。
    ///   而恰恰是"实际长什么样"决定了能不能改、改哪里。
    ///   （2026-08-22 试过静态挖类型名，反射加载 Code.dll 直接爆栈，此路不通。）
    ///
    /// ★三个待答问题★
    ///   1. 相机太近  —— 要知道 FOV 和物理缩放哪一套在生效、当前值多少
    ///   2. 移动格 1×1 —— 假设：合法落点本来就隔一格（因为船占 2×2），
    ///                    但高亮只画了"中心格"，于是看着断断续续。要看 decal 的实际尺寸和间距。
    ///   3. 全息影像固定是护卫舰 —— 看它的 mesh 名。是船自己的 mesh ⇒ 有救；
    ///                    是写死的某个 prefab ⇒ 得另想办法。
    /// </summary>
    internal static class SpaceCombatProbe
    {
        /// <summary>名字里带这些词的对象值得看一眼。全小写，比较时也转小写。</summary>
        private static readonly string[] Keywords =
        {
            "decal", "ghost", "pointer", "highlight", "marker", "preview", "hologram", "cell"
        };

        /// <summary>最多打这么多个对象，防止把日志灌爆。</summary>
        private const int MaxObjects = 60;

        public static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== 海战现场诊断 ==========");

            try { sb.AppendLine(CameraProbe.Dump()); }
            catch (Exception e) { sb.AppendLine("相机读取失败: " + e.Message); }


            sb.AppendLine();
            ShipFacts(sb);
            sb.AppendLine();
            StarSystemShip(sb);
            sb.AppendLine();
            PathManagerTree(sb);
            sb.AppendLine();
            OneCellSized(sb);
            sb.AppendLine();
            SceneRoots(sb);
            sb.AppendLine();
            SceneObjects(sb);

            sb.AppendLine("========== 诊断结束 ==========");
            Main.Log(sb.ToString());
            Main.FlushLog(true);
        }

        /// <summary>
        /// 船本身的占位数据。**这是判断"移动格该多大"的地基** ——
        /// 如果 SizeRect 是 2×2，那么高亮画 1×1 就是显示层没跟上，而不是玩法如此。
        /// </summary>
        private static void ShipFacts(StringBuilder sb)
        {
            sb.AppendLine("-------- 座舰 --------");
            try
            {
                var game = Kingmaker.Game.Instance;
                var ship = game != null && game.Player != null ? game.Player.PlayerShip : null;
                if (ship == null) { sb.AppendLine("  拿不到 PlayerShip（不在海战里？）"); return; }

                sb.Append("  蓝图=").Append(ship.Blueprint != null ? ship.Blueprint.name : "?")
                  .Append("　朝向=").Append(ship.Orientation.ToString("F1")).Append("°");

                // SizeRect / Size / SizeScale 全用反射读 —— 它们分散在基类里，
                // 直接访问会因为类型解析问题编不过，而这里只是要个数值。
                foreach (string prop in new[] { "SizeRect", "Size", "SizeScale" })
                {
                    try
                    {
                        var g = AccessTools.PropertyGetter(ship.GetType(), prop);
                        if (g == null) continue;
                        object v = g.Invoke(ship, null);
                        sb.Append("　").Append(prop).Append("=").Append(v == null ? "null" : v.ToString());
                    }
                    catch { }
                }
                sb.AppendLine();

                // 分档 —— 相机自动分档要按它来
                try { sb.AppendLine("  当前分档=" + StarshipTool.CurrentSize()); } catch { }

                var view = ship.View;
                if (view != null && view.gameObject != null)
                {
                    var vt = view.gameObject.transform;
                    sb.AppendLine("  视图对象=" + PathOf(vt)
                                + "　世界坐标=" + vt.position.ToString("F2")
                                + "　缩放=" + vt.lossyScale.ToString("F3"));
                }

                // 全息影像和本体逐项对比。
                // ★为什么要比缩放★ 玩家反馈"全息影像还是护卫舰的"，但它的对象名分明是
                //   当前巡洋舰模型的克隆。若两者 mesh 相同、只是全息的缩放小一截，
                //   那看着就像"另一条更小的船" —— 那是缩放问题，不是模型选错。
                //   这两种病的修法完全不同，必须先分清。
                try
                {
                    foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                    {
                        if (go == null || !go.scene.IsValid()) continue;
                        if (go.transform.parent != null) continue;
                        if (go.name.IndexOf("UnitHologram", StringComparison.Ordinal) < 0) continue;

                        var ht = go.transform;
                        sb.Append("  全息影像=").Append(go.name)
                          .Append("　缩放=").Append(ht.lossyScale.ToString("F3"))
                          .Append("　激活=").Append(go.activeInHierarchy);
                        var hmf = go.GetComponentInChildren<MeshFilter>(true);
                        if (hmf != null && hmf.sharedMesh != null)
                            sb.Append("　mesh=").Append(hmf.sharedMesh.name)
                              .Append("　顶点数=").Append(hmf.sharedMesh.vertexCount);
                        sb.AppendLine();
                        break;
                    }

                    // 本体的顶点数 —— 和全息一比就知道是不是同一个模型。
                    // ★顶点数比 mesh 名可靠★ 两条船的 mesh 都叫 "Ship"，名字分不出来，顶点数能。
                    if (view != null)
                    {
                        var bmf = view.gameObject.GetComponentInChildren<MeshFilter>(true);
                        if (bmf != null && bmf.sharedMesh != null)
                            sb.AppendLine("  本体 mesh=" + bmf.sharedMesh.name
                                        + "　顶点数=" + bmf.sharedMesh.vertexCount
                                        + "　★两边顶点数不同 = 真的是两个模型★");
                    }
                }
                catch { }
            }
            catch (Exception e) { sb.AppendLine("  读取失败: " + e.Message); }
        }

        /// <summary>
        /// 星图上那条船。**在星图上点诊断才有内容**，海战里是空的。
        ///
        /// ★为什么它不跟着换船模★
        ///   海战的船模走 PartUnitViewSettings.PrefabGuid（我们的换船模就是改这个），
        ///   但星图那条船归 StarSystemStarshipView 管，而那个类**只做位置和朝向插值**：
        ///       m_VisualTransform.SetPositionAndRotation(mechanicsTransform.position, ...);
        ///   模型是它所在 GameObject 的子对象，写死在星图场景的 prefab 里，
        ///   跟 PrefabGuid 一点关系都没有。所以要换它，得先看清子树长什么样。
        /// </summary>
        private static void StarSystemShip(StringBuilder sb)
        {
            sb.AppendLine("-------- 星图上的座舰（在星图上点才有）--------");
            try
            {
                int found = 0;
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go == null || !go.scene.IsValid()) continue;

                    bool hit = false;
                    try
                    {
                        foreach (var c in go.GetComponents<Component>())
                        {
                            if (c == null) continue;
                            if (c.GetType().Name.IndexOf("StarSystemStarshipView",
                                    StringComparison.Ordinal) < 0) continue;
                            hit = true; break;
                        }
                    }
                    catch { }
                    if (!hit) continue;

                    found++;
                    sb.AppendLine("  ● " + PathOf(go.transform)
                                + "　激活=" + go.activeInHierarchy
                                + "　缩放=" + go.transform.lossyScale.ToString("F3"));
                    int printed = 0;
                    Walk(go.transform, 1, sb, ref printed);
                    if (found >= 2) break;      // 一般只有一条，防呆
                }
                if (found == 0)
                    sb.AppendLine("  没找到（不在星图上，或者这版的类名变了）");
            }
            catch (Exception e) { sb.AppendLine("  扫描失败: " + e.Message); }
        }

        /// <summary>一格的边长。实测：SpacePathEndDecal 的 scale 就是 1.35，落点框 2.70 = 正好两格。</summary>
        private const float CellSize = 1.35f;

        /// <summary>
        /// 按**几何特征**找可走格 —— 一格大小、贴着地面。
        ///
        /// ★为什么要换个找法★
        ///   前两轮都按名字筛（decal/cell/highlight/...），可走格一次都没命中；
        ///   把 UnitPathManager 整棵子树翻出来也只有路径终点和落点框。
        ///   说明它们要么名字完全不沾这些词，要么根本不在那棵树下。
        ///   但有一件事是确定的：**它们在屏幕上是一格大小、平铺在地面上的**。
        ///   于是改用几何条件去筛，绕开命名这个不可靠的前提。
        ///
        /// ★按名字归类计数★ 可走格有几十个，全打出来没意义；
        ///   "某个名字出现了 N 次"本身才是线索 —— N 能和图上数出来的绿块对上就找到了。
        /// </summary>
        private static void OneCellSized(StringBuilder sb)
        {
            sb.AppendLine("-------- 一格大小、贴地的对象（按名字归类）--------");
            try
            {
                var count = new Dictionary<string, int>(StringComparer.Ordinal);
                var sample = new Dictionary<string, string>(StringComparer.Ordinal);

                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                foreach (var go in all)
                {
                    if (go == null || !go.scene.IsValid()) continue;
                    if (!go.activeInHierarchy) continue;      // 只要正显示着的
                    var t = go.transform;
                    float sx = t.lossyScale.x, sz = t.lossyScale.z;

                    // 一格见方（留 15% 余量），且高度贴近战场平面
                    bool oneCell = sx > CellSize * 0.85f && sx < CellSize * 1.15f
                                && sz > CellSize * 0.85f && sz < CellSize * 1.15f;
                    if (!oneCell || Math.Abs(t.position.y) > 1.5f) continue;

                    int c;
                    count.TryGetValue(go.name, out c);
                    count[go.name] = c + 1;
                    if (!sample.ContainsKey(go.name))
                    {
                        string mat = "";
                        try
                        {
                            var r = go.GetComponentInChildren<Renderer>(true);
                            if (r != null && r.sharedMaterial != null) mat = "　mat=" + r.sharedMaterial.name;
                        }
                        catch { }
                        sample[go.name] = PathOf(t) + "　pos=" + t.position.ToString("F1") + mat;
                    }
                }

                if (count.Count == 0)
                {
                    sb.AppendLine("  一个都没有 —— 可走格多半不是独立对象，"
                                + "而是整片网格用 shader 一次画完（那样就没有'每格一个对象'可放大）。");
                    return;
                }
                foreach (var kv in count)
                    sb.AppendLine("  × " + kv.Value.ToString().PadLeft(3) + "　" + kv.Key + "　" + sample[kv.Key]);
            }
            catch (Exception e) { sb.AppendLine("  扫描失败: " + e.Message); }
        }

        /// <summary>
        /// 场景顶层对象清单。可走格既然不在 UnitPathManager 下，
        /// 那它归谁管？先看看这场海战里到底有哪些顶层 manager，缩小范围。
        /// </summary>
        private static void SceneRoots(StringBuilder sb)
        {
            sb.AppendLine("-------- 场景顶层对象 --------");
            try
            {
                var names = new List<string>();
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var go in all)
                {
                    if (go == null || !go.scene.IsValid()) continue;
                    if (go.transform.parent != null) continue;       // 只要根
                    string n = go.name + "(" + go.transform.childCount + ")";
                    if (seen.Add(n)) names.Add(n);
                }
                names.Sort(StringComparer.Ordinal);
                sb.AppendLine("  " + string.Join("　", names.ToArray()));
            }
            catch (Exception e) { sb.AppendLine("  扫描失败: " + e.Message); }
        }

        /// <summary>
        /// 把 UnitPathManager 整棵子树打出来（含未激活对象）。
        ///
        /// ★为什么专门加这一段★
        ///   第一轮诊断按关键词筛，只筛到了落点框 PointerCellDecal_Space，
        ///   **地图上那些散落的绿色可走格一个都没命中**。两种可能，改法天差地别：
        ///     · 它们是对象，只是名字不含 decal/cell —— 那就能逐个放大，好办
        ///     · 它们压根不是对象（一个大 quad + shader 采样格子数据一次画完）
        ///       —— 那就没有"每格一个对象"可改，得动材质参数或干脆放弃
        ///   落点框挂在 UnitPathManager 底下，可走格大概率也在那儿，所以整棵翻出来看。
        ///   ★包含未激活对象★ —— 池化的格子平时是 inactive 的，只看激活的会漏。
        /// </summary>
        private static void PathManagerTree(StringBuilder sb)
        {
            // ★海战归 ShipPathManager 管，不是 UnitPathManager★
            //   前两轮只翻了 UnitPathManager（9 个子节点，全是地面那套路径终点/ping），
            //   自然一个可走格都找不到。场景顶层清单里 ShipPathManager 挂着 **109 个**子节点 ——
            //   数量级就对上了"满地绿格"。SpaceCombatGrid 是网格本体，一并看。
            foreach (string mgr in new[] { "ShipPathManager", "SpaceCombatGrid", "UnitPathManager" })
                OneTree(sb, mgr);
        }

        private static void OneTree(StringBuilder sb, string rootName)
        {
            sb.AppendLine("-------- " + rootName + " 子树（含未激活）--------");
            try
            {
                GameObject root = null;
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                foreach (var go in all)
                {
                    if (go == null || go.name != rootName) continue;
                    if (!go.scene.IsValid()) continue;
                    if (go.transform.parent != null) continue;   // 顶层那个才是真身
                    root = go; break;
                }
                if (root == null) { sb.AppendLine("  没找到 " + rootName); return; }

                // 子节点太多时先归类 —— 109 个格子逐行打没有可读性，
                // 而"某个名字出现了 N 次"才是我们要的信息
                var t = root.transform;
                if (t.childCount > 12)
                {
                    var count = new Dictionary<string, int>(StringComparer.Ordinal);
                    var live = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var c = t.GetChild(i);
                        int n; count.TryGetValue(c.name, out n); count[c.name] = n + 1;
                        if (c.gameObject.activeInHierarchy)
                        { live.TryGetValue(c.name, out n); live[c.name] = n + 1; }
                    }
                    sb.AppendLine("  共 " + t.childCount + " 个直接子节点，按名字归类：");
                    foreach (var kv in count)
                    {
                        int on; live.TryGetValue(kv.Key, out on);
                        sb.AppendLine("    × " + kv.Value.ToString().PadLeft(3)
                                    + "（激活 " + on + "）　" + kv.Key);
                    }
                    // 再挑一个激活的，把它整枝打出来当样本
                    for (int i = 0; i < t.childCount; i++)
                    {
                        var c = t.GetChild(i);
                        if (!c.gameObject.activeInHierarchy) continue;
                        sb.AppendLine("  样本（第一个激活的）：");
                        int printed = 0;
                        Walk(c, 1, sb, ref printed);
                        break;
                    }
                    return;
                }

                int p = 0;
                Walk(t, 0, sb, ref p);
                sb.AppendLine("  共 " + p + " 个节点" + (p >= 200 ? "（已截断）" : ""));
            }
            catch (Exception e) { sb.AppendLine("  遍历失败: " + e.Message); }
        }

        private static void Walk(Transform t, int depth, StringBuilder sb, ref int printed)
        {
            if (t == null || printed >= 200) return;
            printed++;

            sb.Append("  ").Append(new string(' ', depth * 2)).Append(t.gameObject.activeInHierarchy ? "● " : "○ ")
              .Append(t.name)
              .Append("　scale=").Append(t.lossyScale.ToString("F2"))
              .Append("　pos=").Append(t.position.ToString("F1"));
            try
            {
                var mf = t.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) sb.Append("　mesh=").Append(mf.sharedMesh.name);
                var comps = t.GetComponents<Component>();
                var names = new List<string>();
                foreach (var c in comps)
                    if (c != null && !(c is Transform)) names.Add(c.GetType().Name);
                if (names.Count > 0) sb.Append("　[").Append(string.Join(",", names.ToArray())).Append("]");
            }
            catch { }
            sb.AppendLine();

            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb, ref printed);
        }

        /// <summary>
        /// 扫场景里名字带关键词的对象。**只扫激活的** —— 移动格和全息影像
        /// 只有在"下达移动指令时"才存在，所以这个按钮必须在**鼠标悬停出现绿框的那一刻**点，
        /// 否则扫出来是空的。这一点在按钮说明里也写了。
        /// </summary>
        private static void SceneObjects(StringBuilder sb)
        {
            sb.AppendLine("-------- 场景对象（名字含 decal/ghost/pointer/highlight/... 的）--------");
            int n = 0;
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<GameObject>();
                sb.AppendLine("  场景激活对象共 " + all.Length + " 个，逐个筛名字…");

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var go in all)
                {
                    if (go == null) continue;
                    string low = go.name.ToLowerInvariant();
                    bool hit = false;
                    for (int i = 0; i < Keywords.Length; i++)
                        if (low.IndexOf(Keywords[i], StringComparison.Ordinal) >= 0) { hit = true; break; }
                    if (!hit) continue;

                    // 同名对象往往有几十个（每个格子一个 decal）。
                    // 全打没意义，只打**第一个**并统计总数 —— 数量本身就是信息：
                    // "可走格 decal 有 N 个"能和地图上数出来的绿块对上。
                    if (!seen.Add(go.name)) { n++; continue; }
                    if (seen.Count > MaxObjects) { sb.AppendLine("  …（超过 " + MaxObjects + " 种名字，截断）"); break; }

                    sb.Append("  · ").Append(PathOf(go.transform))
                      .Append("　scale=").Append(go.transform.lossyScale.ToString("F2"))
                      .Append("　pos=").Append(go.transform.position.ToString("F1"));

                    // mesh 名 —— 全息影像那条就靠这个定性。
                    // ★必须传 true★ 包含未激活的子对象：TargetGhost00 第一轮一个 mesh 都没打出来，
                    //   而它明明在屏幕上显示着 —— 多半就是模型挂在未激活的子节点上（池化/延迟启用）。
                    try
                    {
                        var mf = go.GetComponentInChildren<MeshFilter>(true);
                        if (mf != null && mf.sharedMesh != null)
                            sb.Append("　mesh=").Append(mf.sharedMesh.name);
                        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
                        if (smr != null && smr.sharedMesh != null)
                            sb.Append("　skinnedMesh=").Append(smr.sharedMesh.name);
                        // 材质名也带上 —— "同一个 mesh 换材质"和"根本是另一个模型"要分得开
                        var r = go.GetComponentInChildren<Renderer>(true);
                        if (r != null && r.sharedMaterial != null)
                            sb.Append("　mat=").Append(r.sharedMaterial.name);
                    }
                    catch { }

                    // 组件类型 —— 想改它得先知道该 patch 谁
                    try
                    {
                        var comps = go.GetComponents<Component>();
                        var names = new List<string>();
                        foreach (var c in comps)
                            if (c != null && !(c is Transform)) names.Add(c.GetType().Name);
                        if (names.Count > 0) sb.Append("　组件=[").Append(string.Join(",", names.ToArray())).Append("]");
                    }
                    catch { }

                    sb.AppendLine();
                    n++;
                }

                sb.AppendLine("  命中 " + n + " 个对象，" + seen.Count + " 种不同名字。");
                if (n == 0)
                    sb.AppendLine("  ★一个都没命中 —— 多半是点按钮时绿格没显示。"
                                + "请在**鼠标悬停、地图上正显示绿色移动格**的时候点这个按钮。★");
            }
            catch (Exception e) { sb.AppendLine("  扫描失败: " + e.Message); }
        }

        /// <summary>拼出 a/b/c 形式的层级路径，最多往上 6 层 —— 再深就没有可读性了。</summary>
        private static string PathOf(Transform t)
        {
            try
            {
                var parts = new List<string>();
                int guard = 0;
                while (t != null && guard++ < 6) { parts.Insert(0, t.name); t = t.parent; }
                if (t != null) parts.Insert(0, "…");
                return string.Join("/", parts.ToArray());
            }
            catch { return "?"; }
        }
    }
}
