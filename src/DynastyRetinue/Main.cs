using System;
using UnityEngine;
using UnityModManagerNet;
using HarmonyLib;

namespace DynastyRetinue
{
    /// <summary>
    /// M1 骨架：验证「原版蓝图 spawn + 玩家阵营 + 自定义战斗组」能否产出行为正常的 AI 盟友。
    /// 这一步不过，整个卫队方案作废。
    /// </summary>
    public static class Main
    {
        public static UnityModManager.ModEntry ModEntry;
        public static Harmony HarmonyInstance;
        public static Settings Settings;

        // UMM 入口。Info.json 里 EntryMethod = "DynastyRetinue.Main.Load"
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);

            modEntry.OnToggle  = OnToggle;
            modEntry.OnGUI     = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUpdate  = OnUpdate;

            // ★开发区可见性：靠标记文件，不靠条件编译★
            // 用 #if DEBUG 编译掉的话，我自己测的就不是发出去的那个二进制了 ——
            // 发布版独有的代码路径永远没被跑过。同一个 DLL、只切可见性，
            // 才能保证"我测过的"和"玩家拿到的"逐字节一致。
            // bump.sh pack 只打四个具名文件，这个 flag 永远进不了发布包。
            try
            {
                DevMode = System.IO.File.Exists(
                    System.IO.Path.Combine(modEntry.Path, "dynasty_dev.flag"));
            }
            catch { DevMode = false; }

            // ★检测旧版残留★ 0.8x 时 mod 叫 KgdRetinue，文件夹也叫这个名。
            // UMM 是按「子目录里有没有 Info.json」装载的，**不认目录名** ——
            // 所以解压新版只会多出一个文件夹，旧的照样被加载，两份程序集同时跑：
            // 两套 Harmony 补丁、两个 UI 根、两份名册认领存档里同一批卫兵。
            // 玩家不会想到这一层（他以为"我装的是新版"），所以这里主动喊一声。
            //
            // ★用文件系统查而不是问 UMM★ UnityModManager 0.23.0 没有公开的 modEntries 成员，
            // 而不同版本的 UMM 公开面不一样。查"隔壁有没有 KgdRetinue/Info.json"这个条件
            // 恰好就是 UMM 会去加载它的充要条件，且跨版本稳定。
            try
            {
                var mods = System.IO.Directory.GetParent(modEntry.Path);
                if (mods != null)
                {
                    var legacy = System.IO.Path.Combine(mods.FullName, "KgdRetinue");
                    if (System.IO.File.Exists(System.IO.Path.Combine(legacy, "Info.json")))
                        LegacyConflict = legacy;
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(LegacyConflict))
                LogError("★检测到旧版目录仍在★ " + LegacyConflict
                       + "\n    本 mod 已从 KgdRetinue 更名为 DynastyRetinue，旧目录不会被新版覆盖，"
                       + "而 UMM 只看子目录里有没有 Info.json —— 所以它会把两份都装上。"
                       + "\n    请退出游戏，把旧的 KgdRetinue 文件夹整个删除或移出 UnityModManager 目录"
                       + "（只改名无效）。"
                       + "\n    （若你已在 UMM 里手动禁用了它，可以忽略这条。）");

            HarmonyInstance = new Harmony(modEntry.Info.Id);
            PatchAllSafe(HarmonyInstance, System.Reflection.Assembly.GetExecutingAssembly());

            RetinueLifecycle.Subscribe();
            DeathRules.Subscribe();
            CombatWatch.Install();

            // ★必须在载入时装，不能懒装★
            // m_CustomPrefabGuid 进存档，冷启动读档根本不会走 Apply()；
            // 而 DisableSizeScaling 是 view 上的运行时 bool、不持久化。
            // 懒装 = 每次重开游戏读档，换过模的船都会被 GetSizeScale() 放大 1.5152 倍。
            StarshipViewTool.Install();

            Log("loaded.  版本 " + (modEntry.Info != null ? modEntry.Info.Version : "?"));
            return true;
        }

        /// <summary>
        /// 逐类打补丁，替代 Harmony.PatchAll(Assembly)。
        ///
        /// 为什么不能用 PatchAll（0Harmony 2.2.2.0）：
        ///     PatchAll(asm) => AccessTools.GetTypesFromAssembly(asm)
        ///                         .Do(t => CreateClassProcessor(t).Patch());
        /// CollectionExtensions.Do 是裸的 while(MoveNext()){action(...)}，没有逐项 try/catch；
        /// PatchClassProcessor.Patch() 末尾的 ReportException 又会把异常重新抛出去。
        /// ⇒ 第一个抛异常的补丁类会让**排在它后面的**全部被静默跳过，
        ///    而外面那层 try/catch 只看得到一条错误，看不出还丢了什么。
        /// 遍历顺序 = 程序集 TypeDef 表行号（Roslyn 先发全部顶层类型、再发嵌套类型），
        /// 纯属编译顺序运气 —— v0.10.1~0.10.9 只死了 SelectPatch 一个，是因为它恰好是
        /// 嵌套类型被排到队尾。今后随便加个文件名靠前的补丁类出问题就会连坐大半个 mod。
        ///
        /// ★ 三态，别把 inert 当成功也别当失败 ★
        ///   ok    : Patch() 返回非空列表 —— 真挂上了
        ///   failed: 抛异常 —— 被下面 catch 住并记名
        ///   inert : 返回 null（无 [HarmonyPatch] 标注）或空列表
        ///           （Prepare() 返回 false / TargetMethod() 返回 null）
        ///           —— 这一态原本**完全静默**：Patch() 走 ReportException(null, null)，
        ///              而它第一句就是 if (exception == null) return。
        ///              LocalizationPatch 因此死了一整晚没人发现。所以这里专门把
        ///              "带标注却一个方法都没打上"的类挑出来报错。
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
                // 与 PatchAll 内部同一个取法：它已处理 ReflectionTypeLoadException 并滤掉 null
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
                    // CreateClassProcessor 对没标注的类型很廉价：Patch() 首句就 return null
                    var applied = harmony.CreateClassProcessor(t).Patch();
                    if (applied != null && applied.Count > 0)
                    {
                        ok++;
                        Log("[Harmony] OK   " + t.FullName + "  → " + applied.Count + " 个方法");
                    }
                    else if (isPatchClass) inertNames.Add(t.FullName);
                }
                catch (Exception e)
                {
                    failedNames.Add(t.FullName);
                    var root = e;
                    while (root.InnerException != null) root = root.InnerException;
                    LogError("[Harmony] FAIL " + t.FullName + "  —— " + root.GetType().Name + ": " + root.Message);
                    LogError(e.ToString());
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

        /// <summary>
        /// 统一的开窗入口。新的 uGUI 窗口是主力，旧的 IMGUI 窗口留作退路 ——
        /// 万一 uGUI 那套在某台机器/某个版本上出问题，翻个开关就能继续用，
        /// 不至于让"招募"这个核心功能整个不可用。
        /// </summary>
        public static void OpenRecruitUI(Kingmaker.EntitySystem.Entities.BaseUnitEntity npc)
        {
            // uGUI 窗口是唯一正式入口。旧的 IMGUI 窗口只在它抛异常时兜底 ——
            // 曾经有个 UseNewUI 开关，但载入时无条件被置 true、判据恒真，
            // 等于死代码，v0.49.0 删了。
            try { UI.RetinueUI.Open(); return; }
            catch (Exception e) { LogError("[UI] 新窗口开启失败，回退到旧窗口: " + e); }
            RecruitWindow.Open(npc);
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            if (value) { RetinueLifecycle.Subscribe(); DeathRules.Subscribe(); }
            else
            {
                RetinueLifecycle.Unsubscribe();
                DeathRules.Unsubscribe();
                RecruitWindow.Shutdown();   // 连宿主 GameObject 一起销毁，不留残留
                UI.RetinueUI.Shutdown();    // 新的 uGUI 窗口：销毁 Canvas 根
                ShipYardWindow.Shutdown();  // 船坞窗口(IMGUI 退路)：连宿主 GameObject 一起销毁
                UI.ShipYardUI.Shutdown();   // 船坞窗口(uGUI)：销毁 Canvas 根
                UnitPortraits.Cleanup();    // 把 hold 住的立绘资源还回去
                ShipModelBundleHold.Cleanup();  // 把 hold 住的船模 bundle 还回去
                // 刻意不自动遣散：卫兵现在是持久实体，误触开关不该清掉满级卫队。
                // 遣散必须由玩家显式点按钮。
                int n = RetinueRegistry.Count;
                if (n > 0) Log("注意：仍有 " + n + " 名卫兵留在存档中。禁用 mod 或 DLC 前请先点【遣散全部】。");
                // ★不自动还原船模★：m_CustomPrefabGuid / m_Size 都进存档且是单向的，
                // 但"禁用开关"不等于"要卸载 mod"，静默改玩家的船不合适。
                // 只提醒；真要还原请点【还原原版船模】（StarshipViewTool.RevertAll）。
                try
                {
                    if (StarshipViewTool.CurrentPrefab != null)
                        Log("注意：座舰仍是自定义船模 + 自定义分档，两者都在存档里。"
                          + "彻底卸载 mod 前请先点【还原原版船模】再存盘，否则船会永久保持现在的样子。");
                }
                catch { }
            }
            return true;
        }

        public static bool Enabled { get; private set; }

        /// <summary>
        /// 开发模式。mod 目录下有 dynasty_dev.flag 才为 true。
        /// 控制「开发 · 测试」整区是否出现 —— 那里面的一键测试会清空全部卫兵，
        /// 诊断按钮的输出也只有作者看得懂，不该出现在玩家的面板上。
        /// </summary>
        public static bool DevMode { get; private set; }

        /// <summary>
        /// 玩家区里那些「只有开发者才看得到」的额外内容，此刻该不该显示。
        ///
        /// = 开发模式开着 **且** 没在预览玩家视角。
        ///
        /// ★为什么需要这个而不是直接用 DevMode★ 拍发布用的面板截图时要的是玩家视角，
        /// 而唯一的办法本来是删掉 dynasty_dev.flag 再重启游戏 —— 一来一回四五分钟，
        /// 而且拍完还得建回来。有了这个开关，勾一下就能看到玩家看到的样子。
        ///
        /// ★开发区本身不用它★ 那一区必须始终可达，否则勾上预览之后就再也关不掉了。
        /// 预览模式下开发区只保留这一行开关，其余内容折叠。
        /// </summary>
        public static bool DevUI
        {
            get { return DevMode && (Settings == null || !Settings.PreviewAsPlayer); }
        }

        /// <summary>
        /// 非空 = 检测到旧版 KgdRetinue 也在运行，值是它的目录。
        /// 面板顶部会常驻显示一条红字，直到玩家把旧目录清掉。
        /// </summary>
        public static string LegacyConflict { get; private set; }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (!Enabled) return;
            try
            {
                // 热键。★ 必须先排除修饰键 ★
                // Input.GetKeyDown(F10) 在按住 Ctrl 时照样为 true，而 Ctrl+F10 是 UMM 的
                // 开面板热键 —— 结果是每次开面板都顺手遣散一次卫队。卫兵现在是持久实体，
                // 遣散 = 永久销毁，这个误触代价太大。
                //
                // ★ 整块用 DevMode 门住 ★
                // 在此之前这里没有任何门，而 SpawnKey 的默认值就是 F7 —— 于是
                // **每个玩家的 F7 都绑着"往 party.json 里塞一名持久卫兵"**，
                // 唯一能改/关掉它的输入框却在默认隐藏的开发区里。
                // 玩家误按之后：不知道这名卫兵哪来的，也不知道卸载前必须先遣散它。
                // 「效果对所有人生效、开关只有作者看得见」是最坏的组合，两边取一边即可。
                // 这里选择关掉效果而不是暴露开关：招募窗口才是玩家该走的入口，
                // 热键只是作者反复测试时的快捷方式。
                if (DevMode)
                {
                    bool _mod = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                             || Input.GetKey(KeyCode.LeftAlt)     || Input.GetKey(KeyCode.RightAlt)
                             || Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift);
                    if (!_mod)
                    {
                        if (Settings.SpawnKey != KeyCode.None && Input.GetKeyDown(Settings.SpawnKey))
                            RetinueTest.SpawnOne();
                        if (Settings.DespawnKey != KeyCode.None && Input.GetKeyDown(Settings.DespawnKey))
                            RetinueTest.DespawnAll();
                    }
                }

                RetinueLifecycle.TickPending();
                CombatWatch.Tick();          // 一帧一个 bool 比较，战斗结束那一帧才干活
                if (Settings.WatchMomentum) MomentumWatch.Tick();
            }
            catch (Exception e) { LogError(e); }
        }

        /// <summary>
        /// 分区折叠头。返回是否展开。
        /// 面板原本是 370 行一条道铺到底、33 个按钮混在一起，其中大半是只有我会用的
        /// 探针/导出。分成「招募 / 舰船 / 规则 / 开发·测试」四区，前三区默认展开，
        /// 开发区默认折叠 —— 那里好几个按钮会清场，玩家误点代价不小。
        /// </summary>
        /// <summary>
        /// 不可逆操作的两段式确认：第一次点只"上膛"并把标签换成「再点一次」，
        /// 3 秒内第二次点才真执行；超时自动解除。
        ///
        /// ★为什么需要★【遣散全部】原来零确认，而且和无害的【Dump 状态】同宽同排紧挨着，
        /// 鼠标偏一格就是销毁整支满级卫队。作者自己早就因为"误触代价太大"把
        /// SpawnKey/DespawnKey 整块关进了 DevMode（见 :219），按钮这边留着是不对称的。
        ///
        /// 用两段式而不是弹窗：IMGUI 里做模态对话框要接管输入、还要处理面板关闭时的残留状态，
        /// 而两段式只需要一个 float，且不打断操作流。
        /// </summary>
        private static float _armDismiss, _armShipRevert, _armReset;
        private const float ArmWindow = 3f;
        private static void DangerButton(ref float armedAt, string label, float width,
                                         int affected, System.Action action)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            bool armed = armedAt > 0f && (now - armedAt) < ArmWindow;
            if (armedAt > 0f && !armed) armedAt = 0f;      // 超时自动解除

            if (armed)
            {
                var prev = GUI.color;
                GUI.color = new Color(1f, 0.55f, 0.4f);
                if (GUILayout.Button(L.F("再点一次（{0}）", affected), GUILayout.Width(width + 40f)))
                {
                    armedAt = 0f;
                    GUI.color = prev;
                    try { action(); } catch (Exception e) { LogError(label + ": " + e.Message); }
                    return;
                }
                GUI.color = prev;
            }
            else if (GUILayout.Button(label, GUILayout.Width(width)))
            {
                armedAt = now;
            }
        }

        /// <summary>
        /// 把所有设置恢复成代码里的默认值。
        ///
        /// ★用反射从一个新建的 Settings 实例拷，而不是手写一串赋值★
        /// 手写的版本每加一个设置项就要记得同步一次，而漏掉是没有任何提示的 ——
        /// 玩家点了"恢复默认"，结果某几项纹丝不动，比没有这个按钮更糟。
        /// 反射版永远覆盖全部字段，加新设置不用管它。
        ///
        /// 不还原的东西：
        ///   · PreviewAsPlayer —— 那是开发者的临时视图状态，不是玩法设置
        ///   · ProwLearned* —— 那是实测学到的挂点数据，不是偏好；重学一次要切一趟大巡
        /// </summary>
        private static void ResetSettingsToDefault()
        {
            try
            {
                var fresh = new Settings();
                var keep = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                {
                    "PreviewAsPlayer", "ProwLearned", "ProwLearnedFrom",
                    "ProwDropRatio", "ProwZBackRatio",
                };
                int n = 0;
                foreach (var f in typeof(Settings).GetFields(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (f.IsInitOnly || keep.Contains(f.Name)) continue;
                    var def = f.GetValue(fresh);
                    if (!object.Equals(f.GetValue(Settings), def)) { f.SetValue(Settings, def); n++; }
                }
                L.Reset();   // 语言可能被改回「自动」，缓存要跟着失效
                Log("[设置] 已恢复默认值，改动了 " + n + " 项。"
                  + "（保留了开发者视图状态和学到的舰首挂点比例）");
            }
            catch (Exception e) { LogError("[设置] 恢复默认失败: " + e.Message); }
        }

        private static bool Fold(ref bool open, string title, string hint)
        {
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(open ? "▼" : "▶", GUILayout.Width(28))) open = !open;
            GUILayout.Label("<b><size=14>" + title + "</size></b>"
                            + (string.IsNullOrEmpty(hint) ? "" : "　<color=#aaaaaa>" + hint + "</color>"));
            GUILayout.EndHorizontal();
            return open;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            // ---------- 卫队 ----------
            GUILayout.Label("<b>DynastyRetinue v" + (ModEntry != null && ModEntry.Info != null ? ModEntry.Info.Version : "?") + "</b>");

            // ★卸载须知放在第一屏★ 卫兵是持久实体、进 party.json，
            // 不遣散就关 mod 会在存档里留下读不出来的引用。README 里写了，
            // 但绝大多数玩家不会读 README —— 这条必须是打开面板第一眼就看见的东西。
            // 只在**真的有卫兵或换过船**时才显示，避免变成人人无视的常驻噪音。
            {
                int _n = 0; bool _swapped = false;
                try { _n = RetinueRegistry.Count; } catch { }
                try { _swapped = !string.IsNullOrEmpty(StarshipViewTool.CurrentPrefab); } catch { }
                if (_n > 0 || _swapped)
                {
                    var _w = new System.Text.StringBuilder();
                    _w.AppendLine(L.T("<color=#ffcc66><b>卸载 / 禁用本 mod 或 DLC 之前，按顺序做完这几步：</b></color>"));
                    if (_n > 0)   _w.AppendLine(L.F("　1. 面板顶部点【遣散全部】（当前在册 {0} 名，它们写在存档里）", _n));
                    if (_swapped) _w.AppendLine(L.F("　{0}. 舰船区点【还原原版船模】", _n > 0 ? "2" : "1"));
                    _w.Append(L.F("　{0}. <b>存盘</b> —— 前面几步只在内存里，不存盘等于没做",
                                  (_n > 0 ? 1 : 0) + (_swapped ? 1 : 0) + 1));
                    _w.AppendLine();
                    _w.Append(L.T("<color=#aaaaaa>这条流程是实测验证过的；"
                                + "「不清理就直接删 mod」理论上也安全（存档里只写裸字符串和原版枚举），"
                                + "但没做过完整实验，所以不给承诺。</color>"));
                    GUILayout.Label(_w.ToString());
                }
            }
            // ★一帧只扫一次★ 这一段原来连着调 4 次 RetinueRegistry.Count/All()，
            // 而每次都是「遍历所有 State 里的全部实体 + 逐个 IsGuard」。
            // IMGUI 一帧至少触发两轮事件（Layout / Repaint），于是面板开着的时候
            // 每帧要全量扫八九遍。不是卡顿的主因（5 个卫兵的量级远不够），但纯属白费。
            var _guards = RetinueRegistry.All();
            int _cnt = _guards.Count;
            GUILayout.Label(L.F("<b>卫队</b>   在册 {0}   {1}", _cnt, RetinueRegistry.Describe(_guards)));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("生成一个"), GUILayout.Width(110))) RetinueTest.SpawnOne();
            if (GUILayout.Button(L.T("Dump 状态"), GUILayout.Width(110))) RetinueTest.DumpState();
            DangerButton(ref _armDismiss, L.T("遣散全部"), 110f,
                         _cnt, () => RetinueRegistry.DismissAll());
            GUILayout.EndHorizontal();
            // ★ DLC 缺失提示 ★ 五个分型里四个的单位蓝图来自 DLC3。没启用 DLC3 时会退到
            // 本体兜底单位（见 ChainProbe.UnitFallback），卫兵功能完全正常，但外观和自带
            // 能力会跟对应精英同款。不说清楚的话，玩家看到的是"我招的兵怎么长得跟精英一样"，
            // 会以为是 bug。只在真的发生过回落时才显示，不打扰有 DLC3 的人。
            if (RetinueTest.DlcFallbackUsed)
                GUILayout.Label(L.T("<color=#d0a050>未检测到 DLC3 —— 部分分型已改用本体单位蓝图。"
                                  + "卫兵的职业、装备、AI 都不受影响，只是外观和自带能力会和对应精英相同。</color>"));
            // 旧版残留：常驻红字，直到玩家清掉。比只打日志有效得多。
            if (!string.IsNullOrEmpty(LegacyConflict))
                GUILayout.Label(L.F("<color=#ff8080>★旧版 KgdRetinue 仍在运行★（{0}）"
                                  + "　请退出游戏后把该文件夹整个删除或移出 UnityModManager 目录 —— 只改名无效。"
                                  + "两份同时跑会导致补丁重复、名册重复认领同一批卫兵。</color>", LegacyConflict));
            // ★ 存档安全提醒 ★ 卫兵是持久实体，写进 party.json。
            // 禁用 mod / 关掉 DLC / 换 Steam 账号之后再读档，卫兵引用的蓝图解析不到会导致存档打不开。
            // 有卫兵在册时把数量也报出来，比一句干巴巴的静态警告更能让人真去点遣散。
            {
                int alive = 0;
                try { alive = RetinueRegistry.Count; } catch { }
                GUILayout.Label(alive > 0
                    ? L.F("<color=#ff8080><b>⚠ 存档里有 {0} 名卫兵。</b>禁用 mod、在 Steam 里关闭 DLC、"
                        + "或更换 Steam 账号之前，请先点【遣散全部】—— 否则读档时卫兵引用的蓝图可能解析不到。</color>", alive)
                    : L.T("<color=#ff8080>卫兵是持久实体，会写进存档（party.json）。禁用 mod 或在 Steam 里关闭 DLC 之前，请先点【遣散全部】。</color>"));
            }

            // 分型索引在多个分区里都要用（招募区选它、开发区按它生成），
            // 所以提到折叠块外面声明，不能留在「招募」区的大括号里
            var _archs = Archetypes.All;
            int _cur = Settings.ArchetypeIndex;
            if (_cur < 0 || _cur >= _archs.Length) _cur = 0;

            if (Fold(ref Settings.PanelShowRecruit, L.T("招募"), L.T("分型 / 入口 / 名额上限")))
            {
            // ---------- 分型 ----------
            GUILayout.Space(8);
            GUILayout.Label(L.F("<b>分型</b>   当前 = <color=#80ff80>{0}</color>{1}",
                                _archs[_cur].Name,
                                // 配表字段说明只对改配表的人有意义，玩家看了只会更困惑
                                DevUI ? L.T("    <i>（模板 archetypes.json：unit=模型/装备, brain=AI行为, plan=天赋方案, chain=职业链）</i>") : ""));
            GUILayout.BeginHorizontal();
            for (int i = 0; i < _archs.Length; i++)
            {
                string label = (i == _cur ? "● " : "○ ") + _archs[i].Name;
                if (GUILayout.Button(label, GUILayout.Width(150))) Settings.ArchetypeIndex = i;
                if (i % 4 == 3 && i < _archs.Length - 1) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();

            // 这两个都是「把内容 dump 进日志」的作者工具：玩家不改配表，也看不懂加点方案的内部名。
            if (DevUI)
            {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("重载模板"), GUILayout.Width(110))) Archetypes.Reload();
            if (GUILayout.Button(L.T("列出加点方案"), GUILayout.Width(130))) BuildPlans.Reload();
            GUILayout.EndHorizontal();
            }

            // ---------- 招募入口 ----------
            GUILayout.Space(8);
            GUILayout.Label(L.T("<b>招募入口</b>（挂在 NPC 身上的原生点击交互，不进存档）"));
            Settings.NpcRecruitEntry = GUILayout.Toggle(Settings.NpcRecruitEntry, L.T("点击 NPC 弹招募面板（原生点击交互）"));
            Settings.DialogRecruitEntry = GUILayout.Toggle(Settings.DialogRecruitEntry, L.T("在 NPC 对话里加一条「征募护卫队」选项"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("目标 NPC 关键字"), GUILayout.Width(110));
            Settings.RecruitNpcKeys = GUILayout.TextField(Settings.RecruitNpcKeys ?? "", GUILayout.Width(220));
            if (GUILayout.Button(L.T("挂到当前区域"), GUILayout.Width(110)))
            { RecruitEntry.AttachInArea(true); RecruitDialog.InjectInArea(true); }
            // 「列出可挂载 NPC」只往日志里 dump 一串蓝图名，玩家拿到也不知道该干嘛
            if (DevUI && GUILayout.Button(L.T("列出可挂载 NPC"), GUILayout.Width(130))) RecruitEntry.ListCandidates();
            if (GUILayout.Button(L.T("直接开窗"), GUILayout.Width(90))) OpenRecruitUI(null);
            GUILayout.EndHorizontal();
            // 这里原来还有一个【预览新窗口】按钮，调 UI.RetinueUI.Open()。
            // 而【直接开窗】走的 OpenRecruitUI 第一句就是 try { UI.RetinueUI.Open(); return; } ——
            // 也就是说它是【直接开窗】的**真子集**（少了异常兜底）。两个按钮并排只会让人猜区别。
            if (DevUI)
                GUILayout.Label(L.T("<color=#aaaaaa>名单打在 dynasty_log.txt 里。本船的高阶顾问蓝图名是 HighFactotum，音阵大师是 VoxMaster。</color>"));

            // ---------- 招募上限：利润因子 ----------
            GUILayout.Space(6);
            Settings.RecruitUsePfGate = GUILayout.Toggle(Settings.RecruitUsePfGate,
                L.T("<b>用利润因子解锁招募名额</b>（关掉则退回旧的阶位上限 T1=2 / T2=4 / T3=6）"));
            {
                GUILayout.BeginHorizontal();
                // ★闸门关掉时也要显示上限★ 原来这整块套在 if (RecruitUsePfGate) 里，
                // 关掉闸门之后招募区一个上限控件都没有 —— 玩家只能去改 XML。
                // 曾经有个 GuardCapOverride 字符串字段补这个缺口，v0.49.0 删了：
                // 两个上限来源迟早打架。现在上限只有一处，就是下面这根滑条。
                if (Settings.RecruitUsePfGate)
                {
                GUILayout.Label(L.T("每名所需利润因子"), GUILayout.Width(130));
                Settings.RecruitPfPerGuard = (int)GUILayout.HorizontalSlider(Settings.RecruitPfPerGuard, 1f, 60f, GUILayout.Width(140));
                GUILayout.Label(Settings.RecruitPfPerGuard.ToString(), GUILayout.Width(40));
                }
                GUILayout.Label(L.T("最多几名"), GUILayout.Width(60));
                Settings.RecruitMaxGuards = (int)GUILayout.HorizontalSlider(Settings.RecruitMaxGuards, 0f, 12f, GUILayout.Width(120));
                GUILayout.Label(Settings.RecruitMaxGuards.ToString(), GUILayout.Width(30));
                GUILayout.EndHorizontal();
                if (Settings.RecruitUsePfGate)
                GUILayout.Label("<color=#c8a45c>" + ProfitFactorGate.Summary() + "</color>");
                // 分级表：把每一档的门槛列出来，玩家一眼看到下一档还差多少
                if (Settings.RecruitUsePfGate)
                try
                {
                    var _th = ProfitFactorGate.Thresholds();
                    int _pf = ProfitFactorGate.Current();
                    var _sb = new System.Text.StringBuilder("<color=#aaaaaa>" + L.T("分级："));
                    for (int _i = 0; _i < _th.Length; _i++)
                    {
                        bool _got = _pf >= _th[_i];
                        _sb.Append(_got ? "<color=#7ec8ff>" : "")
                           .Append(L.F("{0}→{1}名", _th[_i], _i + 1))
                           .Append(_got ? "</color>" : "").Append(_i + 1 < _th.Length ? "　" : "");
                    }
                    GUILayout.Label(_sb.Append("</color>").ToString());
                }
                catch { }
            }

            }

            if (Fold(ref Settings.PanelShowShip, L.T("舰船"), L.T("分档加成 / 换船模 / 挂点")))
            {
            // ---------- 舰船 ----------
            GUILayout.Space(8);
            // 状态行：不点任何按钮就能看出"现在到底是不是巡洋舰"。
            // 之前只能靠点一次切换按钮、从日志里读「当前分档=」，太绕。
            {
                string _sz = "?", _pf = L.T("原版");
                try { _sz = StarshipTool.CurrentSize().ToString(); } catch { }
                try { var _p = StarshipViewTool.CurrentPrefab;
                      if (!string.IsNullOrEmpty(_p))
                      { var _k = ShipModelCatalog.ByPrefab(_p); _pf = _k != null ? _k.HullName : _p; } }
                catch { }
                GUILayout.Label(L.F("<b>当前座舰</b>　分档 = <color=#80ff80>{0}</color>"
                                  + "　船模 = <color=#80ff80>{1}</color>"
                                  + "　<color=#aaaaaa>两项都写进存档，但要**存过盘**才留得住：改完直接读档就没了。</color>", _sz, _pf));
            }
            GUILayout.Label(L.T("<b>舰船</b>　分档加成：护盾 / 装甲 / 撞角距离 / 开火次数 / 射程。<color=#aaaaaa>不动配置界面、不扩槽位、不改蓝图。</color>"));
            Settings.ShipExtraShots = GUILayout.Toggle(Settings.ShipExtraShots,
                L.F("换大船后同一槽位可多次开火（当前舰船分档: {0}）", StarshipChargesPatch.ShipSize()));
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("巡洋舰 舷炮 +"), GUILayout.Width(110));
            Settings.ShipCruiserBroadside = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserBroadside, 0f, 4f, GUILayout.Width(120));
            GUILayout.Label(Settings.ShipCruiserBroadside.ToString(), GUILayout.Width(24));
            GUILayout.Label(L.T("大巡洋 舷炮 +"), GUILayout.Width(110));
            Settings.ShipGrandBroadside = (int)GUILayout.HorizontalSlider(Settings.ShipGrandBroadside, 0f, 4f, GUILayout.Width(120));
            GUILayout.Label(Settings.ShipGrandBroadside.ToString(), GUILayout.Width(24));
            GUILayout.Label(L.T("大巡洋 船首/背炮 +"), GUILayout.Width(140));
            Settings.ShipGrandProw = (int)GUILayout.HorizontalSlider(Settings.ShipGrandProw, 0f, 4f, GUILayout.Width(120));
            GUILayout.Label(Settings.ShipGrandProw.ToString(), GUILayout.Width(24));
            GUILayout.EndHorizontal();
            GUILayout.Label(L.T("<color=#aaaaaa>护卫舰/袭击舰无加成，保持原版手感。数值是「额外」次数：+1 = 两打。</color>"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("换船（默认：巡洋/大巡都用 Gothic）"), GUILayout.Width(210));
            if (GUILayout.Button(L.T("护卫舰"), GUILayout.Width(80)))   StarshipViewTool.ApplyTierDefault(Kingmaker.Enums.Size.Frigate_1x2);
            if (GUILayout.Button(L.T("巡洋舰"), GUILayout.Width(80)))   StarshipViewTool.ApplyTierDefault(Kingmaker.Enums.Size.Cruiser_2x4);
            if (GUILayout.Button(L.T("大巡洋舰"), GUILayout.Width(90)))  StarshipViewTool.ApplyTierDefault(Kingmaker.Enums.Size.GrandCruiser_3x6);
            GUILayout.EndHorizontal();
            Settings.ShipSwitchInCombat = GUILayout.Toggle(Settings.ShipSwitchInCombat, L.T("允许战斗中换船（有风险：格子占位会变，寻路网格未必跟着重算）"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("护盾上限 +%  巡洋"), GUILayout.Width(130));
            Settings.ShipCruiserShieldPct = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserShieldPct, 0f, 200f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipCruiserShieldPct + "%", GUILayout.Width(46));
            GUILayout.Label(L.T("大巡"), GUILayout.Width(40));
            Settings.ShipGrandShieldPct = (int)GUILayout.HorizontalSlider(Settings.ShipGrandShieldPct, 0f, 300f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipGrandShieldPct + "%", GUILayout.Width(46));
            GUILayout.EndHorizontal();
            // 「GetMax 是全舰船共用的，不加判据会把敌舰护盾也翻倍」——那是实现依据，
            // 玩家只需要知道"敌舰不会跟着变强"这个结论。
            GUILayout.Label(L.T("<color=#aaaaaa>只对你的座舰生效，敌舰不受影响。</color>"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("装甲减伤 +%  巡洋"), GUILayout.Width(130));
            Settings.ShipCruiserArmourPct = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserArmourPct, 0f, 200f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipCruiserArmourPct + "%", GUILayout.Width(46));
            GUILayout.Label(L.T("大巡"), GUILayout.Width(40));
            Settings.ShipGrandArmourPct = (int)GUILayout.HorizontalSlider(Settings.ShipGrandArmourPct, 0f, 300f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipGrandArmourPct + "%", GUILayout.Width(46));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("撞角行程 +%  巡洋"), GUILayout.Width(130));
            Settings.ShipCruiserRamPct = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserRamPct, 0f, 400f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipCruiserRamPct + "%", GUILayout.Width(46));
            GUILayout.Label(L.T("大巡"), GUILayout.Width(40));
            Settings.ShipGrandRamPct = (int)GUILayout.HorizontalSlider(Settings.ShipGrandRamPct, 0f, 400f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipGrandRamPct + "%", GUILayout.Width(46));
            GUILayout.EndHorizontal();
            // ---------- 射程加成（原来只能改 XML）----------
            // 六项加成里唯一没滑条的一族。护盾/装甲/撞角/多打都有，就它没有。
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("射程 +格　巡洋(非舷炮)"), GUILayout.Width(160));
            Settings.ShipCruiserRange = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserRange, 0f, 8f, GUILayout.Width(110));
            GUILayout.Label(Settings.ShipCruiserRange.ToString(), GUILayout.Width(26));
            GUILayout.Label(L.T("大巡·舷炮"), GUILayout.Width(110));
            Settings.ShipGrandRangeBroadside = (int)GUILayout.HorizontalSlider(Settings.ShipGrandRangeBroadside, 0f, 8f, GUILayout.Width(110));
            GUILayout.Label(Settings.ShipGrandRangeBroadside.ToString(), GUILayout.Width(26));
            GUILayout.Label(L.T("大巡·船脊/舰首"), GUILayout.Width(100));
            Settings.ShipGrandRangeProw = (int)GUILayout.HorizontalSlider(Settings.ShipGrandRangeProw, 0f, 8f, GUILayout.Width(110));
            GUILayout.Label(Settings.ShipGrandRangeProw.ToString(), GUILayout.Width(26));
            GUILayout.EndHorizontal();

            // ---------- 船坞（原来整块只能改 XML）----------
            GUILayout.Space(8);
            Settings.ShipDialogEntry = GUILayout.Toggle(Settings.ShipDialogEntry,
                L.T("<b>在 NPC 对话里加「船坞」选项</b>（用废料买改装，可还原退款）"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("巡洋总价"), GUILayout.Width(70));
            Settings.ShipPriceCruiser = (int)GUILayout.HorizontalSlider(Settings.ShipPriceCruiser, 0f, 5000f, GUILayout.Width(150));
            GUILayout.Label(Settings.ShipPriceCruiser.ToString(), GUILayout.Width(50));
            GUILayout.Label(L.T("大巡总价"), GUILayout.Width(70));
            Settings.ShipPriceGrand = (int)GUILayout.HorizontalSlider(Settings.ShipPriceGrand, 0f, 5000f, GUILayout.Width(150));
            GUILayout.Label(Settings.ShipPriceGrand.ToString(), GUILayout.Width(50));
            GUILayout.EndHorizontal();
            GUILayout.Label(L.T("<color=#aaaaaa>★这是<b>总价</b>不是差价★ 实际收费 = 目标总价 − 已投入总价，"
                              + "所以巡洋→大巡只补差额，降级/还原按同一条规则退钱。</color>"));
            if (Settings.ShipPriceGrand < Settings.ShipPriceCruiser)
                GUILayout.Label(L.T("<color=#ff8080>大巡总价低于巡洋总价 —— 会出现「升级反而退钱」。已自动拉平，"
                                  + "要更低的大巡价请先调低巡洋价。</color>"));
            Settings.ShipYardUnlockAll = GUILayout.Toggle(Settings.ShipYardUnlockAll,
                L.T("解除船体限制（连未校准的船体也允许更换）　<color=#aaaaaa>挂点位置和缩放只在 Gothic / Dictator 上验过</color>"));

            // 这两条解释的是"内部怎么算的"，不是"你该怎么用"，收进开发区
            if (DevUI)
            GUILayout.Label(L.T("<color=#aaaaaa>撞角没有可乘的「基础距离」常量（行程来自寻路），"
                              + "所以按「速度 × 百分比」折算成额外格数。机动性不动。</color>"));
            // 原文里的「[JsonProperty]」「vanilla 枚举」「存档红线」是写给我自己看的依据。
            // 玩家要的结论只有两句：存档不会坏，但船会停在你切过去那一档、要还原得手动切回来。
            GUILayout.Label(L.T("<color=#ffaa66>注意：舰船分档会写进存档。卸载 mod 后存档照样能开，"
                              + "但船会保持在你切过去的那一档 —— 要还原就切回护卫舰再存一次。</color>"));

            // ---------- 换船模（真外观）----------
            GUILayout.Space(6);
            GUILayout.Label(L.T("<b>换船模</b>（真外观。点了会同时把分档设成对应档位）"));
            var _curPrefab = StarshipViewTool.CurrentPrefab;
            var _curModel = string.IsNullOrEmpty(_curPrefab) ? null : ShipModelCatalog.ByPrefab(_curPrefab);
            GUILayout.Label(L.F("当前：{0}", _curModel != null ? _curModel.ToString()
                                                              : "<color=#aaaaaa>" + L.T("原版模型") + "</color>"));
            foreach (var _tier in new[] { Kingmaker.Enums.Size.GrandCruiser_3x6,
                                          Kingmaker.Enums.Size.Cruiser_2x4,
                                          Kingmaker.Enums.Size.Frigate_1x2 })
            {
                // 大巡这一档把巡洋舰船模也列出来 —— 原版只有 2 个 3x6 船模（混沌战舰 / 帝国货船），
                // 想要"帝国战舰造型的大巡"必须靠等比放大巡洋舰船模。
                var _list = ShipModelCatalog.ForTier(_tier);
                if (_tier == Kingmaker.Enums.Size.GrandCruiser_3x6)
                {
                    _list = new System.Collections.Generic.List<ShipModel>(_list);
                    _list.AddRange(ShipModelCatalog.ForTier(Kingmaker.Enums.Size.Cruiser_2x4));
                }
                if (_list == null || _list.Count == 0) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Label(_tier.ToString(), GUILayout.Width(130));
                for (int _i = 0; _i < _list.Count; _i++)
                {
                    var _m = _list[_i];
                    if (GUILayout.Button(_m.HullName, GUILayout.Width(190))) StarshipViewTool.ApplyModelAtTier(_m, _tier);
                }
                GUILayout.EndHorizontal();
            }
            DangerButton(ref _armShipRevert, L.T("还原原版船模"), 140f, 0, () => StarshipViewTool.RevertAll());
            Settings.ShipMountFallback = GUILayout.Toggle(Settings.ShipMountFallback,
                L.T("换船模后自动补上缺失的武器挂点　<color=#d0a050>建议保持默认（开）</color>"
                  + "<color=#aaaaaa>　有些船模没有舰首槽位，关掉后光矛和鱼雷会「在虚空里开火」。</color>"));

            // ★以下整块收进开发区★ 判据是「玩家拿它能做什么决定」：
            //   · 挂点诊断 / 几何诊断 —— 纯 dump 到日志，玩家看不懂也用不上
            //   · 舰首挂点微调滑条 —— 调错了船看起来会变怪，而正确值本来就是自动算的；
            //     这是作者标定挂点时的工具，不是玩法选项
            //   · "学到的舰首比例" / 三层定位法 / 各船模挂点清单 —— 解释的是内部实现，
            //     不是"你该怎么用"。留在玩家区只会让面板显得像调试器。
            // 保留在上面的 ShipMountFallback 开关则相反：它修的是玩家真能看见的 bug
            // （武器在虚空里开火），是个玩法开关。
            if (DevUI)
            {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("挂点诊断"), GUILayout.Width(110))) ShipSlotProbe.Dump();
            if (GUILayout.Button(L.T("挂点几何诊断"), GUILayout.Width(150))) ShipSlotGeometryProbe.Dump();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("舰首挂点微调　前后"), GUILayout.Width(130));
            Settings.ShipProwOffsetPct = (int)GUILayout.HorizontalSlider(Settings.ShipProwOffsetPct, -50f, 50f, GUILayout.Width(130));
            GUILayout.Label(Settings.ShipProwOffsetPct + "%", GUILayout.Width(42));
            GUILayout.Label(L.T("上下"), GUILayout.Width(40));
            Settings.ShipProwUpPct = (int)GUILayout.HorizontalSlider(Settings.ShipProwUpPct, -60f, 60f, GUILayout.Width(130));
            GUILayout.Label(Settings.ShipProwUpPct + "%", GUILayout.Width(42));
            // 拖歪了没法凭记忆拖回来 —— 这个按钮就是"默认值是多少"的答案
            if (GUILayout.Button(L.T("归零"), GUILayout.Width(60)))
            { Settings.ShipProwOffsetPct = 0; Settings.ShipProwUpPct = 0; Log("[挂点] 微调已归零，回到算出来的位置。"); }
            GUILayout.EndHorizontal();
            // 学到的舰首挂点 —— 这是整条链上唯一的地面真值，值得单独一行
            GUILayout.BeginHorizontal();
            if (Settings.ProwLearned)
                GUILayout.Label(L.F("<color=#80ff80>舰首比例已学自「{0}」</color>　下沉 {1}　后收 {2}",
                                    Settings.ProwLearnedFrom,
                                    Settings.ProwDropRatio.ToString("F3"),
                                    Settings.ProwZBackRatio.ToString("F3")), GUILayout.Width(520));
            else
                GUILayout.Label(L.F("<color=#aaaaaa>舰首比例用 Dictator 实测默认值</color>　下沉 {0}　后收 {1}"
                                  + "　<color=#888888>（切一次大巡会重新实测并覆盖）</color>",
                                    Settings.ProwDropRatio.ToString("F3"),
                                    Settings.ProwZBackRatio.ToString("F3")), GUILayout.Width(520));
            Settings.ShipProwUseLearned = GUILayout.Toggle(Settings.ShipProwUseLearned, L.T("用学到的"), GUILayout.Width(90));
            if (Settings.ProwLearned && GUILayout.Button(L.T("忘掉"), GUILayout.Width(60)))
            { Settings.ProwLearned = false; Settings.ProwLearnedFrom = ""; Log("[挂点] 已忘掉学到的舰首挂点，退回公式。"); }
            GUILayout.EndHorizontal();
            GUILayout.Label(L.T("<color=#aaaaaa>0% = 用算出来的船艏位置。合成挂点挂在 StarshipView 下、旋转归零，"
                              + "坐标系的 +Z=船艏 有实据（StarshipFxHitMask 按 mesh.z 分前后舱室）。"
                              + "定位分三层：包围盒+舷炮中线 → 挂点跨度外推 → 借船脊原位；"
                              + "轴向闸门（Port 在 −x、Starboard 在 +x）不通过时直接退到最后一层，不会从船尾开火。"
                              + "这个滑条是在算出来的位置上再沿 +Z 微调，单位是船体 z 向长度。</color>"));
            GUILayout.Label(L.T("<color=#c8a45c>实测挂点（决定武器美术挂不挂得上，挂不上就会「在虚空里开火」）：</color>") + "\n"
                          + L.T("  <color=#7ec8ff>Dictator</color> 20 个：Prow ✓ Keel ✓ Dorsal ✓ Port×4 Starboard×4 —— <color=#7ec8ff>四个里唯一齐全的，大巡默认</color>") + "\n"
                          + L.T("  Gothic 9 个：Port×4 Starboard×4 Dorsal×1 —— <color=#ff8080>缺 Prow，光矛会在虚空开火</color>") + "\n"
                          + L.T("  Universe 运输舰 23 个 / 混沌战列巡洋舰 27 个 —— <color=#ff8080>同样缺 Prow</color>") + "\n"
                          + L.T("<color=#aaaaaa>光矛装在 Prow 槽位。武器美术是挂到船体 prefab 上同类型的 StarshipItemSlot 下面的，"
                              + "匹配不到就退回原点。两个原生大巡船模反而都缺 Prow，所以大巡用放大的 Dictator。</color>"));
            }
            Settings.ShipStretchModel = GUILayout.Toggle(Settings.ShipStretchModel,
                L.T("船模档位低于分档时等比放大撑满（比如把 Gothic 巡洋舰当大巡用 ×1.52）"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("改装界面船模缩放"), GUILayout.Width(130));
            Settings.ShipDollScale = (int)GUILayout.HorizontalSlider(Settings.ShipDollScale, 30f, 200f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipDollScale + "%", GUILayout.Width(46));
            GUILayout.EndHorizontal();
            GUILayout.Label(L.T("<color=#aaaaaa>100% = 归一到原版护卫舰的观感。那个展示房间的机位/灯光/背景"
                              + "全是按护卫舰构图的，换大船不归一就会撑出画面。只影响改装界面，战场模型不受影响。</color>"));
            GUILayout.Label(L.T("<color=#aaaaaa>视觉尺寸与格子占位是两条独立的路："
                              + "分档决定占位/多打判据，prefab 决定外观，"
                              + "DisableSizeScaling 让模型保持原生大小、不被再放大一次。</color>"));

            }

            if (Fold(ref Settings.PanelShowRules, L.T("规则"), L.T("士气池 / 镜头 / 成长 / 装备 / 解除限制")))
            {
            // ---------- 规则 ----------
            GUILayout.Space(8);
            GUILayout.Label(L.T("<b>规则</b>"));
            Settings.AttachFollow     = GUILayout.Toggle(Settings.AttachFollow, L.T("跟随队长"));
            Settings.AlignExperience  = GUILayout.Toggle(Settings.AlignExperience, L.T("招募时按主角经验设起点"));
            Settings.AutoLevelUp      = GUILayout.Toggle(Settings.AutoLevelUp, L.T("自动成长（每次进区域按当前阶位补升级）"));
            Settings.ScaleGuardXp     = GUILayout.Toggle(Settings.ScaleGuardXp, L.T("卫兵经验按比例缩放（不影响队友那份）"));
            Settings.IsolateMomentum  = GUILayout.Toggle(Settings.IsolateMomentum, L.T("士气隔离（卫兵受伤/倒地不扣队伍士气）"));
            Settings.SeparateMomentumPool = GUILayout.Toggle(Settings.SeparateMomentumPool, L.T("卫队独立士气池（大招花自己的；代价是卫兵的 Resolve 也不再进你的池子）"));
            Settings.GuardKillFeedsOwnPool = GUILayout.Toggle(Settings.GuardKillFeedsOwnPool, L.T("卫兵杀敌也给卫队池加分（不动你那份，否则卫队只出力不进账）"));
            Settings.GuardPsykerNoVeil = GUILayout.Toggle(Settings.GuardPsykerNoVeil, L.T("卫兵灵能不推高亚空间威胁　<color=#d0a050>建议保持默认（开）</color>"
                  + "<color=#aaaaaa>　帷幕是区域唯一值、做不了独立池，只能选计不计入。关掉后五个 AI 每回合乱放技能，帷幕会迅速失控。</color>"));
            Settings.NoCameraFollowGuards = GUILayout.Toggle(Settings.NoCameraFollowGuards, L.T("卫兵行动时镜头不跟随（含技能演出特写；你自己队伍不受影响）"));
            Settings.GuardsCanShootInMelee = GUILayout.Toggle(Settings.GuardsCanShootInMelee,
                L.T("卫兵被近战缠住时也能开火　<color=#aaaaaa>原版规则里重武器射击在缠斗中不可用，"
                  + "玩家能手动走位规避、AI 卫兵不能 —— 关掉的话远程卫兵要么整回合忙着退位、要么一枪不开。</color>"));
            // 「发放装备」这四个字太省，作者本人都问过它是干嘛的 ——
            // 作者看不懂的标签，玩家一定看不懂。改成把**两边的后果**都写出来。
            // ---------- 界面语言 ----------
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("界面语言"), GUILayout.Width(70));
            string[] _langs = { L.T("跟随游戏"), "中文", "English" };
            for (int i = 0; i < _langs.Length; i++)
                if (GUILayout.Toggle(Settings.Language == i, _langs[i], "Button", GUILayout.Width(i == 0 ? 90 : 70))
                    && Settings.Language != i)
                    L.Apply(i);   // 立刻生效：重读译文 + 重命名卫兵 + 刷新已开的窗口
            // 原文提到 LocalizationManager.CurrentLocale / l10n_en.json / archetypes.json 的 *_en 字段
            // —— 全是实现细节，玩家看了只会更困惑。想改译文的人自己会去翻文件。
            GUILayout.Label(L.T("<color=#aaaaaa>默认跟随游戏语言，也可以在这里手动切，不用重启。</color>"));
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            Settings.EquipGraduationGear = GUILayout.Toggle(Settings.EquipGraduationGear,
                L.T("<b>给卫兵发装备</b>　<color=#aaaaaa>开：按 archetypes.json 的配表凭空生成一整套"
                  + "（普通卫兵按 T1/T2/T3 三档，精英用专属套），不动你的仓库。"
                  + "关：一件不发，卫兵只有单位蓝图自带的那身 —— 嫌 mod 发的装备太强就关掉。</color>"));
            // ★这一行原来在开发区★ 它是上面那个总开关的强弱旋钮（决定发哪一档），
            // 只改玩法数值、不生成不销毁、装备也是凭空生成不动仓库 —— 属于难度调节而非测试工具。
            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("　└ 普通卫兵发哪一档"), GUILayout.Width(150));
            for (int i = 0; i < 4; i++)
                // ★L.T 要贴着字面量写★ 写成 L.T(new[]{"自动",...}[i]) 的话，
                // tools/check_l10n.py 的字面量扫描会整条跳过（参数不是字符串常量），
                // 于是"自动"既进不了译文表、也不会被报成漏译 —— 静默漏一格。
                if (GUILayout.Toggle(Settings.GearTierOverride == i,
                        new[] { L.T("自动"), "T1", "T2", "T3" }[i], "Button",
                        GUILayout.Width(i == 0 ? 60 : 44)))
                    Settings.GearTierOverride = i;
            GUILayout.Label(L.T("<color=#aaaaaa>「自动」= 按主角等级推。精英不受影响（他们走专属套）。"
                              + "★不追溯★ 只影响之后新生成或补发的，已经穿在身上的不会被扒掉。</color>"));
            GUILayout.EndHorizontal();
            Settings.EliteCanBeDowned = GUILayout.Toggle(Settings.EliteCanBeDowned,
                L.T("精英倒地可救（0 血进昏迷而非死亡）　<color=#aaaaaa>普通卫兵始终永久死亡</color>"));
            GUILayout.Label(L.T("<b>解除限制</b>　<color=#aaaaaa>互不相干的几件事，分开控制</color>"));
            // ★「全部解除」放在单独一行、且排在最前★
            // 原来它和另外三个并排，看着像第四个并列项，实际是总开关 ——
            // 玩家勾了它以后发现精英还是招不了，因为下面那两个精英开关当时没被它管到。
            // 现在 NoEliteCountCap() / NoEliteUnlockGate() 也认它了，位置和文案一并改清楚。
            GUILayout.BeginHorizontal();
            Settings.UnlockTierLimits = GUILayout.Toggle(Settings.UnlockTierLimits,
                L.T("<b>全部解除</b>"), GUILayout.Width(110));
            GUILayout.Label(L.T("<color=#aaaaaa>一个顶下面五个。勾上之后下面几项无论是否勾选都已生效。</color>"));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Settings.UnlockPfGate   = GUILayout.Toggle(Settings.UnlockPfGate,
                L.T("解除利润因子限制"), GUILayout.Width(160));
            Settings.UnlockCountCap = GUILayout.Toggle(Settings.UnlockCountCap,
                L.T("解除数量上限"), GUILayout.Width(140));
            Settings.UnlockLevelCap = GUILayout.Toggle(Settings.UnlockLevelCap,
                L.T("解除等级上限"), GUILayout.Width(140));
            GUILayout.EndHorizontal();
            // ★这两个原来在开发区★ 它们只改玩法数值、没有副作用，属于作弊而非测试工具，
            // 玩家该能碰。更实际的理由：招募窗口的灰字提示按名字引用它们
            // （"面板可勾「解除精英数量上限」"），藏在默认不显示的开发区里等于让玩家
            // 照着提示去找一个看不见的选项。
            GUILayout.BeginHorizontal();
            Settings.UnlockEliteLimit = GUILayout.Toggle(Settings.UnlockEliteLimit,
                L.T("解除精英数量上限"), GUILayout.Width(160));
            Settings.EliteIgnoreUnlock = GUILayout.Toggle(Settings.EliteIgnoreUnlock,
                L.T("无视 T3 解锁条件"), GUILayout.Width(160));
            GUILayout.EndHorizontal();
            GUILayout.Label(L.T("<color=#aaaaaa>"
                + "解除<b>利润因子</b>：名额退回按职业阶位算（T1=2 / T2=4 / T3=6）　"
                + "解除<b>数量</b>：招多少个都行（利润因子和阶位数量一起无视）　"
                + "解除<b>等级</b>：直接顶 55 级、职业链走满三段　"
                + "解除<b>精英数量</b>：每条线的精英不限 2 名　"
                + "无视 <b>T3 解锁</b>：不用先练出 T3 就能招精英"
                + "</color>"));


            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("<b>命名</b>　<color=#aaaaaa>「军衔·人名」，军衔随本人等级三档自动晋升，人名跟他一辈子</color>"), GUILayout.Width(520));
            if (GUILayout.Button(L.T("重新命名全部"), GUILayout.Width(120))) RetinueTest.RenameAll();
            GUILayout.EndHorizontal();
            GUILayout.Label(L.T("<i>军衔取自 archetypes.json 的 guardNames（每条线三档），人名取自根级 guardNamePool；"
                              + "精英用自己的专属军衔。你手改过的名字不会被覆盖 —— 想让 mod 重新接管就点【重新命名全部】。</i>"));

            GUILayout.BeginHorizontal();
            GUILayout.Label(L.T("创伤:"), GUILayout.Width(60));
            string[] _tm = { L.T("无创伤"), L.T("跟队恢复"), L.T("原版") };
            for (int i = 0; i < _tm.Length; i++)
            {
                bool on = (Settings.TraumaMode == i);
                if (GUILayout.Toggle(on, _tm[i], GUILayout.Width(100)) && !on) Settings.TraumaMode = i;
            }
            GUILayout.Label(L.T("    经验比例:"), GUILayout.Width(80));
            Settings.XpRatio = GUILayout.TextField(Settings.XpRatio, GUILayout.Width(60));
            GUILayout.EndHorizontal();
            GUILayout.Label(L.T("<i>无创伤=不进创伤流水线；跟队恢复=队友被治时一起治；原版=每倒地一次永久掉最大生命，且重伤阈值写死 50% 不吃难度减免</i>"));

            // ---------- 经验追赶 ----------
            GUILayout.BeginHorizontal();
            Settings.XpCatchUp = GUILayout.Toggle(Settings.XpCatchUp,
                L.T("经验追赶（落后越多拿越多）"), GUILayout.Width(200));
            GUILayout.Label(L.T("落后"), GUILayout.Width(34));
            Settings.XpCatchUpSpan = (int)GUILayout.HorizontalSlider(Settings.XpCatchUpSpan, 1f, 40f, GUILayout.Width(110));
            GUILayout.Label(L.F("{0} 级吃满", Settings.XpCatchUpSpan), GUILayout.Width(70));
            GUILayout.Label(L.T("上限"), GUILayout.Width(34));
            Settings.XpCatchUpMax = (int)GUILayout.HorizontalSlider(Settings.XpCatchUpMax, 80f, 500f, GUILayout.Width(110));
            GUILayout.Label("×" + (Settings.XpCatchUpMax / 100f).ToString("F1"), GUILayout.Width(46));
            GUILayout.EndHorizontal();
            GUILayout.Label(L.T("<color=#aaaaaa>固定比例的问题是**差距只会单调拉大** —— 越往后招的卫兵越追不上。"
                              + "追赶制：落后 0 级拿「经验比例」那个地板值，落后到设定级数拿满上限，中间线性插值；"
                              + "追平后回落到地板，所以卫兵<b>永远不会反超主角</b>。</color>"));

            }

            // ---------- 反馈（玩家可见）----------
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(L.T("导出诊断包"), GUILayout.Width(120)))
            {
                var _p = DiagnosticReport.Export();
                if (!string.IsNullOrEmpty(_p)) Log("[诊断包] 请把这个文件发给作者：" + _p);
            }
            GUILayout.Label(L.T("<color=#aaaaaa>反馈问题时点这个 —— 会把版本、你改过的设置、在册情况、舰船状态"
                              + "和日志尾部打包成**一个文件**，用户名已抹掉。比直接发 dynasty_log.txt 小得多也全得多。</color>"));
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(DiagnosticReport.LastPath))
                GUILayout.Label(L.F("<color=#7ec8ff>最近导出：{0}</color>", DiagnosticReport.LastPath));
            // ★详细日志：默认关★ 它控制的全是纯观测（士气变化、帷幕跳过、换脑拦截），
            // 一条指令一行，一场会话能把日志灌到好几 MB。原来默认开着、而且**没有开关**，
            // 玩家既关不掉也不知道它存在。现在默认关，报 bug 时再打开重现一次即可。
            GUILayout.BeginHorizontal();
            Settings.WatchMomentum = GUILayout.Toggle(Settings.WatchMomentum,
                L.T("详细日志"), GUILayout.Width(100));
            GUILayout.Label(L.T("<color=#aaaaaa>记录每次士气变化、帷幕跳过、AI 换脑拦截。"
                              + "平时不用开（日志会涨很快）；作者让你复现问题时再打开。</color>"));
            GUILayout.EndHorizontal();

            // 一键还原 —— 调坏了不用去翻 Settings.xml，也不用重装
            GUILayout.BeginHorizontal();
            DangerButton(ref _armReset, L.T("恢复默认设置"), 130f, 0, ResetSettingsToDefault);
            GUILayout.Label(L.T("<color=#aaaaaa>把上面所有选项恢复成初始值。"
                              + "调乱了、或者不确定改过什么的时候点它，比翻 Settings.xml 快。"
                              + "不影响存档里已有的卫兵和座舰。</color>"));
            GUILayout.EndHorizontal();

            // ★预览玩家视角★ 勾上之后，玩家区里所有「只有开发者看得到」的额外内容
            // （配表字段说明、dump 按钮、挂点诊断与微调、内部实现的解释……）全部隐藏，
            // 面板就是玩家装上后看到的样子。拍发布截图用。
            //
            // 预览时**连开发区的折叠头都不画** —— 那行「开发 · 测试　探针/诊断/热键…」
            // 是硬编码中文（玩家看不到所以没做翻译），留在英文面板里会突兀地夹一行中文，
            // 而且它本身就不属于玩家所见。只留一行极简开关，作为回到开发视图的唯一入口。
            //
            // 这个开关用 DevMode 而不是 DevUI：否则勾上之后连它自己都藏了，
            // 只能去删 flag 重启才能恢复。
            if (DevMode && Settings.PreviewAsPlayer)
            {
                GUILayout.Space(10);
                GUILayout.BeginHorizontal();
                Settings.PreviewAsPlayer = GUILayout.Toggle(true, L.T("预览玩家视角"), GUILayout.Width(130));
                GUILayout.Label(L.T("<color=#d0a050>取消勾选即可回到开发视图</color>"));
                GUILayout.EndHorizontal();
                return;
            }

            if (DevMode)
            if (Fold(ref Settings.PanelShowDev, "开发 · 测试", "探针 / 诊断 / 热键　★注意：好几个按钮会清空全部卫兵★"))
            {
            GUILayout.BeginHorizontal();
            Settings.PreviewAsPlayer = GUILayout.Toggle(Settings.PreviewAsPlayer,
                L.T("预览玩家视角"), GUILayout.Width(130));
            GUILayout.Label(L.T("<color=#aaaaaa>勾上后隐藏玩家区里所有开发者专属内容，"
                              + "开发区也只剩一行开关 —— 面板即玩家所见。拍发布截图用，不必删 flag 重启。</color>"));
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            // ---------- 工具 ----------
            GUILayout.Space(8);
            // 从玩家区挪进来的：会整份覆盖 archetypes.json，产物丢掉
            // unit/unitFallback/brain/elites/gear，只有作者调方案时用得上。
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("导入 RTAutoBuilder", GUILayout.Width(150))) Archetypes.ImportFromAutoBuilder();
            GUILayout.Label("<color=#d08080>会覆盖 archetypes.json（先备份成 .bak）。产物只有 name/plan/chain，"
                          + "unit / brain / 精英 / 三档装备全丢。没装 RTAutoBuilder 的话点了不会有任何反应。</color>");
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            // ---------- 一键全测（只在开发模式可见，本区整体已被 DevMode 门住）----------
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("一键自检（只读）", GUILayout.Width(150))) FullTest.RunReadOnly();
            GUILayout.Label("<color=#7ec87e>唯一一个不清场的</color><color=#aaaaaa>：文件 / 分型 / 装备 GUID / 定价 / "
                          + "倒地豁免 / 命名。不生成任何单位，随便点。</color>");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("一键全测（会清空卫兵）", GUILayout.Width(180))) FullTest.RunDestructive();
            GUILayout.Label("<color=#ff8080>自检 + 装备矩阵 + 死亡规则 + 卸载流程。"
                          + "<b>会清空全部卫兵、把座舰还原成原样</b>，跑完别存盘。</color>");
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            // ---------- 实战测试：批量摆人 ----------
            // 和上面两个按钮的区别：这些**只生成、不清场**。一键全测跑完会 Teardown
            // （遣散全部 + 还原座舰），手上一个兵都不剩，没法接着去打。
            GUILayout.Label("<b>实战测试</b>　<color=#aaaaaa>只生成、不清场；生成完去打一场，"
                          + "战斗结束会自动打一份「战斗行为总账」（谁动了、放了什么技能、还是只普攻）</color>");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("生成 5 个普通", GUILayout.Width(130)))  RetinueTest.SpawnAll(true,  false);
            if (GUILayout.Button("生成 10 个精英", GUILayout.Width(130))) RetinueTest.SpawnAll(false, true);
            if (GUILayout.Button("全生成（15 个）", GUILayout.Width(140))) RetinueTest.SpawnAll(true,  true);
            GUILayout.Label("<color=#ffaa66>会绕过名额上限。装备档位用【规则】区那个设，"
                          + "但它不追溯 —— 要先设好再生成。</color>");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            Settings.AutoEndPlayerTurn = GUILayout.Toggle(Settings.AutoEndPlayerTurn,
                "自动结束我的回合", GUILayout.Width(150));
            GUILayout.Label("<color=#ff8080>★你自己的角色会整场什么都不做★ 只为省去反复手点，"
                          + "看完卫兵行为记得关掉。（CanEndTurn 内含 !AnyUnitIsBusy，不会打断动画）</color>");
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            GUILayout.Label("<b>工具</b>");
            GUILayout.BeginHorizontal();
            // ★这两个按钮已合并进【一键全测（会清空卫兵）】★
            // AutoTest.RunAll 和 RunGearMatrix 都会把 25 组配装**各生成一遍**，
            // 而 RunGearMatrix 的日志里已经带了属性（上面那张属性对比表就是从它的输出里解的），
            // RunAll 独有的只剩 brain 记录。为了那一列跑第二遍 25 次生成不划算，
            // brain 已并进 GearTool 的每组日志。留一个入口，少一次误点、少一半时间。
            if (GUILayout.Button("探测 brain", GUILayout.Width(110))) BrainTool.Probe();
            if (GUILayout.Button("探测候选单位", GUILayout.Width(120))) Probe.ProbeUnits();
            if (GUILayout.Button("批量试算方案", GUILayout.Width(120))) PlanProbe.Run();
            if (GUILayout.Button("导出天赋名录", GUILayout.Width(120))) ItemTool.ExportFeatures();
            GUILayout.EndHorizontal();
            // ★这里原来挂着一条描述【一键测装备】/【一键全测】的说明★，但那两个入口
            // 一个已合并、一个（AutoTest.RunAll → autotest.tsv）已是死代码。更糟的是它紧贴在
            // 上面这四个**只读**按钮下面，于是"两个都会自动清场并还原限制"看起来像在说它们。
            // 会清场的是下面【一键全测】那个，标签就写在它自己身上。
            GUILayout.Label("<i>以上四个都是只读探针：读当前状态、写 dynasty_log.txt 或导出文件，"
                          + "不生成单位、不清场、不改存档。</i>");

            GUILayout.BeginHorizontal();
            GUILayout.Label("经验数:", GUILayout.Width(60));
            Settings.DebugXpAmount = GUILayout.TextField(Settings.DebugXpAmount, GUILayout.Width(70));
            if (GUILayout.Button("给卫兵发经验", GUILayout.Width(130)))
            {
                int amt; if (!int.TryParse(Settings.DebugXpAmount, out amt)) amt = 5000;
                RetinueTest.GrantXp(amt);
            }
            if (GUILayout.Button("立即结算成长", GUILayout.Width(130))) RetinueTest.ForceGrowth();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("查物品:", GUILayout.Width(60));
            Settings.ItemQuery = GUILayout.TextField(Settings.ItemQuery, GUILayout.Width(160));
            if (GUILayout.Button("查 GUID", GUILayout.Width(90))) ItemTool.Search(Settings.ItemQuery);
            if (GUILayout.Button("导出物品名录", GUILayout.Width(130))) ItemTool.Export();
            if (GUILayout.Button("护甲排名", GUILayout.Width(90))) ItemTool.RankArmor();
            GUILayout.Label("<i>首次点击要读 2940 条蓝图，需几秒。</i>");
            GUILayout.EndHorizontal();

            // 查询结果直接显示在面板里 —— v0.3.4 只写日志，用户以为按钮没反应
            if (ItemTool.LastHitTotal > 0)
            {
                GUILayout.Label("  命中 " + ItemTool.LastHitTotal + " 条"
                                + (ItemTool.LastHitTotal > ItemTool.LastHits.Count
                                   ? "（只列前 " + ItemTool.LastHits.Count + " 条，关键词再具体些）" : "")
                                + "    <i>【装配】把该物品加进当前分型「" + _archs[_cur].Name + "」的玩家自配清单</i>");
                foreach (var r in ItemTool.LastHits)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(16);
                    GUILayout.Label(r.ZhName, GUILayout.Width(200));
                    GUILayout.Label(r.Type.Replace("Equipment.BlueprintItem", "").Replace("BlueprintItem", ""), GUILayout.Width(110));
                    if (GUILayout.Button("装配", GUILayout.Width(60))) Archetypes.AddPlayerGear(_cur, r.Guid);
                    GUILayout.Label(r.Guid, GUILayout.Width(250));
                    GUILayout.EndHorizontal();
                }
            }
            else if (!string.IsNullOrEmpty(ItemTool.LastQuery))
            {
                GUILayout.Label("  <color=#ffaa00>「" + ItemTool.LastQuery + "」没找到</color>");
            }

            // 当前分型已装配的清单
            {
                var _a = _archs[_cur];
                var pg = _a.PlayerGear;
                GUILayout.Label("  <b>" + _a.Name + "</b> 玩家自配 "
                                + (pg == null ? 0 : pg.Length) + " 件"
                                + "    毕业套装（精英专用）" + (_a.Gear == null ? 0 : _a.Gear.Length) + " 件"
                                + "    精英解锁=" + (GearTool.EliteUnlocked(_cur) ? "<color=#80ff80>是</color>" : "<color=#ff8080>否（该路线还没有卫兵练到 T3）</color>")
                                + "    在册精英 " + GearTool.EliteCount(_cur));
                if (pg != null)
                {
                    for (int i = 0; i < pg.Length; i++)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Space(16);
                        GUILayout.Label(ItemTool.NameOf(pg[i]), GUILayout.Width(200));
                        if (GUILayout.Button("移除", GUILayout.Width(60))) { Archetypes.RemovePlayerGear(_cur, pg[i]); break; }
                        GUILayout.Label(pg[i], GUILayout.Width(250));
                        GUILayout.EndHorizontal();
                    }
                }
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("默认单位 AssetId", GUILayout.Width(110));
            Settings.UnitAssetId = GUILayout.TextField(Settings.UnitAssetId, GUILayout.Width(240));
            if (GUILayout.Button("在游戏内面板打开选中卫兵", GUILayout.Width(200))) RetinueTest.OpenNativePanel();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("<color=#ff8080>死亡规则测试（会真的打死）:</color>", GUILayout.Width(200));
            if (GUILayout.Button("打死一个普通卫兵", GUILayout.Width(150))) RetinueTest.TestKill("normal");
            if (GUILayout.Button("打死一个精英", GUILayout.Width(130))) RetinueTest.TestKill("elite");
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>走的是原版同一条判定路径（SetHitPointsLeft(0) → UnitLifeController.ForceTickOnUnit），"
                          + "不是模拟。预期：普通卫兵 <b>Dead</b> 且从名册移除、名额释放；精英 <b>Unconscious</b> 且仍在册。</color>");
            GUILayout.Label("<i>精英每条路线限 1 个，需该路线先有卫兵练到 T3 职业；用专属蓝图生成（模型/名字都不同），拿毕业套装。普通卫兵拿上面「玩家自配」那套。</i>");

            GUILayout.BeginHorizontal();
            GUILayout.Label("生成热键:", GUILayout.Width(70));
            Settings.SpawnKeyName = GUILayout.TextField(Settings.SpawnKeyName, GUILayout.Width(70));
            GUILayout.Label("遣散热键:", GUILayout.Width(70));
            Settings.DespawnKeyName = GUILayout.TextField(Settings.DespawnKeyName, GUILayout.Width(70));
            if (GUILayout.Button("应用热键", GUILayout.Width(90))) ApplyHotkeys();
            GUILayout.Label("<i>填 Unity KeyCode 名（F7 / G / None）。按住 Ctrl/Alt/Shift 时热键一律不触发，避免和 Ctrl+F10 打架。遣散建议留 None。</i>");
            GUILayout.EndHorizontal();
            }
        }

        /// <summary>把面板里填的 KeyCode 名解析成实际按键。填错就保持原值并报错。</summary>
        private static void ApplyHotkeys()
        {
            KeyCode k;
            if (Enum.TryParse<KeyCode>(Settings.SpawnKeyName, true, out k)) { Settings.SpawnKey = k; Log("生成热键 = " + k); }
            else LogError("无法识别的按键名: " + Settings.SpawnKeyName + "（参考 Unity KeyCode，如 F7 / G / None）");

            if (Enum.TryParse<KeyCode>(Settings.DespawnKeyName, true, out k)) { Settings.DespawnKey = k; Log("遣散热键 = " + k); }
            else LogError("无法识别的按键名: " + Settings.DespawnKeyName);
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry) => Settings.Save(modEntry);

        // UMM 的 Logger 只进面板 Logs 标签页、不落盘，退游戏就没了。
        // 这里同时写一份到 mod 目录，便于事后排查。
        private static string LogPath =>
            System.IO.Path.Combine(ModEntry?.Path ?? ".", "dynasty_log.txt");

        private static void WriteFile(string level, string msg)
        {
            try
            {
                System.IO.File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss}][{level}] {msg}{Environment.NewLine}",
                    System.Text.Encoding.UTF8);
            }
            catch { /* 日志失败不能影响主流程 */ }
        }

        public static void Log(string msg)
        {
            ModEntry?.Logger.Log(msg);
            WriteFile("INFO", msg);
        }
        public static void LogError(Exception e)
        {
            ModEntry?.Logger.Error(e.ToString());
            WriteFile("ERROR", e.ToString());
        }
        public static void LogError(string msg)
        {
            ModEntry?.Logger.Error(msg);
            WriteFile("ERROR", msg);
        }
    }

    public class Settings : UnityModManager.ModSettings
    {
        // DLC3_DL_Guard_Ranged_Ally_Unit —— 原版已按玩家侧盟友设计，最优先候选
        /// <summary>
        /// 全局兜底单位蓝图 —— 分型没配 unit、或配的解析不到时用它。
        ///
        /// ★这里必须是本体蓝图★ 原来的默认值是 02094127…（DLC3_DL_Guard_Ranged_Ally_Unit），
        /// 于是「没买 DLC3」这件事在最后一道兜底上又踩了一次：分型主单位解析不到 →
        /// 退到全局兜底 → 全局兜底也是 DLC3 → 一样招不出人。
        /// 现在是 OfficersDeckGuard（军官分型一直在用的那个），本体自带、实机验证过。
        /// </summary>
        public string UnitAssetId = "1fb60c0ef5fe459980c34a271dfad088";
        public bool AttachFollow = true;
        public bool AlignExperience = true;
        // 原版自带 MechanicsFeatureType.DeathAndTraumasDoesNotAffectMomentum，无需 Harmony
        public bool IsolateMomentum = true;
        // ---------------- 解除限制：拆成三个独立开关 ----------------
        // 原来只有 UnlockTierLimits 一个总开关，同时管着「等级上限 / 数量上限 / 利润因子」
        // 三件互不相干的事，想只放开其中一个做不到。保留它当总开关（=三个全开），
        // 另加三个细粒度的。下面三个方法是给代码用的判据，方法而非属性 ——
        // XmlSerializer 只序列化字段和带 setter 的属性，方法它一定不碰。
        /// <summary>总开关：等于下面三个全部打开。</summary>
        public bool UnlockTierLimits = false;
        /// <summary>只解除**利润因子**限制 —— 名额退回按职业阶位算（T1=2/T2=4/T3=6）。</summary>
        public bool UnlockPfGate   = false;
        /// <summary>解除**数量**上限 —— 招多少个都行（利润因子和阶位数量一起无视）。</summary>
        public bool UnlockCountCap = false;
        /// <summary>解除**等级**上限 —— 卫兵直接顶 55 级、职业链走满三段。</summary>
        public bool UnlockLevelCap = false;

        /// <summary>名额是否完全不限。</summary>
        public bool NoCountCap() { return UnlockTierLimits || UnlockCountCap; }
        /// <summary>是否绕过利润因子（数量全解除时自然也绕过）。</summary>
        public bool NoPfGate()   { return UnlockTierLimits || UnlockCountCap || UnlockPfGate; }
        /// <summary>等级是否不受阶位限制。</summary>
        public bool NoLevelCap() { return UnlockTierLimits || UnlockLevelCap; }

        /// <summary>
        /// 精英数量是否不限。
        ///
        /// ★为什么也要走方法★ 这两个精英开关原来是被**直接读**的
        /// （GearTool.cs 的 495 / 528 行），于是勾了标着「全部解除」的那个总开关之后，
        /// 精英该解锁的还是没解锁 —— 玩家得再单独去勾「无视 T3 解锁条件」。
        /// 标签写着"全部"却管不到全部，这是个纯粹的措辞与实现不一致。
        /// </summary>
        public bool NoEliteCountCap() { return UnlockTierLimits || UnlockEliteLimit; }
        /// <summary>是否跳过「该线先练出一名 T3 卫兵」这个精英解锁前提。</summary>
        public bool NoEliteUnlockGate() { return UnlockTierLimits || EliteIgnoreUnlock; }
        /// <summary>
        /// 详细日志开关。控制的全是**纯观测**：士气变化、帷幕跳过、AI 换脑拦截、
        /// MomentumWatch 每帧轮询。没有任何功能依赖它。
        ///
        /// ★默认必须是 false★ 开着的话卫兵每条指令都打一行，实测一场会话能把
        /// dynasty_log.txt 灌到 5.8 MB；而且 MomentumPatch 每次士气变化都会调
        /// GuardStates() 遍历全部卫兵拼字符串。发布前它一直是 true 且**没有面板开关**，
        /// 玩家既关不掉也不知道有这回事。
        /// </summary>
        public bool WatchMomentum = false;
        // 创伤三档：0=无创伤  1=跟队恢复（队友被治时卫兵一起治）  2=原版
        public int TraumaMode = 0;
        // 卫兵按 XpRatio 缩放拿到的经验（队友那份一分不动，原版每人各拿一份完整值，不存在稀释）
        public bool ScaleGuardXp = true;
        // 卫队独立士气池：自己攒、自己花，不动玩家的
        public bool SeparateMomentumPool = true;
        /// <summary>战斗中卫兵行动时不让镜头跟过去。
        /// 卫队人数多，镜头一个个跟过去会一直跳，很晃眼；玩家自己的队伍不受影响。</summary>
        public bool NoCameraFollowGuards = true;
        /// <summary>卫兵放灵能不推高帷幕（亚空间威胁）。
        /// 帷幕是区域级的单一值，做不了独立池，只能选择计不计入。</summary>
        public bool GuardPsykerNoVeil = true;
        /// <summary>在船上的 NPC 身上挂招募入口（走原生点击交互，不进存档）。</summary>
        public bool NpcRecruitEntry = true;
        /// <summary>挂载目标的蓝图名关键字，逗号分隔、大小写不敏感、子串匹配。
        /// 默认高阶顾问（管家/总管，设定上最贴，且是非可直控 NPC）。
        /// 面板上的【列出可挂载 NPC】会把当前区域的候选打到日志里。</summary>
        public string RecruitNpcKeys = "Factotum";
        /// <summary>把「征募护卫队」作为原生对话选项插进 NPC 的对话列表。
        /// 运行时改蓝图纯内存、重启复原；选中记录只是 GUID 字符串，卸载安全。</summary>
        public bool DialogRecruitEntry = true;

        /// <summary>普通卫兵装备档位覆盖。0=自动（按主角等级推 PlayerTier），1/2/3=强制该档。
        /// 纯测试用途：55 级存档恒为 T3，不覆盖的话 T1/T2 两套装备一次都触发不到。</summary>
        public int GearTierOverride = 0;

        /// <summary>★状态，不是设置★ 下面这些是运行时状态或派生值，**永远不要给它们做 UI**：
        /// LastAugmentTier / PanelShow* / Prow*（实测学到的挂点比值）/ ArchetypeIndex /
        /// SpawnKey|DespawnKey（由对应的 *Name 字符串解析而来）。
        /// 它们进 Settings.xml 只是为了跨会话记住，语义上不属于"玩家可调的选项"。
        /// Prow 三项已经以「只读展示 + 【忘掉】按钮」的形态出现在面板上，那是对的形态。</summary>
        /// <summary>上次看到的植入物层级（AugmentTier）。-1 = 还没记录过。
        /// 用来判断"剧情解锁了"，从而给已有卫兵补发更好的植入物。存在 UMM 的设置文件里，不进游戏存档。</summary>
        public int LastAugmentTier = -1;

        /// <summary>舰船「多打」：按舰船分档给武器槽加每回合开火次数。不改蓝图、不改配置界面。</summary>
        public bool ShipExtraShots = true;
        /// <summary>巡洋舰：左右舷炮额外开火次数（默认 +1 = 两打）。</summary>
        public int ShipCruiserBroadside = 1;
        /// <summary>大巡洋舰：左右舷炮额外开火次数（默认 +2 = 三打）。</summary>
        public int ShipGrandBroadside = 2;
        /// <summary>大巡洋舰：船首/背炮额外开火次数（默认 +1 = 两打）。</summary>
        public int ShipGrandProw = 1;

        /// <summary>巡洋舰：船脊/船首/光矛的射程加成。</summary>
        public int ShipCruiserRange = 3;
        /// <summary>大巡洋舰：舷炮射程加成。</summary>
        public int ShipGrandRangeBroadside = 3;
        /// <summary>大巡洋舰：船脊/船首/光矛射程加成。</summary>
        public int ShipGrandRangeProw = 5;

        /// <summary>允许在太空战**战斗中**切换舰船分档。默认关 —— 格子占位会变，寻路网格未必跟着重算。</summary>
        public bool ShipSwitchInCombat = false;

        /// <summary>巡洋舰：护盾上限加成百分比（50 = ×1.5）。</summary>
        public int ShipCruiserShieldPct = 50;
        /// <summary>大巡洋舰：护盾上限加成百分比（100 = ×2）。</summary>
        public int ShipGrandShieldPct = 100;

        /// <summary>巡洋舰：装甲（减伤）加成百分比。</summary>
        public int ShipCruiserArmourPct = 50;
        /// <summary>大巡洋舰：装甲（减伤）加成百分比。</summary>
        public int ShipGrandArmourPct = 100;

        /// <summary>巡洋舰：撞角额外行程 = 速度 × 此百分比。机动性不动（大船本该更笨重）。</summary>
        public int ShipCruiserRamPct = 100;
        /// <summary>大巡洋舰：撞角额外行程 = 速度 × 此百分比。</summary>
        public int ShipGrandRamPct = 200;

        /// <summary>船模档位低于当前分档时等比放大撑满。
        /// 用途：GrandCruiser_3x6 只有混沌战列巡洋舰和帝国运输舰两个模型，
        /// 想要「帝国 Gothic 级的大巡」只能把巡洋舰船模放大。</summary>
        public bool ShipStretchModel = true;

        /// <summary>改装界面（ShipDollRoom）里船模的额外倍率，100 = 归一到原版护卫舰的观感。
        /// 那个房间的机位是按护卫舰构图的，换大船必然撑出画面 —— 纯显示，随便调。</summary>
        public int ShipDollScale = 100;

        // ---------------- 招募上限：按利润因子解锁 ----------------
        /// <summary>用利润因子决定招募上限（关掉则退回旧的阶位上限 T1=2/T2=4/T3=6）。</summary>
        public bool RecruitUsePfGate = true;
        /// <summary>每名卫兵需要多少利润因子。默认 15，即 90 解锁全部 6 名。</summary>
        public int RecruitPfPerGuard = 15;
        /// <summary>招募硬上限。默认 6。</summary>
        public int RecruitMaxGuards = 6;

        // ---------------- 面板分区折叠状态（纯 UI，不影响任何玩法）----------------
        // 默认只展开「规则」：那一区是常用开关（死亡规则/士气/镜头/成长/解除限制），
        // 而「招募」和「舰船」的日常入口都在游戏内（NPC 对话 + 两个 uGUI 窗口），
        // 面板里那两区主要是配置和诊断，不必一开面板就糊一屏。
        public bool PanelShowRecruit = false;
        public bool PanelShowShip    = false;
        public bool PanelShowRules   = true;
        /// <summary>开发/测试区。默认**折叠** —— 那些按钮玩家用不到，而且好几个会清场。</summary>
        public bool PanelShowDev     = false;

        /// <summary>
        /// 卫兵在被近战缠住时也能开火。
        ///
        /// 原版规则（AbilityData.cs:884）：缠斗中不能用 UsingInThreateningArea=CannotUse 的技能，
        /// 而重武器射击基本都是这一类。玩家可以手动走位规避，卫兵是 AI 控制、不能微操 ——
        /// 实测结果是远程卫兵要么把回合全花在退位上（29 动作只打 5 次），
        /// 要么干脆一枪不开站着被打死。所以默认开。
        /// </summary>
        public bool GuardsCanShootInMelee = true;

        /// <summary>
        /// 自动结束玩家回合。**纯测试用**：观察卫兵 AI 时不用一直手点结束回合。
        /// 只在开发模式下生效且默认关闭 —— 它会让你自己的角色整场什么都不做。
        /// </summary>
        public bool AutoEndPlayerTurn = false;

        /// <summary>换船模后，给船体补上缺失的武器挂点（否则光矛/鱼雷会从舰船原点开火）。</summary>
        public bool ShipMountFallback = true;
        /// <summary>合成的 Prow 挂点相对船脊往前推多少（占船体最长边的百分比）。
        /// 默认 0 = 纯船脊位置 —— 船体 prefab 的朝向轴我没有实据，猜错会从船尾开火。</summary>
        public int ShipProwOffsetPct = 0;
        /// <summary>合成的 Prow 挂点相对船脊高度再抬多少（占船体 y 向高度的百分比）。</summary>
        public int ShipProwUpPct = 0;
        /// <summary>连船底(Keel)挂点也合成。默认关 —— 见 ShipMountFallback 里的说明，
        /// 一件武器美术可以列多个 RequiredSlots，补了可能多长出一份挂在船腹下。</summary>
        /// <summary>
        /// 从**原生**舰首挂点学来的归一化位置（相对船体包围盒）。
        /// Dictator 之类自带 Prow 挂点的船模一出现就会自动学，之后套用到 Gothic 这种缺挂点的船上。
        /// 归一化而不是绝对坐标：两条船长短不一，搬比例才对，搬坐标会落到船体外。
        /// </summary>
        /// <summary>换船模装好武器后，自动重拍改装界面里那条船。
        /// ShipDollRoom 的展示模型是一次性哑拷贝，不自动跟新武器美术。</summary>
        public bool ShipDollResnap = true;
        /// <summary>同一挂点被多件武器的美术抢时，优先显示光矛（其次新星炮/宏炮，鱼雷垫底）。
        /// vanilla 是先毁后建、只能活一件，谁赢本来取决于遍历顺序、玩家控制不了。</summary>
        public bool ShipArtPreferLance = true;
        /// <summary>界面语言：0=跟随游戏，1=中文，2=English。
        /// 默认跟随 —— 装英文版游戏的人开箱即英文，不用先来设置里找开关。</summary>
        public int Language;
        /// <summary>解除船体限制：连未校准的船体也允许更换（挂点/缩放可能不对）。</summary>
        public bool ShipYardUnlockAll;
        /// <summary>在 NPC 对话里加「船坞改装」两条选项（用废料换巡洋 / 大巡）。</summary>
        public bool ShipDialogEntry = true;
        public int ShipPriceCruiser = 500;
        /// <summary>大巡**总价**。低于巡洋总价时由 ShipDialog.TotalFor 夹住，见那里的说明。</summary>
        public int ShipPriceGrand = 1000;

        public bool  ProwLearned;
        /// <summary>舰首比舷炮低多少，以「舷炮→船脊」的高度差为 1 单位。
        /// 默认 0.784 = Dictator 原生 prow_01 实测：(-0.01-(-0.41))/(0.50-(-0.01))。
        /// 也就是舰首炮基本贴龙骨线（-0.41 vs 龙骨 -0.44）。</summary>
        public float ProwDropRatio = 0.784f;
        /// <summary>从船体**实体**前端（命中遮罩 frontHitPositions 的最大 z）往回收多少，占船长。
        /// 默认 0.043 = Dictator 实测 (2.94-2.68)/5.99。
        /// 不用包围盒最前端：Gothic 的包围盒比实体船头多出 0.56，那是一根细撞角。</summary>
        public float ProwZBackRatio = 0.043f;
        /// <summary>撞角让位系数：舰首挂点再沿轴向退开「撞角外伸长度 × 本系数」。
        /// 撞角外伸 = 包围盒最前 − 命中遮罩最前（Gothic 0.56 / Dictator 0.06）。
        /// 1.0 = 完全让开撞角；0 = 不让。每条船自己量，不是固定偏移。</summary>
        public float ProwRamClearance = 1.0f;
        public string ProwLearnedFrom = "";
        /// <summary>关掉就退回公式（公式猜错过六版，只作退路）。</summary>
        public bool ShipProwUseLearned = true;

        public bool ShipSynthKeel = false;
        // 卫兵杀敌同时也给卫队池加一份（不动玩家那份）
        public bool GuardKillFeedsOwnPool = true;
        // 每次区域加载按当前阶位补升级 —— 卫兵"跟久了自己成长"
        public bool AutoLevelUp = true;
        // 0=先锋 1=狙击 2=连射 3=灵能
        public int ArchetypeIndex = 0;
        // 毕业装备：凭空生成（不动玩家仓库）。精英拿 gear，普通拿玩家自配的 playerGear
        public bool EquipGraduationGear = true;
        /// <summary>
        /// 每条分型里**每种精英**各允许几个（不是"每条分型总共几个"）。
        /// 默认 1 = 每种精英一个；一条分型有 2 种精英，所以默认每条分型能有 2 名。
        /// 实际判据在 GearTool.cs:533 —— `EliteCount(arch) >= cap * arch.Elites.Length`。
        /// ★这行注释原来写的是"每条路线限一个"，和代码不符★ 语义没错、注释错了。
        /// 另外要先有卫兵练到 T3 才解锁（EliteIgnoreUnlock 可跳过）。
        /// </summary>
        public int EliteLimitPerArchetype = 1;
        public bool UnlockEliteLimit = false;
        public bool EliteIgnoreUnlock = false;
        public string DebugXpAmount = "5000";
        public string ItemQuery = "";
        public string SpawnKeyName = "F7";
        public string DespawnKeyName = "None";
        public string XpRatio = "0.8";
        /// <summary>追赶制：卫兵落后主角越多，拿的经验倍率越高，追平后回落到 XpRatio。</summary>
        public bool XpCatchUp = true;
        /// <summary>追赶倍率上限（百分比，250 = 2.5 倍）。</summary>
        public int XpCatchUpMax = 250;
        /// <summary>落后多少级时吃满上限。中间线性插值。</summary>
        public int XpCatchUpSpan = 15;
        /// <summary>精英倒地可救（0 血进昏迷而非死亡）。普通卫兵始终永久死亡 ——
        /// 那是原版对 ExCompanion 的默认行为，不需要我们做任何事。</summary>
        public bool EliteCanBeDowned = true;
        /// <summary>
        /// 只在开发模式下有意义：把玩家区里开发者专属的额外内容临时藏起来，
        /// 面板变成玩家装上后看到的样子。用来拍发布截图，省得删 flag 重启。
        /// 不进发布包的行为（开发区整体由 dynasty_dev.flag 控制），所以这个值对玩家无影响。
        /// </summary>
        public bool PreviewAsPlayer = false;

        public KeyCode SpawnKey = KeyCode.F7;
        // 遣散 = 永久销毁，默认不给热键，只能从面板点
        public KeyCode DespawnKey = KeyCode.None;

        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
    }
}
