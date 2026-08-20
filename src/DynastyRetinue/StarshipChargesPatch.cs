using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Enums;
using Kingmaker.EntitySystem.Stats.Base;

namespace DynastyRetinue
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
        public static void ResetLog() { _logged = false; _rangeLogged = false; _shieldLogged = false; _armourLogged = false; _ramLogged = false; }

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
        /// <summary>
        /// 这把炮是不是**玩家座舰**上的。
        ///
        /// ★为什么必须有这一道★ BonusFor / RangeBonusFor 都是
        /// 「读玩家座舰的分档 → 作用在传进来的武器上」，两件事之间**没有任何关联**。
        /// 而射程那条挂在 RuleCalculateAbilityRange.OnTrigger 上，
        /// **每条船算射程都会过一遍** —— 于是玩家一升巡洋舰，全场敌舰的非舷炮也跟着 +3，
        /// 大巡则是舷炮 +3、船脊/舰首 +5。玩家看不见任何提示，只会觉得仗突然变难。
        /// 多打那条挂在 Reload 的 Postfix 上，触发面窄一些，但同一个洞。
        ///
        /// 同文件里护盾(:256) 和装甲(:345) 都做了 ReferenceEquals(owner, ship)，
        /// 只有这两个漏了 —— 抄的时候漏抄了判据，不是设计如此。
        ///
        /// ★fail-closed★ 取不到船主一律返回 false（不给加成）。
        /// 反过来（取不到就给）会让一个反射失败静默地把加成撒给全场。
        /// </summary>
        private static bool IsPlayerShipWeapon(object weapon)
        {
            try
            {
                if (weapon == null) return false;
                // ItemEntityStarshipWeapon.Starship => (StarshipEntity)HoldingSlot.Owner
                //   ref/rt_probe/dec/Warhammer.SpaceCombat.StarshipLogic.Weapon/ItemEntityStarshipWeapon.cs:31
                var st = Get(weapon, "Starship");
                if (st == null) return false;
                var ship = Game.Instance != null && Game.Instance.Player != null
                         ? (object)Game.Instance.Player.PlayerShip : null;
                return ship != null && ReferenceEquals(st, ship);
            }
            catch { return false; }
        }

        private static int BonusFor(object weapon)
        {
            if (!IsPlayerShipWeapon(weapon)) return 0;   // ★别把加成撒给敌舰★
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
            if (!IsPlayerShipWeapon(weapon)) return 0;   // ★别把加成撒给敌舰★
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

        // ---------------------------------------------------------------- 护盾

        /// <summary>
        /// 护盾上限按分档翻倍。
        ///
        /// 落点：Kingmaker.SpaceCombat.StarshipLogic.Parts.StarshipSectorShields.GetMax()
        ///     int num  = 该扇区基数（来自 VoidShieldGenerator.Fore/Port/Starboard/Aft）
        ///     int num2 = Σ StarshipShieldEnhancement.bonusFlat
        ///     int num3 = Σ StarshipShieldEnhancement.bonusPct
        ///     return (num + num2) * (100 + num3) / 100;
        /// 它是**唯一**的护盾上限来源 —— Max / Current / Damage 的 clamp 全走它。
        ///
        /// ★ 必须只对玩家舰生效 ★ GetMax 是所有舰船共用的，不加判据会把敌舰护盾也翻倍。
        ///
        /// ★ 存档 ★ 只改上限的计算，不写任何字段。
        /// StarshipSectorShields.m_Damage 是 [JsonProperty]，而 Damage 的 setter 会
        /// Clamp(value, 0, Max) —— 上限**变大**是安全的（旧伤害值仍在范围内）；
        /// 若将来要往下调，得留意会不会把已有伤害截断。
        /// </summary>
        [HarmonyPatch]
        public static class StarshipShieldPatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Kingmaker.SpaceCombat.StarshipLogic.Parts.StarshipSectorShields");
                return t == null ? null : AccessTools.Method(t, "GetMax");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null) Main.LogError("[舰船] 找不到 StarshipSectorShields.GetMax —— 护盾加成不可用。");
                return m != null;
            }

            private static void Postfix(object __instance, ref int __result)
            {
                try
                {
                    if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipExtraShots) return;
                    if (__result <= 0 || __instance == null) return;
                    if (!IsPlayerShipShields(__instance)) return;   // ★ 敌舰不加 ★

                    int pct = ShieldPct();
                    if (pct <= 0) return;

                    int before = __result;
                    __result = __result * (100 + pct) / 100;

                    if (!_shieldLogged)
                    {
                        _shieldLogged = true;
                        Main.Log("[舰船] 护盾加成生效：扇区上限 " + before + " -> " + __result
                                 + "（+" + pct + "%，分档 " + ShipSize() + "）。本次会话只报这一条。");
                    }
                }
                catch (Exception e) { Main.LogError("[舰船] 护盾 Postfix 失败: " + e.Message); }
            }

            /// <summary>这组扇区护盾是不是玩家座舰的。m_Owner 是 PartStarshipShields，它的 Owner 才是船。</summary>
            private static bool IsPlayerShipShields(object sectorShields)
            {
                try
                {
                    var part = Get(sectorShields, "m_Owner");
                    if (part == null) return false;
                    var owner = Get(part, "Owner");
                    if (owner == null) return false;
                    var ship = Game.Instance != null && Game.Instance.Player != null
                             ? (object)Game.Instance.Player.PlayerShip : null;
                    return ship != null && ReferenceEquals(owner, ship);
                }
                catch { return false; }
            }
        }

        private static bool _shieldLogged;

        /// <summary>当前分档的护盾加成百分比。护卫舰无加成。</summary>
        private static int ShieldPct()
        {
            var sz = ShipSize();
            if (sz == Size.Cruiser_2x4)      return Math.Max(0, Main.Settings.ShipCruiserShieldPct);
            if (sz == Size.GrandCruiser_3x6) return Math.Max(0, Main.Settings.ShipGrandShieldPct);
            return 0;
        }

        // ---------------------------------------------------------------- 装甲（减伤）

        /// <summary>
        /// 舰船装甲（减伤）按分档放大。
        ///
        /// ★ 落点选在源头，不在伤害规则上 ★
        /// 一开始我打在 RuleStarshipCalculateDamageForTarget 的 ResultDeflection 上，
        /// 那是**只改计算不改显示** —— 玩家会看到旧数字却少挨伤害，很难排查。
        /// 真正的唯一源头是：
        ///     Kingmaker.SpaceCombat.StarshipLogic.Parts.PartStarshipHull.GetLocationDeflection(hitLocation)
        ///         => AggregateArmorSources(...)   // 装甲板 + StarshipArmorBonus + Stats
        /// 而伤害规则的构造里就是
        ///     OriginalDeflection = Target.Hull.GetLocationDeflection(ResultHitLocation);
        /// 所以 Postfix 它，**显示和计算同时生效、永远一致**。
        ///
        /// ★ 只对玩家舰生效 ★ 这个方法所有舰船共用。
        /// </summary>
        [HarmonyPatch]
        public static class StarshipArmourPatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Kingmaker.SpaceCombat.StarshipLogic.Parts.PartStarshipHull");
                return t == null ? null : AccessTools.Method(t, "GetLocationDeflection");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null) Main.LogError("[舰船] 找不到 PartStarshipHull.GetLocationDeflection —— 装甲加成不可用。");
                return m != null;
            }

            private static void Postfix(object __instance, ref int __result)
            {
                try
                {
                    if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipExtraShots) return;
                    if (__result <= 0 || __instance == null) return;

                    var owner = Get(__instance, "Owner");
                    var ship = Game.Instance != null && Game.Instance.Player != null
                             ? (object)Game.Instance.Player.PlayerShip : null;
                    if (ship == null || !ReferenceEquals(owner, ship)) return;

                    int pct = ArmourPct();
                    if (pct <= 0) return;

                    int before = __result;
                    __result = __result * (100 + pct) / 100;

                    if (!_armourLogged)
                    {
                        _armourLogged = true;
                        Main.Log("[舰船] 装甲加成生效：减伤 " + before + " -> " + __result
                                 + "（+" + pct + "%，分档 " + ShipSize() + "）。"
                                 + "落点在 GetLocationDeflection，所以界面上的数字也会跟着变。"
                                 + "本次会话只报这一条。");
                    }
                }
                catch (Exception e) { Main.LogError("[舰船] 装甲 Postfix 失败: " + e.Message); }
            }
        }

        private static bool _armourLogged;

        private static int ArmourPct()
        {
            var sz = ShipSize();
            if (sz == Size.Cruiser_2x4)      return Math.Max(0, Main.Settings.ShipCruiserArmourPct);
            if (sz == Size.GrandCruiser_3x6) return Math.Max(0, Main.Settings.ShipGrandArmourPct);
            return 0;
        }

        // ---------------------------------------------------------------- 撞角距离

        /// <summary>
        /// 撞角行程按分档加长。
        ///
        /// 落点：AbilityCustomStarshipRam.BonusDistanceOnAttackAttempt(StarshipEntity owner)
        ///     return bonusDistanceOnAttackAttempt + Σ(fact.RamDistanceBonus);
        /// 这是 vanilla 自己用来给撞角加距离的口子（各种 fact 上的 RamDistanceBonus 都走它），
        /// 真正的行程由寻路的 pathLen 决定，这个值是往上叠的**格数**。
        ///
        /// ★「+100%」的基准是什么 ★
        /// 撞角没有一个叫"基础距离"的常量可以乘 —— 行程来自寻路。
        /// 所以我用舰船的 **Speed 属性**（界面上那个"速度"，护卫舰是 12）当基准：
        ///     额外格数 = Speed × pct / 100
        /// 巡洋舰 +100% ≈ +12 格，大巡 +200% ≈ +24 格。
        /// 这是我的解释，不是原版语义 —— 觉得不合适就调面板滑条。
        ///
        /// ★ 只对玩家舰生效 ★ 参数 owner 就是发起撞击的船，直接比对。
        /// 机动性按用户要求**不动**（大船在设定里本来就该更笨重）。
        /// </summary>
        [HarmonyPatch]
        public static class StarshipRamPatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Warhammer.SpaceCombat.StarshipLogic.Abilities.AbilityCustomStarshipRam");
                return t == null ? null : AccessTools.Method(t, "BonusDistanceOnAttackAttempt");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null) Main.LogError("[舰船] 找不到 AbilityCustomStarshipRam.BonusDistanceOnAttackAttempt —— 撞角加距不可用。");
                return m != null;
            }

            private static void Postfix(object owner, ref int __result)
            {
                try
                {
                    if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipExtraShots) return;
                    if (owner == null) return;

                    var ship = Game.Instance != null && Game.Instance.Player != null
                             ? (object)Game.Instance.Player.PlayerShip : null;
                    if (ship == null || !ReferenceEquals(owner, ship)) return;

                    int pct = RamPct();
                    if (pct <= 0) return;

                    int speed = 0;
                    try
                    {
                        var stats = Get(owner, "Stats");
                        var m = stats != null ? stats.GetType().GetMethod("GetStat", new[] { typeof(StatType) }) : null;
                        if (m != null)
                        {
                            var v = m.Invoke(stats, new object[] { StatType.Speed });
                            speed = Convert.ToInt32(Get(v, "Value") ?? v);
                        }
                    }
                    catch { }
                    if (speed <= 0) speed = 12;   // 读不到就按护卫舰基准，宁可保守

                    int add = speed * pct / 100;
                    if (add <= 0) return;
                    int before = __result;
                    __result = before + add;

                    if (!_ramLogged)
                    {
                        _ramLogged = true;
                        Main.Log("[舰船] 撞角加距生效：额外距离 " + before + " -> " + __result
                                 + "（速度 " + speed + " × " + pct + "%，分档 " + ShipSize() + "）。"
                                 + "本次会话只报这一条。");
                    }
                }
                catch (Exception e) { Main.LogError("[舰船] 撞角 Postfix 失败: " + e.Message); }
            }
        }

        private static bool _ramLogged;

        private static int RamPct()
        {
            var sz = ShipSize();
            if (sz == Size.Cruiser_2x4)      return Math.Max(0, Main.Settings.ShipCruiserRamPct);
            if (sz == Size.GrandCruiser_3x6) return Math.Max(0, Main.Settings.ShipGrandRamPct);
            return 0;
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

        // ================================================================
        // 给 UI 层用的只读查询
        //
        // ShipUiPatches 要在改装界面的 tooltip 里把这些加成写清楚，
        // 但上面那几个算式是 private 的。与其在 UI 那边再抄一份（抄一份就多一处
        // 会和补丁本体走偏的地方 —— 装甲那三个读取点就是原版自己抄出来的教训），
        // 不如在这里开几个只读入口，保证**显示的数字和真正生效的数字同源**。
        //
        // 全部带总开关判断：功能关掉时一律返回 0，tooltip 就什么都不会加。
        // ================================================================

        private static bool UiOn()
        {
            return Main.Enabled && Main.Settings != null && Main.Settings.ShipExtraShots;
        }

        /// <summary>这门炮每轮多打几次（0 = 没加成）。</summary>
        public static int UiExtraShots(object weapon) { return UiOn() ? BonusFor(weapon) : 0; }

        /// <summary>这门炮加多少射程（0 = 没加成）。</summary>
        public static int UiExtraRange(object weapon) { return UiOn() ? RangeBonusFor(weapon) : 0; }

        /// <summary>当前分档的护盾上限加成百分比。</summary>
        public static int UiShieldPct() { return UiOn() ? ShieldPct() : 0; }

        /// <summary>当前分档的装甲（减伤）加成百分比。</summary>
        public static int UiArmourPct() { return UiOn() ? ArmourPct() : 0; }

        /// <summary>当前分档的撞角额外行程百分比（基准是速度属性）。</summary>
        public static int UiRamPct() { return UiOn() ? RamPct() : 0; }

        /// <summary>
        /// 这门炮的**基础**开火次数（不含我们的加成），拿不到返回 -1。
        ///
        /// 取 Blueprint.Charges —— 也就是 Reload() 里那个基数：
        ///     Charges = Blueprint.Charges + Σ(StarshipModifyMaxCharges 匹配的)
        /// 严格说 vanilla 的 StarshipModifyMaxCharges 那部分没算进来，
        /// 所以 tooltip 里写"原本 N 次"时如果玩家装了那类组件会偏小。
        /// 拿不到就返回 -1，调用方改成只显示增量，不写一个可能错的总数。
        /// </summary>
        public static int UiBaseCharges(object weapon)
        {
            try
            {
                var bp = Get(weapon, "Blueprint");
                if (bp == null) return -1;
                var v = Get(bp, "Charges");
                return v is int ? (int)v : -1;
            }
            catch { return -1; }
        }

        /// <summary>分档的中文名，给 tooltip 用。</summary>
        public static string UiTierName()
        {
            var sz = ShipSize();
            if (sz == Size.Cruiser_2x4)      return "巡洋舰";
            if (sz == Size.GrandCruiser_3x6) return "大巡洋舰";
            if (sz == Size.Frigate_1x2)      return "护卫舰";
            if (sz == Size.Raider_1x1)       return "袭击舰";
            return sz.ToString();
        }

        /// <summary>
        /// 按名字取成员。
        ///
        /// ★ 必须逐层走继承链、且只看当前层声明的成员 ★
        /// 不加 DeclaredOnly 的话，基类和派生类都声明了同名成员（Owner 就是这种）时
        /// GetProperty/GetField 会抛 AmbiguousMatchException —— 实测装甲 Postfix
        /// 就是被这个异常整段吞掉的，表现为"补丁挂上了但一点效果没有"。
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
                    var p = t.GetProperty(name, DECL);
                    if (p != null && p.CanRead) return p.GetValue(o, null);
                    var f = t.GetField(name, DECL);
                    if (f != null) return f.GetValue(o);
                }
                catch { /* 这一层有问题就继续往上找 */ }
            }
            return null;
        }

        private static int GetInt(object o, string name)
        {
            var v = Get(o, name);
            return v is int ? (int)v : 0;
        }

        /// <summary>同 Get：逐层 DeclaredOnly，避免同名成员的 AmbiguousMatchException。</summary>
        private static void SetInt(object o, string name, int val)
        {
            if (o == null) return;
            const BindingFlags DECL = BindingFlags.Instance | BindingFlags.Public
                                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    var p = t.GetProperty(name, DECL);
                    if (p != null && p.CanWrite) { p.SetValue(o, val, null); return; }
                    var f = t.GetField(name, DECL);
                    if (f != null) { f.SetValue(o, val); return; }
                }
                catch { }
            }
        }
    }
}
