using System;
using HarmonyLib;
using Kingmaker.Controllers.Units;
using Kingmaker.Controllers.Units.CameraFollow;
using Kingmaker.EntitySystem.Entities;

namespace DynastyRetinue
{
    /// <summary>
    /// 卫兵行动时不让镜头跟过去。
    ///
    /// 起因：卫队满编十几个人，回合制里镜头会挨个跟到每个卫兵身上，
    /// 一路跳来跳去非常晃眼，而玩家其实并不需要看卫兵怎么走位。
    ///
    /// 切入点：CameraFollowController.TryAddTask 是所有跟随/演出任务的**唯一**入口
    ///（Tick 只消费 m_Tasks 队列，队列只由它填充），在这里按任务归属拦掉最干净：
    /// 不动镜头控制器状态、不碰 Cinemachine、也不影响玩家自己队伍的镜头。
    ///
    /// ICameraFollowTask.Owner 是 TargetWrapper，取 .Entity 判断是不是卫兵。
    /// 拿不到就放行 —— 补丁自身绝不能吃掉玩家的镜头。
    /// </summary>
    [HarmonyPatch]
    public static class CameraFollowPatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            // TryAddTask 是 private，且有默认参数，用 AccessTools 按名字取
            return AccessTools.Method(typeof(CameraFollowController), "TryAddTask");
        }

        private static bool Prefix(ICameraFollowTask task)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.NoCameraFollowGuards) return true;
                if (task == null) return true;

                // ★ 技能演出镜头（拉近特写）的 Owner 是 null ★
                // ActionCameraTask 的构造函数是 base(null, null, priority)，
                // 只有 Caster/Target 两个字段。v0.9.5 只看 Owner，于是这类全漏了 ——
                // 表现就是"大部分不跳了，但偶尔某些技能还会跳一下"。
                var act = task as ActionCameraTask;
                if (act != null) return !IsGuard(act.Caster);

                var w = task.Owner;
                if (w == null) return true;
                return !IsGuard(w.Entity);
            }
            catch { return true; }   // 补丁自身出错绝不能影响原版流程
        }

        private static bool IsGuard(object entity)
        {
            var u = entity as BaseUnitEntity;
            if (u == null) return false;
            try { return RetinueRegistry.IsGuard(u); } catch { return false; }
        }
    }
}
