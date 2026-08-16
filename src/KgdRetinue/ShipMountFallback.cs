using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using UnityEngine;

namespace KgdRetinue
{
    /// <summary>
    /// 换船模后「光矛/鱼雷在虚空里开火」的修复。
    ///
    /// ================= 完整因果链（逐行反编译确认，非推测）=================
    ///
    /// 1) 武器美术挂点是**每个船体 prefab 上美术手工摆的**，代码从不写入：
    ///        StarshipView.cs:36   public List&lt;StarshipItemSlot&gt; ItemSlots = new List&lt;...&gt;();
    ///        StarshipView.cs:304  public void FillItemsSlots() { }      // ← 空的
    ///    全树只有 :36 定义、:216 和 :252 两处只读 FindAll，没有任何 GetComponentsInChildren 收集。
    ///    所以不同船模的挂点集合天差地别（实测：Gothic 9 个无 Prow / Dictator 20 个有 Prow）。
    ///
    /// 2) 挂载时按**武器要求的槽位类型**去找挂点，找不到就静默跳过：
    ///        StarshipView.cs:252  var list = ItemSlots.FindAll(x =&gt; x.Type == requiredSlots.SlotType);
    ///        StarshipView.cs:264  foreach (var item3 in list) { Instantiate(prefab, item3.transform...); }
    ///    list 为空时两个 foreach 都是 0 次迭代 —— **没有 else、没有兜底 transform**。
    ///    ⇒ 光矛的美术物体**根本没被创建过**，不是"挂错了位置"。
    ///
    /// 3) 开火点不读挂载物体的 transform，而是走 StarshipFxLocator 的双条件过滤：
    ///        AbilityDeliverStarshipShot.cs:77
    ///            list2 = locators.FindAll(x =&gt; x.weaponSlotType == weaponSlot.Type
    ///                                       &amp;&amp; x.starshipWeaponType == weapon.Blueprint.WeaponType);
    ///        AbilityDeliverStarshipShot.cs:136  list2 非空 → 从 finalShuffledLocators[i].transform.position 发射
    ///        AbilityDeliverStarshipShot.cs:140  list2 为空 → 从 castPosition 发射
    ///        AbilityDeliverStarshipShot.cs:69   castPosition = context.Caster.EyePosition   ← **舰船原点 = 虚空**
    ///    美术没被创建 ⇒ 没有对应 locator ⇒ list2 空 ⇒ 从原点开火。链条闭合。
    ///
    /// ================= 为什么"借用船脊挂点"这个办法成立 =================
    ///
    /// 关键在 StarshipView.cs:271：
    ///        component2.weaponSlotType = GetSlotType(requiredSlots.SlotType);
    /// 盖章用的是**武器要求的**槽位类型，**不是**实际挂上去的那个挂点的类型。
    /// vanilla 里两者恒等（FindAll 就是按前者筛的），所以这个区别从没暴露过；
    /// 但对兜底来说是决定性的 —— 把光矛挂到 Dorsal 挂点上，
    /// requiredSlots.SlotType 仍是 Prow ⇒ locator 仍被盖成 WeaponSlotType.Prow
    /// ⇒ 技能那边 x.weaponSlotType == weaponSlot.Type(Prow) **命中** ⇒ 从该处发射。
    ///
    /// 所以我们**完全不碰挂载逻辑**，只在 SetAllEquipment 跑之前把缺的挂点补齐，
    /// 让 vanilla 自己的 FindAll 能命中。后面整条链一个字都不用改。
    ///
    /// ================= 边界 =================
    /// · 只对**玩家座舰**生效，且只在**我们换过船模**时生效（CurrentPrefab 非空）。
    ///   原版船模缺挂点是原版行为，不替原版做主。
    /// · 合成挂点是**纯场景对象**（AddComponent 出来的 MonoBehaviour + 一个空 GameObject），
    ///   不进任何 [JsonProperty]，不写存档。卸载 mod 后一切照旧。
    ///   ★对比：WeaponSlot.Type 是 [JsonProperty]（WeaponSlot.cs:126），那个碰都不能碰。★
    /// · 幂等：合成出来的挂点带固定名字前缀，已存在就不再建。
    /// </summary>
    public static class ShipMountFallback
    {
        private const string TAG = "KGD_SyntheticSlot_";

        /// <summary>需要保证存在的五个**武器**挂点类型（StarshipView.GetSlotType:286-297 的映射表给出的全集）。</summary>
        private static readonly string[] WeaponSlotTypes = { "Dorsal", "Keel", "Port", "Starboard", "Prow" };

        private static Type _tSlot, _tSlotEnum;
        private static bool _resolved;
        private static bool _logged;

        public static void ResetLog() { _logged = false; }

        private static bool Resolve()
        {
            if (_resolved) return _tSlot != null && _tSlotEnum != null;
            _resolved = true;
            _tSlot     = AccessTools.TypeByName("StarshipItemSlot");       // 全局命名空间，无 namespace
            _tSlotEnum = AccessTools.TypeByName("StarshipItemSlotType");
            if (_tSlot == null || _tSlotEnum == null)
                Main.LogError("[挂点] 找不到 StarshipItemSlot / StarshipItemSlotType —— 挂点兜底不可用。");
            return _tSlot != null && _tSlotEnum != null;
        }

        /// <summary>
        /// StarshipView.SetAllEquipment() 的 Prefix。
        /// 它由 Start() 调用（StarshipView.cs:46-48），换船模重建 view 时必然走一遍，
        /// 正好是我们需要介入的时机 —— 在任何 EquipWeapon 之前。
        /// </summary>
        [HarmonyPatch]
        public static class SetAllEquipmentPatch
        {
            private static MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("StarshipView");
                return t == null ? null : AccessTools.Method(t, "SetAllEquipment");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null) Main.LogError("[挂点] 找不到 StarshipView.SetAllEquipment —— 挂点兜底不可用。");
                return m != null;
            }

            private static void Prefix(object __instance)
            {
                try { EnsureSlots(__instance as Component); }
                catch (Exception e) { Main.LogError("[挂点] 兜底失败: " + e.Message); }
            }
        }

        /// <summary>把缺失的武器挂点补上。已存在的类型一个不碰。</summary>
        private static void EnsureSlots(Component view)
        {
            if (view == null) return;
            if (!Main.Enabled || Main.Settings == null || !Main.Settings.ShipMountFallback) return;
            if (!Resolve()) return;

            // ---- 只管我们换过船模的玩家座舰 ----
            if (string.IsNullOrEmpty(StarshipViewTool.CurrentPrefab)) return;
            object entity = null;
            try
            {
                var uev = Get(view, "UnitEntityView") as Component;
                if (uev != null) entity = Get(uev, "Data");
            }
            catch { }
            object player = null;
            try { player = Game.Instance != null && Game.Instance.Player != null
                         ? (object)Game.Instance.Player.PlayerShip : null; } catch { }
            if (entity == null || player == null || !ReferenceEquals(entity, player)) return;

            var list = Get(view, "ItemSlots") as IList;
            if (list == null) return;

            // ---- 现有挂点类型普查，顺便找一个可借用的锚点 ----
            var have = new System.Collections.Generic.HashSet<string>();
            Transform anchor = null, dorsal = null;
            foreach (var s in list)
            {
                var c = s as Component;
                if (c == null) continue;
                string ty = null;
                var v = Get(c, "Type"); if (v != null) ty = v.ToString();
                if (ty == null) continue;
                have.Add(ty);
                if (anchor == null) anchor = c.transform;
                if (ty == "Dorsal") dorsal = c.transform;
            }

            // 优先借船脊 —— 它在四个已测船模上**都有**，是唯一的通用锚点；
            // 而且船脊在船体正上方中线，从那儿打出去比从任何舷侧点都自然。
            var baseAnchor = dorsal ?? anchor;
            if (baseAnchor == null)
            {
                if (!_logged) { _logged = true;
                    Main.LogError("[挂点] 这个船模一个挂点都没有，没法借位置，兜底放弃。"); }
                return;
            }

            int added = 0;
            var names = new System.Collections.Generic.List<string>();
            foreach (var want in WeaponSlotTypes)
            {
                if (have.Contains(want)) continue;

                object enumVal;
                try { enumVal = Enum.Parse(_tSlotEnum, want); }
                catch { continue; }                      // 这个游戏版本没有这个枚举值就跳过

                var go = new GameObject(TAG + want);
                go.transform.SetParent(baseAnchor, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;

                // Prow 往前推一点 —— 光矛从船脊正中打出去也能接受，
                // 但推到船艏更像样。推多少由面板控制，默认 0（＝纯船脊，最保险）。
                // 之所以默认 0：船体 prefab 的朝向轴我没有实据，猜错就会从船尾开火。
                // 想要更好看就自己拉滑条，日志里有船体长度可参考。
                if (want == "Prow" && Main.Settings.ShipProwOffsetPct != 0)
                {
                    float len = HullLength(view);
                    if (len > 0f)
                        go.transform.localPosition =
                            new Vector3(0f, 0f, len * Main.Settings.ShipProwOffsetPct / 100f);
                }

                var comp = go.AddComponent(_tSlot);
                Set(comp, "Type", enumVal);
                list.Add(comp);
                added++; names.Add(want);
            }

            if (added > 0 && !_logged)
            {
                _logged = true;
                Main.Log("[挂点] 已为换装后的船体补上 " + added + " 个合成挂点: "
                         + string.Join(" ", names.ToArray())
                         + "  （借用" + (dorsal != null ? "船脊" : "第一个可用") + "挂点的位置）\n"
                         + "  原理：武器美术挂不上去时开火点会退回舰船原点（表现为在虚空开火）。"
                         + "补上挂点后 vanilla 自己的 FindAll 就能命中；"
                         + "locator 的 weaponSlotType 是按**武器要求的**槽位盖章的"
                         + "（StarshipView:271），所以借船脊的位置不影响技能侧的匹配。\n"
                         + "  ★纯场景对象，不进存档★  本次会话只报这一条。");
            }
        }

        /// <summary>船体包围盒的最长边，给 Prow 前推量当基准。取不到返回 0。</summary>
        private static float HullLength(Component view)
        {
            try
            {
                var rs = view.GetComponentsInChildren<Renderer>(true);
                if (rs == null || rs.Length == 0) return 0f;
                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                var s = b.size;
                return Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            }
            catch { return 0f; }
        }

        // ---------------------------------------------------------------- 反射小工具

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags DECL = BindingFlags.Instance | BindingFlags.Public
                                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    var f = t.GetField(name, DECL);
                    if (f != null) return f.GetValue(o);
                    var p = t.GetProperty(name, DECL);
                    if (p != null && p.CanRead) return p.GetValue(o, null);
                }
                catch { }
            }
            return null;
        }

        private static void Set(object o, string name, object val)
        {
            if (o == null) return;
            const BindingFlags DECL = BindingFlags.Instance | BindingFlags.Public
                                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    var f = t.GetField(name, DECL);
                    if (f != null) { f.SetValue(o, val); return; }
                    var p = t.GetProperty(name, DECL);
                    if (p != null && p.CanWrite) { p.SetValue(o, val, null); return; }
                }
                catch { }
            }
        }
    }
}
