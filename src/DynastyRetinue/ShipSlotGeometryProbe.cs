using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 挂点**几何**诊断（第二轮：位置对不对，而不是有没有）。
    ///
    /// 背景：ShipMountFallback 借船脊(Dorsal)的 transform 合成了缺失的 Prow/Keel 挂点，
    /// 光矛不再从虚空开火了，但玩家反馈「是从船头射出来的但不是从炮塔」——
    /// 位置只是"能用"，不是"对"。要摆对就得知道船体上各挂点的实际空间分布。
    ///
    /// ★ 为什么必须运行时量 ★
    /// StarshipItemSlot 的坐标是美术在**船体 prefab** 上手工摆的：
    ///     StarshipView.cs:36   public List&lt;StarshipItemSlot&gt; ItemSlots = new List&lt;...&gt;();
    ///     StarshipView.cs:304  public void FillItemsSlots() { }        // ← 空方法
    /// 全树对 ItemSlots 只有三处引用（:36 定义、:216/:252 两处只读 FindAll），
    /// 代码从不写入。prefab 是二进制资源，**静态反编译看不到任何坐标数值**。
    ///
    /// ★ 参考系：一律用 StarshipView.transform ★
    /// 不要用 slot.transform.localPosition —— 它相对的是**未知的父级**
    /// （美术可能把挂点挂在船体根下，也可能挂在某个子部件下，代码里看不出来）。
    /// 统一换算到 StarshipView 局部空间：
    ///     viewLocal = view.transform.InverseTransformPoint(slot.transform.position)
    /// 依据：StarshipView.cs:85   mesh = base.gameObject.GetComponent&lt;MeshFilter&gt;().sharedMesh
    ///       StarshipView.cs:315  Gizmos.DrawSphere(base.transform.TransformPoint(frontHitPosition))
    /// 即"网格顶点空间 == StarshipView.transform 局部空间"，命中遮罩和包围盒都在这个空间里，
    /// 挂点换算过去才能和它们比。
    ///
    /// ★ 轴向约定（有实据，不是猜的）★
    ///     StarshipFxHitMask.cs:47-54   if (item.z &lt;= 0f) back… else front…      ⇒ +Z = 船艏
    ///     StarshipFxHitMask.cs:37-46   vector = -Vector3.right; dot ≥ 0.5 → left ⇒ -X = 左舷(Port)
    /// 所以 Port 应该 x&lt;0、Starboard 应该 x&gt;0、船艏在 +Z。
    /// 本诊断会拿当前船模的 Port/Starboard 挂点**实测验证**这条约定 ——
    /// 万一某个船模的美术反着摆，日志会直接报出来，不至于把舰首推到船尾去。
    ///
    /// 只读，不改任何东西。
    /// </summary>
    public static class ShipSlotGeometryProbe
    {
        public static void Dump()
        {
            try { DumpCore(); }
            catch (Exception e) { Main.LogError("[挂点几何] 诊断失败: " + e); }
        }

        // ============================================================ 主流程

        private static void DumpCore()
        {
            var ship = StarshipViewTool.PlayerShip;
            if (ship == null) { Main.LogError("[挂点几何] 拿不到玩家座舰。"); return; }

            object viewObj = Get(ship, "View");
            if (viewObj == null)
            {
                Main.LogError("[挂点几何] 拿不到 View —— 船没在场景里。"
                              + "★要在【太空战里】点★（改装界面那个是 ShipDollRoom 复制体，"
                              + "身上没有 StarshipView，也没有挂点）。");
                return;
            }
            var sv = FindStarshipView(viewObj as Component);
            if (sv == null) { Main.LogError("[挂点几何] 这个 View 上找不到 StarshipView 组件。"); return; }

            Transform root = sv.transform;              // ★ 全文唯一参考系 ★

            Main.Log("======== 挂点几何诊断 ========");
            Main.Log("  船模 prefab = " + (string.IsNullOrEmpty(StarshipViewTool.CurrentPrefab)
                                           ? "(原版模型)" : StarshipViewTool.CurrentPrefab)
                     + "   分档 = " + ship.Size);

            DumpFrame(sv, root);

            // ---- 1. 包围盒（StarshipView 局部空间）----
            Bounds hull; bool hasHull = LocalBounds(root, BaseRendererTransform(sv) ?? root, out hull, true);
            Bounds all;  bool hasAll  = LocalBounds(root, root, out all, false);

            if (hasHull) Main.Log("  船体包围盒(仅BaseRenderer子树) 中心" + V(hull.center) + " 尺寸" + V(hull.size)
                                  + "  z∈[" + F(hull.min.z) + "," + F(hull.max.z) + "]");
            else         Main.Log("  ★船体包围盒取不到（BaseRenderer 子树下没有 MeshFilter）★");
            if (hasAll)  Main.Log("  全部渲染物包围盒(含已挂武器/引擎) 中心" + V(all.center) + " 尺寸" + V(all.size)
                                  + "  z∈[" + F(all.min.z) + "," + F(all.max.z) + "]");

            Bounds bb = hasHull ? hull : all;
            bool hasBB = hasHull || hasAll;

            // ---- 2. 逐个挂点 ----
            var slots = CollectSlots(sv, root, bb, hasBB);
            if (slots.Count == 0) { Main.LogError("  这个船模一个 StarshipItemSlot 都没有。"); return; }

            Main.Log("  --- 挂点逐个（坐标均已换算到 StarshipView 局部空间）---");
            Main.Log("  约定：+Z=船艏  -Z=船尾  -X=左舷Port  +X=右舷Starboard"
                     + "（StarshipFxHitMask.cs:37-54）");
            slots.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Type, b.Type);
                return c != 0 ? c : b.Local.z.CompareTo(a.Local.z);
            });
            foreach (var s in slots) Main.Log("    " + s.Line);

            // ---- 3. 按类型聚合 ----
            Main.Log("  --- 按类型聚合（min/mean/max 的 z，以及 x 的均值）---");
            var byType = new Dictionary<string, List<SlotInfo>>();
            foreach (var s in slots)
            {
                List<SlotInfo> l;
                if (!byType.TryGetValue(s.Type, out l)) { l = new List<SlotInfo>(); byType[s.Type] = l; }
                l.Add(s);
            }
            foreach (var kv in byType)
            {
                float zmin = float.MaxValue, zmax = float.MinValue, zsum = 0f, xsum = 0f, ysum = 0f;
                foreach (var s in kv.Value)
                {
                    zmin = Mathf.Min(zmin, s.Local.z); zmax = Mathf.Max(zmax, s.Local.z);
                    zsum += s.Local.z; xsum += s.Local.x; ysum += s.Local.y;
                }
                int n = kv.Value.Count;
                Main.Log("    " + kv.Key.PadRight(12) + "×" + n
                         + "  z: min=" + F(zmin) + " mean=" + F(zsum / n) + " max=" + F(zmax)
                         + "   x均值=" + F(xsum / n) + "  y均值=" + F(ysum / n));
            }

            // ---- 4. 轴向自检 ----
            AxisSanity(byType);

            // ---- 5. 命中遮罩交叉验证 ----
            DumpHitMask(sv, root, bb, hasBB);

            // ---- 6. 现有开火点（StarshipFxLocator）----
            DumpLocators(sv, root);

            // ---- 7. 船艏位置的几种估计 ----
            EstimateProw(byType, bb, hasBB, root, slots);

            Main.Log("======== 诊断结束 ========");
        }

        // ============================================================ 参考系本身

        /// <summary>把参考系自己的可疑点先暴露出来：缩放、层级、FindMesh 会不会炸。</summary>
        private static void DumpFrame(Component sv, Transform root)
        {
            var sb = new StringBuilder();
            sb.Append("  参考系 StarshipView 挂在 GameObject '").Append(root.name).Append("'");
            sb.Append("\n    层级路径: ").Append(PathOf(root, null));
            sb.Append("\n    lossyScale=").Append(V(root.lossyScale))
              .Append("  localScale=").Append(V(root.localScale))
              .Append("  世界坐标=").Append(V(root.position));

            // StarshipView.cs:85 无空检查地解引用 GetComponent<MeshFilter>()，
            // 换的船模如果这个 GameObject 上没有 MeshFilter，vanilla 的 FindMesh() 会 NRE。
            var mf = root.GetComponent<MeshFilter>();
            sb.Append("\n    本体 MeshFilter: ").Append(mf == null ? "★没有★（vanilla FindMesh() 在这个船模上会空引用，StarshipView.cs:85）"
                                                                  : (mf.sharedMesh == null ? "有但 sharedMesh 为空" : "有，顶点数=" + mf.sharedMesh.vertexCount));
            var br = Get(sv, "BaseRenderer") as Renderer;
            sb.Append("\n    BaseRenderer: ").Append(br == null ? "null"
                     : (br.gameObject == root.gameObject ? "就在本体上" : "在子物体 '" + br.gameObject.name + "'"));
            var msh = Get(sv, "mesh") as Mesh;
            sb.Append("\n    mesh 字段: ").Append(msh == null ? "null（未缓存）" : msh.name + " 顶点数=" + msh.vertexCount);
            Main.Log(sb.ToString());
        }

        private static Transform BaseRendererTransform(Component sv)
        {
            var br = Get(sv, "BaseRenderer") as Renderer;
            return br == null ? null : br.transform;
        }

        // ============================================================ 挂点收集

        private class SlotInfo
        {
            public string Type;
            public Vector3 Local;       // StarshipView 局部空间
            public Transform T;         // 挂点本身，换算 localPosition 时要用
            public string Line;
        }

        private static List<SlotInfo> CollectSlots(Component sv, Transform root, Bounds bb, bool hasBB)
        {
            var result = new List<SlotInfo>();
            var list = Get(sv, "ItemSlots") as IEnumerable;
            if (list == null) return result;

            int idx = 0;
            foreach (var o in list)
            {
                var c = o as Component;
                if (c == null) { Main.Log("    [" + idx++ + "] (null 条目)"); continue; }

                string ty = "?";
                var tv = Get(c, "Type"); if (tv != null) ty = tv.ToString();

                Vector3 world = c.transform.position;
                Vector3 local = root.InverseTransformPoint(world);      // ★ 关键换算 ★

                var sb = new StringBuilder();
                sb.Append("[").Append(idx).Append("] ").Append(ty.PadRight(12));
                sb.Append(" 局部").Append(V(local));
                if (hasBB) sb.Append(" 归一").Append(V(Norm(local, bb)));
                sb.Append("\n         GO='").Append(c.name).Append("'");
                if (c.name.StartsWith("KGD_SyntheticSlot_")) sb.Append(" ←★本 mod 合成的★");
                sb.Append("  父级='").Append(c.transform.parent == null ? "(无)" : c.transform.parent.name).Append("'");
                sb.Append("  原始 localPosition").Append(V(c.transform.localPosition));
                sb.Append("\n         相对 view 的路径: ").Append(PathOf(c.transform, root));

                // 中间层若有旋转/缩放，localPosition 就和 view 空间不是一回事 —— 必须点出来
                string warp = WarpBetween(c.transform, root);
                if (warp != null) sb.Append("\n         ★中间层有变换: ").Append(warp)
                                    .Append("（所以 localPosition 不能直接当 view 空间坐标用）");

                result.Add(new SlotInfo { Type = ty, Local = local, T = c.transform, Line = sb.ToString() });
                idx++;
            }
            return result;
        }

        /// <summary>从 t 到 stop 之间，有没有非单位旋转 / 非 1 缩放。有就返回描述。</summary>
        private static string WarpBetween(Transform t, Transform stop)
        {
            var sb = new StringBuilder();
            for (var p = t; p != null && p != stop; p = p.parent)
            {
                bool rot = Quaternion.Angle(p.localRotation, Quaternion.identity) > 0.5f;
                bool scl = (p.localScale - Vector3.one).sqrMagnitude > 1e-6f;
                if (rot || scl)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(p.name).Append(rot ? " 旋转" + F(Quaternion.Angle(p.localRotation, Quaternion.identity)) + "°" : "")
                      .Append(scl ? " 缩放" + V(p.localScale) : "");
                }
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        // ============================================================ 轴向自检

        private static void AxisSanity(Dictionary<string, List<SlotInfo>> byType)
        {
            List<SlotInfo> port, star;
            byType.TryGetValue("Port", out port);
            byType.TryGetValue("Starboard", out star);
            if (port == null || star == null || port.Count == 0 || star.Count == 0)
            {
                Main.Log("  --- 轴向自检: 跳过（没有成对的 Port/Starboard 挂点，无法验证）---");
                return;
            }
            float px = 0f, sx = 0f;
            foreach (var s in port) px += s.Local.x; px /= port.Count;
            foreach (var s in star) sx += s.Local.x; sx /= star.Count;

            Main.Log("  --- 轴向自检 ---");
            Main.Log("    Port 平均 x=" + F(px) + "   Starboard 平均 x=" + F(sx));
            if (px < 0f && sx > 0f)
                Main.Log("    ✓ 与 StarshipFxHitMask.cs:37-46 的约定一致（-X=Port, +X=Starboard）"
                         + "  ⇒ +Z=船艏 这条可以放心用");
            else if (px > 0f && sx < 0f)
                Main.LogError("    ★左右反了★ 这个船模的美术把 Port 摆在 +X。"
                              + "整套约定在这个模型上是镜像的，+Z 很可能也是船尾 —— 船艏估计要取反 z。");
            else
                Main.LogError("    ★Port/Starboard 的 x 同号（" + F(px) + " / " + F(sx) + "）★"
                              + " 挂点不是按左右舷分开摆的，x 轴判据在这个模型上无效。");

            if (Mathf.Abs(px) < 1e-3f && Mathf.Abs(sx) < 1e-3f)
                Main.LogError("    ★两边 x 都≈0★ 舷炮挂点全在中线上，这个模型的挂点可能只是占位符。");
        }

        // ============================================================ 命中遮罩交叉验证

        /// <summary>
        /// StarshipFxHitMask 是烘焙在 ScriptableObject 资产里的（[CreateAssetMenu]，
        /// FillHitPositionsFromMesh 全树无调用方 ⇒ 只在编辑器里烘焙过）。
        /// 换了船模之后，这份遮罩很可能还是**原来那条船**的 ——
        /// 拿它的 z 范围和当前网格包围盒对一下就知道。
        /// 顺便：按 StarshipFxHitMask.cs:47 的构造，frontHitPositions 的 z 必然 &gt;0，
        /// 这是对"+Z=船艏"最直接的运行时佐证。
        /// </summary>
        private static void DumpHitMask(Component sv, Transform root, Bounds bb, bool hasBB)
        {
            var mask = Get(sv, "starshipFxHitMask");
            if (mask == null) { Main.Log("  --- 命中遮罩: 没有（starshipFxHitMask 为 null）---"); return; }

            Main.Log("  --- 命中遮罩 StarshipFxHitMask 交叉验证 ---");
            var mm = Get(mask, "mesh") as Mesh;
            Main.Log("    遮罩烘焙自网格: " + (mm == null ? "null" : mm.name + " 顶点数=" + mm.vertexCount));

            foreach (var name in new[] { "frontHitPositions", "backHitPositions", "leftHitPositions", "rightHitPositions" })
            {
                var en = Get(mask, name) as IEnumerable;
                if (en == null) { Main.Log("    " + name + ": 读不到"); continue; }
                int n = 0; float zmin = float.MaxValue, zmax = float.MinValue, xmin = float.MaxValue, xmax = float.MinValue;
                foreach (var o in en)
                {
                    if (!(o is Vector3)) continue;
                    var v = (Vector3)o; n++;
                    zmin = Mathf.Min(zmin, v.z); zmax = Mathf.Max(zmax, v.z);
                    xmin = Mathf.Min(xmin, v.x); xmax = Mathf.Max(xmax, v.x);
                }
                if (n == 0) { Main.Log("    " + name.PadRight(18) + " 空"); continue; }
                Main.Log("    " + name.PadRight(18) + "×" + n
                         + "  z∈[" + F(zmin) + "," + F(zmax) + "]  x∈[" + F(xmin) + "," + F(xmax) + "]");
                if (name == "frontHitPositions" && zmin <= 0f)
                    Main.LogError("      ★front 里出现 z≤0★ 与 StarshipFxHitMask.cs:47 的构造矛盾，"
                                  + "这份遮罩多半不是当前网格烘的。");
                if (hasBB && (zmax > bb.max.z + 0.01f || zmin < bb.min.z - 0.01f))
                    Main.LogError("      ★超出当前船体包围盒 z∈[" + F(bb.min.z) + "," + F(bb.max.z) + "]★"
                                  + " ⇒ 这份遮罩是**别的船模**的，受击特效位置也会不对。");
            }
        }

        // ============================================================ 现有开火点

        /// <summary>
        /// 真正决定"从哪儿开火"的是 StarshipFxLocator 的位置：
        ///     AbilityDeliverStarshipShot.cs:75  GetComponentsInChildren&lt;StarshipFxLocator&gt;()
        ///     AbilityDeliverStarshipShot.cs:77  FindAll(x =&gt; x.weaponSlotType == weaponSlot.Type
        ///                                                &amp;&amp; x.starshipWeaponType == weapon.Blueprint.WeaponType)
        ///     AbilityDeliverStarshipShot.cs:136 从 finalShuffledLocators[index].transform.position 发射
        /// 注意 :136 只取 position，**不取 rotation** —— 所以合成挂点只需要摆对位置，朝向无所谓。
        /// </summary>
        private static void DumpLocators(Component sv, Transform root)
        {
            Main.Log("  --- 现有开火点 StarshipFxLocator（这才是炮口实际位置）---");
            int n = 0;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().Name != "StarshipFxLocator") continue;
                n++;
                string ws = "?", wt = "?";
                var a = Get(c, "weaponSlotType"); if (a != null) ws = a.ToString();
                var b = Get(c, "starshipWeaponType"); if (b != null) wt = b.ToString();
                Vector3 local = root.InverseTransformPoint(c.transform.position);
                Main.Log("    slotType=" + ws.PadRight(11) + " weaponType=" + wt.PadRight(14)
                         + " 局部" + V(local) + "  GO='" + c.name + "'");
            }
            if (n == 0) Main.Log("    （一个都没有 —— 武器美术没挂上，此时开火点会退回 "
                                 + "context.Caster.EyePosition，AbilityDeliverStarshipShot.cs:69/140）");
            else Main.Log("    共 " + n + " 个。★对照上面的挂点表：光矛的 locator 落在哪儿，炮口就在哪儿★");
        }

        // ============================================================ 船艏估计

        private static void EstimateProw(Dictionary<string, List<SlotInfo>> byType,
                                         Bounds bb, bool hasBB, Transform root, List<SlotInfo> all)
        {
            Main.Log("  --- 船艏位置的几种估计（都在 StarshipView 局部空间）---");

            // A. 包围盒最前端 —— 最直接，前提是 +Z 确实是船艏
            if (hasBB)
                Main.Log("    A 包围盒法:   z=" + F(bb.max.z) + "  →  点" + V(new Vector3(0f, bb.center.y, bb.max.z))
                         + "   [船体最前端；若包围盒含天线/已挂武器会偏前]");

            // B. 最靠前的现有挂点
            float zmax = float.MinValue; SlotInfo front = null;
            foreach (var s in all) if (s.Local.z > zmax) { zmax = s.Local.z; front = s; }
            if (front != null)
                Main.Log("    B 最前挂点:   z=" + F(front.Local.z) + " (" + front.Type + ")  →  点"
                         + V(new Vector3(0f, front.Local.y, front.Local.z))
                         + "   [保证在船体上，但舷炮通常够不到船头]");

            // C. 舷侧收敛外推 —— 船体向船艏收窄，|x| 随 z 递减，外推到 |x|=0
            EstimateByConvergence(byType, bb, hasBB);

            // D. 船脊往前推到包围盒前端（当前 ShipMountFallback 用的那条路的"正确版"）
            List<SlotInfo> dorsal;
            if (byType.TryGetValue("Dorsal", out dorsal) && dorsal.Count > 0 && hasBB)
            {
                var d = dorsal[0].Local;
                var target = new Vector3(d.x, d.y, bb.max.z);
                Main.Log("    D 船脊前推:   从" + V(d) + " 推到" + V(target)
                         + "   [保留船脊的高度，只把 z 推到船体最前端 —— 观感上最像舰首炮]");
                // ShipMountFallback:169 是 SetParent(baseAnchor, false)，
                // 所以合成挂点的父级就是船脊挂点本身，localPosition 要在船脊的空间里算。
                Vector3 lp = dorsal[0].T.InverseTransformPoint(root.TransformPoint(target));
                Main.Log("      → 合成挂点父级 = 船脊挂点 '" + dorsal[0].T.name + "'，"
                         + "其 localPosition 应设为 " + V(lp));
            }

            Main.Log("    ★判读：先看上面「轴向自检」是否通过。不通过就别用任何带 +Z 的估计。");
        }

        /// <summary>
        /// C 法：舷炮 |x| 对 z 做最小二乘，外推到 |x|=0 的那个 z。
        /// 船体越靠近船艏越窄，这条线的零点就是中线汇聚处 ≈ 船艏尖端。
        /// 舷侧接近平行（b≈0）时外推会爆炸，所以要夹取并报警。
        /// </summary>
        private static void EstimateByConvergence(Dictionary<string, List<SlotInfo>> byType, Bounds bb, bool hasBB)
        {
            var pts = new List<Vector2>();   // (z, |x|)
            foreach (var key in new[] { "Port", "Starboard" })
            {
                List<SlotInfo> l;
                if (!byType.TryGetValue(key, out l)) continue;
                foreach (var s in l) pts.Add(new Vector2(s.Local.z, Mathf.Abs(s.Local.x)));
            }
            if (pts.Count < 3) { Main.Log("    C 舷侧收敛:   跳过（舷炮挂点少于 3 个，拟合没意义）"); return; }

            float sx = 0f, sy = 0f, sxx = 0f, sxy = 0f;
            foreach (var p in pts) { sx += p.x; sy += p.y; sxx += p.x * p.x; sxy += p.x * p.y; }
            int n = pts.Count;
            float den = n * sxx - sx * sx;
            if (Mathf.Abs(den) < 1e-6f) { Main.Log("    C 舷侧收敛:   跳过（舷炮 z 全相同，无法拟合）"); return; }
            float slope = (n * sxy - sx * sy) / den;          // d|x|/dz
            float icept = (sy - slope * sx) / n;

            float zRange = 0f, zmn = float.MaxValue, zmx = float.MinValue;
            foreach (var p in pts) { zmn = Mathf.Min(zmn, p.x); zmx = Mathf.Max(zmx, p.x); }
            zRange = zmx - zmn;

            Main.Log("    C 舷侧收敛:   |x| = " + F(icept) + " + " + F(slope) + "·z"
                     + "   (舷炮 z 跨度 " + F(zRange) + ")");
            if (slope >= -1e-4f)
            {
                Main.LogError("      ★斜率非负 —— 舷侧不收窄（平行舷或舷炮集中在船腰），此法在这个模型上失效★");
                return;
            }
            float zProw = -icept / slope;
            string note = "";
            if (hasBB && zProw > bb.max.z) { note = "  [已超出包围盒前端 " + F(bb.max.z) + "，建议夹取]"; }
            if (hasBB && zProw < zmx)      { note += "  [比最靠前的舷炮还靠后，可疑]"; }
            Main.Log("      → 外推船艏 z=" + F(zProw) + note);
        }

        /// <summary>把一个 view 空间的目标点换算成"挂在 anchor 下面"时该用的 localPosition。</summary>
        public static Vector3 ToLocalOfParent(Transform root, Vector3 viewLocalTarget, Transform parent)
        {
            return parent.InverseTransformPoint(root.TransformPoint(viewLocalTarget));
        }

        // ============================================================ 包围盒

        /// <summary>
        /// subtreeRoot 子树里所有网格，8 个角点变换到 root 局部空间后求 AABB。
        ///
        /// 为什么不用 Renderer.bounds：那是**世界轴对齐**的盒子，船一旋转就膨胀
        /// （45° 偏航时长度虚高约 41%）—— 当前 ShipMountFallback.HullLength():206-218
        /// 就是这么算的，只能当粗略量级，不能拿来定船艏坐标。
        ///
        /// excludeSlotAttachments：跳过挂在 StarshipItemSlot 底下的网格。
        /// 武器美术是 Instantiate(prefab, …, item3.transform)（StarshipView.cs:266）
        /// 挂上去的，算船体尺寸时必须排掉，否则"船体前端"会被炮管带跑。
        /// </summary>
        private static bool LocalBounds(Transform root, Transform subtreeRoot, out Bounds b,
                                        bool excludeSlotAttachments)
        {
            b = new Bounds();
            bool any = false;
            if (subtreeRoot == null) return false;

            foreach (var mf in subtreeRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (excludeSlotAttachments && UnderItemSlot(mf.transform, root)) continue;
                Accumulate(root, mf.transform, mf.sharedMesh.bounds, ref b, ref any);
            }
            foreach (var sm in subtreeRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (sm == null || sm.sharedMesh == null) continue;
                if (excludeSlotAttachments && UnderItemSlot(sm.transform, root)) continue;
                Accumulate(root, sm.transform, sm.sharedMesh.bounds, ref b, ref any);
            }
            return any;
        }

        /// <summary>t 到 stop 之间是否挂在某个 StarshipItemSlot 下面（即：是不是后挂上去的武器美术）。</summary>
        private static bool UnderItemSlot(Transform t, Transform stop)
        {
            for (var p = t; p != null && p != stop; p = p.parent)
                foreach (var c in p.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "StarshipItemSlot") return true;
            return false;
        }

        private static void Accumulate(Transform root, Transform owner, Bounds local, ref Bounds acc, ref bool any)
        {
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + (((i & 1) == 0) ? -e.x : e.x),
                    c.y + (((i & 2) == 0) ? -e.y : e.y),
                    c.z + (((i & 4) == 0) ? -e.z : e.z));
                Vector3 p = root.InverseTransformPoint(owner.TransformPoint(corner));
                if (!any) { acc = new Bounds(p, Vector3.zero); any = true; }
                else acc.Encapsulate(p);
            }
        }

        /// <summary>归一化到 [-1,1]：0=船体中心，±1=包围盒边界。对称船体读起来最直观。</summary>
        private static Vector3 Norm(Vector3 p, Bounds b)
        {
            Vector3 e = b.extents;
            return new Vector3(
                Mathf.Abs(e.x) < 1e-6f ? 0f : (p.x - b.center.x) / e.x,
                Mathf.Abs(e.y) < 1e-6f ? 0f : (p.y - b.center.y) / e.y,
                Mathf.Abs(e.z) < 1e-6f ? 0f : (p.z - b.center.z) / e.z);
        }

        // ============================================================ 杂项

        private static string PathOf(Transform t, Transform stop)
        {
            var parts = new List<string>();
            for (var p = t; p != null && p != stop; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return (stop != null ? "<view>/" : "") + string.Join("/", parts.ToArray());
        }

        private static string F(float f) { return f.ToString("F2"); }
        private static string V(Vector3 v) { return "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")"; }

        private static Component FindStarshipView(Component view)
        {
            if (view == null) return null;
            try
            {
                foreach (var c in view.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name == "StarshipView") return c;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// ★ 必须逐层 DeclaredOnly ★（沿用 ShipSlotProbe.Get 的写法）
        /// 不带 DeclaredOnly 时，基类和派生类同名成员（Entity.View / Owner / Blueprint 都是）
        /// 会抛 AmbiguousMatchException。
        /// </summary>
        private static object Get(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags DECL = BindingFlags.Instance | BindingFlags.Public
                                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    var f = t.GetField(name, DECL);
                    if (f != null) return f.GetValue(o);
                    var p = t.GetProperty(name, DECL);
                    if (p != null && p.CanRead) return p.GetValue(o, null);
                }
                catch { }
            }
            return null;
        }
    }
}
