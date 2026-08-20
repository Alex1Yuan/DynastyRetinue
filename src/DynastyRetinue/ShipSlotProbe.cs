using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 船模挂点诊断。
    ///
    /// 为什么需要：换船模之后**光矛的开火点跑到虚空里**，而舷炮正常。
    /// 机制（StarshipView 已反编译确认）：
    ///     List&lt;StarshipItemSlot&gt; list = ItemSlots.FindAll(x =&gt; x.Type == requiredSlots.SlotType);
    ///     ...
    ///     Object.Instantiate(prefab, item3.transform.position, ..., item3.transform);
    /// 武器美术是挂到**船体 prefab 上那个 StarshipItemSlot 的 transform** 下面的。
    /// 挂点由美术在每个船模上手工摆放，不同船模的槽位类型集合**不一样**。
    /// 匹配不到 ⇒ list 为空 ⇒ 美术没挂上去 ⇒ 开火点退回原点。
    ///
    /// 所以要修必须先知道：**当前船模到底有哪些槽位类型**、我们的武器又需要哪些。
    /// 这个类就是把这两张表打进日志，避免靠猜。
    /// </summary>
    public static class ShipSlotProbe
    {
        private const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>把当前玩家舰的船体挂点 + 已装武器的需求槽位打进日志。</summary>
        public static void Dump()
        {
            try
            {
                var ship = StarshipViewTool.PlayerShip;
                if (ship == null) { Main.LogError("[挂点] 拿不到玩家座舰。"); return; }

                Main.Log("======== 船模挂点诊断 ========");
                // 先把最基本的三件事打出来 —— 上一轮就是因为缺这个，
                // 分不清"补丁没生效"还是"分档/船模压根没换过"
                Main.Log("  分档(Size)   = " + ship.Size);
                Main.Log("  自定义prefab = " + (string.IsNullOrEmpty(StarshipViewTool.CurrentPrefab)
                                                ? "(无，用的原版模型)" : StarshipViewTool.CurrentPrefab));
                Main.Log("  护盾扇区上限 = " + DumpShields(ship));
                Main.Log("  装甲(各面)   = " + DumpArmour(ship));
                var model = ShipModelCatalog.ByPrefab(StarshipViewTool.CurrentPrefab);
                Main.Log("  当前船模: " + (model != null ? model.ToString() : "原版模型")
                         + "   分档: " + ship.Size);

                // ---- 1. 船体 prefab 上实际有哪些 StarshipItemSlot ----
                object view = Get(ship, "View");
                if (view == null)
                {
                    Main.LogError("  拿不到 View —— 船现在没在场景里显示。"
                                  + "★挂点诊断要在【太空战里】点★（改装界面那个是 ShipDollRoom 的复制体，"
                                  + "身上没有 StarshipView，也没有武器挂点）。上面几行数值仍然有效。");
                    return;
                }

                var comp = FindStarshipView(view as Component);
                if (comp == null) { Main.LogError("  这个 View 上找不到 StarshipView 组件。"); return; }

                object slotsObj = Get(comp, "ItemSlots");

                var counts = new Dictionary<string, int>();
                int total = 0;
                var en = slotsObj as System.Collections.IEnumerable;
                if (en != null)
                {
                    foreach (var s in en)
                    {
                        if (s == null) continue;
                        total++;
                        string ty = "?";
                        var tv = Get(s, "Type");
                        if (tv != null) ty = tv.ToString();
                        int c; counts.TryGetValue(ty, out c); counts[ty] = c + 1;
                    }
                }
                var sb = new StringBuilder();
                foreach (var kv in counts) sb.Append(kv.Key).Append("×").Append(kv.Value).Append("  ");
                Main.Log("  船体挂点（StarshipItemSlot）共 " + total + " 个: "
                         + (sb.Length > 0 ? sb.ToString() : "（一个都没有）"));

                // ---- 2. 已装的武器各自需要什么槽位 ----
                Main.Log("  --- 已装武器 ---");
                reqCount.Clear();
                DumpWeapons(ship, counts);
                ReportCollisions();
                Main.Log("======== 诊断结束 ========");
                Main.Log("  判读（两件事，别混）："
                         + "\n    · 「挂点」= 船体上有没有这个类型的挂点。没有 ⇒ 美术挂不上去 ⇒ 开火点退回原点（在虚空开火）。"
                         + "\n    · 「美术」= 这件武器自己有没有炮塔模型。无 StarshipEE ⇒ EquipWeapon 第一行就 return，"
                         + "补再多挂点也不会出现炮 —— 这是武器数据本身的属性，换个存档换套武器就会变。");
            }
            catch (Exception e) { Main.LogError("[挂点] 诊断失败: " + e); }
        }

        /// <summary>四个扇区的护盾上限，用来验证 GetMax 的 Postfix 有没有生效。</summary>
        private static string DumpShields(object ship)
        {
            try
            {
                var part = GetPart(ship, "PartStarshipShields");
                if (part == null) return "(读不到 PartStarshipShields)";
                var sb = new StringBuilder();
                foreach (var sec in new[] { "Fore", "Port", "Starboard", "Aft" })
                {
                    object v = null;
                    try
                    {
                        var m = part.GetType().GetMethod("GetShields", new[] { typeof(Kingmaker.SpaceCombat.StarshipLogic.Parts.StarshipSectorShieldsType) });
                        var en = Enum.Parse(typeof(Kingmaker.SpaceCombat.StarshipLogic.Parts.StarshipSectorShieldsType), sec);
                        var s = m.Invoke(part, new[] { en });
                        v = Get(s, "Max");
                    }
                    catch { }
                    sb.Append(sec).Append("=").Append(v == null ? "?" : v.ToString()).Append("  ");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "(异常 " + e.Message + ")"; }
        }

        /// <summary>四个方向的装甲，用来验证 GetLocationDeflection 的 Postfix 有没有生效。</summary>
        private static string DumpArmour(object ship)
        {
            try
            {
                var hull = GetPart(ship, "PartStarshipHull");
                if (hull == null) return "(读不到 PartStarshipHull)";
                var m = hull.GetType().GetMethod("GetLocationDeflection");
                if (m == null) return "(找不到 GetLocationDeflection)";
                var et = m.GetParameters()[0].ParameterType;
                var sb = new StringBuilder();
                foreach (var loc in new[] { "Fore", "Port", "Starboard", "Aft" })
                {
                    object v = null;
                    try { v = m.Invoke(hull, new[] { Enum.Parse(et, loc) }); } catch { }
                    sb.Append(loc).Append("=").Append(v == null ? "?" : v.ToString()).Append("  ");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "(异常 " + e.Message + ")"; }
        }

        private static Component FindStarshipView(Component view)
        {
            if (view == null) return null;
            try
            {
                foreach (var c in view.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name == "StarshipView") return c;
            }
            catch { }
            return null;
        }

        private static void DumpWeapons(object ship, Dictionary<string, int> have)
        {
            try
            {
                var hull = Get(ship, "Hull") ?? GetPart(ship, "PartStarshipHull");
                var slots = Get(hull, "HullSlots");
                var weapons = Get(slots, "WeaponSlots") as System.Collections.IEnumerable;
                if (weapons == null) { Main.Log("    （读不到 WeaponSlots）"); return; }

                foreach (var ws in weapons)
                {
                    if (ws == null) continue;
                    string slotType = "?", wname = "(空)", wtype = "?";
                    object item = null;
                    try
                    {
                        item = Get(ws, "MaybeItem");
                        if (item != null)
                        {
                            var bp = Get(item, "Blueprint");
                            var n = Get(bp, "Name"); if (n != null) wname = n.ToString();
                            var wt = Get(bp, "WeaponType"); if (wt != null) wtype = wt.ToString();

                            // ★ 槽位类型要从**武器**上问，不是从槽位对象上问 ★
                            // 之前读的是 slot.Blueprint.Type / slot.SlotData.Type，五件武器全读成 "?"，
                            // 于是"挂点：没有"对所有武器无差别地打了一遍 —— 假警报，
                            // 实际上宏炮是能正常挂上的。
                            // StarshipChargesPatch.SlotName 走的是 weapon.WeaponSlot.Type，
                            // 多打/射程加成靠它分左右舷且实测生效，是已验证可用的那条路。
                            slotType = StarshipChargesPatch.SlotName(item);
                        }
                    }
                    catch { }

                    if (slotType == "?")
                    {
                        // 退路：再从槽位对象那边试一次（空槽位也走这里）
                        try
                        {
                            var bpSlot = Get(ws, "Blueprint") ?? Get(ws, "SlotData");
                            var t = Get(bpSlot, "Type") ?? Get(ws, "Type") ?? Get(ws, "SlotType");
                            if (t != null) slotType = t.ToString();
                        }
                        catch { }
                    }

                    if (item == null) { Main.Log("    槽位 " + slotType.PadRight(11) + " (空)"); continue; }

                    // ★判定必须用「美术要求的槽位」，不是「武器装在哪个槽位」★
                    // 两者可以不一致，实测：重型导弹炮台装在 Dorsal，美术却要 Prow。
                    // vanilla 的 FindAll 用的是前者（StarshipView.cs:250），
                    // 按后者判会给出假阳性 —— 显示「有 ✓」而实际一门炮都挂不上。
                    var reqTypes = ArtRequiredSlots(item);
                    string verdict;
                    if (reqTypes == null || reqTypes.Count == 0)
                    {
                        bool ok0 = have.ContainsKey(slotType);
                        verdict = slotType == "?" ? "槽位类型读不出来，无法判断"
                                : (ok0 ? "有 ✓（按安装槽位判，该武器无美术要求）"
                                       : "★没有 —— 开火点会跑到虚空★");
                    }
                    else
                    {
                        var missing = new List<string>();
                        foreach (var t in reqTypes) if (!have.ContainsKey(t)) missing.Add(t);
                        verdict = missing.Count == 0
                            ? "有 ✓"
                            : "★缺 " + string.Join("/", missing.ToArray())
                              + " —— 美术挂不上（注意：这是**美术要求**的槽位，"
                              + "和它装在 " + slotType + " 槽无关）★";
                        foreach (var t in reqTypes)
                        {
                            int c; reqCount.TryGetValue(t, out c); reqCount[t] = c + 1;
                        }
                    }
                    Main.Log("    槽位 " + slotType.PadRight(11) + " 武器 " + wname
                             + "  [" + wtype + "]   挂点: " + verdict
                             + "   美术: " + ArtReport(item));
                }
            }
            catch (Exception e) { Main.LogError("    读武器失败: " + e.Message); }
        }


        /// <summary>
        /// 这件武器**有没有美术可挂**。
        ///
        /// ★为什么必须单独查★ 之前所有诊断都在回答"挂点存不存在"，
        /// 而 EquipWeapon 在碰挂点之前还有两条静默 return（StarshipView.cs:238-246）：
        ///     if (weaponBP.StarshipEE == null) return;                       // 压根没有美术资产
        ///     var d = weaponBP.StarshipEE.EEArtSlotsDescription;
        ///     if (d == null || d.Count &lt;= 0) return;                         // 有资产但没挂件描述
        /// 两条都不打日志、不抛异常。所以"挂点全 ✓ 却一门炮都看不见"完全可能，
        /// 而且换个存档、换套武器就会变 —— 正是玩家实测到的现象。
        ///
        /// 顺带把每个挂件要求的槽位类型和 Prefab 是否为空一起打出来：
        /// RequiredSlots 才是 FindAll 真正用的键，它和"武器装在哪个槽位"不一定一致。
        /// </summary>
        private static string ArtReport(object item)
        {
            try
            {
                var bp = Get(item, "Blueprint");
                if (bp == null) return "读不到蓝图";
                var see = Get(bp, "StarshipEE");
                if (see == null)
                    return "★无 StarshipEE —— 这件武器根本没有炮塔美术，"
                         + "EquipWeapon 第一行就 return，和挂点无关★";

                var list = Get(see, "EEArtSlotsDescription") as System.Collections.IEnumerable;
                if (list == null) return "★EEArtSlotsDescription 为 null★";

                int n = 0; var sb = new System.Text.StringBuilder();
                foreach (var d in list)
                {
                    n++;
                    if (d == null) { sb.Append(" [null]"); continue; }
                    bool hasPrefab = Get(d, "Prefab") != null;
                    var req = Get(d, "RequiredSlots") as System.Collections.IEnumerable;
                    var types = new System.Text.StringBuilder();
                    if (req != null)
                        foreach (var r in req)
                        {
                            var t = Get(r, "SlotType");
                            if (types.Length > 0) types.Append("/");
                            types.Append(t == null ? "?" : t.ToString());
                        }
                    sb.Append(" [要求槽位 ").Append(types.Length > 0 ? types.ToString() : "（空）")
                      .Append(hasPrefab ? "" : "　★Prefab 为空★").Append("]");
                }
                if (n == 0)
                    return "★EEArtSlotsDescription 是空列表 —— 有资产但没有任何挂件，"
                         + "EquipWeapon 第二个 if 就 return★";
                return n + " 个挂件" + sb.ToString();
            }
            catch (Exception e) { return "查美术失败: " + e.Message; }
        }


        /// <summary>各槽位类型被几件武器的美术抢占。</summary>
        private static readonly Dictionary<string,int> reqCount = new Dictionary<string,int>(StringComparer.Ordinal);

        /// <summary>
        /// 报告"多件武器的美术抢同一个挂点类型"。
        ///
        /// ★这是 vanilla 的硬限制，补挂点解决不了★
        /// StarshipView.cs:252-259 是**先毁后建**，而且作用于该类型的**全部**挂点：
        ///     var list = ItemSlots.FindAll(x =&gt; x.Type == requiredSlots.SlotType);
        ///     foreach (var item2 in list) if (item2.itemPrefab != null) Destroy(item2.itemPrefab);
        /// 所以两件要求同一类型的武器，**后处理的那件会把前一件的美术砸掉**，
        /// 最终只剩最后一件可见。多补几个同类型挂点也没用 —— 那只会让胜者出现好几份。
        /// </summary>
        private static void ReportCollisions()
        {
            foreach (var kv in reqCount)
            {
                if (kv.Value < 2) continue;
                Main.LogError("  ★挂点争用★ 有 " + kv.Value + " 件武器的美术都要求 " + kv.Key
                            + " 槽位。vanilla 是先毁后建（StarshipView.cs:252-259），"
                            + "后处理的那件会把前一件砸掉 ⇒ 最终只看得到一件。"
                            + "这是原版硬限制，补再多挂点也没用（只会让胜者出现好几份）。");
            }
        }

        /// <summary>这件武器的美术要求哪些槽位类型。没有美术返回 null。</summary>
        private static List<string> ArtRequiredSlots(object item)
        {
            try
            {
                var bp = Get(item, "Blueprint"); if (bp == null) return null;
                var see = Get(bp, "StarshipEE"); if (see == null) return null;
                var descs = Get(see, "EEArtSlotsDescription") as System.Collections.IEnumerable;
                if (descs == null) return null;
                var r = new List<string>();
                foreach (var d in descs)
                {
                    if (d == null) continue;
                    var req = Get(d, "RequiredSlots") as System.Collections.IEnumerable;
                    if (req == null) continue;
                    foreach (var q in req)
                    {
                        var t = Get(q, "SlotType");
                        if (t != null && !r.Contains(t.ToString())) r.Add(t.ToString());
                    }
                }
                return r;
            }
            catch { return null; }
        }

        private static object GetPart(object entity, string typeName)
        {
            // ★ 不要去枚举 Entity.Parts ★
            // 它是 PartsManager，不是 IEnumerable —— 上一版在这里当集合遍历，
            // 结果永远拿不到，日志里那两条「读不到 PartStarshipHull」是这个 bug，
            // 不是船上真没有这个 Part。StarshipEntity 上有现成的强类型属性，直接用。
            string prop = typeName == "PartStarshipHull" ? "Hull"
                        : typeName == "PartStarshipShields" ? "Shields"
                        : null;
            if (prop != null)
            {
                var v = Get(entity, prop);
                if (v != null) return v;
            }

            // 退路：PartsManager 上找 GetAll()/Parts 之类能枚举的东西
            try
            {
                var pm = Get(entity, "Parts");
                if (pm != null)
                {
                    var en = pm as System.Collections.IEnumerable;
                    if (en == null)
                    {
                        var m = pm.GetType().GetMethod("GetAll", System.Type.EmptyTypes);
                        if (m != null) en = m.Invoke(pm, null) as System.Collections.IEnumerable;
                        if (en == null) en = Get(pm, "Parts") as System.Collections.IEnumerable;
                    }
                    if (en != null)
                        foreach (var p in en) if (p != null && p.GetType().Name == typeName) return p;
                }
            }
            catch { }
            return null;
        }

        private static object Get(object o, string name)
        {
            // ★ 必须逐层 DeclaredOnly ★
            // 不带 DeclaredOnly 的 GetProperty/GetField，在基类和派生类都声明了同名成员时
            // （Entity.View / Owner / Blueprint 全都是）会抛 AmbiguousMatchException。
            // v0.20.0 的舰船补丁就是栽在这上面，这个文件里我又原样写了一遍 ——
            // 结果 View 永远拿不到，诊断在哪点都是"拿不到 View"。
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
