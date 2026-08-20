using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using UnityEngine;

namespace DynastyRetinue
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

        /// <summary>
        /// 从一个**原生**舰首挂点学两个比例。
        ///
        /// ★为什么学"比例"而不是"包围盒归一化坐标"★
        /// 第一版用 (p - bb.min)/bb.size 归一化，错的：Gothic 的包围盒高 2.54，
        /// 但其中很大一块是船底垂下来的吊坠装饰（Keel 挂点在 y=-0.99，包围盒底 -1.09）；
        /// Dictator 的包围盒高 1.95，几乎全是实体船身。同一个归一化 y 在两条船上
        /// 指向完全不同的结构层。
        ///
        /// 改成用**两条船都有的挂点**当标尺：
        ///   垂直 = (舷炮y - 舰首y) / (船脊y - 舷炮y)     ← 舰首比舷炮低多少，以"舷炮到船脊"为单位
        ///   纵向 = (包围盒最前 - 舰首z) / 船长            ← 从船头往回收多少
        /// 这两个量对船体比例不敏感，因为分子分母都是同一条船自己的结构尺度。
        ///
        /// Dictator 实测（19:52:37 诊断）：
        ///   船脊 y=0.50   舷炮 y=-0.01   舰首 y=-0.41   龙骨 y=-0.44
        ///   舰首 z=2.68   包围盒 z∈[-3.00,3.00] 船长 5.99
        ///   ⇒ 垂直 = (-0.01 - (-0.41)) / (0.50 - (-0.01)) = 0.40/0.51 = 0.784
        ///   ⇒ 纵向 = (3.00 - 2.68) / 5.99 = 0.053
        /// 也就是说**舰首炮基本贴在龙骨线上**（-0.41 vs 龙骨 -0.44），
        /// 而我之前一直摆在舷炮中线，整整高了一层甲板 —— 这就是"炮浮在船艏上方"的全部原因。
        /// </summary>
        private static void LearnFrom(Vector3 prowLocal, Bounds bb, float frontZ, float broadsideY, float dorsalY)
        {
            try
            {
                var st = Main.Settings; if (st == null) return;
                float span = dorsalY - broadsideY;
                if (Mathf.Abs(span) < 1e-3f || bb.size.z < 1e-3f) return;

                float drop  = (broadsideY - prowLocal.y) / span;
                float zback = (frontZ - prowLocal.z) / bb.size.z;
                if (float.IsNaN(drop) || float.IsNaN(zback)) return;
                // 离谱值不学：挂点被父级变换坑了、或者这条船结构特殊
                if (drop < -3f || drop > 5f || zback < -0.2f || zback > 0.6f)
                {
                    Main.LogError("[挂点] 学到的比例超出合理范围（下沉 " + drop.ToString("F2")
                                + "　后收 " + zback.ToString("F3") + "），本次不采纳。");
                    return;
                }

                bool changed = !st.ProwLearned
                            || Mathf.Abs(st.ProwDropRatio - drop) > 0.01f
                            || Mathf.Abs(st.ProwZBackRatio - zback) > 0.002f;
                st.ProwDropRatio = drop; st.ProwZBackRatio = zback; st.ProwLearned = true;
                try
                {
                    var m = ShipModelCatalog.ByPrefab(StarshipViewTool.CurrentPrefab);
                    st.ProwLearnedFrom = m != null ? m.Hull : (StarshipViewTool.CurrentPrefab ?? "?");
                }
                catch { st.ProwLearnedFrom = "?"; }

                if (changed)
                    Main.Log("[挂点] ★学到原生舰首挂点★ 来源「" + st.ProwLearnedFrom + "」"
                           + "　舰首局部坐标 " + prowLocal.ToString("F2")
                           + "\n  舷炮 y=" + broadsideY.ToString("F2") + "　船脊 y=" + dorsalY.ToString("F2")
                           + "　包围盒最前 z=" + bb.max.z.ToString("F2") + "　船长 " + bb.size.z.ToString("F2")
                           + "\n  ⇒ 下沉比 " + drop.ToString("F3") + "（舰首比舷炮低多少，以舷炮→船脊为 1）"
                           + "　后收比 " + zback.ToString("F3") + "（从船头往回收，占船长）"
                           + "\n  以后没有原生舰首挂点的船就按这两个比例摆。这是美术自己摆的位置，比任何公式都可信。");
            }
            catch (Exception e) { Main.LogError("[挂点] 学习失败: " + e.Message); }
        }

        /// <summary>
        /// 用学来的（或 Dictator 实测的默认）比例算这条船的舰首挂点。
        /// 拿不到舷炮/船脊这两个标尺就返回 false，交给上层退回公式。
        /// </summary>
        private static bool TryLearned(Bounds bb, float frontZ, float broadsideY, float? dorsalY, out Vector3 p)
        {
            p = Vector3.zero;
            var st = Main.Settings;
            if (st == null || !st.ShipProwUseLearned) return false;
            if (!dorsalY.HasValue) return false;
            float span = dorsalY.Value - broadsideY;
            if (Mathf.Abs(span) < 1e-3f) return false;
            // ★撞角让位★ 再沿轴向退开"撞角外伸的长度"。
            // 撞角外伸 = 包围盒最前 - 实体船头（命中遮罩最前）：
            //     Gothic   3.76 - 3.20 = 0.56   ← 长撞角，退得多
            //     Dictator 3.00 - 2.94 = 0.06   ← 钝头，几乎不退
            // 这个量是**每条船自己量出来的**，不是我又定一个魔法数：
            // 撞角越长的船，炮越往后让，正好是玩家要的"离撞角有一些距离"。
            //
            // 自检：套回 Dictator 得 2.94 - 0.26 - 0.06 = 2.62，
            // 而它原生 prow_01 在 2.68 —— 差 0.06。也就是说这条公式在
            // **我们知道答案的那条船上几乎复现了答案**，不是无根据的偏移。
            float ram = Mathf.Max(0f, bb.max.z - frontZ) * st.ProwRamClearance;
            p = new Vector3(0f,
                            broadsideY - st.ProwDropRatio * span,
                            frontZ - st.ProwZBackRatio * bb.size.z - ram);
            return true;
        }


        /// <summary>
        /// 船体**实体**前端的 z（StarshipView 局部空间）。拿不到返回 false。
        ///
        /// ★为什么不能用包围盒的 max.z★
        /// Gothic 的包围盒最前端是 3.76，而 vanilla 自己从网格顶点烘焙的 frontHitPositions
        /// 最前只到 3.20 —— 中间那 0.56 是**一根细撞角**，只有它一根戳在前面。
        /// 拿包围盒当基准，炮就被推到撞角上（玩家实测："炮看起来在船首上"）。
        /// Dictator 的船头是钝的，两个数几乎重合（3.00 vs 2.94），所以在它身上看不出区别 ——
        /// 这正是"只在一条船上验证"会漏掉的那类差异。
        ///
        /// frontHitPositions 是 50 个采样点，细长突起本来就采不到几个，
        /// 于是它天然表达的是"船头实体部分到哪儿为止"，正是我们要的基准。
        /// （StarshipFxHitMask.cs:47 保证 front 里的点 z 恒 &gt; 0。）
        /// </summary>
        private static bool HullFrontZ(Component view, out float z)
        {
            z = 0f;
            try
            {
                var mask = Get(view, "starshipFxHitMask");
                if (mask == null) return false;
                var en = Get(mask, "frontHitPositions") as IEnumerable;
                if (en == null) return false;
                float m = float.MinValue; int n = 0;
                foreach (var o in en)
                {
                    if (!(o is Vector3)) continue;
                    var v = (Vector3)o; n++;
                    if (v.z > m) m = v.z;
                }
                if (n == 0) return false;
                z = m; return true;
            }
            catch { return false; }
        }


        /// <summary>
        /// 已装武器的**美术**实际要求哪些槽位类型（去重）。
        ///
        /// ★为什么不能只认死那 5 种★ v0.39.x 的实测把这个假设打穿了：
        ///     槽位 Prow    焚化者光矛      → 美术要求 Prow          （一致）
        ///     槽位 Prow    加努斯之勇鱼雷  → 美术要求 TorpedoTubes  （★不一致★）
        ///     槽位 Dorsal  重型导弹炮台    → 美术要求 Prow          （★不一致★）
        /// vanilla 的 FindAll 用的是**美术描述里写死的 RequiredSlots**
        /// （StarshipView.cs:250），不是"这件武器装在哪个槽位"。
        /// 而 TorpedoTubes 是 StarshipItemSlotType 的合法成员（=4），
        /// Gothic 和 Dictator 两条船都没有这种挂点 —— 于是那件鱼雷的美术永远挂不上。
        ///
        /// 拿不到就返回 null，调用方退回原来那 5 种。
        /// </summary>
        private static System.Collections.Generic.HashSet<string> NeededSlotTypes(object shipEntity)
        {
            try
            {
                var hull = shipEntity == null ? null : Get(shipEntity, "Hull");
                if (hull == null) return null;
                var slots = Get(hull, "HullSlots");
                if (slots == null) return null;
                var ws = Get(slots, "WeaponSlots") as IEnumerable;
                if (ws == null) return null;

                var need = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                // ★只补 vanilla 船体**真的会摆**的那几种★
                // v0.40.0 改成"照美术要求补"之后，给鱼雷补出了 TorpedoTubes 挂点，
                // 屏幕上多了一坨没贴图的白色占位体 —— 玩家实测。
                // 回头看：**没有任何一条 vanilla 船体有 TorpedoTubes 挂点**，
                // 那不是遗漏而是设计：鱼雷是船体开口，不该有外挂炮塔，
                // 它那份美术就是个占位。同理 AugurArray / LandingBays / PlasmaDrive
                // 是结构件不是武器。所以取交集，别把占位体请出来。
                var allowed = new System.Collections.Generic.HashSet<string>(WeaponSlotTypes, StringComparer.Ordinal);
                foreach (var slot in ws)
                {
                    object item = null;
                    try { item = Get(slot, "Item"); } catch { }
                    if (item == null) continue;
                    var bp = Get(item, "Blueprint"); if (bp == null) continue;
                    var see = Get(bp, "StarshipEE"); if (see == null) continue;
                    var descs = Get(see, "EEArtSlotsDescription") as IEnumerable; if (descs == null) continue;
                    foreach (var d in descs)
                    {
                        if (d == null) continue;
                        var req = Get(d, "RequiredSlots") as IEnumerable; if (req == null) continue;
                        foreach (var r in req)
                        {
                            var t = Get(r, "SlotType");
                            if (t == null) continue;
                            var ts = t.ToString();
                            if (allowed.Contains(ts)) need.Add(ts);
                        }
                    }
                }
                return need.Count > 0 ? need : null;
            }
            catch { return null; }
        }

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

            // ★ 坐标系：StarshipView.transform 的局部空间 ★
            // 这是全局唯一有实据的那个：
            //   StarshipView.cs:315   Gizmos.DrawSphere(transform.TransformPoint(frontHitPosition))
            //   StarshipFxHitMask.cs:47-53  item.z <= 0 → 船尾 / 否则船艏      ⇒ +Z = 船艏
            //   StarshipFxHitMask.cs:37-46  dot(-right) ≥ 0.5 → 左舷          ⇒ -X = Port, +X = Starboard
            // ★ 而挂点的 localPosition 相对的是**未知的父级**（美术随便挂在哪一层），
            //   所以一律走 root.InverseTransformPoint(slot.position) 换算，别读 localPosition。
            var root = view.transform;

            // ---- 现有挂点普查 ----
            var have = new System.Collections.Generic.HashSet<string>();
            Transform dorsal = null, anyT = null;
            float? dorsalLocalY = null;
            float? minBroadsideY = null;
            float pxSum = 0f, pySum = 0f, sxSum = 0f, sySum = 0f;
            int pN = 0, sN = 0;
            float zMin = float.MaxValue, zMax = float.MinValue;
            Transform realProw = null;      // ★原生 Prow 挂点（不是我们合成的）—— 学习样本
            foreach (var s in list)
            {
                var c = s as Component;
                if (c == null) continue;
                var v = Get(c, "Type"); if (v == null) continue;
                string ty = v.ToString();
                have.Add(ty);
                if (anyT == null) anyT = c.transform;

                Vector3 l = root.InverseTransformPoint(c.transform.position);
                if (ty == "Dorsal") { dorsal = c.transform; dorsalLocalY = l.y; }
                if (ty == "Prow" && !c.gameObject.name.StartsWith(TAG, StringComparison.Ordinal))
                    realProw = c.transform;
                if (l.z < zMin) zMin = l.z;
                if (l.z > zMax) zMax = l.z;
                if (ty == "Port")           { pxSum += l.x; pySum += l.y; pN++; }
                else if (ty == "Starboard") { sxSum += l.x; sySum += l.y; sN++; }
                if (ty == "Port" || ty == "Starboard")
                    if (!minBroadsideY.HasValue || l.y < minBroadsideY.Value) minBroadsideY = l.y;
            }
            if (anyT == null)
            {
                if (!_logged) { _logged = true;
                    Main.LogError("[挂点] 这个船模一个挂点都没有，没法定位，兜底放弃。"); }
                return;
            }

            // ---- L0 轴向闸门 ----
            // 绕 Y 轴 180° 会**同时**翻转 X 和 Z，所以「Port 在 -x、Starboard 在 +x」
            // 这一条同时验证了 +Z 是船艏 —— 正好挡住"从船尾开火"这个唯一的致命失效。
            // 闸门不过就一层都不推，直接退到 L3（＝现状，位置不变）。
            bool axisOk = false; string axisWhy;
            if (pN == 0 || sN == 0) axisWhy = "没有成对的 Port/Starboard 挂点，无法验轴";
            else
            {
                float px = pxSum / pN, sx = sxSum / sN;
                if (px < 0f && sx > 0f && sx - px > 1e-3f) { axisOk = true; axisWhy = null; }
                else axisWhy = "Port 均值 x=" + px.ToString("F2") + " / Starboard 均值 x=" + sx.ToString("F2")
                             + "，与 StarshipFxHitMask 的约定不符";
            }

            // ---- 算船艏位置（L0.5 学来的 → L1 → L2 → L3）----
            Vector3 prowLocal; string how;
            float hullLenZ = 0f;
            Bounds bb; bool hasBounds = HullBoundsLocal(view, root, out bb);
            if (hasBounds) hullLenZ = bb.size.z;
            // 实体船头 z：优先用 vanilla 从网格烘的 frontHitPositions，拿不到才退回包围盒最前端
            float frontZ; bool hasFront = HullFrontZ(view, out frontZ);
            if (!hasFront) frontZ = hasBounds ? bb.max.z : 0f;

            // ★ 从原生 Prow 挂点学 ★
            // 这条船自己就有 vanilla 摆好的舰首挂点（Dictator 有，Gothic 没有）时，
            // 把它换算成**包围盒归一化坐标**存下来，下次遇到没有 Prow 的船就照搬。
            // 这是唯一一份"美术自己认为舰首炮该在哪"的地面真值 ——
            // 比任何"包围盒 max.z 往回收 N%"的公式都可信，那种公式我猜错了六版。
            float bsY = (pN > 0 && sN > 0) ? (pySum / pN + sySum / sN) * 0.5f
                      : (minBroadsideY.HasValue ? minBroadsideY.Value : 0f);
            bool hasBs = (pN > 0 && sN > 0) || minBroadsideY.HasValue;
            if (realProw != null && hasBounds && hasBs && dorsalLocalY.HasValue && bb.size.z > 1e-4f)
                LearnFrom(root.InverseTransformPoint(realProw.position), bb, frontZ, bsY, dorsalLocalY.Value);

            // 船体中线 X：用左右舷挂点反推（它们本来就骑在中线两侧）
            float cx = (pN > 0 && sN > 0) ? (pxSum / pN + sxSum / sN) * 0.5f : (hasBounds ? bb.center.x : 0f);

            // ★ 高度 Y：左右舷挂点的平均高度（＝"舷炮中线"）★
            //
            // ================= 为什么是这个，别再改 =================
            // 这条线来回改过六版，全靠肉眼看截图，改错了三次。v0.34.0 用日志数字
            // 把它钉死了 —— 玩家明确说「那次炮位置是对的，只是地板上有东西」的那一跑，
            // 日志里合成挂点是 **(0.00, 0.05, 3.49)**，而 imperial_cruiser_gothic 的
            // 实测几何是：
            //     船体包围盒  中心(0.00,0.18,0.48) 尺寸(2.14,2.54,6.55)  z∈[-2.79,3.76]
            //     Port  ×4    x均值=-0.36  y均值=0.06
            //     Starboard×4 x均值= 0.36  y均值=0.05
            // 0.05 = 舷炮中线，3.49 = bb.max.z - 4%·船长。两个数都只有 v0.28.0 的
            // 公式能算出来 ⇒ **被玩家判定为正确的就是 v0.28.0**。
            //
            // 反过来，包围盒底 + 10% 会算出 y = -1.09 + 0.25 = **-0.84**，
            // 比 Keel 挂点（y=-0.99）只高 0.15 —— 那是船底吊坠结构所在的空腔，
            // 炮挂在那里就是"飘在船下面的虚空里"（v0.33.1 实测截图）。
            //
            // 我之前把 v0.28.0 记成"炮飘在撞角上方"，是因为那一跑同时合成了 Keel，
            // 画面里有两门炮，我把**低的那门 Keel 复制品**当成了主炮。
            // v0.29.4 关掉 Keel 合成后只剩一门，误判才被日志坐标戳穿。
            //
            // 演进（✓/✗ 以玩家实测截图为准）：
            //   v0.28.0 舷炮中线 + 回收 4%     ✓ 正确（但当时 Keel 也合成，多一门在地板上）
            //   v0.28.1 抬到船脊高度            ✗
            //   v0.29.2 回收量 12% → 18%        ✗ 越收越靠后
            //   v0.29.3 包围盒底 + 10%          ✗ 沉到船底虚空
            //   v0.30.0 舷炮最低点 + 回收 18%   ✗ 高度对了但 z 还是太靠后
            //   v0.33.1 又改回包围盒底 + 10%    ✗ 同 v0.29.3
            //   v0.34.0 还原 v0.28.0 的两个数   ← 现在这版
            //
            // 包围盒底那条退路**故意不保留** —— 它是错的，留着只会被将来的我再选中。
            float cy;
            if (pN > 0 && sN > 0)            cy = (pySum / pN + sySum / sN) * 0.5f;
            else if (minBroadsideY.HasValue) cy = minBroadsideY.Value;
            else if (dorsalLocalY.HasValue)  cy = dorsalLocalY.Value;
            else if (hasBounds)              cy = bb.center.y;
            else                             cy = 0f;

            Vector3 learned;
            if (axisOk && hasBounds && hasBs && TryLearned(bb, frontZ, bsY, dorsalLocalY, out learned))
            {
                // L0.5：按 Dictator 实测比例摆。x 仍取舷炮中线 —— 那是这条船自己的实测值。
                prowLocal = new Vector3(cx, learned.y, learned.z);
                how = "L0.5 比例定位（" + (Main.Settings.ProwLearned
                        ? "学自「" + (Main.Settings.ProwLearnedFrom ?? "?") + "」"
                        : "Dictator 实测默认值")
                    + "　下沉 " + Main.Settings.ProwDropRatio.ToString("F3")
                    + "　后收 " + Main.Settings.ProwZBackRatio.ToString("F3")
                    + "　前端基准 " + frontZ.ToString("F2") + (hasFront ? "(命中遮罩)" : "(包围盒)")
                    + "　撞角让位 " + Mathf.Max(0f, bb.max.z - frontZ).ToString("F2") + "）";
            }
            else if (axisOk && hasBounds)
            {
                // 没学到过就退回公式。这个公式**猜错过六版**，只是"有总比没有强"，
                // 别再花时间调它 —— 正解是让玩家在 Dictator 上过一次，把真值学下来。
                prowLocal = new Vector3(cx, cy, frontZ - bb.size.z * 0.04f);
                how = "L1 包围盒(" + bb.size.ToString("F1") + ") + 舷炮中线　"
                    + "<未学到原生舰首挂点：切一次 Dictator（大巡）即可学到真值>";
            }
            else if (axisOk && zMax > zMin)
            {
                float push = (zMax - zMin) * 0.25f;
                prowLocal = new Vector3(cx, cy, zMax + push);
                how = "L2 挂点跨度外推（zMax " + zMax.ToString("F1") + " +" + push.ToString("F1") + "）";
            }
            else
            {
                var src = dorsal ?? anyT;
                prowLocal = root.InverseTransformPoint(src.position);
                how = "L3 借" + (dorsal != null ? "船脊" : "第一个可用") + "挂点原位"
                    + (axisOk ? "（拿不到船体包围盒）" : "（★轴向闸门未通过：" + axisWhy + "★）");
            }

            // 面板微调：沿 root 的 +Z / +Y，以船体对应方向的长度为单位。默认都是 0。
            int pct = Main.Settings.ShipProwOffsetPct;
            if (pct != 0)
            {
                float unit = hullLenZ > 0f ? hullLenZ : (zMax > zMin ? zMax - zMin : 0f);
                prowLocal.z += unit * pct / 100f;
            }
            int upPct = Main.Settings.ShipProwUpPct;
            if (upPct != 0 && hasBounds)
                prowLocal.y += bb.size.y * upPct / 100f;

            int added = 0;
            var names = new System.Collections.Generic.List<string>();

            // 优先按**武器美术实际要求**的槽位类型来补；读不到才退回那 5 种硬编码。
            var need = NeededSlotTypes(entity);
            System.Collections.Generic.IEnumerable<string> wants = need != null
                ? (System.Collections.Generic.IEnumerable<string>)need
                : WeaponSlotTypes;
            if (need != null)
            {
                var miss = new System.Collections.Generic.List<string>();
                foreach (var w in need) if (!have.Contains(w)) miss.Add(w);
                if (miss.Count > 0)
                    Main.Log("[挂点] 武器美术要求的槽位类型: " + string.Join(" ", new List<string>(need).ToArray())
                           + "　船体缺: " + string.Join(" ", miss.ToArray()));
            }

            foreach (var want in wants)
            {
                if (have.Contains(want)) continue;

                // ★ Keel（船底）默认不合成 ★
                // 一件武器的美术描述里可以列**多个** RequiredSlots，
                // vanilla 会在每一个匹配到的挂点上都实例化一份
                //（StarshipView.cs:250-266 是两层 foreach）。
                // 所以给一艘本来没有 Keel 挂点的船凭空补一个，
                // 可能让某件 Prow 武器的美术**多长出第二份**挂在船腹下。
                // 而玩家船上通常压根没有 Keel 武器（实测：光矛/鱼雷=Prow、
                // 迫击炮=Dorsal、另两门=Port/Starboard），补它零收益。
                // 真装了 Keel 武器再到面板打开这个开关。
                if (want == "Keel" && !Main.Settings.ShipSynthKeel) continue;

                object enumVal;
                try { enumVal = Enum.Parse(_tSlotEnum, want); }
                catch { continue; }

                var go = new GameObject(TAG + want);
                // ★ 父级必须是 StarshipView 自己，不能是船脊挂点 ★
                //   一是坐标系：只有 root 的 +Z 有实据是船艏；
                //   二是旋转：开火点 = slot.position + slot.rotation * (locator 在美术里的偏移)，
                //     挂点转 90° 炮口就绕挂点甩 90°，所以 localRotation 必须显式归零。
                //   安全性：Projectile.cs:262 里 vanilla 自己就是
                //     starshipTarget.View.GetComponentInChildren<StarshipView>()，
                //     说明 sv 必在 view 根子树内 ⇒ AbilityDeliverStarshipShot.cs:75 的
                //     GetComponentsInChildren<StarshipFxLocator>() 照样收得到，不会退回虚空。
                go.transform.SetParent(root, false);
                go.transform.localPosition = (want == "Keel" && hasBounds)
                    ? new Vector3(cx, bb.min.y + bb.size.y * 0.04f, prowLocal.z)   // 船底：同 z、贴底
                    : prowLocal;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale    = Vector3.one;

                var comp = go.AddComponent(_tSlot);
                Set(comp, "Type", enumVal);
                list.Add(comp);
                added++; names.Add(want);
            }

            // ★不再"本次会话只报一条"★ 那个节流在 v0.34.1 的排查里直接挡住了诊断：
            // 玩家换了存档、连点了五次换船，日志里只有第一次那条，
            // 结果"这个档到底补没补挂点"完全看不出来。挂点合成本来就是低频事件
            // （只在 view 重建时跑），每次都打一行不吵。
            if (added > 0)
            {
                Main.Log("[挂点] 补上 " + added + " 个合成挂点: " + string.Join(" ", names.ToArray())
                         + "\n  定位: " + how + "　局部坐标 " + prowLocal.ToString("F2")
                         + "　父级=StarshipView　旋转已归零"
                         + (pct != 0 ? "　面板微调 " + pct + "%" : "")
                         + "\n  ★纯场景对象，不进存档★");
            }
            else
            {
                // 一个都没补也要说话 —— "静默"和"船本来就齐全"在日志上长得一样，
                // 而这两者要采取的行动完全相反。
                Main.Log("[挂点] 本次没有补任何挂点（船模自带的挂点已覆盖所有已装武器要求的槽位类型，"
                         + "或者缺的那种被设置关掉了）。");
            }
        }

        /// <summary>
        /// 船体包围盒，换算到 root 的局部空间。拿不到返回 false。
        ///
        /// ★ 为什么不用 Renderer.bounds ★
        /// 那是**世界轴对齐**盒，船一转就虚高 —— 转 45° 时最长边虚高约 41%。
        /// 旧的 HullLength() 就是这个毛病。
        /// ★ 为什么不收全部 Renderer ★
        /// VFXRenderer 也是 Renderer，等离子尾焰会把盒子往船尾拉长，船艏就算歪了。
        /// 只收 MeshFilter / SkinnedMeshRenderer 的 sharedMesh.bounds（模型空间，
        /// 且不受 Read/Write 开关影响）。
        /// ★ 跳过挂在 StarshipItemSlot 之下的网格 ★
        /// 那是已实例化的武器美术，会把"船体前端"带跑。
        /// （Prefix 时机上美术其实还没生成，这层是保险。）
        /// </summary>
        private static bool HullBoundsLocal(Component view, Transform root, out Bounds box)
        {
            box = new Bounds();
            bool any = false;
            try
            {
                var mfs = view.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < mfs.Length; i++)
                    if (mfs[i] != null && mfs[i].sharedMesh != null && !UnderSlot(mfs[i].transform))
                        Accumulate(root, mfs[i].transform, mfs[i].sharedMesh.bounds, ref box, ref any);

                var sks = view.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < sks.Length; i++)
                    if (sks[i] != null && sks[i].sharedMesh != null && !UnderSlot(sks[i].transform))
                        Accumulate(root, sks[i].transform, sks[i].sharedMesh.bounds, ref box, ref any);
            }
            catch { }
            return any;
        }

        /// <summary>这个 transform 是不是挂在某个 StarshipItemSlot 底下。</summary>
        private static bool UnderSlot(Transform t)
        {
            try
            {
                for (var p = t; p != null; p = p.parent)
                    if (p.GetComponent(_tSlot) != null) return true;
            }
            catch { }
            return false;
        }

        /// <summary>把一个模型空间包围盒的 8 个角换算到 root 局部空间后并入 box。</summary>
        private static void Accumulate(Transform root, Transform owner, Bounds local, ref Bounds box, ref bool any)
        {
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = root.InverseTransformPoint(owner.TransformPoint(corner));
                if (!any) { box = new Bounds(p, Vector3.zero); any = true; }
                else box.Encapsulate(p);
            }
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
