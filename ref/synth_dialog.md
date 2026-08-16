# 修复清单 · KgdRetinue 对话选项

我把五路调查 + 核验的结论**全部重新独立验证过一遍**（反编译 + TypeDef 表 dump + 日志核对）。核验方推翻调查方的地方以核验为准；核验方之间互相矛盾的地方，以我自己的实测为准，下面会明确标出。

---

## 0. 先纠正一个前提：PatchAll 中止**没有**"挡住其他一切"

你的任务描述里说"先修 PatchAll 中止，因为它挡住了其他一切"。这个前提**不成立**，我实测推翻了它：

```
$ ilspycmd --dump-table TypeDef "…/UnityModManager/KgdRetinue/KgdRetinue.dll"
RID  9  CameraFollowPatch      (顶层)
RID 12  GuardKillCreditPatch   (顶层)
RID 13  InventoryPatch         (顶层)
RID 17  MomentumGroupPatch     (顶层)
RID 18  MomentumPatch          (顶层)
RID 29  VeilPatch              (顶层)
RID 30  XpPatch                (顶层)
RID 31  TraumaPatch            (顶层)
RID 32  TraumaHealPatch        (顶层)
RID 43  LocalizationPatch      (NestedPublic in RecruitDialog)
RID 44  SelectPatch            (NestedPublic in RecruitDialog)   ← 抛异常者
RID 45~50 = Drawer / <States>d__11 / <>c / __StaticArrayInit… / Ent   ← 无一是补丁类
```

源码里 `[HarmonyPatch` 共 **11** 个，与上表一一对应。`SelectPatch` 是**最后一个**。`Harmony.PatchAll` 用 `GetTypesFromAssembly(asm).Do(...)` 按 TypeDef 行号遍历，所以它抛异常时其余 10 个**已经全部处理完毕**。

运行日志正面印证（中止发生在 22:37:29）：
- `22:39:17` `[背包] 已拦截 RestoreSharedInventory` ← InventoryPatch 活着
- `22:39:36` 连续 4 条 `[帷幕] 已拦截：卫兵…灵能不计入亚空间威胁` ← VeilPatch 活着

**所以真实故障是三个互相独立的 bug，不是一条链：**

| # | 故障 | 症状 | 与 PatchAll 中止的关系 |
|---|---|---|---|
| 1 | `new BlueprintAnswer()` 留下 null 字段 → `CanShow()` NRE | 左侧 NPC 区空白、只剩占位文字 | **无关** |
| 2 | `SelectPatch` 未消歧 → AmbiguousMatchException | 点选项没反应 | 就是它 |
| 3 | `LocalizationPatch` 类型名写错 → `Prepare()` 静默 false | 选项文字为空 | **无关**（RID 43 排在 44 前面，轮得到它） |

故障 1 才是玩家实际看到的那个症状的根因，日志已实锤（下面 C 节）。

---

## 1. 修复清单（按落地顺序）

| 序 | 文件 | 改什么 | 为什么（一句话因果） |
|---|---|---|---|
| **A** | `Main.cs` L29-35 | `PatchAll` → 逐类 try/catch + 三态统计日志 | `CollectionExtensions.Do` 无逐项保护，一个补丁类抛异常会让**排在它后面的**全部静默跳过；且 `Prepare()==false` 是**第三态**（无异常无日志），现有代码看不见 |
| **B** | `RecruitDialog.cs` L466-480 | `SelectPatch` 补参数签名 + `Prefix` 改为返回 `bool` 并对自家 answer 返回 `false` | `DialogController` 有两个 `SelectAnswer` 重载 ⇒ 必然 Ambiguous；返回 `false` 顺带彻底堵死 `BookEventLog` 存档红线 |
| **C** | `RecruitDialog.cs` L69-108 + L195/208/243 | `EnsureAnswer()` 补齐 7 个 null 字段；删掉 `SetNextCue(_answer, hostCue)` | `HasShowCheck => ShowCheck.Type` 在 `CanShow()` 里裸解引用 null ⇒ NRE ⇒ `PlayBasicCue` 里后续的 `HandleOnCueShow` 永不执行 ⇒ 整个对话 UI 停在初始状态 |
| **D** | `RecruitDialog.cs` L43-61 | `LocalizationPatch` 的类型全名补 `.Shared` + `Prepare()` 加告警日志 | `Kingmaker.Localization.LocalizationPack` **不存在**，真名是 `Kingmaker.Localization.Shared.LocalizationPack`（LocalizationShared.dll）⇒ `TypeByName` 三级回退全落空 ⇒ 补丁静默不装 |

顺序说明：A 先做（它是安全网，且改完 B 之后 A 才有可观测的验收数字）；C 是真正让玩家看到变化的那个；B、D 各自独立。

---

## 2. 完整可编译代码

### 【A】`D:/RT_RetinueMod/src/KgdRetinue/Main.cs`

**替换第 29~35 行**（从 `HarmonyInstance = new Harmony(...)` 到 `catch (Exception e) { LogError("Harmony 补丁失败…"); }` 整段）为：

```csharp
            HarmonyInstance = new Harmony(modEntry.Info.Id);
            PatchAllSafe(HarmonyInstance, System.Reflection.Assembly.GetExecutingAssembly());
```

**然后在 `Main` 类内新增下面这个方法**（放在 `Load` 之后即可；`using System;` 与 `using HarmonyLib;` 文件头已有，其余全部用全限定名，不需要动 using）：

```csharp
        /// <summary>
        /// 逐类打补丁，替代 Harmony.PatchAll(Assembly)。
        ///
        /// 为什么不能用 PatchAll（已反编译 0Harmony.dll 2.2.2.0 确认）：
        ///     public void PatchAll(Assembly a)
        ///         => AccessTools.GetTypesFromAssembly(a).Do(t => CreateClassProcessor(t).Patch());
        /// 而 CollectionExtensions.Do 是裸的 while(MoveNext()){action(...)}，没有逐项 try/catch；
        /// PatchClassProcessor.Patch() 虽然内部 try 了，但末尾 ReportException 会把异常再抛出去。
        /// ⇒ 第一个抛异常的补丁类会让**排在它后面**的所有类被静默跳过，
        ///    而外面那层 try/catch 只能看到一条错误，看不出还丢了什么。
        /// 遍历顺序 = 程序集 TypeDef 表行号（Roslyn 先发全部顶层类型、再发全部嵌套类型），
        /// 纯属编译顺序运气，不能当保障。
        ///
        /// ★ 三态，别把 inert 当成功也别当失败 ★
        ///   ok    : Patch() 返回非空列表 —— 真的挂上了
        ///   failed: 抛异常 —— 会被下面 catch 住并记名
        ///   inert : Patch() 返回 null（无 [HarmonyPatch] 标注）
        ///           或返回空列表（Prepare() 返回 false / TargetMethod() 返回 null）
        ///           —— 这一态原本**完全静默**，是 LocalizationPatch 死了一整晚没人发现的原因，
        ///              所以这里专门把"带标注却一个方法都没打上"的类挑出来报错。
        /// </summary>
        private static void PatchAllSafe(Harmony harmony, System.Reflection.Assembly asm)
        {
            if (harmony == null || asm == null)
            {
                LogError("[Harmony] 实例或程序集为空，补丁全部跳过。");
                return;
            }

            System.Collections.Generic.IEnumerable<Type> types;
            try
            {
                // 与 PatchAll 内部同一个取法：它已处理 ReflectionTypeLoadException 并过滤掉 null
                types = AccessTools.GetTypesFromAssembly(asm);
            }
            catch (Exception e)
            {
                LogError("[Harmony] 取类型列表失败，补丁全部跳过: " + e);
                return;
            }

            int ok = 0;
            var failedNames = new System.Collections.Generic.List<string>();
            var inertNames  = new System.Collections.Generic.List<string>();

            foreach (var t in types)
            {
                if (t == null) continue;

                bool isPatchClass;
                try { isPatchClass = t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0; }
                catch { isPatchClass = false; }

                try
                {
                    // CreateClassProcessor 对没标注的类型是廉价的：Patch() 首句就 return null
                    var applied = harmony.CreateClassProcessor(t).Patch();
                    if (applied != null && applied.Count > 0)
                    {
                        ok++;
                        Log("[Harmony] OK   " + t.FullName + "  → " + applied.Count + " 个方法");
                    }
                    else if (isPatchClass)
                    {
                        inertNames.Add(t.FullName);
                    }
                }
                catch (Exception e)
                {
                    failedNames.Add(t.FullName);
                    var root = e;
                    while (root.InnerException != null) root = root.InnerException;
                    LogError("[Harmony] FAIL " + t.FullName + "  —— " + root.GetType().Name + ": " + root.Message);
                    LogError(e);   // 完整堆栈另起一条
                }
            }

            Log("[Harmony] 补丁完成：成功 " + ok + " 个类，失败 " + failedNames.Count
                + "，带标注却未生效 " + inertNames.Count + " 个。");

            if (failedNames.Count > 0)
                LogError("[Harmony] 失败清单: " + string.Join(", ", failedNames.ToArray()));
            if (inertNames.Count > 0)
                LogError("[Harmony] 静默未生效清单（多半是 Prepare() 返回 false 或 TargetMethod() 返回 null）: "
                         + string.Join(", ", inertNames.ToArray()));
        }
```

**怎么验证改对了**：进游戏后 `kgd_log.txt` 出现 `[Harmony] 补丁完成：成功 11 个类，失败 0，带标注却未生效 0 个。`
（只做 A、不做 B/D 时期望值是 **成功 9、失败 1（SelectPatch）、未生效 1（LocalizationPatch）** —— 看到这个数字说明 A 生效了，同时正好把 B、D 两个 bug 显式点名。）

---

### 【B】`RecruitDialog.cs` — `SelectPatch`

**替换第 462~480 行**（`/// <summary> 钩住"这条 answer 被选中"…` 注释块到 `SelectPatch` 类结束）为：

```csharp
        /// <summary>
        /// 钩住"这条 answer 被选中"。动作放在这里而不是 answer.OnSelect —— 见类注释里的硬规则 3。
        ///
        /// ★ 必须给参数类型签名 ★ DialogController 有两个重载（Code.dll）：
        ///     public void SelectAnswer(string answerGuid)                                   // 629 行
        ///     public void SelectAnswer(BlueprintAnswer a, BaseUnitEntity manual = null)     // 643 行
        /// 不给 argumentTypes ⇒ AccessTools.DeclaredMethod 走 type.GetMethod(name) ⇒ AmbiguousMatchException。
        /// 带默认值的可选参数在反射签名里**照样算一个形参**，所以两个类型都得写。
        /// 只钩 2 参重载就够：string 重载内部（637 行）会转调它，联机的
        /// DialogAnswerGameCommand 也走 string 重载，两条入口都覆盖到。
        ///
        /// ★ 为什么 Prefix 返回 false（跳过原方法）★
        /// DialogController.SelectAnswer 第 664 行：
        ///     var bookEventLog = Game.Instance.Player.Dialog.BookEventLog;
        ///     if (bookEventLog.ContainsKey(Dialog)) bookEventLog[Dialog].Add(answer);
        /// 而 DialogState.BookEventLog 是
        ///     [JsonProperty] Dictionary&lt;BlueprintDialog, List&lt;BlueprintScriptableObject&gt;&gt;
        /// —— **类型化蓝图引用**，不是 GUID 字符串。只要我们的选项被选中于任何书页事件对话，
        /// 我们自建的蓝图就以类型化引用进了存档 ⇒ 触碰存档红线。
        /// 返回 false 让原方法完全不跑，顺带也避开了 AddHistoryEntry / ApplyShiftDialog /
        /// ReceiveRewards / ScheduleCue，是对硬约束最干净的形态。
        /// 注意：一旦确认是我们自己的 answer，无论开窗成不成功都返回 false ——
        /// 绝不能因为 RecruitWindow 抛异常就把控制权交回去，那等于把红线又打开了。
        /// </summary>
        [HarmonyPatch(typeof(DialogController), "SelectAnswer",
                      new Type[] { typeof(BlueprintAnswer), typeof(BaseUnitEntity) })]
        public static class SelectPatch
        {
            // 形参名必须与原方法一致（answer / manualUnitSelection）；不关心的形参可以省略不写。
            private static bool Prefix(BlueprintAnswer answer)
            {
                // 别人的选项：原样放行，零副作用。绝不能在这里加日志（每条对话选项都会走这里）。
                if (answer == null || answer.AssetGuid != AnswerGuid) return true;

                try
                {
                    Main.Log("[招募对话] 玩家选择了征募选项");
                    // 如果实测发现对话窗抢焦点、招募面板点不动，就取消下面这行注释：
                    // Game.Instance.DialogController.StopDialog();
                    RecruitWindow.Open(null);
                }
                catch (Exception e) { Main.LogError("[招募对话] 选择处理失败: " + e); }

                return false;   // ★ 无条件跳过原方法 ★
            }
        }
```

**怎么验证改对了**：
1. `kgd_log.txt` 里不再出现 `Ambiguous match for … SelectAnswer`；
2. `[Harmony] OK   KgdRetinue.RecruitDialog+SelectPatch → 1 个方法`；
3. 点选项后出现 `[招募对话] 玩家选择了征募选项`（**该行在全部 24 次历史加载中出现 0 次**，从未成功过）。

---

### 【C】`RecruitDialog.cs` — answer 构造 / 文案 / NextCue

#### C-1 文件头新增两个 using（第 12 行 `using Kingmaker.UnitLogic.Interaction;` 之后）

```csharp
using Kingmaker.ElementsSystem;            // ConditionsChecker, ActionList  → RogueTrader.GameCore.dll（csproj 已引用）
using Kingmaker.EntitySystem.Stats.Base;   // StatType                       → 同名 dll（csproj 已引用）
```

> `ShowCheck` 在 `Kingmaker.DialogSystem.Blueprints`、`CheckData`/`CharacterSelection`/`CueSelection` 在 `Kingmaker.DialogSystem` —— 这两个 using 文件里已经有了，**不要重复添加**（CS0105 警告）。**csproj 完全不用改。**

#### C-2 替换第 68~108 行（`EnsureAnswer` + `SetText` 两个方法整体）

```csharp
        /// <summary>构造并注册我们的 answer（只做一次）。</summary>
        private static BlueprintAnswer EnsureAnswer()
        {
            if (_answer != null) return _answer;
            try
            {
                // SimpleBlueprint 是普通类不是 ScriptableObject，AssetGuid 就是 string
                var a = new BlueprintAnswer();
                a.name = "Kgd_RecruitAnswer";
                a.AssetGuid = AnswerGuid;

                // ══════════════════════════════════════════════════════════════════
                // ★★★ 头号 bug 修复点 ★★★
                // new BlueprintAnswer() 只跑 C# 字段初始化器；平时由 JSON 反序列化填的
                // 引用类型字段全是 null，而游戏代码一律当非空用、且**没有 try/catch**。
                //
                // 实测（GameLogFull.txt 23:40:21:525）：
                //     System.NullReferenceException
                //        at BlueprintAnswer.CanShow()
                //        at DialogController.PlayBasicCue(BlueprintCue)
                //        at DialogController.PlayCue(BlueprintCueBase)
                //        at DialogController.Tick()
                // 崩点是 CanShow() 的第三个 if：
                //     public bool HasShowCheck => ShowCheck.Type != StatType.Unknown;
                // ShowCheck 是 [Serializable] **class**（不是 struct）且无初始化器 ⇒ null ⇒ NRE。
                //
                // 后果不止"我们这条选项没了"：DialogController.PlayBasicCue 里是
                //     AddAnswers(...);                                   // ← 这里抛，且 m_Answers 已被 Clear
                //     EventBus.RaiseEvent(h => h.HandleOnCueShow(...));  // ← 永不执行
                // HandleOnCueShow 是 DialogVM 唯一给 Cue/Answers/SpeakerPortrait/SpeakerName
                // 赋值的地方 ⇒ 全部停在 null ⇒ 头像 SetActive(false)、名字 SetActive(false)、
                // m_CueView.Bind(null) 提前 return ⇒ TMP 保留 prefab 设计期占位文字。
                // 这就是玩家看到的"左侧 NPC 区变空、只剩占位文本"。
                //
                // 同时它也解释了"日志里没有 missing string"：UI 根本没走到读文案那一步。
                // ══════════════════════════════════════════════════════════════════

                // CanShow() 第 3 个 if。Type 默认 StatType.Unknown(0) ⇒ HasShowCheck 恒 false，
                // 于是永远不会碰到 private 的 OnCheckSuccess / OnCheckFail（那两个字段设不了，
                // 但调用点是 ?.Run()，留 null 完全安全，不要试图去反射填）。
                a.ShowCheck = new ShowCheck();

                // CanShow() 末尾 ShowConditions.Check(this)；CanSelect() 里 SelectConditions.Check()。
                // ConditionsChecker.Conditions 本身没有初始化器（是 null），但 HasConditions 判了
                // conditions != null、Check() 在 !HasConditions 时提前 return true ⇒ 裸 new 就够。
                a.ShowConditions   = new ConditionsChecker();
                a.SelectConditions = new ConditionsChecker();

                // BlueprintAnswer.HasExchangeData → HasExchangeDataOnSelect() → OnSelect.Actions。
                // ActionList.Actions 自带 = new GameAction[0] ⇒ 裸 new 就够。
                // ★ 硬规则 3：这里永远保持空动作列表，绝不放授予 fact / 召唤单位的动作 ★
                a.OnSelect = new ActionList();

                // BlueprintAnswer.SkillChecks / SkillChecksDC 第一句就是 FakeChecks.Length。
                // DialogAnswerBaseView.BindViewImplementation 会读 SkillChecksDC。
                a.FakeChecks = new CheckData[0];

                // SkillChecksDC 里 CharacterSelection.SelectUnit(...)；
                // （原方法 SelectAnswer 第 654 行也读 .SelectionType，但我们的 Prefix 已跳过它）。
                // Type.Clear(=0) ⇒ SelectUnit 直接返回 null，不指定行动单位，正是我们要的。
                a.CharacterSelection = new CharacterSelection
                {
                    SelectionType   = CharacterSelection.Type.Clear,
                    ComparisonStats = new StatType[0],   // 只有 Manual/Random 分支会读，防御性填上
                };

                // ★ NextCue 必须非 null 但**保持空** ★
                // 读它的地方：SkillChecksDC（NextCue.Cues.Dereference()）、SkillChecks（NextCue.Select()）、
                // CanShow() 的 RequireValidCue 分支、以及我们自己的 FindExitIndexInList / CollectCues。
                // 为什么不指回宿主 cue（原来第 243 行 SetNextCue 干的事）：
                //   a) AnswersList_0002 同时挂在 20+ 个 cue 上，玩家可能是从别的 cue 进的菜单，硬跳会重放不相干台词；
                //   b) 若目标 cue 勾了 ShowOnce，PlayCue 每次都 ShownCuesAdd ⇒ CanShow() 必 false
                //      ⇒ Select() 返回 null ⇒ PlayCue(null) ⇒ StopDialog()，表现为"选完对话莫名关掉"；
                //   c) 重播会重跑 cue.ApplyShiftDialog() / ReceiveRewards()，污染玩家数值。
                // 而我们的 Prefix 返回 false，原方法根本不跑，NextCue 在流程上已经无关紧要。
                a.NextCue = new CueSelection();

                a.Description = new LocalizedString();

                a.ShowOnce              = false;
                a.ShowOnceCurrentDialog = false;
                a.RequireValidCue       = false;   // 必须 false：NextCue 为空时 true 会让 CanShow() 过滤掉自己
                a.DebugMode             = false;
                a.AddToHistory          = false;   // 纵深防御：Prefix 已跳过 AddHistoryEntry，这里再关一道

                // AddToHistory / SoulMarkShift / SoulMarkRequirement 有初始化器；
                // Components / m_AllElements 走 ComponentsArray / ElementsArray 惰性建表，都不用管。

                SetText(a);

                // ── 自检：只做纯空值断言，★绝不调用 CanShow()/CanSelect()★ ──
                // 理由：EnsureAnswer 在**区域加载时**执行（不在对话中）。
                //   · CanShow() 成功路径会走 DialogDebug.Add(this,"answer shown") → GameHistoryLog 写对话历史，是噪声；
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

                ResourcesLibrary.BlueprintsCache.AddCachedBlueprint(AnswerGuid, a);
                _answer = a;
                Main.Log("[招募对话] answer 已注册 " + AnswerGuid);
            }
            catch (Exception e) { Main.LogError("[招募对话] 构造 answer 失败: " + e); }
            return _answer;
        }

        /// <summary>
        /// 设置选项文案。BlueprintAnswer.Text 是 private LocalizedString，只能反射写。
        /// LocalizedString 没有任何缓存字段，取值时每次都去 LocalizationManager.CurrentPack 查表，
        /// 所以这里只设 Key，真正的文字由 LocalizationPatch 在查表函数上拦下来回填。
        /// </summary>
        private static void SetText(BlueprintAnswer a)
        {
            try
            {
                // Text 由 BlueprintAnswer **自身**声明（基类没有同名字段），所以这里恒能找到；
                // 保留判空只是为了将来版本改名时能立刻看见，而不是像以前那样静默 return。
                var f = typeof(BlueprintAnswer).GetField("Text",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f == null)
                {
                    Main.LogError("[招募对话] 找不到 BlueprintAnswer.Text 字段，选项将没有文字。");
                    return;
                }
                var ls = new LocalizedString();
                ls.Key = TextKey;
                f.SetValue(a, ls);
                Main.Log("[招募对话] Text.Key 已设为 " + TextKey);
            }
            catch (Exception e) { Main.LogError("[招募对话] 设置文案失败: " + e); }
        }
```

#### C-3 删掉 `NextCue` 指回宿主 cue 的三处（`InjectInto` 内）

**第 195 行**，删除：
```csharp
                BlueprintCue hostCue = null;   // 菜单所在的 cue，用来给 NextCue 指回去
```

**第 208 行**，把
```csharp
                        { best = lst; bestWhere = cue.name; hostCue = cue; }
```
改成
```csharp
                        { best = lst; bestWhere = cue.name; }
```

**第 239~243 行**，把
```csharp
                        // ★ NextCue 必须指回菜单所在的 cue ★
                        // 之前留空是为了少造一个 BlueprintCue、少一个 AssetId，
                        // 但实测选完之后对话跳进空状态：左边只剩占位文本、NPC 头像和台词都没了。
                        // 指回宿主 cue，选完就回到原来的选项菜单，行为跟原版的"次要话题"一致。
                        SetNextCue(_answer, hostCue);
```
改成
```csharp
                        // ★ 不再给 NextCue 指回宿主 cue ★
                        // 当初那么做是因为误判：以为"对话跳进空状态"是 NextCue 留空造成的。
                        // 真凶是 CanShow() 的 NRE（见 EnsureAnswer 里的长注释）——
                        // 空 NextCue 只会走 PlayCue(null) → StopDialog()，而且我们的 Prefix
                        // 现在直接跳过 SelectAnswer，NextCue 压根不会被消费。
                        // 指回 vanilla cue 反而有重放台词 / 撞 ShowOnce / 重复 ApplyShiftDialog 的风险。
```

> `SetNextCue` 方法（第 294~308 行）自此无人调用。留着不会报错（未使用的 private 方法在 C# 里无警告），想删也可以；我建议**删掉**，免得以后有人又把它接回去。

**怎么验证改对了**：
1. `GameLogFull.txt` 里不再出现 `at Kingmaker.DialogSystem.Blueprints.BlueprintAnswer.CanShow()` 的 NullReferenceException；
2. `GameLogFull.txt` 出现 `Dialog Kgd_RecruitAnswer: answer shown` —— **有这行才证明选项真的进了 `m_Answers`**；
3. 左侧 NPC 头像、名字、台词恢复正常（不再是空白 + 占位文字）。

---

### 【D】`RecruitDialog.cs` — `LocalizationPatch`

**替换第 38~61 行**（注释块 + 整个 `LocalizationPatch` 类）为：

```csharp
        /// <summary>
        /// 让本地化查表认识我们的 key。
        /// LocalizedString.Text → GetText → LoadImpl → LocalizedString.TryGetText(pack, out)
        ///                     → pack.TryGetText(key, out text)   ← 钩最后这一步
        /// 只拦我们自己的 key，其余原样放行。
        ///
        /// ★★★ 之前这里写的类型名是错的，导致本补丁从 v0.10.1 起从未生效过 ★★★
        /// 真名是 Kingmaker.Localization.**Shared**.LocalizationPack，位于 LocalizationShared.dll。
        /// （ilspycmd 对 "Kingmaker.Localization.LocalizationPack" 直接报
        ///   Could not find a type named … —— 这个类型压根不存在。）
        /// AccessTools.TypeByName 的三级回退全部落空：
        ///   1) Type.GetType(name)                    → null（无程序集限定名）
        ///   2) AllTypes().First(t => t.FullName==n)  → null（FullName 多了 .Shared）
        ///   3) AllTypes().First(t => t.Name==n)      → null（Name 只是 "LocalizationPack"，不含点号）
        /// ⇒ TargetMethod() 返回 null ⇒ Prepare() 返回 false ⇒ PatchClassProcessor.Patch() 走
        ///      RunMethod&lt;HarmonyCleanup&gt;(...); ReportException(null, null); return new List&lt;MethodInfo&gt;();
        ///   而 ReportException 第一句就是 if (exception == null) return;
        /// ⇒ **静默返回空列表，不抛异常、不打任何日志**。这就是它死了一整晚没人发现的原因。
        /// 所以下面的 Prepare() 必须自己打一条错误日志。
        ///
        /// 注：不能改成 typeof(LocalizationPack) —— csproj 没有引用 LocalizationShared.dll，
        ///     那样会 CS0246。必须继续走字符串版 TypeByName。
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

                // LocalizationPack.TryGetText(string key, out string text, bool reportUnknown = true)
                // 全类只有这一个 TryGetText（GetText 是另一个名字），不会二次歧义。
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

            // 形参名必须与原方法一致（key / text）；原方法的 out 在补丁里用 ref 接是 Harmony 的正确写法；
            // 不关心的 reportUnknown 省略不写即可。
            private static bool Prefix(string key, ref string text, ref bool __result)
            {
                if (key != TextKey) return true;
                text = TextValue;
                __result = true;
                return false;
            }
        }
```

**怎么验证改对了**：
1. `kgd_log.txt` 出现 `[Harmony] OK   KgdRetinue.RecruitDialog+LocalizationPatch → 1 个方法`，且**不再**出现 `找不到 LocalizationPack.TryGetText`；
2. `GameLogFull.txt` 里**没有** `missing string: kgd_recruit_answer`（该通道是通的 —— 日志里能看到别的 key 的 missing string，说明缺这条就是真没查到）；
3. 选项文字显示为「（护卫队）关于我的护卫队……」。

> **备选方案（不采用，仅备案）**：`LocalizationPack.PutString(string, string)` 是 public 且全 Managed 目录只有它自己有这个符号（无任何调用者），可以不打补丁直接注册字符串。但需要给 csproj 加 `LocalizationShared` + `Kingmaker.Localization.Enums` 两个 Reference，还要订阅 `LocalizationManager.LocaleChanged`（实测一次启动里 `LocalizationManager.Init` 跑了**两次**：23:38:50 与 23:39:32，mod 在中间加载，`CurrentPack` 会被整个换掉）。改一个字符串就能解决的事不值得引入这些，**先按【D】做**；只有当 Harmony 钩子实测仍不生效时才升级到这个方案。

---

## 3. 仍需进游戏验证的（不要当成已确认）

| # | 事项 | 现状 | 判定方法 |
|---|---|---|---|
| 1 | **占位串到底是不是 "TEXT"** | ui 调查员把 `surfacedialogpcview.res` 全部 10 个 LZ4 block 解开逐字节搜过，**`TEXT` 作为 Unity 序列化字符串命中 0 次**；实际挖到的占位是 `<alpha=#85>0.<alpha=#FF><indent=1.2em>Option</indent>`。68 个 UI bundle、`blueprint.assets`、三个本地化包、四个相关 dll 全部 0 命中。所以"TMP 保留 prefab 占位"这个**机制**已证明，但"占位恰好是 TEXT"**被证伪**。 | 修完后截图看实际显示的是什么串 |
| 2 | **玩家描述的"选项能显示、能点击"与代码矛盾** | 按当前代码，`CanShow()` 在下标 20 处 NRE，answer 根本进不了 `m_Answers`，玩家不可能点到它。合理解释是他看到的是**崩溃后未刷新的残留 UI**，或更早一版构建。 | 修完后看有没有 `Dialog Kgd_RecruitAnswer: answer shown` |
| 3 | **Prefix 返回 false 后的观感** | 对话窗会停在当前 cue 不动，招募窗（IMGUI OnGUI 覆盖层）叠在上面；该选项会显示成"已选中"高亮（`AnswerVM.WasChoose` 在 UI 侧已置位，纯视觉，重进对话即复原）。IMGUI 能否在 uGUI Canvas 之上正常接收点击，**没有实测过**。 | 观感不好或点不动，就取消 `StopDialog()` 那行注释 |
| 4 | **士气 / 创伤 / XP / 摄像机四组补丁是否真的一直活着** | 22:37 之后 InventoryPatch、VeilPatch 有**正面日志**；其余六个是靠 TypeDef RID 顺序推证（RID 9~32 < 44），**没有正面观测**。 | 开着 WatchMomentum 打一场战斗 |
| 5 | **多人联机** | `DialogAnswerGameCommand.IsSynchronized => true`，选项选择走网络同步；我们自建的 answer GUID 在未装 mod 的客户端上无法解析。单机路径已覆盖（string 重载转调 2 参重载），**联机未评估**。 | — |
| 6 | **`Cue_7` 是否真的勾了 `ShowOnce`** | 没解出 `blueprints-pack.bbp` 验证。这只影响"当初 SetNextCue 到底有多危险"的措辞，不影响修复（我们已彻底不指向 vanilla cue）。 | — |
| 7 | **`BookEventLog` 里的失效 GUID 是否真会导致存档打不开** | 它是 `List<BlueprintScriptableObject>`（列表能容纳 null 元素），比 `EntityFactSource.m_Blueprint` 那种类型化标量字段宽容，反序列化器的容错行为**没有实测**。按最坏情况处理。 | 需要存档修复工具 |

---

## 4. 因 Bug 静默失效过的功能（诚实汇报）

先说结论：**因 PatchAll 中止而失效的功能只有一个 —— `SelectPatch`。** 其余 9 个玩法补丁一直是好的，我有正面日志。

| 功能 | 补丁类 | 起始失效版本 | 失效原因 | 是否与 PatchAll 中止有关 |
|---|---|---|---|---|
| 点对话选项弹招募窗 | `RecruitDialog+SelectPatch` | **v0.10.1**（`22:37:29`，首条 AmbiguousMatchException）| 未给参数签名 → AmbiguousMatchException | ✅ 就是它。而且它自诞生起**一次都没生效过**：`玩家选择了征募选项` 在全部 24 次加载中出现 **0** 次 |
| 招募选项的中文文案 | `RecruitDialog+LocalizationPatch` | **v0.10.1**（RecruitDialog.cs 首次出现于 `22:38:24`）| `TypeByName` 类型全名写错 `.Shared` → `Prepare()` 返回 false → **静默跳过** | ❌ 无关。它 RID 43 排在 SelectPatch(44) 前面，本来就轮得到它 |

**受影响版本**：`0.10.1 / 0.10.2 / 0.10.3 / 0.10.7 / 0.10.8 / 0.10.9`（共 6 次加载抛异常）。
`0.10.0`（`22:08:53`）及以前 18 次加载全部是 `Harmony 补丁已应用。`，干净。

**明确没有失效的**（一度被怀疑但已排除）：

| 功能 | 补丁类 | 证据 |
|---|---|---|
| 卫兵灵能不涨亚空间威胁 | `VeilPatch` (RID 29) | `22:39:36` 连续 4 条 `[帷幕] 已拦截…` —— 中止后 **127 秒** |
| 保住卫兵自带装备 | `InventoryPatch` (RID 13) | `22:39:17` `[背包] 已拦截 RestoreSharedInventory…` —— 中止后 **108 秒** |
| 摄像机不跟随卫兵 | `CameraFollowPatch` (RID 9) | 顺序推证：若它的 `TargetMethod` 失败，RID 13/29 根本轮不到，而后两者有实锤 |
| 卫队独立士气池 / 杀敌记功 / 倒地不扣士气 | `MomentumGroupPatch`(17) `GuardKillCreditPatch`(12) `MomentumPatch`(18) | RID 全部 < 44，顺序推证 |
| 卫兵经验缩放 / 创伤开关 / 跟队治疗 | `XpPatch`(30) `TraumaPatch`(31) `TraumaHealPatch`(32) | 同上 |

> **这次是运气**：`SelectPatch` 恰好是嵌套类型、被 Roslyn 排到 TypeDef 表队尾才没连累别人。今后随便加一个文件名靠前的补丁类（`A*.cs`、`B*.cs`），一旦抛异常就会连坐大半个 mod。修复【A】就是为了让这件事永远不再可能发生。

---

## 5. 硬约束逐条核验

### 约束 1：绝不能让 mod 自建蓝图成为被持久化的、类型化标量字段的值（尤其 `EntityFactSource.m_Blueprint`）

✅ **满足，且本次修复是净收益。**

- 【C】补的字段全部写在**我们自己的内存 `BlueprintAnswer`** 上。蓝图从不被序列化，存档只存 AssetGuid，重启即从 `blueprints-pack.bbp` 复原。
- `OnSelect` 保持**空 `ActionList`** —— 遵守类注释硬规则 3，绝不授予 fact / 召唤单位，实际动作留在 Harmony 钩子里做。因此永远不会产生 `EntityFactSource.m_Blueprint`。
- **本次新发现的红线缺口**（原设计里没人注意到）：`DialogController.SelectAnswer` 第 664 行
  ```csharp
  var bookEventLog = Game.Instance.Player.Dialog.BookEventLog;
  if (bookEventLog.ContainsKey(Dialog)) bookEventLog[Dialog].Add(answer);
  ```
  而 `DialogState.BookEventLog` 是 `[JsonProperty] Dictionary<BlueprintDialog, List<BlueprintScriptableObject>>` —— **类型化蓝图引用**。
  **【B】的 `Prefix` 返回 `false` 让原方法完全不执行，这条路径被彻底堵死。**
  补充一个时序细节：`answer.CharacterSelection.SelectionType` 的解引用在第 654 行、**早于** BookEventLog 写入（664 行）。也就是说**今天**因为 CharacterSelection 是 null，SelectAnswer 会在写 BookEventLog 之前就 NRE，反而没腐坏存档 —— 但【C】一上就把这个口子打开了。**所以【B】和【C】必须同批上线，不能只上 C。**
- 已确认安全、无需处理的：`DialogState.SelectedAnswers` 是 `HashSet<string>` 且 `SelectedAnswersAdd(bp)` 只存 `bp.AssetGuid` 字符串；`AnswerChecks` 是 `Dictionary<string, CheckResult>`；`ShownAnswerLists` 是 `HashSet<string>`。全部落在你已确认安全的"不透明 GUID 字符串"那一类。
- `AddToHistory = false` 额外绕开了 `AddHistoryEntry` → `AnswerShowData`（该类持有 `BlueprintAnswer` 强引用但字段**无** `[JsonProperty]`/序列化特性，只经 EventBus 给 UI；风险显著低于 BookEventLog，但没追到 handler 末端，所以关掉更保险）。
- 本次修复**一个新 AssetId 都不增加**（这也是不再造 `BlueprintCue` 给 NextCue 指的额外好处）。

### 约束 2：绝不能给卫兵自定义 brain，绝不能新建 `BlueprintUnit`

✅ **满足。** 四项修复全部只涉及：Harmony 补丁应用方式、`DialogController.SelectAnswer` 的 Prefix、我们自己的 `BlueprintAnswer` 字段填充、`LocalizationPack.TryGetText` 的 Prefix。与单位、brain、`BlueprintUnit` 完全无交集。

### 约束 3：必须与 ToyBox 共存

✅ **满足，且冲突面比现在更小。**

- 不新增任何 patch 目标；【B】只是给已有的补丁补上参数类型，【D】只是改一个字符串。
- 【A】的逐类 `Patch()` 用的是我们自己的 `Harmony` 实例（id = `modEntry.Info.Id`），不动 Harmony 全局状态，不影响 ToyBox 已挂的补丁。反而消除了"补丁半应用"这种难查状态。
- 【D】用 `TargetMethod()` + `Prepare()` 的软失败写法，找不到就不装，不会跟 ToyBox 抢。
- ⚠️ **一处需要知情的行为变化**：Harmony 2.x 里，某个 Prefix 返回 `false` 后，**后续的 Prefix 默认会被跳过**（除非它们声明了 `bool __runOriginal` 形参）。所以如果 ToyBox 也 Prefix 了 `SelectAnswer`，在**我们自己这条 answer 上**它的 Prefix 会被跳过。影响范围仅限我们这一条选项（`AssetGuid != AnswerGuid` 时我们 `return true`，零副作用），可接受。Postfix 不受影响，照常执行。

---

## 6. 分歧点与最保守做法

| 分歧 | 双方主张 | 我的裁定 | 依据 |
|---|---|---|---|
| **PatchAll 中止的爆炸半径** | `blastradius` 核验：只死了 SelectPatch。`ui` 核验：`XpPatch/VeilPatch/MomentumPatch/…` 全被连坐 | **只死了 SelectPatch。`ui` 核验错。** | 我自己 dump 了 TypeDef 表：SelectPatch = RID 44 是最后一个补丁类，RID 45~50 无补丁类；且 InventoryPatch / VeilPatch 在中止后 108s / 127s 仍有正面日志 |
| **LocalizationPatch 为什么没生效** | `overloads`/`nullfields` 调查：因 PatchAll 中止被连累。三个核验方：类型名写错，独立失效 | **类型名写错，与中止无关。** | RID 43 < 44，本来就轮得到它；ilspycmd 直接报 `Could not find a type named 'Kingmaker.Localization.LocalizationPack'` |
| **`SetText` 是否会静默失败** | `localization` 调查：`GetField("Text")` 可能返回 null 导致静默失败。其核验方：`Text` 由 `BlueprintAnswer` 自身声明，恒能找到 | **恒能找到，`SetText` 不是病因。** 但仍把 `return;` 改成打日志 | 反编译确认 `Text` 在 `BlueprintAnswer` 上、两级基类均无同名字段（不会 AmbiguousMatch） |
| **`OnCheckSuccess`/`OnCheckFail` 要不要填** | `localization` 调查的代码里直接 `a.OnCheckSuccess = new ActionList();` | **不能填，会 CS0122。** 也不需要填 | 反编译确认两者是 `[SerializeField] private ActionList`；且只在 `if (HasShowCheck)` 内以 `?.Run()` 调用，而我们让 `HasShowCheck` 恒 false |
| **`CharacterSelection.Type` 用 `Clear` 还是 `Keep`** | `nullfields` 核验推 `Keep`（保留 ActingUnit），`ui` 调查推 `Clear` | **此处争议已被【B】消解 —— 取哪个都无所谓。** 用 `Clear`（枚举默认值 0） | `SelectAnswer` 被 Prefix 跳过，`SelectionType` 只剩 `SkillChecksDC` 里读，而我们 `FakeChecks` 空 + `NextCue` 空 + `HasShowCheck` false ⇒ 返回值根本没被用 |
| **本地化用 Harmony 钩子还是 `PutString`** | `localization` 调查推 `PutString`（需改 csproj + 订阅 `LocaleChanged`），核验方认可但保留钩子方案 | **先用钩子（改一个字符串，零 csproj 变更）。** `PutString` 备案 | 最小改动优先；`PutString` 还得处理 `LocalizationManager.Init` 一次启动跑两次导致 `CurrentPack` 被换的问题 |
| **`ConditionsChecker.Conditions` 要不要显式给空数组** | `overloads` 调查说要（怕 `HasConditions` 解引用 null），其核验方说不用 | **不用。** 裸 `new ConditionsChecker()` 即安全 | 反编译确认 `HasConditions` 内部 `if (conditions != null) return conditions.Length > 0; return false;`，`Check()` 开头 `if (!HasConditions) return true;` |
| **Prefix 返回 `false` 是否会导致观感问题** | 无人实测 | **此处存在分歧，需进游戏验证。最保守做法：先按代码里那样保留 `StopDialog()` 注释掉的状态跑一次；若对话窗抢焦点/招募面板点不动，再取消注释。** | 两种都不违反硬约束；`StopDialog()` 只是把对话正常关掉，不写任何持久化状态 |

---

## 7. 落地后的验证顺序（按这个顺序判读，别混淆三个故障）

```
1. kgd_log.txt  →  [Harmony] 补丁完成：成功 11 个类，失败 0，带标注却未生效 0 个。
                   （数字不对时，失败/未生效的类名会被直接点名，不必再靠 RID 推证）

2. GameLogFull  →  搜 "BlueprintAnswer.CanShow"
                   还有 NRE  ⇒ 【C】没生效，先别管文案，回头查 EnsureAnswer
                   没有了    ⇒ 继续第 3 步

3. GameLogFull  →  搜 "Kgd_RecruitAnswer: answer shown"
                   有        ⇒ 选项**真的**进了 m_Answers（历史上从未出现过这行）
                   没有      ⇒ 选项被 ShowConditions 或别的条件过滤了，看同行的 DialogDebug 原因

4. 进对话看文字 →  显示「（护卫队）关于我的护卫队……」  ⇒ 全通
                   空白 + 有 missing string: kgd_recruit_answer  ⇒ 【D】没生效
                   空白 + 没有 missing string                     ⇒ 还有别的异常打断渲染，回第 2 步
                   带 "[enGB] " 前缀                              ⇒ （仅 PutString 备选方案才可能出现）

5. 点选项       →  kgd_log 出现 "[招募对话] 玩家选择了征募选项"  ⇒ 【B】生效
                   （该行在全部 24 次历史加载中出现 0 次，必须实测才算数）
```

---

**涉及文件（绝对路径）**
- `D:\RT_RetinueMod\src\KgdRetinue\Main.cs` —— 修复 A（第 29~35 行 + 新增 `PatchAllSafe`）
- `D:\RT_RetinueMod\src\KgdRetinue\RecruitDialog.cs` —— 修复 B（462~480）、C（12 行后加 using / 68~108 / 195 / 208 / 239~243）、D（38~61）
- `D:\RT_RetinueMod\src\KgdRetinue\KgdRetinue.csproj` —— **不需要改动**（`Kingmaker.ElementsSystem` 在已引用的 `RogueTrader.GameCore.dll`，`StatType` 在已引用的 `Kingmaker.EntitySystem.Stats.Base.dll`）