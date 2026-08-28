using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 海战点击：去掉只对巡洋舰生效的半格光标偏移。
    ///
    /// ================== 病灶 ==================
    /// UnitPathManager 里有两个"光标世界坐标"，它们**不是同一个**：
    ///
    ///     ShipPathNodeMarkersController.PointerWorldCorrectedPosition
    ///         => ClickEventsController.WorldPosition;                  // 原始
    ///     UnitPathManager.PointerWorldCorrectedPosition
    ///         => ClickEventsController.WorldPosition - m_DecalOffset;  // 减了一个偏移
    ///
    /// 下命令那条路（UnitCommandsRunner.MoveSelectedUnitToPointTB）读的是**后者**的 CurrentNode，
    /// 而高亮/标记那条路读前者。于是 m_DecalOffset 非零时，
    /// **鼠标看到的格 ≠ 实际下达指令的格**。
    ///
    /// 而 m_DecalOffset 只在巡洋舰上非零：
    ///     护卫舰 1×2   (0,0,0)   —— DecalScale==1 时被强制归零
    ///     巡洋   2×4   (±格/2, 0, ±格/2)  ← 半格对角，永不为零
    ///     大巡   3×6   (0,0,0)   —— GetCellOffsetForUnit 的两个分支都不命中
    ///
    /// 那两个分支是为**地面 2×2 单位**写的（IsBigAndEvenUnit 只认 Size.Large），
    /// 原版玩家开的是 1×2 护卫舰，偏移恒为零，所以这条路径 Owlcat 大概率从没测过。
    ///
    /// ================== 这解释了什么 ==================
    /// 玩家逐档指认出的那组修正量，换算后带着一个**世界固定的 (0.5, 0.5) 半格分量**。
    /// 我曾把它拟合成"平局取整残差 e − Rot·e"，还据此改过船的渲染位置（已回滚）。
    /// 它根本不是取整残差 —— **它就是 m_DecalOffset**。
    /// 一个只写给地面单位的特例，被套到了 2×4 的船上。
    ///
    /// ================== 修法 ==================
    /// 舰船一律把 m_DecalOffset 归零，等于把巡洋放进大巡/护卫舰已经在跑的那个状态。
    /// **只动 m_DecalOffset，不动 m_SizeOffset** —— 后者在 UpdatePathRenderer 里
    /// 给预测路径线定位，归零会让路径线整体偏半格。
    ///
    /// ★纯输入/显示层★
    ///   不碰占位、不碰寻路、不碰落点集合、不进存档。改的是"光标落在哪一格"的换算。
    ///
    /// ★内联风险★
    ///   UpdateSizeOffset() 方法体很短，是 Mono 的内联候选 —— 内联了补丁就不会执行。
    ///   所以首次执行时必打一条日志：**没看到那条日志 = 补丁没跑**，
    ///   而不是"跑了但没效果"。这两者以前分不清，害玩家白测过好几轮。
    ///   若确实被内联，退路是 Postfix GetCellOffsetForUnit(MechanicEntity)（public、多分支、内联安全），
    ///   代价是 m_SizeOffset 一起归零（路径线偏半格，仅观感）。
    /// </summary>
    [HarmonyPatch]
    internal static class ShipPointerOffsetFix
    {
        private const string TypeName = "Kingmaker.UI.PathRenderer.UnitPathManager";

        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName(TypeName);
            return t == null ? null : AccessTools.Method(t, "UpdateSizeOffset");
        }

        private static bool Prepare()
        {
            var m = TargetMethod();
            Main.Log("[光标偏移] 补丁挂载 " + (m != null
                ? "成功 → UnitPathManager.UpdateSizeOffset()"
                : "失败：找不到 UnitPathManager.UpdateSizeOffset"));
            return m != null;
        }

        private static FieldInfo _fDecal;
        private static FieldInfo _fSelected;
        private static bool _logged;
        private static bool _warned;

        private static void Postfix(object __instance)
        {
                if (!Main.Enabled) return;   // 关掉 mod 就交还原版（OnToggle 不撤 Harmony 补丁）
            try
            {
                // ★★ 已停用 —— 单独归零是错的 ★★
                //
                //   实测（玩家 1.4.5）：归零之后**点击位置和绿格分家了**。
                //   原版里这两者本来是一致的（都相对船模偏了同一个量），
                //   说明 m_DecalOffset 正是让「光标取格」和「绿格标记」配对的那个偏移，
                //   而不是单纯的多余特例。清掉它就等于把配对拆了。
                //
                //   正确做法是**同时**给两者加同一个 Δ：
                //       绿格标记 = 锚点 + GetSizePositionOffset(压方 rect)      ← 无朝向
                //       船模     = 锚点 + GetSizePositionOffset(真实 SizeRect, 朝向)
                //       Δ = 船模偏移 − 标记偏移
                //   把 Δ 同时加到 marker 的 transform 和 m_DecalOffset 上，
                //   三者一起归位而配对关系不破。单改任何一边都会重现本次的分家。
                return;

#pragma warning disable 162
                var s = Main.Settings;
                if (s == null || !s.ShipGridBySize) return;
                if (__instance == null) return;

                var t = __instance.GetType();
                if (_fDecal == null) _fDecal = AccessTools.Field(t, "m_DecalOffset");
                if (_fDecal == null)
                {
                    if (!_warned) { _warned = true; Main.LogError("[光标偏移] 找不到 m_DecalOffset，未修正"); }
                    return;
                }

                // 只对舰船生效。地面单位那两个分支本来就是给它们写的，不碰。
                var unit = SelectedUnit(t, __instance);
                if (!(unit is StarshipEntity)) return;

                var before = (Vector3)_fDecal.GetValue(__instance);
                if (before == Vector3.zero) return;      // 已经是零（大巡/护卫舰），无需处理
                _fDecal.SetValue(__instance, Vector3.zero);

                if (!_logged)
                {
                    _logged = true;
                    float cell = Kingmaker.Pathfinding.GraphParamsMechanicsCache.GridCellSize;
                    if (cell <= 0.001f) cell = 1f;
                    Main.Log("[光标偏移] 已归零　原值=" + before.ToString("F3")
                           + "　= 格(" + (before.x / cell).ToString("F2") + ","
                           + (before.z / cell).ToString("F2") + ")"
                           + "\n    这个偏移让『鼠标看到的格』和『实际下指令的格』错开，只在巡洋舰上非零。"
                           + "\n    ★看不到这条日志 = 补丁没执行（方法可能被内联），不是没效果★");
                    Main.FlushLog(true);
                }
            }
#pragma warning restore 162
            catch (Exception e)
            {
                if (!_warned) { _warned = true; Main.LogError("[光标偏移] 修正失败（保持原样）: " + e.Message); }
            }
        }

        /// <summary>取当前选中的单位。字段名在不同版本可能不同，逐个试，全失败就放弃。</summary>
        private static object SelectedUnit(Type t, object inst)
        {
            foreach (var n in new string[] { "SelectedUnit", "m_SelectedUnit", "Unit", "m_Unit" })
            {
                try { var p = AccessTools.Property(t, n); if (p != null) { var v = p.GetValue(inst); if (v != null) return v; } } catch { }
                try { var f = AccessTools.Field(t, n); if (f != null) { var v = f.GetValue(inst); if (v != null) return v; } } catch { }
            }
            // 兜底：海战里能动的就是玩家座舰
            try
            {
                var game = Kingmaker.Game.Instance;
                return game != null && game.Player != null ? game.Player.PlayerShip : null;
            }
            catch { return null; }
        }
    }
}
