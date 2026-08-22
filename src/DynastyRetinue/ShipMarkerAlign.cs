using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Pathfinding;
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
    /// 同时也意味着：**只要给 m_DecalOffset 加一个量，"画在哪"和"选中谁"会一起挪**
    /// （上面两条式子里它的符号相反，恰好一次改对两处）。
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

                UnityEngine.Vector2Int org;
                if (!ShipGridPatch.TryGetBlockOrigin(ship, out org)) return Vector3.zero;
                if (org.x == 0 && org.y == 0) return Vector3.zero;

                return new Vector3(org.x * cell, 0f, org.y * cell);
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[三件套对齐] 算位移失败: " + e.Message); }
                return Vector3.zero;
            }
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
    /// ★一次改两处★ 见 ShipMarkerAlign 头注 —— m_DecalOffset 在「选中谁」和「画在哪」
    ///   两条式子里符号相反，加同一个量即可让圆圈和选中的落点一起挪，配对不破。
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
                _fDecal.SetValue(__instance, cur + s);
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
                if (s == Vector3.zero) return;

                var game = Kingmaker.Game.Instance;
                var click = game != null ? game.ClickEventsController : null;
                if (click == null) return;

                var node = Kingmaker.Pathfinding.GridAreaHelper.GetNearestNodeXZ(click.WorldPosition - s);
                _fNode.SetValue(__instance, node);
                _fChanged.SetValue(__instance, !ReferenceEquals(node, _prev));
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[三件套对齐] 高亮块修正失败: " + e.Message); }
            }
        }
    }
}
