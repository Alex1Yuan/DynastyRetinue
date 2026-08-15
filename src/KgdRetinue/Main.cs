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

            modEntry.OnToggle  = OnToggle;
            modEntry.OnGUI     = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUpdate  = OnUpdate;

            HarmonyInstance = new Harmony(modEntry.Info.Id);
            try
            {
                HarmonyInstance.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
                Log("Harmony 补丁已应用。");
            }
            catch (Exception e) { LogError("Harmony 补丁失败（不影响其余功能）: " + e); }

            RetinueLifecycle.Subscribe();

            Log("loaded.  版本 " + (modEntry.Info != null ? modEntry.Info.Version : "?"));
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            if (value) RetinueLifecycle.Subscribe();
            else
            {
                RetinueLifecycle.Unsubscribe();
                // 刻意不自动遣散：卫兵现在是持久实体，误触开关不该清掉满级卫队。
                // 遣散必须由玩家显式点按钮。
                int n = RetinueRegistry.Count;
                if (n > 0) Log("注意：仍有 " + n + " 名卫兵留在存档中。禁用 mod 或 DLC 前请先点【遣散全部】。");
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

            // ---------- 分型 ----------
            GUILayout.Space(8);
            var _archs = Archetypes.All;
            int _cur = Settings.ArchetypeIndex;
            if (_cur < 0 || _cur >= _archs.Length) _cur = 0;
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
            Settings.UnlockTierLimits = GUILayout.Toggle(Settings.UnlockTierLimits, "解除阶位限制（无视职业阶位，直接顶 55 级 / 数量不限）");

            GUILayout.BeginHorizontal();
            Settings.RenameGuards = GUILayout.Toggle(Settings.RenameGuards, "自定义卫兵名（压掉单位蓝图自带的名字）", GUILayout.Width(300));
            GUILayout.Label("名字前缀:", GUILayout.Width(70));
            Settings.GuardNamePrefix = GUILayout.TextField(Settings.GuardNamePrefix, GUILayout.Width(100));
            if (GUILayout.Button("重新命名全部", GUILayout.Width(120))) RetinueTest.RenameAll();
            GUILayout.EndHorizontal();
            GUILayout.Label("<i>命名为「前缀·分型 编号」。只在卫兵还没有自定义名时赋值；改了前缀要点【重新命名全部】才会重算。</i>");

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

            // ---------- 工具 ----------
            GUILayout.Space(8);
            GUILayout.Label("<b>工具</b>");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("探测 brain", GUILayout.Width(120))) BrainTool.Probe();
            if (GUILayout.Button("探测候选单位", GUILayout.Width(130))) Probe.ProbeUnits();
            if (GUILayout.Button("批量试算方案", GUILayout.Width(130))) PlanProbe.Run();
            if (GUILayout.Button("★ 一键全测 ★", GUILayout.Width(130))) AutoTest.RunAll();
            if (GUILayout.Button("导出天赋名录", GUILayout.Width(130))) ItemTool.ExportFeatures();
            GUILayout.Label("<i>一键全测：清场 → 每个分型生成全部精英+一个普通 → 收集命中率/属性/装备 → 写 autotest.tsv → 自动遣散。临时解除数量与解锁限制。</i>");
            GUILayout.EndHorizontal();

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
        public bool UnlockTierLimits = false;
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
        public KeyCode SpawnKey = KeyCode.F7;
        // 遣散 = 永久销毁，默认不给热键，只能从面板点
        public KeyCode DespawnKey = KeyCode.None;

        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
    }
}
