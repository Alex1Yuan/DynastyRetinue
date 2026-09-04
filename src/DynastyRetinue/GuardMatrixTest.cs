using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items;
using Kingmaker.Blueprints.Items.Weapons;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.UnitLogic.Progression.Features;
using Kingmaker.UnitLogic.Progression.Paths;

namespace DynastyRetinue
{
    /// <summary>
    /// 开发区全路线矩阵：每条普通路线的 T1/T2/T3 + 全部精英，各生成一名并验收。
    /// 逐样本跨帧运行，样本销毁并复查为零后才继续；不会把测试实体留进存档。
    /// </summary>
    internal static class GuardMatrixTest
    {
        private sealed class Case
        {
            public int ArchIndex;
            public int Tier;
            public ChainProbe.Archetype Arch;
            public ChainProbe.EliteDef Elite;
            public string Label;
        }

        private sealed class Result
        {
            public string Label;
            public bool Passed;
            public string Problems;
            public string Unit;
            public int Level;
            public string Paths;
            public string Gear;
            public string Brain;
        }

        private static readonly List<Case> Cases = new List<Case>();
        private static readonly List<Result> Results = new List<Result>();
        private static int _index;
        private static BaseUnitEntity _current;
        private static bool _running;
        private static Action<bool> _completed;

        private static bool _oldAlignExperience;
        private static bool _oldAutoLevelUp;
        private static bool _oldEquipGear;
        private static bool _oldMeleeEliteSupport;
        private static bool _oldUnlockTier;
        private static bool _oldUnlockLevel;
        private static bool _oldUnlockElite;
        private static bool _oldIgnoreUnlock;
        private static int _oldGearTier;
        private static bool _settingsSaved;

        internal static bool IsRunning { get { return _running; } }

        internal static void Run(Action<bool> completed = null)
        {
            if (_running)
            {
                Main.Log("[卫队矩阵] 上一轮仍在运行。");
                if (completed != null) completed(false);
                return;
            }
            if (!Main.DevMode)
            {
                Main.Log("[卫队矩阵] 仅开发模式可用。");
                if (completed != null) completed(false);
                return;
            }
            if (Game.Instance == null || Game.Instance.Player == null
                || Game.Instance.Player.MainCharacterEntity == null)
            {
                Main.LogError("[卫队矩阵] 请先进入步行区域。");
                if (completed != null) completed(false);
                return;
            }
            if (Game.Instance.Player.IsInCombat)
            {
                Main.LogError("[卫队矩阵] 战斗中不能运行。");
                if (completed != null) completed(false);
                return;
            }
            if (CoopState.SharedGameplayRequired)
            {
                Main.LogError("[卫队矩阵] 合作会话中禁止运行：测试阶位是本地内存状态，会导致不同步。");
                if (completed != null) completed(false);
                return;
            }

            _running = true;
            _settingsSaved = false;
            _completed = completed;
            Cases.Clear();
            Results.Clear();
            _index = 0;
            _current = null;

            try
            {
                SaveSettings();
                BuildCases();
                if (Cases.Count == 0) { Abort("没有生成任何测试用例"); return; }
                Main.Log("======== 卫队全路线矩阵开始：普通 "
                       + CountNormals() + "（每线 T1/T2/T3）+ 精英 " + CountElites()
                       + "，合计 " + Cases.Count + " ========");
                Main.Log("  ★会先清空现有卫兵；每个测试样本验完立即销毁，名册复查为 0 才继续。★");
                RetinueRegistry.DismissAll();
                Deferred.NextFrames(3, BeginNext);
            }
            catch (Exception e) { Abort("初始化失败: " + e); }
        }

        private static void SaveSettings()
        {
            _oldAlignExperience = Main.Settings.AlignExperience;
            _oldAutoLevelUp = Main.Settings.AutoLevelUp;
            _oldEquipGear = Main.Settings.EquipGraduationGear;
            _oldMeleeEliteSupport = Main.Settings.MeleeEliteSupport;
            _oldUnlockTier = Main.Settings.UnlockTierLimits;
            _oldUnlockLevel = Main.Settings.UnlockLevelCap;
            _oldUnlockElite = Main.Settings.UnlockEliteLimit;
            _oldIgnoreUnlock = Main.Settings.EliteIgnoreUnlock;
            _oldGearTier = Main.Settings.GearTierOverride;
            _settingsSaved = true;

            // 测试自己控制真实阶位与经验，不能被主角等级、“全部解除”或个人开关覆盖。
            Main.Settings.AlignExperience = false;
            Main.Settings.AutoLevelUp = true;
            Main.Settings.EquipGraduationGear = true;
            Main.Settings.MeleeEliteSupport = true;
            Main.Settings.UnlockTierLimits = false;
            Main.Settings.UnlockLevelCap = false;
            Main.Settings.UnlockEliteLimit = true;
            Main.Settings.EliteIgnoreUnlock = true;
        }

        private static void RestoreSettings()
        {
            RetinueTest.TestTierOverride = 0;
            if (!_settingsSaved || Main.Settings == null) return;
            Main.Settings.AlignExperience = _oldAlignExperience;
            Main.Settings.AutoLevelUp = _oldAutoLevelUp;
            Main.Settings.EquipGraduationGear = _oldEquipGear;
            Main.Settings.MeleeEliteSupport = _oldMeleeEliteSupport;
            Main.Settings.UnlockTierLimits = _oldUnlockTier;
            Main.Settings.UnlockLevelCap = _oldUnlockLevel;
            Main.Settings.UnlockEliteLimit = _oldUnlockElite;
            Main.Settings.EliteIgnoreUnlock = _oldIgnoreUnlock;
            Main.Settings.GearTierOverride = _oldGearTier;
            _settingsSaved = false;
        }

        private static void BuildCases()
        {
            var archs = Archetypes.All;
            if (archs == null) return;
            for (int ai = 0; ai < archs.Length; ai++)
            {
                var arch = archs[ai];
                if (arch == null) continue;
                for (int tier = 1; tier <= 3; tier++)
                    Cases.Add(new Case
                    {
                        ArchIndex = ai, Tier = tier, Arch = arch,
                        Label = arch.Name + " / 普通 T" + tier
                    });
                if (arch.Elites == null) continue;
                for (int ei = 0; ei < arch.Elites.Length; ei++)
                {
                    var elite = arch.Elites[ei];
                    if (elite == null) continue;
                    Cases.Add(new Case
                    {
                        ArchIndex = ai, Tier = 3, Arch = arch, Elite = elite,
                        Label = arch.Name + " / 精英[" + ei + "] " + (elite.Name ?? "?")
                    });
                }
            }
        }

        private static int CountNormals()
        {
            int n = 0;
            foreach (var c in Cases) if (c.Elite == null) n++;
            return n;
        }

        private static int CountElites()
        {
            int n = 0;
            foreach (var c in Cases) if (c.Elite != null) n++;
            return n;
        }

        private static void BeginNext()
        {
            try
            {
                int left = RetinueRegistry.All(true).Count;
                if (left != 0)
                {
                    Abort("上一个样本清理后名册仍有 " + left + " 名；停止，防止污染存档。");
                    return;
                }
                if (_index >= Cases.Count) { Finish(); return; }

                Case c = Cases[_index];
                RetinueTest.TestTierOverride = c.Tier;
                Main.Settings.GearTierOverride = c.Tier;
                GearTool.LastOk = GearTool.LastAlready = GearTool.LastFail = GearTool.LastMiss = 0;
                GearTool.LastNames = GearTool.LastRejected = "";

                Main.Log("---- [" + (_index + 1) + "/" + Cases.Count + "] " + c.Label + " ----");
                _current = RetinueTest.SpawnOne(c.ArchIndex, c.Elite, true, c.Elite == null);
                if (_current == null)
                {
                    Results.Add(new Result { Label = c.Label, Passed = false, Problems = "SpawnOne 返回 null" });
                    _index++;
                    Deferred.NextFrames(2, BeginNext);
                    return;
                }
                Deferred.NextFrames(3, InspectCurrent);
            }
            catch (Exception e) { Abort("生成样本异常: " + e); }
        }

        private static void InspectCurrent()
        {
            try
            {
                Case c = Cases[_index];
                Result r = Inspect(c, _current);
                Results.Add(r);
                if (r.Passed) Main.Log("  PASS " + c.Label);
                else Main.LogError("  FAIL " + c.Label + " :: " + r.Problems);
                DestroyCurrent();
                _index++;
                Deferred.NextFrames(3, BeginNext);
            }
            catch (Exception e) { Abort("验收样本异常: " + e); }
        }

        private static Result Inspect(Case c, BaseUnitEntity unit)
        {
            var bad = new List<string>();
            var r = new Result { Label = c.Label };
            if (unit == null || unit.IsDisposed)
            {
                r.Passed = false;
                r.Problems = "实体为空或已销毁";
                return r;
            }

            try { r.Unit = unit.Blueprint != null ? unit.Blueprint.name : "?"; } catch { r.Unit = "?"; }
            try { r.Level = unit.Progression.CharacterLevel; } catch { r.Level = -1; }
            try { r.Gear = RetinueTest.GearLine(unit); } catch { r.Gear = "?"; }
            try { r.Brain = unit.Brain != null && unit.Brain.Blueprint != null ? unit.Brain.Blueprint.name : "?"; } catch { r.Brain = "?"; }

            int wantLevel = Archetypes.GuardLevelCap(c.Tier);
            if (r.Level != wantLevel) bad.Add("等级 " + r.Level + " != " + wantLevel);

            string[] sourceChain = c.Elite != null && c.Elite.Chain != null && c.Elite.Chain.Length > 0
                                 ? c.Elite.Chain : c.Arch.Chain;
            BuildPlans.Plan effectivePlan = EffectivePlan(c, sourceChain);
            string[] chain = EffectiveChain(sourceChain, effectivePlan);
            int depth = c.Elite != null ? 3 : Archetypes.ChainDepth(c.Tier);
            var pathText = new List<string>();
            for (int i = 0; i < depth; i++)
            {
                if (chain == null || i >= chain.Length || string.IsNullOrEmpty(chain[i]))
                { bad.Add("职业链第 " + (i + 1) + " 段为空"); continue; }
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>(chain[i]);
                if (bp == null) { bad.Add("职业链解析不到 " + chain[i]); continue; }
                int rank = unit.Progression.GetPathRank(bp);
                pathText.Add(bp.name + "=" + rank + "/" + bp.Ranks);
                if (rank != bp.Ranks) bad.Add(bp.name + " rank " + rank + "/" + bp.Ranks);
            }
            r.Paths = string.Join(" | ", pathText.ToArray());
            VerifyNoExtraPaths(unit, chain, depth, bad);

            VerifyFeatureList(unit, c.Arch.GrantFeatures, "常驻能力", bad, false);
            if (c.Arch.GrantFeaturesTier != null
                && c.Tier - 1 < c.Arch.GrantFeaturesTier.Length)
                VerifyFeatureList(unit, c.Arch.GrantFeaturesTier[c.Tier - 1], "T" + c.Tier + "能力", bad, false);
            VerifyNoStaleTierFeatures(unit, c.Arch, c.Tier, bad);
            VerifyFeatureList(unit, c.Elite != null ? c.Elite.PreGrant : c.Arch.PreGrant,
                              "前置能力", bad, false);

            // 与正式发放共用同一入口：玩家 playerGear 覆盖默认三档时，测试也验覆盖后的真配置。
            string[] gear = GearTool.GearFor(unit, c.Arch);
            VerifyGear(unit, gear, bad);
            VerifyWeaponSets(unit, bad);
            if (c.Elite == null && c.Tier == 3
                && c.Arch.Name != null && c.Arch.Name.IndexOf("灵能", StringComparison.Ordinal) >= 0)
                VerifyLegacyPsykerWeaponRepair(unit, c.Arch, gear, bad);
            VerifyBrain(unit, c, bad);

            var audit = Archetypes.LastAudit;
            if (audit != null && audit.Applicable > 0 && audit.Ok != audit.Applicable)
                bad.Add("方案落实 " + audit.Ok + "/" + audit.Applicable
                      + "（A" + audit.MissA + " B" + audit.MissB + " C" + audit.MissC + "）");
            if (GearTool.LastFail > 0) bad.Add("装备过程失败 " + GearTool.LastFail + " 格: " + GearTool.LastRejected);
            // LastMiss=该候选格所有蓝图都不可用，正式逻辑允许在缺 DLC 时跳过。
            // VerifyGear 已对“至少一个候选可加载但没穿上”判失败，纯缺资源格只记在 TSV，不假红。

            r.Passed = bad.Count == 0;
            r.Problems = r.Passed ? "" : string.Join(" ; ", bad.ToArray());
            return r;
        }

        private static BuildPlans.Plan EffectivePlan(Case c, string[] chain)
        {
            if (c.Elite != null && c.Elite.PlanSegments != null)
                return BuildPlans.Compose(c.Elite.Name, chain,
                                          c.Elite.PlanSegments, c.Elite.ExcludeFeatures);
            string planName = c.Elite != null
                            ? (!string.IsNullOrEmpty(c.Elite.PlanName) ? c.Elite.PlanName
                               : (c.Elite.Chain != null && c.Elite.Chain.Length > 0 ? null : c.Arch.PlanName))
                            : c.Arch.PlanName;
            return BuildPlans.Get(planName);
        }

        private static string[] EffectiveChain(string[] chain, BuildPlans.Plan plan)
        {
            if (plan != null && !string.IsNullOrEmpty(plan.First) && !string.IsNullOrEmpty(plan.Second))
                return new[] { plan.First, plan.Second, "bcefe9c41c7841c9a99b1dbac1793025" };
            return chain;
        }

        private static void VerifyNoExtraPaths(BaseUnitEntity unit, string[] expected, int depth,
                                               List<string> bad)
        {
            var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (expected != null)
                for (int i = 0; i < depth && i < expected.Length; i++)
                    if (!string.IsNullOrEmpty(expected[i])) allow.Add(expected[i]);
            try
            {
                foreach (var cp in unit.Progression.AllCareerPaths)
                {
                    if (cp.Blueprint == null) continue;
                    string id = cp.Blueprint.AssetGuid.ToString();
                    if (!allow.Contains(id)) bad.Add("多余职业路径 " + cp.Blueprint.name + " rank=" + cp.Rank);
                }
            }
            catch (Exception e) { bad.Add("额外职业路径检查异常 " + e.Message); }
        }

        private static void VerifyNoStaleTierFeatures(BaseUnitEntity unit, ChainProbe.Archetype arch,
                                                      int tier, List<string> bad)
        {
            if (arch == null || arch.GrantFeaturesTier == null) return;
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tier - 1 >= 0 && tier - 1 < arch.GrantFeaturesTier.Length
                && arch.GrantFeaturesTier[tier - 1] != null)
                foreach (string id in arch.GrantFeaturesTier[tier - 1])
                    if (!string.IsNullOrEmpty(id)) keep.Add(id.Trim());

            for (int t = 0; t < arch.GrantFeaturesTier.Length; t++)
            {
                if (t == tier - 1 || arch.GrantFeaturesTier[t] == null) continue;
                foreach (string raw in arch.GrantFeaturesTier[t])
                {
                    string id = raw != null ? raw.Trim() : "";
                    if (string.IsNullOrEmpty(id) || keep.Contains(id)) continue;
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(id);
                    if (bp != null && unit.Facts.Contains(bp))
                        bad.Add("残留 T" + (t + 1) + " 能力 "
                              + (string.IsNullOrEmpty(bp.Name) ? bp.name : bp.Name));
                }
            }
        }

        private static void VerifyFeatureList(BaseUnitEntity unit, string[] ids, string label,
                                              List<string> bad, bool allowUnavailable)
        {
            if (ids == null) return;
            foreach (string raw in ids)
            {
                string id = raw != null ? raw.Trim() : "";
                if (string.IsNullOrEmpty(id)) { bad.Add(label + "含空 GUID"); continue; }
                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintFeature>(id);
                if (bp == null)
                {
                    if (!allowUnavailable) bad.Add(label + "解析不到 " + id);
                    continue;
                }
                if (!unit.Facts.Contains(bp))
                {
                    // keyTalents 是“有机会时优先”，只有当前已经满足前置却没落地才算失败。
                    if (!allowUnavailable || PrereqDiag.AllMet(unit, bp))
                        bad.Add(label + "缺 " + (string.IsNullOrEmpty(bp.Name) ? bp.name : bp.Name));
                }
            }
        }

        private static void VerifyGear(BaseUnitEntity unit, string[] entries, List<string> bad)
        {
            if (entries == null) return;
            var worn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var slot in unit.Body.AllSlots)
                {
                    var item = slot != null ? slot.MaybeItem : null;
                    if (item == null || item.Blueprint == null) continue;
                    string id = item.Blueprint.AssetGuid.ToString();
                    int n;
                    worn.TryGetValue(id, out n);
                    worn[id] = n + 1;
                }
            }
            catch (Exception e) { bad.Add("读取装备异常 " + e.Message); return; }

            foreach (string entry in entries)
            {
                if (string.IsNullOrEmpty(entry)) { bad.Add("装备表含空格"); continue; }
                bool available = false, found = false;
                foreach (string raw in entry.Split('|'))
                {
                    string id = raw != null ? raw.Trim() : "";
                    if (string.IsNullOrEmpty(id)) continue;
                    BlueprintItem bp = null;
                    try { bp = ResourcesLibrary.TryGetBlueprint<BlueprintItem>(id); } catch { }
                    if (bp == null) continue;
                    available = true;
                    int count;
                    if (!worn.TryGetValue(id, out count) || count <= 0) continue;
                    worn[id] = count - 1;       // 一件实际物品只能满足一个配置格
                    found = true;
                    break;
                }
                if (available && !found) bad.Add("装备候选格没有足够的实际件数: " + entry);
            }
        }

        private static void VerifyWeaponSets(BaseUnitEntity unit, List<string> bad)
        {
            try
            {
                bool mech = GearTool.HasBallisticMechadendrite(unit.Body);
                var sets = unit.Body.HandsEquipmentSets;
                if (sets == null) { bad.Add("没有武器套组"); return; }
                for (int i = 0; i < sets.Count; i++)
                {
                    var set = sets[i];
                    if (set == null) continue;
                    var main = set.PrimaryHand != null ? set.PrimaryHand.MaybeItem : null;
                    var off = set.SecondaryHand != null ? set.SecondaryHand.MaybeItem : null;
                    var mw = main != null ? main.Blueprint as BlueprintItemWeapon : null;
                    if (!mech && mw != null && mw.IsTwoHanded && off != null)
                        bad.Add("套组" + (i + 1) + " 双手主武器仍与副手并存");
                }
            }
            catch (Exception e) { bad.Add("武器槽检查异常 " + e.Message); }
        }

        private static void VerifyLegacyPsykerWeaponRepair(BaseUnitEntity unit,
                                                           ChainProbe.Archetype arch,
                                                           string[] gear, List<string> bad)
        {
            int beforeProblems = bad.Count;
            try
            {
                if (unit.Body == null || unit.Body.HandsEquipmentSets == null
                    || unit.Body.HandsEquipmentSets.Count < 2 || gear == null || gear.Length < 2)
                { bad.Add("旧灵能武器迁移：缺少两套手位或两条武器配置"); return; }

                BlueprintItemWeapon staffBp = null, pistolBp = null;
                foreach (string id in gear[0].Split('|'))
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintItemWeapon>(id.Trim());
                    if (bp != null) { staffBp = bp; break; }
                }
                foreach (string id in gear[1].Split('|'))
                {
                    var bp = ResourcesLibrary.TryGetBlueprint<BlueprintItemWeapon>(id.Trim());
                    if (bp != null) { pistolBp = bp; break; }
                }
                if (staffBp == null || pistolBp == null || !staffBp.IsTwoHanded)
                { bad.Add("旧灵能武器迁移：解析不到双手法杖/手枪"); return; }

                int beforeStaff = CountItemInstances(unit, staffBp);
                int beforePistol = CountItemInstances(unit, pistolBp);
                var sets = unit.Body.HandsEquipmentSets;
                Kingmaker.Items.Slots.HandSlot staffSlot = null, pistolSlot = null;
                for (int i = 0; i < sets.Count; i++)
                {
                    var s = sets[i];
                    if (s == null) continue;
                    if (s.PrimaryHand.MaybeItem != null && s.PrimaryHand.MaybeItem.Blueprint == staffBp)
                        staffSlot = s.PrimaryHand;
                    if (s.PrimaryHand.MaybeItem != null && s.PrimaryHand.MaybeItem.Blueprint == pistolBp)
                        pistolSlot = s.PrimaryHand;
                }
                if (staffSlot == null || pistolSlot == null)
                { bad.Add("旧灵能武器迁移：初始正确布局不存在"); return; }

                // 复现 1.7.100：把手枪插进法杖同组副手，原版会移除法杖。
                var brokenSet = staffSlot.HandsEquipmentSet;
                ItemEntity pistol = pistolSlot.MaybeItem;
                brokenSet.SecondaryHand.InsertItem(pistol);
                if (staffSlot.MaybeItem != null)
                { bad.Add("旧灵能武器迁移：未能构造法杖被副手挤掉的坏布局"); return; }

                GearTool.Equip(unit, arch);
                int afterStaff = CountItemInstances(unit, staffBp);
                int afterPistol = CountItemInstances(unit, pistolBp);
                if (afterStaff != beforeStaff || afterPistol != beforePistol)
                    bad.Add("旧灵能武器迁移复制/丢失物品：法杖 " + beforeStaff + "->" + afterStaff
                          + "，手枪 " + beforePistol + "->" + afterPistol);

                bool staffEquipped = false, pistolEquipped = false;
                for (int i = 0; i < sets.Count; i++)
                {
                    var s = sets[i];
                    if (s == null) continue;
                    if (s.PrimaryHand.MaybeItem != null && s.PrimaryHand.MaybeItem.Blueprint == staffBp)
                        staffEquipped = true;
                    if (s.PrimaryHand.MaybeItem != null && s.PrimaryHand.MaybeItem.Blueprint == pistolBp)
                        pistolEquipped = true;
                }
                if (!staffEquipped || !pistolEquipped)
                    bad.Add("旧灵能武器迁移未恢复：法杖=" + staffEquipped + " 手枪=" + pistolEquipped);
                if (bad.Count == beforeProblems)
                    Main.Log("  ✓ 旧灵能武器迁移：坏布局已恢复，法杖/手枪实例数未增加");
            }
            catch (Exception e) { bad.Add("旧灵能武器迁移异常 " + e.Message); }
        }

        private static int CountItemInstances(BaseUnitEntity unit, BlueprintItem bp)
        {
            int n = 0;
            if (unit == null || bp == null || unit.Inventory == null || unit.Inventory.Collection == null) return n;
            foreach (var item in unit.Inventory.Collection.Items)
                if (item != null && item.Blueprint == bp) n++;
            return n;
        }

        private static void VerifyBrain(BaseUnitEntity unit, Case c, List<string> bad)
        {
            string want = c.Elite != null && !string.IsNullOrEmpty(c.Elite.BrainId)
                        ? c.Elite.BrainId : c.Arch.BrainId;
            if (string.IsNullOrEmpty(want)) return;
            string got = null;
            try { got = unit.Brain != null && unit.Brain.Blueprint != null
                      ? unit.Brain.Blueprint.AssetGuid.ToString() : null; } catch { }
            if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase))
                bad.Add("brain " + (got ?? "无") + " != " + want);
        }

        private static void DestroyCurrent()
        {
            BaseUnitEntity unit = _current;
            _current = null;
            if (unit == null) return;
            try { AnimFallback.ForgetMeleeElite(unit); } catch { }
            try { unit.Remove<UnitPartFollowUnit>(); } catch { }
            try { unit.Remove<UnitPartCompanion>(); } catch { }
            try { unit.IsInGame = false; } catch { }
            try { Game.Instance.EntityDestroyer.Destroy(unit); } catch (Exception e) { Main.LogError("[卫队矩阵] 销毁样本失败: " + e.Message); }
            try { Game.Instance.EntityDestroyer.Tick(); } catch (Exception e) { Main.LogError("[卫队矩阵] 销毁队列失败: " + e.Message); }
        }

        private static void Finish()
        {
            bool ok = false;
            try
            {
                int pass = 0, fail = 0;
                foreach (var r in Results) { if (r.Passed) pass++; else fail++; }
                int left = RetinueRegistry.All(true).Count;
                ok = fail == 0 && left == 0 && Results.Count == Cases.Count;
                if (!WriteReport()) ok = false;
                Main.Log("======== 卫队全路线矩阵结束：PASS " + pass + " / FAIL " + fail
                       + " / 未执行 " + (Cases.Count - Results.Count) + " / 清理残留 " + left + " ========");
                if (ok) Main.Log("  ✓ 全部样本通过，且测试实体已清空。");
                else Main.LogError("  ✗ 矩阵未通过；不要把本次结果当成发布门禁。明细见 guard_matrix.tsv");
            }
            catch (Exception e) { Main.LogError("[卫队矩阵] 汇总失败: " + e); }
            finally
            {
                RestoreSettings();
                _running = false;
                Action<bool> done = _completed;
                _completed = null;
                if (done != null) done(ok);
            }
        }

        private static void Abort(string reason)
        {
            Main.LogError("[卫队矩阵] 中止：" + reason);
            try { DestroyCurrent(); RetinueRegistry.DismissAll(); } catch { }
            int left = -1;
            try { left = RetinueRegistry.All(true).Count; } catch { }
            if (left != 0) Main.LogError("[卫队矩阵] 中止清理后仍残留 " + left + " 个卫兵/墓碑实体。请勿存档。");
            try { WriteReport(); } catch { }
            RestoreSettings();
            _running = false;
            Action<bool> done = _completed;
            _completed = null;
            if (done != null) done(false);
        }

        /// <summary>mod 禁用/热卸载前的同步收尾；必须在 Deferred.Shutdown 之前调用。</summary>
        internal static void CancelAndCleanup(string reason)
        {
            if (!_running) return;
            // 停用时不能再回调 FullTest 调度下一段；Deferred 紧接着会被 Shutdown。
            _completed = null;
            Abort(string.IsNullOrEmpty(reason) ? "外部取消" : reason);
        }

        private static bool WriteReport()
        {
            try
            {
                var sb = new StringBuilder("result\tlabel\tunit\tlevel\tpaths\tbrain\tgear\tproblems\n");
                foreach (var r in Results)
                    sb.Append(r.Passed ? "PASS" : "FAIL").Append('\t')
                      .Append(Clean(r.Label)).Append('\t').Append(Clean(r.Unit)).Append('\t')
                      .Append(r.Level).Append('\t').Append(Clean(r.Paths)).Append('\t')
                      .Append(Clean(r.Brain)).Append('\t').Append(Clean(r.Gear)).Append('\t')
                      .Append(Clean(r.Problems)).AppendLine();
                string path = Path.Combine(Main.ModEntry != null ? Main.ModEntry.Path : ".", "guard_matrix.tsv");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Main.Log("  -> " + path);
                return true;
            }
            catch (Exception e)
            {
                Main.LogError("[卫队矩阵] 写 guard_matrix.tsv 失败: " + e.Message);
                return false;
            }
        }

        private static string Clean(string value)
        {
            return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
