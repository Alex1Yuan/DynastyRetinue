using System;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Parts;

namespace DynastyRetinue
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

                exp = (int)(exp * RatioFor(u));
            }
            catch { /* 补丁出错不能影响原版发经验 */ }
        }

        /// <summary>
        /// 这名卫兵这次该拿多少倍经验。
        ///
        /// ★ 为什么不能用固定系数 ★
        /// 原来是恒定 0.8：卫兵永远比主角涨得慢，**差距只会单调拉大**，
        /// 于是越往后招的卫兵越追不上、越没用，最后只能靠面板手动灌经验补救。
        ///
        /// 改成追赶制（用户提议）：落后越多拿得越多，追平后回落到地板值。
        ///     落后 0 级   → 地板（默认 0.8，即和原来一样）
        ///     落后 span 级 → 上限（默认 2.5 倍）
        ///     中间线性插值
        /// 追平之后不会超过地板，所以卫兵**永远不会反超主角**，
        /// 只是"落后了能补回来"。三个数都在面板上可调。
        /// </summary>
        private static float RatioFor(BaseUnitEntity guard)
        {
            float floorR = ParseF(Main.Settings.XpRatio, 0.8f);
            if (floorR < 0f) floorR = 0f;
            if (floorR > 4f) floorR = 4f;

            if (!Main.Settings.XpCatchUp) return floorR;
            // ★招募时的初始对齐不吃追赶倍率★
            // 追赶制是给战斗中的**增量**经验用的：落后越多补得越快。
            // 但新卫兵经验是 0、落后到顶，而初始对齐会把主角的**全部**经验
            // 一次性灌进来 —— 走的还是同一条 GainExperience。
            // 结果是 85799 × 2.5 = 214497，卫兵一出生就比主角高十几级
            // （实测主角 lv42 / 卫兵 lv55），跟注释里"永远不会反超主角"正好相反。
            // 加追赶制那版漏了这条路径。初始对齐用地板倍率，净结果回到 0.8 × 主角经验。
            if (RetinueTest.AligningExperience) return floorR;

            try
            {
                var g = Kingmaker.Game.Instance;
                var leader = g != null && g.Player != null ? g.Player.MainCharacterEntity : null;
                if (leader == null || leader.Progression == null || guard.Progression == null) return floorR;

                int gap = leader.Progression.CharacterLevel - guard.Progression.CharacterLevel;
                if (gap <= 0) return floorR;

                float maxR = Main.Settings.XpCatchUpMax / 100f;      // 面板存百分比
                if (maxR < floorR) maxR = floorR;
                int span = Main.Settings.XpCatchUpSpan;
                if (span < 1) span = 1;

                float t = gap >= span ? 1f : (float)gap / span;
                return floorR + (maxR - floorR) * t;
            }
            catch { return floorR; }
        }

        private static float ParseF(string s, float dflt)
        {
            float v;
            return float.TryParse(s, out v) ? v : dflt;
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
