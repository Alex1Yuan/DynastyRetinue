using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items;
using Kingmaker.Blueprints.Items.Weapons;

namespace DynastyRetinue
{
    /// <summary>
    /// 阿斯塔特装备探测：这些原版物品到底是什么规格。
    ///
    /// ★为什么要探★
    ///   要回答三个问题，而它们都只能实机看：
    ///     ① 双手能不能都拿近战 —— 取决于武器是不是 TwoHanded、以及有没有
    ///        专门的限制组件（包里有个 AstartesCombatKnife_Restriction，可疑）；
    ///     ② 阿斯塔特能穿的装备到底有几件（游戏里确实少）；
    ///     ③ 想调数值的话，先得知道原值是多少。
    ///
    /// ★为什么不建议克隆物品★
    ///   克隆 = 新蓝图 = 新 AssetId。装到卫兵身上再存盘，那个 id 就进了
    ///   party.json；玩家卸载 mod 后读档，反序列化找不到它，**存档永久打不开**
    ///   （那处没有 try/catch）。整个 mod 到现在一个自建蓝图都没有。
    ///   要改数值就改这些原版物品本身 —— 存档里记的仍是原版 GUID，
    ///   卸载后自动恢复原值，零残留。这些又基本只有阿斯塔特会用，
    ///   全局影响很小。
    ///
    /// 只读。这个探测本身不改任何东西。
    /// </summary>
    public static class ItemProbe
    {
        private static readonly (string Name, string Id)[] Items =
        {
            // 近战
            ("AstartesXenophaseSword", "0ccfe3de0a0f46a09e5b21c032a21dc4"),
            ("AstartesPowerSword",     "3ca6a5ac041843e387be01edff1ea1be"),
            ("AstartesChainsword",     "4d820ce4c3134a44bc83bc72af32638c"),
            ("AstartesChainswordT2",   "87c135cffa7747e1894bdb8207f28ae3"),
            ("AstartesChainAxe",       "c225669f42784fe5852fe134e2768b56"),
            ("AstartesChainAxeT2",     "e9186ff71fd5477c9bbf092ba1600018"),
            ("AstartesCombatKnife",    "88de523b22324c0b84373ac545f344cb"),
            ("FrostAxe",               "67db0f23724442fbae91a21191114c54"),
            ("FrostAxeCH5Unique",      "5e536199ce074052babb98226bbd29fc"),
            // 远程
            ("AstartesBolter",         "0b1a6f7920114dafb5ffc2bdea37bf8d"),
            ("AstartesStormBolter",    "3415d3e981cc4c10a573e6fc2e06ddf8"),
            ("AnnihilatorAstartesBolter","781b90112a784f03843bb8faa34d1ae7"),
            ("AstartesBoltPistol",     "5e1bae4c2c7e4bd99411173f8dbe74f0"),
            ("AstartesBoltPistolT2",   "1b4c97da14ca466987603b2e8587fb9f"),
            ("AstartesPlasmaPistolT0", "51334a2918e64ceab33b2ad032bc74a4"),
            ("AstartesPlasmaPistolT1", "d01d6fc678f34bc8a7c6851fbc985221"),
            ("AstartesFlamer",         "8003fbd84d5c44b0808440983a34b6d5"),
            ("AstartesFlamerT2",       "52a21c0ec9e447cba63c6c50a6c52fdc"),
            ("GenocideAstartesFlamer", "a7b71b397d8f437cad9442b53cc6fdad"),
            ("MultiMelta",             "3ef5c9b1be5c44ef900d06f78ef1c641"),
            ("MultiMelta_DLC3V",       "d30426fa78e24904a9bd2e3694b4d389"),
            // 护具
            ("UlfarArmor",             "a793aa6d59704a369c1d10a445a6eb80"),
            ("PowerBoots",             "1e436fcc4bb24969baa184be84784095"),
            ("PowerGloves",            "e78897b9b9514aed828d11131ef81c24"),
            ("CompensatorAstartesGloves","93dd7ab998aa4676b78281792ea619e8"),
            ("WolfPeltCloak",          "34abddeecf7a4024974d364e15ee7637"),
            // ---- 占位/测试物品：能不能拿来当"新装备"的载体 ----
            // ★这是绕开存档红线的关键★ 它们的 GUID 是**原版**的，
            //   所以卸载 mod 之后存档里那条记录仍然能正常反序列化 ——
            //   那件装备只是退回原始（空白）数值。是降级，不是损坏。
            //   克隆一份新蓝图就没这个性质：新 AssetId 卸载后找不到，存档打不开。
            // 要确认三件事：① 数值原始状态 ② 有没有模型/图标 ③ 是不是真没人用
            //   （③ 离线查不了，引用关系在压缩的蓝图正文里）
            ("p_TestAreaPC_Melee",     "df9f8d855ea348a1a3f7a9ef8ebf89e6"),
            ("p_TestAreaPC_Ranged",    "d40596aa846a4b5d926814ccb64939b8"),
            ("p_Axe10test",            "524fea4803af43f4beebc5bd3bccf797"),
            ("p_Autogun10test",        "1152a4e51ee7456e922ac8be387f14fb"),
            ("p_ShotgunTest_Proto",    "32a6b98335b5466d9ba9315e4084f567"),
            ("p_ShotgunTest_Proto2",   "0e192a942a344182a04d606216a58c5a"),
            ("p_GrenadeTest",          "d898e300a5fc453e9c5d9945b0a10d49"),
            ("p_ShieldForTest",        "9b2aa2e4ae5d479597cf343e283308c7"),
            ("p_ArbitesArmour_Test",   "eb4b7898399d4de28b71cf66ee498e52"),
            ("p_TestArea_Neck",        "6a2bee0e3a4b48fd854f5a2ad2d6d8ef"),
            ("p_HeinrixDaggerPlaceholder","de3c1dc8c47a4d37897a05939dcad47a"),
            ("p_TestHerald",           "deb6d03fc0084423974c66769bb7f4cd"),
            ("p_Obsolete_DigitalLasRing","825880d91b2c4c3197305d733ecf59c2"),
            ("p_TEST_TurretWeapon",    "e95d7937f9a6442183c3b15b57824c91"),
            ("p_TestMSW_Banner1_Staff","33771513246e4a67b051843d7f646bba"),
            ("p_SomeLoreWings_Test",   "d8fe2d68c1da445c9b10dec4275770c0"),
            ("p_Eufrates_BeamOfChange_test","377a97f2dab24f8f934155ea495a6f40"),
        };

        public static void Run()
        {
            try
            {
                Main.Log("========== 阿斯塔特装备探测 ==========");
                foreach (var it in Items)
                {
                    object raw = null;
                    try { raw = ResourcesLibrary.TryGetBlueprint(it.Id); } catch { }
                    if (raw == null) { Main.Log($"  [{it.Name}] 找不到 {it.Id}"); continue; }

                    var bp = raw as BlueprintItem;
                    if (bp == null)
                    {
                        Main.Log($"  [{it.Name}] 不是 BlueprintItem —— {raw.GetType().Name}");
                        continue;
                    }

                    string extra = "";
                    var w = bp as BlueprintItemWeapon;
                    if (w != null)
                    {
                        // ★这几项决定"能不能双手各拿一把近战"★
                        //   IsTwoHanded 为 true 的话副手位天然放不下（GearTool 里也是这么判的）。
                        string hands = "?", fam = "?", dmg = "?";
                        try { hands = w.IsTwoHanded ? "双手" : "单手"; } catch { }
                        try { fam = w.Family.ToString(); } catch { }
                        try { dmg = $"{w.WarhammerDamage}　穿甲{w.WarhammerPenetration}"; } catch { }
                        extra = $"  {hands}  族={fam}  伤害={dmg}";
                    }

                    // 组件列表里能看出限制类的东西（种族/熟练度/装备条件）
                    string comps = "";
                    try
                    {
                        var cs = bp.ComponentsArray;
                        if (cs != null && cs.Length > 0)
                            comps = "  组件=" + string.Join(",", cs.Where(c => c != null)
                                .Select(c => c.GetType().Name).Distinct().Take(6));
                    }
                    catch { }

                    string dn = "?";
                    try { dn = bp.Name; } catch { }
                    // 占位物品经常没配模型/图标 —— 装上去会是隐形武器。
                    // 这两项决定它能不能直接用，还是得先指到原版模型上。
                    string art = "";
                    try { art += bp.Icon != null ? "图标✓" : "图标✗"; } catch { art += "图标?"; }
                    try
                    {
                        var v = bp.GetType().GetProperty("VisualParameters")?.GetValue(bp);
                        art += v != null ? " 外观✓" : " 外观✗";
                    }
                    catch { art += " 外观?"; }
                    Main.Log($"  [{it.Name}] {dn}　{bp.GetType().Name.Replace("Blueprint","")}{extra}  {art}{comps}");
                }
                Main.Log("========== 装备探测结束 ==========");
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError(e); Main.FlushLog(true); }
        }
    }
}
