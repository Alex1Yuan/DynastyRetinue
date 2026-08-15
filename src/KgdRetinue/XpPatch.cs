using System;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Parts;

namespace KgdRetinue
{
    /// <summary>
    /// 卫兵经验独立化。
    ///
    /// 先澄清一件事：原版 Player.GainPartyExperience（Player.cs:1070-1085）是
    ///     foreach (...) item.Progression.GainExperience(gained, log: false);
    /// **没有除法** —— 每个角色各拿一份完整的 gained。所以卫兵进 AllCharacters
    /// **不会稀释队友的经验**，这一点实测确认过。
    ///
    /// 但"卫兵按全额队伍经验涨"意味着 XpRatio 形同虚设（比例从 0.8 单调爬向 1.0），
    /// 玩家失去了"卫兵比主角弱多少"这个调节手段。这里按比例缩放卫兵拿到的经验，
    /// 把那个旋钮还回去 —— 队友那份一分不动。
    /// </summary>
    [HarmonyPatch(typeof(PartUnitProgression), nameof(PartUnitProgression.GainExperience))]
    public static class XpPatch
    {
        private static void Prefix(PartUnitProgression __instance, ref int exp)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.ScaleGuardXp) return;
                if (exp <= 0) return;
                var u = __instance.Owner as BaseUnitEntity;
                if (u == null || !RetinueRegistry.IsGuard(u)) return;

                float ratio;
                if (!float.TryParse(Main.Settings.XpRatio, out ratio)) ratio = 0.8f;
                if (ratio < 0f) ratio = 0f;
                if (ratio > 4f) ratio = 4f;

                exp = (int)(exp * ratio);
            }
            catch { /* 补丁出错不能影响原版发经验 */ }
        }
    }

    /// <summary>
    /// 创伤三档处理。TraumaMode：
    ///   0 = 无创伤     —— 卫兵完全不进创伤/重伤流水线（默认）
    ///   1 = 跟队恢复   —— 卫兵照常吃创伤，但队友被治疗时卫兵一起治
    ///   2 = 原版       —— 完全不干预
    ///
    /// 背景：PartHealth.AddWoundsAndTraumasIfNecessary 第一行是
    ///   if (!Player.AllCharacters.Contains(ConcreteOwner)) return;
    /// 卫兵因为进了 CrossSceneState 被塞进 AllCharacters，过不了这个早退。
    /// 而且 :336 的 `Owner.IsInPlayerParty ? 难度设置 : 0.5f` 对卫兵走 0.5f 分支，
    /// 重伤阈值写死最大生命的 50%、不吃难度减免，**比真队友更容易重伤**。
    /// </summary>
    [HarmonyPatch(typeof(PartHealth), "AddWoundsAndTraumasIfNecessary")]
    public static class TraumaPatch
    {
        private static bool Prefix(PartHealth __instance)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null) return true;
                if (Main.Settings.TraumaMode != 0) return true;   // 只有"无创伤"档才拦
                var u = __instance.ConcreteOwner as BaseUnitEntity;
                if (u == null || !RetinueRegistry.IsGuard(u)) return true;
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>
    /// 跟队恢复：队友的创伤被治好时，把卫兵一起治了。
    /// 直接 Postfix HealTrauma 而不是订阅 IHealWoundOrTrauma ——
    /// 后者是 ISubscriber&lt;IEntity&gt; 的实体级事件，全局订阅收不到。
    /// </summary>
    [HarmonyPatch(typeof(PartHealth), nameof(PartHealth.HealTrauma))]
    public static class TraumaHealPatch
    {
        private static bool _reentry;

        private static void Postfix(PartHealth __instance, int count)
        {
            try
            {
                if (_reentry) return;
                if (!Main.Enabled || Main.Settings == null) return;
                if (Main.Settings.TraumaMode != 1) return;

                var u = __instance.ConcreteOwner as BaseUnitEntity;
                if (u == null || RetinueRegistry.IsGuard(u)) return;   // 卫兵自己被治时不再传播

                _reentry = true;
                try
                {
                    int n = 0;
                    foreach (var g in RetinueRegistry.All())
                    {
                        var h = g.GetHealthOptional();
                        if (h == null) continue;
                        h.HealTrauma(count);
                        n++;
                    }
                    if (n > 0) Main.Log("[创伤] 队友恢复，同步治疗 " + n + " 名卫兵（" + count + " 层）");
                }
                finally { _reentry = false; }
            }
            catch { _reentry = false; }
        }
    }
}
