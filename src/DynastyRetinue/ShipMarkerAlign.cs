using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;          // CustomGridNodeBase / CustomGridGraph 在这儿，不在 Pathfinding 下
using Pathfinding;                    // GraphNode / IntRect 才是 A* 包自己的
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 海战三件套对齐：把**绿格标记**和**鼠标取格**挪到船模上。
    ///
    /// ================== 原版三者的关系（反编译确认）==================
    ///   落点方块  ShipPathManager.SetPathMarkers:
    ///       sizeRect.ymax = ymin + Width - 1;              // 巡洋 (0,0,1,3) → (0,0,1,1)
    ///       off = GetSizePositionOffset(sizeRect);         // = (0.5, 0.5) 格，**恒定，不随朝向**
    ///       marker.position = node.position + off;  localScale *= Width;
    ///
    ///   鼠标      UnitPathManager:
    ///       GetCellOffsetForUnit(巡洋) = (±c/2, ±c/2)      // 符号跟着光标在 CurrentNode 的哪个象限
    ///       m_DecalOffset = (m_DecalScale == 1) ? 0 : m_SizeOffset;
    ///       选中谁   CurrentNode = nearest(光标世界坐标 − m_DecalOffset)
    ///       画在哪   圆圈位置     = CurrentNode.位置      + m_DecalOffset     ← 注意符号相反
    ///
    /// 于是原版的圆圈本来就是个 W×W 方块，中心落在「离光标最近的格顶点」；
    /// 而标记偏移 (0.5,0.5) 格指的正是同一个顶点。**两者天生咬合** ——
    /// 这就是 1.4.5 单独把 m_DecalOffset 归零后立刻"点击和绿格分家"的原因。
    ///
    /// ★符号相反的真实后果 —— 不是"一次改两处"★（1.4.10 的推理漏洞，1.4.11 更正）
    ///   我曾以为"给 m_DecalOffset 加个量就能一次改对两处"。代入数字后是反的：
    ///       CurrentNode = nearest(P − base − S)
    ///       圆圈        = CurrentNode + base + S  ≈  P
    ///   常量 S 在圆圈位置上**自己抵消**，圆圈始终钉在光标上。
    ///   抵消恰好是对的 —— 圆圈落在"被指令的锚点"平移后方块的中心。
    ///   加 S 真正改变的只有**选中哪个节点**。这正是玩家实测
    ///   "实际落点对齐了模型、可是渲染没跟上"的原因。
    ///   点亮哪一块绿格是**第三条链**，见 ShipMarkerPickPatch。
    ///
    /// ================== 修法 ==================
    ///       S = ShipGridPatch.TryGetBlockOrigin(船) × 格宽
    ///   把 S 同时加到 marker 偏移和 m_DecalOffset 上。两边同一个量，配对不破。
    ///   绿格铺格用的也是同一个 TryGetBlockOrigin —— **三者同源，不是各算各的**。
    ///
    /// S 的含义：船首那 W×W 块相对锚点的最小角。绿格/marker/光标本来都钉在
    /// 「锚点上的 W×W 块」（不随朝向转），而船体是刚性旋转的；把它们平移到船首块上，
    /// 就落到船模身上了。展开：
    ///       0°(0,0)　90°(0,−(W−1))　180°(−(W−1),−(W−1))　270°(−(W−1),0)
    ///       W=1 护卫舰恒 (0,0)（天生不用修）　W=2 (0,±1)　W=3 (0,±2)　**恒为整格**
    ///
    /// ★与玩家八档实测表互证★
    ///   TieBreakOffset 是玩家逐档肉眼指认**船模**拟合出来的，取负后
    ///   与本式在 0°/90°/180° 三档逐格相同（两条来源无共用中间量，不是自证）。
    ///   但那是拟合式，只在 W=2 上成立，故不作为实现，仅在自检日志里并排打印。
    ///
    /// ★历史★ 1.4.9 用拟合式算 S 并挪了 marker + 光标，但**漏了绿格铺格**，
    ///   玩家实测"实际落点对齐了模型、绿格还在原地"。1.4.10 起三者统一到本式。
    ///
    /// ★只能用一个全局朝向★
    ///   m_DecalOffset 是**单个向量**，而 ShipPath.Result 里每个落点各带自己的最终朝向。
    ///   所以统一用**座舰当前朝向**：不转向的落点完全吻合，需要转向的落点会有偏差。
    ///   要每个落点各自精确就得放弃"鼠标与标记严格配对"，那会重现 1.4.5 的分家。
    ///   Footprint 用的也是当前朝向，三者口径一致。
    ///
    /// ★选中技能时不介入★
    ///   此时 m_DecalScale == 1、m_DecalOffset 恒为零，光标是 1×1 的目标格拾取器，
    ///   射界由船的真实位置生成 —— 挪它只会把瞄准搞坏。
    ///
    /// ★纯显示/输入层★ 不碰占位、寻路、落点集合、命中判定，不进存档。
    /// </summary>
    internal static class ShipMarkerAlign
    {
        /// <summary>当前是否正在给绿格标记算偏移。只有这段窗口里才接管一参重载。</summary>
        internal static bool InMarkerScope;
        internal static StarshipEntity MarkerShip;

        private static bool _warned;
        private static bool _logged;

        /// <summary>
        /// 位移量 S（世界坐标）= 船首 W×W 块相对锚点的最小角 × 格宽。
        /// 拿不到就返回零 —— 此时绿格也不会挪（同一个来源），三者仍然自洽。
        ///
        /// ★与绿格同源，不是"两个都算一遍"★
        ///   ShipGridPatch.TryGetBlockOrigin 是唯一出处，绿格铺格用它、这里也用它。
        ///   1.4.9 用的是拟合式 e−Rot·e，虽然在 2×4 上数值相同，但那是两条独立算法，
        ///   任一处改动都可能悄悄分家；而且该式在 W≠2 时是错的
        ///   （W=3 会算出半格，真几何是整 2 格）。
        ///
        /// ★不要在这里调 GetSizePositionOffset★
        ///   1.4.7 读档直接崩，就是因为这里调了 GetSizePositionOffset(IntRect)，
        ///   而补丁正挂在那个重载上 → Postfix → 本函数 → 同一个重载 → 无限递归 → 栈溢出。
        /// </summary>
        internal static Vector3 Shift(MechanicEntity unit)
        {
            try
            {
                var ship = unit as StarshipEntity;
                if (ship == null) return Vector3.zero;
                if (ship.SizeRect.Width <= 1) return Vector3.zero;   // 护卫舰：原版本来就一致

                float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                if (cell <= 0.001f) return Vector3.zero;

                // ★每帧命中，必须便宜★ 位移只取决于 (舷宽, 朝向档)，两者都不常变。
                //   不缓存的话每帧都要走 TryGetBlockOrigin 里的字符串拼接 + 字典查找。
                //   1.4.15 海战变卡，这是其中一处。
                var fwd = ship.Forward;
                int bucket = UnityEngine.Mathf.RoundToInt(
                    UnityEngine.Mathf.Atan2(fwd.x, fwd.z) * UnityEngine.Mathf.Rad2Deg / 45f) & 7;
                int w = ship.SizeRect.Width;
                if (_sBucket == bucket && _sWidth == w && _sCell == cell) return _sVal;

                UnityEngine.Vector2Int org;
                Vector3 v = Vector3.zero;
                if (ShipGridPatch.TryGetBlockOrigin(ship, out org) && (org.x != 0 || org.y != 0))
                    v = new Vector3(org.x * cell, 0f, org.y * cell);

                _sBucket = bucket; _sWidth = w; _sCell = cell; _sVal = v;
                return v;
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[三件套对齐] 算位移失败: " + e.Message); }
                return Vector3.zero;
            }
        }

        private static int _sBucket = -999, _sWidth = -1;
        private static float _sCell = -1f;
        private static Vector3 _sVal;

        /// <summary>
        /// 吸附到 metagrid 锚点 —— 公式逐字抄自
        /// `PartStarshipNavigation.GetNodeInMetagrid`（PartStarshipNavigation.cs:426）。
        ///
        /// ★为什么圆圈会落在方块边界上★
        ///   落点锚点在一个**间距 W** 的子格上（巡洋 W=2），相位由座舰起点决定：
        ///       num = -startNode.X % W;   snapped.X = (cur.X + num) / W * W - num - xmin;
        ///   而圆圈画在 `CurrentNode + m_DecalOffset`，m_DecalOffset 是 ±半格，
        ///   于是圆圈落在**间距 1** 的顶点格上 —— 两个子格不同，有一半概率
        ///   落在方块的角或边中点而不是中心。
        ///   护卫舰 W=1 时 `m_DecalScale==1` 让 m_DecalOffset 恒为零，圆圈正好在格心，
        ///   所以原版从来没暴露过这个问题。
        ///
        /// ★所以不再靠算术，靠构造★
        ///   把圆圈和高亮块**都**吸附到同一个 metagrid 锚点，圆圈画在该锚点方块的中心。
        ///   这样「圆圈 == 高亮块 == 实际被指令的落点」是构造出来的，
        ///   不是靠调偏移量凑出来的 —— 这套算术我已经猜错三次了。
        ///
        /// 拿不到图/起点就原样返回，绝不瞎猜。
        /// </summary>
        internal static CustomGridNodeBase SnapToMetagrid(StarshipEntity ship, CustomGridNodeBase current)
        {
            try
            {
                if (ship == null || current == null) return current;
                var rect = ship.SizeRect;
                int w = rect.Width;
                if (w <= 1) return current;

                var graph = current.Graph as CustomGridGraph;
                if (graph == null) return current;

                // ★每帧命中★ 相位只取决于座舰起点，而船不动时它不变。
                //   不缓存的话每帧都要做一次 AstarPath.GetNearest 空间查询。
                //   世代号由 SetPathMarkers 递增 —— 船一动路径就重建，缓存自动失效。
                int gen = ShipPathContext.Generation;
                int nx, nz;
                if (_snapGen == gen && _snapW == w) { nx = _snapNx; nz = _snapNz; }
                else
                {
                    var active = AstarPath.active;
                    if (active == null) return current;
                    var start = active.GetNearest(ship.Position).node as CustomGridNodeBase;
                    if (start == null) return current;
                    var s2 = graph.GetNode(start.XCoordinateInGrid + rect.xmin,
                                           start.ZCoordinateInGrid + rect.ymin);
                    if (s2 == null) return current;
                    nx = -s2.XCoordinateInGrid % w;
                    nz = -s2.ZCoordinateInGrid % w;
                    _snapGen = gen; _snapW = w; _snapNx = nx; _snapNz = nz;
                }

                var snapped = graph.GetNode(
                    (current.XCoordinateInGrid + nx) / w * w - nx - rect.xmin,
                    (current.ZCoordinateInGrid + nz) / w * w - nz - rect.ymin);
                return snapped ?? current;
            }
            catch { return current; }
        }

        // internal：诊断要把吸附的中间量一起打出来（相位 nx/nz 是 metagrid 公式的核心，
        // 它一旦不稳，吸附结果就会在两个锚点之间来回跳）
        internal static int _snapGen = -1, _snapW = -1, _snapNx, _snapNz;

        /// <summary>
        /// 锚点 → 该锚点 W×W 方块中心的偏移。与原版 marker 用的
        /// `GetSizePositionOffset(压方 rect)` 逐字等价：((W-1)/2)·格宽。
        /// </summary>
        internal static Vector3 BlockCenter(StarshipEntity ship)
        {
            try
            {
                if (ship == null) return Vector3.zero;
                int w = ship.SizeRect.Width;
                if (w <= 1) return Vector3.zero;
                float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                if (cell <= 0.001f) return Vector3.zero;
                float h = (w - 1) * 0.5f * cell;
                return new Vector3(h, 0f, h);
            }
            catch { return Vector3.zero; }
        }

        internal static void LogOnce(Vector3 s)
        {
            try
            {
                if (_logged) return;
                var cfg = Main.Settings;
                if (cfg == null || !cfg.WatchMomentum) return;
                _logged = true;
                float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                if (cell <= 0.001f) cell = 1f;
                Main.Log("[三件套对齐] S = 船首块最小角 = 格("
                       + (s.x / cell).ToString("F2") + ", " + (s.z / cell).ToString("F2") + ")"
                       + "\n    同时加到『绿格标记』和『鼠标取格』上，两者一起挪到船模位置。"
                       + "\n    与绿格铺格同源（ShipGridPatch.TryGetBlockOrigin），必然一致。"
                       + "\n    ★看不到这条 = 补丁没执行★");
                Main.FlushLog(true);
            }
            catch { }
        }
    }

    /// <summary>
    /// 圈定"正在给绿格标记算偏移"的窗口。
    /// 一参重载 GetSizePositionOffset(IntRect) 是公共工具方法，别处也可能用；
    /// 只在这段窗口里接管，别的调用一律原样。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipMarkerScope
    {
        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var list = new System.Collections.Generic.List<MethodBase>();
            var t = AccessTools.TypeByName("Kingmaker.UI.PathRenderer.ShipPathManager");
            if (t == null) return list;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (m.Name == "SetPathMarkers") list.Add(m);
            return list;
        }

        private static bool Prepare()
        {
            int n = 0;
            foreach (var m in TargetMethods()) n++;
            Main.Log("[三件套对齐] 标记窗口挂载 " + (n > 0 ? "成功（" + n + " 个 SetPathMarkers 重载）"
                                                        : "失败：找不到 ShipPathManager.SetPathMarkers"));
            return n > 0;
        }

        private static void Prefix(StarshipEntity starship)
        {
            ShipMarkerAlign.InMarkerScope = true;
            ShipMarkerAlign.MarkerShip = starship;
        }

        /// <summary>窗口必须关掉，否则会泄漏到别的调用上去。</summary>
        private static void Postfix()
        {
            ShipMarkerAlign.InMarkerScope = false;
            ShipMarkerAlign.MarkerShip = null;
        }
    }

    /// <summary>绿格标记：在标记窗口内给偏移加上 S。</summary>
    [HarmonyPatch(typeof(Kingmaker.Code.Enums.Helper.SizePathfindingHelper), "GetSizePositionOffset",
                  new Type[] { typeof(IntRect) })]
    internal static class ShipMarkerOffsetPatch
    {
        private static void Postfix(ref Vector3 __result)
        {
            try
            {
                if (!ShipMarkerAlign.InMarkerScope) return;
                var cfg = Main.Settings;
                if (cfg == null || !cfg.ShipGridBySize) return;

                var s = ShipMarkerAlign.Shift(ShipMarkerAlign.MarkerShip);
                if (s == Vector3.zero) return;
                __result += s;
                ShipMarkerAlign.LogOnce(s);
            }
            catch { }
        }
    }

    /// <summary>
    /// 鼠标取格：给 m_DecalOffset 加上同一个 S。
    ///
    /// ★它只改"选中谁"★ 见 ShipMarkerAlign 头注 —— 常量 S 在圆圈位置上会自己抵消
    ///   （CurrentNode 减一次、画圆圈时又加回来），所以本补丁的作用是让**实际被指令的落点**
    ///   跟着挪；圆圈因抵消而自动落在平移后方块的中心，不需要额外处理。
    ///   点亮哪一块绿格是第三条链，见 ShipMarkerPickPatch。
    ///
    /// ★挂载点没被内联★
    ///   UpdateSizeOffset() 很短，本是 Mono 的内联候选。但 1.4.5 在同一个方法上把
    ///   m_DecalOffset 归零时，玩家实测点击行为确实变了 —— 已证明补丁能执行。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipPointerAlignPatch
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Kingmaker.UI.PathRenderer.UnitPathManager");
            return t == null ? null : AccessTools.Method(t, "UpdateSizeOffset");
        }

        private static bool Prepare()
        {
            var m = TargetMethod();
            Main.Log("[三件套对齐] 鼠标取格挂载 " + (m != null ? "成功 → UnitPathManager.UpdateSizeOffset()"
                                                             : "失败：找不到 UpdateSizeOffset"));
            return m != null;
        }

        private static FieldInfo _fDecal;
        private static FieldInfo _fScale;
        private static PropertyInfo _pSelected;
        private static bool _probed;
        private static bool _warned;

        private static void Postfix(object __instance)
        {
            try
            {
                var cfg = Main.Settings;
                if (cfg == null || !cfg.ShipGridBySize) return;
                if (__instance == null) return;

                var t = __instance.GetType();
                if (!_probed)
                {
                    _probed = true;
                    _fDecal = AccessTools.Field(t, "m_DecalOffset");
                    _fScale = AccessTools.Field(t, "m_DecalScale");
                    _pSelected = AccessTools.Property(t, "SelectedUnit");
                }
                if (_fDecal == null || _fScale == null || _pSelected == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Main.LogError("[三件套对齐] UnitPathManager 字段对不上（"
                                    + "m_DecalOffset=" + (_fDecal != null) + " m_DecalScale=" + (_fScale != null)
                                    + " SelectedUnit=" + (_pSelected != null) + "）—— 鼠标取格保持原样");
                    }
                    return;
                }

                // ★选中技能时 m_DecalScale==1、偏移恒为零★ 那是 1×1 的目标格拾取器，不能挪。
                if ((int)_fScale.GetValue(__instance) == 1) return;

                var s = ShipMarkerAlign.Shift(_pSelected.GetValue(__instance) as MechanicEntity);
                if (s == Vector3.zero) return;

                var cur = (Vector3)_fDecal.GetValue(__instance);
                // ★不是 cur + s，是直接**替换**成 s★
                //
                //   玩家的关键对比：选中轴炮时 1×1 光标**从不跳变**，只有移动会跳。
                //   差别就在 base：
                //       选技能   m_DecalScale==1 → m_DecalOffset = 0    → nearest(P)      = 光标底下那格
                //       移动     巡洋            → m_DecalOffset = base → nearest(P−base) = 常常不是那格
                //   base 是 GetCellOffsetForUnit 给的 (±c/2,±c/2) 象限向量，它选的是
                //   「离光标最近的**顶点**」所对应的块 —— 那是一套**浮动**的格子；
                //   而 marker 只存在于相位固定的 metagrid 锚点上。两套格子对不齐，
                //   于是有时候压根找不到对应的 marker，画面就停在别处。
                //
                //   把 base 去掉、只留 S，采样点就是 P−S，即「光标底下那一格」的逻辑坐标
                //   （绿格整体被挪了 S，所以要减回去）。这与 ShipMarkerPickPatch 的取格
                //   逐字相同 —— 高亮块和实际落点从此必然同一个节点。
                //
                //   1.4.5 单独把它归零曾导致「点击和绿格分家」，那是因为当时圆圈位置还
                //   依赖它；现在圆圈由 ShipDecalCenterPatch 显式定位，不再受影响。
                _fDecal.SetValue(__instance, s);
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[三件套对齐] 鼠标取格修正失败: " + e.Message); }
            }
        }
    }

    /// <summary>
    /// 高亮块：让"点亮哪一块绿格"也跟着挪。
    ///
    /// ================== 为什么单独需要它 ==================
    /// 海战里有**三条**互相独立的取格链，1.4.10 只修了前两条：
    ///
    ///   ① 铺满的绿格   ShipGridPatch → Footprint + org                     已挪
    ///   ② 圆圈 + 实际指令  UnitPathManager: nearest(P − base − S)           已挪
    ///   ③ 点亮的那一块   ShipPathNodeMarkersController:
    ///         PointerWorldCorrectedPosition => ClickEventsController.WorldPosition  ← **原始，一点没减**
    ///         m_CurrentNode = 它.GetNearestNodeXZ();
    ///         → ShipPathManager.UpdatePathNodeMarkers(m_CurrentNode) 点亮该节点的 marker
    ///
    /// 于是 ③ 比 ② 差了整整一个 S，玩家看到的就是"圆圈没落在高亮块中心"。
    ///
    /// ★为什么 S 不能靠 m_DecalOffset 传给 ②的圆圈★（1.4.10 的推理漏洞）
    ///   m_DecalOffset 在两条式子里符号相反：
    ///       选中谁 CurrentNode = nearest(P − m_DecalOffset)
    ///       画在哪 圆圈       = CurrentNode + m_DecalOffset
    ///   加常量 S 在圆圈位置上**自己抵消**（圆圈始终 ≈ 光标）。抵消是对的 ——
    ///   代入数字可验：圆圈恰好落在"被指令的锚点"的平移后方块中心。
    ///   真正没跟上的是 ③。
    ///
    /// ★挂载点安全★ UpdateCurrentNode 的 IL 是 43 字节，远超 Mono 约 20 字节的内联阈值。
    ///   （属性 getter 只有 34 字节但更接近阈值，且 getter 更容易被内联，故不挂它。）
    ///
    /// ★保持原语义★ 用 Prefix 抓进门时的 m_CurrentNode 做对比基准，
    ///   与原实现（拿局部变量 currentNode 比）完全一致 —— 不自己维护"上一次"，
    ///   免得 OnStop 把 m_CurrentNode 置 null 之后我们这边还留着陈旧值。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipMarkerPickPatch
    {
        private const string TypeName = "Kingmaker.Controllers.SpaceCombat.ShipPathNodeMarkersController";

        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName(TypeName);
            return t == null ? null : AccessTools.Method(t, "UpdateCurrentNode");
        }

        private static bool Prepare()
        {
            var m = TargetMethod();
            Main.Log("[三件套对齐] 高亮块挂载 " + (m != null
                ? "成功 → ShipPathNodeMarkersController.UpdateCurrentNode()"
                : "失败：找不到 UpdateCurrentNode —— 高亮块会比圆圈差一个 S"));
            return m != null;
        }

        private static FieldInfo _fNode, _fChanged;
        private static bool _probed, _warned;
        private static object _prev;
        private static int _lx = int.MinValue, _lz = int.MinValue;   // 光标格去重，避免每帧建串

        private static void Probe(Type t)
        {
            if (_probed) return;
            _probed = true;
            _fNode = AccessTools.Field(t, "m_CurrentNode");
            _fChanged = AccessTools.Field(t, "m_CurrentNodeChanged");
        }

        private static void Prefix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                Probe(__instance.GetType());
                _prev = _fNode != null ? _fNode.GetValue(__instance) : null;
            }
            catch { _prev = null; }
        }

        private static void Postfix(object __instance)
        {
            try
            {
                var cfg = Main.Settings;
                if (cfg == null || !cfg.ShipGridBySize) return;
                if (__instance == null) return;
                Probe(__instance.GetType());
                if (_fNode == null || _fChanged == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Main.LogError("[三件套对齐] ShipPathNodeMarkersController 字段对不上"
                                    + "（m_CurrentNode=" + (_fNode != null)
                                    + " m_CurrentNodeChanged=" + (_fChanged != null) + "）—— 高亮块保持原样");
                    }
                    return;
                }

                var ship = ShipPathContext.Current;
                var s = ShipMarkerAlign.Shift(ship);

                var game = Kingmaker.Game.Instance;
                var click = game != null ? game.ClickEventsController : null;
                if (click == null) return;

                // ★S 为零时也要走完诊断★
                //   0°/180° 档的 S = (0,0)，此前在这里直接 return，于是那些朝向
                //   一行日志都没有 —— 而玩家恰恰是在那些朝向看到跳变的。
                //   S=0 意味着本补丁完全不介入，那里的跳变是**原版自己的**：
                //   原版 marker 只存在于间距 W 的锚点上，却用 nearest(P)（间距 1）取格，
                //   W=2 时四格里有三格匹配不上。
                bool engaged = (s != Vector3.zero);

                var raw = Kingmaker.Pathfinding.GridAreaHelper.GetNearestNodeXZ(click.WorldPosition - s);
                // ★吸附到 metagrid★ marker 只存在于间距 W 的锚点上（ShipPath.Result 的键
                //   就是这些锚点，见 PartStarshipNavigation.cs:379 先 GetNodeInMetagrid 再查表）。
                //   不吸附的话 nearest(...) 落在非锚点格上，UpdatePathNodeMarkers 找不到匹配、
                //   什么都不亮 —— W=2 时四格里有三格会这样。
                var node = ShipMarkerAlign.SnapToMetagrid(ship, raw);
                // ★吸附必须无条件做，不能被 engaged 挡住★
                //   engaged 只表示「这个朝向要不要平移 S」，与「要不要吸附」无关。
                //   1.4.21~1.4.23 误把 SetValue 一起关在 engaged 里，于是 S=0 的朝向
                //   算了吸附、打了日志、却没写回去 —— 游戏继续用原版的 nearest(P)。
                //   而那个值四次里有三次不是 metagrid 锚点，UpdatePathNodeMarkers
                //   找不到匹配就 return（此前已 DisablePathNodeMarkers），高亮消失；
                //   下一格又碰上锚点，又出现 —— 这就是玩家看到的 A→B→A 摆动。
                //
                //   自证：日志里连续两次同一个锚点却 changed=True，说明字段没被我写过
                //   （_prev 拿到的是原版写的 raw 节点，不是我上次写的锚点）。
                _fNode.SetValue(__instance, node);
                _fChanged.SetValue(__instance, !ReferenceEquals(node, _prev));

                // ★判据：光标那一格必须落在高亮块内★
                //   块 = [锚点+S, 锚点+S+W-1]。在里面 = 2×2 粒度（改不掉）；
                //   在外面 = 我算错了。玩家实测是在外面，所以这里要把四个数一起打出来：
                //   光标格 / 减 S 后 / 吸附后 / 块范围 / changed 标志。
                //   changed 若为 false 而节点确实变了，UpdatePathNodeMarkers 就不会被调用，
                //   画面会停在上一格 —— 那是另一种病因，日志能区分。
                if (cfg.WatchMomentum && node != null)
                {
                    var cn = Kingmaker.Pathfinding.GridAreaHelper.GetNearestNodeXZ(click.WorldPosition);
                    int cx = cn != null ? cn.XCoordinateInGrid : -1;
                    int cz = cn != null ? cn.ZCoordinateInGrid : -1;
                    if (cx != _lx || cz != _lz)
                    {
                        _lx = cx; _lz = cz;
                        float cc = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                        if (cc <= 0.001f) cc = 1f;
                        int sx = UnityEngine.Mathf.RoundToInt(s.x / cc);
                        int sz = UnityEngine.Mathf.RoundToInt(s.z / cc);
                        int w = ship.SizeRect.Width;
                        int bx = node.XCoordinateInGrid + sx, bz = node.ZCoordinateInGrid + sz;
                        bool inside = cx >= bx && cx <= bx + w - 1 && cz >= bz && cz <= bz + w - 1;
                        Main.Log("[高亮块] 光标格(" + cx + "," + cz + ")"
                               + "　减S后(" + (raw != null ? raw.XCoordinateInGrid : -1) + "," + (raw != null ? raw.ZCoordinateInGrid : -1) + ")"
                               + "　吸附后(" + node.XCoordinateInGrid + "," + node.ZCoordinateInGrid + ")"
                               + "　S=(" + sx + "," + sz + ")"
                               + "　块 x:" + bx + "~" + (bx + w - 1) + " z:" + bz + "~" + (bz + w - 1)
                               + (inside ? "　OK 在块内" : "　BAD 在块外")
                               + "　changed=" + (!ReferenceEquals(node, _prev))+ (engaged ? "" : "　[S=0 本补丁未介入，此处为原版行为]")
                               // ★量画面，不是量逻辑★
                               //   前面 16/16 的「在块内」只证明了**逻辑**包含，
                               //   没人量过那个块**画在哪**。绿格若与逻辑格错开，两者就是两回事；
                               //   拿逻辑去否定玩家看到的画面，正是本项目栽过三次的恒等式陷阱。
                               //   差值的绝对值若超过「半块」，说明画面上确实在块外。
                               + "\n    光标世界=(" + (click.WorldPosition.x / cc).ToString("F2")
                               + "," + (click.WorldPosition.z / cc).ToString("F2") + ")"
                               + "  块中心=(" + ((node.Vector3Position.x + (w - 1) * 0.5f * cc + s.x) / cc).ToString("F2")
                               + "," + ((node.Vector3Position.z + (w - 1) * 0.5f * cc + s.z) / cc).ToString("F2") + ")"
                               + "  差=(" + ((click.WorldPosition.x - node.Vector3Position.x - (w - 1) * 0.5f * cc - s.x) / cc).ToString("F2")
                               + "," + ((click.WorldPosition.z - node.Vector3Position.z - (w - 1) * 0.5f * cc - s.z) / cc).ToString("F2") + ")"
                               + "  半块=" + (w * 0.5f).ToString("F2")
                               // 吸附算法的全部中间量。metagrid 公式：
                               //   nx = -起点.X % W;  锚点.X = (原始.X + nx) / W * W - nx - xmin
                               // 相位 nx/nz 一旦不稳，锚点就会在两个值之间来回跳 ——
                               // 这是「来回摆动」最可能的来源，而它只取决于座舰起点。
                               + "\n    相位 nx=" + ShipMarkerAlign._snapNx + " nz=" + ShipMarkerAlign._snapNz
                               + "　世代=" + ShipMarkerAlign._snapGen + "/" + ShipPathContext.Generation
                               + "　rect.min=(" + ship.SizeRect.xmin + "," + ship.SizeRect.ymin + ")"
                               + "　W=" + w);
                        Main.FlushLog(true);
                    }
                }
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[三件套对齐] 高亮块修正失败: " + e.Message); }
            }
        }
    }

    /// <summary>
    /// 圆圈落点：吸附到 metagrid 锚点，画在该锚点 W×W 方块的**中心**。
    ///
    /// ================== 病灶 ==================
    /// 原版 `m_CurrentDecalPosition = CurrentNode.Vector3Position + m_DecalOffset`
    /// （UnitPathManager.cs:373 / :427），而 `m_DecalOffset` 是 ±半格的象限向量。
    /// 于是圆圈落在**间距 1 的顶点格**上；而落点方块的中心在**间距 W 的子格**上
    /// （PartStarshipNavigation.cs:426 的 metagrid 公式）。
    /// 两个子格不同 ⇒ W=2 时圆圈有一半概率落在方块的角或边中点。
    ///
    /// ★为什么 Δ / S 修不了它★
    ///   Δ 是平移。平移会把圆圈和方块中心**一起**挪动，`mod W` 的余数不变，
    ///   所以任何常量都改变不了「圆圈落在哪个子格」。必须做**量化**（吸附），不是平移。
    ///   护卫舰 W=1 时两个子格重合，且 `m_DecalScale==1` 使 m_DecalOffset 恒为零
    ///   （UnitPathManager.cs:300），圆圈正好在格心 —— 所以原版从未暴露。
    ///
    /// ★构造而非算术★
    ///   位置 = SnapToMetagrid(CurrentNode) + BlockCenter + S
    ///   与高亮块用的是同一个吸附函数、同一个 S ⇒ 三者重合是构造出来的。
    ///
    /// ★只接管光标圆圈★
    ///   SetDecalPosition 有三个调用点（UnitPathManager.cs:374 / :428 是光标圆圈，
    ///   :678 是另一个 decal）。按 transform 精确门禁，别的一律放行。
    ///
    /// ★挂载点安全★ SetDecalPosition 的 IL 是 174 字节，远超 Mono 约 20 字节的内联阈值。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipDecalCenterPatch
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Kingmaker.UI.PathRenderer.UnitPathManager");
            return t == null ? null : AccessTools.Method(t, "SetDecalPosition");
        }

        private static bool Prepare()
        {
            var m = TargetMethod();
            Main.Log("[三件套对齐] 圆圈落点挂载 " + (m != null
                ? "成功 → UnitPathManager.SetDecalPosition()"
                : "失败：找不到 SetDecalPosition —— 圆圈会留在方块边界上"));
            return m != null;
        }

        private static FieldInfo _fDecal;
        private static bool _probed, _warned, _logged;

        private static int _logX = int.MinValue, _logZ = int.MinValue;
        private static bool _logHit;
        private static FieldInfo _fMarkers, _fMkGo, _fMkX, _fMkZ;
        private static bool _mkProbed;

        /// <summary>
        /// 取「真正被画出来的那个 W×W 绿格方块」的世界坐标。
        ///
        /// ShipPathManager 在 SetPathMarkers 里为每个可达锚点实例化一个 PathNodeMarker，
        /// 位置 = node.position + sizePositionOffset（已被 ShipMarkerOffsetPatch 加过 S），
        /// localScale *= Width。它就是玩家眼里那个方块。
        /// 直接抄它的 transform.position，圆圈落在方块中心便是构造出来的，
        /// 不依赖我对坐标的任何建模 —— 而那个建模已经连续对不上好几版了。
        ///
        /// 找不到对应锚点的 marker 时返回 null（调用方回退到自算坐标），
        /// 并且日志会明说"没找到"—— 那本身就是重要信息：
        /// 说明高亮块和圆圈根本不在同一个锚点上。
        /// </summary>
        private static Vector3? MarkerPos(CustomGridNodeBase anchor)
        {
            try
            {
                if (anchor == null) return null;
                int ax = anchor.XCoordinateInGrid, az = anchor.ZCoordinateInGrid;

                // ★这是 1.4.15 海战变卡的主凶★
                //   原实现每帧反射遍历整个 m_PathNodeMarkers（几百个节点，
                //   每个还要装箱结构体 + 2~3 次 FieldInfo.GetValue）＝ 每帧上千次反射。
                //   实际上结果只取决于 (世代号, 锚点)，而锚点只在光标换格时才变。
                //   一格缓存就够。
                int gen = ShipPathContext.Generation;
                if (_mkGen == gen && _mkX == ax && _mkZ == az) return _mkHit ? (Vector3?)_mkVal : null;

                var r = Lookup(ax, az);
                _mkGen = gen; _mkX = ax; _mkZ = az;
                _mkHit = r.HasValue; _mkVal = r ?? Vector3.zero;
                return r;
            }
            catch { return null; }
        }

        private static int _mkGen = -1, _mkX = int.MinValue, _mkZ = int.MinValue;
        private static bool _mkHit;
        private static Vector3 _mkVal;

        private static Vector3? Lookup(int ax, int az)
        {
            try
            {
                var t = AccessTools.TypeByName("Kingmaker.UI.PathRenderer.ShipPathManager");
                if (t == null) return null;
                var p = AccessTools.Property(t, "Instance");
                var inst = p != null ? p.GetValue(null) : null;
                if (inst == null) return null;

                if (!_mkProbed) { _mkProbed = true; _fMarkers = AccessTools.Field(t, "m_PathNodeMarkers"); }
                if (_fMarkers == null) return null;
                var list = _fMarkers.GetValue(inst) as System.Collections.IEnumerable;
                if (list == null) return null;

                foreach (var e in list)
                {
                    if (e == null) continue;
                    if (_fMkX == null)
                    {
                        var et = e.GetType();
                        _fMkX = AccessTools.Field(et, "XCoordinateInGrid");
                        _fMkZ = AccessTools.Field(et, "ZCoordinateInGrid");
                        _fMkGo = AccessTools.Field(et, "GameObject");
                    }
                    if (_fMkX == null || _fMkZ == null || _fMkGo == null) return null;
                    if ((int)_fMkX.GetValue(e) != ax) continue;
                    if ((int)_fMkZ.GetValue(e) != az) continue;
                    var go = _fMkGo.GetValue(e) as GameObject;
                    return go != null ? (Vector3?)go.transform.position : null;
                }
                return null;
            }
            catch { return null; }
        }

        private static void Prefix(object __instance, Transform decalTransform,
                                   GraphNode node, ref Vector3? overridePosition)
        {
            try
            {
                var cfg = Main.Settings;
                if (cfg == null || !cfg.ShipGridBySize) return;
                if (__instance == null || decalTransform == null) return;

                if (!_probed)
                {
                    _probed = true;
                    _fDecal = AccessTools.Field(__instance.GetType(), "m_CreatedPointerCellDecal");
                    _fScaleP = AccessTools.Field(__instance.GetType(), "m_DecalScale");
                }
                if (_fDecal == null)
                {
                    if (!_warned) { _warned = true; Main.LogError("[三件套对齐] 找不到 m_CreatedPointerCellDecal —— 圆圈保持原样"); }
                    return;
                }

                // ★选中技能时一律不介入★
                //   GetCellScaleByUnit（UnitPathManager.cs:282）在选中技能时返回 1，
                //   光标退化成 1×1 的目标格拾取器。此时把它吸附到 2×2 的 metagrid 锚点上，
                //   鼠标每移动两格才跳一次 —— 玩家实测「点击炮攻击时选单位的格子跳变」就是这么来的。
                //   舰炮瞄准必须保持逐格，射界也是按船的真实位置生成的，不能挪。
                if (_fScaleP != null && (int)_fScaleP.GetValue(__instance) == 1) return;

                // ★只接管光标圆圈★ 别的 decal 原样放行
                var pointer = _fDecal.GetValue(__instance) as Component;
                if (pointer == null || !ReferenceEquals(pointer.transform, decalTransform)) return;

                var ship = ShipPathContext.Current;
                if (ship == null)
                {
                    var g = Kingmaker.Game.Instance;
                    ship = g != null && g.Player != null ? g.Player.PlayerShip : null;
                }
                if (ship == null || ship.SizeRect.Width <= 1) return;

                var cur = node as CustomGridNodeBase;
                if (cur == null) return;
                var anchor = ShipMarkerAlign.SnapToMetagrid(ship, cur);
                if (anchor == null) return;

                var pos = (Vector3)anchor.Vector3Position
                        + ShipMarkerAlign.BlockCenter(ship)
                        + ShipMarkerAlign.Shift(ship);

                // ★以「渲染出来的那个绿格方块」为准，而不是以我算的坐标为准★
                //   1.4.12 的自检显示：我算的圆圈位置和我算的方块中心逐格吻合，
                //   但玩家看到的仍然不吻合 —— 说明我的模型和实际渲染对不上，
                //   再按模型调偏移量没有意义。
                //   ShipPathManager 的 marker 是**真正被画出来**的那个 W×W 方块，
                //   直接抄它的 transform.position，圆圈落在方块中心就是构造出来的。
                var mk = MarkerPos(anchor);
                if (mk.HasValue) pos = mk.Value;
                LogCorners(pointer, anchor, ship);

                // ★用引擎自己给的角点自标定 pivot★
                //   实测（1.4.16 日志）：decal 的四个角围出的区域中心
                //   与它的 transform.position **差半格** —— 轴心不在自己的中心上。
                //   所以前面几版把根节点放到方块中心，画出来仍然偏半个方块。
                //   这里不去猜偏移量，直接量：视觉中心 − 根节点 = pivot 偏移，减掉即可。
                //   与 pivot 在哪、缩放多少都无关，自动成立。
                pos -= PivotDelta(pointer, decalTransform,
                                  _fScaleP != null ? (int)_fScaleP.GetValue(__instance) : 1);

                // y 保持原样：原版随后会 CheckHeight 贴地，别把高度按死
                if (overridePosition.HasValue) pos.y = overridePosition.Value.y;
                overridePosition = pos;

                // ★别在每帧路径上拼字符串★ 只在锚点真的换了的时候才建串。
                if (cfg.WatchMomentum &&
                    (_logX != anchor.XCoordinateInGrid || _logZ != anchor.ZCoordinateInGrid || _logHit != mk.HasValue))
                {
                    _logX = anchor.XCoordinateInGrid; _logZ = anchor.ZCoordinateInGrid; _logHit = mk.HasValue;
                    {
                        float c = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                        if (c <= 0.001f) c = 1f;
                        var calc = (Vector3)anchor.Vector3Position + ShipMarkerAlign.BlockCenter(ship)
                                 + ShipMarkerAlign.Shift(ship);
                        Main.Log("[三件套对齐] 圆圈 锚点(" + anchor.XCoordinateInGrid + "," + anchor.ZCoordinateInGrid + ")"
                               + " 原始格(" + cur.XCoordinateInGrid + "," + cur.ZCoordinateInGrid + ")"
                               + "\n    我算的方块中心 = 格(" + (calc.x / c).ToString("F2") + "," + (calc.z / c).ToString("F2") + ")"
                               + "\n    真实 marker 位置 = " + (mk.HasValue
                                    ? "格(" + (mk.Value.x / c).ToString("F2") + "," + (mk.Value.z / c).ToString("F2") + ")"
                                    : "★没找到该锚点的 marker★（说明高亮块和圆圈根本不在同一个锚点上）"));
                        Main.FlushLog(true);
                    }
                }
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[三件套对齐] 圆圈落点修正失败: " + e.Message); }
            }
        }

        /// <summary>
        /// 罗盘（四箭头 path-end 标记）被父节点缩放放大了局部偏移，把它补回来。
        ///
        /// ================== 证据链 ==================
        /// 1.4.13 的日志证明 decal **根节点**位置完全正确：
        ///     锚点(262,258) 我算的方块中心 = 格(13.00,8.00)　真实 marker 位置 = 格(13.00,8.00)
        ///     锚点(264,256) 我算的方块中心 = 格(15.00,6.00)　真实 marker 位置 = 格(15.00,6.00)
        /// 逐格一致。可玩家看到的罗盘仍在方块边上 ⇒ 位移只可能来自 prefab 内部。
        ///
        /// `PointerCellDecal.m_PathEnd` 是子物体，而 SetDecalPosition 会写根节点缩放：
        ///     localScale.x = 1.35f * m_DecalScale;      // 护卫舰 1.35，巡洋 2.7，大巡 4.05
        /// 子物体的局部偏移被同比放大 ⇒ W 越大偏得越远。
        /// 护卫舰 W=1 时 scale 就是设计值，所以原版从来没暴露过。
        ///
        /// ★修法★ localPosition = 原始值 / m_DecalScale，
        ///   让它渲染出来的世界偏移与护卫舰上一致 —— 保留美术意图，只去掉放大。
        ///
        /// ★必须先存原始值★ 每帧都会跑，不存的话第二帧就拿被改过的值再除一次，
        ///   罗盘会一路缩向中心。用 instanceID 作键，decal 被重建时自动重新采样。
        /// </summary>
        private static void Postfix(object __instance, Transform decalTransform)
        {
            try
            {
                var cfg = Main.Settings;
                if (cfg == null || !cfg.ShipGridBySize) return;
                if (__instance == null || decalTransform == null) return;
                if (_fDecal == null) return;

                var pointer = _fDecal.GetValue(__instance) as Component;
                if (pointer == null || !ReferenceEquals(pointer.transform, decalTransform)) return;

                if (!_peProbed)
                {
                    _peProbed = true;
                    _fPathEnd = AccessTools.Field(pointer.GetType(), "m_PathEnd");
                    _fScale = AccessTools.Field(__instance.GetType(), "m_DecalScale");
                }
                if (_fPathEnd == null || _fScale == null) return;

                int scale = (int)_fScale.GetValue(__instance);
                if (scale <= 1) return;                       // 护卫舰：原版就是设计值，不碰

                var pe = _fPathEnd.GetValue(pointer) as GameObject;
                if (pe == null) return;
                var t = pe.transform;

                int id = t.GetInstanceID();
                Vector3 baseLocal;
                if (!_peBase.TryGetValue(id, out baseLocal))
                {
                    baseLocal = t.localPosition;              // ★只在第一次见到时采样★
                    _peBase[id] = baseLocal;
                    if (cfg.WatchMomentum)
                    {
                        Main.Log("[三件套对齐] 罗盘补正　根缩放=" + (1.35f * scale).ToString("F2")
                               + "（m_DecalScale=" + scale + "）"
                               + "　m_PathEnd 原始局部偏移=" + baseLocal.ToString("F3")
                               + "\n    该偏移会被根缩放同比放大，W 越大偏得越远；现按 /" + scale + " 补回。"
                               + "\n    若原始偏移本来就是 (0,0,0)，说明罗盘不是偏移放大导致的，需另查。");
                        Main.FlushLog(true);
                    }
                }
                if (baseLocal == Vector3.zero) return;        // 本来就没偏移，不用管

                var want = baseLocal / scale;
                if ((t.localPosition - want).sqrMagnitude > 1e-8f) t.localPosition = want;
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[三件套对齐] 罗盘补正失败: " + e.Message); }
            }
        }

        /// <summary>
        /// 量 decal **实际渲染在哪** —— 用它自己的四个角。
        ///
        /// ★为什么必须量这个★
        ///   前几版我一直拿「我算的坐标」和「我算的坐标」互证：
        ///     decal 根节点 == marker.transform.position     ✔ 日志逐格一致
        ///     CalculateArea(压方 rect) == {(0,0),(0,1),(1,0),(1,1)}  ✔
        ///     GetSizePositionOffset == 半格                  ✔
        ///   全都对得上，可玩家看到的就是偏。说明**渲染出来的东西**和这套坐标不是一回事，
        ///   而我从没量过渲染本身。这正是本项目栽过两次的恒等式陷阱的第三种形态。
        ///
        ///   `PointerCellDecal.CornersPositions` 是 public 属性，返回 m_Corners 四个
        ///   Transform 的**世界坐标** —— 这是引擎自己给出的、decal 真实占据的范围，
        ///   不经过我的任何换算。
        ///
        /// 判据：这四个角应当恰好是「锚点方块」的四角。对不上，差多少一目了然。
        /// </summary>
        private static void LogCorners(Component pointer, CustomGridNodeBase anchor, StarshipEntity ship)
        {
            try
            {
                if (_cornersLogged) return;
                var cfg = Main.Settings;
                if (cfg == null || !cfg.WatchMomentum) return;
                if (_pCorners == null)
                {
                    _pCorners = AccessTools.Property(pointer.GetType(), "CornersPositions");
                    if (_pCorners == null) { _cornersLogged = true; Main.Log("[三件套对齐] 没有 CornersPositions 属性，跳过角点测量"); return; }
                }
                var raw = _pCorners.GetValue(pointer) as System.Collections.IEnumerable;
                if (raw == null) return;

                float c = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                if (c <= 0.001f) return;

                var sb = new System.Text.StringBuilder();
                int n = 0;
                foreach (var o in raw)
                {
                    var v = (Vector3)o; n++;
                    sb.Append("(").Append((v.x / c).ToString("F2")).Append(",")
                      .Append((v.z / c).ToString("F2")).Append(") ");
                }
                if (n == 0) return;
                _cornersLogged = true;

                var centre = (Vector3)anchor.Vector3Position + BlockCenterOf(ship) + ShipMarkerAlign.Shift(ship);
                float half = ship.SizeRect.Width * 0.5f;      // 方块半边长，单位：格
                Main.Log("[三件套对齐] ★decal 实际渲染范围★ 角点(" + n + " 个)：" + sb.ToString().TrimEnd()
                       + "\n    期望方块中心 = 格(" + (centre.x / c).ToString("F2") + "," + (centre.z / c).ToString("F2") + ")"
                       + "　半边长 = " + half.ToString("F2") + " 格"
                       + "\n    即期望角点应落在中心 ±" + half.ToString("F2") + " 格处。对不上就说明 decal 的网格/轴心不居中。");
                Main.FlushLog(true);
            }
            catch { _cornersLogged = true; }
        }

        private static Vector3 BlockCenterOf(StarshipEntity ship) { return ShipMarkerAlign.BlockCenter(ship); }

        private static System.Reflection.PropertyInfo _pCorners;
        private static bool _cornersLogged;

        /// <summary>
        /// pivot 偏移 = decal 的**视觉中心** − 它的 transform.position。
        ///
        /// 实测样本（1.4.16 日志，m_DecalScale=1）：
        ///     角点 (-0.98,0.00) (-0.98,-1.00) (-0.02,-1.00) (-0.02,0.00)  → 视觉中心 (-0.50,-0.50)
        ///     而根节点被放在 (-1.00,-1.00)  → 偏移 = (+0.50,+0.50) 格
        /// 也就是 decal 的轴心在自己的角上，不在中心。前面几版一直把根节点当视觉中心用，
        /// 所以无论锚点算得多准，画出来都偏半个方块。
        ///
        /// ★不猜、自标定★ CornersPositions 是引擎自己给的四角世界坐标，
        ///   取平均就是视觉中心，减去根节点就是偏移。pivot 在哪、缩放多少都自动成立。
        ///
        /// ★按缩放缓存★ CornersPositions 内部是 `m_Corners.Select(...).ToList()`，
        ///   每次调用都分配一个 List。这是每帧路径，不能每帧分配（1.4.15 已经因为
        ///   每帧反射把海战搞卡过一次）。偏移只随缩放变，按 scale 缓存即可。
        /// </summary>
        private static Vector3 PivotDelta(Component pointer, Transform root, int scale)
        {
            try
            {
                if (_pivotScale == scale) return _pivotVal;
                if (_pCorners == null)
                {
                    _pCorners = AccessTools.Property(pointer.GetType(), "CornersPositions");
                    if (_pCorners == null) { _pivotScale = scale; _pivotVal = Vector3.zero; return Vector3.zero; }
                }
                var raw = _pCorners.GetValue(pointer) as System.Collections.IEnumerable;
                if (raw == null) return _pivotVal;

                Vector3 sum = Vector3.zero; int n = 0;
                foreach (var o in raw) { sum += (Vector3)o; n++; }
                if (n == 0) return _pivotVal;

                var d = sum / n - root.position;
                d.y = 0f;                                   // 高度交给引擎的 CheckHeight
                _pivotScale = scale; _pivotVal = d;

                var cfg = Main.Settings;
                if (cfg != null && cfg.WatchMomentum)
                {
                    float c = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                    if (c <= 0.001f) c = 1f;
                    Main.Log("[三件套对齐] pivot 自标定　m_DecalScale=" + scale
                           + "　偏移 = 格(" + (d.x / c).ToString("F2") + "," + (d.z / c).ToString("F2") + ")"
                           + "\n    = decal 视觉中心 − 根节点。已从目标位置里减掉。"
                           + "\n    打出 (0.00,0.00) 说明轴心本来就居中，圆圈偏移另有来源。");
                    Main.FlushLog(true);
                }
                return d;
            }
            catch { return _pivotVal; }
        }

        private static int _pivotScale = int.MinValue;
        private static Vector3 _pivotVal;

        private static FieldInfo _fPathEnd, _fScale;
        private static FieldInfo _fScaleP;   // Prefix 侧的 m_DecalScale（与 Postfix 各自探测，互不依赖顺序）
        private static bool _peProbed;
        private static readonly System.Collections.Generic.Dictionary<int, Vector3> _peBase =
            new System.Collections.Generic.Dictionary<int, Vector3>();
    }
}
