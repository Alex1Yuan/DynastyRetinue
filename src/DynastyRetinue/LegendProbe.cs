using System;
using System.Collections.Generic;
using System.Text;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem;                 // GetHealthOptional 的扩展方法所在
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.Items;                        // ItemEntity / PartUnitBody 的槽位
using Kingmaker.UnitLogic.Parts;              // PartHealth
using Kingmaker.UnitLogic.Progression.Paths;  // BlueprintCareerPath
using UnityEngine;

namespace DynastyRetinue
{
    /// <summary>
    /// 传奇档候选单位的血量实测。**开发按钮，一次性用完即弃。**
    ///
    /// ★为什么必须生成才能测★
    ///   血量不是 BlueprintUnit 上的字段 —— GetDefaultLevel() 是把组件里的 Ranks
    ///   加起来算的，血量同理由组件 + 属性推导。所以 tools/units_full.tsv
    ///   （3069 条全量表）能给出体型/brain/种族/阵营，唯独给不了血量。
    ///   而 UnitInspect 读的 GetHealthOptional 是**实体**上的，必须先有实体。
    ///
    /// ★为什么不能靠玩家跑图看★
    ///   候选分散在血鸦收藏库、乌尔法任务线、DLC3 等不同场景，
    ///   UnitInspect 又只列当前区域 —— 要凑齐得跑遍整个流程，不现实。
    ///   生成→读→立刻销毁，一次点击在任何地方都能拿到全部数字。
    ///
    /// ★为什么要等帧★
    ///   EntitySpawner.SpawnUnit 是**延迟入册**的（EntitySpawnController 只往
    ///   m_ToSpawn 里加，要到下一次 Tick 才真正进 state），当场读血量会拿到空。
    ///   等两帧再读，与 RetinueTest.SpawnOne 的做法一致。
    ///
    /// ★安全性★
    ///   · 只在开发模式可见，发布包里点不到。
    ///   · 战斗中直接拒绝 —— 凭空生成几个 boss 会打乱回合序。
    ///   · 生成后立刻销毁，不入我们的名册（不调 RetinueRegistry 的任何登记），
    ///     所以既不占名额，也不会被卫兵的各种补丁处理。
    ///   · 联机中直接拒绝 —— 单边生成实体会把随机流推快一格，
    ///     那是 CoopCommand 头注里记的那条红线。
    /// </summary>
    internal static class LegendProbe
    {
        /// <summary>候选清单。guid 来自 tools/units_full.tsv，体型/brain 那一栏是表里的值，用来核对。</summary>
        private static readonly string[,] Candidates =
        {
            // guid                                 显示标签                      表里的体型
            { "88651654158644c699669d2ecb1ebc94", "阿斯塔特·血鸦（佐拉尔）",      "Large" },
            { "3fd86919340247f7a2d510b5d91f0258", "阿斯塔特·太空野狼 Halbrandt",  "Large" },
            { "4b81380dc07245849efcf0bc6572025e", "阿斯塔特·太空野狼 axe（对照）", "Large" },
            { "60d35aaa53f6411bb05e8abdd4ec87e3", "阿斯塔特·乌尔法 Ch05",         "Large" },
            { "56c0155a432f4e8593bd542ca92c860c", "恶魔引擎·Helbrute 乌尔法任务",  "Gargantuan" },
            { "25b8a0bbb9e0420d8e97a1ee78749bc2", "恶魔引擎·Helbrute Lair01",     "Gargantuan" },
            { "94f1c2b53a9e4900a1c1e307da6f026e", "恶魔引擎·Helbrute 展品",        "Gargantuan" },
            { "da50661ad29a47318662acc75c3bb7d0", "恶魔引擎·Defiler 电击迷你boss", "Gargantuan" },
            { "95d579779b2e467eb411daf200d6355d", "恶魔引擎·Defiler 原型",         "Gargantuan" },
            { "09ce003f693e4b2d8bc1a3815ad56c46", "恶魔引擎·ForgeFiend（对照）",   "Huge" },
            { "5bc8b3a8fb834977a3692a2325aff0f6", "灵能·DLC3 审判官",             "Medium" },
            { "bd3f39c5ab5649c8a27e49517ed6d5c0", "灵能·DLC3 审判官 死灵线",       "Medium" },
        };


        /// <summary>
        /// 机械教线的候选 —— 用来实测**先天能力**（不是血量，血量已经测过了）。
        ///
        /// ★为什么要测这个★ 我们要把先天能力做成按阶位替换（grantFeaturesT1/T2/T3），
        ///   但蓝图 AddFacts 里到底挂了什么，离线抽不出来（要解析组件表）。
        ///   生成出来读实体上的 Features / Abilities 是最直接的办法。
        /// </summary>
        private static readonly string[,] MechLine =
        {
            // ★1.6.7 加：找一个 prefab 里带触须的教条侧模型★
            //   实测:HeretekMagi 的 prefab 触须是烘在网格里的（借过来也在），
            //   DarkMagos 的没有。数据里分不出哪个是烘死的、哪个是装备挂的 —— 只能看。
            //   下面这些都在「反查带触须的单位」名单里（引用 MechadendriteCoilItem 的共 56 个），
            //   摆出来一眼就能挑出哪个背后真有管子。
            { "69d40ecaf9354e758a312d9e3ed23c0d", "触须候选·Opticon22 奥普蒂孔22号",     "Medium" },
            { "82c9efb12739f3342815786f19c81b5f", "触须候选·AgriWorldMagus 农业世界贤者", "Medium" },
            { "eeaee064bfda4f1a822ce57bc8265412", "触须候选·TechPriest_RTPalace 宫殿技师","Medium" },
            { "db965c01bf4d47a892ce417ac635915d", "触须候选·FabricatorsLeutenant 工造副官","Medium" },
            { "2869eb5bcf614796b11d8cfc1089ec87", "触须候选·AssassinTechpriest 刺客技师", "Medium" },
            { "ddb53592cea949d98d85c738af61a3e1", "触须候选·MechanicusMob 机械教众",      "Medium" },
            // ★1.7.1 换底子后的四个关键对照★ 前两个是新底子，后两个是旧底子。
            //   血量只能实机测（CheckNewStatsComponent 是运行时算的，离线读不到），
            //   而换底子最大的风险正是「模型挑对了、结果只有几十血」——
            //   裁判庭狙击手那次就是这么栽的（FootfallAnverSniper 96 血）。
            //   妮赫尔应该没问题：她有击杀目标(Obj4_KillBrassWhisper)、半血阶段变体
            //   (BrassWhisper50Percent)和战斗脑，是正经 boss —— CutsceneNeutrals
            //   只是说她默认不敌对，不代表她是摆件。但数字还是要看。
            { "ca936a024b954b188d2bd397e6ea49d3", "★新底子·教条 BrassWhisper 妮赫尔",        "Medium" },
            { "287d7a4d2bb146998dc450cb3eccee78", "★新底子·异端 PasqalQuest_Dements 德曼兹", "Medium" },
            { "2711d4883bb24692afaf1e8a3ab4335f", "旧底子·教条 DarkMagos（对照）",           "Medium" },
            { "e8237ec331214eac8251661293e7796b", "旧底子·异端 HeretekMagi 306（对照）",     "Medium" },
            { "4c77d8f0acc2473cadd14ae869aac708", "T1候选·技工（教条）152",          "Medium" },
            { "737fc140e70f4ac08b642a67deef64a4", "T2候选·护教军游骑兵（教条）297",   "Medium" },
            { "bd8e6264794945cbab40c5201b5fb6f3", "T3候选·高阶助祭阿尔瓦-9 423",      "Medium" },
            { "aa02b505be774674ae924f19dc17e6f6", "精英候选·斯卡里安锈色追踪者 308",  "Medium" },
            { "6ab30fcb20954e10be43344314511ca6", "精英候选·匠人德卡巴洛斯 434",      "Medium" },
            { "5ba73a4183c24e909d691acabadc1db8", "炮灰候选·战斗仆从",               "Medium" },
            // ★补齐 DLC3 机械教全部 13 个★ 前一轮只测了 6 个，剩下这些一直是盲区。
            //   T3 候选尤其重要：高阶助祭和匠人都是有名有姓的角色，天然该当精英，
            //   T3 得另找一个。而 T3 只是**借模型**（阶位是职业等级，共用同一个 unit 蓝图），
            //   所以候选自己多少血完全不影响卫兵强度 —— 只看长得像不像。
            { "ab131771270542b69fb7a687062b39c0", "T3候选·异端电僧 ElectroPriest",   "Medium" },
            { "2fae43ca36204db7aa9a961536fc21d3", "T3候选·异端工头 ChiefWorker",     "Medium" },
            { "5f393bd508864d0caa0d0bf1448425e2", "参考·异端工人 Worker",            "Medium" },
            { "a92cdde1068c4609b437cd5a22f8b3b0", "炮灰·教条仆从 Servitor",          "Medium" },
            { "d74728f897ef4b0a85978e25f43ea155", "炮灰·教条战斗仆从",               "Medium" },
            { "2af759a6dba447dca72494973763d3c0", "炮灰·异端战斗仆从",               "Medium" },
            // ★对照组：现有五条线的基础单位★
            //   加它们是为了回答一个具体问题：「精英」(EliteMobFeature) 该不该发给机械教 T3。
            //   那条特性是野怪战力加成（较高伤害 + 护甲穿透 + 闪避/招架减免），
            //   给了就是直接加强。但如果现有五条线的基础单位本来就带着它，
            //   那机械教加上只是**持平**而不是特权 —— 这个差别只能靠对照看出来，
            //   光看机械教那五个单位永远得不出答案。
            { "270b3e09cf424209b126b236f1655108", "对照·近战 DL_Guard_Melee_Ally",   "Medium" },
            { "36e39788d1c648c8a75fbf36371f35f9", "对照·狙击 DL_Guard_Sniper",       "Medium" },
            { "5fc80452fb6a4e2db02cd0a305715446", "对照·连射 Sororitas_HBolter",     "Medium" },
            { "1df7ad7561ca47a08a2c2b0a8152871e", "对照·灵能 OfficersDeckGuardAstro","Medium" },
            { "1fb60c0ef5fe459980c34a271dfad088", "对照·军官 OfficersDeckGuard",     "Medium" },
            // ★新发现的贤者级候选★ 之前那 16 个是手挑的，漏了这几个。
            //   教条精英2 一直缺干净人选（本体的贤者不是腐化就是疯狂），这几个补上了空缺。
            { "a408822526b74651960ecc431d85bdb0", "教条精英2候选·高阶机械技师 Opticon-22", "Medium" },
            { "a21c99571096411db69058d094ae97a1", "教条精英2候选·军官甲板技术神甫",        "Medium" },
            { "7effee518fdb44d28c7001f5ecaf4507", "教条精英2候选·佛特佛技术神甫",          "Medium" },
            { "89ad4f8695d04684bda5954a1fe3385e", "教条精英2候选·佛特佛机械技师",          "Medium" },
            { "e8237ec331214eac8251661293e7796b", "异端精英2候选·异端贤者 HeretekMagi",     "Medium" },
        };

        /// <summary>
        /// 把几个关键特性的**组件和数值**打出来。
        ///
        /// ★为什么必须读组件而不是读说明文字★
        ///   杂兵/精英 的中文说明里**两个都写着"加值"**：
        ///     精英「造成较高伤害，拥有**一定**护甲穿透、闪避减免与招架减免加值」
        ///     杂兵「造成的伤害较低，拥有**少量**护甲穿透、闪避减免与招架减免加值」
        ///   读起来杂兵完全可能只是「更小的正面加值」而不是负面标签。
        ///   若真如此，「去掉杂兵」会让卫兵**变弱** —— 与意图相反。
        ///   而这个决定要动所有老玩家存档里的卫兵，不能拿有歧义的文案当依据。
        /// </summary>
        private static readonly string[,] FeatureProbe =
        {
            { "ae9bfd6223f64a81b3fb4bc5b964d044", "杂兵 TroopMobFeature" },
            { "64c1061530f84fc68a17061c067c4928", "精英 EliteMobFeature" },
            { "a9878a4fc7b84726ac71fe5f6c46fe45", "摒弃血肉2" },
            { "a43f63e888a941efbdb3b5ea3ad7bfee", "摒弃血肉3" },
            { "ab03e788cbdc4c63b416d5bd770dadf9", "摒弃血肉4" },
            // ★机械触须的装备限制★ 作者怀疑它们要「某个植入物 + 起源天赋」才装得上。
            //   若真有 EquipmentRestriction 之类的组件，会在这里显形 ——
            //   那就决定了「给 T3 补触须造型」这条路走不走得通。
            { "6701580dba564550a3f5839a271ad8ac", "物品·MechadendriteCentralItem" },
            { "82526c450b3446e186c82aa82ebd51d4", "物品·MechadendriteCoilItem" },
            { "f0996a1c715a403296177d8833d16511", "物品·MechadendriteUtilityItem" },
            { "af44851d14bb4ee98cff1fb6a67083df", "物品·弹道机械触须" },
            // ★1.5.88 加这两条★ 卫兵右键看不到防御属性（不是队伍成员，原版检视面板残缺），
            //   而「招架」不是 StatType 里的字段、是 RuleCalculateParryChance 算出来的结果，
            //   没有一个数可以直接读。所以改从**组件字段**入手，一次点击回答两个问题：
            //   ① Parry25 挂的到底是哪个组件、加多少 —— 1.5.73 给错过一次
            //      （Parry10 挂的是 Target/Attacker 侧，那是攻击方视角，给自己加不到招架）
            //   ② AutoStriking 的四个触发开关（Dodge/Parry/Cover/Block）分别是什么
            //      —— 防御反击实测求值 0 次，可能是"没被近战打到"，
            //         也可能是"发生的事件类型不在它的开关里"。这两者靠计数分不开。
            { "da338f7caf3e44ddac7da84ee6f2ddc9", "招架25（现用）" },
            { "53c19a9468d24539863989b3be9ed1f5", "招架10（1.5.73 用错的那个，做对照）" },
            { "a3c5541e26f5428f853318342edcbbf4", "自主攻击能量剑·防御反击" },
            { "d1a76d2a304241ff926a15b3d34ec360", "命运使者特性·无视护甲" },
        };

        public static void DumpFeatureComponents()
        {
            try
            {
                if (!Main.DevMode) { Main.Log("[特性组件] 仅开发模式可用。"); return; }
                var sb = new StringBuilder();
                sb.AppendLine("======== 关键特性的组件与数值 ========");
                sb.AppendLine("  ★看每个组件的字段值，判断是加值还是减值、幅度多少★");

                for (int i = 0; i < FeatureProbe.GetLength(0); i++)
                {
                    string guid = FeatureProbe[i, 0], label = FeatureProbe[i, 1];
                    sb.AppendLine();
                    sb.AppendLine("  ── " + label + " ──");
                    Kingmaker.Blueprints.SimpleBlueprint bp =
                        ResourcesLibrary.TryGetBlueprint<Kingmaker.Blueprints.Facts.BlueprintUnitFact>(guid);
                    // 物品不是 BlueprintUnitFact，得单独试一次 —— 机械触须走的是这条
                    if (bp == null) bp = ResourcesLibrary.TryGetBlueprint<Kingmaker.Blueprints.Items.BlueprintItem>(guid);
                    if (bp == null) { sb.AppendLine("    解析不到蓝图。"); continue; }

                    var comps = (bp as Kingmaker.Blueprints.BlueprintScriptableObject) != null
                              ? ((Kingmaker.Blueprints.BlueprintScriptableObject)bp).ComponentsArray : null;
                    if (comps == null || comps.Length == 0) { sb.AppendLine("    （没有组件）"); continue; }

                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        var t = c.GetType();
                        sb.AppendLine("    " + t.Name);
                        // 把组件上的公有/私有实例字段全打出来 —— 数值就藏在这里
                        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public
                                                    | System.Reflection.BindingFlags.NonPublic
                                                    | System.Reflection.BindingFlags.Instance))
                        {
                            object v = null;
                            try { v = f.GetValue(c); } catch { }
                            string s = v == null ? "null" : v.ToString();
                            if (s.Length > 90) s = s.Substring(0, 90) + "…";
                            sb.AppendLine("        " + f.Name + " = " + s);
                        }
                    }
                }
                Main.Log(sb.ToString());
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError("[特性组件] 失败: " + e.Message); }
        }

        /// <summary>
        /// 本体（非 DLC3）机械教主题候选。**当 T3 的皮完全可用**——外观只是借模型，
        /// 不需要 DLC3，所有玩家都有。血量那一列是 1.5.18 那轮实测值，仅供参考：
        /// 外观不影响卫兵强度（阶位是职业等级，共用同一个 unit 蓝图）。
        /// 名字里带「腐化 / 疯狂 / 混沌 / 附身」的是黑化款，配教条线会串味，标注出来。
        /// </summary>
        private static readonly string[,] BaseGame =
        {
            { "4eeb0a3e89fd47e59bae8759c530329d", "本体·塔祖斯技工精英 123（干净）",     "Medium" },
            { "14c191894a4245149eebe8ac911d555c", "本体·不朽护教军殉道者 330（干净）",   "Medium" },
            { "84a354589be241598c28289bfcbd7c13", "本体·不朽护教军射手 297（干净）",     "Medium" },
            { "3b59e14549cf4cdeb75a20fad85a748c", "本体·塔祖斯护教军近战 38（干净）",    "Medium" },
            { "d6c51b2891764ae1b177d65623066f37", "本体·塔祖斯护教军远程 33（干净）",    "Medium" },
            { "3c5c7c0e9c87489dafba65a45961a4db", "本体·疯狂贤者 206（疯狂）",           "Medium" },
            { "2711d4883bb24692afaf1e8a3ab4335f", "本体·技术神甫 DarkMagos 199（黑化）", "Medium" },
            { "1b2ad836f8cd437790f32a6c269110ed", "本体·腐化技术神甫 489（腐化）",       "Medium" },
            { "906197899b9d40d8b1f0189a57fbd7b2", "本体·腐化贤者 216（异端技师）",       "Medium" },
            { "d8e940ea190a47d6b2d4e3ce9203f058", "本体·吉伦塔泽塔贤者 935（剧情友方）", "Medium" },
            { "9c409b4c473f454b91085d56264acb76", "本体·损坏的锈色追踪者 368（混沌）",   "Medium" },
            { "0a7e17bde9c943a6a4db4362da861ee1", "本体·疯狂技工 49（疯狂）",            "Medium" },
            { "f5de71120c8a46b48b38e47dec7073de", "本体·腐化技工 58（腐化）",            "Medium" },
            { "0a51a4a887db48a1bc66aa8b4d03e008", "本体·被附体的下士 40（附身）",        "Medium" },
            { "ac64204409e7448e9204f74b4913f318", "本体·腐化的护教军 36（腐化）",        "Medium" },
            { "93420269f45f4b45a8cb376ed01c298b", "本体·教士狂乱逻辑（狂乱）",           "Medium" },
        };

        /// <summary>
        /// ★一次导全★ DLC3 机械教 13 个 + 本体候选 16 个 + 现有五线对照 5 个。
        ///
        /// 为什么单独做这个：之前每加几个候选就要作者重启进游戏点一次，
        /// 而每次只拿到一部分信息（先有血量没特性、后有特性没装备），
        /// 结果同一批单位来回测了四五轮还对不齐。一次全导，之后查文件就行。
        /// </summary>
        public static void ExportAll()
        {
            _exportToFile = true;
            Run(BuildAllSet(), false, true);
        }

        /// <summary>把导出集**摆在地上**供查看模型（分排摆，见 ShowPerRow）。
        /// 看完请点「清理候选」—— 区域实体会进存档，别带着它们存盘。</summary>
        public static void ShowAll()
        {
            var set = BuildAllSet();
            Run(set, true);
        }

        /// <summary>导出集 = DLC3 机械教 13 + 本体候选 16 + 现有五线对照 5。</summary>
        private static string[,] BuildAllSet()
        {
            var all = new List<string[]>();
            for (int i = 0; i < MechLine.GetLength(0); i++)
                all.Add(new[] { MechLine[i, 0], MechLine[i, 1], MechLine[i, 2] });
            for (int i = 0; i < BaseGame.GetLength(0); i++)
                all.Add(new[] { BaseGame[i, 0], BaseGame[i, 1], BaseGame[i, 2] });
            var set = new string[all.Count, 3];
            for (int i = 0; i < all.Count; i++)
                for (int j = 0; j < 3; j++) set[i, j] = all[i][j];
            return set;
        }

        /// <summary>本轮结果是否额外写一份到文件（导全时用）。</summary>
        private static bool _exportToFile;

        /// <summary>
        /// 导出**当前队伍成员**的完整装备与植入物，写 party_gear.txt。
        ///
        /// ★为什么读实况而不是读蓝图★
        ///   帕斯卡/绮贝菈的蓝图版本是**序章形态**（帕斯卡 70 血、绮贝菈 75 血），
        ///   身上是新手装。而我们要拄的是**毕业装备** —— 那只存在于作者自己的存档里。
        ///   现有五条线的配表注释里写着“直接抄你存档 Manual_25 里阿贝拉德本人的”，
        ///   走的就是这条路，这个按钮只是把它自动化。
        /// </summary>
        public static void ExportPartyGear()
        {
            try
            {
                if (!Main.DevMode) { Main.Log("[队伍装备] 仅开发模式可用。"); return; }
                var game = Game.Instance;
                if (game == null || game.Player == null) { Main.LogError("[队伍装备] 取不到 Player。"); return; }

                var sb = new StringBuilder();
                sb.AppendLine("======== 队伍成员的装备与植入物（实况）========");
                sb.AppendLine("  拿它填 gear 表：精英=毕业装备，T3=准毕业，T2/T1 递减。");
                sb.AppendLine();

                int n = 0;
                foreach (var u in game.Player.PartyAndPets)
                {
                    if (u == null) continue;
                    n++;
                    string hp = "?";
                    try { var h = u.GetHealthOptional(); if (h != null) hp = h.MaxHitPoints.ToString(); }
                    catch { }
                    int lv = -1;
                    try { if (u.Progression != null) lv = u.Progression.CharacterLevel; } catch { }
                    sb.AppendLine("  ── " + (u.CharacterName ?? "?") + "　lv" + lv + "　血量 " + hp + " ──");

                    var body = u.Body;
                    if (body == null || body.AllSlots == null) { sb.AppendLine("      （取不到 Body）"); continue; }
                    var rows = new List<string>();
                    foreach (var slot in body.AllSlots)
                    {
                        if (slot == null) continue;
                        ItemEntity it = null;
                        try { it = slot.MaybeItem; } catch { }
                        if (it == null || it.Blueprint == null) continue;
                        rows.Add(string.Format("      {0,-22} {1,-34} \"{2}\",",
                                 slot.GetType().Name,
                                 Nm(it.Blueprint.Name, it.Blueprint.name),
                                 it.Blueprint.AssetGuid));
                    }
                    if (rows.Count == 0) sb.AppendLine("      （空）");
                    else { rows.Sort(StringComparer.Ordinal); foreach (var r in rows) sb.AppendLine(r); }
                    sb.AppendLine();
                }
                sb.AppendLine("  共 " + n + " 名队伍成员。");

                Main.Log(sb.ToString());
                try
                {
                    string path = System.IO.Path.Combine(
                        Main.ModEntry != null ? Main.ModEntry.Path : ".", "party_gear.txt");
                    System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
                    Main.Log("[队伍装备] 已写入 " + path);
                }
                catch (Exception e2) { Main.LogError("[队伍装备] 写文件失败: " + e2.Message); }
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError("[队伍装备] 失败: " + e.Message); }
        }

        public static void Run() { Run(Candidates, false); }
        public static void DumpMechFacts() { Run(MechLine, false, true); }

        /// <summary>
        /// 卫兵手里每把武器的**族/类/穿甲/附魔**，外加这个单位实际拥有的技能清单。
        /// 一次点击导出到 weapon_facts.txt。
        ///
        /// ★为什么必须实机读，离线读不到★
        ///   Family/Category 虽然是 BlueprintItemWeapon 上的字段，离线 blob 里能翻到字节，
        ///   但枚举值的偏移要靠猜；而**附魔**分两层 —— 蓝图上的 m_Enchantments 是"出厂自带"，
        ///   ItemEntity.Enchantments 是"这一件实例身上现在挂着的"，后者只有运行时存在。
        ///   我们要判断能不能给卫兵单独加附魔，看的正是后者。
        ///
        /// ★这个导出回答三个待定问题★
        ///   ① 静电臂铠 / 跨音速利刃的 Family 是不是 Blade —— 决定"要不要打长剑判定补丁"。
        ///      若本来就是 Blade，那整个补丁不用写。
        ///   ② 两把武器的 WarhammerPenetration 各是多少 —— 决定"100% 穿甲"要补多少。
        ///   ③ 单位有没有 ChargeAbility —— 决定冲锋是"授予"还是"已有、只是 AI 不用"。
        ///      如果本来就有，那 1.5.73 换成近战 brain 之后可能自动就会冲了。
        /// </summary>
        public static void ExportWeaponFacts()
        {
            try
            {
                if (!Main.DevMode) { Main.Log("[武器实况] 仅开发模式可用。"); return; }

                var guards = RetinueRegistry.All();
                var sb = new StringBuilder();
                sb.AppendLine("======== 卫兵武器实况（族 / 穿甲 / 附魔 / 技能）========");
                sb.AppendLine("  Family 枚举: None Laser Solid Bolt Melta Plasma Flame Exotic Chain Power Primitive Force Blade ChainSaw");
                sb.AppendLine("  Category 枚举: None Melee Thrown Pistol Basic Heavy");
                sb.AppendLine();

                if (guards == null || guards.Count == 0)
                {
                    sb.AppendLine("  当前没有卫兵 —— 先用「★一键招齐机械教」再点这里。");
                    Main.Log(sb.ToString());
                    Main.FlushLog(true);
                    return;
                }

                foreach (var u in guards)
                {
                    if (u == null) continue;
                    sb.AppendLine("  ── " + (u.CharacterName ?? "?") + " ──");

                    // brain：验证 1.5.73 的 brain 覆盖真的生效了
                    try
                    {
                        string bn = (u.Brain != null && u.Brain.Blueprint != null) ? u.Brain.Blueprint.name : "(无)";
                        sb.AppendLine("      brain = " + bn);
                    }
                    catch { sb.AppendLine("      brain = (读不到)"); }

                    var body = u.Body;
                    if (body != null && body.AllSlots != null)
                    {
                        foreach (var slot in body.AllSlots)
                        {
                            if (slot == null) continue;
                            ItemEntity it = null;
                            try { it = slot.MaybeItem; } catch { }
                            if (it == null || it.Blueprint == null) continue;
                            var w = it.Blueprint as Kingmaker.Blueprints.Items.Weapons.BlueprintItemWeapon;
                            if (w == null) continue;   // 只看武器

                            sb.AppendLine("      【武器】" + Nm(w.Name, w.name) + "  " + w.AssetGuid);
                            sb.AppendLine("          槽位 " + slot.GetType().Name);
                            sb.AppendLine("          Family=" + w.Family + "  Category=" + w.Category
                                          + "  Classification=" + w.Classification + "  双手=" + w.IsTwoHanded);
                            sb.AppendLine("          伤害 " + w.WarhammerDamage + "-" + w.WarhammerMaxDamage
                                          + "  穿甲 " + w.WarhammerPenetration + "  闪避穿透 " + w.DodgePenetration);

                            // 出厂附魔（蓝图上的）
                            try
                            {
                                var fi = typeof(Kingmaker.Blueprints.Items.Weapons.BlueprintItemWeapon)
                                         .GetField("m_Enchantments",
                                             System.Reflection.BindingFlags.Public
                                             | System.Reflection.BindingFlags.NonPublic
                                             | System.Reflection.BindingFlags.Instance);
                                var arr = fi != null ? fi.GetValue(w) as System.Array : null;
                                if (arr == null || arr.Length == 0) sb.AppendLine("          出厂附魔: (无)");
                                else
                                {
                                    sb.AppendLine("          出厂附魔 " + arr.Length + " 条:");
                                    foreach (var r in arr)
                                    {
                                        string s = "?";
                                        try { s = r == null ? "(null)" : r.ToString(); } catch { }
                                        sb.AppendLine("              " + s);
                                    }
                                }
                            }
                            catch (Exception e1) { sb.AppendLine("          出厂附魔: 读失败 " + e1.Message); }

                            // 实例附魔（这一件身上现在挂着的）—— 决定 AddEnchantment 可不可行
                            try
                            {
                                var ench = it.Enchantments;
                                if (ench == null || ench.Count == 0) sb.AppendLine("          实例附魔: (无)");
                                else
                                {
                                    sb.AppendLine("          实例附魔 " + ench.Count + " 条:");
                                    foreach (var en in ench)
                                    {
                                        if (en == null || en.Blueprint == null) continue;
                                        sb.AppendLine("              " + en.Blueprint.name + "  " + en.Blueprint.AssetGuid);
                                    }
                                }
                            }
                            catch (Exception e2) { sb.AppendLine("          实例附魔: 读失败 " + e2.Message); }
                        }
                    }

                    // 技能清单 —— 找 Charge / 招架 / 穿甲一类
                    try
                    {
                        // ★中文名和内部名一起打★ 作者说话用中文名（森罗刃网/利刃之舞/死从天降），
                        //   而补丁和配表只认蓝图 guid。中间那层对照表（features_zh.tsv）
                        //   在旧 mod 目录里、已经没了，离线查不到。所以在这里一次打全：
                        //   显示名 · 内部名 · guid，之后按名字定位技能就不用再猜。
                        var names = new List<string>();
                        if (u.Abilities != null && u.Abilities.RawFacts != null)
                            foreach (var ab in u.Abilities.RawFacts)
                            {
                                if (ab == null || ab.Blueprint == null) continue;
                                var bp = ab.Blueprint;
                                string disp = null;
                                try { disp = bp.Name; } catch { }
                                names.Add(string.Format("{0,-16} {1,-46} {2}",
                                          string.IsNullOrEmpty(disp) ? "(无名)" : disp, bp.name, bp.AssetGuid));
                            }
                        names.Sort(StringComparer.Ordinal);
                        sb.AppendLine("      技能 " + names.Count + " 个（显示名 · 内部名 · guid）:");
                        foreach (var n in names) sb.AppendLine("          " + n);
                    }
                    catch (Exception e3) { sb.AppendLine("      技能: 读失败 " + e3.Message); }

                    sb.AppendLine();
                }

                Main.Log(sb.ToString());
                try
                {
                    string path = System.IO.Path.Combine(
                        Main.ModEntry != null ? Main.ModEntry.Path : ".", "weapon_facts.txt");
                    System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
                    Main.Log("[武器实况] 已写入 " + path);
                }
                catch (Exception e4) { Main.LogError("[武器实况] 写文件失败: " + e4.Message); }
                Main.FlushLog(true);
            }
            catch (Exception e) { Main.LogError("[武器实况] 失败: " + e.Message); }
        }


        private static readonly List<BaseUnitEntity> _shown = new List<BaseUnitEntity>();

        /// <summary>销毁「留在原地」模式生成的全部候选。过图时也会被调。</summary>
        public static void ClearShown()
        {
            int n = 0;
            foreach (var u in _shown)
            {
                try { if (u != null) { Game.Instance.EntityDestroyer.Destroy(u); n++; } }
                catch (Exception e) { Main.LogError("[传奇探针] 清理失败: " + e.Message); }
            }
            _shown.Clear();
            if (n > 0) { Main.Log("[传奇探针] 已清理 " + n + " 个展示用候选。"); Main.FlushLog(true); }
        }

        private static void Run(string[,] set, bool keep, bool facts = false)
        {
            try
            {
                if (!Main.DevMode) { Main.Log("[传奇探针] 仅开发模式可用。"); return; }

                var game = Game.Instance;
                if (game == null || game.Player == null) { Main.LogError("[传奇探针] 取不到 Game。"); return; }

                // ★战斗中一律拒绝★ 凭空生成 boss 会打乱回合序，销毁又会留下空档。
                bool inCombat;
                try { inCombat = game.Player.IsInCombat; } catch { inCombat = true; }
                if (inCombat) { Main.Log("[传奇探针] 战斗中不能测，先退出战斗。"); return; }

                // ★联机中一律拒绝★ 单边生成实体会把 Uuid 的随机流永久推快一格，
                //   见 CoopCommand 头注记的那条红线。
                try { if (CoopState.InSession) { Main.Log("[传奇探针] 联机中不能测（单边生成实体会导致不同步）。"); return; } }
                catch { }

                var leader = game.Player.MainCharacterEntity;
                if (leader == null) { Main.LogError("[传奇探针] 取不到主角，无法定位生成点。"); return; }
                var state = leader.HoldingState;
                if (state == null) { Main.LogError("[传奇探针] 取不到区域状态。"); return; }

                var spawned = new List<KeyValuePair<string, BaseUnitEntity>>();
                int n = set.GetLength(0);
                for (int i = 0; i < n; i++)
                {
                    string guid = set[i, 0], label = set[i, 1];
                    try
                    {
                        var bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(guid);
                        if (bp == null) { Main.Log("[传奇探针] " + label + "：蓝图解析不到（DLC 没装？） guid=" + guid); continue; }
                        // ★网格摆放★ 三十多个排成一行要一百多米，镜头根本框不住，
                        //   而且远端的人挤在天边看不清。分排摆，一排 6 个。
                        //   列距 1.8 米（Medium 单位占 1 格，不会互相挤）；
                        //   排距 2.5 米（留出看清前后两排的余量）。
                        int col = i % ShowPerRow, row = i / ShowPerRow;
                        var pos = keep
                            ? leader.Position + new Vector3((col - ShowPerRow * 0.5f) * 1.8f, 0f, 3f + row * 2.5f)
                            : leader.Position + new Vector3((i - n * 0.5f) * 1.6f, 0f, 0f);
                        var u = game.EntitySpawner.SpawnUnit(bp, pos, Quaternion.identity, state);
                        if (u == null) { Main.Log("[传奇探针] " + label + "：SpawnUnit 返回 null。"); continue; }
                        spawned.Add(new KeyValuePair<string, BaseUnitEntity>(label + "\t" + set[i, 2], u));
                    }
                    catch (Exception e) { Main.LogError("[传奇探针] " + label + " 生成失败: " + e.Message); }
                }

                if (spawned.Count == 0) { Main.Log("[传奇探针] 一个都没生成出来。"); return; }

                if (keep)
                {
                    foreach (var kv in spawned) _shown.Add(kv.Value);
                    Main.Log("[传奇探针] 已生成 " + spawned.Count + " 个候选**留在原地**供查看模型。"
                           + "\n    ★看完请立刻点「清理候选」★ 区域里的实体是会进存档的，"
                           + "别带着它们存档。过图也会自动清。"
                           + "\n    顺序（从左到右）：" + Labels(set));
                    Main.FlushLog(true);
                    return;
                }

                Main.Log("[传奇探针] 已生成 " + spawned.Count + " 个，等两帧后读数并立刻销毁……");
                Deferred.NextFrames(2, () => { if (facts) ReadFactsAndDestroy(spawned); else ReadAndDestroy(spawned); });
            }
            catch (Exception e) { Main.LogError("[传奇探针] 失败: " + e.Message); }
        }

        /// <summary>「留在原地」模式一排摆几个。分排摆是因为三十多个排成一行有一百多米，
        /// 镜头框不住，远端的人还挤在天边看不清。</summary>
        private const int ShowPerRow = 6;

        private static string Labels(string[,] set)
        {
            // 按排列出来，和地上的摆法一一对应 —— 平铺一长串的话，
            // 人站在第 4 排第 2 个，你得自己数到第 20 个，没法用。
            var sb = new StringBuilder();
            int n = set.GetLength(0);
            for (int i = 0; i < n; i++)
            {
                if (i % ShowPerRow == 0)
                    sb.Append(i > 0 ? "\n    " : "\n    ").Append("第 ").Append(i / ShowPerRow + 1).Append(" 排（离你最近的算第 1 排）：");
                sb.Append(' ').Append(i % ShowPerRow + 1).Append('.').Append(set[i, 1]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 打印每个候选**实体上挂着的**先天能力，供分配 grantFeaturesT1/T2/T3 时挑选。
        ///
        /// ★为什么读实体而不是读蓝图★ 蓝图的 AddFacts 只是来源之一，实体上最终有什么
        ///   还受种族、默认装备等影响。我们要发的是"让卫兵表现得像它"，所以以实体为准。
        ///
        /// ★三个集合都要打★ 先天能力会散落在三处：被动进 Progression.Features，
        ///   主动技能进 Abilities，切换类进 ActivatableAbilities。只打一处会漏。
        /// </summary>
        private static void ReadFactsAndDestroy(List<KeyValuePair<string, BaseUnitEntity>> spawned)
        {
            var sb = new StringBuilder();
            sb.AppendLine("======== 机械教候选：先天能力实测 ========");
            sb.AppendLine("  ★怎么用这份清单★");
            sb.AppendLine("    · 想让卫兵在某个阶位「表现得像这个单位」，就把它的条目抄进");
            sb.AppendLine("      archetypes.json 的 grantFeaturesT1 / T2 / T3。升阶会自动撤旧发新。");
            sb.AppendLine("    · ★熟练度类的（Proficiency / WeaponFamily / Armor 之类）不要放进阶位表★，");
            sb.AppendLine("      要放就放常驻的 grantFeatures —— 升阶撤掉熟练度会让已选天赋的前置悬空、");
            sb.AppendLine("      手里的武器装不回去，而且两样都不报错，只表现成卫兵莫名变弱。");
            sb.AppendLine("    · 名字后面括号里是 guid，直接复制。");

            foreach (var kv in spawned)
            {
                string label = kv.Key;
                int t = label.IndexOf('\t');
                if (t >= 0) label = label.Substring(0, t);

                sb.AppendLine();
                // 血量一并打出来 —— 之前特性和血量分在两次运行里，对不上号，
                // 每次都要人肉在两份日志之间来回找。
                string hp = "?";
                try { var h = kv.Value != null ? kv.Value.GetHealthOptional() : null; if (h != null) hp = h.MaxHitPoints.ToString(); }
                catch { }
                // ★真实显示名必须打★ 头衔（贤者 / 助祭 / 匠人 / 技师）决定它在机械教
                //   阶级里的位置，而阶级正是「谁当精英、谁当 T3」的判据。
                //   我手写的标签是按内部名起的，而内部名和中文头衔对不上 ——
                //   比如 Quetza-al_Techpriest 内部名写着 Techpriest，中文却是「泽塔贤者」。
                string disp = "?";
                try
                {
                    var b = kv.Value != null ? kv.Value.Blueprint : null;
                    if (b != null) disp = string.IsNullOrEmpty(b.CharacterName) ? b.name : b.CharacterName;
                }
                catch { }
                sb.AppendLine("  ── " + label + "　【" + disp + "　血量 " + hp + "】 ──");

                var u = kv.Value;
                if (u == null) { sb.AppendLine("    （实体为 null，没读到）"); continue; }

                DumpOne(sb, "被动 Features", () =>
                {
                    var outp = new List<string>();
                    foreach (var f in u.Progression.Features)
                        if (f != null && f.Blueprint != null)
                            outp.Add(Nm(f.Blueprint.Name, f.Blueprint.name) + " (" + f.Blueprint.AssetGuid + ")");
                    return outp;
                });

                DumpOne(sb, "主动 Abilities", () =>
                {
                    var outp = new List<string>();
                    foreach (var a in u.Abilities)
                        if (a != null && a.Blueprint != null)
                            outp.Add(Nm(a.Blueprint.Name, a.Blueprint.name) + " (" + a.Blueprint.AssetGuid + ")");
                    return outp;
                });

                DumpOne(sb, "切换 ActivatableAbilities", () =>
                {
                    var outp = new List<string>();
                    foreach (var a in u.ActivatableAbilities)
                        if (a != null && a.Blueprint != null)
                            outp.Add(Nm(a.Blueprint.Name, a.Blueprint.name) + " (" + a.Blueprint.AssetGuid + ")");
                    return outp;
                });

                // ★装备和植入物必须单独打★
                //   探针读的是**生成后的实体**，而实体身上的特性既来自蓝图，
                //   也来自已装备的武器和植入物 —— 上面那三个集合把两者混在一起了。
                //   实测吃过亏：我把「电激甲壳」「电激感应器(MK战士)」「相位振荡器(MK-I)」
                //   当成先天特性写进了 grantFeaturesT2/T3 的建议，查 items 表才发现
                //   它们全是 BlueprintItemAugment（植入物），「欧姆尼塞亚之斧」是武器。
                //
                //   分清楚很重要：植入物该进 gearT1/T2/T3，不该当特性发 ——
                //   直接发特性会绕过植入物的剧情层级门（PartyAugmentManager.CurrentAvailableTier），
                //   等于偷偷解锁，而且玩家背包里看不到那件东西。
                DumpOne(sb, "装备 / 植入物", () =>
                {
                    var outp = new List<string>();
                    var body = u.Body;
                    if (body == null || body.AllSlots == null) return outp;
                    foreach (var slot in body.AllSlots)
                    {
                        if (slot == null) continue;
                        ItemEntity it = null;
                        try { it = slot.MaybeItem; } catch { }
                        if (it == null || it.Blueprint == null) continue;
                        outp.Add(slot.GetType().Name + ": "
                               + Nm(it.Blueprint.Name, it.Blueprint.name)
                               + " (" + it.Blueprint.AssetGuid + ")");
                    }
                    return outp;
                });

                try { Game.Instance.EntityDestroyer.Destroy(u); }
                catch (Exception e) { Main.LogError("[传奇探针] 销毁 " + label + " 失败: " + e.Message); }
            }

            sb.AppendLine();
            sb.AppendLine("  —— 全部已销毁。不入名册、不占名额、不进存档。");
            Main.Log(sb.ToString());
            Main.FlushLog(true);

            // ★导全模式：另存一份到独立文件★
            //   日志会按 4 MB 轮转，而这份数据要反复查阅几天；混在日志里迟早被冲掉。
            //   单独一个文件，作者随时打开就能看，不用再进游戏。
            if (_exportToFile)
            {
                _exportToFile = false;
                try
                {
                    string path = System.IO.Path.Combine(
                        Main.ModEntry != null ? Main.ModEntry.Path : ".", "mech_candidates.txt");
                    System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
                    Main.Log("[导出] 已写入 " + path + "　（日志会轮转，这份不会）");
                    Main.FlushLog(true);
                }
                catch (Exception e) { Main.LogError("[导出] 写文件失败: " + e.Message); }
            }
        }

        /// <summary>显示名优先，没有才退回内部名 —— 两个都空的话至少还能看出是哪一条。</summary>
        private static string Nm(string display, string internalName)
        {
            if (!string.IsNullOrEmpty(display)) return display;
            if (!string.IsNullOrEmpty(internalName)) return internalName;
            return "(无名)";
        }

        /// <summary>
        /// 打一个集合。★空集合也要打出来★ —— 「一条都没有」和「这段代码没跑」
        /// 在日志里必须长得不一样，否则又是一轮白测（见走路掉帧那次的阈值教训）。
        /// </summary>
        private static void DumpOne(StringBuilder sb, string title, Func<List<string>> read)
        {
            List<string> items;
            try { items = read(); }
            catch (Exception e) { sb.AppendLine("    " + title + "：读取失败 —— " + e.Message); return; }

            if (items == null || items.Count == 0) { sb.AppendLine("    " + title + "：（无）"); return; }
            sb.AppendLine("    " + title + "：" + items.Count + " 条");
            items.Sort(StringComparer.Ordinal);
            foreach (var s in items) sb.AppendLine("      " + s);
        }

        private static void ReadAndDestroy(List<KeyValuePair<string, BaseUnitEntity>> spawned)
        {
            var sb = new StringBuilder();
            sb.AppendLine("======== 传奇档候选：血量 / 升级能力 实测 ========");
            sb.AppendLine("  ★体型一栏是 tools/units_full.tsv 离线抽的值，与运行时并排列出用于互证★");
            sb.AppendLine("  ★升级那三列是本轮新增：职业路线能不能上，离线查不出来（连玩家角色的蓝图");
            sb.AppendLine("    都引用 0 个职业路线，说明路线是运行时挂的），只能生成出来试。★");
            sb.AppendLine("  候选                                 表里体型     实测体型     基础血  推级后等级  推级后血量");

            // 用近战分型 chain 的第一条（T1）来试推 —— 那是配表里实际在用、已验证的路线
            BlueprintCareerPath path = null;
            try { path = ResourcesLibrary.TryGetBlueprint<BlueprintCareerPath>("974496d72fbe4329b438ee15cf004bd2"); }
            catch { }
            if (path == null) sb.AppendLine("  ★取不到测试用职业路线，升级那几列会是 -★");

            foreach (var kv in spawned)
            {
                string label = kv.Key, sizeExpect = "";
                int t = label.IndexOf('\t');
                if (t >= 0) { sizeExpect = label.Substring(t + 1); label = label.Substring(0, t); }

                string hp0 = "?", sizeReal = "?", lv1 = "-", hp1 = "-";
                var u = kv.Value;
                try
                {
                    if (u != null)
                    {
                        var h = u.GetHealthOptional();
                        if (h != null) hp0 = h.MaxHitPoints.ToString();
                        try { sizeReal = u.Blueprint != null ? u.Blueprint.Size.ToString() : "?"; } catch { }

                        // ★试着推职业等级★ 能推动 = 这个蓝图支持职业路线。
                        //   推满 T1（15 级）就够判断，不用推到 55 —— 只是要个能/不能的答案
                        //   和一个「升级到底给不给血」的量级。
                        if (path != null)
                        {
                            int moved = 0;
                            for (int i = 0; i < 15; i++)
                            {
                                bool ok;
                                try { ok = Archetypes.ForceAdvanceRank(u, path); } catch { ok = false; }
                                if (!ok) break;
                                moved++;
                            }
                            try
                            {
                                int lv = u.Progression != null ? u.Progression.CharacterLevel : -1;
                                lv1 = moved > 0 ? (lv + "（推了 " + moved + " 级）") : "★推不动★";
                            }
                            catch { lv1 = moved > 0 ? ("?（推了 " + moved + " 级）") : "★推不动★"; }
                            try
                            {
                                var h2 = u.GetHealthOptional();
                                if (h2 != null) hp1 = h2.MaxHitPoints.ToString();
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                bool match = sizeReal == sizeExpect;
                sb.AppendLine(string.Format("  {0,-36} {1,-11} {2,-11} {3,-7} {4,-13} {5}{6}",
                    label, sizeExpect, sizeReal, hp0, lv1, hp1, match ? "" : "   ★体型对不上★"));

                try { if (u != null) Game.Instance.EntityDestroyer.Destroy(u); }
                catch (Exception e) { Main.LogError("[传奇探针] 销毁 " + label + " 失败: " + e.Message); }
            }

            sb.AppendLine("  —— 全部已销毁。不入名册、不占名额、不进存档。");
            sb.AppendLine("  ★怎么读★");
            sb.AppendLine("    · 「推不动」= 该蓝图不支持职业路线，基础血就是它的终值；");
            sb.AppendLine("      我们的加点流水线对它无效，血量补正只能靠特性或修正值。");
            sb.AppendLine("    · 能推动的，看「推级后血量 ÷ 基础血」—— 那是升级带来的倍率，");
            sb.AppendLine("      T1 才 15 级，推到 55 级还会再涨。拿基础血直接比较是不公平的。");
            Main.Log(sb.ToString());
            Main.FlushLog(true);
        }
    }
}
