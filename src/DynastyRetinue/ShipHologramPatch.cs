using System;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 让海战的绿色全息影像用**本船的模型和尺寸**，而不是那个通用占位低模。
    ///
    /// ================== 病灶 ==================
    /// 舰船全息压根没有"按船型选模型"这回事：
    ///
    ///     public static UnitHologram CreateHologramSpaceship(this BaseUnitEntity unit)
    ///     {
    ///         unitEntityView = SetupHologramPrefab(FxRoot.DefaultStarshipHologramPrefab, unit);
    ///         ...
    ///         unitHologram.SetupStarship(unitEntityView, unit.View);
    ///     }
    ///
    /// 而地面单位那条路（CreateHologram → GetHologramPrefab）有三级选择：
    /// 蓝图上的 UnitCustomHologram → 按骨架匹配 HologramPrefabs → 回落默认。
    /// 舰船直接用唯一那个默认 prefab，**所有船长得一样**。
    /// 实测：全息 mesh=Ship 顶点 1552，本体 mesh=imperial_cruiser_gothic 顶点 75989。
    ///
    /// 缩放也没同步，而且同样是"地面有、海战漏"：
    ///     Setup()        → SetupAvatar() 里有 `localScale = originalAvatar.localScale`
    ///     SetupStarship() → 没有 SetupAvatar，这一步整个不存在
    /// 实测全息 1.000、本体 1.515 —— 所以它不只是形状不对，还小了一圈。
    ///
    /// ================== 修法 ==================
    /// 在 CreateHologramSpaceship 之后，把全息对象的 mesh 换成本体的、缩放对齐本体。
    /// **不碰材质** —— 那层绿色是 SetupShading 挂上去的独立 FX，换 mesh 不影响它。
    ///
    /// ★为什么不新建 prefab★ 那要引入新的 AssetId，踩存档红线。
    /// 这里只是把两个**已存在**的运行时对象的 mesh 引用对调，不产生任何新资源。
    ///
    /// ★纯视觉★ 全息影像只在拖拽移动指令时显示，不参与任何判定，不进存档。
    /// </summary>
    [HarmonyPatch(typeof(UnitHologramExtension), "CreateHologramSpaceship")]
    internal static class ShipHologramPatch
    {
        private static bool _warned;

        private static void Postfix(BaseUnitEntity unit, UnitHologram __result)
        {
            try
            {
                if (!Main.Enabled) return;
                var s = Main.Settings;
                if (s == null || !s.ShipHologramFix) return;
                if (__result == null || unit == null || unit.View == null) return;

                var holo = __result.gameObject;
                var src = unit.View.gameObject;
                if (holo == null || src == null) return;

                // ---- 1. 换模型 ----
                // 取各自的第一个 MeshFilter：舰船是整体静态模型，不是骨骼蒙皮，
                // 所以 MeshFilter 这一条就够，不用管 SkinnedMeshRenderer。
                var srcMf = src.GetComponentInChildren<MeshFilter>(true);
                var dstMf = holo.GetComponentInChildren<MeshFilter>(true);
                if (srcMf != null && srcMf.sharedMesh != null && dstMf != null)
                {
                    if (dstMf.sharedMesh != srcMf.sharedMesh)
                        dstMf.sharedMesh = srcMf.sharedMesh;
                }

                // ---- 2. 对齐尺寸 ----
                // 全息对象是场景根对象（CreateHologramSpaceship 没给它设父级），
                // 所以 localScale 就是世界缩放，直接抄本体的 lossyScale 即可。
                holo.transform.localScale = src.transform.lossyScale;

                Main.LogVerbose("[全息] 已对齐：mesh=" + (srcMf != null && srcMf.sharedMesh != null
                                                       ? srcMf.sharedMesh.name : "?")
                              + "　缩放=" + holo.transform.localScale.ToString("F3"));
            }
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[全息] 对齐失败（不影响其它功能）: " + e.Message); }
            }
        }
    }
}
