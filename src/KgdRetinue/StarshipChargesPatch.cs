using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Enums;

namespace KgdRetinue
{
    /// <summary>
    /// 舰船「多打」：按舰船分档给武器槽加每回合开火次数。
    ///
    /// 设计（用户拍板）：**不动配置界面、不改蓝图、不扩槽位**。
    /// 换大船带来的差异靠"同一个槽位能打几次"体现，而不是"能装几门炮"。
    /// 这么做的三个好处：
    ///   1. 槽位实例是 [JsonProperty]、进存档且 PrePostLoad 不重读蓝图 ——
    ///      扩槽位不可逆、且只对新建的船生效。改 charges 完全没有这个问题。
    ///   2. 改装界面的槽位数不变，ShipUpgradeVm 那一串下标假设（Weapons.Count 必须 >= 5、
    ///      Keel/None 会导致 ArgumentOutOfRangeException）统统不用碰。
    ///   3. 零新增 AssetId。
    ///
    /// 落点（已反编译确认，Warhammer.SpaceCombat.StarshipLogic.Weapon.ItemEntityStarshipWeapon）：
    ///     public void Reload()
    ///     {
    ///         if (Blueprint != null &amp;&amp; Starship.Facts.GetComponents((StarshipBlockRecharge b) =&gt; b.Match(this)).Empty())
    ///         {
    ///             int num = (from x in Starship.Facts.GetComponents&lt;StarshipModifyMaxCharges&gt;()
    ///                        where x.WeaponType == Blueprint.WeaponType
    ///                        select x.Value).DefaultIfEmpty(0).Sum();
    ///             Charges = Blueprint.Charges + num;
    ///         }
    ///     }
    /// charges 是每回合开火次数的**唯一**限制器，所以 Postfix 加数就等于"多打"。
    /// ★ 注意 ★ 原方法有 StarshipBlockRecharge 的短路：被封锁充能时它**不进 if**，
    /// Charges 保持原值。我们的 Postfix 必须尊重这一点 —— 只在 Charges &gt; 0 时加，
    /// 否则会把"被封锁"的状态强行解开。
    ///
    /// 舰船分档用 vanilla 的 Kingmaker.Enums.Size：
    ///     Raider_1x1 &lt; Frigate_1x2 &lt; Cruiser_2x4 &lt; GrandCruiser_3x6
    /// </summary>
    [HarmonyPatch]
    public static class StarshipChargesPatch
    {
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Warhammer.SpaceCombat.StarshipLogic.Weapon.ItemEntityStarshipWeapon");
            return t == null ? null : AccessTools.Method(t, "Reload");
        }

        private static bool Prepare()
        {
            var m = TargetMethod();
            if (m == null)
                Main.LogError("[舰船] 找不到 ItemEntityStarshipWeapon.Reload —— 多打功能不可用。");
            return m != null;
        }

        private static void Postfix(object __instance)
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipExtraShots) return;
                if (__instance == null) return;

                int cur = GetInt(__instance, "Charges");
                // 0 = 被 StarshipBlockRecharge 封锁，或本来就没弹。别把封锁状态解开。
                if (cur <= 0) return;

                int bonus = BonusFor(__instance);
                if (bonus <= 0) return;

                SetInt(__instance, "Charges", cur + bonus);
                if (!_logged)
                {
                    _logged = true;
                    Main.Log("[舰船] 多打生效：" + SlotName(__instance) + " charges " + cur + " -> " + (cur + bonus)
                             + "（舰船分档 " + ShipSize() + "）。本次会话只报这一条。");
                }
            }
            catch (Exception e) { Main.LogError("[舰船] 多打 Postfix 失败: " + e.Message); }
        }

        private static bool _logged;
        /// <summary>换船/重开战斗时把"只报一条"的闸复位，便于观察。</summary>
        public static void ResetLog() { _logged = false; _rangeLogged = false; }

        // ---------------------------------------------------------------- 规则

        /// <summary>
        /// 当前玩家舰的分档。读 MechanicEntity.Size（=&gt; GetStateOptional()?.Size ?? OriginalSize），
        /// 拿不到就当护卫舰，即"不加成"。
        /// </summary>
        public static Size ShipSize()
        {
            try
            {
                var ship = Game.Instance != null && Game.Instance.Player != null
                         ? Game.Instance.Player.PlayerShip : null;
                if (ship == null) return Size.Frigate_1x2;
                var p = ship.GetType().GetProperty("Size");
                if (p == null) return Size.Frigate_1x2;
                return (Size)p.GetValue(ship, null);
            }
            catch { return Size.Frigate_1x2;
            }
        }

        /// <summary>
        /// 这一门炮能多打几次。
        ///
        /// 规则（面板可调）：
        ///   护卫舰/袭击舰 —— 无加成，保持原版手感
        ///   巡洋舰       —— 左右舷炮 +N（默认 +1，即两打）
        ///   大巡洋舰     —— 左右舷炮 +N2（默认 +2，即三打），船首/背炮 +1（两打）
        /// 用**槽位类型**而不是武器类型来区分 —— 舷炮和船首主炮可能同为 Macrobatteries，
        /// 只看 WeaponType 分不开（vanilla 的 StarshipModifyMaxCharges 就是只看 WeaponType，
        /// 所以它做不到"只加舷炮"，这也是我们不复用那个组件的原因）。
        /// </summary>
        private static int BonusFor(object weapon)
        {
            var sz = ShipSize();
            if (sz != Size.Cruiser_2x4 && sz != Size.GrandCruiser_3x6) return 0;

            string slot = SlotName(weapon);
            bool broadside = slot == "Port" || slot == "Starboard";

            if (sz == Size.Cruiser_2x4)
                return broadside ? Math.Max(0, Main.Settings.ShipCruiserBroadside) : 0;

            // GrandCruiser
            return broadside ? Math.Max(0, Main.Settings.ShipGrandBroadside)
                             : Math.Max(0, Main.Settings.ShipGrandProw);
        }

        // ---------------------------------------------------------------- 射程

        /// <summary>
        /// 船脊/船首/光矛的射程加成。
        ///
        /// 落点：RuleCalculateAbilityRange.OnTrigger 里
        ///     Result = (OverrideRange ?? DefaultRange) + Bonus + FiringArcBonus;
        /// vanilla 自己的 StarshipAbilityRangeExtender 就是往 evt.Bonus 上加数
        /// （OnEventAboutToTrigger 里 evt.Bonus += extraRange），我们用同一个口子。
        ///
        /// 设计（用户拍板）：舷炮堆**次数**、船脊/光矛堆**射程**，两类武器分工不同。
        ///   巡洋舰   —— 舷炮 2 打；船脊/船首/光矛 +射程
        ///   大巡洋舰 —— 舷炮 3 打 +射程；船脊/船首/光矛 2 打 +射程
        /// </summary>
        [HarmonyPatch]
        public static class StarshipRangePatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Kingmaker.RuleSystem.Rules.RuleCalculateAbilityRange");
                return t == null ? null : AccessTools.Method(t, "OnTrigger");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null) Main.LogError("[舰船] 找不到 RuleCalculateAbilityRange.OnTrigger —— 射程加成不可用。");
                return m != null;
            }

            /// Prefix：在 OnTrigger 算 Result **之前**把加成塞进 Bonus，
            /// 这样完全走 vanilla 的合成公式，不用自己算 Result。
            private static void Prefix(object __instance)
            {
                try
                {
                    if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipExtraShots) return;
                    if (__instance == null) return;

                    var ability = Get(__instance, "Ability");
                    if (ability == null) return;
                    var weapon = Get(ability, "StarshipWeapon");
                    if (weapon == null) return;               // 不是舰炮，与我们无关

                    int add = RangeBonusFor(weapon);
                    if (add <= 0) return;

                    int bonus = GetInt(__instance, "Bonus");
                    SetInt(__instance, "Bonus", bonus + add);

                    if (!_rangeLogged)
                    {
                        _rangeLogged = true;
                        Main.Log("[舰船] 射程加成生效：" + SlotName(weapon) + " +" + add
                                 + "（分档 " + ShipSize() + "）。本次会话只报这一条。");
                    }
                }
                catch (Exception e) { Main.LogError("[舰船] 射程 Prefix 失败: " + e.Message); }
            }
        }

        private static bool _rangeLogged;

        /// <summary>这门炮能加多少射程。舷炮不加 —— 它们靠次数。</summary>
        private static int RangeBonusFor(object weapon)
        {
            var sz = ShipSize();
            if (sz != Size.Cruiser_2x4 && sz != Size.GrandCruiser_3x6) return 0;

            string slot = SlotName(weapon);
            bool broadside = slot == "Port" || slot == "Starboard";

            if (sz == Size.Cruiser_2x4)
                return broadside ? 0 : Math.Max(0, Main.Settings.ShipCruiserRange);

            // 大巡洋舰：舷炮也吃射程，船脊/船首更多
            return broadside ? Math.Max(0, Main.Settings.ShipGrandRangeBroadside)
                             : Math.Max(0, Main.Settings.ShipGrandRangeProw);
        }

        // ---------------------------------------------------------------- 反射小工具

        /// <summary>
        /// 取这门炮所在槽位的类型名（Prow/Port/Starboard/Dorsal/…）。
        /// WeaponSlot 上取 Type 的路径在不同版本可能不同，逐个试，全失败返回 "?"。
        /// </summary>
        public static string SlotName(object weapon)
        {
            try
            {
                var slot = Get(weapon, "WeaponSlot");
                if (slot == null) return "?";
                foreach (var n in new[] { "Type", "SlotType" })
                {
                    var v = Get(slot, n);
                    if (v != null) return v.ToString();
                }
                var data = Get(slot, "SlotData") ?? Get(slot, "Blueprint");
                if (data != null)
                {
                    var v = Get(data, "Type");
                    if (v != null) return v.ToString();
                }
            }
            catch { }
            return "?";
        }

        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name, BF);
            if (p != null) return p.GetValue(o, null);
            var f = t.GetField(name, BF);
            return f != null ? f.GetValue(o) : null;
        }

        private static int GetInt(object o, string name)
        {
            var v = Get(o, name);
            return v is int ? (int)v : 0;
        }

        private static void SetInt(object o, string name, int val)
        {
            var t = o.GetType();
            var p = t.GetProperty(name, BF);
            if (p != null && p.CanWrite) { p.SetValue(o, val, null); return; }
            var f = t.GetField(name, BF);
            if (f != null) f.SetValue(o, val);
        }
    }
}
