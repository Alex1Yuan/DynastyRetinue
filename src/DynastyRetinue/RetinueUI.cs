using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DynastyRetinue.UI
{
    // =====================================================================
    // 1) 原版观感素材：不带任何资源文件，全部从场景里"活着"的原版 UI 上摘。
    //    摘活对象的好处：材质/图集/SDF 都是游戏自己加载好的，
    //    不需要 shader 修复（那是 AssetBundle 路线才有的 pink-square 问题）。
    // =====================================================================
    internal static class VanillaSkin
    {
        private static bool _fontTried;
        private static TMP_FontAsset _font;
        private static Material _fontMat;

        private static bool _spriteTried;
        private static Sprite _panel;
        private static Sprite _button;
        private static Sprite _row;

        // 兜底配色（摘不到贴图时用纯色；数值先按原版金/墨色取近似，后续用 DumpUIConfigColors 校准）
        public static readonly Color Gold    = new Color(0.776f, 0.635f, 0.306f, 1f);
        public static readonly Color GoldDim = new Color(0.55f, 0.45f, 0.22f, 1f);
        public static readonly Color Ink     = new Color(0.043f, 0.051f, 0.047f, 0.96f);
        public static readonly Color RowBg   = new Color(0.09f, 0.10f, 0.09f, 0.90f);
        public static readonly Color Text    = new Color(0.909f, 0.870f, 0.769f, 1f);
        public static readonly Color TextDim = new Color(0.62f, 0.60f, 0.55f, 1f);

        // 白名单：按 sprite 名取，不按面积猜。名字来自 sharedassets0 的 UI 图集条目。
        private static readonly string[] PanelNames =
        {
            "ModalWindow_HoloBorderWindow2", "ModalWindow_HoloBorderWindow3",
            "Frame_common_01", "Frame_common_02", "Monitor_FrameBox", "PopupBackground",
        };
        private static readonly string[] ButtonNames =
        {
            "ButtonPanel_Normal", "ActionBar_Button_Normal", "ButtonRightPanel_Normal",
        };
        private static readonly string[] RowNames =
        {
            "Monitor_FrameContent", "ModalWindow_HoloBackground", "Frame_common_02",
        };

        public static TMP_FontAsset Font { get { EnsureFont(); return _font; } }
        public static Material FontMaterial { get { EnsureFont(); return _fontMat; } }
        public static Sprite PanelSprite { get { EnsureSprites(); return _panel; } }
        public static Sprite ButtonSprite { get { EnsureSprites(); return _button; } }
        public static Sprite RowSprite { get { EnsureSprites(); return _row; } }

        /// <summary>
        /// 挑字体时用来打分的探针串 —— 全是 mod 界面上真会出现的字。
        ///
        /// ★为什么需要打分，不能拿到第一个就用★
        ///   这游戏同时加载着四十来个 TMP 字体，其中**只有五到八个有中文**，
        ///   其余是拉丁文/数字/图标字体。原来的写法是 FirstOrDefault ——
        ///   摘到哪个纯看遍历顺序，而那个顺序因机器、因存档、因当前场景而异。
        ///   摘中没有中文的那一个，招募窗口和船坞窗口就是满屏白方块，
        ///   而作者本机一直摘得到对的那个，所以永远复现不出来。
        ///   有玩家反馈"看不到字"、附带的截图正是这种：按钮上的字正常
        ///   （那几个字恰好在字体里），标题和条目全是方块。
        /// </summary>
        private const string FontProbe =
            "船坞座舰改装招募卫队分型近战狙击连射灵能军官废料利润因子解锁名额精英已是原样关闭还原默认设置巡洋舰护卫舰帝国级";

        private static void EnsureFont()
        {
            if (_fontTried) return;
            _fontTried = true;
            try
            {
                // 只认场景里活着的 TMP 文本 —— FindObjectsOfTypeAll 会返回未实例化的 prefab，
                // 它们的材质可能没加载，用了就是粉方块。
                var live = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                    .Where(t => t != null && t.font != null
                             && t.fontSharedMaterial != null
                             && t.gameObject.scene.IsValid());

                TMP_FontAsset bestFont = null; Material bestMat = null; int bestScore = -1;
                var scored = new Dictionary<int, int>();   // fontAsset InstanceID -> 覆盖数

                foreach (var t in live)
                {
                    int id = t.font.GetInstanceID();
                    int score;
                    if (!scored.TryGetValue(id, out score))
                    {
                        score = CoverageOf(t.font);
                        scored[id] = score;
                    }
                    // ★字体和材质必须成对★ 拿 A 的字体配 B 的材质会渲染成方块/粉块，
                    // 所以这里始终取同一个 TMP 文本身上的那一对。
                    if (score > bestScore) { bestScore = score; bestFont = t.font; bestMat = t.fontSharedMaterial; }
                    if (bestScore >= FontProbe.Length) break;   // 全覆盖，不用再找
                }

                if (bestFont != null && bestScore > 0)
                {
                    _font = bestFont; _fontMat = bestMat;
                    Main.Log($"[UI] 摘到字体 {bestFont.name}，探针覆盖 {bestScore}/{FontProbe.Length}"
                           + (bestScore < FontProbe.Length ? "　★覆盖不全，界面可能出现方块★" : ""));
                    return;
                }

                // 场景里一个带中文的都没摘到，退回全量字体资产按覆盖率挑。
                // 这一步没有配套材质，只能用字体自己的 material。
                TMP_FontAsset any = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                    .Where(f => f != null)
                    .OrderByDescending(CoverageOf)
                    .FirstOrDefault();
                if (any != null)
                {
                    _font = any; _fontMat = any.material;
                    Main.Log($"[UI] 场景里没摘到合适字体，退回资产 {any.name}，探针覆盖 {CoverageOf(any)}/{FontProbe.Length}");
                    return;
                }

                Main.Log("[UI] 未摘到原版 TMP 字体，退回 TMP 默认字体（能用，观感退化）");
            }
            catch (Exception e) { Main.LogError("[UI] 摘字体失败: " + e.Message); }
        }

        /// <summary>探针串里有多少个字这个字体（含 fallback 链）能显示。</summary>
        private static int CoverageOf(TMP_FontAsset f)
        {
            if (f == null) return 0;
            try
            {
                var set = new HashSet<uint>();
                CollectChars(f, set, new HashSet<int>(), 0);
                int n = 0;
                foreach (char c in FontProbe) if (set.Contains(c)) n++;
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 收集字体及其 fallback 链的全部字符。
        /// visited 防环 —— TMP 允许 A→B→A 这种配置，不防会无限递归；depth 是第二道保险。
        /// </summary>
        private static void CollectChars(TMP_FontAsset f, HashSet<uint> set, HashSet<int> visited, int depth)
        {
            if (f == null || depth > 6) return;
            if (!visited.Add(f.GetInstanceID())) return;
            try
            {
                var table = f.characterTable;
                if (table != null)
                    for (int i = 0; i < table.Count; i++)
                        if (table[i] != null) set.Add(table[i].unicode);
            }
            catch { }
            try
            {
                var fb = f.fallbackFontAssetTable;
                if (fb != null) foreach (var g in fb) CollectChars(g, set, visited, depth + 1);
            }
            catch { }
        }

        private static void EnsureSprites()
        {
            if (_spriteTried) return;
            _spriteTried = true;
            try
            {
                Dictionary<string, Sprite> live = new Dictionary<string, Sprite>();
                foreach (Image img in Resources.FindObjectsOfTypeAll<Image>())
                {
                    if (img == null || img.sprite == null) continue;
                    if (!img.gameObject.scene.IsValid()) continue;
                    string n = img.sprite.name;
                    if (!live.ContainsKey(n)) live[n] = img.sprite;
                }
                _panel  = Pick(live, PanelNames);
                _button = Pick(live, ButtonNames);
                _row    = Pick(live, RowNames);
                Main.Log("[UI] 摘贴图: panel=" + Nm(_panel) + " button=" + Nm(_button) + " row=" + Nm(_row)
                         + " (候选池 " + live.Count + ")");
            }
            catch (Exception e) { Main.LogError("[UI] 摘贴图失败: " + e.Message); }
        }

        private static Sprite Pick(Dictionary<string, Sprite> pool, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                Sprite s;
                if (pool.TryGetValue(names[i], out s) && s != null) return s;
            }
            return null;
        }

        private static string Nm(Sprite s) { return s == null ? "null" : s.name; }

        /// <summary>探针：把场景里活着的九宫格 sprite 全打到日志，用来挑白名单。</summary>
        public static void DumpNineSliceCandidates(int max = 120)
        {
            try
            {
                var seen = new HashSet<string>();
                int n = 0;
                foreach (Image img in Resources.FindObjectsOfTypeAll<Image>())
                {
                    if (img == null || img.sprite == null) continue;
                    if (!img.gameObject.scene.IsValid()) continue;
                    if (img.sprite.border == Vector4.zero) continue;
                    if (!seen.Add(img.sprite.name)) continue;
                    Main.Log("[UI/9slice] " + img.sprite.name
                             + "  border=" + img.sprite.border
                             + "  size=" + img.sprite.rect.width + "x" + img.sprite.rect.height);
                    if (++n >= max) break;
                }
                Main.Log("[UI/9slice] 共 " + n + " 个候选");
            }
            catch (Exception e) { Main.LogError(e.Message); }
        }

        /// <summary>切场景/回主菜单后旧引用可能已随场景卸载，重开窗口前重摘。</summary>
        public static void Reset()
        {
            _fontTried = false; _font = null; _fontMat = null;
            _spriteTried = false; _panel = null; _button = null; _row = null;
        }
    }

    // =====================================================================
    // 2) 窗口本体：自建 Canvas（路线 B）。
    //    不碰任何原版 GameObject、不碰存档：只读 Archetypes / GearTool / 蓝图立绘，
    //    写操作只有点「招募」时调 RetinueTest.SpawnOne（那是既有逻辑，与本 UI 无关）。
    // =====================================================================
    public static class RetinueUI
    {
        private const string RootName = "DynastyRetinue_UI";

        private static GameObject _root;
        private static UiHost _host;
        private static Transform _archContent;
        private static Transform _unitContent;
        private static TextMeshProUGUI _titleRight;
        /// <summary>标题下方那条利润因子状态。招募名额由它解锁，每次 Refresh 重画。</summary>
        private static TextMeshProUGUI _pfLabel;
        private static int _selected = -1;

        /// <summary>
        /// 左栏分型按钮的底图，用来切换选中高亮。
        ///
        /// ★为什么要存着★ 高亮原来只在 RebuildArchetypes() 里画，而点分型的回调只调
        /// RebuildUnits()（重建右栏）—— 于是左栏的高亮要等到下一次完整 Refresh()
        /// 才更新，表现就是"点了左边没反应，得先点一次右边的招募才亮"。
        /// 直接在回调里重建左栏也能修，但那会销毁重建一整列按钮：闪一下，
        /// 而且滚动位置会被顶回顶部。存着底图只改颜色，两者都没有。
        /// </summary>
        private static readonly List<Image> _archBg = new List<Image>();
        private static Color _archBgNormal;

        /// <summary>按 _selected 重涂左栏高亮。不重建任何 GameObject。</summary>
        private static void PaintArchSelection()
        {
            for (int i = 0; i < _archBg.Count; i++)
            {
                Image bg = _archBg[i];
                if (bg == null) continue;   // 窗口关过又开，旧对象已销毁
                bg.color = (i == _selected)
                    ? new Color(VanillaSkin.Gold.r, VanillaSkin.Gold.g, VanillaSkin.Gold.b, 0.28f)
                    : _archBgNormal;
            }
        }

        public static bool IsOpen { get { return _root != null; } }

        // ------------------------------------------------------------- 生命周期
        public static void Open()
        {
            if (IsOpen) { Refresh(); return; }
            try
            {
                _root = new GameObject(RootName,
                    typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                UnityEngine.Object.DontDestroyOnLoad(_root);

                Canvas c = _root.GetComponent<Canvas>();
                VanillaWidgets.PrepareCanvas(c);       // TMP 需要 TexCoord1

                CanvasScaler sc = _root.GetComponent<CanvasScaler>();
                sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                sc.referenceResolution = new Vector2(1920f, 1080f);
                sc.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                sc.matchWidthOrHeight = 0.5f;

                _host = _root.AddComponent<UiHost>();

                BuildClickBlocker(_root.transform);
                EnsureEventSystem(_root.transform);
                BuildFrame(_root.transform);

                _selected = -1;
                RefreshProfitFactor();   // ★别漏★ 首次开窗原本只调 Rebuild*，状态条永远是空的
                RebuildArchetypes();
                RebuildUnits();

                // ★ 放最后：此时整棵树已建好，SetLayerRecursive 一次盖到底
                ApplyVanillaRenderPath(c);
            }
            catch (Exception e)
            {
                Main.LogError("[UI] 开窗失败: " + e);
                Close();
            }
        }

        public static void Close()
        {
            try
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
            }
            catch (Exception e) { Main.LogError("[UI] 关窗异常: " + e.Message); }
            _root = null; _host = null;
            _archContent = null; _unitContent = null; _titleRight = null;
            _selected = -1;
        }

        /// <summary>mod 禁用/卸载：销毁 Canvas 根（所有子物体一并没）、清素材缓存、归还立绘资源句柄。</summary>
        public static void Shutdown()
        {
            Close();
            VanillaSkin.Reset();
            VanillaWidgets.Reset();      // 销毁 KGD_CloneHolder（DontDestroyOnLoad，否则热重载泄漏）
            Deferred.Shutdown();
            try
            {
                foreach (var kv in _gen)
                {
                    if (kv.Value == null) continue;
                    Texture tx = kv.Value.texture;
                    UnityEngine.Object.Destroy(kv.Value);
                    if (tx != null) UnityEngine.Object.Destroy(tx);
                }
            }
            catch { }
            _gen.Clear();
            try { UnitPortraits.Cleanup(); } catch { }
            // 兜底：万一有上一次会话遗留的同名根（热重载场景），按名字扫掉
            try
            {
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (go != null && go.name == RootName && go.scene.IsValid())
                        UnityEngine.Object.Destroy(go);
                }
            }
            catch { }
        }

        public static void Toggle() { if (IsOpen) Close(); else Open(); }

        public static void Refresh() { RefreshProfitFactor(); RebuildArchetypes(); RebuildUnits(); }

        /// <summary>
        /// 重画标题下那条利润因子状态。
        /// 已解锁的档位标蓝，未解锁的留灰 —— 玩家一眼看到下一档还差多少。
        /// </summary>
        private static void RefreshProfitFactor()
        {
            if (_pfLabel == null) return;
            try
            {
                if (Main.Settings != null && Main.Settings.NoCountCap())
                {
                    _pfLabel.text = L.T("<color=#7ec8ff>已在面板解除数量上限 —— 招募名额不受利润因子约束</color>");
                    return;
                }
                if (Main.Settings != null && Main.Settings.NoPfGate())
                {
                    _pfLabel.text = L.T("<color=#aaaaaa>已解除利润因子限制 —— 名额按职业阶位算（T1=2 / T2=4 / T3=6）</color>");
                    return;
                }
                if (Main.Settings != null && !Main.Settings.RecruitUsePfGate)
                {
                    _pfLabel.text = L.T("<color=#aaaaaa>招募名额按职业阶位限制（T1=2 / T2=4 / T3=6）。想改成按利润因子解锁请到 mod 面板勾选。</color>");
                    return;
                }

                int pf = ProfitFactorGate.Current();
                int un = ProfitFactorGate.Unlocked();
                int cap = ProfitFactorGate.HardCap();
                int have = RetinueRegistry.Count;
                int next = ProfitFactorGate.NextThreshold();

                var sb = new System.Text.StringBuilder();
                sb.Append(L.F("利润因子 <color=#7ec8ff>{0}</color>　名额 <color=#7ec8ff>{1}/{2}</color>（上限 {3}）",
                              pf < 0 ? "?" : pf.ToString(), have, un, cap));
                if (next > 0 && pf >= 0)
                    sb.Append("　").Append(L.F("下一名需 <color=#7ec8ff>{0}</color>，还差 {1}", next, next - pf));
                else if (pf >= 0)
                    sb.Append("　").Append(L.T("已全部解锁"));

                // 分级表
                var th = ProfitFactorGate.Thresholds();
                if (th.Length > 0)
                {
                    sb.Append("　　");
                    for (int i = 0; i < th.Length; i++)
                    {
                        bool got = pf >= th[i];
                        sb.Append(got ? "<color=#7ec8ff>" : "<color=#7a7a7a>")
                          .Append(th[i]).Append("</color>");
                        if (i + 1 < th.Length) sb.Append("<color=#5a5a5a>·</color>");
                    }
                }
                _pfLabel.text = sb.ToString();
            }
            catch (Exception e) { Main.LogError("[UI] 利润因子状态刷新失败: " + e.Message); }
        }

        // ------------------------------------------------------------- 骨架搭建
        internal static void BuildClickBlocker(Transform parent)
        {
            GameObject go = NewUI("ClickBlocker", parent);
            Stretch(go, 0f);
            Image img = go.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.55f);
            img.raycastTarget = true;    // 吃掉点击，避免点穿到世界
        }

        internal static void EnsureEventSystem(Transform parent)
        {
            if (EventSystem.current != null) return;
            // 挂在自己根下 -> 关窗一起销毁，不污染全局
            GameObject es = new GameObject("DynastyRetinue_EventSystem",
                typeof(EventSystem), typeof(StandaloneInputModule));
            es.transform.SetParent(parent, false);
        }

        private static void BuildFrame(Transform parent)
        {
            // 主面板
            GameObject panel = NewUI("Panel", parent);
            RectTransform prt = (RectTransform)panel.transform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = new Vector2(1280f, 780f);
            PaintPanel(panel.AddComponent<Image>(), PanelTex(), VanillaSkin.Ink);

            // 标题栏
            TextMeshProUGUI title = MakeLabel(panel.transform, L.T("卫队招募"), 34f, VanillaSkin.Gold,
                                              TextAlignmentOptions.Left);
            RectTransform trt = (RectTransform)title.transform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.offsetMin = new Vector2(32f, -76f); trt.offsetMax = new Vector2(-180f, -20f);

            // 利润因子状态条 —— 招募名额由它解锁，所以放在标题正下方最显眼的位置
            // ★高度必须给够★ TMP 在 Ellipsis/Truncate 模式下，rect 高度小于所需行高时
            // 会把**整个字符串清空**而不是截断。之前给 16px 配 17 号字，结果一片空白。
            _pfLabel = MakeLabel(panel.transform, "", 16f, VanillaSkin.Gold, TextAlignmentOptions.Left);
            _pfLabel.enableWordWrapping = false;
            _pfLabel.overflowMode = TextOverflowModes.Overflow;
            RectTransform pfrt = (RectTransform)_pfLabel.transform;
            pfrt.anchorMin = new Vector2(0f, 1f); pfrt.anchorMax = new Vector2(1f, 1f);
            pfrt.pivot = new Vector2(0.5f, 1f);
            pfrt.offsetMin = new Vector2(32f, -108f); pfrt.offsetMax = new Vector2(-40f, -78f);

            // 关闭按钮
            Button close = MakeButton(panel.transform, L.T("关闭"), 110f, 38f, Close);
            RectTransform crt = (RectTransform)close.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-28f, -22f);

            // 左列：分型
            GameObject left = NewUI("LeftColumn", panel.transform);
            RectTransform lrt = (RectTransform)left.transform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0f, 1f);
            lrt.pivot = new Vector2(0f, 0.5f);
            lrt.offsetMin = new Vector2(28f, 28f);
            lrt.offsetMax = new Vector2(28f + 320f, -116f);
            PaintPanel(left.AddComponent<Image>(), RowTex(), VanillaSkin.RowBg);
            MakeSectionLabel(left.transform, L.T("分型"));
            _archContent = MakeScrollArea(left.transform, 44f);

            // 右列：该分型下的单位
            GameObject right = NewUI("RightColumn", panel.transform);
            RectTransform rrt = (RectTransform)right.transform;
            rrt.anchorMin = new Vector2(0f, 0f); rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0.5f, 0.5f);
            rrt.offsetMin = new Vector2(28f + 320f + 16f, 28f);
            rrt.offsetMax = new Vector2(-28f, -116f);
            PaintPanel(right.AddComponent<Image>(), RowTex(), VanillaSkin.RowBg);
            _titleRight = MakeSectionLabel(right.transform, L.T("请先选择左侧分型"));
            _unitContent = MakeScrollArea(right.transform, 44f);
        }

        // ------------------------------------------------------------- 列表填充
        private static void RebuildArchetypes()
        {
            if (_archContent == null) return;
            ClearChildren(_archContent);

            ChainProbe.Archetype[] all = null;
            try { all = Archetypes.All; } catch (Exception e) { Main.LogError(e.Message); }
            if (all == null || all.Length == 0)
            {
                MakeLabel(_archContent, L.T("没有可用分型（archetypes.json 没载入？）"), 20f, VanillaSkin.TextDim,
                          TextAlignmentOptions.Left);
                return;
            }

            _archBg.Clear();
            for (int i = 0; i < all.Length; i++)
            {
                int idx = i;   // 闭包捕获
                ChainProbe.Archetype a = all[i];
                Button b = MakeButton(_archContent, a.Name, 0f, 42f,
                                      () => { _selected = idx; PaintArchSelection(); RebuildUnits(); });
                LayoutElement le = b.gameObject.AddComponent<LayoutElement>();
                le.minHeight = 42f; le.preferredHeight = 42f;
                Image bg = b.GetComponent<Image>();
                if (i == 0 && bg != null) _archBgNormal = bg.color;   // 未选中时的原色，用第一个当样本
                _archBg.Add(bg);
            }
            PaintArchSelection();
            ReapplyLayer();   // 新建的子物体停在 layer 0，UICamera 只收 layer 5
        }

        private static void RebuildUnits()
        {
            if (_unitContent == null) return;
            ClearChildren(_unitContent);

            ChainProbe.Archetype[] all = null;
            try { all = Archetypes.All; } catch { }
            if (all == null || _selected < 0 || _selected >= all.Length)
            {
                if (_titleRight != null) _titleRight.text = L.T("请先选择左侧分型");
                return;
            }

            ChainProbe.Archetype arch = all[_selected];
            if (_titleRight != null) _titleRight.text = L.F("{0} — 可招募单位", arch.Name);

            // 第一行：普通卫兵
            AddUnitRow(NormalUnitId(arch), L.T("普通卫兵"), NormalSubtitle(), _selected, null);

            // 后续行：该分型下的精英
            if (arch.Elites != null)
            {
                for (int i = 0; i < arch.Elites.Length; i++)
                {
                    ChainProbe.EliteDef ed = arch.Elites[i];
                    if (ed == null) continue;
                    string sub = EliteSubtitle(_selected, ed);
                    AddUnitRow(ed.UnitId, ed.Name, sub, _selected, ed);
                }
            }
            ReapplyLayer();   // 新建的子物体停在 layer 0，UICamera 只收 layer 5
            // ★ 必须补跑 ★ TextGuard 只在开窗后第 3 帧跑一次，而这些行是点了分型之后才建的，
            //   那时守卫早跑完了 —— 实测「招募」「改装备」就是这样漏掉的。
            //   延一帧等布局稳定（rect 还没算出来时 characterCount 恒为 0，会误报）。
            Deferred.NextFrames(1, TextGuard);
        }

        private static string NormalUnitId(ChainProbe.Archetype a)
        {
            if (a != null && !string.IsNullOrEmpty(a.UnitId)) return a.UnitId;
            try { return Main.Settings != null ? Main.Settings.UnitAssetId : null; }
            catch { return null; }
        }

        /// <summary>普通卫兵那行的副标题：名额满了要说清楚是为什么，光把按钮变灰看不出原因。</summary>
        private static string NormalSubtitle()
        {
            try
            {
                if (!CapReached()) return L.T("无限制");
                if (Main.Settings != null && Main.Settings.RecruitUsePfGate && !Main.Settings.NoPfGate())
                {
                    int next = ProfitFactorGate.NextThreshold();
                    return next > 0
                        ? L.F("名额已满 — 利润因子到 {0} 解锁下一名", next)
                        : L.F("名额已满 — 已达上限 {0} 名", ProfitFactorGate.HardCap());
                }
                return L.T("名额已满 — 受职业阶位限制");
            }
            catch { return L.T("无限制"); }
        }

        private static string EliteSubtitle(int archIndex, ChainProbe.EliteDef ed)
        {
            try
            {
                ChainProbe.EliteDef next = GearTool.NextElite(archIndex);
                if (next != null && ReferenceEquals(next, ed)) return L.T("可招募");
                if (!GearTool.EliteUnlocked(archIndex))
                    return L.T("未解锁 — 需本路线卫兵练到 T3 职业");
                return L.T("已招募 / 排队中");
            }
            catch { return ""; }
        }

        private static void AddUnitRow(string unitId, string name, string subtitle,
                                       int archIndex, ChainProbe.EliteDef elite)
        {
            GameObject row = NewUI("Row_" + (name ?? "?"), _unitContent);
            ((RectTransform)row.transform).sizeDelta = new Vector2(0f, 116f);   // 别赌默认尺寸
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = 116f; le.preferredHeight = 116f;
            PaintPanel(row.AddComponent<Image>(), RowTex(), VanillaSkin.RowBg);

            // 立绘
            GameObject port = NewUI("Portrait", row.transform);
            RectTransform port_rt = (RectTransform)port.transform;
            port_rt.anchorMin = port_rt.anchorMax = new Vector2(0f, 0.5f);
            port_rt.pivot = new Vector2(0f, 0.5f);
            port_rt.anchoredPosition = new Vector2(12f, 0f);
            port_rt.sizeDelta = new Vector2(76f, 96f);
            Image pimg = port.AddComponent<Image>();
            pimg.preserveAspect = true;
            Sprite face = null;
            try { face = UnitPortraits.Get(unitId, PortraitSize.Small); } catch { }
            if (face != null) { pimg.sprite = face; pimg.color = Color.white; }
            else { pimg.color = new Color(0.15f, 0.15f, 0.15f, 1f); }

            // 名字 + 副标题
            TextMeshProUGUI nameTxt = MakeLabel(row.transform, name ?? L.T("(未命名)"), 24f,
                                                VanillaSkin.Text, TextAlignmentOptions.Left);
            RectTransform nrt = (RectTransform)nameTxt.transform;
            nrt.anchorMin = new Vector2(0f, 0.5f); nrt.anchorMax = new Vector2(1f, 1f);
            nrt.offsetMin = new Vector2(104f, 0f); nrt.offsetMax = new Vector2(-300f, -10f);

            TextMeshProUGUI subTxt = MakeLabel(row.transform, subtitle ?? "", 18f,
                                               VanillaSkin.TextDim, TextAlignmentOptions.Left);
            RectTransform srt = (RectTransform)subTxt.transform;
            srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(1f, 0.5f);
            srt.offsetMin = new Vector2(104f, 10f); srt.offsetMax = new Vector2(-300f, 0f);

            // 两个按钮
            Button gear = MakeButton(row.transform, L.T("改装备"), 118f, 38f, () => OnEditGear(archIndex, elite));
            RectTransform grt = (RectTransform)gear.transform;
            grt.anchorMin = grt.anchorMax = new Vector2(1f, 0.5f);
            grt.pivot = new Vector2(1f, 0.5f);
            grt.anchoredPosition = new Vector2(-152f, 0f);

            Button hire = MakeButton(row.transform, L.T("招募"), 118f, 38f, () => OnRecruit(archIndex, elite));
            RectTransform hrt = (RectTransform)hire.transform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(1f, 0.5f);
            hrt.pivot = new Vector2(1f, 0.5f);
            hrt.anchoredPosition = new Vector2(-12f, 0f);

            // ★名额满了一律不能招，普通卫兵也一样★
            // 原来只有精英行判了 NextElite，普通卫兵那行按钮永远是亮的 ——
            // 点下去 SpawnOne 内部才拒绝，玩家只看到"点了没反应"。
            bool full = CapReached();
            if (elite != null)
            {
                bool ok = false;
                try
                {
                    ChainProbe.EliteDef next = GearTool.NextElite(archIndex);
                    ok = next != null && ReferenceEquals(next, elite);
                }
                catch { }
                SetInteractable(hire, ok && !full);
            }
            else
            {
                SetInteractable(hire, !full);
            }
        }

        // ------------------------------------------------------------- 交互
        private static void OnRecruit(int archIndex, ChainProbe.EliteDef elite)
        {
            try
            {
                var g = RetinueTest.SpawnOne(archIndex, elite, false, elite == null);
                Main.Log(g != null
                    ? "[招募] 成功: " + (elite != null ? elite.Name : "普通卫兵")
                    : "[招募] 未生成（数量上限或解锁条件，看日志）");

                // ★ 必须延迟刷新，同帧刷是错的 ★
                // SpawnUnit 是**延迟入册**的：新卫兵要到下一次 Tick 才进 state，
                // 所以同帧读 RetinueRegistry.Count 拿到的还是招募**前**的数字。
                // 症状是计数和按钮灰不灰都慢一拍 —— 招第 1 个显示 0、招第 2 个才显示 1
                //（实测截图确认）。
                // 先刷一次让界面立刻有反馈，再延迟两帧刷成真值。
                Refresh();
                Deferred.NextFrames(2, Refresh);
            }
            catch (Exception e) { Main.LogError("[招募] 失败: " + e.Message); }
        }

        /// <summary>
        /// 名额是否已满 —— UI 侧的判据，必须和 RetinueTest.SpawnOne 里那套**完全一致**，
        /// 否则会出现"按钮亮着但点了没反应"，那比按钮变灰更难理解。
        /// </summary>
        private static bool CapReached()
        {
            try
            {
                if (Main.Settings == null) return false;
                if (Main.Settings.NoCountCap()) return false;

                int cap;
                if (Main.Settings.RecruitUsePfGate && !Main.Settings.NoPfGate())
                    cap = ProfitFactorGate.Unlocked();
                else
                {
                    var g = Kingmaker.Game.Instance;
                    var leader = g != null && g.Player != null ? g.Player.MainCharacterEntity : null;
                    cap = Archetypes.GuardCountCap(Archetypes.PlayerTier(leader));
                }
                return RetinueRegistry.Count >= cap;
            }
            catch { return false; }
        }

        private static void OnEditGear(int archIndex, ChainProbe.EliteDef elite)
        {
            // 第二阶段才做（按约定：阶段一只做招募，用固定装备组；阶段二才是装配界面）。
            // 但按钮不能点了没反应 —— 那看起来像坏了，实测用户就是这么反馈的。
            string who = elite != null ? elite.Name : "普通卫兵";
            // 日志固定中文（诊断用），界面那份单独走本地化
            string whoShown = elite != null ? elite.Name : L.T("普通卫兵");
            if (_titleRight != null)
                _titleRight.text = L.F("「{0}」装备编辑属于第二阶段，尚未实现（当前用固定装备组：按玩家阶位发 T1/T2/T3）", whoShown);
            Main.Log("[装备] 改装备尚未实现（第二阶段）: archIndex=" + archIndex + " " + who);
        }

        // ---------------------------------------------------------- 原版渲染路径
        // 全游戏 0 个 ScreenSpaceOverlay Canvas（732 个 Canvas 实测：rm=1 有 8 个，rm=2 有 724 个，rm=0 为 0）。
        // 原版 UI 全部挂在 UICamera 上，走自研 SRP(WaaaghPipeline) 的 camera stack。
        // Overlay 是唯一落在那条链外面的东西 —— 对话开 FullscreenBlur 后合成解算变了，就把我们洗白。
        private const int   KgdSortingOrder = 32000;               // < short 上限 32767
        private const float KgdVanillaPlane = 2765.174072265625f;  // 实测原版 rm=1 根 Canvas 的值

        private static int _uiLayer = -1;
        private static int UiLayer
        {
            get
            {
                if (_uiLayer < 0)
                {
                    int l = LayerMask.NameToLayer("UI");
                    _uiLayer = (l >= 0) ? l : 5;
                }
                return _uiLayer;
            }
        }

        /// ★★★ 整个修法的成败所在 ★★★
        /// UICamera 的 cullingMask == 32（仅 layer 5 "UI"）；new GameObject() 默认 layer 0，
        /// 不改层则整窗被剔除、**完全不显示**。
        private static void SetLayerRecursive(Transform t, int layer)
        {
            if (t == null) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        private static Camera ResolveUiCamera()
        {
            try { Camera cam = Kingmaker.UI.UICamera.Instance; if (cam != null) return cam; }
            catch (Exception e) { Main.LogError("[UI] UICamera.Instance: " + e.Message); }
            // Claim() 标了 [NotNull] 但实际会返回 null（isPlaying/Prefab 不满足时直接 return Instance），
            // 别信那个特性标注。
            try { Camera cam = Kingmaker.UI.UICamera.Claim(); if (cam != null) return cam; }
            catch (Exception e) { Main.LogError("[UI] UICamera.Claim(): " + e.Message); }
            return null;
        }

        internal static void ApplyVanillaRenderPath(Canvas c)
        {
            if (c == null) return;
            Camera cam = ResolveUiCamera();
            if (cam == null)
            {
                // 兜底：主菜单 / 蓝图未加载。至少能看见（对话中可能发白）。
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = KgdSortingOrder;
                Main.Log("[UI] 未取到 UICamera，回退 ScreenSpaceOverlay");
                return;
            }

            c.renderMode = RenderMode.ScreenSpaceCamera;
            c.worldCamera = cam;

            float plane = KgdVanillaPlane; int layerId = 0; bool copied = false;
            try
            {
                Canvas[] all = UnityEngine.Object.FindObjectsOfType<Canvas>();
                for (int i = 0; i < all.Length; i++)
                {
                    Canvas g = all[i];
                    if (g == null || g == c || !g.isRootCanvas) continue;
                    if (g.renderMode != RenderMode.ScreenSpaceCamera || g.worldCamera != cam) continue;
                    plane = g.planeDistance; layerId = g.sortingLayerID; copied = true; break;
                }
            }
            catch (Exception e) { Main.LogError("[UI] 抄原版 Canvas 参数失败: " + e.Message); }

            float lo = cam.nearClipPlane + 0.01f;    // 实测 near=1915
            float hi = cam.farClipPlane  - 0.01f;    // 实测 far =3725
            if (hi < lo) hi = lo;
            c.planeDistance  = Mathf.Clamp(plane, lo, hi);
            c.sortingLayerID = layerId;
            c.sortingOrder   = KgdSortingOrder;

            SetLayerRecursive(c.transform, UiLayer);   // ★ 少这一行 = 黑窗

            Main.Log("[UI] Canvas->ScreenSpaceCamera cam=" + cam.name
                   + " layer=" + UiLayer + " camMask=" + cam.cullingMask
                   + " plane=" + c.planeDistance.ToString("0.###")
                   + " order=" + c.sortingOrder + (copied ? " (抄自原版)" : " (常量+clamp)"));
        }

        /// 切区域/读档后 UICamera 被重建，UICameraClaimer.OnDisable 会把 worldCamera 置 null，
        /// 那时窗口既不可见也点不中。挂 UiHost.LateUpdate 每帧兜。
        internal static void TickRenderPathGuard()
        {
            if (_root == null) return;
            Canvas c = _root.GetComponent<Canvas>();
            if (c == null) return;
            if (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera == null)
                ApplyVanillaRenderPath(c);
        }

        /// 动态建行之后补层：RebuildArchetypes / RebuildUnits 新建的子物体停在 layer 0。
        internal static void ReapplyLayer()
        {
            if (_root == null) return;
            Canvas c = _root.GetComponent<Canvas>();
            if (c != null && c.renderMode == RenderMode.ScreenSpaceCamera)
                SetLayerRecursive(_root.transform, UiLayer);
        }

        /// <summary>
        /// 自愈守卫：布局稳定后实测每个标签**真实渲染了几个字符**。
        /// characterCount==0 而 text 非空 = TMP 因纵向放不下把整串丢了 —— 切 Overflow 抢救，
        /// 并把出事的控件打进日志。比反推字体度量可靠。
        /// </summary>
        internal static void TextGuard()
        {
            if (_root == null) return;
            try
            {
                Canvas.ForceUpdateCanvases();
                TextMeshProUGUI[] all = _root.GetComponentsInChildren<TextMeshProUGUI>(true);
                int fixedCount = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    TextMeshProUGUI t = all[i];
                    if (t == null || !t.gameObject.activeInHierarchy) continue;
                    if (string.IsNullOrEmpty(t.text)) continue;
                    if (t.textInfo != null && t.textInfo.characterCount > 0) continue;

                    RectTransform rt = t.rectTransform;
                    Main.Log("[UI][文字被裁] '" + t.text + "' size=" + t.fontSize
                           + " rect=" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0")
                           + " overflow=" + t.overflowMode
                           + " @ " + (t.transform.parent != null ? t.transform.parent.name : "?")
                           + " -> 已切 Overflow 抢救");
                    t.overflowMode = TextOverflowModes.Overflow;
                    t.ForceMeshUpdate();
                    fixedCount++;
                }
                if (fixedCount == 0) Main.Log("[UI] TextGuard: 全部标签正常渲染");
            }
            catch (Exception e) { Main.LogError("[UI] TextGuard: " + e.Message); }
        }

        // ------------------------------------------------------------- 小工具
        internal static GameObject NewUI(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(GameObject go, float pad) { StretchPad(go, pad, pad); }

        /// <summary>
        /// 拉伸铺满父物体，横纵内边距分开给。
        /// ★ 纵向内边距是雷区：TMP 在 Ellipsis/Truncate 下，一旦
        ///   rect.height < fontSize×(ascent−descent)/pointSize，会把**整串**字符丢掉
        ///   （GenerateTextMesh 判定后走 m_characterCount=0 分支）。
        ///   给文字用时 padY 一律 0，靠 Center 对齐自然居中。
        /// </summary>
        internal static void StretchPad(GameObject go, float padX, float padY)
        {
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
        }

        internal static void StretchPadPublic(GameObject go, float padX, float padY)
        { StretchPad(go, padX, padY); }
        internal static TextMeshProUGUI MakeLabelPublic(Transform p, string s, float sz,
                                                        Color c, TextAlignmentOptions a)
        { return MakeLabel(p, s, sz, c, a); }

        private static void StretchOld(GameObject go, float pad)
        {
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
        }

        /// <summary>
        /// 铺底。
        ///
        /// ★ 不再用"从场景里摘来的 Sprite" ★
        /// 白名单按名字猜九宫格太不可靠：实测摘到的是一张**光晕图**，被拉伸后
        /// 在窗口下半部糊出一大片发亮的绿白色块，同时整个面板显得半透明。
        /// 现在全部改成 GenTex 程序生成 —— 纯色/渐变 + 明确的边框像素 + 正确的 9-slice border，
        /// 完全可控、跨版本零依赖，也不会再出这种事。
        /// （字体仍然从场景里摘，那部分实测是好的。）
        /// </summary>
        internal static void PaintPanel(Image img, Sprite sprite, Color fallback)
        {
            img.sprite = sprite;
            img.type = (sprite != null && sprite.border != Vector4.zero) ? Image.Type.Sliced : Image.Type.Simple;
            img.color = sprite != null ? Color.white : fallback;
        }

        // ---------------------------------------------------------------- 程序生成贴图

        private static readonly Dictionary<string, Sprite> _gen = new Dictionary<string, Sprite>();

        /// <summary>
        /// 生成一张「竖向渐变 + 深色细边」的九宫格贴图。
        /// 参照原版按钮：上浅下深的金色渐变，外面一圈暗色描边，方角。
        /// </summary>
        private static Sprite GenTex(string key, Color top, Color bottom, Color border, int pad = 2)
        {
            Sprite got;
            if (_gen.TryGetValue(key, out got) && got != null) return got;

            const int W = 16, H = 32;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            for (int y = 0; y < H; y++)
            {
                // 纹理坐标 y=0 在下方，渐变要上浅下深，所以用 (H-1-y)
                float t = (H - 1 - y) / (float)(H - 1);
                Color c = Color.Lerp(top, bottom, t);
                for (int x = 0; x < W; x++)
                {
                    bool edge = x < pad || x >= W - pad || y < pad || y >= H - pad;
                    tex.SetPixel(x, y, edge ? border : c);
                }
            }
            tex.Apply(false, false);

            // border 让四角不被拉伸（9-slice）。pad+1 留一点余量，避免缩放时描边被吃掉。
            var s = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f, 0,
                                  SpriteMeshType.FullRect,
                                  new Vector4(pad + 1, pad + 1, pad + 1, pad + 1));
            s.name = "Kgd_" + key;
            _gen[key] = s;
            return s;
        }

        /// <summary>窗口/分区底：近乎不透明的暗色 + 金边。半透明会让下面的场景糊进来。</summary>
        internal static Sprite PanelTex()
        {
            return GenTex("panel",
                new Color(0.075f, 0.082f, 0.075f, 0.985f),
                new Color(0.040f, 0.046f, 0.042f, 0.985f),
                new Color(0.478f, 0.392f, 0.196f, 1f), 2);
        }

        /// <summary>列表行底：比窗口底稍亮一档，拉开层次。</summary>
        private static Sprite RowTex()
        {
            return GenTex("row",
                new Color(0.125f, 0.130f, 0.118f, 0.96f),
                new Color(0.085f, 0.090f, 0.082f, 0.96f),
                new Color(0.290f, 0.250f, 0.145f, 1f), 1);
        }

        /// <summary>按钮底：照参考图 —— 上浅下深的金色渐变 + 深棕描边，方角。</summary>
        internal static Sprite ButtonTex()
        {
            return GenTex("btn",
                new Color(0.839f, 0.722f, 0.404f, 1f),
                new Color(0.541f, 0.435f, 0.208f, 1f),
                new Color(0.180f, 0.145f, 0.082f, 1f), 2);
        }

        /// <summary>按钮底（禁用态）：同形状但去色压暗，一眼能看出点不了。</summary>
        private static Sprite ButtonDimTex()
        {
            return GenTex("btnDim",
                new Color(0.340f, 0.320f, 0.280f, 1f),
                new Color(0.225f, 0.212f, 0.185f, 1f),
                new Color(0.150f, 0.140f, 0.120f, 1f), 2);
        }

        private static TextMeshProUGUI MakeLabel(Transform parent, string text, float size,
                                                 Color color, TextAlignmentOptions align)
        {
            GameObject go = NewUI("Label", parent);
            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            if (VanillaSkin.Font != null)
            {
                t.font = VanillaSkin.Font;
                if (VanillaSkin.FontMaterial != null) t.fontSharedMaterial = VanillaSkin.FontMaterial;
            }
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            return t;
        }

        internal static TextMeshProUGUI MakeSectionLabel(Transform parent, string text)
        {
            TextMeshProUGUI t = MakeLabel(parent, text, 22f, VanillaSkin.Gold, TextAlignmentOptions.Left);
            RectTransform rt = (RectTransform)t.transform;
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(14f, -40f); rt.offsetMax = new Vector2(-14f, -6f);   // 框高 30->34，22pt 装得下
            return t;
        }

        internal static Button MakeButton(Transform parent, string text, float w, float h, Action onClick)
        {
            // ① 先试原版克隆（克隆 ESC 菜单里活着的 OwlcatButton）
            Button v = VanillaWidgets.MakeVanillaButton(parent, text, w, h, onClick);
            if (v != null) return v;

            // ② 回退：程序生成
            GameObject go = NewUI("Button_" + text, parent);

            Image img = go.AddComponent<Image>();
            PaintPanel(img, ButtonTex(), VanillaSkin.Gold);
            Button b = go.AddComponent<Button>();
            b.targetGraphic = img;
            b.transition = Selectable.Transition.ColorTint;
            ColorBlock cb = b.colors;
            cb.normalColor      = Color.white;
            cb.highlightedColor = new Color(1.15f, 1.12f, 1.0f, 1f);
            cb.pressedColor     = new Color(0.75f, 0.72f, 0.62f, 1f);
            cb.disabledColor    = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            b.colors = cb;
            if (onClick != null) b.onClick.AddListener(new UnityEngine.Events.UnityAction(onClick));

            TextMeshProUGUI label = MakeLabel(go.transform, text, 19f,
                                              new Color(0.13f, 0.10f, 0.05f, 1f),
                                              TextAlignmentOptions.Center);
            label.overflowMode = TextOverflowModes.Overflow;   // 见 VanillaWidgets 里同款注释
            // ★★★ 根因修复：纵向 padding 必须是 0 ★★★
            // 原来四边各缩 6，h=34 的按钮只剩 22px，19pt 中文行高约 25~26px 放不下
            // → TMP 把整串字符清零 → 白框没字。
            StretchPad(label.gameObject, 8f, 0f);

            RectTransform rt = (RectTransform)go.transform;   // ★ 尺寸最后设，锚点已定型
            rt.sizeDelta = new Vector2(w > 0f ? w : 0f, h);
            return b;
        }

        internal static void SetInteractable(Button b, bool on)
        {
            if (b == null) return;
            if (VanillaWidgets.TrySetInteractable(b, on)) return;   // 克隆体：交给 OwlcatButton
            b.interactable = on;
            Image img = b.targetGraphic as Image;
            if (img != null) PaintPanel(img, on ? ButtonTex() : ButtonDimTex(), VanillaSkin.Gold);
            TextMeshProUGUI t2 = b.GetComponentInChildren<TextMeshProUGUI>();
            if (t2 != null) t2.color = on ? new Color(0.13f, 0.10f, 0.05f, 1f)
                                          : new Color(0.55f, 0.53f, 0.48f, 1f);
        }

        /// <summary>建一个纵向滚动区，返回 content。topInset 给区块标题让位。</summary>
        internal static Transform MakeScrollArea(Transform parent, float topInset)
        {
            GameObject scroll = NewUI("Scroll", parent);
            RectTransform srt = (RectTransform)scroll.transform;
            srt.anchorMin = Vector2.zero; srt.anchorMax = Vector2.one;
            srt.offsetMin = new Vector2(10f, 10f);
            srt.offsetMax = new Vector2(-10f, -topInset);
            ScrollRect sr = scroll.AddComponent<ScrollRect>();

            GameObject viewport = NewUI("Viewport", scroll.transform);
            Stretch(viewport, 0f);
            viewport.AddComponent<RectMask2D>();   // 不需要 Graphic，比 Mask 省一个 drawcall

            GameObject content = NewUI("Content", viewport.transform);
            RectTransform crt = (RectTransform)content.transform;
            crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.offsetMin = new Vector2(0f, 0f); crt.offsetMax = new Vector2(0f, 0f);

            VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 8f;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childControlHeight = true;   // false 时 VLG 只读 sizeDelta.y，LayoutElement.preferredHeight 全程无效
            vlg.childControlWidth = true;
            vlg.padding = new RectOffset(4, 4, 4, 4);

            ContentSizeFitter csf = content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            sr.viewport = (RectTransform)viewport.transform;
            sr.content = crt;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 32f;
            return content.transform;
        }

        private static void ClearChildren(Transform t)
        {
            if (t == null) return;
            for (int i = t.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        // ------------------------------------------------------------- 宿主
        /// <summary>只做两件事：ESC 关窗、mod 被禁用时自毁。不碰任何游戏状态。</summary>
        private sealed class UiHost : MonoBehaviour
        {
            private int _frames;

            private void Update()
            {
                if (!Main.Enabled) { RetinueUI.Close(); return; }
                if (Input.GetKeyDown(KeyCode.Escape)) RetinueUI.Close();
            }

            private void LateUpdate()
            {
                RetinueUI.TickRenderPathGuard();
                if (_frames < 3) { _frames++; if (_frames == 3) RetinueUI.TextGuard(); }
            }
        }
    }
}

// ---------------------------------------------------------------------------
// 可选（第 4 步再加）：让 ESC 只关我们的窗口、不同时弹出游戏的 ESC 菜单。
// 目标已用 ilspycmd 核实存在于当前 1.6.1.514：
//   Kingmaker.Code.UI.MVVM.VM.EscMenu.EscMenuContextVM.RequestEscMenu()  // private void
//
// [HarmonyPatch(typeof(Kingmaker.Code.UI.MVVM.VM.EscMenu.EscMenuContextVM), "RequestEscMenu")]
// internal static class KgdEscMenuPatch
// {
//     [HarmonyPrefix]
//     private static bool Prefix()
//     {
//         if (!DynastyRetinue.UI.RetinueUI.IsOpen) return true;   // 放行
//         DynastyRetinue.UI.RetinueUI.Close();
//         return false;                                        // 吃掉这次 ESC
//     }
// }
// ---------------------------------------------------------------------------
