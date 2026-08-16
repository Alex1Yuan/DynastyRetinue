using System;
using UnityEngine;
using UnityModManagerNet;
using HarmonyLib;

namespace KgdRetinue
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

        // UMM 入口。Info.json 里 EntryMethod = "KgdRetinue.Main.Load"
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;
            Settings = UnityModManager.ModSettings.Load<Settings>(modEntry);

            // 这两个曾经是开关，现在是唯一路径：新窗口早就是正式 UI，命名也早就成体系了。
            // 面板上不再露出来，但字段留着 —— 老 Settings.xml 里可能存着 false，
            // 不在这里强制拉回 true，升级上来的玩家会莫名其妙没窗口/没名字。
            try { Settings.UseNewUI = true; Settings.RenameGuards = true; } catch { }

            modEntry.OnToggle  = OnToggle;
            modEntry.OnGUI     = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUpdate  = OnUpdate;

            HarmonyInstance = new Harmony(modEntry.Info.Id);
            PatchAllSafe(HarmonyInstance, System.Reflection.Assembly.GetExecutingAssembly());

            RetinueLifecycle.Subscribe();
            DeathRules.Subscribe();

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
            if (Settings != null && Settings.UseNewUI)
            {
                try { UI.RetinueUI.Open(); return; }
                catch (Exception e) { LogError("[UI] 新窗口开启失败，回退到旧窗口: " + e); }
            }
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

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (!Enabled) return;
            try
            {
                // 热键。★ 必须先排除修饰键 ★
                // Input.GetKeyDown(F10) 在按住 Ctrl 时照样为 true，而 Ctrl+F10 是 UMM 的
                // 开面板热键 —— 结果是每次开面板都顺手遣散一次卫队。卫兵现在是持久实体，
                // 遣散 = 永久销毁，这个误触代价太大。
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

                RetinueLifecycle.TickPending();
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
            GUILayout.Label("<b>KgdRetinue v" + (ModEntry != null && ModEntry.Info != null ? ModEntry.Info.Version : "?") + "</b>");
            GUILayout.Label("<b>卫队</b>   在册 " + RetinueRegistry.Count + "   " + RetinueRegistry.Describe());
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("生成一个", GUILayout.Width(110))) RetinueTest.SpawnOne();
            if (GUILayout.Button("Dump 状态", GUILayout.Width(110))) RetinueTest.DumpState();
            if (GUILayout.Button("遣散全部", GUILayout.Width(110))) RetinueRegistry.DismissAll();
            GUILayout.EndHorizontal();
            // ★ 存档安全提醒 ★ 卫兵是持久实体，写进 party.json。
            // 禁用 mod / 关掉 DLC / 换 Steam 账号之后再读档，卫兵引用的蓝图解析不到会导致存档打不开。
            // 有卫兵在册时把数量也报出来，比一句干巴巴的静态警告更能让人真去点遣散。
            {
                int alive = 0;
                try { alive = RetinueRegistry.Count; } catch { }
                GUILayout.Label(alive > 0
                    ? "<color=#ff8080><b>⚠ 存档里有 " + alive + " 名卫兵。</b>禁用 mod、在 Steam 里关闭 DLC、"
                      + "或更换 Steam 账号之前，请先点【遣散全部】—— 否则读档时卫兵引用的蓝图可能解析不到。</color>"
                    : "<color=#ff8080>卫兵是持久实体，会写进存档（party.json）。禁用 mod 或在 Steam 里关闭 DLC 之前，请先点【遣散全部】。</color>");
            }

            // 分型索引在多个分区里都要用（招募区选它、开发区按它生成），
            // 所以提到折叠块外面声明，不能留在「招募」区的大括号里
            var _archs = Archetypes.All;
            int _cur = Settings.ArchetypeIndex;
            if (_cur < 0 || _cur >= _archs.Length) _cur = 0;

            if (Fold(ref Settings.PanelShowRecruit, "招募", "分型 / 入口 / 名额上限 / 装备档位"))
            {
            // ---------- 分型 ----------
            GUILayout.Space(8);
            GUILayout.Label("<b>分型</b>   当前 = <color=#80ff80>" + _archs[_cur].Name + "</color>"
                            + "    <i>（模板 archetypes.json：unit=模型/装备, brain=AI行为, plan=天赋方案, chain=职业链）</i>");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < _archs.Length; i++)
            {
                string label = (i == _cur ? "● " : "○ ") + _archs[i].Name;
                if (GUILayout.Button(label, GUILayout.Width(150))) Settings.ArchetypeIndex = i;
                if (i % 4 == 3 && i < _archs.Length - 1) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("重载模板", GUILayout.Width(110))) Archetypes.Reload();
            if (GUILayout.Button("导入 RTAutoBuilder", GUILayout.Width(150))) Archetypes.ImportFromAutoBuilder();
            if (GUILayout.Button("列出加点方案", GUILayout.Width(130))) BuildPlans.Reload();
            GUILayout.EndHorizontal();

            // ---------- 招募入口 ----------
            GUILayout.Space(8);
            GUILayout.Label("<b>招募入口</b>（挂在 NPC 身上的原生点击交互，不进存档）");
            Settings.NpcRecruitEntry = GUILayout.Toggle(Settings.NpcRecruitEntry, "点击 NPC 弹招募面板（原生点击交互）");
            Settings.DialogRecruitEntry = GUILayout.Toggle(Settings.DialogRecruitEntry, "在 NPC 对话里加一条「征募护卫队」选项");
            GUILayout.BeginHorizontal();
            GUILayout.Label("目标 NPC 关键字", GUILayout.Width(110));
            Settings.RecruitNpcKeys = GUILayout.TextField(Settings.RecruitNpcKeys ?? "", GUILayout.Width(220));
            if (GUILayout.Button("挂到当前区域", GUILayout.Width(110)))
            { RecruitEntry.AttachInArea(true); RecruitDialog.InjectInArea(true); }
            if (GUILayout.Button("列出可挂载 NPC", GUILayout.Width(130))) RecruitEntry.ListCandidates();
            if (GUILayout.Button("直接开窗", GUILayout.Width(90))) OpenRecruitUI(null);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("预览新窗口", GUILayout.Width(110))) UI.RetinueUI.Open();
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>名单打在 kgd_log.txt 里。本船的高阶顾问蓝图名是 HighFactotum，音阵大师是 VoxMaster。</color>");

            // ---------- 装备档位覆盖 ----------
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>装备档位</b>（普通卫兵）", GUILayout.Width(150));
            string[] tierNames = { "自动（跟玩家等级）", "强制 T1", "强制 T2", "强制 T3" };
            for (int i = 0; i < 4; i++)
                if (GUILayout.Toggle(Settings.GearTierOverride == i, tierNames[i], "Button", GUILayout.Width(i == 0 ? 150 : 80)))
                    Settings.GearTierOverride = i;
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>自动档由主角等级推出（≥36 = T3，≥16 = T2）。"
                          + "55 级存档恒为 T3，要验 T1/T2 那两套就在这里强制。改完对已招募的卫兵无效，重新招一个才会按新档位发。</color>");

            }

            if (Fold(ref Settings.PanelShowShip, "舰船", "分档加成 / 换船模 / 挂点"))
            {
            // ---------- 舰船 ----------
            GUILayout.Space(8);
            // 状态行：不点任何按钮就能看出"现在到底是不是巡洋舰"。
            // 之前只能靠点一次切换按钮、从日志里读「当前分档=」，太绕。
            {
                string _sz = "?", _pf = "原版";
                try { _sz = StarshipTool.CurrentSize().ToString(); } catch { }
                try { var _p = StarshipViewTool.CurrentPrefab;
                      if (!string.IsNullOrEmpty(_p))
                      { var _k = ShipModelCatalog.ByPrefab(_p); _pf = _k != null ? _k.Hull : _p; } }
                catch { }
                GUILayout.Label("<b>当前座舰</b>　分档 = <color=#80ff80>" + _sz + "</color>"
                              + "　船模 = <color=#80ff80>" + _pf + "</color>"
                              + "　<color=#aaaaaa>两项都写进存档，但要**存过盘**才留得住：改完直接读档就没了。</color>");
            }
            GUILayout.Label("<b>舰船</b>（只改开火次数，不动配置界面、不扩槽位、不改蓝图）");
            Settings.ShipExtraShots = GUILayout.Toggle(Settings.ShipExtraShots,
                "换大船后同一槽位可多次开火（当前舰船分档: " + StarshipChargesPatch.ShipSize() + "）");
            GUILayout.BeginHorizontal();
            GUILayout.Label("巡洋舰 舷炮 +", GUILayout.Width(110));
            Settings.ShipCruiserBroadside = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserBroadside, 0f, 4f, GUILayout.Width(120));
            GUILayout.Label(Settings.ShipCruiserBroadside.ToString(), GUILayout.Width(24));
            GUILayout.Label("大巡洋 舷炮 +", GUILayout.Width(110));
            Settings.ShipGrandBroadside = (int)GUILayout.HorizontalSlider(Settings.ShipGrandBroadside, 0f, 4f, GUILayout.Width(120));
            GUILayout.Label(Settings.ShipGrandBroadside.ToString(), GUILayout.Width(24));
            GUILayout.Label("大巡洋 船首/背炮 +", GUILayout.Width(140));
            Settings.ShipGrandProw = (int)GUILayout.HorizontalSlider(Settings.ShipGrandProw, 0f, 4f, GUILayout.Width(120));
            GUILayout.Label(Settings.ShipGrandProw.ToString(), GUILayout.Width(24));
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>护卫舰/袭击舰无加成，保持原版手感。数值是「额外」次数：+1 = 两打。</color>");

            GUILayout.BeginHorizontal();
            GUILayout.Label("换船（默认：巡洋/大巡都用 Gothic）", GUILayout.Width(210));
            if (GUILayout.Button("护卫舰", GUILayout.Width(80)))   StarshipViewTool.ApplyTierDefault(Kingmaker.Enums.Size.Frigate_1x2);
            if (GUILayout.Button("巡洋舰", GUILayout.Width(80)))   StarshipViewTool.ApplyTierDefault(Kingmaker.Enums.Size.Cruiser_2x4);
            if (GUILayout.Button("大巡洋舰", GUILayout.Width(90)))  StarshipViewTool.ApplyTierDefault(Kingmaker.Enums.Size.GrandCruiser_3x6);
            GUILayout.EndHorizontal();
            Settings.ShipSwitchInCombat = GUILayout.Toggle(Settings.ShipSwitchInCombat, "允许战斗中换船（有风险：格子占位会变，寻路网格未必跟着重算）");
            GUILayout.BeginHorizontal();
            GUILayout.Label("护盾上限 +%  巡洋", GUILayout.Width(130));
            Settings.ShipCruiserShieldPct = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserShieldPct, 0f, 200f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipCruiserShieldPct + "%", GUILayout.Width(46));
            GUILayout.Label("大巡", GUILayout.Width(40));
            Settings.ShipGrandShieldPct = (int)GUILayout.HorizontalSlider(Settings.ShipGrandShieldPct, 0f, 300f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipGrandShieldPct + "%", GUILayout.Width(46));
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>只对玩家座舰生效（GetMax 是全舰船共用的，不加判据会把敌舰护盾也翻倍）。</color>");
            GUILayout.BeginHorizontal();
            GUILayout.Label("装甲减伤 +%  巡洋", GUILayout.Width(130));
            Settings.ShipCruiserArmourPct = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserArmourPct, 0f, 200f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipCruiserArmourPct + "%", GUILayout.Width(46));
            GUILayout.Label("大巡", GUILayout.Width(40));
            Settings.ShipGrandArmourPct = (int)GUILayout.HorizontalSlider(Settings.ShipGrandArmourPct, 0f, 300f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipGrandArmourPct + "%", GUILayout.Width(46));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("撞角行程 +%  巡洋", GUILayout.Width(130));
            Settings.ShipCruiserRamPct = (int)GUILayout.HorizontalSlider(Settings.ShipCruiserRamPct, 0f, 400f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipCruiserRamPct + "%", GUILayout.Width(46));
            GUILayout.Label("大巡", GUILayout.Width(40));
            Settings.ShipGrandRamPct = (int)GUILayout.HorizontalSlider(Settings.ShipGrandRamPct, 0f, 400f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipGrandRamPct + "%", GUILayout.Width(46));
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>撞角没有可乘的「基础距离」常量（行程来自寻路），"
                          + "所以按「速度 × 百分比」折算成额外格数。机动性不动。</color>");
            GUILayout.Label("<color=#ffaa66>注意：舰船分档是 [JsonProperty]，会写进存档。"
                          + "它是 vanilla 枚举、不碰存档红线，卸载 mod 后存档照样能开，"
                          + "但船会保持在你切过去的那一档 —— 要还原就切回护卫舰再存一次。</color>");

            // ---------- 换船模（真外观）----------
            GUILayout.Space(6);
            GUILayout.Label("<b>换船模</b>（真外观。点了会同时把分档设成对应档位）");
            var _curPrefab = StarshipViewTool.CurrentPrefab;
            var _curModel = string.IsNullOrEmpty(_curPrefab) ? null : ShipModelCatalog.ByPrefab(_curPrefab);
            GUILayout.Label("当前：" + (_curModel != null ? _curModel.ToString() : "<color=#aaaaaa>原版模型</color>"));
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
                    if (GUILayout.Button(_m.Hull, GUILayout.Width(190))) StarshipViewTool.ApplyModelAtTier(_m, _tier);
                }
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button("还原原版船模", GUILayout.Width(140))) StarshipViewTool.RevertAll();
            if (GUILayout.Button("挂点诊断", GUILayout.Width(110))) ShipSlotProbe.Dump();
            if (GUILayout.Button("挂点几何诊断", GUILayout.Width(130))) ShipSlotGeometryProbe.Dump();
            Settings.ShipMountFallback = GUILayout.Toggle(Settings.ShipMountFallback,
                "换船模后自动补上缺失的武器挂点（修「光矛/鱼雷在虚空开火」）");
            GUILayout.BeginHorizontal();
            GUILayout.Label("舰首挂点微调　前后", GUILayout.Width(130));
            Settings.ShipProwOffsetPct = (int)GUILayout.HorizontalSlider(Settings.ShipProwOffsetPct, -50f, 50f, GUILayout.Width(130));
            GUILayout.Label(Settings.ShipProwOffsetPct + "%", GUILayout.Width(42));
            GUILayout.Label("上下", GUILayout.Width(40));
            Settings.ShipProwUpPct = (int)GUILayout.HorizontalSlider(Settings.ShipProwUpPct, -60f, 60f, GUILayout.Width(130));
            GUILayout.Label(Settings.ShipProwUpPct + "%", GUILayout.Width(42));
            // 拖歪了没法凭记忆拖回来 —— 这个按钮就是"默认值是多少"的答案
            if (GUILayout.Button("归零", GUILayout.Width(60)))
            { Settings.ShipProwOffsetPct = 0; Settings.ShipProwUpPct = 0; Log("[挂点] 微调已归零，回到算出来的位置。"); }
            GUILayout.EndHorizontal();
            // 学到的舰首挂点 —— 这是整条链上唯一的地面真值，值得单独一行
            GUILayout.BeginHorizontal();
            if (Settings.ProwLearned)
                GUILayout.Label("<color=#80ff80>舰首比例已学自「" + Settings.ProwLearnedFrom + "」</color>　"
                              + "下沉 " + Settings.ProwDropRatio.ToString("F3")
                              + "　后收 " + Settings.ProwZBackRatio.ToString("F3"), GUILayout.Width(520));
            else
                GUILayout.Label("<color=#aaaaaa>舰首比例用 Dictator 实测默认值</color>　"
                              + "下沉 " + Settings.ProwDropRatio.ToString("F3")
                              + "　后收 " + Settings.ProwZBackRatio.ToString("F3")
                              + "　<color=#888888>（切一次大巡会重新实测并覆盖）</color>", GUILayout.Width(520));
            Settings.ShipProwUseLearned = GUILayout.Toggle(Settings.ShipProwUseLearned, "用学到的", GUILayout.Width(90));
            if (Settings.ProwLearned && GUILayout.Button("忘掉", GUILayout.Width(60)))
            { Settings.ProwLearned = false; Settings.ProwLearnedFrom = ""; Log("[挂点] 已忘掉学到的舰首挂点，退回公式。"); }
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>0% = 用算出来的船艏位置。合成挂点挂在 StarshipView 下、旋转归零，"
                          + "坐标系的 +Z=船艏 有实据（StarshipFxHitMask 按 mesh.z 分前后舱室）。"
                          + "定位分三层：包围盒+舷炮中线 → 挂点跨度外推 → 借船脊原位；"
                          + "轴向闸门（Port 在 −x、Starboard 在 +x）不通过时直接退到最后一层，不会从船尾开火。"
                          + "这个滑条是在算出来的位置上再沿 +Z 微调，单位是船体 z 向长度。</color>");
            GUILayout.Label("<color=#c8a45c>实测挂点（决定武器美术挂不挂得上，挂不上就会「在虚空里开火」）：</color>\n"
                          + "  <color=#7ec8ff>Dictator</color> 20 个：Prow ✓ Keel ✓ Dorsal ✓ Port×4 Starboard×4 —— <color=#7ec8ff>四个里唯一齐全的，大巡默认</color>\n"
                          + "  Gothic 9 个：Port×4 Starboard×4 Dorsal×1 —— <color=#ff8080>缺 Prow，光矛会在虚空开火</color>\n"
                          + "  Universe 运输舰 23 个 / 混沌战列巡洋舰 27 个 —— <color=#ff8080>同样缺 Prow</color>\n"
                          + "<color=#aaaaaa>光矛装在 Prow 槽位。武器美术是挂到船体 prefab 上同类型的 StarshipItemSlot 下面的，"
                          + "匹配不到就退回原点。两个原生大巡船模反而都缺 Prow，所以大巡用放大的 Dictator。</color>");
            Settings.ShipStretchModel = GUILayout.Toggle(Settings.ShipStretchModel,
                "船模档位低于分档时等比放大撑满（比如把 Gothic 巡洋舰当大巡用 ×1.52）");
            GUILayout.BeginHorizontal();
            GUILayout.Label("改装界面船模缩放", GUILayout.Width(130));
            Settings.ShipDollScale = (int)GUILayout.HorizontalSlider(Settings.ShipDollScale, 30f, 200f, GUILayout.Width(140));
            GUILayout.Label(Settings.ShipDollScale + "%", GUILayout.Width(46));
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>100% = 归一到原版护卫舰的观感。那个展示房间的机位/灯光/背景"
                          + "全是按护卫舰构图的，换大船不归一就会撑出画面。只影响改装界面，战场模型不受影响。</color>");
            GUILayout.Label("<color=#aaaaaa>视觉尺寸与格子占位是两条独立的路："
                          + "分档决定占位/多打判据，prefab 决定外观，"
                          + "DisableSizeScaling 让模型保持原生大小、不被再放大一次。</color>");

            }

            if (Fold(ref Settings.PanelShowRules, "规则", "士气池 / 镜头 / 成长 / 命名 / 解除限制"))
            {
            // ---------- 规则 ----------
            GUILayout.Space(8);
            GUILayout.Label("<b>规则</b>");
            Settings.AttachFollow     = GUILayout.Toggle(Settings.AttachFollow, "跟随队长");
            Settings.AlignExperience  = GUILayout.Toggle(Settings.AlignExperience, "招募时按主角经验设起点");
            Settings.AutoLevelUp      = GUILayout.Toggle(Settings.AutoLevelUp, "自动成长（每次进区域按当前阶位补升级）");
            Settings.ScaleGuardXp     = GUILayout.Toggle(Settings.ScaleGuardXp, "卫兵经验按比例缩放（不影响队友那份）");
            Settings.IsolateMomentum  = GUILayout.Toggle(Settings.IsolateMomentum, "士气隔离（卫兵受伤/倒地不扣队伍士气）");
            Settings.SeparateMomentumPool = GUILayout.Toggle(Settings.SeparateMomentumPool, "卫队独立士气池（大招花自己的；代价是卫兵的 Resolve 也不再进你的池子）");
            Settings.GuardKillFeedsOwnPool = GUILayout.Toggle(Settings.GuardKillFeedsOwnPool, "卫兵杀敌也给卫队池加分（不动你那份，否则卫队只出力不进账）");
            Settings.GuardPsykerNoVeil = GUILayout.Toggle(Settings.GuardPsykerNoVeil, "卫兵灵能不推高亚空间威胁（帷幕是区域唯一值、做不了独立池，只能选计不计入）");
            Settings.NoCameraFollowGuards = GUILayout.Toggle(Settings.NoCameraFollowGuards, "卫兵行动时镜头不跟随（含技能演出特写；你自己队伍不受影响）");
            Settings.EliteCanBeDowned = GUILayout.Toggle(Settings.EliteCanBeDowned,
                "精英倒地可救（0 血进昏迷而非死亡）　<color=#aaaaaa>普通卫兵始终永久死亡 —— "
                + "那是原版对 ExCompanion 的默认行为（UnitLifeController.CalculateLifeState），不需要我们做任何事</color>");
            GUILayout.Label("<b>解除限制</b>　<color=#aaaaaa>三件互不相干的事，分开控制</color>");
            GUILayout.BeginHorizontal();
            Settings.UnlockPfGate   = GUILayout.Toggle(Settings.UnlockPfGate,
                "解除利润因子限制", GUILayout.Width(160));
            Settings.UnlockCountCap = GUILayout.Toggle(Settings.UnlockCountCap,
                "解除数量上限", GUILayout.Width(140));
            Settings.UnlockLevelCap = GUILayout.Toggle(Settings.UnlockLevelCap,
                "解除等级上限", GUILayout.Width(140));
            Settings.UnlockTierLimits = GUILayout.Toggle(Settings.UnlockTierLimits,
                "<b>全部解除</b>", GUILayout.Width(110));
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>"
                + "解除<b>利润因子</b>：名额退回按职业阶位算（T1=2 / T2=4 / T3=6）　"
                + "解除<b>数量</b>：招多少个都行（利润因子和阶位数量一起无视）　"
                + "解除<b>等级</b>：直接顶 55 级、职业链走满三段"
                + "</color>");

            // ---------- 招募上限：利润因子 ----------
            GUILayout.Space(6);
            Settings.RecruitUsePfGate = GUILayout.Toggle(Settings.RecruitUsePfGate,
                "<b>用利润因子解锁招募名额</b>（关掉则退回旧的阶位上限 T1=2 / T2=4 / T3=6）");
            if (Settings.RecruitUsePfGate)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("每名所需利润因子", GUILayout.Width(130));
                Settings.RecruitPfPerGuard = (int)GUILayout.HorizontalSlider(Settings.RecruitPfPerGuard, 1f, 60f, GUILayout.Width(140));
                GUILayout.Label(Settings.RecruitPfPerGuard.ToString(), GUILayout.Width(40));
                GUILayout.Label("最多几名", GUILayout.Width(60));
                Settings.RecruitMaxGuards = (int)GUILayout.HorizontalSlider(Settings.RecruitMaxGuards, 0f, 12f, GUILayout.Width(120));
                GUILayout.Label(Settings.RecruitMaxGuards.ToString(), GUILayout.Width(30));
                GUILayout.EndHorizontal();
                GUILayout.Label("<color=#c8a45c>" + ProfitFactorGate.Summary() + "</color>");
                // 分级表：把每一档的门槛列出来，玩家一眼看到下一档还差多少
                try
                {
                    var _th = ProfitFactorGate.Thresholds();
                    int _pf = ProfitFactorGate.Current();
                    var _sb = new System.Text.StringBuilder("<color=#aaaaaa>分级：");
                    for (int _i = 0; _i < _th.Length; _i++)
                    {
                        bool _got = _pf >= _th[_i];
                        _sb.Append(_got ? "<color=#7ec8ff>" : "").Append(_th[_i]).Append("→").Append(_i + 1).Append("名")
                           .Append(_got ? "</color>" : "").Append(_i + 1 < _th.Length ? "　" : "");
                    }
                    GUILayout.Label(_sb.Append("</color>").ToString());
                }
                catch { }
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>命名</b>　<color=#aaaaaa>「军衔·人名」，军衔随本人等级三档自动晋升，人名跟他一辈子</color>", GUILayout.Width(520));
            if (GUILayout.Button("重新命名全部", GUILayout.Width(120))) RetinueTest.RenameAll();
            GUILayout.EndHorizontal();
            GUILayout.Label("<i>军衔取自 archetypes.json 的 guardNames（每条线三档），人名取自根级 guardNamePool；"
                          + "精英用自己的专属军衔。你手改过的名字不会被覆盖 —— 想让 mod 重新接管就点【重新命名全部】。</i>");

            GUILayout.BeginHorizontal();
            GUILayout.Label("创伤:", GUILayout.Width(60));
            string[] _tm = { "无创伤", "跟队恢复", "原版" };
            for (int i = 0; i < _tm.Length; i++)
            {
                bool on = (Settings.TraumaMode == i);
                if (GUILayout.Toggle(on, _tm[i], GUILayout.Width(100)) && !on) Settings.TraumaMode = i;
            }
            GUILayout.Label("    经验比例:", GUILayout.Width(80));
            Settings.XpRatio = GUILayout.TextField(Settings.XpRatio, GUILayout.Width(60));
            GUILayout.Label("    默认单位 AssetId（分型未指定 unit 时用）:", GUILayout.Width(280));
            Settings.UnitAssetId = GUILayout.TextField(Settings.UnitAssetId, GUILayout.Width(280));
            GUILayout.EndHorizontal();
            GUILayout.Label("<i>无创伤=不进创伤流水线；跟队恢复=队友被治时一起治；原版=每倒地一次永久掉最大生命，且重伤阈值写死 50% 不吃难度减免</i>");

            // ---------- 经验追赶 ----------
            GUILayout.BeginHorizontal();
            Settings.XpCatchUp = GUILayout.Toggle(Settings.XpCatchUp,
                "经验追赶（落后越多拿越多）", GUILayout.Width(200));
            GUILayout.Label("落后", GUILayout.Width(34));
            Settings.XpCatchUpSpan = (int)GUILayout.HorizontalSlider(Settings.XpCatchUpSpan, 1f, 40f, GUILayout.Width(110));
            GUILayout.Label(Settings.XpCatchUpSpan + " 级吃满", GUILayout.Width(70));
            GUILayout.Label("上限", GUILayout.Width(34));
            Settings.XpCatchUpMax = (int)GUILayout.HorizontalSlider(Settings.XpCatchUpMax, 80f, 500f, GUILayout.Width(110));
            GUILayout.Label("×" + (Settings.XpCatchUpMax / 100f).ToString("F1"), GUILayout.Width(46));
            GUILayout.EndHorizontal();
            GUILayout.Label("<color=#aaaaaa>固定比例的问题是**差距只会单调拉大** —— 越往后招的卫兵越追不上。"
                          + "追赶制：落后 0 级拿「经验比例」那个地板值，落后到设定级数拿满上限，中间线性插值；"
                          + "追平后回落到地板，所以卫兵<b>永远不会反超主角</b>。</color>");

            }

            if (Fold(ref Settings.PanelShowDev, "开发 · 测试", "★注意：好几个按钮会清空全部卫兵★"))
            {
            // ---------- 工具 ----------
            GUILayout.Space(8);
            GUILayout.Label("<b>工具</b>");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("★ 一键全测 ★", GUILayout.Width(130))) AutoTest.RunAll();
            if (GUILayout.Button("★ 一键测装备 ★", GUILayout.Width(140))) AutoTest.RunGearMatrix();
            if (GUILayout.Button("探测 brain", GUILayout.Width(110))) BrainTool.Probe();
            if (GUILayout.Button("探测候选单位", GUILayout.Width(120))) Probe.ProbeUnits();
            if (GUILayout.Button("批量试算方案", GUILayout.Width(120))) PlanProbe.Run();
            if (GUILayout.Button("导出天赋名录", GUILayout.Width(120))) ItemTool.ExportFeatures();
            GUILayout.EndHorizontal();
            GUILayout.Label("<i>一键测装备：5 分型 × T1/T2/T3 = 15 组普通卫兵 <b>+ 全部 10 个精英</b>，一次跑完，写 geartest.tsv。"
                          + "　一键全测：额外收集命中率/属性，写 autotest.tsv。两个都会自动清场并还原限制。</i>");

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
            Settings.EquipGraduationGear = GUILayout.Toggle(Settings.EquipGraduationGear, "发放装备", GUILayout.Width(90));
            Settings.UnlockEliteLimit = GUILayout.Toggle(Settings.UnlockEliteLimit, "解除精英数量上限", GUILayout.Width(150));
            Settings.EliteIgnoreUnlock = GUILayout.Toggle(Settings.EliteIgnoreUnlock, "无视 T3 解锁条件", GUILayout.Width(150));
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
            System.IO.Path.Combine(ModEntry?.Path ?? ".", "kgd_log.txt");

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
        public string UnitAssetId = "02094127ee4c402fbedbce1aff086e62";
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
        public bool WatchMomentum = true;
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

        /// <summary>用新的 uGUI 窗口（仿原版配色/字体）。关掉则回退到旧的 IMGUI 窗口。</summary>
        public bool UseNewUI = true;

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
        public bool PanelShowRecruit = true;
        public bool PanelShowShip    = true;
        public bool PanelShowRules   = true;
        /// <summary>开发/测试区。默认**折叠** —— 那些按钮玩家用不到，而且好几个会清场。</summary>
        public bool PanelShowDev     = false;

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
        public bool  ProwLearned;
        /// <summary>舰首比舷炮低多少，以「舷炮→船脊」的高度差为 1 单位。
        /// 默认 0.784 = Dictator 原生 prow_01 实测：(-0.01-(-0.41))/(0.50-(-0.01))。
        /// 也就是舰首炮基本贴龙骨线（-0.41 vs 龙骨 -0.44）。</summary>
        public float ProwDropRatio = 0.784f;
        /// <summary>从船体最前端往回收多少，占船长。默认 0.053 = Dictator 实测 (3.00-2.68)/5.99。</summary>
        public float ProwZBackRatio = 0.053f;
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
        // 用自定义名压掉单位蓝图自带的显示名（灵能分型的 Inquisitor 蓝图顶着具名角色的名字）
        public bool RenameGuards = true;
        public string GuardNamePrefix = "卫兵";
        // 毕业装备：凭空生成（不动玩家仓库）。精英拿 gear，普通拿玩家自配的 playerGear
        public bool EquipGraduationGear = true;
        // 精英：每条路线限一个，且要先有卫兵练到 T3 才解锁
        public int EliteLimitPerArchetype = 1;
        public bool UnlockEliteLimit = false;
        public bool EliteIgnoreUnlock = false;
        public string DebugXpAmount = "5000";
        // 卫兵数量上限覆盖：0/空 = 用内置默认（T1=2 T2=4 T3=6）
        public string GuardCapOverride = "0";
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
        public KeyCode SpawnKey = KeyCode.F7;
        // 遣散 = 永久销毁，默认不给热键，只能从面板点
        public KeyCode DespawnKey = KeyCode.None;

        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
    }
}
