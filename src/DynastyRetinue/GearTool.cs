using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items;
using Kingmaker.Blueprints.Items.Armors;
using Kingmaker.Blueprints.Items.Augments;
using Kingmaker.Blueprints.Items.Equipment;
using Kingmaker.Blueprints.Items.Shields;
using Kingmaker.Blueprints.Items.Weapons;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.Items.Slots;
using Kingmaker.UnitLogic.Progression.Features;

namespace DynastyRetinue
{
    /// <summary>
    /// 毕业装备发放。
    ///
    /// 设计取舍（用户拍板）：**凭空生成，不从玩家仓库拿**。
    /// 理由是卫兵不该跟玩家抢装备；代价是数值上偏强，所以用「只发顶阶」来平衡 ——
    /// 顶阶卫兵本来就受数量上限约束（Archetypes.GuardCountCap）。
    ///
    /// 存档安全：这里生成的全部是**原版物品蓝图**，AssetId 原本就存在，
    /// 卸载 mod 后 BlueprintConverter 照样解析得到，不触碰零新增 AssetId 那条红线。
    ///
    /// 装备走原版 PartUnitBody.TryInsertItem(bp, slot)（PartUnitBody.cs:496）：
    /// 它自己 CreateEntity、验槽位、验 CanBeEquippedBy，装不上就退回背包，不会抛。
    /// 植入物额外要一步 ApplyInsertion()，见原版 PartUnitBody.cs:350-358。
    /// </summary>
    public static class GearTool
    {
        /// <summary>
        /// 授予额外天赋（主要是熟练度）。必须在发装备**之前**跑 ——
        /// 没有动力甲/重武器/异形武器专精的话，对应装备会被
        /// CanBeEquippedBy 拒掉，而 ArmorSlot 还会把这个折进 IsItemSupported，
        /// 症状是看起来毫无道理的「槽位拒绝」。
        ///
        /// 只授予原版 BlueprintFeature，AssetId 本来就存在，不碰存档红线。
        /// </summary>
        public static int GrantFeatures(BaseUnitEntity g, ChainProbe.Archetype arch)
        {
            if (g == null || arch == null || arch.GrantFeatures == null) return 0;
            int n = 0;
            var added = new List<string>();
            foreach (var guid in arch.GrantFeatures)
            {
                if (string.IsNullOrEmpty(guid)) continue;
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(guid.Trim());
                    if (bp == null) continue;          // 未启用的 DLC，静默跳过
                    if (g.Facts.Contains(bp)) continue; // 幂等：已有就别再加，否则每次过图叠一层
                    g.Progression.Features.Add(bp);
                    n++; added.Add(bp.Name);
                }
                catch (Exception e) { Main.LogError("  授予天赋失败 " + guid + ": " + e.Message); }
            }
            if (n > 0) Main.Log("  授予天赋 " + n + " 个: " + string.Join(", ", added.ToArray()));
            return n;
        }

        /// <summary>上一次发装备的结果 —— 供「一键测装备」取用，免得去 parse 日志文本。</summary>
        public static int LastOk, LastAlready, LastMiss, LastFail;
        public static string LastNames = "", LastRejected = "";

        /// <summary>
        /// 给卫兵发装备。返回实际装上的件数。
        /// gear 由 GearFor 决定（精英 = 毕业套装，普通 = 玩家自配）。
        /// 解析不到的蓝图（未启用 DLC）静默跳过 —— 不该因为少个 DLC 就整套不发。
        /// </summary>
        public static int Equip(BaseUnitEntity g, ChainProbe.Archetype arch)
        {
            if (g == null || arch == null) return 0;
            var gear = GearFor(g, arch);
            if (gear == null || gear.Length == 0) return 0;
            var body = g.Body;
            if (body == null) { Main.LogError("  装备：卫兵没有 Body，跳过。"); return 0; }

            int ok = 0, miss = 0, fail = 0, already = 0;
            var names = new List<string>();
            var rejected = new List<string>();

            // 已经穿在身上的蓝图集合 —— 逐件比对用。
            // v0.3.5 的判据是「有任意一件就整套跳过」，结果精英用的 lv45 预设阿贝拉德
            // 自带装备里只要撞上一件，整套毕业装备就一件都不发了。改成逐件。
            var worn = WornGuids(body);
            // guid -> 中文名，事后核对用（见 using 块结束处）
            var placedGuids = new Dictionary<string, string>();
            var used = new HashSet<ItemSlot>();

            // ★★ 全程开 IgnoreLock ★★
            // 不开的话装备几乎必然失败，两道闸：
            //   ItemSlot.IsPossibleInsertItems():152-155
            //       TurnBasedModeActive && Owner.IsPlayerFaction -> false
            //   EquipmentSlot<T>.IsItemSupported():24-27
            //       Owner.IsInCombat && !IgnoreLock -> false
            // 卫兵是 PlayerFaction，所以只要处于回合制模式/战斗中就一件都装不上，
            // 症状是清一色的「槽位拒绝」—— v0.4.0 实测正是如此。
            // IgnoreLock 是原版自己留的后门（ItemSlot.cs:28 ContextFlag），
            // 两处判断都认它，RemoveItem 那边的 CanRemoveItem 同样认。
            using (ContextData<ItemSlot.IgnoreLock>.Request())
            {
            foreach (var entry in gear)
            {
                if (string.IsNullOrEmpty(entry)) continue;

                // 一格可以配多个候选（json 里写成嵌套数组），依次尝试到能装上为止。
                // 用途：① 五把狙击枪本来就是替代关系 ② DLC 限定装备做兜底
                //       ③ 异形装备装不上时退到普通装备
                var candidates = entry.Split('|');

                // 逐件幂等：这一格的任意候选已经穿着了就跳过。
                // ApplyRuntimeState 每次过图/读档都会跑，没这个判据植入物会一层层叠加。
                bool have = false;
                foreach (var c in candidates)
                    if (!string.IsNullOrEmpty(c) && worn.Contains(c.Trim())) { have = true; break; }
                if (have) { already++; continue; }

                bool placed = false;
                var tried = new List<string>();

                foreach (var guid in candidates)
                {
                    if (string.IsNullOrEmpty(guid)) continue;
                    BlueprintItem bp = null;
                    try { bp = ResourcesLibrary.TryGetBlueprint<BlueprintItem>(guid.Trim()); } catch { }
                    if (bp == null) { tried.Add("(解析不到 …" + Tail(guid) + ")"); continue; }

                    string why;
                    if (TryPlace(g, body, bp, ref ok, names, used, out why))
                    {
                        // 回退过程要记下来 —— 否则"最后用了哪件、前面为什么不行"全看不见
                        if (tried.Count > 0)
                            Main.Log("    候选回退 -> " + bp.Name + "  (先试过: " + string.Join("; ", tried.ToArray()) + ")");
                        placedGuids[guid.Trim()] = bp.Name;
                        placed = true;
                        break;
                    }
                    tried.Add(bp.Name + " ← " + why);
                }

                if (!placed)
                {
                    if (tried.Count > 0) { rejected.Add(string.Join("; ", tried.ToArray())); fail++; }
                    else miss++;
                }
            }
            }   // using IgnoreLock

            // ★ 事后核对：装上去的有没有又被挤掉 ★
            // 实测踩过一次：铁壁主手装了双手雷霆锤「崇高虔诚」，随后副手塞霰弹枪，
            // 游戏为腾位置把双手武器摘了 —— 但日志里那一格早已记成"装上"，
            // 于是日志说 9 件全好、游戏里主武器没了，白白误导了一轮排查。
            // 这里在全部发完之后回读一次实际穿戴，把"装上又没了"的单独报出来。
            try
            {
                var wornAfter = WornGuids(body);
                var lost = new List<string>();
                foreach (var kv in placedGuids)
                    if (!wornAfter.Contains(kv.Key)) lost.Add(kv.Value);
                if (lost.Count > 0)
                {
                    Main.LogError("  ⚠ 装上后又被挤掉 " + lost.Count + " 件: " + string.Join(", ", lost.ToArray())
                                  + "  —— 多半是双手武器与副手冲突，或同槽位后发的把先发的顶了。"
                                  + "请在 archetypes.json 里调整该格的候选顺序。");
                    ok -= lost.Count;
                    fail += lost.Count;
                    foreach (var l in lost) rejected.Add(l + " ← 装上后被后续装备挤掉");
                }
            }
            catch (Exception e) { Main.LogError("  装备事后核对失败: " + e.Message); }

            if (ok == 0 && already > 0 && fail == 0 && miss == 0) return 0;   // 全都已在身上，安静退出

            // 供一键测装备取用（跟 Archetypes.LastAudit 同一个套路：把上一次的结果留在静态字段里）
            LastOk = ok; LastAlready = already; LastMiss = miss; LastFail = fail;
            LastNames = string.Join(", ", names.ToArray());
            LastRejected = string.Join(" ; ", rejected.ToArray());

            Main.Log("  装备: 装上 " + ok + " 件"
                     + (already > 0 ? "，已在身上 " + already + " 件" : "")
                     + (miss > 0 ? "，蓝图全部解析不到 " + miss + " 格（多半是未启用的 DLC）" : "")
                     + (fail > 0 ? "，装不上 " + fail + " 格" : "")
                     + (names.Count > 0 ? "  [" + string.Join(", ", names.ToArray()) + "]" : ""));
            if (rejected.Count > 0)
                foreach (var r in rejected)
                    Main.Log("    装不上: " + r);
            return ok;
        }

        private static string Tail(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "?";
            return guid.Substring(Math.Max(0, guid.Length - 6));
        }

        /// <summary>
        /// 试着把一件装备装到卫兵身上。装上返回 true，否则 reason 里给出原因。
        ///
        /// ★ 必须先验能不能装，再摘旧装备 ★
        /// TryInsertItem 在 CanBeEquippedBy 失败时会把东西塞进背包然后 return
        /// （PartUnitBody.cs:509-514）。若先摘了旧的，结果就是
        /// 「新的装不上 + 旧的已经没了」= 槽位空着，比不发装备还糟。
        /// </summary>
        private static bool TryPlace(BaseUnitEntity g, PartUnitBody body, BlueprintItem bp,
                                     ref int ok, List<string> names, HashSet<ItemSlot> used,
                                     out string reason)
        {
            reason = null;
            try
            {
                var aug = bp as BlueprintItemAugment;
                if (aug != null)
                {
                    var aslot = body.Augments.GetOrCreateSlot(aug.AugmentSlot);
                    if (aslot == null) { reason = "拿不到植入位"; return false; }
                    if (used.Contains(aslot)) { reason = "该植入位本轮已用"; return false; }
                    // 该植入位被**别的**植入物占着：毕业套优先，摘掉换上
                    try { if (aslot.MaybeItem != null && aslot.IsPossibleRemoveItems()) aslot.RemoveItem(false); } catch { }
                    if (aslot.MaybeItem != null) { reason = "植入位被占且摘不掉"; return false; }
                    body.TryInsertItem(aug, aslot);
                    if (aslot.MaybeItem == null)
                    {
                        // ★ 这里之前的分支判断是错的 ★
                        // ItemSlot.CanInsertItem:159-177 本身就是
                        //     IsPossibleInsertItems() && IsItemSupported(item) && item.CanBeEquippedBy(Owner)
                        // 所以「资格不够」时 CanInsertItem 也是 false，旧代码却一律报
                        // "槽位拒绝(类型不匹配或植入系统被禁用)" —— 层级门被伪装成了类型不匹配。
                        // 全量审计就是被这条误导，得出"植入系统故障、改配置无效"的结论。
                        // 现在**逐道门单独探**，把三种失败彻底分开：
                        //     IsPossibleInsertItems  —— 槽位被锁（战斗中/回合制，IgnoreLock 没生效）
                        //     IsItemSupported        —— 植入物的 AugmentSlot 与本槽蓝图不匹配，
                        //                               或 body.Augments.Disabled（AugmentSlot.cs:66-73）
                        //     CanBeEquippedBy        —— 资格：种族排除、或 EquipmentRestrictionAugmentTier
                        //                               的**队伍全局剧情门** CurrentAvailableTier
                        bool canInsert = false, supported = false, unitOk = false, slotUnlocked = false;
                        try
                        {
                            var probe2 = aug.CreateEntity();
                            try { slotUnlocked = aslot.IsPossibleInsertItems(); } catch { }
                            try { supported = aslot.IsItemSupported(probe2); } catch { }
                            try { unitOk = probe2.CanBeEquippedBy(g); } catch { }
                            try { canInsert = aslot.CanInsertItem(probe2); } catch { }
                        }
                        catch { }

                        string tierInfo = "";
                        try
                        {
                            var pam = Kingmaker.Game.Instance != null && Kingmaker.Game.Instance.Player != null
                                    ? Kingmaker.Game.Instance.Player.PartyAugmentManager : null;
                            if (pam != null) tierInfo = "  队伍植入层级=" + pam.CurrentAvailableTier;
                        }
                        catch { }

                        bool augDisabled = false;
                        try { augDisabled = body.Augments.Disabled; } catch { }

                        if (!slotUnlocked)      reason = "植入位被锁(IsPossibleInsertItems=false，多半还在战斗/回合制)";
                        else if (augDisabled)   reason = "该单位的植入系统被禁用(UnitAugments.Disabled)";
                        else if (!supported)    reason = "槽位类型不匹配(这件植入物的 AugmentSlot 不是本槽)";
                        else if (!unitOk)       reason = "资格不够(CanBeEquippedBy 拒绝：种族排除 或 剧情层级未解锁)" + tierInfo;
                        else if (!canInsert)    reason = "CanInsertItem 拒绝但三道门单独都过了(未知)" + tierInfo;
                        else                    reason = "三道门都过了却没插进去(TryInsertItem 内部拒绝)" + tierInfo;
                        return false;
                    }
                    aslot.ApplyInsertion();
                    used.Add(aslot);
                    ok++; names.Add(bp.Name);
                    return true;
                }

                var slots = CandidateSlots(body, bp, used);
                var why = new List<string>();
                foreach (var slot in slots)
                {
                    ItemEntity probe = null;
                    try { probe = bp.CreateEntity(); } catch (Exception e) { why.Add("建实体失败:" + e.GetType().Name); continue; }
                    if (probe == null) { why.Add("建实体返回 null"); continue; }

                    bool slotOk = false, unitOk = false;
                    try { slotOk = slot.CanInsertItem(probe); } catch { }
                    // 单独测 —— ArmorSlot.IsItemSupported 会把种族限制折进槽位检查
                    // （ArmorSlot.cs:28-32），不分开测两种原因会混成一条
                    try { unitOk = probe.CanBeEquippedBy(g); } catch { }

                    if (!slotOk)
                    {
                        why.Add("[" + SlotName(body, slot) + "]"
                                + (unitOk ? "槽位不收" : "单位不够格(缺熟练度/种族限制?)"));
                        continue;
                    }
                    if (bp is BlueprintItemEquipment && !unitOk)
                    { why.Add("[" + SlotName(body, slot) + "]单位不够格(缺熟练度/种族限制?)"); continue; }

                    // 到这里才动旧装备
                    try { if (slot.MaybeItem != null && slot.IsPossibleRemoveItems()) slot.RemoveItem(false); } catch { }
                    body.TryInsertItem(bp, slot);
                    if (slot.MaybeItem == null || slot.MaybeItem.Blueprint != bp)
                    { why.Add("[" + SlotName(body, slot) + "]插入后不是它(被退回背包)"); continue; }

                    used.Add(slot);
                    ok++; names.Add(bp.Name + "@" + SlotName(body, slot));
                    return true;
                }

                reason = (why.Count > 0) ? string.Join(" / ", why.ToArray()) : "没有可用槽位";
                return false;
            }
            catch (Exception e)
            {
                reason = "异常:" + e.Message;
                return false;
            }
        }

        /// <summary>当前穿戴/装填在身上的全部蓝图 GUID（含植入物槽）。</summary>
        private static HashSet<string> WornGuids(PartUnitBody body)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var slot in body.AllSlots)
                {
                    if (slot == null) continue;
                    var it = slot.MaybeItem;
                    if (it == null || it.Blueprint == null) continue;
                    set.Add(it.Blueprint.AssetGuid.ToString());
                }
            }
            catch { }
            return set;
        }

        /// <summary>
        /// 这件装备可以尝试的槽位，按优先级排。TryPlace 会逐个试到成功为止。
        ///
        /// 武器要遍历**两个套组的全部 4 个手位**：PartUnitBody.cs:220 建的是
        /// HandsEquipmentSet[2]，而 PrimaryHand/SecondaryHand 只指向 CurrentHandsEquipmentSet。
        /// 双手武器占满一整组，所以两把双手武器只能一组放一把 —— 只看当前组必然失败。
        /// </summary>
        private static IEnumerable<ItemSlot> CandidateSlots(PartUnitBody body, BlueprintItem bp, HashSet<ItemSlot> used)
        {
            var list = new List<ItemSlot>();

            if (bp is BlueprintItemWeapon || bp is BlueprintItemShield)
            {
                var sets = body.HandsEquipmentSets;
                if (sets != null)
                {
                    // 先当前组的主手（毕业武器该当主武器），再当前组副手，再另一组
                    int cur = 0;
                    try { cur = body.CurrentHandEquipmentSetIndex; } catch { }
                    for (int k = 0; k < sets.Count; k++)
                    {
                        var set = sets[(cur + k) % sets.Count];
                        if (set == null) continue;
                        if (bp is BlueprintItemShield)
                        {
                            // 盾进副手，但主手是双手武器时这一组放不下
                            if (!MainIsTwoHanded(set)) Add(list, set.SecondaryHand, used);
                            continue;
                        }

                        // 双手武器只能进主手 —— 塞副手要么被拒、要么把主手顶掉
                        bool twoH = IsTwoHanded(bp);
                        Add(list, set.PrimaryHand, used);
                        if (!twoH && !MainIsTwoHanded(set)) Add(list, set.SecondaryHand, used);
                    }
                }
                return list;
            }

            if (bp is BlueprintItemEquipmentRing)
            {
                // 戒指两个槽对称，优先空的
                if (body.Ring1 != null && body.Ring1.MaybeItem == null) Add(list, body.Ring1, used);
                if (body.Ring2 != null && body.Ring2.MaybeItem == null) Add(list, body.Ring2, used);
                Add(list, body.Ring1, used);
                Add(list, body.Ring2, used);
                return list;
            }

            ItemSlot one = null;
            if (bp is BlueprintItemArmor)                    one = body.Armor;
            else if (bp is BlueprintItemEquipmentHead)       one = body.Head;
            else if (bp is BlueprintItemEquipmentGlasses)    one = body.Glasses;
            else if (bp is BlueprintItemEquipmentNeck)       one = body.Neck;
            else if (bp is BlueprintItemEquipmentGloves)     one = body.Gloves;
            else if (bp is BlueprintItemEquipmentFeet)       one = body.Feet;
            else if (bp is BlueprintItemEquipmentShoulders)  one = body.Shoulders;
            else if (bp is BlueprintItemEquipmentWrist)      one = body.Wrist;
            else if (bp is BlueprintItemEquipmentBelt)       one = body.Belt;
            else if (bp is BlueprintItemEquipmentShirt)      one = body.Shirt;
            else if (bp is BlueprintItemEquipmentPetProtocol)one = body.PetProtocol;
            Add(list, one, used);
            return list;
        }

        private static void Add(List<ItemSlot> l, ItemSlot s, HashSet<ItemSlot> used)
        {
            if (s == null || used.Contains(s) || l.Contains(s)) return;
            l.Add(s);
        }

        /// <summary>槽位的可读名字，诊断用。</summary>
        private static string SlotName(PartUnitBody body, ItemSlot s)
        {
            try
            {
                if (ReferenceEquals(s, body.Armor)) return "护甲";
                if (ReferenceEquals(s, body.Head)) return "头";
                if (ReferenceEquals(s, body.Neck)) return "项链";
                if (ReferenceEquals(s, body.Gloves)) return "手套";
                if (ReferenceEquals(s, body.Feet)) return "靴";
                if (ReferenceEquals(s, body.Shoulders)) return "披风";
                if (ReferenceEquals(s, body.Ring1)) return "戒指1";
                if (ReferenceEquals(s, body.Ring2)) return "戒指2";
                var sets = body.HandsEquipmentSets;
                if (sets != null)
                    for (int i = 0; i < sets.Count; i++)
                    {
                        if (sets[i] == null) continue;
                        if (ReferenceEquals(s, sets[i].PrimaryHand)) return "套组" + (i + 1) + "主手";
                        if (ReferenceEquals(s, sets[i].SecondaryHand)) return "套组" + (i + 1) + "副手";
                    }
            }
            catch { }
            return s != null ? s.GetType().Name : "?";
        }

        /// <summary>
        /// 这个卫兵对应哪个精英定义 —— 判据是「它由某个精英的 unit 蓝图生成」。
        /// 蓝图随实体持久化，不需要额外存标记，读档后照样认得出来。不是精英返回 null。
        /// </summary>
        public static ChainProbe.EliteDef EliteDefOf(BaseUnitEntity g, ChainProbe.Archetype arch)
        {
            if (g == null || arch == null || arch.Elites == null) return null;
            try
            {
                // ① 精英标记最优先（写在 CustomPetName 里，见 RetinueRegistry.SetEliteTag）。
                //    有了它，多个精英就能共用同一个单位蓝图 —— 蓝图不再是身份判据。
                int ta, te;
                RetinueRegistry.GetEliteTag(g, out ta, out te);
                if (te >= 0 && te < arch.Elites.Length) return arch.Elites[te];

                // ② 旧存档里没标记的卫兵，退回蓝图匹配
                var bp = g.OriginalBlueprint ?? g.Blueprint;
                if (bp == null) return null;
                string guid = bp.AssetGuid.ToString();
                foreach (var d in arch.Elites)
                    if (d != null && string.Equals(d.UnitId, guid, StringComparison.OrdinalIgnoreCase))
                        return d;
            }
            catch { }
            return null;
        }

        /// <summary>某个精英定义在它所属分型里的下标 —— 生成时要把它写进标记。</summary>
        public static int IndexOfElite(ChainProbe.Archetype arch, ChainProbe.EliteDef def)
        {
            if (arch == null || arch.Elites == null || def == null) return -1;
            for (int i = 0; i < arch.Elites.Length; i++)
                if (ReferenceEquals(arch.Elites[i], def)) return i;
            return -1;
        }

        public static bool IsElite(BaseUnitEntity g, ChainProbe.Archetype arch)
        {
            return EliteDefOf(g, arch) != null;
        }

        /// <summary>当前在册的、属于该分型的精英数量。</summary>
        public static int EliteCount(int archIndex)
        {
            var arch = Archetypes.Get(archIndex);
            if (arch == null || arch.Elites == null) return 0;
            int n = 0;
            foreach (var g in RetinueRegistry.All())
                if (RetinueRegistry.ArchetypeOf(g) == archIndex && IsElite(g, arch)) n++;
            return n;
        }

        /// <summary>
        /// 该分型是否已解锁精英。
        /// 规则：这条路线上得先有一个卫兵练到 T3 职业（链的第三段）——
        /// 精英是"这条路走到头"的奖励，不是开局就能买的。
        /// 面板可以取消这个限制。
        /// </summary>
        public static bool EliteUnlocked(int archIndex)
        {
            // 走方法而不是直接读字段 —— 面板上那个「全部解除」总开关要能管到这里
            if (Main.Settings != null && Main.Settings.NoEliteUnlockGate()) return true;
            var arch = Archetypes.Get(archIndex);
            if (arch == null || arch.Chain == null || arch.Chain.Length < 3) return false;
            string t3 = arch.Chain[2];
            foreach (var g in RetinueRegistry.All())
            {
                if (RetinueRegistry.ArchetypeOf(g) != archIndex) continue;
                try
                {
                    foreach (var cp in g.Progression.AllCareerPaths)
                    {
                        if (cp.Blueprint == null) continue;
                        if (string.Equals(cp.Blueprint.AssetGuid.ToString(), t3, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
            }
            return false;
        }

        /// <summary>
        /// 下一个还没生成的精英定义。全生成过了 / 没解锁 / 到上限，返回 null。
        /// 一个分型可以有多个精英 —— 比如近战的先锋路线和首席战士路线都用阿贝拉德，
        /// 两者单位蓝图不同、职业链不同、毕业装备也不同。
        /// </summary>
        public static ChainProbe.EliteDef NextElite(int archIndex)
        {
            var arch = Archetypes.Get(archIndex);
            if (arch == null || arch.Elites == null || arch.Elites.Length == 0) return null;
            if (Main.Settings == null) return null;
            if (!EliteUnlocked(archIndex)) return null;

            if (!Main.Settings.NoEliteCountCap())
            {
                int cap = Main.Settings.EliteLimitPerArchetype;
                if (cap < 0) cap = 1;
                // 上限理解为「每种精英各允许 cap 个」，默认 cap=1 即每种一个
                if (EliteCount(archIndex) >= cap * arch.Elites.Length) return null;
            }

            var have = new HashSet<int>();
            foreach (var g in RetinueRegistry.All())
            {
                if (RetinueRegistry.ArchetypeOf(g) != archIndex) continue;
                var d0 = EliteDefOf(g, arch);
                int idx = IndexOfElite(arch, d0);
                if (idx >= 0) have.Add(idx);
            }
            for (int i = 0; i < arch.Elites.Length; i++)
                if (arch.Elites[i] != null && !have.Contains(i)) return arch.Elites[i];   // 第一个还没生成的
            return null;
        }

        public static bool CanSpawnElite(int archIndex)
        {
            return NextElite(archIndex) != null;
        }

        /// <summary>
        /// 该给这个卫兵发哪套装备。
        ///   精英 -> 它自己那条精英定义里的 gear
        ///   普通 -> 模板的 playerGear（玩家在面板里自己装配的），没配就不发
        /// </summary>
        public static string[] GearFor(BaseUnitEntity g, ChainProbe.Archetype arch)
        {
            if (arch == null) return null;
            if (Main.Settings == null || !Main.Settings.EquipGraduationGear) return null;
            var d = EliteDefOf(g, arch);
            if (d != null) return d.Gear;

            // 普通卫兵按阶位发三套渐进装备。分档依据是物品 Rarity ——
            // 实测 items_zh.tsv 里 ItemLevel 有 2755/2940 是 0，用不了；
            // 而 Rarity 与护甲数值单调正相关（吸收中位 Common 40 / Pattern 45 / Unique 50）。
            // 玩家自己在面板装配过 playerGear 的话，那个优先 —— 手动配置压过默认。
            if (arch.PlayerGear != null && arch.PlayerGear.Length > 0) return arch.PlayerGear;

            int tier = 1;
            try
            {
                var leader = Kingmaker.Game.Instance != null && Kingmaker.Game.Instance.Player != null
                           ? Kingmaker.Game.Instance.Player.MainCharacterEntity : null;
                if (leader != null) tier = Archetypes.PlayerTier(leader);
            }
            catch { }

            // 面板上的档位覆盖（0=自动）。纯测试用途：PlayerTier 由玩家等级推出，
            // 55 级存档恒为 T3，不覆盖的话 T1/T2 两套装备一次都触发不到、没法验。
            try
            {
                if (Main.Settings != null && Main.Settings.GearTierOverride > 0)
                {
                    tier = Main.Settings.GearTierOverride;
                    Main.Log("  [装备] 档位被面板覆盖为 T" + tier + "（自动值 "
                             + Archetypes.PlayerTier(Kingmaker.Game.Instance.Player.MainCharacterEntity) + "）");
                }
            }
            catch { }

            // 降级取用：T3 没配就退 T2，再退 T1。配置不全也不会让卫兵裸奔。
            if (tier >= 3 && NotEmpty(arch.GearT3)) return arch.GearT3;
            if (tier >= 2 && NotEmpty(arch.GearT2)) return arch.GearT2;
            if (NotEmpty(arch.GearT1)) return arch.GearT1;
            if (NotEmpty(arch.GearT2)) return arch.GearT2;
            if (NotEmpty(arch.GearT3)) return arch.GearT3;
            return null;
        }

        private static bool NotEmpty(string[] a) { return a != null && a.Length > 0; }

        /// <summary>这件蓝图是不是双手武器。</summary>
        private static bool IsTwoHanded(BlueprintItem bp)
        {
            var w = bp as BlueprintItemWeapon;
            return w != null && w.IsTwoHanded;
        }

        /// <summary>
        /// 这一组的主手上放着双手武器吗？
        ///
        /// ★为什么要这个判断★ 实测：法杖/雷霆锤这类双手武器装进套装1主手后，
        /// 副武器被 CandidateSlots 顺位塞进**同一组的副手**，游戏为腾位置直接把双手武器摘掉 ——
        /// 日志却已经把它记成"装上"，是个静默失败（v0.14.1 的事后核对才把它抓出来）。
        /// 正确的构筑姿势是**副武器放套装 2 主手**，靠切换套组用，而不是占同组副手。
        /// 所以这里让本组副手在主手为双手武器时直接出局，候选自然落到下一组。
        /// </summary>
        private static bool MainIsTwoHanded(HandsEquipmentSet set)
        {
            try
            {
                if (set == null || set.PrimaryHand == null) return false;
                var it = set.PrimaryHand.MaybeItem;
                return it != null && IsTwoHanded(it.Blueprint);
            }
            catch { return false; }
        }
    }
}
