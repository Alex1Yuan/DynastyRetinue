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
        /// <summary>
        /// 授予分型的先天能力。tier &gt;= 1 时额外处理**按阶位替换**的那一组。
        ///
        /// ★为什么不用记账★
        ///   每次区域加载都会重算，判据全部来自配表 + 卫兵当前身上有什么，
        ///   不依赖"上次发了哪些"这种需要持久化的状态。好处有三：
        ///     · 不进存档，也就不可能和存档不一致
        ///     · 玩家中途改配表（或我们发新版改了配表），下次进区域自动收敛
        ///     · 读旧档导致阶位回退时，同样自动收敛回低阶那套
        ///
        /// ★为什么先撤后发★
        ///   同一个 guid 可能同时出现在 T1 和 T2 里（作者想让它跨阶保留）。
        ///   先算出 keep 再算 drop = drop 里天然不含 keep，那种 guid 不会被
        ///   "撤掉又发回来"地空转一遍，也就不会有一帧的能力闪断。
        /// </summary>
        public static int GrantFeatures(BaseUnitEntity g, ChainProbe.Archetype arch, int tier = 0)
        {
            if (g == null || arch == null) return 0;
            // 独立的装备调用也必须过滤；提前返回避免按阶位撤销老兵已有 Facts。
            if (GuardGrowth.RestrictBootstrap(g))
                return GuardGrowth.GrantGearProficiencies(g, arch, tier);

            // ---- 本次应当拥有的 = 常驻 ∪ 本阶位 ----
            var keep = new List<string>();
            if (arch.GrantFeatures != null) keep.AddRange(arch.GrantFeatures);

            string[][] tiers = arch.GrantFeaturesTier;
            if (tier >= 1 && tiers != null)
            {
                int idx = tier - 1;
                if (idx >= 0 && idx < tiers.Length && tiers[idx] != null) keep.AddRange(tiers[idx]);

                // ---- 该撤掉的 = 别的阶位声明过的 − keep ----
                for (int t = 0; t < tiers.Length; t++)
                {
                    if (t == idx || tiers[t] == null) continue;
                    foreach (var guid in tiers[t])
                    {
                        if (string.IsNullOrEmpty(guid)) continue;
                        string gid = guid.Trim();
                        if (keep.Contains(gid)) continue;      // 跨阶保留的，别动
                        try
                        {
                            var bp = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(gid);
                            if (bp == null) continue;
                            if (!g.Facts.Contains(bp)) continue;   // 本来就没有，不用撤
                            g.Progression.Features.Remove(bp);
                            // ★别直接用 bp.Name★ 有些特性在原版数据里**根本没有显示名** ——
                            //   比如 Augment_2Metallicization_BlueprintFeature（金属化2）：
                            //   蓝图只有 431 字节、一个本地化 key 都没有，因为它只是内部管线
                            //   （挂 Augments_Metallicization_Buff），玩家看到的名字在植入物和 buff 上。
                            //   直接打 bp.Name 会输出空白，日志上看起来像"解析失败"，
                            //   但 guid 是好的、功能也正常 —— 白查过一次。退到内部名至少能认出是谁。
                            Main.Log("  撤销上一阶位能力: "
                                     + (string.IsNullOrEmpty(bp.Name) ? bp.name : bp.Name));
                        }
                        catch (Exception e) { Main.LogError("  撤销天赋失败 " + gid + ": " + e.Message); }
                    }
                }
            }

            // ---- 发 keep 里还没有的 ----
            int n = 0;
            var added = new List<string>();
            foreach (var guid in keep)
            {
                if (string.IsNullOrEmpty(guid)) continue;
                try
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(guid.Trim());
                    if (bp == null) continue;          // 未启用的 DLC，静默跳过
                    if (g.Facts.Contains(bp)) continue; // 幂等：已有就别再加，否则每次过图叠一层
                    g.Progression.Features.Add(bp);
                    n++; added.Add(string.IsNullOrEmpty(bp.Name) ? bp.name : bp.Name);   // 同上：没显示名的退到内部名
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

            // 本轮真正插入的物品实例，事后逐件核对是否仍留在装备槽。
            // 不能按 GUID 去重：锈行猎手等配置明确要求两把同 GUID 武器。
            var placedItems = new List<KeyValuePair<ItemEntity, string>>();
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

                // 逐件幂等：**按顺序走到第一个"已经穿着"的候选就停**，不再往后发。
                //
                // ★为什么不是"任意候选已穿就整格跳过"★
                //   v1.0.88 之前是那样写的。赏金猎手换成裁判庭狙击手之后，
                //   它的单位**自带 DLC3_DL_Arbites_StandartArmour（45/6）**，而那件正好是
                //   这一格的第四候选 —— 于是整格被判为"已满足"，首选的厚重虚空护甲(85/7)
                //   一次都没发过。同线的寂静之眼候选列表**一模一样**却拿到了 85/7，
                //   唯一差别就是它的单位不自带那件。
                //
                // ★为什么也不能"只看首选"★
                //   那样的话，首选装不上（比如磐石首席的雕血师是种族限定）时，
                //   每次过图都会把已经穿着的那件摘下来重装一遍，白白折腾。
                //
                //   按顺序走：排在已穿着那件**前面**的才尝试 —— 那才是真正的升级；
                //   走到已穿着的那件就说明没有更好的了，停手。
                // ★每个条目都重新读当前穿戴，并消费一个真实槽位★
                //   不能用 GUID HashSet：配置里允许同一 GUID 出现两次（双持同款武器），
                //   一把不能同时满足两条。也不能沿用 Equip 开头的旧快照：前一条可能刚
                //   把法杖装回套组1，后一条必须看见这个新状态。
                int stopAt = candidates.Length;
                string wornGuid = null;
                ItemSlot wornSlot = null;
                for (int ci = 0; ci < candidates.Length; ci++)
                {
                    string c = candidates[ci];
                    if (string.IsNullOrEmpty(c)) continue;
                    ItemSlot slot = FindWornSlot(body, c.Trim(), used);
                    if (slot != null)
                    {
                        stopAt = ci;
                        wornGuid = c.Trim();
                        wornSlot = slot;
                        break;
                    }
                }
                if (stopAt == 0)
                {
                    used.Add(wornSlot);
                    already++;
                    continue;
                }

                bool placed = false;
                var tried = new List<string>();

                for (int ci = 0; ci < stopAt; ci++)
                {
                    string guid = candidates[ci];
                    if (string.IsNullOrEmpty(guid)) continue;
                    BlueprintItem bp = null;
                    try { bp = ResourcesLibrary.TryGetBlueprint<BlueprintItem>(guid.Trim()); } catch { }
                    if (bp == null) { tried.Add("(解析不到 …" + Tail(guid) + ")"); continue; }

                    string why;
                    ItemEntity placedItem;
                    if (TryPlace(g, body, bp, ref ok, names, used, out why, out placedItem))
                    {
                        // 回退过程要记下来 —— 否则"最后用了哪件、前面为什么不行"全看不见
                        if (tried.Count > 0)
                            Main.Log("    候选回退 -> " + bp.Name + "  (先试过: " + string.Join("; ", tried.ToArray()) + ")");
                        if (placedItem != null)
                            placedItems.Add(new KeyValuePair<ItemEntity, string>(placedItem, bp.Name));
                        placed = true;
                        break;
                    }
                    tried.Add(bp.Name + " ← " + why);
                }

                if (!placed)
                {
                    // 前面的升级候选都失败时，当前穿着的后备候选仍是这条配表的有效结果；
                    // 把这个真实槽位保留下来，属于 already，不应报“装不上”制造假红。
                    if (wornSlot != null)
                    {
                        used.Add(wornSlot);
                        already++;
                        if (tried.Count > 0)
                            Main.Log("    保留当前后备 " + wornGuid + "（升级候选失败: "
                                   + string.Join("; ", tried.ToArray()) + ")");
                    }
                    else if (tried.Count > 0) { rejected.Add(string.Join("; ", tried.ToArray())); fail++; }
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
                var lost = new List<string>();
                foreach (var kv in placedItems)
                    if (kv.Key == null || kv.Key.HoldingSlot == null) lost.Add(kv.Value);
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
                                     out string reason, out ItemEntity placedItem)
        {
            reason = null;
            placedItem = null;
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
                    placedItem = aslot.MaybeItem;
                    ok++; names.Add(bp.Name);
                    return true;
                }

                var slots = CandidateSlots(body, bp, used);
                var why = new List<string>();
                // 修复既有卫兵时，插回双手主武器会把冲突副手退回它自己的背包。
                // 优先复用那件原物品到下一套组，找不到才创建，避免每次修复多复制一把武器。
                ItemEntity reusable = FindLooseInventoryItem(g, bp);
                foreach (var slot in slots)
                {
                    ItemEntity probe = reusable;
                    if (probe == null)
                    {
                        try { probe = bp.CreateEntity(); }
                        catch (Exception e) { why.Add("建实体失败:" + e.GetType().Name); continue; }
                    }
                    if (probe == null) { why.Add("建实体返回 null"); continue; }

                    bool slotOk = false, unitOk = false;
                    try { slotOk = slot.CanInsertItem(probe); } catch { }
                    // 单独测 —— ArmorSlot.IsItemSupported 会把种族限制折进槽位检查
                    // （ArmorSlot.cs:28-32），不分开测两种原因会混成一条
                    try { unitOk = probe.CanBeEquippedBy(g); } catch { }

                    if (!slotOk)
                    {
                        // ★把 CanInsertItem 拆开报★ 它内部是两道检查合并的，
                        //   只报「槽位不收」分不清是「这个槽现在不让插」还是「这个槽不认这类物品」，
                        //   而这两者的修法完全不同（前者找锁、后者换物品/开槽位）。
                        //   实机卡在这里：贤者精英的等离子步枪两个手部套组都不收，
                        //   而同样的步枪在 T3 普通卫兵身上装得上 —— 差别必须精确到这一层。
                        string sub = "";
                        if (unitOk)
                        {
                            bool canInsert = false, supported = false;
                            try { canInsert = slot.IsPossibleInsertItems(); } catch { }
                            try { supported = slot.IsItemSupported(probe); } catch { }
                            sub = canInsert
                                ? (supported ? "槽位不收（两道都过却仍拒，原因未知）"
                                             : "槽位不收：IsItemSupported=false（这个槽不认这类物品）")
                                : "槽位不收：IsPossibleInsertItems=false（这个槽当前不让插）";
                        }
                        why.Add("[" + SlotName(body, slot) + "]"
                                + (unitOk ? sub : "单位不够格 —— " + WhyNotEquippable(bp, g)));
                        continue;
                    }
                    if (bp is BlueprintItemEquipment && !unitOk)
                    { why.Add("[" + SlotName(body, slot) + "]单位不够格 —— " + WhyNotEquippable(bp, g)); continue; }

                    // 到这里才动旧装备
                    try { if (slot.MaybeItem != null && slot.IsPossibleRemoveItems()) slot.RemoveItem(false); } catch { }
                    if (reusable != null) slot.InsertItem(reusable);
                    else body.TryInsertItem(bp, slot);
                    if (slot.MaybeItem == null || slot.MaybeItem.Blueprint != bp)
                    { why.Add("[" + SlotName(body, slot) + "]插入后不是它(被退回背包)"); continue; }

                    used.Add(slot);
                    placedItem = slot.MaybeItem;
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

        /// <summary>
        /// 「单位不够格」到底卡在哪一条。
        ///
        /// ★为什么值得单独查★
        ///   CanBeEquippedBy 把一族限制裹成一个 bool，原来日志只能写
        ///   「缺熟练度/种族限制?」—— 那个问号是猜的。而实际可能是属性门槛、
        ///   需要某个天赋、职业限定、只有主角能穿、甚至干脆标了不可装备。
        ///   这几种的应对完全不同（补属性 / 补天赋 / 换件），猜错就白改。
        ///   逐条测一遍，把**失败的那条组件名**打出来，一眼定位。
        /// </summary>
        private static string WhyNotEquippable(BlueprintItem bp, BaseUnitEntity g)
        {
            try
            {
                var eq = bp as Kingmaker.Blueprints.Items.Equipment.BlueprintItemEquipment;
                if (eq == null) return "非装备类";
                // GetComponents 返回的是结构体枚举器，不是引用类型，不能和 null 比
                var comps = bp.GetComponents<Kingmaker.Blueprints.Items.Components.EquipmentRestriction>();
                var bad = new List<string>();
                int n = 0;
                foreach (var c in comps)
                {
                    n++;
                    bool ok = true;
                    try { ok = c.CanBeEquippedBy(g); } catch (Exception e) { bad.Add(c.GetType().Name + "(测试抛异常:" + e.GetType().Name + ")"); continue; }
                    if (!ok) bad.Add(c.GetType().Name.Replace("EquipmentRestriction", ""));
                }
                if (n == 0) return "没有限制组件（那问题不在限制上）";
                return bad.Count == 0 ? "限制全过（卡在别处）" : "卡在: " + string.Join(", ", bad.ToArray());
            }
            catch (Exception e) { return "查限制失败:" + e.GetType().Name; }
        }

        /// <summary>找卫兵自己背包里尚未装备的同蓝图物品；只在发装备冷路径使用。</summary>
        private static ItemEntity FindLooseInventoryItem(BaseUnitEntity unit, BlueprintItem bp)
        {
            if (unit == null || bp == null || unit.Inventory == null || unit.Inventory.Collection == null) return null;
            try
            {
                foreach (var item in unit.Inventory.Collection.Items)
                    if (item != null && item.Blueprint == bp && item.HoldingSlot == null) return item;
            }
            catch { }
            return null;
        }

        /// <summary>找一件尚未被本轮其他配表条目消费的实际穿戴槽。</summary>
        private static ItemSlot FindWornSlot(PartUnitBody body, string guid, HashSet<ItemSlot> used)
        {
            if (body == null || string.IsNullOrEmpty(guid)) return null;
            try
            {
                foreach (var slot in body.AllSlots)
                {
                    if (slot == null || (used != null && used.Contains(slot))) continue;
                    var item = slot.MaybeItem;
                    if (item == null || item.Blueprint == null) continue;
                    if (string.Equals(item.Blueprint.AssetGuid.ToString(), guid,
                                      StringComparison.OrdinalIgnoreCase)) return slot;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 这件装备可以尝试的槽位，按优先级排。TryPlace 会逐个试到成功为止。
        ///
        /// 武器要遍历**两个套组的全部 4 个手位**：PartUnitBody.cs:220 建的是
        /// HandsEquipmentSet[2]，而 PrimaryHand/SecondaryHand 只指向 CurrentHandsEquipmentSet。
        /// 双手武器占满一整组，所以两把双手武器只能一组放一把 —— 只看当前组必然失败。
        /// </summary>
        /// <summary>
        /// 这名单位有没有装着**弹道**机械触须 —— 双手武器能否进副手就看它。
        ///
        /// ★为什么按名字判而不是列 guid★ 和 GearLookPatch.IsMechadendrite 同一个理由：
        ///   触须物品有 6 种以上，还有专属变体；列 guid 早晚漏一个，而漏了的表现是
        ///   「这个贤者的副手武器莫名其妙装不上」—— 又一个不报错的静默失败。
        /// ★为什么用反射★ PartUnitBody.Mechadendrites 是**字段**不是属性
        ///   （tools\dump_type.ps1 实测：FIELDS 里 `List`1 Mechadendrites`，
        ///   PROPERTIES 里没有），而且元素类型在 Code.dll 里没有可强类型引用的
        ///   MechadendriteSlot。反射一次拿 FieldInfo 缓存住，之后只是取值 + 遍历三五个槽位。
        /// ★取不到时返回 false★ 保守侧：双手武器只列主手，不会顶掉已装好的武器。
        ///   宁可贤者少一把副手枪，也不能让别人的毕业武器被静默挤飞。
        /// ★频率★ 只在发装备时按件调用，不是每帧路径。
        /// </summary>
        private static System.Reflection.FieldInfo _mechField;
        private static bool _mechLooked;

        internal static bool HasBallisticMechadendrite(PartUnitBody body)
        {
            try
            {
                if (body == null) return false;
                if (!_mechLooked)
                {
                    _mechLooked = true;
                    _mechField = typeof(PartUnitBody).GetField("Mechadendrites",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                      | System.Reflection.BindingFlags.Instance);
                    if (_mechField == null)
                        Main.Log("[装备] 取不到 PartUnitBody.Mechadendrites 字段 —— "
                               + "双手武器一律只列主手（保守侧，不会顶掉已装的武器）。");
                }
                if (_mechField == null) return false;
                var en = _mechField.GetValue(body) as System.Collections.IEnumerable;
                if (en == null) return false;
                foreach (var slot in en)
                {
                    if (slot == null) continue;
                    object item = null;
                    try
                    {
                        var pi = slot.GetType().GetProperty("MaybeItem");
                        if (pi != null) item = pi.GetValue(slot, null);
                    }
                    catch { }
                    var ie = item as ItemEntity;
                    string n = ie != null && ie.Blueprint != null ? ie.Blueprint.name : null;
                    if (!string.IsNullOrEmpty(n)
                        && n.IndexOf("Mechadendrite", StringComparison.OrdinalIgnoreCase) >= 0
                        && n.IndexOf("Ballistic", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

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

                    // ★双手武器能不能进副手，取决于这名单位有没有弹道机械触须★
                    //
                    //   1.5.67 把副手也列进了双手武器的候选，理由写在下面那段注释里 ——
                    //   弹道机械触须**就是**让副手能拿双手远程武器的那个东西，机械教贤者
                    //   的「主手近战 + 副手双手远程」全靠它。那次改动对贤者是对的。
                    //
                    //   ★但它漏了一个前提★ 那条例外是**触须**开的，不是所有单位都有。
                    //   圣焰净罪（战斗修女）没有触须，却继承了这条例外，于是：
                    //       候选顺序 = 套组1主手 → 套组1副手 → 套组2主手 → 套组2副手
                    //       血腥礼赞（双手）占了套组1主手
                    //       高级重型伐木枪（双手）跳过已占的主手，落到**套组1副手**
                    //       —— 双手武器进副手，引擎把整组占掉，主手的血腥礼赞被顶飞
                    //   实测三次生成三次复现：最终两把伐木枪、礼赞消失，日志报
                    //   「装上后又被挤掉 1 件: 血腥礼赞」。而只有一把双手武器的怒火枪长完全正常。
                    //
                    //   ★为什么不能改成「所有主手排前面」★ 那样贤者的双手等离子会被赶去
                    //   套组2，主手近战+副手远程就不在同一组了，得切套组才能用 ——
                    //   等于把 1.5.67 解决的问题又造回来。实测贤者确实在吃这条
                    //   （总账里的「死亡低语——副手武器」「生机断绝-副手武器」）。
                    //   所以判别必须落在**触须**上，回到那条例外本来的因果。
                    bool twoH = false;
                    try { var _w = bp as BlueprintItemWeapon; twoH = _w != null && _w.IsTwoHanded; } catch { }
                    bool ballisticMechadendrite = HasBallisticMechadendrite(body);
                    bool offhandOk = !twoH || ballisticMechadendrite;
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

                        // ★不再预判「双手武器只能进主手」★
                        //
                        //   原来这里写死 `if (!twoH && !MainIsTwoHanded(set))` 才给副手，
                        //   注释理由是「塞副手要么被拒、要么把主手顶掉」。那是我们的推断，
                        //   不是游戏规则 —— **弹道机械触须的作用正是让副手能拿双手远程武器**。
                        //   机械教贤者就卡在这儿：主手拿雷锤（双手），副手因此从未进入候选，
                        //   日志里只看得到「[套组1主手]/[套组2主手] 槽位不收」，
                        //   而真正该用的套组1副手一次都没被试过。
                        //
                        //   现在两个手位都列为候选，由引擎的 CanInsertItem / IsItemSupported
                        //   决定收不收 —— 它才是权威，而且它认识触须开出来的例外。
                        //   顺序仍是「先主手后副手」，所以先发的武器照样占主手，
                        //   后发的才落副手，落位语义不变。
                        //   被拒时 1.5.60 那套诊断会说清是哪道检查挡的，不会静默失败。
                        Add(list, set.PrimaryHand, used);
                        // 普通单位：待装武器是双手，或本组主手已经是双手，副手都必须跳过；
                        // 后发武器自然落到下一套组主手。弹道机械触须是唯一例外。
                        if (offhandOk && (ballisticMechadendrite || !MainIsTwoHanded(set)))
                            Add(list, set.SecondaryHand, used);
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
                // 精英标记是**唯一**判据（写在 CustomPetName 里，见 RetinueRegistry.SetEliteTag）。
                // 有了它，多个精英就能共用同一个单位蓝图 —— 蓝图不是身份判据。
                int ta, te;
                RetinueRegistry.GetEliteTag(g, out ta, out te);
                if (te >= 0 && te < arch.Elites.Length) return arch.Elites[te];

                // ★这里原来还有第②步「没标记就退回蓝图匹配」，已经删掉★
                //
                //   它本来是给 v0.83.0（引入标记那一版）之前的存档兜底的。
                //   而 1.0.0 才是首个公开版本，标记比它更早 ——
                //   对任何真实玩家来说这段都是死代码。
                //
                //   但它会主动造成误判：换精英蓝图时，只要新 UnitId 恰好是
                //   **老存档里普通卫兵用过的蓝图**，那些普通卫兵就会被认成精英。
                //   实际踩到的一次：亚空间审判者换成 DLC3_DL_Inquisitor_Unit(700)，
                //   而那个蓝图正是老版本灵能普通兵用的 —— 于是存量的普通灵能兵
                //   平白拿到倒地豁免、占掉精英名额、改名时套上精英军衔。
                //
                //   身份判据必须是显式标记，不能是"长得像"。蓝图会换，标记不会。
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
            return EliteCount(archIndex, null);
        }

        internal static int EliteCount(int archIndex, System.Collections.Generic.List<BaseUnitEntity> roster)
        {
            return EliteCount(archIndex, null, roster);
        }

        internal static int EliteCount(int archIndex, string recruitGroup,
                                       System.Collections.Generic.List<BaseUnitEntity> roster)
        {
            var arch = Archetypes.Get(archIndex);
            if (arch == null || arch.Elites == null) return 0;
            if (roster == null) roster = RetinueRegistry.All();
            int n = 0;
            foreach (var g in roster)
            {
                if (RetinueRegistry.ArchetypeOf(g) != archIndex) continue;
                var d = EliteDefOf(g, arch);
                if (d == null) continue;
                if (recruitGroup != null && !SameRecruitGroup(d.RecruitGroup, recruitGroup)) continue;
                n++;
            }
            return n;
        }

        internal static bool SameRecruitGroup(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal static int RecruitGroupDefinitionCount(ChainProbe.Archetype arch, string recruitGroup)
        {
            if (arch == null || arch.Elites == null) return 0;
            int n = 0;
            for (int i = 0; i < arch.Elites.Length; i++)
            {
                var e = arch.Elites[i];
                if (e != null && (recruitGroup == null || SameRecruitGroup(e.RecruitGroup, recruitGroup))) n++;
            }
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
            return EliteUnlocked(archIndex, null);
        }

        internal static bool EliteUnlocked(int archIndex, System.Collections.Generic.List<BaseUnitEntity> roster)
        {
            // 走方法而不是直接读字段 —— 面板上那个「全部解除」总开关要能管到这里
            if (Main.Settings != null && Main.Settings.NoEliteUnlockGate()) return true;
            var arch = Archetypes.Get(archIndex);
            if (arch == null || arch.Chain == null || arch.Chain.Length < 3) return false;
            if (roster == null) roster = RetinueRegistry.All();
            string t3 = arch.Chain[2];
            foreach (var g in roster)
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
            return NextElite(archIndex, null, null);
        }

        internal static ChainProbe.EliteDef NextElite(
            int archIndex, System.Collections.Generic.List<BaseUnitEntity> roster)
        {
            return NextElite(archIndex, null, roster);
        }

        internal static ChainProbe.EliteDef NextElite(
            int archIndex, string recruitGroup, System.Collections.Generic.List<BaseUnitEntity> roster)
        {
            var arch = Archetypes.Get(archIndex);
            if (arch == null || arch.Elites == null || arch.Elites.Length == 0) return null;
            if (Main.Settings == null) return null;
            if (roster == null) roster = RetinueRegistry.All();
            if (!EliteUnlocked(archIndex, roster)) return null;

            int defs = RecruitGroupDefinitionCount(arch, recruitGroup);
            if (defs == 0) return null;
            if (!Main.Settings.NoEliteCountCap())
            {
                int cap = Main.Settings.EliteLimitPerArchetype;
                if (cap < 0) cap = 1;
                // group=null 保持全分型旧公式；有 group 时两条路线各算各的容量。
                if (EliteCount(archIndex, recruitGroup, roster) >= cap * defs) return null;
            }

            var have = new HashSet<int>();
            foreach (var g in roster)
            {
                if (RetinueRegistry.ArchetypeOf(g) != archIndex) continue;
                var d0 = EliteDefOf(g, arch);
                int idx = IndexOfElite(arch, d0);
                if (idx >= 0) have.Add(idx);
            }
            for (int i = 0; i < arch.Elites.Length; i++)
            {
                var e = arch.Elites[i];
                if (e == null || have.Contains(i)) continue;
                if (recruitGroup != null && !SameRecruitGroup(e.RecruitGroup, recruitGroup)) continue;
                if (WeaponGate.IsRecruitBlocked(e.UnitId)) continue;
                return e;      // 原数组顺序；教条自然 0→2，异端自然 1→3
            }
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
