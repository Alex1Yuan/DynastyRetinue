using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Controllers.Dialog;
using Kingmaker.DialogSystem;
using Kingmaker.DialogSystem.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Localization;
using Kingmaker.UnitLogic.Interaction;
using Kingmaker.ElementsSystem;            // ConditionsChecker, ActionList
using Kingmaker.EntitySystem.Stats.Base;   // StatType

namespace DynastyRetinue
{
    /// <summary>
    /// 把「征募护卫队」作为一条**原生对话选项**插进 NPC 的对话里。
    ///
    /// 存档安全性（工作流实测确认）：
    ///   1. 运行时改蓝图**纯内存**——蓝图从不被序列化，存档只存 AssetGuid，
    ///      而蓝图对象每次开游戏都从 blueprints-pack.bbp 重新读。所以往 vanilla cue 的
    ///      Answers 列表里插一条，重启即复原，不动原版数据文件。
    ///   2. 选中这条选项后，GUID 进 DialogState.SelectedAnswers —— 那是 HashSet&lt;string&gt;，
    ///      **纯字符串、永不经过 BlueprintConverter 解析** ⇒ 卸载 mod 后是一段死文本，无害。
    ///   3. ★硬规则★ 这条 answer 的 OnSelect **绝不能**授予 fact/召唤单位。
    ///      否则我们的蓝图会被记成 EntityFactSource.m_Blueprint（类型化标量字段，
    ///      无字典容错）⇒ 卸载后存档打不开。所以实际动作走 Harmony 钩子，在我们代码里做。
    /// </summary>
    public static class RecruitDialog
    {
        /// <summary>
        /// 一条注入式对话选项。v0.38.0 把这个类从"只有招募一条"泛化成多条 ——
        /// 舰船升级要复用同一套机制，而这套机制里每个字段为什么要非空、
        /// 为什么不能走原版 SelectAnswer，都是逐个崩溃换来的（见下面各处长注释）。
        /// **复制一份等于让那些知识分叉**，所以宁可泛化也不复制。
        /// </summary>
        public sealed class Entry
        {
            public string Guid;
            public string TextKey;
            /// <summary>★必须是委托不能是字符串★ 文案里含价格，而价格随当前分档变
            /// （巡洋→大巡只补差价）。写成 string 会在注册那一刻求值一次就冻住 ——
            /// v0.38.0 的 bug：换成巡洋之后，大巡那条仍然显示 1000 而不是 500。
            /// LocalizedString 每次取值都去查表，所以这里每次都能拿到最新的。</summary>
            public Func<string> Text;
            /// <summary>玩家选中后要做的事。★绝不能在 answer 的 OnSelect 里做★</summary>
            public Action OnPicked;
            /// <summary>返回 false 就不注入（面板开关）。</summary>
            public Func<bool> Enabled;
            /// <summary>true = 选中后**不**关闭对话框。船坞要留在对话里，
            /// 好让顾问在成交后还能说话，而不是把玩家一脚踢出对话。</summary>
            public bool KeepDialog;
        }

        private static readonly List<Entry> Entries = new List<Entry>();

        /// <summary>把内置的几条注册进去。幂等，注册表按 guid 去重。</summary>
        public static void EnsureBuiltins()
        {
            Register(new Entry {
                Guid     = AnswerGuid,
                TextKey  = TextKey,
                Text     = delegate { return L.T(TextValue); },
                Enabled  = delegate { return Main.Settings != null && Main.Settings.DialogRecruitEntry; },
                // ★招募不留在对话里★（1.0.66 撤回 1.0.54 的改动）
                //   1.0.54 给它加过 KeepDialog=true，目的是让"对话结束自动关窗"
                //   在合作里成立（房主点一下两台都开窗，而关窗不复制）。
                //   但留在对话里之后，刚点过的那一条**不会变暗** ——
                //   原版点过的选项都会变暗，同一个列表里两套显示逻辑，很突兀。
                //   根因是同一个对话会话里答案列表不重建；试过在选中时刷新（1.0.63）
                //   和在关窗时刷新（1.0.64），都没能让它变暗。
                //
                //   权衡下来：自动关窗省的是客机一次点击，而显示不一致是**每次都看得见**的。
                //   撤回，退回选中即关对话 —— 下次进对话是全新会话，显示自然正确。
                OnPicked = delegate { Main.OpenRecruitUI(null, true); },
            });
            ShipDialog.RegisterAll();
        }

        private static string Label(Entry e)
        {
            try { return e != null && e.Text != null ? e.Text() : "?"; } catch { return "?"; }
        }

        public static void Register(Entry e)
        {
            if (e == null || string.IsNullOrEmpty(e.Guid)) return;
            foreach (var x in Entries) if (x.Guid == e.Guid) return;
            Entries.Add(e);
        }

        /// <summary>固定 GUID —— 不随机生成，保证跨版本稳定、便于排查。</summary>
        public const string AnswerGuid = "kgd00001000010000100001000010001";
        /// <summary>本地化 key。文案不走反射写字段（LocalizedString 根本没有缓存字段，
        /// 它每次都去 LocalizationManager.CurrentPack 查表），改成 Harmony 钩查表函数。</summary>
        public const string TextKey = "dynasty_recruit_answer";
        public const string TextValue = "（护卫队）关于我的护卫队……";

        /// <summary>
        /// 让本地化查表认识我们的 key。
        /// LocalizedString.Text → GetText → LoadImpl → LocalizedString.TryGetText(pack, out)
        ///                     → pack.TryGetText(key, out text)   ← 钩最后这一步
        /// 只拦我们自己的 key，其余原样放行。
        ///
        /// ★★★ 之前这里的类型名是错的，本补丁从 v0.10.1 起从未生效过 ★★★
        /// 真名是 Kingmaker.Localization.**Shared**.LocalizationPack（LocalizationShared.dll）。
        /// AccessTools.TypeByName 的三级回退全落空：
        ///   1) Type.GetType(name)                   → null（无程序集限定名）
        ///   2) AllTypes().First(t => t.FullName==n) → null（FullName 多了 .Shared）
        ///   3) AllTypes().First(t => t.Name==n)     → null（Name 是 "LocalizationPack"，不含点号）
        /// ⇒ TargetMethod() 返回 null ⇒ Prepare() 返回 false ⇒ Patch() 走 ReportException(null, null)，
        ///   而它第一句就是 if (exception == null) return ⇒ **静默返回空列表，不抛异常不打日志**。
        /// 所以下面的 Prepare() 必须自己打一条错误日志。
        ///
        /// 注：不能改成 typeof(LocalizationPack) —— csproj 没引用 LocalizationShared.dll，会 CS0246。
        /// </summary>
        [HarmonyPatch]
        public static class LocalizationPatch
        {
            private static System.Reflection.MethodBase TargetMethod()
            {
                var t = AccessTools.TypeByName("Kingmaker.Localization.Shared.LocalizationPack")
                     ?? Type.GetType("Kingmaker.Localization.Shared.LocalizationPack, LocalizationShared", false)
                     ?? AccessTools.TypeByName("LocalizationPack");   // 防将来再换命名空间
                if (t == null) return null;
                // 全类只有这一个 TryGetText（GetText 是另一个名字），不会二次歧义
                return AccessTools.Method(t, "TryGetText");
            }

            private static bool Prepare()
            {
                var m = TargetMethod();
                if (m == null)
                    Main.LogError("[招募对话] 找不到 LocalizationPack.TryGetText —— "
                                  + "选项文案将为空白。检查游戏版本是否改了命名空间。");
                return m != null;
            }

            // 形参名须与原方法一致（key / text）；out 在补丁里用 ref 接是 Harmony 的正确写法。
            private static bool Prefix(string key, ref string text, ref bool __result)
            {
                if (string.IsNullOrEmpty(key)) return true;
                string val = null;
                foreach (var e in Entries)
                    if (e.TextKey == key)
                    { try { val = e.Text != null ? e.Text() : null; } catch { val = null; } break; }
                if (val == null) return true;
                text = val;
                __result = true;
                return false;
            }
        }

        private static readonly Dictionary<string, BlueprintAnswer> _answers =
            new Dictionary<string, BlueprintAnswer>(StringComparer.Ordinal);
        private static readonly HashSet<string> _injected = new HashSet<string>(StringComparer.Ordinal);

        public static void ResetForNewArea() { _injected.Clear(); }

        /// <summary>构造并注册我们的 answer（只做一次）。</summary>
        private static BlueprintAnswer EnsureAnswer(Entry entry)
        {
            BlueprintAnswer cached;
            if (_answers.TryGetValue(entry.Guid, out cached) && cached != null) return cached;
            BlueprintAnswer _answer = null;
            try
            {
                // SimpleBlueprint 是普通类不是 ScriptableObject，AssetGuid 就是 string
                var a = new BlueprintAnswer();
                a.name = "Kgd_Answer_" + entry.TextKey;
                a.AssetGuid = entry.Guid;

                // ══════════════════════════════════════════════════════════════════
                // ★★★ 头号 bug 修复点 ★★★
                // new BlueprintAnswer() 只跑 C# 字段初始化器；平时由 JSON 反序列化填的
                // 引用类型字段全是 null，而游戏代码一律当非空用、且没有 try/catch。
                //
                // 实测（GameLogFull.txt:6075）：
                //     System.NullReferenceException
                //        at BlueprintAnswer.CanShow()
                //        at DialogController.PlayBasicCue(BlueprintCue)
                // 崩点是 CanShow() 里的
                //     public bool HasShowCheck => ShowCheck.Type != StatType.Unknown;
                // ShowCheck 是 [Serializable] **class**（已反编译确认，不是 struct）
                // 且无初始化器 ⇒ null ⇒ NRE。
                //
                // 后果不止"我们这条选项没了"：PlayBasicCue 里是
                //     AddAnswers(...);                                   // ← 这里抛，m_Answers 已被 Clear
                //     EventBus.RaiseEvent(h => h.HandleOnCueShow(...));  // ← 永不执行
                // HandleOnCueShow 是 DialogVM 唯一给 Cue/Answers/SpeakerPortrait/SpeakerName
                // 赋值的地方 ⇒ 全停在 null ⇒ 头像与名字 SetActive(false)、Bind(null) 提前 return
                // ⇒ TMP 保留 prefab 设计期占位文字。这就是"左侧变空、只剩占位文本"。
                // 同时也解释了"日志里没有 missing string"：UI 根本没走到读文案那一步。
                // ══════════════════════════════════════════════════════════════════

                // CanShow() 第 3 个 if。Type 默认 StatType.Unknown(0) ⇒ HasShowCheck 恒 false，
                // 于是永远碰不到 private 的 OnCheckSuccess / OnCheckFail
                //（那两个字段是 private 设不了，但调用点是 ?.Run()，留 null 完全安全，别去反射填）。
                a.ShowCheck = new ShowCheck();

                // CanShow() 末尾 ShowConditions.Check(this)；CanSelect() 里 SelectConditions.Check()。
                // ConditionsChecker.Conditions 本身是 null，但 HasConditions 判了 != null、
                // Check() 在 !HasConditions 时提前 return true ⇒ 裸 new 就够。
                a.ShowConditions   = new ConditionsChecker();
                a.SelectConditions = new ConditionsChecker();

                // HasExchangeData → HasExchangeDataOnSelect() → OnSelect.Actions。
                // ActionList.Actions 自带 = new GameAction[0] ⇒ 裸 new 就够。
                // ★ 硬规则 3：这里永远保持空动作列表，绝不放授予 fact / 召唤单位的动作 ★
                a.OnSelect = new ActionList();

                // SkillChecks / SkillChecksDC 第一句就是 FakeChecks.Length。
                a.FakeChecks = new CheckData[0];

                // SkillChecksDC 里 CharacterSelection.SelectUnit(...)。
                // Type.Clear(=0) ⇒ SelectUnit 直接返回 null，不指定行动单位，正是我们要的。
                a.CharacterSelection = new CharacterSelection
                {
                    SelectionType   = CharacterSelection.Type.Clear,
                    ComparisonStats = new StatType[0],   // 只有 Manual/Random 分支会读，防御性填上
                };

                // ★ NextCue 必须非 null 但**保持空** ★
                // 读它的地方：SkillChecksDC / SkillChecks / CanShow() 的 RequireValidCue 分支，
                // 以及我们自己的 FindExitIndexInList / CollectCues。
                // 为什么不指回宿主 cue（上一版 SetNextCue 干的事）：
                //   a) AnswersList_0002 同时挂在 20+ 个 cue 上，玩家可能从别的 cue 进的菜单，硬跳会重放不相干台词；
                //   b) 目标 cue 若勾了 ShowOnce，PlayCue 每次都 ShownCuesAdd ⇒ CanShow() 必 false
                //      ⇒ Select() 返回 null ⇒ PlayCue(null) ⇒ StopDialog()，表现为"选完对话莫名关掉"；
                //   c) 重播会重跑 cue.ApplyShiftDialog() / ReceiveRewards()，污染玩家数值。
                // 而 SelectPatch 的 Prefix 返回 false，原方法根本不跑，NextCue 在流程上已无关紧要。
                a.NextCue = new CueSelection();

                a.Description = new LocalizedString();

                a.ShowOnce              = false;
                a.ShowOnceCurrentDialog = false;
                a.RequireValidCue       = false;   // 必须 false：NextCue 为空时 true 会让 CanShow() 过滤掉自己
                a.DebugMode             = false;
                a.AddToHistory          = false;   // 纵深防御：Prefix 已跳过 AddHistoryEntry，这里再关一道

                SetText(a, entry.TextKey);

                // ── 自检：只做纯空值断言，★绝不调用 CanShow()/CanSelect()★ ──
                // 理由：EnsureAnswer 在**区域加载时**执行（不在对话中）。
                //   · CanShow() 成功路径会 DialogDebug.Add(...) 写对话历史，是噪声；
                //   · CanSelect() → IsRequirementsSatisfied() 会摸殖民地上下文，为空时 NRE，
                //     反而把本来正常的 answer 误判丢弃。
                if (a.ShowCheck == null || a.ShowConditions == null || a.SelectConditions == null
                    || a.OnSelect == null || a.OnSelect.Actions == null
                    || a.FakeChecks == null || a.CharacterSelection == null
                    || a.NextCue == null || a.NextCue.Cues == null
                    || a.SoulMarkShift == null || a.SoulMarkRequirement == null)
                {
                    Main.LogError("[招募对话] answer 字段自检失败，放弃注册 —— "
                                  + "宁可不插入，也不能插一条会把整段对话打空的选项。");
                    return null;
                }

                ResourcesLibrary.BlueprintsCache.AddCachedBlueprint(entry.Guid, a);
                _answer = a;
                _answers[entry.Guid] = a;
                Main.LogVerbose("[对话注入] answer 已注册 " + entry.Guid + "  「" + Label(entry) + "」");
            }
            catch (Exception e) { Main.LogError("[招募对话] 构造 answer 失败: " + e); }
            return _answer;
        }

        /// <summary>
        /// 设置选项文案。BlueprintAnswer.Text 是 private LocalizedString，只能反射写。
        /// LocalizedString 没有任何缓存字段，取值时每次都去 LocalizationManager.CurrentPack 查表，
        /// 所以这里只设 Key，真正的文字由 LocalizationPatch 在查表函数上拦下来回填。
        /// </summary>
        private static void SetText(BlueprintAnswer a, string textKey)
        {
            try
            {
                // Text 由 BlueprintAnswer **自身**声明（两级基类均无同名字段），恒能找到；
                // 保留判空只是为了将来版本改名时立刻看得见，而不是像以前那样静默 return。
                var f = typeof(BlueprintAnswer).GetField("Text",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f == null)
                {
                    Main.LogError("[招募对话] 找不到 BlueprintAnswer.Text 字段，选项将没有文字。");
                    return;
                }
                var ls = new LocalizedString();
                ls.Key = textKey;
                f.SetValue(a, ls);
                Main.LogVerbose("[对话注入] Text.Key 已设为 " + textKey);
            }
            catch (Exception e) { Main.LogError("[招募对话] 设置文案失败: " + e); }
        }

        /// <summary>把选项插进当前区域目标 NPC 的对话首个 cue。返回插入数。</summary>
        public static int InjectInArea(bool verbose = false)
        {
            int n = 0;
            try
            {
                if (!Main.Enabled || Main.Settings == null) return 0;
                EnsureBuiltins();

                // 先把这一轮要注入的选项算出来。全部被开关关掉就直接返回，
                // 免得白扫一遍 AllBaseUnits。
                var active = new List<Entry>();
                foreach (var e in Entries)
                {
                    bool on = true;
                    try { if (e.Enabled != null) on = e.Enabled(); } catch { on = false; }
                    if (!on) continue;
                    if (EnsureAnswer(e) == null) continue;
                    active.Add(e);
                }
                if (active.Count == 0) return 0;

                var game = Game.Instance;
                if (game == null || game.State == null) return 0;
                var keys = Split(Main.Settings.RecruitNpcKeys);
                if (keys.Count == 0) return 0;

                foreach (var u in game.State.AllBaseUnits)
                {
                    if (u == null || !u.IsInGame || u.Blueprint == null) continue;
                    string bp = u.Blueprint.name ?? "";
                    bool hit = false;
                    foreach (var k in keys)
                        if (bp.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                    if (!hit) continue;

                    BlueprintDialog dlg = null;
                    // ★ 对话不一定绑在单位蓝图上 ★
                    // HighFactotum 的蓝图只有 76 字节、零组件 —— 它的对话是场景 spawner 挂的
                    //（SpawnerInteractionDialog）。所以要读**活体实体**的交互列表，
                    // 而不是 u.Blueprint.GetComponents<DialogOnClick>()。
                    // 两种来源都实现了 IDialogReference，但取 Dialog 的属性不在接口上，
                    // 所以用反射统一取，免得为每个实现写一遍。
                    try
                    {
                        var part = u.GetOptional<Kingmaker.UnitLogic.Parts.UnitPartInteractions>();
                        if (part != null)
                            foreach (var it in part.Interactions)
                            {
                                if (it == null) continue;
                                if (verbose) Main.LogVerbose("[招募对话]   交互: " + it.GetType().Name);
                                dlg = FindDialog(it);
                                if (dlg != null) break;
                            }
                    }
                    catch (Exception e) { Main.LogError("[招募对话] 读交互失败 " + bp + ": " + e.Message); }

                    // 兜底：万一真有蓝图级的 DialogOnClick
                    if (dlg == null)
                    {
                        try
                        {
                            foreach (var c in u.Blueprint.GetComponents<DialogOnClick>()) { dlg = c.Dialog; if (dlg != null) break; }
                        }
                        catch { }
                    }
                    if (dlg == null) { if (verbose) Main.LogVerbose("[招募对话] " + bp + " 身上找不到对话（交互数 " + InteractionCount(u) + "）"); continue; }
                    if (verbose) Main.LogVerbose("[招募对话] " + bp + " 的对话 = " + dlg.name);

                    foreach (var e in active) n += InjectInto(dlg, e, verbose);
                }
                if (verbose && n == 0) Main.LogVerbose("[招募对话] 没插入任何选项（关键字: " + Main.Settings.RecruitNpcKeys + "）");
            }
            catch (Exception e) { Main.LogError("[招募对话] InjectInArea 异常: " + e); }
            return n;
        }

        private static int InjectInto(BlueprintDialog dlg, Entry entry, bool verbose)
        {
            int n = 0;
            try
            {
                // ★ 不能只插 FirstCue ★
                // DialogController.AddAnswers：`if (continueCue) { 只放一个「继续」 } else { 逐条筛选 }`
                // 带 Continue 的叙述 cue 会把答案列表整个换掉，插进去等于没插。
                // 所以遍历整张对话图，只挑**本来就列选项**的枢纽 cue。
                var cues = CollectCues(dlg);
                if (verbose) Main.LogVerbose("[招募对话] " + dlg.name + " 共 " + cues.Count + " 个 cue");

                // ★ 真正的选项枢纽是 BlueprintAnswersList，不是 cue ★
                // 实测 HighFactotumDialogue 里几乎每个 cue 都只挂 1 个"答案"，
                // 而玩家看到的 12 条选项装在一个 AnswersList 里 —— AddAnswers 遇到它会递归展开：
                //     if (answer2 is BlueprintAnswersList list && list.CanSelect())
                //         AddAnswers(list.Answers.Dereference(), null);
                // 所以要插进那个 list 的 Answers，插到 cue 上只会挂在某条叙述回复旁边。
                BlueprintAnswersList best = null;
                string bestWhere = null;
                foreach (var cue in cues)
                {
                    if (cue.Answers == null) continue;
                    foreach (var ar in cue.Answers)
                    {
                        BlueprintAnswersList lst = null;
                        try { lst = ar != null ? ar.Get() as BlueprintAnswersList : null; } catch { }
                        if (lst == null || lst.Answers == null) continue;
                        if (verbose) Main.LogVerbose("[招募对话]   列表 " + lst.name + " 在 " + cue.name
                                              + "  含 " + lst.Answers.Count + " 条");
                        // 取条目最多的那个 —— 主选项菜单
                        if (best == null || lst.Answers.Count > best.Answers.Count)
                        { best = lst; bestWhere = cue.name; }
                    }
                }

                if (best == null)
                {
                    if (verbose) Main.LogVerbose("[招募对话] " + dlg.name + " 里没有 AnswersList，退回按 cue 找枢纽");
                    return InjectIntoCueHub(dlg, cues, entry, verbose);
                }

                {
                    string key = dlg.name + "/list/" + best.name + "|" + entry.Guid;
                    if (_injected.Contains(key)) return 0;

                    bool dup = false;
                    foreach (var ar in best.Answers)
                    {
                        try { if (ar != null && ar.Get() != null && ar.Get().AssetGuid == entry.Guid) { dup = true; break; } }
                        catch { }
                    }
                    if (!dup)
                    {
                        var r = new BlueprintAnswerBaseReference();
                        var gf = typeof(BlueprintReferenceBase).GetField("guid",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        if (gf != null) gf.SetValue(r, entry.Guid);

                        int at = FindExitIndexInList(best.Answers);
                        if (at < 0 || at > best.Answers.Count) at = best.Answers.Count;
                        best.Answers.Insert(at, r);

                        // ★ 不再给 NextCue 指回宿主 cue ★
                        // 当初那么做是因为误判：以为"选完对话变空"是 NextCue 留空造成的。
                        // 真凶是 CanShow() 的 NRE（见 EnsureAnswer 里的长注释）——
                        // 空 NextCue 只会走 PlayCue(null) → StopDialog()，而且 SelectPatch
                        // 现在直接跳过 SelectAnswer，NextCue 压根不会被消费。
                        // 指回 vanilla cue 反而有重放台词 / 撞 ShowOnce / 重复 ApplyShiftDialog 的风险。
                        n++;
                        Main.LogVerbose("[对话注入] 「" + Label(entry) + "」已插入到选项列表 " + best.name + "（" + bestWhere + "）"
                                 + "  位置 " + at + "/" + best.Answers.Count
                                 + (at < best.Answers.Count - 1 ? "（退出项之前）" : "（末尾）"));
                    }
                    _injected.Add(key);
                }
            }
            catch (Exception e) { Main.LogError("[招募对话] 插入失败 " + dlg.name + ": " + e.Message); }
            return n;
        }

        /// <summary>没有 AnswersList 时的退路：按老办法找 cue 枢纽。</summary>
        private static int InjectIntoCueHub(BlueprintDialog dlg, List<BlueprintCue> cues, Entry entry, bool verbose)
        {
            int n = 0;
            try
            {
                BlueprintCue hub = null;
                foreach (var cue in cues)
                {
                    bool hasContinue = cue.Continue != null && cue.Continue.Cues != null && cue.Continue.Cues.Count > 0;
                    if (cue.Answers != null && cue.Answers.Count > 1 && !hasContinue && hub == null) hub = cue;
                }
                if (hub == null) { if (verbose) Main.LogVerbose("[招募对话] " + dlg.name + " 也没有 cue 枢纽"); return 0; }
                string key = dlg.name + "/" + hub.name;
                if (_injected.Contains(key)) return 0;
                var r = new BlueprintAnswerBaseReference();
                var gf = typeof(BlueprintReferenceBase).GetField("guid", BindingFlags.Instance | BindingFlags.NonPublic);
                if (gf != null) gf.SetValue(r, entry.Guid);
                int at = FindExitIndexInList(hub.Answers);
                if (at < 0 || at > hub.Answers.Count) at = hub.Answers.Count;
                hub.Answers.Insert(at, r);
                _injected.Add(key);
                n++;
                Main.LogVerbose("[招募对话] 已插入到 cue 枢纽 " + hub.name + " 位置 " + at);
            }
            catch (Exception e) { Main.LogError("[招募对话] 退路插入失败: " + e.Message); }
            return n;
        }

        /// <summary>
        /// 找出"退出对话"那条选项的下标，我们插在它前面。
        ///
        /// 两条判据，按可靠性排序：
        ///   1. 名字里带 Exit/Leave/Goodbye/Bye —— 本对话里就有 HighFactotumExitDialog / HighFactotumLeave
        ///   2. NextCue 为空（选完对话就结束）—— 结构性特征，不依赖命名习惯
        /// 都认不出来就返回 -1，调用方退回追加。
        /// </summary>
        /// <summary>把 answer 的 NextCue 指向某个 cue（选完回到那里）。
        /// ★ 已弃用，无人调用 ★ 保留仅作记录：这条路有重放台词 / 撞 ShowOnce /
        /// 重复 ApplyShiftDialog 三重风险，别再接回去。</summary>
        [Obsolete("别用：见 EnsureAnswer 里 NextCue 那段注释")]
        private static void SetNextCue(BlueprintAnswer a, BlueprintCue cue)
        {
            try
            {
                if (a == null || cue == null) return;
                var sel = new CueSelection();
                var r = new BlueprintCueBaseReference();
                var gf = typeof(BlueprintReferenceBase).GetField("guid",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (gf != null) gf.SetValue(r, cue.AssetGuid);
                sel.Cues.Add(r);
                a.NextCue = sel;
            }
            catch (Exception e) { Main.LogError("[招募对话] 设置 NextCue 失败: " + e.Message); }
        }

        private static int FindExitIndex(BlueprintCue cue)
        {
            try { return FindExitIndexInList(cue.Answers); } catch { return -1; }
        }

        /// <summary>在一组答案里找"退出对话"的下标，我们插在它前面。</summary>
        private static int FindExitIndexInList(List<BlueprintAnswerBaseReference> answers)
        {
            try
            {
                if (answers == null) return -1;
                int structural = -1;
                for (int i = 0; i < answers.Count; i++)
                {
                    var ab = answers[i] != null ? answers[i].Get() : null;
                    if (ab == null) continue;
                    string nm = ab.name ?? "";
                    foreach (var k in new[] { "Exit", "Leave", "Goodbye", "Bye", "Farewell" })
                        if (nm.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return i;

                    if (structural < 0)
                    {
                        var ans = ab as BlueprintAnswer;
                        if (ans != null && (ans.NextCue == null || ans.NextCue.Cues == null || ans.NextCue.Cues.Count == 0))
                            structural = i;
                    }
                }
                return structural;
            }
            catch { return -1; }
        }

        /// <summary>从 FirstCue 出发遍历整张对话图（沿 Continue 和各 answer 的 NextCue）。</summary>
        private static List<BlueprintCue> CollectCues(BlueprintDialog dlg)
        {
            var outp = new List<BlueprintCue>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<BlueprintCueBase>();
            try
            {
                Enqueue(queue, dlg.FirstCue);
                int guard = 0;
                while (queue.Count > 0 && guard++ < 2000)
                {
                    var cb = queue.Dequeue();
                    if (cb == null) continue;
                    string id = cb.AssetGuid;
                    if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;

                    var cue = cb as BlueprintCue;
                    if (cue == null) continue;
                    outp.Add(cue);

                    Enqueue(queue, cue.Continue);
                    if (cue.Answers != null)
                        foreach (var ar in cue.Answers)
                        {
                            try
                            {
                                var ab = ar != null ? ar.Get() : null;
                                var ans = ab as BlueprintAnswer;
                                if (ans != null) { Enqueue(queue, ans.NextCue); continue; }
                                // 答案列表：把里面的答案也展开
                                var lst = ab as BlueprintAnswersList;
                                if (lst != null && lst.Answers != null)
                                    foreach (var i2 in lst.Answers)
                                    {
                                        var a2 = i2 != null ? i2.Get() as BlueprintAnswer : null;
                                        if (a2 != null) Enqueue(queue, a2.NextCue);
                                    }
                            }
                            catch { }
                        }
                }
            }
            catch (Exception e) { Main.LogError("[招募对话] 遍历对话图失败: " + e.Message); }
            return outp;
        }

        private static void Enqueue(Queue<BlueprintCueBase> q, CueSelection sel)
        {
            if (sel == null || sel.Cues == null) return;
            foreach (var c in sel.Cues)
            {
                try { var b = c != null ? c.Get() : null; if (b != null) q.Enqueue(b); }
                catch { }
            }
        }

        /// <summary>
        /// 从一个交互对象里挖出 BlueprintDialog。
        ///
        /// 挂在单位上的往往是**包装类**：SpawnerInteractionPart.Wrapper 只有一个
        /// `public SpawnerInteraction Source`，真正带 Dialog 的 SpawnerInteractionDialog 在里面。
        /// 所以先看自己有没有 Dialog，没有就下钻一层看 Source。
        /// </summary>
        private static BlueprintDialog FindDialog(object it, int depth = 0)
        {
            if (it == null || depth > 2) return null;
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var p = it.GetType().GetProperty("Dialog", BF);
                if (p != null)
                {
                    var d = p.GetValue(it, null) as BlueprintDialog;
                    if (d != null) return d;
                }
                var f = it.GetType().GetField("m_Dialog", BF);
                if (f != null)
                {
                    var r = f.GetValue(it) as BlueprintDialogReference;
                    if (r != null && r.Get() != null) return r.Get();
                }
                // 下钻：Source / Interaction / Inner
                foreach (var name in new[] { "Source", "Interaction", "Inner" })
                {
                    var sf = it.GetType().GetField(name, BF);
                    if (sf != null)
                    {
                        var d = FindDialog(sf.GetValue(it), depth + 1);
                        if (d != null) return d;
                    }
                    var sp = it.GetType().GetProperty(name, BF);
                    if (sp != null)
                    {
                        var d = FindDialog(sp.GetValue(it, null), depth + 1);
                        if (d != null) return d;
                    }
                }
            }
            catch { }
            return null;
        }

        private static int InteractionCount(BaseUnitEntity u)
        {
            try
            {
                var p = u.GetOptional<Kingmaker.UnitLogic.Parts.UnitPartInteractions>();
                return p == null ? 0 : p.Interactions.Count;
            }
            catch { return -1; }
        }

        private static List<string> Split(string s)        {
            var l = new List<string>();
            if (string.IsNullOrEmpty(s)) return l;
            foreach (var p in s.Split(',', ';', '|')) { var t = p.Trim(); if (t.Length > 0) l.Add(t); }
            return l;
        }

        /// <summary>
        /// 把我们的选项挪到**可见列表**里"退出对话"的前一个。
        ///
        /// 为什么不能在插入时定位：DialogController.AddAnswers 会把原始 AnswersList
        /// （HighFactotumDialogue 里是 30 条）按 answer.CanShow() 过滤成玩家真正看到的那 12 条，
        ///     foreach (var a in answers) if (a != null && a.CanShow()) m_Answers.Add(a);
        /// 相对顺序保留、但下标整个变了 —— 我们插在原始表第 20 位，过滤完落到可见第 6 位。
        /// 所以定位必须发生在过滤**之后**，也就是这个 Postfix 里。
        ///
        /// 定位规则（按可靠性排序）：
        ///   1. 名字带 Exit/Leave/Goodbye/Bye/Farewell 的第一条 —— 插到它前面
        ///   2. 都认不出来就放在**最后一条之前**（Count-1）——
        ///      原版对话的最后一个可见选项就是退出，这条规则不依赖命名习惯
        /// 只移动我们自己那一条，不动 vanilla 条目的相对顺序。
        /// </summary>
        [HarmonyPatch(typeof(DialogController), "AddAnswers",
                      new Type[] { typeof(System.Collections.Generic.IEnumerable<BlueprintAnswerBase>),
                                   typeof(BlueprintCueBase) })]
        public static class AnswerOrderPatch
        {
            private static void Postfix(DialogController __instance)
            {
                try
                {
                    var f = AccessTools.Field(typeof(DialogController), "m_Answers");
                    if (f == null) return;
                    var list = f.GetValue(__instance) as List<BlueprintAnswer>;
                    if (list == null || list.Count < 2) return;

                    // ★把我们**全部**条目一起挪到退出项之前★
                    // v0.38.0 起我们有 3 条选项，原来只挪"找到的第一条"，
                    // 剩下的留在原位 ⇒ 玩家看到「卫队」又跑回第六个位置。
                    var ours = new List<BlueprintAnswer>();
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] == null) continue;
                        bool isOurs = false;
                        foreach (var e in Entries) if (e.Guid == list[i].AssetGuid) { isOurs = true; break; }
                        if (isOurs) { ours.Insert(0, list[i]); list.RemoveAt(i); }
                    }
                    if (ours.Count == 0) return;

                    // 已经摘干净了，现在在**剩下的 vanilla 条目**里找退出项。
                    // 不用再补偿下标 —— 上一版那套 mine/target 互相偏移的算术
                    // 只在"只挪一条"时成立，条目变成 3 条之后就错了。
                    int target = -1;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] == null) continue;
                        string nm = list[i].name ?? "";
                        foreach (var k in ExitKeys)
                            if (nm.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { target = i; break; }
                        if (target >= 0) break;
                    }
                    // 认不出退出项 ⇒ 放到**最后一条之前**（原版最后一个可见选项就是退出）。
                    // ★别写成 list.Count★ 那是"追加到末尾"，等于排到退出项**后面**——
                    // v0.41.0 就是这么写的，玩家实测：两条选项跑到了「离开」下面。
                    // 这里的 list 已经把我们自己的条目摘干净了，所以 Count-1 就是退出项的下标。
                    if (target < 0) target = list.Count - 1;
                    if (target < 0) target = 0;
                    if (target > list.Count) target = list.Count;

                    list.InsertRange(target, ours);

                    if (!_orderLogged)
                    {
                        _orderLogged = true;
                        Main.LogVerbose("[对话注入] 可见列表重排：" + ours.Count + " 条我们的选项整体挪到第 "
                                 + target + " 位（退出项之前），共 " + list.Count + " 条可见。");
                    }
                }
                catch (Exception e) { Main.LogError("[招募对话] 重排可见列表失败: " + e.Message); }
            }
        }

        private static bool _orderLogged;
        private static readonly string[] ExitKeys = { "Exit", "Leave", "Goodbye", "Bye", "Farewell" };

        /// <summary>
        /// 钩住"这条 answer 被选中"。动作放在这里而不是 answer.OnSelect —— 见类注释里的硬规则 3。
        ///
        /// ★ 必须给参数类型签名 ★ DialogController 有两个重载（已反编译确认）：
        ///     public void SelectAnswer(string answerGuid)                                // :629
        ///     public void SelectAnswer(BlueprintAnswer answer, BaseUnitEntity m = null)  // :643
        /// 不给 argumentTypes ⇒ AccessTools.DeclaredMethod 走 type.GetMethod(name)
        /// ⇒ AmbiguousMatchException ⇒ 整个 PatchAll 中止（v0.10.1~0.10.9 的病根）。
        /// 带默认值的可选参数在反射签名里照样算一个形参，所以两个类型都得写。
        /// 只钩 2 参重载就够：string 重载内部（:637）会转调它。
        ///
        /// ★ 为什么 Prefix 返回 false（跳过原方法）★
        /// SelectAnswer :664 有
        ///     var bookEventLog = Game.Instance.Player.Dialog.BookEventLog;
        ///     if (bookEventLog.ContainsKey(Dialog)) bookEventLog[Dialog].Add(answer);
        /// 而 DialogState.BookEventLog 是
        ///     [JsonProperty] Dictionary&lt;BlueprintDialog, List&lt;BlueprintScriptableObject&gt;&gt;
        /// —— **类型化蓝图引用**，不是 GUID 字符串。只要我们的选项在任何书页事件对话里被选中，
        /// 自建蓝图就以类型化引用进了存档 ⇒ 触碰存档红线。
        /// 返回 false 让原方法完全不跑，顺带避开 AddHistoryEntry / ApplyShiftDialog /
        /// ReceiveRewards / ScheduleCue。
        ///
        /// ★ 时序注意 ★ 今天因为 CharacterSelection 是 null，SelectAnswer 会在 :654 就 NRE，
        /// 早于 :664 写 BookEventLog，所以反而没腐坏存档 —— 但 EnsureAnswer 的空字段修复一上，
        /// 这个口子就打开了。**所以本补丁和 EnsureAnswer 的修复必须同批上线。**
        ///
        /// 一旦确认是我们自己的 answer，无论开窗成不成功都返回 false ——
        /// 绝不能因为 RecruitWindow 抛异常就把控制权交回去，那等于把红线又打开了。
        /// </summary>
        [HarmonyPatch(typeof(DialogController), "SelectAnswer",
                      new Type[] { typeof(BlueprintAnswer), typeof(BaseUnitEntity) })]
        public static class SelectPatch
        {
            // 形参名须与原方法一致（answer）；不关心的形参可以省略不写。
            private static bool Prefix(BlueprintAnswer answer)
            {
                // 别人的选项：原样放行，零副作用。绝不能在这里加日志（每条对话选项都走这里）。
                if (answer == null) return true;
                Entry entry = null;
                foreach (var e in Entries) if (e.Guid == answer.AssetGuid) { entry = e; break; }
                if (entry == null) return true;   // 别人的选项，原样放行

                try
                {
                    Main.Log("[对话注入] 玩家选择了「" + Label(entry) + "」");

                    var dc = Game.Instance != null ? Game.Instance.DialogController : null;

                    // ★ 补上"已点过"标记 ★
                    // vanilla SelectAnswer:662-663 就是这两行，我们 return false 跳过了原方法，
                    // 所以选项一直显示成没点过的亮色。这里手动补，且**只补这两行** ——
                    // 原方法里其余的（BookEventLog / AddHistoryEntry / ReceiveRewards /
                    // ApplyShiftDialog / ScheduleCue）一概不要。
                    //
                    // 存档安全性：
                    //   · DialogState.SelectedAnswers 是 [JsonProperty] readonly HashSet<string>，
                    //     SelectedAnswersAdd(bp) 只存 bp.AssetGuid 字符串 —— 不透明 GUID，
                    //     永不经过 BlueprintConverter 解析，卸载 mod 后是一段死文本，无害。
                    //   · LocalSelectedAnswers 是 DialogController 上的 runtime 字段
                    //     （无 [JsonProperty]，且 :514 每次开对话都 Clear），不进存档。
                    // 对比之下 BookEventLog 是 Dictionary<BlueprintDialog, List<BlueprintScriptableObject>>
                    // ——类型化蓝图引用，那个才是碰不得的，所以坚决不走原方法。
                    try
                    {
                        var st = Game.Instance != null && Game.Instance.Player != null
                               ? Game.Instance.Player.Dialog : null;
                        if (st != null) st.SelectedAnswersAdd(answer);
                        if (dc != null && dc.LocalSelectedAnswers != null) dc.LocalSelectedAnswers.Add(answer);
                    }
                    catch (Exception e) { Main.LogError("[招募对话] 标记已选失败: " + e.Message); }

                    // ★ 关掉原版对话框，只留我们的窗口 ★
                    // 用**不带 force** 的重载：vanilla 正常结束对话走的就是这条
                    //（PlayCue(null) -> StopDialog()），它会跑 dialog.FinishActions ——
                    // 等价于玩家点了"退出对话"。force:true 会跳过 FinishActions，
                    // 可能留下原版预期该设的标记，反而更不安全。
                    // StopDialog 内部有 DialogStopScheduled 自锁，重复调用无害。
                    if (!entry.KeepDialog)
                    {
                        if (dc != null) dc.StopDialog();
                        else Main.LogError("[对话注入] 拿不到 DialogController，对话框不会自动关闭。");
                    }

                    // StopDialog() 同步派发 IDialogInteractionHandler，且 StopMode(Dialog) 是**延迟生效**的；
                    // 在 SelectAnswer 的 Harmony prefix 里同帧建 UI = 在 EventBus 派发中重入。推迟 2 帧跨过它。
                    var act = entry.OnPicked;
                    if (act != null) Deferred.NextFrames(2, delegate { try { act(); } catch (Exception ex) { Main.LogError("[对话注入] 动作失败: " + ex); } });
                }
                catch (Exception e) { Main.LogError("[招募对话] 选择处理失败: " + e); }

                return false;   // ★ 无条件跳过原方法 ★
            }
        }
    }
}
