using System;
using System.Collections.Generic;
using System.Text;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem;                 // GetHealthOptional 的扩展方法所在
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
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
        /// 机械教候选。DLC3 Opticon 那一整套（两派各自成组）+ 几个本体备选。
        /// 本体备选是给没启用 DLC3 的玩家兜底用的，必须一并看模型 ——
        /// 现有五条线的注释都写着「没启用 DLC3 时依次退到…」，不配兜底那批玩家会招到默认甲板卫兵。
        /// </summary>
        private static readonly string[,] Mechanicus =
        {
            { "a92cdde1068c4609b437cd5a22f8b3b0", "教条·机仆",          "Medium" },
            { "d74728f897ef4b0a85978e25f43ea155", "教条·战斗机仆",      "Medium" },
            { "737fc140e70f4ac08b642a67deef64a4", "教条·电子修士",      "Medium" },
            { "4c77d8f0acc2473cadd14ae869aac708", "教条·技工",          "Medium" },
            { "bd8e6264794945cbab40c5201b5fb6f3", "教条·机械教士",      "Medium" },
            { "aa02b505be774674ae924f19dc17e6f6", "教条·锈行者",        "Medium" },
            { "23f85cedf62344ff9a077e6106c9e98a", "异端·技工",          "Medium" },
            { "b8ec45b228034db48d1658e35d59193a", "异端·电子修士",      "Medium" },
            { "6ab30fcb20954e10be43344314511ca6", "异端·机械教士",      "Medium" },
            { "ab131771270542b69fb7a687062b39c0", "异端·电僧",          "Medium" },
            // —— 以下本体（非 DLC3），给兜底链备选 ——
            { "454847134b48402792be9798bf92b0ca", "本体·殖民地战斗机仆", "Medium" },
            { "5d7b454772704f8dbbff8c83fe1dcd32", "本体·海盗技术神甫",   "Medium" },
            { "ef3a6e0349f140828e15c524b85706b8", "本体·电僧（远古反应堆）","Medium" },
        };

        /// <summary>
        /// 本体（非 DLC3）机械教备选 —— 专为兜底链挑的。
        ///
        /// ★为什么要单独一组★ 首轮实测发现本体备选血量崩了：
        ///     DLC3   技工 152 / 护教军游骑兵 297 / 高阶助祭 423
        ///     本体   可疑的技术神甫 51 / 远古反应堆电僧 54
        /// 差 3~8 倍。没启用 DLC3 的玩家会招到明显弱一大截的卫兵，
        /// 而现有五条线的注释都承诺「功能不受影响，只是外观和自带能力会跟精英同款」——
        /// 血量差 8 倍显然不叫「功能不受影响」。
        ///
        /// 所以从全库 111 个「本体 + 机械教主题 + 带 brain」里挑了 16 个覆盖三个角色的，
        /// 优先 Mobs 阵营（真会打的），一次测出血量再定兜底链。
        /// </summary>
        private static readonly string[,] BaseGame =
        {
            { "4eeb0a3e89fd47e59bae8759c530329d", "技工·塔尔祖斯",       "Medium" },
            { "0a7e17bde9c943a6a4db4362da861ee1", "技工·狂乱",           "Medium" },
            { "f5de71120c8a46b48b38e47dec7073de", "技工·腐化电弧",       "Medium" },
            { "0a51a4a887db48a1bc66aa8b4d03e008", "技工·附身",           "Medium" },
            { "3b59e14549cf4cdeb75a20fad85a748c", "游骑兵·塔尔祖斯近战", "Medium" },
            { "d6c51b2891764ae1b177d65623066f37", "游骑兵·塔尔祖斯远程", "Medium" },
            { "14c191894a4245149eebe8ac911d555c", "游骑兵·不朽近战",     "Medium" },
            { "84a354589be241598c28289bfcbd7c13", "游骑兵·不朽远程",     "Medium" },
            { "ac64204409e7448e9204f74b4913f318", "游骑兵·腐化动力",     "Medium" },
            { "93420269f45f4b45a8cb376ed01c298b", "教士·狂乱逻辑",       "Medium" },
            { "3c5c7c0e9c87489dafba65a45961a4db", "教士·废船疯Magos",    "Medium" },
            { "1b2ad836f8cd437790f32a6c269110ed", "教士·疯狂",           "Medium" },
            { "2711d4883bb24692afaf1e8a3ab4335f", "教士·黑暗Magos",      "Medium" },
            { "906197899b9d40d8b1f0189a57fbd7b2", "教士·异端匠人",       "Medium" },
            { "d8e940ea190a47d6b2d4e3ce9203f058", "教士·奎兹阿尔",       "Medium" },
            { "9c409b4c473f454b91085d56264acb76", "锈行者·混沌近战",     "Medium" },
        };

        public static void Run() { Run(Candidates, false); }
        public static void RunMech() { Run(Mechanicus, false); }
        public static void ShowBase() { Run(BaseGame, true); }

        /// <summary>
        /// 把候选生成出来**留在原地**供肉眼看模型。
        ///
        /// ★这个模式会在区域里留下实体，必须清干净★
        ///   区域状态里的实体是**会进存档**的。所以：
        ///     · 生成的实体全部记在 _shown 里，「清理候选」按钮一键销毁；
        ///     · 过图时自动清（RetinueLifecycle 会调 ClearShown）；
        ///     · 战斗中拒绝生成，免得把它们卷进回合序。
        ///   即便如此，**看完请立刻点清理，别存档**。日志里也会喊这一句。
        /// </summary>
        public static void Show() { Run(Mechanicus, true); }

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

        private static void Run(string[,] set, bool keep)
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
                        // ★排开摆放★ 十几个叠在一起没法看模型，横向排开一米一个
                        var pos = leader.Position + new Vector3((i - n * 0.5f) * 1.6f, 0f, keep ? 3f : 0f);
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
                Deferred.NextFrames(2, () => ReadAndDestroy(spawned));
            }
            catch (Exception e) { Main.LogError("[传奇探针] 失败: " + e.Message); }
        }

        private static string Labels(string[,] set)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < set.GetLength(0); i++) { if (i > 0) sb.Append(" | "); sb.Append(set[i, 1]); }
            return sb.ToString();
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
