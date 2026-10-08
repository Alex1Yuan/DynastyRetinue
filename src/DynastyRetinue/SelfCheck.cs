using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Enums;
using Warhammer.SpaceCombat.Blueprints;
using Warhammer.SpaceCombat.Blueprints.Slots;

namespace DynastyRetinue
{
    /// <summary>
    /// 自检：读档后自动跑一遍**不需要任何点击**的检查，把结论写进日志。
    ///
    /// ================= 为什么要有 =================
    /// 这个 mod 的验证一直靠"玩家去面板点按钮 → 我读日志"。但很多检查本质上
    /// 只是"某个 GUID 解析得到吗""某个文件在吗""某个不变式成立吗"，
    /// 完全不需要人参与 —— 让人去点，纯属把机器该干的活推给人，
    /// 而且远程/无鼠标的时候直接卡死。
    ///
    /// 所以：读档后自动跑，一次，结果打成一个 ✓/✗ 块。
    ///
    /// ★门槛★ 只在 mod 目录下有 dynasty_selftest.flag 时跑。玩家不该看到这些噪音，
    /// 而且里面会遍历几百个蓝图，虽然只有一次，也没必要让所有人付这个成本。
    ///
    /// ★边界★ 这里**只做只读检查**：解析蓝图、读文件、算不变式。
    /// 不生成卫兵、不改存档、不动任何游戏状态 —— 自检本身绝不能成为 bug 来源。
    /// 需要真的生成单位的那些（装备能不能装上、加点命不命中）由
    /// 【一键测装备】负责，那个会造实体，必须由人显式触发。
    /// </summary>
    public static class SelfCheck
    {
        private static bool _ran;

        public static bool Armed
        {
            get
            {
                try
                {
                    return Main.ModEntry != null
                        && File.Exists(Path.Combine(Main.ModEntry.Path, "dynasty_selftest.flag"));
                }
                catch { return false; }
            }
        }

        /// <summary>手动触发，无视 flag 和"一次会话只跑一遍"。给【一键全测】用。</summary>
        public static void ForceRun()
        {
            _ran = true;
            try { Run(); }
            catch (Exception e) { Main.LogError("[自检] 自身崩了（这本身就是一条失败）: " + e); }
        }

        /// <summary>读档 / 进区域后调一次。幂等 —— 一次会话只跑一遍。</summary>
        public static void RunOnce()
        {
            if (_ran || !Armed) return;
            _ran = true;
            try { Run(); }
            catch (Exception e) { Main.LogError("[自检] 自身崩了（这本身就是一条失败）: " + e); }
        }

        private static int _pass, _fail, _warn;

        private static void Ok(string what, string detail)
        { _pass++; Main.Log("  ✓ " + what.PadRight(28) + detail); }
        private static void Bad(string what, string detail)
        { _fail++; Main.LogError("  ✗ " + what.PadRight(28) + detail); }
        private static void Warn(string what, string detail)
        { _warn++; Main.Log("  ! " + what.PadRight(28) + detail); }

        private static void Run()
        {
            _pass = _fail = _warn = 0;
            Main.Log("======== 自检开始（只读，不生成任何单位、不改存档）========");

            Files();
            Archetypes_();
            GearGuids();
            GearCoverage();
            SettingsSanity();
            SpaceEscortBlueprint();
            FleetBudget_();
            SpaceFleetRefits();
            SpaceFleetComponents();
            SpaceFleetSaveSnapshots();
            ShipState();
            Report();

            Main.Log("======== 自检结束：通过 " + _pass + "　失败 " + _fail + "　提醒 " + _warn
                   + (_fail == 0 ? "　<全部通过>" : "　★有失败项，见上面的 ✗★") + " ========");
        }

        // ---------------------------------------------------------------- 文件

        private static void Files()
        {
            string dir = Main.ModEntry != null ? Main.ModEntry.Path : null;
            if (string.IsNullOrEmpty(dir)) { Bad("mod 目录", "拿不到 ModEntry.Path"); return; }

            // 运行时必需的两个数据文件。缺了 mod 名存实亡，而且失败方式很隐蔽
            // （回退内置默认，功能大幅退化但不报错）—— 所以这里显式查。
            foreach (var f in new[] { "archetypes.json", "plans.json" })
            {
                string p = Path.Combine(dir, f);
                if (File.Exists(p)) Ok("数据文件 " + f, new FileInfo(p).Length / 1024 + " KB");
                else Bad("数据文件 " + f, "缺失！mod 会退回内置默认，精英/装备表/人名池全没有");
            }

            // 发布包里不该有的东西，出现了说明打包路径错了
            foreach (var f in new[] { "Settings.xml", "dynasty_log.txt" })
                if (File.Exists(Path.Combine(dir, f)))
                    Ok("本机文件 " + f, "在（正常，发布包不含它）");

            Ok("开发区", Main.DevMode ? "可见（有 dynasty_dev.flag）" : "已隐藏（发布版形态）");
        }

        // ---------------------------------------------------------------- 分型

        private static void Archetypes_()
        {
            try
            {
                var all = Archetypes.All;
                if (all == null || all.Length == 0) { Bad("分型模板", "一个都没有"); return; }

                // ★这条最关键★ 回退到内置默认时也是"有分型"，但只有 4 条且没有精英。
                // 只查数量会漏掉那种情况，所以顺带查精英总数。
                int elites = 0;
                foreach (var a in all) if (a.Elites != null) elites += a.Elites.Length;

                var names = new List<string>();
                foreach (var a in all) names.Add(a.Name);
                string s = all.Length + " 条（" + string.Join(" / ", names.ToArray()) + "），精英 " + elites + " 名";

                if (all.Length >= 5 && elites >= 10) Ok("分型模板", s);
                else Bad("分型模板", s + "　←★多半是回退到了内置默认："
                                       + "archetypes.json 没读到，或读的时候蓝图缓存还没就绪★");

                if (Archetypes.GuardNamePool == null || Archetypes.GuardNamePool.Length < 20)
                    Warn("人名池", "只有 " + (Archetypes.GuardNamePool == null ? 0 : Archetypes.GuardNamePool.Length)
                               + " 个，卫兵会重名或退回编号");
                else Ok("人名池", Archetypes.GuardNamePool.Length + " 个");

                // ★机械教下标是存档/联机协议★ kgd.e:arch:elite 写的是原始下标，
                // 不能为了 UI 分组重排数组。逐项验证，任何一个漂移都必须红灯。
                ChainProbe.Archetype mech = null;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null && all[i].Name != null
                        && all[i].Name.IndexOf("Mechanicus", StringComparison.OrdinalIgnoreCase) >= 0)
                    { mech = all[i]; break; }
                string[] ids = {
                    "aa02b505be774674ae924f19dc17e6f6",
                    "ab131771270542b69fb7a687062b39c0",
                    "ca936a024b954b188d2bd397e6ea49d3",
                    "287d7a4d2bb146998dc450cb3eccee78"
                };
                string[] groups = { "dogmatic", "heretek", "dogmatic", "heretek" };
                bool mapOk = mech != null && mech.Elites != null && mech.Elites.Length >= 4;
                if (mapOk)
                    for (int i = 0; i < 4; i++)
                        mapOk &= mech.Elites[i] != null
                              && string.Equals(mech.Elites[i].UnitId, ids[i], StringComparison.OrdinalIgnoreCase)
                              && GearTool.SameRecruitGroup(mech.Elites[i].RecruitGroup, groups[i]);
                if (mapOk) Ok("机械教招募分组", "原下标 0教条 / 1异端 / 2教条 / 3异端，存档映射未漂移");
                else Bad("机械教招募分组", "配置缺失或 elites 顺序被改过；会破坏 kgd.e 存档身份与联机 eliteIndex");
            }
            catch (Exception e) { Bad("分型模板", "异常: " + e.Message); }
        }

        // ---------------------------------------------------------------- 装备 GUID

        /// <summary>
        /// 把所有装备候选链里的 GUID 逐个解析。
        /// ★只查"解析得到吗"，不查"装得上吗"★ —— 后者要真的造一个单位来试，
        /// 那是【一键测装备】的活。这里能抓的是"GUID 打错了 / DLC 没装"，
        /// 而那恰恰是最常见、也最容易被回退逻辑掩盖的一类错误。
        /// </summary>
        private static void GearGuids()
        {
            try
            {
                int total = 0, miss = 0;
                var missing = new List<string>();
                var all = Archetypes.All;
                foreach (var a in all)
                {
                    foreach (var chain in AllGearChains(a))
                        foreach (var g in chain.Split('|'))
                        {
                            if (string.IsNullOrEmpty(g)) continue;
                            total++;
                            object bp = null;
                            // 用非泛型重载 + 字符串：泛型那个要指定具体蓝图类型，
                            // 而这里的候选链混着武器/护甲/植入物，一个类型套不住。
                            try { bp = ResourcesLibrary.TryGetBlueprint(g); }
                            catch { }
                            if (bp == null) { miss++; if (missing.Count < 12) missing.Add(a.Name + " " + g); }
                        }
                }
                if (total == 0) { Warn("装备 GUID", "一条都没扫到（分型可能已回退默认）"); return; }
                if (miss == 0) Ok("装备 GUID", total + " 个全部解析成功");
                else Warn("装备 GUID", total + " 个里 " + miss + " 个解析不到（多半是未启用的 DLC）：\n      "
                                     + string.Join("\n      ", missing.ToArray()));
            }
            catch (Exception e) { Bad("装备 GUID", "异常: " + e.Message); }
        }

        private static IEnumerable<string> AllGearChains(ChainProbe.Archetype a)
        {
            foreach (var g in new[] { a.GearT1, a.GearT2, a.GearT3 })
                if (g != null) foreach (var s in g) if (!string.IsNullOrEmpty(s)) yield return s;
            if (a.Elites != null)
                foreach (var e in a.Elites)
                    if (e != null && e.Gear != null)
                        foreach (var s in e.Gear) if (!string.IsNullOrEmpty(s)) yield return s;
        }

        // ---------------------------------------------------------------- 档位覆盖

        /// <summary>
        /// T3 槽位空洞：某个装备**类型**在 T1/T2 配了、T3 没配。
        ///
        /// ★为什么这条必须单独查★
        /// 【一键测装备】报的「12/12、0 槽位装不上」意思是"**配表里写了的**都成功穿上了"，
        /// 它不验"配表本身全不全" —— 没配的东西当然不会报错。于是可以同时出现
        /// 「100% 通过」和「四个槽位空着」。
        ///
        /// 而这在 T3 上特别致命，因为 GearFor（GearTool.cs:595-597）返回的是
        /// **单独一档数组、不累加**，且 55 级存档恒为 T3 —— 也就是说
        /// **新招的卫兵只会拿到 gearT3，压根不经过 T1/T2**。
        /// 「删掉后档那条，前档那件靠只增不减留着」只对**逐级长大**的卫兵成立；
        /// 对直接招在 T3 的卫兵，那一格就是空的，而且没有任何提示。
        /// </summary>
        private static void GearCoverage()
        {
            try
            {
                int holes = 0;
                var detail = new List<string>();
                foreach (var a in Archetypes.All)
                {
                    var t1 = TypeCount(a.GearT1);
                    var t2 = TypeCount(a.GearT2);
                    var t3 = TypeCount(a.GearT3);
                    foreach (var kv in t1) Merge(t2, kv.Key, kv.Value);   // 前档取两者较多的那个
                    foreach (var kv in t2)
                    {
                        int has;
                        t3.TryGetValue(kv.Key, out has);
                        if (has < kv.Value)
                        {
                            holes++;
                            if (detail.Count < 8)
                                detail.Add(a.Name + " " + kv.Key + " T3=" + has + " 前档=" + kv.Value);
                        }
                    }
                }
                if (holes == 0) Ok("档位覆盖", "T3 覆盖了前档的所有装备类型，无空洞");
                else Warn("档位覆盖", holes + " 处 T3 空洞 —— ★T3 直招的卫兵这些槽会是空的★\n      "
                                    + string.Join("\n      ", detail.ToArray()));
            }
            catch (Exception e) { Warn("档位覆盖", "查不了: " + e.Message); }
        }

        private static void Merge(Dictionary<string, int> d, string k, int v)
        {
            int cur; d.TryGetValue(k, out cur);
            if (v > cur) d[k] = v;
        }

        /// <summary>把一档配表按**蓝图类型**计数 —— GearTool 是按类型定槽的，下标不代表槽位。</summary>
        private static Dictionary<string, int> TypeCount(string[] tier)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (tier == null) return d;
            foreach (var entry in tier)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var first = entry.Split('|')[0].Trim();       // 候选链只看首选
                if (string.IsNullOrEmpty(first)) continue;
                object bp = null;
                try { bp = ResourcesLibrary.TryGetBlueprint(first); } catch { }
                if (bp == null) continue;                     // 解析不到的归 GearGuids 那条管
                string t = bp.GetType().Name;
                int c; d.TryGetValue(t, out c); d[t] = c + 1;
            }
            return d;
        }

        // ---------------------------------------------------------------- 设置不变式

        private static void SettingsSanity()
        {
            var st = Main.Settings;
            if (st == null) { Bad("设置", "Settings 为 null"); return; }

            // 差价制的前提：目标档总价必须单调不减，否则"升级反而退钱"
            if (st.ShipPriceGrand < st.ShipPriceCruiser)
                Warn("船坞定价", "大巡 " + st.ShipPriceGrand + " < 巡洋 " + st.ShipPriceCruiser
                              + "，已由 ShipDialog.TotalFor 夹住，但面板上的数字是误导的");
            else Ok("船坞定价", "巡洋 " + st.ShipPriceCruiser + " / 大巡 " + st.ShipPriceGrand + "（单调）");

            if (st.RecruitMaxGuards <= 0)
                Warn("招募上限", "为 " + st.RecruitMaxGuards + "，一个都招不了");
            else Ok("招募上限", st.RecruitMaxGuards + " 名，每名 " + st.RecruitPfPerGuard + " 利润因子");

            if (st.FleetPfFrigate < 0 || st.FleetPfCruiser < 0 || st.FleetPfGrandCruiser < 0
                || st.FleetPfRefitPerSlot < 0 || st.FleetPfPerShot < 0 || st.FleetPfPerRange < 0)
                Warn("舰队 PF 费率", "含负数；运行时按 0 夹取，请在面板恢复为非负值");
            else Ok("舰队 PF 费率", st.FleetPfFrigate + "/" + st.FleetPfCruiser + "/"
                + st.FleetPfGrandCruiser + "，换装/开火/射程 " + st.FleetPfRefitPerSlot
                + "/" + st.FleetPfPerShot + "/" + st.FleetPfPerRange);

            // 这五个都是"作弊"性质的开关，发布默认应当全关。
            // ★精英那两个原来漏在这里★ 它们本来待在开发区，于是写这条检查时没想到；
            // 挪进玩家区之后，带着它们发包和带着前三个发包是同一类错误。
            bool unlocked = st.NoCountCap() || st.NoPfGate() || st.NoLevelCap()
                         || st.UnlockEliteLimit || st.EliteIgnoreUnlock;
            if (unlocked) Warn("解除限制", "有开关处于打开状态 —— 发布默认应当全关，"
                                        + "别把本机配置当成玩家的默认体验");
            else Ok("解除限制", "全关（发布默认形态）");
        }

        // ---------------------------------------------------------------- 海战卫队蓝图

        private static void SpaceEscortBlueprint()
        {
            try
            {
                int passed, total;
                string detail = SpaceEscortService.ValidateAllBlueprints(out passed, out total);
                if (passed == total)
                    Ok("海战卫队蓝图", passed + "/" + total + " 项通过：原版舰尺寸 / brain / prefab / 武器及 PlayerFaction；" + detail);
                else Bad("海战卫队蓝图", passed + "/" + total + " 通过；" + detail);
            }
            catch (Exception e) { Bad("海战卫队蓝图", "异常: " + e.Message); }
        }

        // ---------------------------------------------------------------- 舰队预算（纯内存，不读场景/蓝图）

        private static void FleetBudget_()
        {
            try
            {
                var hulls = SpaceFleetCatalog.All();
                var legacySlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    { SpaceFleetCatalog.FrigateGuid, 6 },
                    { SpaceFleetCatalog.ProwLanceFrigateGuid, 6 },
                    { SpaceFleetCatalog.CruiserGuid, 9 },
                    { SpaceFleetCatalog.GrandCruiserGuid, 8 }
                };
                bool slotsOk = hulls.Length >= legacySlots.Count;
                int legacyFound = 0;
                var hullIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allKeys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < hulls.Length; i++)
                {
                    var hull = hulls[i];
                    if (hull == null || hull.Slots == null || hull.Slots.Length <= 4
                        || string.IsNullOrEmpty(hull.BlueprintGuid) || !hullIds.Add(hull.BlueprintGuid)
                        || hull.Class < 0 || hull.Class > 2 || hull.Capacity != hull.Class + 1)
                    { slotsOk = false; continue; }
                    int legacyCount;
                    if (legacySlots.TryGetValue(hull.BlueprintGuid, out legacyCount))
                    {
                        legacyFound++;
                        if (hull.Slots.Length != legacyCount) slotsOk = false;
                    }
                    int components = 0, broadside = 0, other = 0;
                    var local = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var slot in hull.Slots)
                    {
                        if (slot == null || string.IsNullOrEmpty(slot.Key) || !local.Add(slot.Key)
                            || !allKeys.Add(hull.BlueprintGuid + ":" + slot.Key))
                        { slotsOk = false; continue; }
                        if (!slot.IsWeapon) components++;
                        else if (slot.WeaponType == WeaponSlotType.Port
                            || slot.WeaponType == WeaponSlotType.Starboard) broadside++;
                        else other++;
                    }
                    slotsOk &= components == 4 && broadside == hull.BroadsideWeaponSlots
                        && other == hull.NonBroadsideWeaponSlots;
                }
                slotsOk &= legacyFound == legacySlots.Count;
                if (slotsOk) Ok("舰队槽位目录", hulls.Length + " 种舰型；旧舰槽位不变，身份/SlotKey 唯一，主组件与炮数匹配");
                else Bad("舰队槽位目录", "旧舰槽位、舰型身份、组件/炮数或 SlotKey 不一致");

                var rates = FleetBudget.DefaultRates();
                long sixF = FullFleet(rates, SpaceFleetCatalog.FrigateGuid, 6, 0, 0, 0, 0);
                long threeC = FullFleet(rates, SpaceFleetCatalog.CruiserGuid, 3, 1, 0, 0, 3);
                long twoG = FullFleet(rates, SpaceFleetCatalog.GrandCruiserGuid, 2, 2, 1, 3, 5);
                long mixed = FullShip(rates, SpaceFleetCatalog.FrigateGuid, 0, 0, 0, 0)
                    + FullShip(rates, SpaceFleetCatalog.CruiserGuid, 1, 0, 0, 3)
                    + FullShip(rates, SpaceFleetCatalog.GrandCruiserGuid, 2, 1, 3, 5);
                if (sixF == 132 && threeC == 147 && twoG == 148 && mixed == 145)
                    Ok("舰队 PF 默认样例", "6F=132 / 3C=147 / 2G=148 / F+C+G=145");
                else Bad("舰队 PF 默认样例", "实际 " + sixF + "/" + threeC + "/" + twoG + "/" + mixed);
            }
            catch (Exception e) { Bad("舰队预算", "异常: " + e.Message); }
        }

        private static void SpaceFleetRefits()
        {
            try
            {
                int passed = 0;
                var failed = new List<string>();
                var verified = new List<string>();
                foreach (var weapon in SpaceFleetCatalog.RefitWeapons)
                {
                    string failure;
                    if (SpaceFleetCatalog.ValidateRefitWeapon(weapon, out failure))
                    {
                        passed++;
                        verified.Add(SpaceEscortService.ItemDisplayName(weapon.ItemGuid)
                            + " [" + weapon.Role + "]");
                    }
                    else failed.Add((weapon != null ? weapon.ItemGuid : "<null>") + "：" + failure);
                }
                if (passed == SpaceFleetCatalog.RefitWeapons.Length)
                    Ok("舰队换装 AI", passed + "/" + SpaceFleetCatalog.RefitWeapons.Length
                        + " 项通过：槽位 / target ability / donor overlay");
                else Bad("舰队换装 AI", passed + "/" + SpaceFleetCatalog.RefitWeapons.Length
                    + " 项通过；" + string.Join("；", failed.ToArray()));
                Main.Log("    已验证舰炮：" + string.Join("；", verified.ToArray()));
            }
            catch (Exception e) { Bad("舰队换装 AI", "异常: " + e.Message); }
        }

        private static void SpaceFleetComponents()
        {
            try
            {
                var hulls = SpaceFleetCatalog.All();
                var acquired = new List<string>();
                foreach (var hull in hulls)
                    foreach (var slot in hull.Slots)
                        if (!slot.IsWeapon && !string.IsNullOrEmpty(slot.OriginalItemGuid))
                            acquired.Add(slot.OriginalItemGuid);
                bool typesOk = true, optionsOk = true;
                int components = 0;
                foreach (var hull in hulls)
                {
                    string failure;
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintStarship>(hull.BlueprintGuid);
                    typesOk &= SpaceFleetCatalog.ValidateOriginalComponents(bp, hull, out failure);
                    foreach (var slot in hull.Slots)
                    {
                        if (slot.IsWeapon) continue;
                        components++;
                        var type = SpaceFleetCatalog.ComponentBlueprintType(slot);
                        var field = typeof(Warhammer.SpaceCombat.StarshipLogic.Equipment.HullSlots)
                            .GetField(slot.Key.Substring("component:".Length));
                        typesOk &= type != null && field != null && field.FieldType.IsGenericType
                            && field.FieldType.GetGenericArguments()[0] == type;
                        var stockOnly = SpaceEscortService.ItemOptions(hull, slot.Key, new string[0]);
                        optionsOk &= stockOnly.Count == 1 && stockOnly[0] == (slot.OriginalItemGuid ?? "");
                        var options = SpaceEscortService.ItemOptions(hull, slot.Key, acquired);
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var guid in options)
                        {
                            BlueprintStarshipItem item;
                            SpaceFleetWeaponDef weapon;
                            optionsOk &= seen.Add(guid) && SpaceFleetCatalog.TryResolveItem(
                                hull, slot.Key, guid, out item, out weapon);
                        }
                        BlueprintStarshipItem rejected;
                        SpaceFleetWeaponDef ignored;
                        optionsOk &= !SpaceFleetCatalog.TryResolveItem(hull, slot.Key,
                            SpaceFleetCatalog.RefitWeapons[0].ItemGuid, out rejected, out ignored);
                        if (!string.IsNullOrEmpty(slot.OriginalItemGuid))
                            optionsOk &= !SpaceFleetCatalog.TryResolveItem(hull, slot.Key, "",
                                out rejected, out ignored);
                    }
                }
                if (typesOk && optionsOk && components == hulls.Length * 4)
                    Ok("舰队主组件", hulls.Length + " 舰型 × 4 槽：原装 / Code.dll 泛型槽类型 / 获得筛选 / 跨类型与非法卸空拒绝");
                else Bad("舰队主组件", "主组件原装、类型、候选或槽数不一致");

                var frigate = SpaceFleetCatalog.Find(SpaceFleetCatalog.FrigateGuid);
                var plasma = SpaceFleetCatalog.FindSlot(frigate, "component:PlasmaDrives");
                var donor = SpaceFleetCatalog.Find(SpaceFleetCatalog.CruiserGuid);
                string replacement = SpaceFleetCatalog.FindSlot(donor, plasma.Key).OriginalItemGuid;
                var entry = new SpaceFleetEntry { Id = "component-selfcheck", BlueprintGuid = frigate.BlueprintGuid };
                entry.Loadout.Add(new SpaceFleetLoadoutChoice { SlotKey = plasma.Key, ItemGuid = replacement });
                var rows = new List<string>();
                string reason;
                bool payloadOk = !SpaceEscortService.AppendCanonicalLoadout(entry, frigate, rows,
                    new string[0], out reason) && rows.Count == 0;
                payloadOk &= SpaceEscortService.AppendCanonicalLoadout(entry, frigate, rows,
                    acquired, out reason) && rows.Count == 3 && rows[0] == "1";
                List<SpaceFleetLoadoutChoice> parsed;
                payloadOk &= SpaceEscortService.TryParseLoadout(frigate, rows.ToArray(), 1, 1,
                    out parsed, out reason) && parsed.Count == 1
                    && parsed[0].SlotKey == plasma.Key && parsed[0].ItemGuid == replacement;
                payloadOk &= !SpaceEscortService.TryParseLoadout(frigate,
                    new[] { plasma.Key, replacement, plasma.Key, replacement }, 0, 2, out parsed, out reason);
                payloadOk &= !SpaceEscortService.TryParseLoadout(frigate,
                    new[] { "component:WarpDrives", replacement }, 0, 1, out parsed, out reason);
                payloadOk &= !SpaceEscortService.TryParseLoadout(frigate,
                    new[] { plasma.Key, SpaceFleetCatalog.RefitWeapons[0].ItemGuid }, 0, 1, out parsed, out reason);
                payloadOk &= !SpaceEscortService.TryParseLoadout(frigate,
                    new[] { plasma.Key }, 0, 1, out parsed, out reason);
                payloadOk &= SpaceEscortService.TryParseLoadout(frigate,
                    new[] { "component:ArmorPlating", "" }, 0, 1, out parsed, out reason) && parsed.Count == 0;
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(SpaceFleetEntry));
                using (var writer = new StringWriter())
                {
                    serializer.Serialize(writer, entry);
                    using (var reader = new StringReader(writer.ToString()))
                    {
                        var saved = (SpaceFleetEntry)serializer.Deserialize(reader);
                        payloadOk &= saved.Loadout.Count == 1 && saved.Loadout[0].SlotKey == plasma.Key
                            && saved.Loadout[0].ItemGuid == replacement;
                    }
                }
                if (payloadOk) Ok("舰队组件 payload", "往返 / 未获得拒绝 / 重复与隐藏槽拒绝 / 错型与截断拒绝 / 原装空槽 / XML 往返");
                else Bad("舰队组件 payload", "组件 loadout 序列化或拒绝规则异常");

                var before = entry.Loadout;
                var proposed = new List<SpaceFleetLoadoutChoice>();
                bool rollbackOk = !SpaceEscortService.SaveLoadoutChange(entry, proposed, () => false)
                    && ReferenceEquals(entry.Loadout, before) && entry.Loadout.Count == 1;
                try { SpaceEscortService.SaveLoadoutChange(entry, proposed, () => { throw new IOException("selfcheck"); }); }
                catch (IOException) { }
                rollbackOk &= ReferenceEquals(entry.Loadout, before) && entry.Loadout[0].ItemGuid == replacement;
                rollbackOk &= SpaceEscortService.SaveLoadoutChange(entry, proposed, () => true)
                    && ReferenceEquals(entry.Loadout, proposed);
                entry.Loadout = null;
                rollbackOk &= !SpaceEscortService.SaveLoadoutChange(entry, proposed, () => false) && entry.Loadout == null;

                // 预算只计槽数；这些仅为内存价格探针，不是蓝图或新增 AssetId。
                entry.Loadout = new List<SpaceFleetLoadoutChoice>();
                foreach (var slot in frigate.Slots)
                    if (!slot.IsWeapon)
                        entry.Loadout.Add(new SpaceFleetLoadoutChoice { SlotKey = slot.Key, ItemGuid = "cost-only-a" });
                var rates = FleetBudget.DefaultRates();
                long first = FleetBudget.Cost(entry, rates).Refit;
                foreach (var choice in entry.Loadout) choice.ItemGuid = "cost-only-b";
                long second = FleetBudget.Cost(entry, rates).Refit;
                foreach (var choice in entry.Loadout)
                    choice.ItemGuid = SpaceFleetCatalog.FindSlot(frigate, choice.SlotKey).OriginalItemGuid;
                long restored = FleetBudget.Cost(entry, rates).Refit;
                bool budgetOk = first == 4 * rates.RefitPerSlot && first == second && restored == 0
                    && FleetBudget.CanApply(100, 100, out reason) && FleetBudget.CanApply(100, 99, out reason);
                if (rollbackOk && budgetOk) Ok("舰队组件预算/回滚", "四槽同价 / 同价替换 / 原装归零 / false 与异常恢复原引用；仅内存");
                else Bad("舰队组件预算/回滚", "槽价、还原或保存失败回滚异常");
            }
            catch (Exception e) { Bad("舰队主组件", "异常: " + e.Message); }
        }

        /// <summary>
        /// 真正经过原版 settings.json 序列化边界的 A/B 快照检查。
        /// 所有容器和名册均为独立内存对象；不取 Game.Instance.State.InGameSettings，
        /// 不替换 Main.Settings，不调用保存管理器，也不创建任何磁盘存档。
        /// </summary>
        private static void SpaceFleetSaveSnapshots()
        {
            try
            {
                string gameId = Guid.NewGuid().ToString("N");
                var source = FleetSnapshotNativeSettings();
                RequireFleetSnapshot(SpaceFleetSaveStore.StorageKey == "dynastyretinue.space_fleet.v1",
                    "StorageKey 与约定不一致");
                var live = SpaceFleetSaveStore.Get(source, gameId, true);
                RequireFleetSnapshot(FleetSnapshotIsEmpty(live, gameId),
                    "独立新容器没有得到 DVer3 空名册");
                RequireFleetSnapshot(FleetSnapshotNativeKeysIntact(source),
                    "Get(create=true) 改动了其他 native key");

                // 两个真实舷炮 GUID 仅作为序列化数据；这里不解析蓝图、不造实体。
                live.Initialized = true;
                live.NextId = 2;
                live.Entries.Add(new SpaceFleetEntry
                {
                    Id = "fleet-1",
                    BlueprintGuid = SpaceFleetCatalog.GrandCruiserGuid,
                    Name = "快照 A · 原名舰",
                    BroadsideExtraShots = 1,
                    NonBroadsideExtraShots = 0,
                    BroadsideExtraRange = 1,
                    NonBroadsideExtraRange = 0,
                    Loadout = new List<SpaceFleetLoadoutChoice>
                    {
                        new SpaceFleetLoadoutChoice
                        {
                            SlotKey = "weapon:Starboard:0",
                            ItemGuid = FleetSnapshotWeaponA
                        }
                    }
                });
                live.KnownShipItemGuids.Add(FleetSnapshotWeaponA);
                RequireFleetSnapshot(SpaceFleetSaveStore.TryWrite(source, live),
                    "所属容器写入快照 A 被拒绝");
                RequireFleetSnapshot(source.List.ContainsKey(SpaceFleetSaveStore.StorageKey)
                    && FleetSnapshotNativeKeysIntact(source), "A 未写入 native key 或覆盖了其他 key");
                string jsonA = FleetSnapshotSerialize(source);

                // 必须原地改同一份名册及嵌套对象，再保存 B；不能分别造两份预期结果冒充隔离。
                live.NextId = 3;
                live.Entries[0].Name = "快照 B · 改名舰";
                live.Entries[0].Loadout[0].ItemGuid = FleetSnapshotWeaponB;
                live.Entries[0].BroadsideExtraShots = 2;
                live.Entries[0].NonBroadsideExtraShots = 1;
                live.Entries[0].BroadsideExtraRange = 3;
                live.Entries[0].NonBroadsideExtraRange = 2;
                live.KnownShipItemGuids.Add(FleetSnapshotWeaponB);
                RequireFleetSnapshot(SpaceFleetSaveStore.TryWrite(source, live),
                    "所属容器写入快照 B 被拒绝");
                RequireFleetSnapshot(FleetSnapshotNativeKeysIntact(source), "B 覆盖了其他 native key");
                string jsonB = FleetSnapshotSerialize(source);
                RequireFleetSnapshot(!string.Equals(jsonA, jsonB, StringComparison.Ordinal)
                    && FleetSnapshotHasNoTypeMetadata(jsonA) && FleetSnapshotHasNoTypeMetadata(jsonB),
                    "A/B 未形成不同快照，或 JSON 泄漏 $type/mod 类型元数据");

                var nativeA = FleetSnapshotDeserialize(jsonA);
                var nativeB = FleetSnapshotDeserialize(jsonB);
                RequireFleetSnapshot(!ReferenceEquals(nativeA, nativeB)
                    && !ReferenceEquals(nativeA.List, nativeB.List)
                    && !ReferenceEquals(source.List, nativeA.List)
                    && !ReferenceEquals(source.List, nativeB.List), "native 容器或 List 共用引用");
                var rosterA = SpaceFleetSaveStore.Get(nativeA, gameId, false);
                var rosterB = SpaceFleetSaveStore.Get(nativeB, gameId, false);
                RequireFleetSnapshot(FleetSnapshotMatches(rosterA, gameId, false)
                    && FleetSnapshotMatches(rosterB, gameId, true)
                    && FleetSnapshotMatches(live, gameId, true), "原版往返后 A/B 内容混淆或字段丢失");
                RequireFleetSnapshot(FleetSnapshotReferencesDisjoint(rosterA, rosterB)
                    && FleetSnapshotReferencesDisjoint(rosterA, live)
                    && FleetSnapshotReferencesDisjoint(rosterB, live),
                    "A/B/source 名册、Entries、Loadout、choice 或 knownitems 共用可变引用");
                RequireFleetSnapshot(FleetSnapshotNativeKeysIntact(nativeA)
                    && FleetSnapshotNativeKeysIntact(nativeB), "原版往返丢失其他 native key");

                // 同 GameId、没有 StorageKey 的旧档，不能借用 source/A/B 的缓存或名册。
                var oldNative = FleetSnapshotNativeSettings();
                RequireFleetSnapshot(!oldNative.List.ContainsKey(SpaceFleetSaveStore.StorageKey),
                    "旧档探针不应自带 StorageKey");
                var oldRoster = SpaceFleetSaveStore.Get(oldNative, gameId, true);
                RequireFleetSnapshot(FleetSnapshotIsEmpty(oldRoster, gameId)
                    && FleetSnapshotReferencesDisjoint(oldRoster, rosterA)
                    && FleetSnapshotReferencesDisjoint(oldRoster, rosterB)
                    && FleetSnapshotReferencesDisjoint(oldRoster, live),
                    "同 GameId 的无 key 旧档复用了已有名册/可变集合");
                RequireFleetSnapshot(FleetSnapshotNativeKeysIntact(oldNative),
                    "旧档创建空名册时覆盖其他 native key");
                // 交错访问后再读，专门检查仅以 GameId 为键的错误缓存。
                rosterB = SpaceFleetSaveStore.Get(nativeB, gameId, false);
                rosterA = SpaceFleetSaveStore.Get(nativeA, gameId, false);
                RequireFleetSnapshot(FleetSnapshotMatches(rosterA, gameId, false)
                    && FleetSnapshotMatches(rosterB, gameId, true)
                    && FleetSnapshotIsEmpty(SpaceFleetSaveStore.Get(oldNative, gameId, true), gameId)
                    && FleetSnapshotMatches(SpaceFleetSaveStore.Get(source, gameId, false), gameId, true),
                    "交错 Get 将同 GameId 的不同 native 容器串档");
                Ok("舰队存档 A/B 快照", "原版 settings.json 往返：改名/换装/四项强化/knownitems 各自保留，无类型元数据");

                string beforeWrongWriteA = FleetSnapshotSerialize(nativeA);
                string beforeWrongWriteB = FleetSnapshotSerialize(nativeB);
                RequireFleetSnapshot(!SpaceFleetSaveStore.TryWrite(nativeB, rosterA),
                    "错误地允许把 A 容器取得的 roster 写进 B");
                RequireFleetSnapshot(FleetSnapshotSerialize(nativeA) == beforeWrongWriteA
                    && FleetSnapshotSerialize(nativeB) == beforeWrongWriteB
                    && FleetSnapshotMatches(SpaceFleetSaveStore.Get(nativeB, gameId, false), gameId, true)
                    && FleetSnapshotMatches(rosterA, gameId, false)
                    && FleetSnapshotNativeKeysIntact(nativeA) && FleetSnapshotNativeKeysIntact(nativeB),
                    "跨容器写入虽被拒绝，却已改动 native 内容/缓存/源名册");
                Ok("舰队存档容器归属", "无 key 旧档为空；同 GameId 分容器隔离；A roster 写 B 拒绝且无副作用");

                // 深层修改仅落在还原出来的 A；既检查引用，也检查实际可观察状态。
                rosterA.Entries[0].Name = "快照 A · 局部内存修改";
                rosterA.Entries[0].Loadout[0].ItemGuid = FleetSnapshotWeaponB;
                rosterA.Entries[0].BroadsideExtraShots = 2;
                rosterA.KnownShipItemGuids.Add(FleetSnapshotWeaponB);
                RequireFleetSnapshot(FleetSnapshotMatches(rosterB, gameId, true)
                    && FleetSnapshotMatches(live, gameId, true)
                    && FleetSnapshotIsEmpty(oldRoster, gameId)
                    && FleetSnapshotSerialize(nativeB) == beforeWrongWriteB,
                    "修改 A 的嵌套对象污染了 B/source/旧档");
                RequireFleetSnapshot(SpaceFleetSaveStore.TryWrite(nativeA, rosterA),
                    "从 A 容器 Get 取得的 roster 不能写回 A");
                string editedJsonA = FleetSnapshotSerialize(nativeA);
                var editedNativeA = FleetSnapshotDeserialize(editedJsonA);
                var editedA = SpaceFleetSaveStore.Get(editedNativeA, gameId, false);
                RequireFleetSnapshot(editedJsonA != beforeWrongWriteA
                    && editedA != null && editedA.Entries != null && editedA.Entries.Count == 1
                    && editedA.Entries[0] != null
                    && editedA.Entries[0].Name == "快照 A · 局部内存修改"
                    && editedA.Entries[0].BroadsideExtraShots == 2
                    && editedA.Entries[0].Loadout != null && editedA.Entries[0].Loadout.Count == 1
                    && editedA.Entries[0].Loadout[0] != null
                    && editedA.Entries[0].Loadout[0].ItemGuid == FleetSnapshotWeaponB
                    && editedA.KnownShipItemGuids != null && editedA.KnownShipItemGuids.Count == 2
                    && editedA.KnownShipItemGuids.Contains(FleetSnapshotWeaponA)
                    && editedA.KnownShipItemGuids.Contains(FleetSnapshotWeaponB)
                    && FleetSnapshotReferencesDisjoint(editedA, rosterA)
                    && FleetSnapshotNativeKeysIntact(editedNativeA),
                    "本容器 TryWrite 成功后，原版再往返未保留局部修改或仍共用引用");
                var againNativeA = FleetSnapshotDeserialize(jsonA);
                var againA = SpaceFleetSaveStore.Get(againNativeA, gameId, false);
                RequireFleetSnapshot(FleetSnapshotMatches(againA, gameId, false)
                    && FleetSnapshotReferencesDisjoint(againA, rosterA)
                    && FleetSnapshotReferencesDisjoint(againA, rosterB)
                    && FleetSnapshotMatches(SpaceFleetSaveStore.Get(nativeB, gameId, false), gameId, true)
                    && FleetSnapshotNativeKeysIntact(nativeA) && FleetSnapshotNativeKeysIntact(againNativeA),
                    "再次读取原 A 字符串受后续内存修改影响，或其他 native key 丢失");
                Ok("舰队存档深层隔离", "名册/条目/loadout choice/knownitems 不共享；局部修改写回后，原 A 字符串仍还原 A");
            }
            catch (Exception e) { Bad("舰队存档快照隔离", "异常: " + e.Message); }
        }

        private const string FleetSnapshotWeaponA = "7bdc12cf63124a86b41cc7ca929d015d";
        private const string FleetSnapshotWeaponB = "4f1fc55e29314ddcbc3ee34b459db589";
        private const string FleetSnapshotUnknownKey = "selfcheck.native.unknown";

        private static InGameSettings FleetSnapshotNativeSettings()
        {
            var settings = new InGameSettings();
            settings.List[FleetSnapshotUnknownKey] = "保留 native key\n\"A/B\"";
            settings.List[FleetSnapshotUnknownKey + ".integer"] = 37;
            settings.List[FleetSnapshotUnknownKey + ".boolean"] = true;
            return settings;
        }

        private static bool FleetSnapshotNativeKeysIntact(InGameSettings settings)
        {
            object text, number, flag;
            return settings != null && settings.List != null
                && settings.List.TryGetValue(FleetSnapshotUnknownKey, out text)
                && Equals(text, "保留 native key\n\"A/B\"")
                && settings.List.TryGetValue(FleetSnapshotUnknownKey + ".integer", out number)
                && Equals(number, 37)
                && settings.List.TryGetValue(FleetSnapshotUnknownKey + ".boolean", out flag)
                && Equals(flag, true);
        }

        private static string FleetSnapshotSerialize(InGameSettings settings)
        {
            using (var text = new StringWriter(System.Globalization.CultureInfo.InvariantCulture))
            using (var writer = new Newtonsoft.Json.JsonTextWriter(text))
            {
                // 与 SaveManager.SerializeInGameSettings 同一个原版序列化器；
                // 不依赖可能不可见的 SerializeObject 扩展，也不修改 Serializer 配置。
                Kingmaker.Settings.SettingsJsonSerializer.Serializer.Serialize(
                    writer, settings, typeof(InGameSettings));
                writer.Flush();
                return text.ToString();
            }
        }

        private static InGameSettings FleetSnapshotDeserialize(string json)
        {
            using (var text = new StringReader(json))
            using (var reader = new Newtonsoft.Json.JsonTextReader(text))
            {
                var settings = Kingmaker.Settings.SettingsJsonSerializer.Serializer
                    .Deserialize<InGameSettings>(reader);
                RequireFleetSnapshot(settings != null && settings.List != null,
                    "原版反序列化未还原 InGameSettings.List");
                return settings;
            }
        }

        private static bool FleetSnapshotHasNoTypeMetadata(string json)
        {
            // 也覆盖 StorageKey 保存 JSON 字符串时其中被转义的 $type。
            return json.IndexOf("$type", StringComparison.OrdinalIgnoreCase) < 0
                && json.IndexOf(typeof(SpaceFleetRosterState).FullName, StringComparison.Ordinal) < 0
                && json.IndexOf(typeof(SpaceFleetEntry).FullName, StringComparison.Ordinal) < 0
                && json.IndexOf(typeof(SpaceFleetLoadoutChoice).FullName, StringComparison.Ordinal) < 0;
        }

        private static bool FleetSnapshotIsEmpty(SpaceFleetRosterState roster, string gameId)
        {
            return roster != null && roster.DataVersion == 3 && roster.GameId == gameId
                && roster.Entries != null && roster.Entries.Count == 0
                && roster.KnownShipItemGuids != null && roster.KnownShipItemGuids.Count == 0;
        }

        private static bool FleetSnapshotMatches(SpaceFleetRosterState roster, string gameId, bool b)
        {
            if (roster == null || roster.DataVersion != 3 || roster.GameId != gameId
                || !roster.Initialized || roster.NextId != (b ? 3 : 2)
                || roster.Entries == null || roster.Entries.Count != 1
                || roster.KnownShipItemGuids == null
                || roster.KnownShipItemGuids.Count != (b ? 2 : 1)
                || !roster.KnownShipItemGuids.Contains(FleetSnapshotWeaponA)
                || (b && !roster.KnownShipItemGuids.Contains(FleetSnapshotWeaponB))) return false;
            var entry = roster.Entries[0];
            return entry != null && entry.Id == "fleet-1"
                && entry.BlueprintGuid == SpaceFleetCatalog.GrandCruiserGuid
                && entry.Name == (b ? "快照 B · 改名舰" : "快照 A · 原名舰")
                && entry.BroadsideExtraShots == (b ? 2 : 1)
                && entry.NonBroadsideExtraShots == (b ? 1 : 0)
                && entry.BroadsideExtraRange == (b ? 3 : 1)
                && entry.NonBroadsideExtraRange == (b ? 2 : 0)
                && entry.Loadout != null && entry.Loadout.Count == 1 && entry.Loadout[0] != null
                && entry.Loadout[0].SlotKey == "weapon:Starboard:0"
                && entry.Loadout[0].ItemGuid == (b ? FleetSnapshotWeaponB : FleetSnapshotWeaponA);
        }

        private static bool FleetSnapshotReferencesDisjoint(SpaceFleetRosterState a, SpaceFleetRosterState b)
        {
            if (a == null || b == null || ReferenceEquals(a, b)
                || ReferenceEquals(a.Entries, b.Entries)
                || ReferenceEquals(a.KnownShipItemGuids, b.KnownShipItemGuids)
                || a.Entries == null || b.Entries == null) return false;
            foreach (var left in a.Entries)
                foreach (var right in b.Entries)
                {
                    if (left == null || right == null || ReferenceEquals(left, right)
                        || left.Loadout == null || right.Loadout == null
                        || ReferenceEquals(left.Loadout, right.Loadout)) return false;
                    foreach (var lc in left.Loadout)
                        foreach (var rc in right.Loadout)
                            if (lc != null && ReferenceEquals(lc, rc)) return false;
                }
            return true;
        }

        private static void RequireFleetSnapshot(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }

        private static long FullFleet(FleetBudgetRates rates, string hullGuid, int count,
            int bs, int ns, int br, int nr)
        {
            return FleetBudget.Mul(count, FullShip(rates, hullGuid, bs, ns, br, nr));
        }

        private static long FullShip(FleetBudgetRates rates, string hullGuid,
            int bs, int ns, int br, int nr)
        {
            var hull = SpaceFleetCatalog.Find(hullGuid);
            var entry = new SpaceFleetEntry
            {
                BlueprintGuid = hullGuid,
                BroadsideExtraShots = bs,
                NonBroadsideExtraShots = ns,
                BroadsideExtraRange = br,
                NonBroadsideExtraRange = nr
            };
            foreach (var slot in hull.Slots)
                entry.Loadout.Add(new SpaceFleetLoadoutChoice
                {
                    SlotKey = slot.Key,
                    ItemGuid = string.IsNullOrEmpty(slot.OriginalItemGuid)
                        ? "00000000000000000000000000000001"
                        : "00000000000000000000000000000002"
                });
            return FleetBudget.Cost(entry, rates).Total;
        }

        // ---------------------------------------------------------------- 舰船

        private static void ShipState()
        {
            try
            {
                var cur = ShipDialog.Current();
                var orig = ShipDialog.OriginalSize();
                Ok("座舰", "当前 " + ShipDialog.SizeName(cur) + "　原生 " + ShipDialog.SizeName(orig)
                         + "　船模 " + (string.IsNullOrEmpty(StarshipViewTool.CurrentPrefab) ? "原版" : "自定义"));

                // 卸载相关：这两条是玩家唯一会踩坏的地方，自检里显式提醒
                int n = RetinueRegistry.Count;
                if (n > 0 || cur != orig)
                    Warn("卸载前须知", "在册 " + n + " 名卫兵" + (cur != orig ? "、座舰已改装" : "")
                                    + " —— 禁用 mod 前需先遣散 / 还原船模再存盘");
                else Ok("卸载前须知", "无卫兵、座舰原样，可以直接禁用");
            }
            catch (Exception e) { Warn("座舰", "读不到（可能不在游戏内）: " + e.Message); }
        }

        // ---------------------------------------------------------------- 诊断包

        /// <summary>
        /// 顺便验一次诊断包：能不能生成、生成的东西里**还有没有用户名**。
        /// 这条本来要玩家自己导出再用记事本搜，属于典型的"机器该干的活"。
        /// </summary>
        private static void Report()
        {
            try
            {
                string p = DiagnosticReport.Export();
                if (string.IsNullOrEmpty(p)) { Bad("诊断包", "导出失败"); return; }

                string body = File.ReadAllText(p, Encoding.UTF8);
                string user = null;
                try { user = Environment.UserName; } catch { }
                bool leak = !string.IsNullOrEmpty(user) && user.Length >= 3
                         && body.IndexOf(user, StringComparison.OrdinalIgnoreCase) >= 0;

                if (leak) Bad("诊断包脱敏", "★里面还能搜到用户名「" + user + "」★ 不能让玩家直接发出去");
                else Ok("诊断包", new FileInfo(p).Length / 1024 + " KB，未检出用户名　" + Path.GetFileName(p));
            }
            catch (Exception e) { Bad("诊断包", "异常: " + e.Message); }
        }
    }
}
