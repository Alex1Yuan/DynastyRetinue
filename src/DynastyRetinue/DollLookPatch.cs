using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.View;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 【实验中，默认关闭】把卫兵的视图换成 DollLook 合成的那一套。
    ///
    /// 拦 `PartUnitViewSettings.Instantiate` 而不是写 `Doll` 字段的理由见 DollLook 的注释：
    /// 那个字段一旦有值就同时进存档和同步哈希，而这里只影响"这一次造出来的视图"，
    /// 字段全程 null —— 关掉开关或卸载 mod，下次生成视图就自动回原样，不用清理任何东西。
    ///
    /// ★变形优先★
    ///   原版 Instantiate 先处理 PartPetPolymorphed / PartPolymorphed 两条变形分支，
    ///   之后才轮到 Doll。变形是玩法效果（比如被变成别的东西），外观只是表现，
    ///   表现不该盖掉玩法。所以只要挂着任一变形部件就直接放行原版。
    ///
    /// ★开关切换后不会立刻生效★
    ///   视图是生成时造的，已经在场上的卫兵不会重建。要过图或重新读档才看得到。
    ///   这不是 bug，是这个拦截点的性质 —— 写在这里免得下次又当成 bug 查一遍。
    /// </summary>
    [HarmonyPatch(typeof(PartUnitViewSettings), "Instantiate")]
    internal static class DollLookPatch
    {
        private static bool Prefix(PartUnitViewSettings __instance, bool ignorePolymorph, ref UnitEntityView __result)
        {
            try
            {
                if (!Main.Enabled) return true;
                if (Main.Settings == null) return true;

                var u = __instance != null ? __instance.Owner as BaseUnitEntity : null;
                if (u == null) { Bail("拿不到单位实体"); return true; }
                if (!RetinueRegistry.IsGuard(u)) return true;   // 非卫兵是常态，不记

                // 变形中就别抢 —— 玩法效果优先于外观
                if (!ignorePolymorph &&
                    (u.GetOptional<PartPetPolymorphed>() != null || u.GetOptional<PartPolymorphed>() != null))
                { Bail("该卫兵处于变形状态"); return true; }

                // ★唯一入口是分配表★（「外观」页 / UMM 面板「外观」区）
                //   null = 跟随装备 = 完全不干预；借模型那条走 AppearancePatch，不归这里管。
                //   1.0.74 曾有一段"分配表为空就按内置卡斯金"的过渡兜底，是为了在矩阵界面
                //   做好之前还能测。界面上线后它变成了 bug：矩阵明明全是「跟随装备」，
                //   卫兵却还是卡斯金 —— 因为空表恰好命中兜底。已删除。
                var look = LookAssign.LookFor(u);
                if (look == null || !look.IsCompose) return true;

                DollData doll = DollLook.Build(u, look.Parts);
                if (doll == null || doll.RacePreset == null) return true;   // 造不出来就走原版

                UnitEntityView view = doll.CreateUnitView();
                if (view == null) { Main.LogError("[外观] CreateUnitView 返回 null，回退原版外观。"); return true; }

                // 和原版 Doll 分支一致地摆好位置朝向
                view.ViewTransform.position = u.Position;
                view.ViewTransform.rotation = Quaternion.Euler(0f, u.Orientation, 0f);

                Main.Log("[外观] " + (u.CharacterName ?? "?") + " 使用合成外观（部件 "
                         + doll.EquipmentEntityIds.Count + " 件，" + doll.RacePreset.name + "，" + u.Gender + "）");
                __result = view;
                return false;
            }
            catch (Exception e)
            {
                // 外观出问题绝不能让单位生不出来
                Main.LogError("[外观] 合成视图失败，回退原版: " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// 每种放弃原因只记一次。
        /// ★为什么要有这个★ 1.0.70 实测：补丁挂上了、区域也重建了，日志里却一条
        /// [外观] 都没有 —— 于是分不清是"开关没开"、"不认识这个卫兵"还是"补丁点错了"，
        /// 只能靠猜。把每条岔路口都记一笔，下次一眼看得出停在哪。
        /// </summary>
        private static readonly HashSet<string> _bailed = new HashSet<string>(StringComparer.Ordinal);
        private static void Bail(string why)
        {
            if (_bailed.Add(why)) Main.Log("[外观] 未生效：" + why + "。");
        }

        /// <summary>
        /// 就地重建所有卫兵的视图，不用过图。抄的是原版变形结束时的做法
        /// （Polymorph.RestoreView）：造新视图 → AttachView（内部会 DetachView 旧的）
        /// → 摆回原位 → 销毁旧的 GameObject。
        /// 开发按钮专用 —— RetinueRegistry.All() 会拷贝全区域实体，不能进高频路径。
        /// </summary>
        public static int RebuildAllGuardViews()
        {
            int n = 0;
            var list = RetinueRegistry.All();
            for (int i = 0; i < list.Count; i++)
                if (RebuildOne(list[i])) n++;
            try { Kingmaker.Game.Instance.SelectionCharacter.ReselectCurrentUnit(); } catch { }
            Main.Log("[外观] 就地重建了 " + n + " 名卫兵的视图。");
            return n;
        }

        /// <summary>重建一名卫兵的视图。没有视图（还没挂上）返回 false。</summary>
        public static bool RebuildOne(BaseUnitEntity u)
        {
            if (u == null) return false;
            try
            {
                var old = u.View;
                if (old == null) return false;
                var fresh = u.ViewSettings.Instantiate();
                if (fresh == null) { Main.LogError("[外观] 重建视图失败：" + (u.CharacterName ?? "?") + " Instantiate 返回 null"); return false; }

                var pos = old.ViewTransform.position;
                var rot = old.ViewTransform.rotation;
                var scene = old.ViewTransform.gameObject.scene;

                u.AttachView(fresh);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(u.View.ViewTransform.gameObject, scene);
                u.View.ViewTransform.position = pos;
                u.View.ViewTransform.rotation = rot;
                UnityEngine.Object.Destroy(old.ViewTransform.gameObject);
                return true;
            }
            catch (Exception e) { Main.LogError("[外观] 重建视图异常 " + (u != null ? u.CharacterName : "?") + ": " + e.Message); return false; }
        }

        /// <summary>
        /// 新招募的卫兵：等它的视图挂上来之后再重建一次。
        ///
        /// ★为什么要等★
        ///   `EntitySpawnController` 只把单位加进 m_ToSpawn，**要到下一次 Tick 才真正入册**
        ///   （RetinueTest 里那条注释早就写着），视图更在其后。而身份标记是 SpawnUnit
        ///   返回后立刻设的 —— 也就是说视图创建时机既不在"标记之前"也不紧跟其后，
        ///   隔着不确定的帧数。
        ///   1.0.72 试过在 SpawnUnit 前后开一个"正在生成卫兵"的短窗，**没用**：
        ///   窗口早在视图创建之前就关了。实测现象是新兵永远是原版外观，
        ///   而手点【重建卫兵视图】就正常 —— 正是这个时序差。
        ///   所以不猜帧数，轮询到视图出现为止。
        /// </summary>
        public static void RebuildWhenReady(string uid, int triesLeft = 60)
        {
            if (string.IsNullOrEmpty(uid)) return;
            Deferred.NextFrames(1, () =>
            {
                var g = RetinueRegistry.ByUniqueId(uid);
                if (g != null && RebuildOne(g)) return;
                if (triesLeft > 1) { RebuildWhenReady(uid, triesLeft - 1); return; }
                Main.Log("[外观] " + uid + " 等了 60 帧仍没有视图，放弃重建（过图或读档时会正常）。");
            });
        }
    }
}
