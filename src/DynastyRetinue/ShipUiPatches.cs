using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Enums;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 舰船 UI 侧的三个补丁。全部**只改显示**，不碰任何进存档的字段。
    ///
    /// ★ 为什么需要这个文件 ★
    /// v0.20.0 把装甲加成打在 PartStarshipHull.GetLocationDeflection() 上，
    /// 伤害计算确实变了，**但界面上的数字纹丝不动**。反编译之后发现：
    /// 原版有**三个互相独立**的装甲读取点，各自抄了一遍同样的聚合逻辑 ——
    ///
    ///   1. PartStarshipHull.GetLocationDeflection(hitLocation)
    ///        = Stats.GetStat(对应方向) + ArmorPlatings.对应方向 + Σ StarshipArmorBonus
    ///        真正参与伤害结算的就是它。（已在 StarshipArmourPatch 里加成）
    ///
    ///   2. ShipVM.UpdateStats()              —— 改装界面 / 星系图底部那个船形图示
    ///        局部函数 AggregateArmorSources(statType, …) 里有个 **原版 bug**：
    ///            int num = ship.Stats.GetStat(StatType.ArmourFore);
    ///        statType 参数被丢掉了，四个方向全都读 ArmourFore 的属性值。
    ///        （只有 ArmorPlatings 那部分是分方向的，所以原版看着还算对，
    ///          因为船板贡献了全部、属性那份恒为 0。）
    ///
    ///   3. ShipShieldsPanelVM.UpdateHandler() —— 太空战里的面板
    ///        只读 Stats.GetStat(...).ModifiedValue，**完全不含船板**。
    ///        所以原版在这儿一直显示 0 —— 你说的"护甲板这个不会显示"就是它。
    ///
    /// 处理办法：把 2 和 3 的四个值直接改成 GetLocationDeflection 的返回值。
    /// 这样三处共用同一个真值来源，我们的加成只需要打在 1 上，显示自然跟着走，
    /// 顺带把原版那两个抄漏了的地方也对齐了。
    ///
    /// ★ 保守起见只在加成 > 0 时接管 ★
    /// 护卫舰档（pct = 0）完全不碰，原版什么样还是什么样 ——
    /// 我不想为了"修 bug"去改一个玩家没开启我们功能时的原版行为。
    /// </summary>
    public static class ShipUiPatches
    {
        // ------------------------------------------------------------------
        // 真值来源：hull.GetLocationDeflection(方向)
        // 每帧都会被调（两个 VM 都挂在 MainThreadDispatcher.UpdateAsObservable 上），
        // 所以 MethodInfo / 枚举值 / FieldInfo 全部缓存，别每帧反射查一遍。
        // ------------------------------------------------------------------

        private static MethodInfo _mDeflection;
        private static object _locFore, _locPort, _locStarboard, _locAft;
        private static bool _resolved;

        private static bool ResolveDeflection()
        {
            if (_resolved) return _mDeflection != null;
            _resolved = true;
            try
            {
                var t = AccessTools.TypeByName("Kingmaker.SpaceCombat.StarshipLogic.Parts.PartStarshipHull");
                if (t == null) return false;
                _mDeflection = AccessTools.Method(t, "GetLocationDeflection");
                if (_mDeflection == null) return false;

                // 枚举类型不去猜命名空间，直接从参数签名上取 —— 版本变了也不会错
                var et = _mDeflection.GetParameters()[0].ParameterType;
                _locFore      = Enum.Parse(et, "Fore");
                _locPort      = Enum.Parse(et, "Port");
                _locStarboard = Enum.Parse(et, "Starboard");
                _locAft       = Enum.Parse(et, "Aft");
                return true;
            }
            catch (Exception e)
            {
                Main.LogError("[舰船] 解析 GetLocationDeflection 失败: " + e.Message);
                _mDeflection = null;
                return false;
            }
        }

        /// <summary>拿玩家舰四个方向的真实减伤（已含我们的加成）。拿不到返回 null。</summary>
        private static int[] PlayerArmour()
        {
            if (!ResolveDeflection()) return null;
            try
            {
                var ship = Game.Instance != null && Game.Instance.Player != null
                         ? (object)Game.Instance.Player.PlayerShip : null;
                if (ship == null) return null;

                var hull = ship.GetType().GetProperty("Hull",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var h = hull != null ? hull.GetValue(ship, null) : null;
                if (h == null) return null;

                return new[]
                {
                    (int)_mDeflection.Invoke(h, new[] { _locFore }),
                    (int)_mDeflection.Invoke(h, new[] { _locPort }),
                    (int)_mDeflection.Invoke(h, new[] { _locStarboard }),
                    (int)_mDeflection.Invoke(h, new[] { _locAft }),
                };
            }
            catch { return null; }
        }

        /// <summary>当前分档下装甲加成的百分比；0 表示不接管显示。</summary>
        private static int ArmourPctNow()
        {
            try
            {
                if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipExtraShots) return 0;
                var sz = StarshipChargesPatch.ShipSize();
                if (sz == Size.Cruiser_2x4)      return Math.Max(0, Main.Settings.ShipCruiserArmourPct);
                if (sz == Size.GrandCruiser_3x6) return Math.Max(0, Main.Settings.ShipGrandArmourPct);
                return 0;
            }
            catch { return 0; }
        }

        /// <summary>给 ReactiveProperty&lt;T&gt; 字段赋值。字段名找不到就静默跳过。</summary>
        private static void SetRP(object vm, FieldInfo fi, int value, bool asFloat)
        {
            if (fi == null || vm == null) return;
            try
            {
                var rp = fi.GetValue(vm);
                if (rp == null) return;
                var p = rp.GetType().GetProperty("Value");
                if (p == null || !p.CanWrite) return;
                p.SetValue(rp, asFloat ? (object)(float)value : (object)value, null);
            }
            catch { }
        }

        private static FieldInfo F(Type t, string name)
        {
            try { return t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return null; }
        }

        // ==================================================================
        // 1) 改装界面 / 星系图底部的船形图示
        // ==================================================================

        /// <summary>
        /// ShipVM.UpdateStats() 的 Postfix。
        /// 原版在这里写四个 ReactiveProperty&lt;float&gt;：
        ///     ShipArmorFront / ShipArmorLeft / ShipArmorRight / ShipArmorRear
        /// 我们在它写完之后覆盖成 GetLocationDeflection 的真值。
        /// （ShipArmorRear 原版写了两遍，第二遍才是分方向正确的那份 —— 我们统一覆盖，不受影响。）
        /// </summary>
        [HarmonyPatch]
        public static class ShipArmourDisplayPatch
        {
            private static Type _vm;
            private static FieldInfo _front, _left, _right, _rear;

            private static MethodBase TargetMethod()
            {
                _vm = AccessTools.TypeByName("Kingmaker.Code.UI.MVVM.VM.Space.ShipVM");
                return _vm == null ? null : AccessTools.Method(_vm, "UpdateStats");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null)
                {
                    Main.LogError("[舰船] 找不到 ShipVM.UpdateStats —— 改装界面的装甲数字不会跟着变。");
                    return false;
                }
                _front = F(_vm, "ShipArmorFront");
                _left  = F(_vm, "ShipArmorLeft");
                _right = F(_vm, "ShipArmorRight");
                _rear  = F(_vm, "ShipArmorRear");
                if (_front == null || _left == null || _right == null || _rear == null)
                {
                    Main.LogError("[舰船] ShipVM 上找不到四个 ShipArmorXxx 字段，改装界面的装甲数字不接管。");
                    return false;
                }
                return true;
            }

            private static void Postfix(object __instance)
            {
                try
                {
                    if (ArmourPctNow() <= 0) return;          // 护卫舰档：原版行为原样保留
                    var a = PlayerArmour();
                    if (a == null) return;

                    SetRP(__instance, _front, a[0], true);
                    SetRP(__instance, _left,  a[1], true);
                    SetRP(__instance, _right, a[2], true);
                    SetRP(__instance, _rear,  a[3], true);

                    if (!_logged)
                    {
                        _logged = true;
                        Main.Log("[舰船] 改装界面装甲数字已接管：艏 " + a[0] + " 左舷 " + a[1]
                                 + " 右舷 " + a[2] + " 艉 " + a[3]
                                 + "（取自 GetLocationDeflection，与伤害计算同源；"
                                 + "顺带修掉原版四个方向都读 ArmourFore 的抄漏）。本次会话只报这一条。");
                    }
                }
                catch (Exception e) { Main.LogError("[舰船] 改装界面装甲显示失败: " + e.Message); }
            }

            private static bool _logged;
        }

        // ==================================================================
        // 2) 太空战面板
        // ==================================================================

        /// <summary>
        /// ShipShieldsPanelVM.UpdateHandler() 的 Postfix。
        /// 原版这里只读 Stats（不含船板），所以战斗中装甲一直显示 0。
        /// 换成 GetLocationDeflection 之后才和实际减伤对得上。
        /// </summary>
        [HarmonyPatch]
        public static class ShipCombatArmourDisplayPatch
        {
            private static Type _vm;
            private static FieldInfo _fore, _starboard, _port, _aft;

            private static MethodBase TargetMethod()
            {
                _vm = AccessTools.TypeByName("Kingmaker.Code.UI.MVVM.VM.SpaceCombat.ShipShieldsPanelVM");
                return _vm == null ? null : AccessTools.Method(_vm, "UpdateHandler");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null)
                {
                    Main.LogError("[舰船] 找不到 ShipShieldsPanelVM.UpdateHandler —— 太空战面板的装甲数字不会变。");
                    return false;
                }
                _fore      = F(_vm, "ShipArmorFore");
                _starboard = F(_vm, "ShipArmorStarboard");
                _port      = F(_vm, "ShipArmorPort");
                _aft       = F(_vm, "ShipArmorAft");
                if (_fore == null || _starboard == null || _port == null || _aft == null)
                {
                    Main.LogError("[舰船] ShipShieldsPanelVM 上找不到四个 ShipArmorXxx 字段，战斗面板不接管。");
                    return false;
                }
                return true;
            }

            private static void Postfix(object __instance)
            {
                try
                {
                    if (ArmourPctNow() <= 0) return;
                    var a = PlayerArmour();
                    if (a == null) return;

                    SetRP(__instance, _fore,      a[0], false);
                    SetRP(__instance, _port,      a[1], false);
                    SetRP(__instance, _starboard, a[2], false);
                    SetRP(__instance, _aft,       a[3], false);

                    if (!_logged)
                    {
                        _logged = true;
                        Main.Log("[舰船] 太空战面板装甲数字已接管：艏 " + a[0] + " 左舷 " + a[1]
                                 + " 右舷 " + a[2] + " 艉 " + a[3]
                                 + "（原版这里只读属性、不含船板，所以一直显示 0）。本次会话只报这一条。");
                    }
                }
                catch (Exception e) { Main.LogError("[舰船] 战斗面板装甲显示失败: " + e.Message); }
            }

            private static bool _logged;
        }

        // ==================================================================
        // 3) 改装界面里的船模太大
        // ==================================================================

        /// <summary>
        /// ShipDollRoom.CreateSimpleAvatar(BaseUnitEntity) 的 Postfix。
        ///
        /// 原版最后一行：
        ///     m_SimpleAvatar.transform.localScale = unitEntityView.transform.localScale;
        /// 它把**战场那个 view 的缩放**照抄进展示房间，而这个房间的机位、灯光、
        /// 背景星球全是按**原版护卫舰**构图的。于是：
        ///   · 换成 Gothic（本体就是巡洋舰尺度的网格）→ 撑出画面
        ///   · 再切到大巡（我们额外 ×1.5152）→ 只剩半条船在屏幕里
        ///
        /// 归一化的依据：不管用哪个 prefab，最终观感都是
        ///     apparent / 护卫舰 = 1.5152 ^ (当前分档 − 护卫舰)
        /// 因为「prefab 原生档位」这一项在 vanilla 的缩放公式和我们的接管里正好抵消
        ///（我们乘的是 分档−prefab原生档，prefab 网格本身相对护卫舰大 prefab原生档−护卫舰 档）。
        /// 所以乘上 0.66 ^ (分档 − 护卫舰) 就退回护卫舰的观感，构图必然是对的。
        ///
        /// 想看大船的压迫感就把面板上的「改装界面船模缩放」调上去 —— 纯显示，随便调。
        /// ★ 只动展示房间那个复制体，战场上的模型一点不碰。★
        /// </summary>
        [HarmonyPatch]
        public static class ShipDollScalePatch
        {
            private static Type _room;
            private static FieldInfo _avatar;

            private static MethodBase TargetMethod()
            {
                _room = AccessTools.TypeByName("Kingmaker.UI.DollRoom.ShipDollRoom");
                return _room == null ? null : AccessTools.Method(_room, "CreateSimpleAvatar");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null)
                {
                    Main.LogError("[船模] 找不到 ShipDollRoom.CreateSimpleAvatar —— 改装界面的船模大小不受控。");
                    return false;
                }
                _avatar = F(_room, "m_SimpleAvatar");
                if (_avatar == null)
                {
                    Main.LogError("[船模] ShipDollRoom 上找不到 m_SimpleAvatar，改装界面船模大小不接管。");
                    return false;
                }
                return true;
            }

            private static void Postfix(object __instance)
            {
                try
                {
                    if (!Main.Enabled || Main.Settings == null) return;

                    var go = _avatar.GetValue(__instance) as GameObject;
                    if (go == null) return;

                    // 分档比护卫舰高几档
                    int tiers = 0;
                    try
                    {
                        var sz = StarshipChargesPatch.ShipSize();
                        tiers = (int)sz - (int)Size.Frigate_1x2;
                    }
                    catch { }
                    if (tiers < 0) tiers = 0;

                    float fit = 1f;
                    for (int i = 0; i < tiers; i++) fit *= 0.66f;

                    // ★留白★ 归一到"护卫舰观感"之后，巡洋/大巡仍然把画面填得很满
                    //（玩家实测："镜头距离还行，但有点填得太满"）。
                    // 原因是这两条船的网格比例比护卫舰更"宽扁"，同样的纵向尺度下横向更占地方。
                    // 再收一点点，留出边距。
                    // ★只对换过档的船生效★ tiers==0 时一个乘数都不加 ——
                    // 没换过船的玩家看到的必须还是 vanilla 原样，我们不去改原版构图。
                    if (tiers > 0) fit *= 0.85f;

                    float mult = Main.Settings.ShipDollScale / 100f;
                    if (mult <= 0f) mult = 1f;

                    var before = go.transform.localScale;
                    go.transform.localScale = before * fit * mult;

                    // ★去掉「本次会话只报这一条」★ 玩家实测："还原之后装配界面镜头缩得很近"，
                    // 而这条日志被节流掉了 ⇒ 还原后到底按几档算的、before 是多少，全看不见。
                    // 换船/还原都是低频，每次打一行不吵，却能一眼分辨是分档读错了、
                    // 还是 before 本身就带着上一轮的缩放。
                    {

                        // 顺手把包围盒打出来，万一某个 prefab 的网格不守 1.5152 那套比例，
                        // 看这行就知道该往哪个方向调滑条，不用靠猜。
                        string bounds = "(测不到)";
                        try
                        {
                            var rs = go.GetComponentsInChildren<Renderer>(true);
                            if (rs != null && rs.Length > 0)
                            {
                                var b = rs[0].bounds;
                                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                                bounds = b.size.ToString("F1");
                            }
                        }
                        catch { }
                        Main.Log("[船模] 改装界面缩放归一：原始 " + before.ToString("F3") + "　" + before.x.ToString("F3")
                                 + " -> " + go.transform.localScale.x.ToString("F3")
                                 + "（分档高于护卫舰 " + tiers + " 档 → ×" + fit.ToString("F3")
                                 + "，面板倍率 ×" + mult.ToString("F2") + "）  包围盒 " + bounds
                                 + "。只影响展示房间，战场模型不变。本次会话只报这一条。");
                    }
                }
                catch (Exception e) { Main.LogError("[船模] 改装界面缩放失败: " + e.Message); }
            }

            private static bool _logged;
        }

        // ==================================================================
        // 4) 改装界面 tooltip：把分档带来的加成写清楚
        // ==================================================================

        /// <summary>
        /// TooltipTemplateItem.GetBody() 的 Postfix —— 在组件 tooltip 末尾追加一段说明。
        ///
        /// ★ 为什么落在这里 ★
        /// 改装界面里**所有**组件槽（武器、船板、护盾发生器）的 tooltip 都是
        /// ShipComponentItemSlotVM 构造的同一个 TooltipTemplateItem：
        ///     Tooltip.Value = new TooltipTemplateItem(item, null, false, false, null, true);
        /// 所以一个补丁就能覆盖三类组件，不用去动任何槽位的布局或预制体 ——
        /// 这也是"轻量级"的含义：**纯追加**，原有的砖块一块不改、一块不删。
        ///
        /// 数字全部走 StarshipChargesPatch 的 Ui* 只读入口，和真正生效的算式同源。
        /// 原版有三处各抄一遍装甲聚合逻辑、结果互相不一致，这个教训不想再犯一次。
        ///
        /// 功能总开关关掉、或当前是护卫舰档时，一个字都不加，tooltip 保持原版。
        /// </summary>
        [HarmonyPatch]
        public static class ShipComponentTooltipPatch
        {
            private static Type _tpl;

            private static MethodBase TargetMethod()
            {
                _tpl = AccessTools.TypeByName("Kingmaker.Code.UI.MVVM.VM.Tooltip.Templates.TooltipTemplateItem");
                return _tpl == null ? null : AccessTools.Method(_tpl, "GetBody");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null) Main.LogError("[舰船] 找不到 TooltipTemplateItem.GetBody —— 改装界面不会显示加成说明。");
                return m != null;
            }

            private static void Postfix(object __instance,
                                        ref IEnumerable<Owlcat.Runtime.UI.Tooltips.ITooltipBrick> __result)
            {
                try
                {
                    if (__result == null) return;
                    var text = LineFor(__instance);
                    if (string.IsNullOrEmpty(text)) return;
                    __result = Append(__result, text);
                }
                catch (Exception e) { Main.LogError("[舰船] tooltip 追加失败: " + e.Message); }
            }

            private static IEnumerable<Owlcat.Runtime.UI.Tooltips.ITooltipBrick> Append(
                IEnumerable<Owlcat.Runtime.UI.Tooltips.ITooltipBrick> src, string text)
            {
                foreach (var b in src) yield return b;
                yield return new Kingmaker.Code.UI.MVVM.VM.Tooltip.Bricks.TooltipBrickText(text);
            }

            /// <summary>这件组件该不该加说明、加什么。不该加返回 null。</summary>
            private static string LineFor(object tpl)
            {
                int shieldPct = StarshipChargesPatch.UiShieldPct();
                int armourPct = StarshipChargesPatch.UiArmourPct();
                // 护卫舰档 / 功能关闭：三个百分比都是 0，武器加成也必然是 0，直接退出
                var item = Get(tpl, "m_Item");
                if (item == null) return null;

                string tier = StarshipChargesPatch.UiTierName();
                string head = "<color=#c8a45c>【卫队 Mod · " + tier + "】</color>\n";

                // ---- 武器：多打 + 射程 ----
                if (item.GetType().Name == "ItemEntityStarshipWeapon")
                {
                    int shots = StarshipChargesPatch.UiExtraShots(item);
                    int range = StarshipChargesPatch.UiExtraRange(item);
                    if (shots <= 0 && range <= 0) return null;

                    string slot = StarshipChargesPatch.SlotName(item);
                    string slotZh = slot == "Port" ? "左舷" : slot == "Starboard" ? "右舷"
                                  : slot == "Dorsal" ? "船脊" : slot == "Prow" ? "舰首"
                                  : slot == "Keel" ? "船底" : slot;

                    var sb = new StringBuilder(head);
                    sb.Append(slotZh).Append("槽位：");
                    if (shots > 0)
                    {
                        int baseCharges = StarshipChargesPatch.UiBaseCharges(item);
                        if (baseCharges >= 0)
                            sb.Append("每轮 <color=#7ec8ff>×").Append(baseCharges + shots)
                              .Append("</color> 次开火（原本 ").Append(baseCharges).Append(" 次）");
                        else
                            sb.Append("每轮 <color=#7ec8ff>额外 +").Append(shots).Append("</color> 次开火");
                        if (range > 0) sb.Append(" · ");
                    }
                    if (range > 0) sb.Append("射程 <color=#7ec8ff>+").Append(range).Append("</color>");
                    return sb.ToString();
                }

                // ---- 船板：装甲 ----
                var bp = Get(item, "Blueprint");
                if (bp != null && bp.GetType().Name == "BlueprintItemArmorPlating")
                {
                    if (armourPct <= 0) return null;
                    return head + "所有方向的减伤 <color=#7ec8ff>+" + armourPct
                         + "%</color>（下方船形图上的数字已是加成后的实际值）";
                }

                // ---- 护盾发生器 ----
                if (item.GetType().Name == "ItemEntityVoidShieldGenerator"
                    || (bp != null && bp.GetType().Name == "BlueprintVoidShieldGenerator"))
                {
                    if (shieldPct <= 0) return null;
                    return head + "四个扇区的护盾上限 <color=#7ec8ff>+" + shieldPct
                         + "%</color>（下方船形图上的数字已是加成后的实际值）";
                }

                return null;
            }
        }

        private static object Get(object o, string name)
        {
            // 逐层 DeclaredOnly —— 不这么写会在 Blueprint / Owner 这类
            // 基类派生类同名的成员上抛 AmbiguousMatchException（这坑踩过两次了）
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
                catch { }
            }
            return null;
        }
    }
}
