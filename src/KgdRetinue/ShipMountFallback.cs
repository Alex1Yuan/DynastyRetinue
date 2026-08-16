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

            // ---- 算船艏位置（L1 → L2 → L3）----
            Vector3 prowLocal; string how;
            float hullLenZ = 0f;
            Bounds bb; bool hasBounds = HullBoundsLocal(view, root, out bb);
            if (hasBounds) hullLenZ = bb.size.z;

            // 船体中线 X：用左右舷挂点反推（它们本来就骑在中线两侧）
            float cx = (pN > 0 && sN > 0) ? (pxSum / pN + sxSum / sN) * 0.5f : (hasBounds ? bb.center.x : 0f);

            // ★ 高度 Y：取**舷炮挂点里最低的那个** ★
            // 为什么不用包围盒底：那是**整艘船的全局最小值**，落在舯部龙骨上；
            // 而船艏那一段的底面比它高不少，把舰首炮摆到全局底部就掉到船体外面去了
            //（实测：炮悬在船腹下方的空中）。
            // 舷炮挂点是美术手工摆在船壳侧面的真实点，按构造一定贴着船体，
            // 取其中最低的一个 ⇒ 落在下层炮甲板那条线上，正是吊装式舰首炮该在的高度。
            //
            // 演进记录（每步都是实测反馈驱动的，别退回去）：
            //   v0.28.0 左右舷**平均**高度 + 只回收 4%  → 炮飘在撞角上方的虚空
            //   v0.28.1 船脊高度                        → 位置对了，但底座板翘在船体外
            //   v0.29.2 回收 12% → 18%                  → 前后贴上了
            //   v0.29.3 包围盒底 + 10%                  → 太低，炮掉到船腹下方的空中
            //   v0.29.6 舷炮最低点                      → 贴在下层炮甲板线上
            float cy;
            if (minBroadsideY.HasValue)     cy = minBroadsideY.Value;
            else if (dorsalLocalY.HasValue) cy = dorsalLocalY.Value;
            else                            cy = hasBounds ? bb.center.y : 0f;

            if (axisOk && hasBounds)
            {
                // 往回收 18%。这个数是实测调出来的，不是拍的：
                //   4%  → 炮飘在撞角上方的虚空里
                //   12% → 炮塔本体落对了，但底座那块板还探出船艏之外
                //   18% → 底座贴着船体
                // 之所以要收这么多，是因为武器美术的几何**从挂点往前长**（炮管朝 +Z），
                // 挂点在船艏边缘 = 整个炮座悬空。
                prowLocal = new Vector3(cx, cy, bb.max.z - bb.size.z * 0.18f);
                how = "L1 包围盒(" + bb.size.ToString("F1") + ") + 船脊高度";
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
            foreach (var want in WeaponSlotTypes)
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

            if (added > 0 && !_logged)
            {
                _logged = true;
                Main.Log("[挂点] 补上 " + added + " 个合成挂点: " + string.Join(" ", names.ToArray())
                         + "\n  定位: " + how + "　局部坐标 " + prowLocal.ToString("F2")
                         + "　父级=StarshipView　旋转已归零"
                         + (pct != 0 ? "　面板微调 " + pct + "%" : "")
                         + "\n  原理：武器美术挂不上去时开火点退回舰船原点（在虚空开火）。"
                         + "补上挂点后 vanilla 的 FindAll 就能命中；locator 的 weaponSlotType 是按"
                         + "**武器要求的**槽位盖章的（StarshipView:275），所以位置不影响技能侧匹配。"
                         + "\n  ★纯场景对象，不进存档★  本次会话只报这一条。");
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
