using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Kingmaker.EntitySystem.Entities;
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

        /// <summary>
        /// 用原版控件自带的字体覆盖我们挑的那个。
        ///
        /// ★为什么这是最可靠的来源★
        ///   克隆原版按钮时手里就有它的 TextMeshProUGUI，那正是**游戏自己**
        ///   拿来渲染这个窗口中文的字体和材质 —— 不需要任何启发式。
        ///   实机证据：按钮（克隆原版，自带字体）中文正常显示，
        ///   而标签（我们 new 的，用 EnsureFont 挑出来的字体）整片方框。
        ///   同一个窗口两种表现，差别只在字体来源。
        /// </summary>
        public static void AdoptFont(TMP_FontAsset font, Material mat)
        {
            if (font == null) return;

            if (ReferenceEquals(_font, font)) return;
            _font = font;
            if (mat != null) _fontMat = mat;
            _fontTried = true;
            Main.LogVerbose("[UI] 采用原版控件的字体：" + font.name);
        }

        private static void EnsureFont()
        {
            // ★不能"试过一次就永久锁定"★
            //   原来是 if (_fontTried) return; —— 万一第一次调用发生在场景里
            //   还没几个 TMP 文本的时机，就会把一个覆盖不全的字体锁死一整局，
            //   打分再准也救不回来（候选池本身是空的）。
            //   现在只有拿到**探针全覆盖**的字体才算定案，否则下次还会再挑一次。
            if (_fontTried && _font != null) return;
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
                var scored = new Dictionary<int, int>();   // fontAsset InstanceID -> 打分

                foreach (var t in live)
                {
                    int id = t.font.GetInstanceID();
                    int score;
                    if (!scored.TryGetValue(id, out score))
                    {
                        // ★自身覆盖优先于 fallback 覆盖★
                        //   两者都能正常渲染（材质已经不钉了），但自身有字形的那个
                        //   只需要一个 draw call，靠 fallback 的每种字体要多一个子对象。
                        //   同分时优先自身 —— 权重放在千位，fallback 覆盖只做低位裁决。
                        score = OwnCoverageOf(t.font) * 1000 + CoverageOf(t.font);
                        scored[id] = score;
                    }
                    // ★字体和材质必须成对★ 拿 A 的字体配 B 的材质会渲染成方块/粉块。
                    // 材质现在只留给诊断用，不再往标签上钉（见 MakeLabel）。
                    if (score > bestScore) { bestScore = score; bestFont = t.font; bestMat = t.fontSharedMaterial; }
                    if (bestScore >= FontProbe.Length * 1000 + FontProbe.Length) break;   // 自身全覆盖，不用再找
                }

                if (bestFont != null && bestScore > 0)
                {
                    _font = bestFont; _fontMat = bestMat;
                    int own = OwnCoverageOf(bestFont), all = CoverageOf(bestFont);
                    // 含 fallback 都不全 ⇒ 不算定案，下次还会再挑（也可能被 AdoptFont 覆盖）
                    if (all < FontProbe.Length) _fontTried = false;
                    Main.LogVerbose($"[UI] 摘到字体 {bestFont.name}，自身 {own}/{FontProbe.Length}"
                           + $"　含fallback {all}/{FontProbe.Length}"
                           + (all < FontProbe.Length ? "　★覆盖不全，界面可能出现方块★"
                              : own == 0 ? "　（中文全靠 fallback —— 所以绝不能钉材质）" : "")
                           + "　" + AtlasInfo(bestFont));
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
                    Main.LogVerbose($"[UI] 场景里没摘到合适字体，退回资产 {any.name}，探针覆盖 {CoverageOf(any)}/{FontProbe.Length}");
                    return;
                }

                Main.Log("[UI] 未摘到原版 TMP 字体，退回 TMP 默认字体（能用，观感退化）");
            }
            catch (Exception e) { Main.LogError("[UI] 摘字体失败: " + e.Message); }
        }

        /// <summary>
        /// 图集页数 + 图集填充模式，用来判方框到底是不是材质锁页造成的。
        ///
        /// ★为什么这两个值是关键★
        ///   作者机和玩家机用的是**同一份 mod、同一个 ScreenFont、同样写死材质**，
        ///   却只有玩家出方框。最合理的解释是 Dynamic 图集会随用到的字动态增长：
        ///   作者会话里那些字还在第 0 页，玩家会话里已经长到第 2、3 页，
        ///   而写死的材质只指向第 0 页。
        ///   把页数和模式打出来，下次报告就能证实或推翻这个解释。
        /// </summary>
        private static string AtlasInfo(TMP_FontAsset f)
        {
            try
            {
                int n = (f.atlasTextures != null) ? f.atlasTextures.Length : -1;
                return $"图集 {n} 页 / {f.atlasPopulationMode}";
            }
            catch { return "图集信息读取失败"; }
        }

        /// <summary>
        /// 探针串里有多少个字是这个字体**自己**有的（不看 fallback）。
        ///
        /// ★为什么必须和含 fallback 的版本分开看★
        ///   两个数差得越大，说明这个字体越是"靠别人显示中文"。
        ///   自身 0 / 含 fallback 55 就是拉丁底字体挂中文 fallback 的典型 ——
        ///   历时三个版本的方块 bug 正是选中了这种字体又钉死它的材质。
        ///   现在材质不钉了，选它也能正常渲染，但日志里把两个数都打出来，
        ///   下次再出问题时一眼就能定位，不用再猜。
        /// </summary>
        private static int OwnCoverageOf(TMP_FontAsset f)
        {
            if (f == null) return 0;
            try
            {
                var set = new HashSet<uint>();
                CollectChars(f, set, new HashSet<int>(), 6);   // depth=6 ⇒ 进门即返回，不递归 fallback
                int n = 0;
                foreach (char c in FontProbe) if (set.Contains(c)) n++;
                return n;
            }
            catch { return 0; }
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
                Main.LogVerbose("[UI] 摘贴图: panel=" + Nm(_panel) + " button=" + Nm(_button) + " row=" + Nm(_row)
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
                    Main.LogVerbose("[UI/9slice] " + img.sprite.name
                             + "  border=" + img.sprite.border
                             + "  size=" + img.sprite.rect.width + "x" + img.sprite.rect.height);
                    if (++n >= max) break;
                }
                Main.LogVerbose("[UI/9slice] 共 " + n + " 个候选");
            }
            catch (Exception e) { Main.LogError(e.Message); }
        }

        /// <summary>切场景/回主菜单后旧引用可能已随场景卸载，重开窗口前重摘。</summary>
        public static void Reset()
        {
            _fontTried = false; _font = null; _fontMat = null;
            _spriteTried = false; _panel = null; _button = null; _row = null;
        }

        // ★手动换字体的开关已移除（1.0.36）★
        //   它建立在"选错了字体"这个错误假设上。实测证明四个中文可用字体
        //   （ScreenFont / PaperFont / HeaderFont / HeaderFont_Digital）
        //   自身汉字都是 0，汉字一律由同一套 fallback 渲染 ——
        //   换哪个都走同一条渲染路径，这个开关**不可能**改变任何结果，
        //   留着只会误导玩家去试一个注定无效的操作。
        //   真正的病因是 fallback 子网格的 layer（见 LayerFixer）。

        /// <summary>
        /// 对一个**真的渲染出来了**的标签做验尸，把方块问题一次问死。
        ///
        /// ★为什么必须读实机而不是继续推理★
        ///   方块 bug 已经改错三个方向了（选字体 → 多图集 → 材质）。每一版都是
        ///   "照着截图推一个假设，改完发出去，再看下一张截图"。根本原因是我手上
        ///   一直没有**渲染时刻**的事实，只有渲染结果的照片。
        ///
        /// ★这三行就是全部答案★
        ///   ① 材质的 _MainTex 和字体自己的图集是不是同一张 —— 不是就必然出方块
        ///   ② 每个字**实际由哪个 fontAsset 解析** —— 和主字体不同就说明走了 fallback
        ///   ③ materialReferenceIndex —— 非 0 说明 TMP 建了子对象，那条路是通的
        ///   有了这三行，"是材质不配套"还是"字形压根没解析出来"就不用猜了。
        /// </summary>
        public static void DiagnoseLabel(TextMeshProUGUI t, string tag)
        {
            if (t == null) return;
            try
            {
                t.ForceMeshUpdate();   // 必须先跑一次排版，textInfo 才是有效的

                var f = t.font;
                var mat = t.fontSharedMaterial;
                string atlas = "?", main = "?";
                try { if (f != null && f.atlasTexture != null) atlas = f.atlasTexture.name; } catch { }
                try { if (mat != null && mat.mainTexture != null) main = mat.mainTexture.name; } catch { }
                bool paired = atlas == main;

                var info = t.textInfo;
                var sb = new System.Text.StringBuilder();
                int n = 0;
                if (info != null && info.characterInfo != null)
                {
                    for (int i = 0; i < info.characterCount && n < 6; i++)
                    {
                        var ci = info.characterInfo[i];
                        if (ci.character == ' ') continue;
                        string fa = "?";
                        try { fa = ci.fontAsset != null ? ci.fontAsset.name : "null"; } catch { }
                        sb.Append($"'{ci.character}'→{fa}#{ci.materialReferenceIndex} ");
                        n++;
                    }
                }

                Main.LogVerbose($"[UI/验尸:{tag}] 字体={(f != null ? f.name : "null")} 图集={atlas}"
                       + $"　材质={(mat != null ? mat.name : "null")} 贴图={main}"
                       + (paired ? "　[配套]" : "　★不配套 —— 方块就是这么来的★")
                       + "　解析: " + sb);
            }
            catch (Exception e) { Main.LogError("[UI/验尸] " + e.Message); }
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
        // 页签：0 = 当前卫队，1 = 招募卫队
        private static int _tab;
        private static GameObject _pageRoster, _pageRecruit, _pageLooks;
        private static Transform _looksContent;
        /// <summary>外观页的"画笔"：点格子会把它刷进去。空串 = 跟随装备。</summary>
        private static string _lookBrush = LookCatalog.FollowGear;
        private static Transform _rosterContent;
        private static readonly System.Collections.Generic.List<Button> _tabBtns = new System.Collections.Generic.List<Button>();
        private static Color _tabNormal, _tabActive;
        /// <summary>正在等二次确认的那名卫兵的 UniqueId。同一时刻只可能有一个。</summary>
        private static string _confirmUid;
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
        /// <summary>
        /// ★"对话结束就自动关窗"这个想法行不通，别再加回来★
        ///
        ///   动机是合理的：合作里对话答案是被复制的指令，房主点一下**两台**都会
        ///   开窗，而关窗是纯本地操作，于是客机那边得手动关。
        ///
        ///   但实机日志证明触发条件根本不成立 —— 招募那条对话选项被选中后
        ///   **对话本身就结束了**，窗口建好的下一帧游戏早已不在 Dialog 模式，
        ///   守卫立刻判定"对话结束"并关窗。结果是**所有人**（房主和客机都一样）
        ///   点了选项什么都不出来：
        ///       [对话注入] 玩家选择了「（护卫队）…」
        ///       [UI] Canvas->ScreenSpaceCamera …      ← 建起来了
        ///       [UI] 对话已结束，自动关闭招募窗口。     ← 同一秒被关
        ///
        ///   加个"必须先看到过对话模式"的前提能挡住崩坏，但那等于让这段代码
        ///   **永不触发** —— 留着比删掉更糟，因为下次有人会以为它在工作。
        ///
        ///   要真做这件事，得挂在**对话状态机的结束事件**上，而不是轮询 CurrentMode；
        ///   而且要先确认那个事件在客机上也会触发。在验证之前不要再实现。
        /// </summary>
        // ★不登记到 DialogCloseWatch★ 招募是"选中即关对话"，
        //   StopDialog 会立刻触发对话结束事件；登记了就会把刚打开的窗口当场关掉
        //   —— 那正是 1.0.48~1.0.52 的故障。船坞是 KeepDialog=true，不受影响，仍然登记。
        public static void OpenFromDialog() { Open(); }

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

        public static void Refresh() { RefreshProfitFactor(); RebuildArchetypes(); RebuildUnits(); if (_tab == 0) RebuildRoster(); }

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

            // ---- 页签 ----
            // ★为什么要分页★ 原来一个窗口既是招募界面又是卫队一览，两件事的操作对象不同
            //   （招募看的是"分型/精英"，管理看的是"具体某个人"），混在一起谁都不好找。
            GameObject tabs = NewUI("Tabs", panel.transform);
            RectTransform tbrt = (RectTransform)tabs.transform;
            tbrt.anchorMin = new Vector2(0f, 1f); tbrt.anchorMax = new Vector2(1f, 1f);
            tbrt.pivot = new Vector2(0.5f, 1f);
            tbrt.offsetMin = new Vector2(28f, -152f); tbrt.offsetMax = new Vector2(-28f, -112f);
            HorizontalLayoutGroup tlg = tabs.AddComponent<HorizontalLayoutGroup>();
            tlg.spacing = 8f; tlg.childForceExpandWidth = false; tlg.childForceExpandHeight = true;
            tlg.childControlWidth = false; tlg.childControlHeight = true;

            _tabBtns.Clear();
            _tabBtns.Add(MakeButton(tabs.transform, L.T("当前卫队"), 200f, 40f, () => SwitchTab(0)));
            _tabBtns.Add(MakeButton(tabs.transform, L.T("招募卫队"), 200f, 40f, () => SwitchTab(1)));
            _tabBtns.Add(MakeButton(tabs.transform, L.T("外观"), 200f, 40f, () => SwitchTab(2)));
            // ★两个颜色都必须先有值★ 原版按钮克隆出来时 Image 可能挂在**子物体**上，
            //   GetComponent 取不到 —— 那样 _tabActive 会保持 default(Color) = 全透明，
            //   现象是"选中的页签整个消失"。所以先给默认值，取到了再覆盖。
            _tabNormal = Color.white;
            _tabActive = VanillaSkin.Gold;
            for (int i = 0; i < _tabBtns.Count; i++)
            {
                Image img = _tabBtns[i] != null ? _tabBtns[i].GetComponentInChildren<Image>() : null;
                if (img != null && i == 0) _tabNormal = img.color;
            }

            // ---- 两页容器 ----
            // 两页占同一块区域，靠 SetActive 切换。页签占掉 40px，所以顶边从 -116 挪到 -160。
            _pageRoster  = NewUI("PageRoster",  panel.transform);
            _pageRecruit = NewUI("PageRecruit", panel.transform);
            _pageLooks   = NewUI("PageLooks",   panel.transform);
            foreach (var pg in new[] { _pageRoster, _pageRecruit, _pageLooks })
            {
                RectTransform r = (RectTransform)pg.transform;
                r.anchorMin = new Vector2(0f, 0f); r.anchorMax = new Vector2(1f, 1f);
                r.pivot = new Vector2(0.5f, 0.5f);
                r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
            }
            BuildRosterPage(_pageRoster.transform);
            BuildLooksPage(_pageLooks.transform);

            // 左列：分型
            GameObject left = NewUI("LeftColumn", _pageRecruit.transform);
            RectTransform lrt = (RectTransform)left.transform;
            lrt.anchorMin = new Vector2(0f, 0f); lrt.anchorMax = new Vector2(0f, 1f);
            lrt.pivot = new Vector2(0f, 0.5f);
            lrt.offsetMin = new Vector2(28f, 28f);
            lrt.offsetMax = new Vector2(28f + 320f, -160f);
            PaintPanel(left.AddComponent<Image>(), RowTex(), VanillaSkin.RowBg);
            MakeSectionLabel(left.transform, L.T("分型"));
            _archContent = MakeScrollArea(left.transform, 44f);

            // 右列：该分型下的单位
            GameObject right = NewUI("RightColumn", _pageRecruit.transform);
            RectTransform rrt = (RectTransform)right.transform;
            rrt.anchorMin = new Vector2(0f, 0f); rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0.5f, 0.5f);
            rrt.offsetMin = new Vector2(28f + 320f + 16f, 28f);
            rrt.offsetMax = new Vector2(-28f, -160f);
            PaintPanel(right.AddComponent<Image>(), RowTex(), VanillaSkin.RowBg);
            _titleRight = MakeSectionLabel(right.transform, L.T("请先选择左侧分型"));
            _unitContent = MakeScrollArea(right.transform, 44f);
        
            SwitchTab(_tab);
        }

        // ------------------------------------------------------------- 当前卫队页

        private static void BuildRosterPage(Transform parent)
        {
            GameObject box = NewUI("RosterBox", parent);
            RectTransform brt = (RectTransform)box.transform;
            brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(1f, 1f);
            brt.pivot = new Vector2(0.5f, 0.5f);
            brt.offsetMin = new Vector2(28f, 28f + 52f);   // 底部给操作条留 52
            brt.offsetMax = new Vector2(-28f, -160f);
            PaintPanel(box.AddComponent<Image>(), RowTex(), VanillaSkin.RowBg);
            MakeSectionLabel(box.transform, L.T("在册卫兵"));
            _rosterContent = MakeScrollArea(box.transform, 44f);

            GameObject bar = NewUI("RosterBar", parent);
            RectTransform bar_rt = (RectTransform)bar.transform;
            bar_rt.anchorMin = new Vector2(0f, 0f); bar_rt.anchorMax = new Vector2(1f, 0f);
            bar_rt.pivot = new Vector2(0.5f, 0f);
            bar_rt.offsetMin = new Vector2(28f, 28f); bar_rt.offsetMax = new Vector2(-28f, 28f + 44f);

            Button all = MakeButton(bar.transform, L.T("遣散全部"), 160f, 40f, OnDismissAll);
            RectTransform art = (RectTransform)all.transform;
            art.anchorMin = art.anchorMax = new Vector2(1f, 0.5f);
            art.pivot = new Vector2(1f, 0.5f);
            art.anchoredPosition = new Vector2(-4f, 0f);
        }

        /// <summary>切页。两页共用同一块区域，靠 SetActive 换。</summary>
        private static void SwitchTab(int tab)
        {
            _tab = tab;
            // 换页就取消待确认的遣散 —— 否则回来时随手一点就把人删了
            _confirmUid = null;
            if (_pageRoster  != null) _pageRoster.SetActive(tab == 0);
            if (_pageRecruit != null) _pageRecruit.SetActive(tab == 1);
            if (_pageLooks   != null) _pageLooks.SetActive(tab == 2);
            for (int i = 0; i < _tabBtns.Count; i++)
            {
                Image img = _tabBtns[i] != null ? _tabBtns[i].GetComponentInChildren<Image>() : null;
                if (img != null) img.color = (i == tab) ? _tabActive : _tabNormal;
            }
            if (tab == 0) RebuildRoster();
            else if (tab == 2) RebuildLooks();
            MarkLayerDirty();
        }

        /// <summary>在册卫兵列表。每人一行，右侧「遣散」要点两次。</summary>
        private static void RebuildRoster()
        {
            if (_rosterContent == null) return;
            ClearChildren(_rosterContent);

            System.Collections.Generic.List<BaseUnitEntity> list = null;
            try { list = RetinueRegistry.All(); } catch (Exception e) { Main.LogError(e.Message); }
            if (list == null || list.Count == 0)
            {
                MakeLabel(_rosterContent, L.T("还没有卫兵。去「招募卫队」页招一个。"), 20f,
                          VanillaSkin.TextDim, TextAlignmentOptions.Left);
                return;
            }

            for (int i = 0; i < list.Count; i++) AddRosterRow(list[i]);
            ReapplyLayer();
            MarkLayerDirty();
        }

        /// <summary>名册行高。★别再压低★ 上半放 22 号名字、下半放 17 号副标题，
        /// 高度不够时 TMP 会把整串清空（见 AddRosterRow 里的注释）。</summary>
        private const float RowH = 96f;

        private static void AddRosterRow(BaseUnitEntity g)
        {
            if (g == null) return;
            string uid = g.UniqueId;

            GameObject row = NewUI("Guard", _rosterContent);
            ((RectTransform)row.transform).sizeDelta = new Vector2(0f, RowH);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = RowH; le.preferredHeight = RowH;
            PaintPanel(row.AddComponent<Image>(), RowTex(), VanillaSkin.RowBg);

            GameObject port = NewUI("Portrait", row.transform);
            RectTransform port_rt = (RectTransform)port.transform;
            port_rt.anchorMin = port_rt.anchorMax = new Vector2(0f, 0.5f);
            port_rt.pivot = new Vector2(0f, 0.5f);
            port_rt.anchoredPosition = new Vector2(12f, 0f);
            port_rt.sizeDelta = new Vector2(58f, 72f);
            Image pimg = port.AddComponent<Image>();
            pimg.preserveAspect = true;
            Sprite face = null;
            try
            {
                string bpId = (g.Blueprint != null) ? g.Blueprint.AssetGuid.ToString() : null;
                face = UnitPortraits.Get(bpId, PortraitSize.Small);
            }
            catch { }
            if (face != null) { pimg.sprite = face; pimg.color = Color.white; }
            else { pimg.color = new Color(0.15f, 0.15f, 0.15f, 1f); }

            TextMeshProUGUI nameTxt = MakeLabel(row.transform, g.CharacterName ?? L.T("(未命名)"), 22f,
                                                VanillaSkin.Text, TextAlignmentOptions.Left);
            // ★必须设成 Overflow★ 默认的 Ellipsis/Truncate 在 rect 高度不够时
            //   **把整串字符清空**而不是截断 —— 现象是"名字整个不见了"，
            //   而副标题字号小一号却正常显示，看起来像是数据丢了，其实是排版。
            //   本 mod 的利润因子条踩过同一个坑（16px 配 17 号字，一片空白）。
            nameTxt.enableWordWrapping = false;
            nameTxt.overflowMode = TextOverflowModes.Overflow;
            RectTransform nrt = (RectTransform)nameTxt.transform;
            nrt.anchorMin = new Vector2(0f, 0.46f); nrt.anchorMax = new Vector2(1f, 1f);
            nrt.offsetMin = new Vector2(84f, 0f); nrt.offsetMax = new Vector2(-190f, -6f);

            TextMeshProUGUI subTxt = MakeLabel(row.transform, RosterSubtitle(g), 17f,
                                               VanillaSkin.TextDim, TextAlignmentOptions.Left);
            subTxt.enableWordWrapping = false;
            subTxt.overflowMode = TextOverflowModes.Overflow;
            RectTransform srt = (RectTransform)subTxt.transform;
            srt.anchorMin = new Vector2(0f, 0f); srt.anchorMax = new Vector2(1f, 0.46f);
            srt.offsetMin = new Vector2(84f, 6f); srt.offsetMax = new Vector2(-190f, 0f);

            // ★遣散要点两次★ 这一步不可撤销（卫兵连同身上的装备一起没），
            //   而按钮就排在每一行的同一个位置 —— 手滑的代价太大。
            //   第一次点变成「确认遣散?」，再点一次才执行；点别人或切页就取消。
            bool armed = string.Equals(_confirmUid, uid, StringComparison.Ordinal);
            Button del = MakeButton(row.transform,
                                    armed ? L.T("确认遣散?") : L.T("遣散"), 150f, 36f,
                                    delegate { OnDismissOne(uid); });
            RectTransform drt = (RectTransform)del.transform;
            drt.anchorMin = drt.anchorMax = new Vector2(1f, 0.5f);
            drt.pivot = new Vector2(1f, 0.5f);
            drt.anchoredPosition = new Vector2(-12f, 0f);
            if (armed)
            {
                Image dimg = del.GetComponent<Image>();
                if (dimg != null) dimg.color = new Color(0.62f, 0.18f, 0.18f, 1f);
            }
        }

        private static string RosterSubtitle(BaseUnitEntity g)
        {
            try
            {
                int ai = RetinueRegistry.ArchetypeOf(g);
                ChainProbe.Archetype a = (ai >= 0) ? Archetypes.Get(ai) : null;
                string arch = (a != null) ? a.Name : L.T("未知分型");
                int lv = (g.Progression != null) ? g.Progression.CharacterLevel : 0;
                LookDef look = LookAssign.LookFor(g);
                string ln = (look != null) ? look.Display() : L.T("跟随装备");
                return L.F("{0}　{1} 级　外观：{2}", arch, lv, ln);
            }
            catch { return ""; }
        }

        private static void OnDismissOne(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            if (!string.Equals(_confirmUid, uid, StringComparison.Ordinal))
            {
                _confirmUid = uid;      // 第一次只是上膛
                RebuildRoster();
                return;
            }
            _confirmUid = null;
            // ★必须走指令通道★ 直接调 RemoveOne 只在本机生效，联机时双方人数不一致 = 失步。
            CoopCommand.Send("dismiss", uid);
            // ★刷全窗口而不只是名册★ 遣散会改名额，而名额同时影响
            //   顶部那条利润因子/名额，以及招募页按钮的可点状态（CapReached）。
            //   只重建名册的话，人少了但顶部还写着旧数字、招募按钮还灰着。
            Deferred.NextFrames(2, delegate { if (IsOpen) Refresh(); });
        }

        private static void OnDismissAll()
        {
            CoopCommand.Send("dismissall");
            Deferred.NextFrames(2, delegate { if (IsOpen) Refresh(); });
        }

        // ------------------------------------------------------------- 外观页

        private static void BuildLooksPage(Transform parent)
        {
            GameObject box = NewUI("LooksBox", parent);
            RectTransform brt = (RectTransform)box.transform;
            brt.anchorMin = new Vector2(0f, 0f); brt.anchorMax = new Vector2(1f, 1f);
            brt.pivot = new Vector2(0.5f, 0.5f);
            brt.offsetMin = new Vector2(28f, 28f);
            brt.offsetMax = new Vector2(-28f, -160f);
            PaintPanel(box.AddComponent<Image>(), RowTex(), VanillaSkin.RowBg);
            MakeSectionLabel(box.transform, L.T("外观"));
            _looksContent = MakeScrollArea(box.transform, 10f);
        }

        /// <summary>
        /// 外观分配矩阵。行 = 分型，列 = T1/T2/T3/精英。
        ///
        /// ★为什么是画笔而不是每格循环切换★
        ///   20 个格子。循环切换要把某一列全设成同一个风格得点很多次，而且风格越多越难点中。
        ///   画笔的点击次数和风格数量无关：选一次画笔，之后点哪格刷哪格，
        ///   点行头刷整行、点列头刷整列。
        ///
        /// ★三列不是并存的三种卫兵★
        ///   阶位来自 Archetypes.PlayerTier（主角等级），是全局进度 ——
        ///   读作"随战役推进，这个分型依次变成什么样"。第四列是精英，不随阶位变。
        /// </summary>
        /// <summary>行头列宽。要容得下最长的分型名（「连射 Suppress」在 170 下会换行）。</summary>
        private const float HeadW = 240f;
        /// <summary>矩阵格子宽。要容得下最长的风格名。</summary>
        private const float ColW = 180f;

        private static void RebuildLooks()
        {
            if (_looksContent == null) return;
            ClearChildren(_looksContent);

            var tip = MakeLabel(_looksContent, L.T("影响所有卫兵（含之后招募的）。外观是本地设置，联机不同步 —— 各人可以设自己喜欢的。"),
                                17f, VanillaSkin.TextDim, TextAlignmentOptions.Left);
            tip.enableWordWrapping = false; tip.overflowMode = TextOverflowModes.Overflow;

            LookDef[] looks = LookCatalog.All;

            // ---- 画笔 ----
            Transform brushRow = MakeRow(_looksContent, 44f);
            MakeRowLabel(brushRow, L.T("风格"), 90f);
            AddBrushButton(brushRow, LookCatalog.FollowGear, L.T("跟随装备"));
            for (int i = 0; i < looks.Length; i++) AddBrushButton(brushRow, looks[i].Id, looks[i].Display());
            Button applyAll = MakeButton(brushRow, L.T("全部设为"), 150f, 38f,
                delegate { LookAssign.SetAll(_lookBrush); AfterLookChange(); });
            AddWidth(applyAll.gameObject, 150f);

            // 改了 looks.json 之后重读清单。没有它就只能重启游戏 —— 而调配方是
            // "改一件看一眼"的循环，重启的代价高到会让这个配置形同虚设。
            Button reload = MakeButton(brushRow, L.T("重载风格"), 150f, 38f,
                delegate { LookCatalog.Invalidate(); AfterLookChange(); });
            AddWidth(reload.gameObject, 150f);

            if (looks.Length == 0)
                MakeLabel(_looksContent, L.T("looks.json 里没有可用的风格，只能「跟随装备」。"),
                          17f, VanillaSkin.Gold, TextAlignmentOptions.Left);

            // ---- 列头 ----
            string[] cols = { "T1", "T2", "T3", L.T("精英") };
            Transform head = MakeRow(_looksContent, 42f);
            // ★占位不能用 MakeLabel★ 它克隆的是原版控件，那些控件**自带 LayoutElement**；
            //   再 AddComponent 一个会有两个组件同时争这一格的宽度，实测结果是占位失效、
            //   整行列头左移一格，和下面的数据行对不上。空物体只挂一个 LayoutElement 最干净。
            GameObject spacer = NewUI("Spacer", head);
            LayoutElement sle = spacer.AddComponent<LayoutElement>();
            sle.minWidth = HeadW; sle.preferredWidth = HeadW;
            for (int c = 0; c < LookAssign.Cols; c++)
            {
                int cc = c;
                Button b = MakeButton(head, cols[c], ColW, 36f,
                    delegate { LookAssign.SetCol(cc, _lookBrush); AfterLookChange(); });
                AddWidth(b.gameObject, ColW);
            }

            // ---- 矩阵 ----
            ChainProbe.Archetype[] all = null;
            try { all = Archetypes.All; } catch (Exception e) { Main.LogError(e.Message); }
            for (int i = 0; all != null && i < all.Length; i++)
            {
                int ii = i;
                Transform row = MakeRow(_looksContent, 42f);
                Button rb = MakeButton(row, all[i].Name ?? "?", HeadW, 36f,
                    delegate { LookAssign.SetRow(ii, _lookBrush); AfterLookChange(); });
                AddWidth(rb.gameObject, HeadW);

                for (int c = 0; c < LookAssign.Cols; c++)
                {
                    int cc = c;
                    string cur = LookAssign.Get(i, c);
                    LookDef ld = LookCatalog.Get(cur);
                    string txt = (ld != null) ? ld.Display() : L.T("跟随装备");
                    Button cb = MakeButton(row, txt, ColW, 36f,
                        delegate { LookAssign.Set(ii, cc, _lookBrush); AfterLookChange(); });
                    AddWidth(cb.gameObject, ColW);
                    // 和画笔一致的格子高亮 —— 一眼看出这次要刷哪些
                    if (string.Equals(cur ?? "", _lookBrush ?? "", StringComparison.OrdinalIgnoreCase))
                    {
                        Image img = cb.GetComponentInChildren<Image>();
                        if (img != null) img.color = VanillaSkin.Gold;
                    }
                }
            }

            // ---- 是否显示所穿装备 ----
            Transform gearRow = MakeRow(_looksContent, 44f);
            bool show = !Main.Settings.HideGearLook;
            Button tg = MakeButton(gearRow,
                show ? L.T("显示所穿装备：开") : L.T("显示所穿装备：关"), 240f, 38f,
                delegate { Main.Settings.HideGearLook = !Main.Settings.HideGearLook; AfterLookChange(); });
            AddWidth(tg.gameObject, 260f);
            MakeRowLabel(gearRow,
                L.T("开：模型随身上装备变化。关：只显示选定风格，手持武器不受影响；对「借模型」风格无效。"), 0f);

            ReapplyLayer();
            MarkLayerDirty();
        }

        private static void AddBrushButton(Transform row, string id, string label)
        {
            string cap = id;
            Button b = MakeButton(row, label, 150f, 38f, delegate { _lookBrush = cap; RebuildLooks(); });
            AddWidth(b.gameObject, 150f);
            if (string.Equals(_lookBrush ?? "", cap ?? "", StringComparison.OrdinalIgnoreCase))
            {
                Image img = b.GetComponentInChildren<Image>();
                if (img != null) img.color = VanillaSkin.Gold;
            }
        }

        /// <summary>
        /// 给控件定宽。★先找已有的 LayoutElement★ —— 克隆出来的原版控件自带一个，
        /// 再 AddComponent 会变成两个同时生效，宽度以哪个为准是不确定的。
        /// </summary>
        private static void AddWidth(GameObject go, float w)
        {
            if (go == null) return;
            LayoutElement le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minWidth = w; le.preferredWidth = w;
            le.flexibleWidth = 0f;
        }

        /// <summary>一整行，用横向布局排子控件。</summary>
        private static Transform MakeRow(Transform parent, float h)
        {
            GameObject row = NewUI("Row", parent);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = h; le.preferredHeight = h;
            HorizontalLayoutGroup g = row.AddComponent<HorizontalLayoutGroup>();
            g.spacing = 6f;
            g.childForceExpandWidth = false; g.childForceExpandHeight = true;
            // ★childControlWidth 必须是 true★ 为 false 时布局组**完全忽略 LayoutElement 的宽度**，
            //   改用子物体自己的 sizeDelta 排列。而克隆出来的原版按钮带的是它自己那套尺寸
            //   （实测「近战 Melee」按 240 要，出来是 360），标签更是压根没设过宽度 ——
            //   于是「风格」两个字直接压在第一个按钮上面，列头和数据行也对不齐。
            //   我加的那些 AddWidth 在 false 的情况下一个都不生效。
            g.childControlWidth = true; g.childControlHeight = true;
            return row.transform;
        }

        private static TextMeshProUGUI MakeRowLabel(Transform row, string text, float w)
        {
            TextMeshProUGUI t = MakeLabel(row, text, 17f, VanillaSkin.TextDim, TextAlignmentOptions.Left);
            if (w > 0f) AddWidth(t.gameObject, w);
            else
            {
                LayoutElement le = t.gameObject.GetComponent<LayoutElement>();
                if (le == null) le = t.gameObject.AddComponent<LayoutElement>();
                le.flexibleWidth = 1f;
            }
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            return t;
        }

        /// <summary>改完分配表：重画这一页 + 就地重建卫兵视图，立刻看得到。</summary>
        private static void AfterLookChange()
        {
            RebuildLooks();
            try { DollLookPatch.RebuildAllGuardViews(); }
            catch (Exception e) { Main.LogError("[外观] 刷新失败: " + e.Message); }
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
            ReapplyLayer();   // 立即补：我们自己 new 的子物体
            MarkLayerDirty();  // 延迟补：TMP 的 fallback 子网格要等 Canvas 重建才出生
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
            ReapplyLayer();   // 立即补：我们自己 new 的子物体
            MarkLayerDirty();  // 延迟补：TMP 的 fallback 子网格要等 Canvas 重建才出生
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
                // ★不再直接生成，改成发一条指令★
                //
                //   官方合作是 lockstep：两台机器各跑一遍同样的模拟，网上只传指令。
                //   在这里直接 SpawnOne 等于只有本机改了状态，帧末对哈希必然不一致 ——
                //   实测确认会立刻弹不同步。而且代价不止这一次：新实体的 UniqueId 来自
                //   Uuid.Instance（StatefulRandom，随机状态属于同步状态），
                //   单边生成会把本机随机流永久推快一格，之后每次原版生成的 id 都错位。
                //
                //   走 CoopCommand 之后单机和联机是**同一条路径**：
                //   单机时指令进队列后立刻在本机执行，联机时两台一起执行。
                //   好处是这条路径只有一套代码，不会出现"单机测过、联机没测过"的分支。
                //
                //   ★参数必须是能在对方机器上重建的东西★
                //   精英是个对象，不能传；传它在该分型 Elites 数组里的**下标**。
                //   两边的 archetypes.json 来自同一个 mod 版本，下标必然对应同一条目
                //   —— 而"版本是否一致"由游戏自带的 mod 握手负责报警。
                int eliteIdx = -1;
                if (elite != null)
                {
                    var arch = Archetypes.Get(archIndex);
                    var es = (arch != null) ? arch.Elites : null;
                    if (es != null)
                        for (int i = 0; i < es.Length; i++)
                            if (ReferenceEquals(es[i], elite)) { eliteIdx = i; break; }
                    if (eliteIdx < 0)
                    {
                        Main.LogError("[招募] 找不到精英下标，已取消（不能拿对象当参数发出去）。");
                        return;
                    }
                }

                // ★名额判定要在**发起方**做完，把结论随指令带走★
                //   SpawnOne 内部的 skipCap=false 分支会去读 Main.Settings.NoCountCap()——
                //   那是**本机设置**。两个玩家的解锁开关不同的话，一台生成、一台不生成，
                //   立刻分叉。所以这里由发起方解析成一个布尔值，执行侧只认这个值。
                bool skipCap = false;
                try { skipCap = Main.Settings != null && Main.Settings.NoCountCap(); } catch { }

                // 参数尾部挂上**发起方的设置快照** —— 见 CoopSettings 类注释。
                //   实测第一个真实分叉就是这里：两台收到同一条指令、生成同一个 uid，
                //   但一台 AlignExperience 开着（卫兵 31 级）、一台关着（1 级）。
                var args = new System.Collections.Generic.List<string>
                {
                    archIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    eliteIdx.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    skipCap ? "1" : "0",
                };
                args.AddRange(CoopSettings.Capture());
                CoopCommand.Send("recruit", args.ToArray());
            }
            catch (Exception e) { Main.LogError("[招募] 失败: " + e.Message); }
        }

        /// <summary>
        /// 指令送达后真正执行招募 —— **两台机器都会走到这里**。
        /// 必须完全同步、且只认参数，不许读本机设置（理由见 CoopCommand 类注释）。
        /// </summary>
        internal static void ExecuteRecruit(int archIndex, int eliteIdx, bool skipCap, string[] rawArgs)
        {
            // ★try/finally 不可省★ 中途抛异常而没还原的话，
            //   玩家的设置就被联机对端永久改掉了 —— 那比不同步还糟。
            var saved = CoopSettings.Apply(rawArgs, 3);
            try { ExecuteRecruitCore(archIndex, eliteIdx, skipCap); }
            finally { CoopSettings.Restore(saved); }
        }

        private static void ExecuteRecruitCore(int archIndex, int eliteIdx, bool skipCap)
        {
            ChainProbe.EliteDef elite = null;
            if (eliteIdx >= 0)
            {
                var arch = Archetypes.Get(archIndex);
                var es = (arch != null) ? arch.Elites : null;
                if (es == null || eliteIdx >= es.Length)
                {
                    Main.LogError("[招募] 精英下标 " + eliteIdx + " 越界 —— 两边的 archetypes.json 可能不是同一版。");
                    return;
                }
                elite = es[eliteIdx];
            }

            var g = RetinueTest.SpawnOne(archIndex, elite, skipCap, elite == null);
            Main.Log(g != null
                ? "[招募] 成功: " + (elite != null ? elite.Name : "普通卫兵")
                : "[招募] 未生成（数量上限或解锁条件，看日志）");

            // ★ 必须延迟刷新，同帧刷是错的 ★
            // SpawnUnit 是**延迟入册**的：新卫兵要到下一次 Tick 才进 state，
            // 所以同帧读 RetinueRegistry.Count 拿到的还是招募**前**的数字。
            // 先刷一次让界面立刻有反馈，再延迟两帧刷成真值。
            if (IsOpen) { Refresh(); Deferred.NextFrames(2, Refresh); }
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

            Main.LogVerbose("[UI] Canvas->ScreenSpaceCamera cam=" + cam.name
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
        /// <summary>
        /// 还需要连补几帧的计数。
        ///
        /// ★为什么"补一次"不够★
        ///   ReapplyLayer 原来都是建完行**同步**调的，那一刻 TMP 还没生成网格，
        ///   fallback 子网格（TMP_SubMeshUI）尚不存在 —— 补了个寂寞。
        ///   等 Canvas 在本帧末尾重建时它们才出生，停在 layer 0，UICamera 收不到。
        ///   所以要在**之后的**几帧再补。3 帧是给 Canvas 重建留的余量，
        ///   补完就归零，不进常驻每帧路径。
        /// </summary>
        private static int _layerDirty;

        /// <summary>标记"这次改动之后可能有新的 TMP 子网格"，由 UiHost 连补几帧。</summary>
        internal static void MarkLayerDirty() { _layerDirty = 3; }

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
                if (fixedCount == 0) Main.LogVerbose("[UI] TextGuard: 全部标签正常渲染");

                // ★★ 关键：补层必须放在 ForceUpdateCanvases **之后** ★★
                //
                //   实机日志证明，所有"能显示中文"的字体自身中文字形都是 0
                //   （HeaderFont / ScreenFont / PaperFont 一律 自身 0/55、含fallback 55/55），
                //   也就是说界面上每一个汉字都得靠 **TMP fallback** 渲染。
                //   而 TMP 的 fallback 不是画在主 mesh 上的 —— 它会**运行时新建
                //   TMP_SubMeshUI 子物体**，每个 fallback 字体一个，各自带自己的材质。
                //
                //   这些子物体是在**首次生成网格时**才出现的，也就是上面那句
                //   Canvas.ForceUpdateCanvases() 触发的。而我们的窗口抄了原版渲染路径
                //   （ScreenSpaceCamera + UICamera，cullingMask 只含 layer 5），
                //   SetLayerRecursive 却是在 Open() 末尾**一次性**刷的 ——
                //   刷的时候子物体还不存在，等它们出生时停在 layer 0，UICamera 不渲染。
                //
                //   ReapplyLayer 这个函数早就有了，注释里写的是"动态建行之后补层"，
                //   但没人想到 TMP 自己也会在我们背后建物体。补这一下，
                //   全部 fallback 子网格才进得了相机。
                ReapplyLayer();

                if (all.Length > 0) DiagnoseFirstLabel(all);
            }
            catch (Exception e) { Main.LogError("[UI] TextGuard: " + e.Message); }
        }

        /// <summary>
        /// 挑一个真有汉字的标签验尸，并把它的 TMP 子网格（fallback 用）也一并报告。
        /// 每次开窗打一行 —— 一行就能定位方块问题，比来回换版本发包便宜得多。
        /// </summary>
        private static void DiagnoseFirstLabel(TextMeshProUGUI[] all)
        {
            try
            {
                TextMeshProUGUI pick = null;
                int offLayer = 0, totalSub = 0;
                var bad = new System.Text.StringBuilder();
                int named = 0;

                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    bool hasCjk = false;
                    foreach (char ch in t.text) if (ch >= 0x4E00 && ch <= 0x9FFF) { hasCjk = true; break; }
                    if (!hasCjk) continue;
                    if (pick == null) pick = t;

                    // ★逐个标签查子网格的层★ 之前只验第一个，于是"标题好了、别的没好"
                    //   这种局面完全看不出来 —— 被验的恰好是好的那一个。
                    var ss = t.GetComponentsInChildren<TMP_SubMeshUI>(true);
                    for (int k = 0; k < ss.Length; k++)
                    {
                        totalSub++;
                        if (ss[k].gameObject.layer == UiLayer) continue;
                        offLayer++;
                        if (named < 5)
                        {
                            named++;
                            string txt = t.text.Length > 8 ? t.text.Substring(0, 8) : t.text;
                            bad.Append($"'{txt}'@{(t.transform.parent != null ? t.transform.parent.name : "?")}"
                                     + $"(层{ss[k].gameObject.layer}) ");
                        }
                    }
                }
                if (pick == null) return;

                VanillaSkin.DiagnoseLabel(pick, pick.transform.parent != null ? pick.transform.parent.name : "?");
                Main.LogVerbose($"[UI/验尸] 中文标签的 fallback 子网格共 {totalSub} 个　目标层={UiLayer}"
                       + (offLayer == 0 ? "　全部到位" : $"　★{offLayer} 个掉队：{bad}★"));

                // ★逐个标签报字体 + 子网格材质 + shader★
                //
                //   实机截图显示：方块的颜色**跟着富文本的 <color> 走**
                //   （状态条里蓝块金块并存）。背景贴图不可能一个字一种颜色，
                //   所以那些块**就是字本身** —— 网格画了、颜色对，只是采样出实心。
                //   那就不是层的问题，嫌疑转到**子网格的材质**：
                //   TMP 建 fallback 材质时拿主材质当模板、只换图集贴图，
                //   主材质的 shader / 图集尺寸参数和 fallback 图集对不上就会糊成实心。
                //
                //   标题恰好正常，很可能只是它创建时 VanillaSkin.Font 是配得上的那个 ——
                //   AdoptFont 会在建窗过程中换 _font，不同标签拿到的字体本就可能不同。
                //   所以必须**逐个**看，只验一个永远只会验到好的那个。
                int shown = 0;
                for (int i = 0; i < all.Length && shown < 8; i++)
                {
                    var t = all[i];
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    bool cjk = false;
                    foreach (char ch in t.text) if (ch >= 0x4E00 && ch <= 0x9FFF) { cjk = true; break; }
                    if (!cjk) continue;
                    shown++;

                    var sb2 = new System.Text.StringBuilder();
                    var ss = t.GetComponentsInChildren<TMP_SubMeshUI>(true);
                    for (int k = 0; k < ss.Length; k++)
                    {
                        var m  = ss[k].sharedMaterial;
                        var fa = ss[k].fontAsset;
                        // ★SDF 三参数是判案关键★
                        //   _GradientScale / _TextureWidth / _TextureHeight 和**具体图集**绑定。
                        //   子网格材质是 TMP 拿主材质当模板复制来的 —— 如果这三个值
                        //   还是主字体图集的，配上思源宋体的图集，SDF 阈值就全错，
                        //   整个字块内部一律通过 ⇒ 实心方块（颜色仍是文字色）。
                        //   把它和 fallback 字体**原生材质**的同名参数并排打出来，
                        //   两组数不一样就实锤了。
                        string p = "?", pn = "?";
                        try
                        {
                            if (m != null)
                                p = SdfParams(m);
                            if (fa != null && fa.material != null)
                                pn = SdfParams(fa.material);
                        }
                        catch { }
                        // ★materialID 才是判"共用"的唯一硬证据★
                        //   材质**名字**说明不了问题：TMP 用 new Material(源) 复制出
                        //   fallback 材质时会连名字一起继承，所以一堆不同的材质实例
                        //   都叫 ScreenFont_Base。只有 InstanceID 能区分是不是同一个对象。
                        //   如果字号不同的标签共用同一个 ID，那 TMP 写在材质上的
                        //   _ScaleRatioA（跟字号走）就会互相覆盖 —— 最后写的赢，
                        //   其余的糊掉。这正好对应"大字号标题正常、小字号条目成块"。
                        sb2.Append($"[{(fa != null ? fa.name : "?")} 材质={(m != null ? m.name : "null")}"
                                 + $"#{(m != null ? m.GetInstanceID() : 0)}"
                                 + $" 实际={p} 原生={pn}{(p == pn ? "" : " ★不符★")}"
                                 + $" 比例={ScaleRatio(m)}"
                                 + $" 层{ss[k].gameObject.layer}]");
                    }
                    string txt = t.text.Length > 10 ? t.text.Substring(0, 10) : t.text;
                    Main.LogVerbose($"[UI/标签] '{txt}' 字号={t.fontSize:F0} 字体={(t.font != null ? t.font.name : "null")}"
                           + $" 主材质={(t.fontSharedMaterial != null ? t.fontSharedMaterial.name : "null")}"
                           + $" 层={t.gameObject.layer} 子网格{ss.Length}个 {sb2}");
                }
            }
            catch (Exception e) { Main.LogError("[UI/验尸] " + e.Message); }
        }

        // ------------------------------------------------------------- 小工具

        /// <summary>
        /// SDF 材质的三个"和图集绑定"的参数，压成一行方便并排比对。
        /// 子网格材质是 TMP 拿主材质当模板复制的；如果这三个值还是主字体图集的，
        /// 配上思源宋体的图集就会让 SDF 阈值全错 —— 字块内部一律通过测试，
        /// 渲染成用文字颜色填充的实心方块。
        /// </summary>
        /// <summary>
        /// TMP 写在材质上的 _ScaleRatioA。它**跟字号走**，所以两个字号不同的标签
        /// 若共用同一个材质实例，这个值就会被后写的那个覆盖 —— 先写的那个渲染就错。
        /// </summary>
        private static string ScaleRatio(Material m)
        {
            try { return m.GetFloat("_ScaleRatioA").ToString("F2"); }
            catch { return "?"; }
        }

        private static string SdfParams(Material m)
        {
            try
            {
                // ★不能写成 $"...{m.GetFloat(\"_X\")}..."★
                //   C# 的插值串里不允许再出现字符串字面量的引号（无论转不转义），
                //   解析器会在第一个引号处提前结束这个串。先取到局部变量再拼。
                float g = m.GetFloat("_GradientScale");
                float w = m.GetFloat("_TextureWidth");
                float h = m.GetFloat("_TextureHeight");
                return "G" + g.ToString("F1") + "/" + w.ToString("F0") + "x" + h.ToString("F0");
            }
            catch { return "?"; }
        }
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
            // ★首选：克隆一个原版的 TMP 文本★
            //
            //   方块 bug 追了六个版本，每一轮的日志都显示我们的标签和好用的标签
            //   **逐字段相同**（同一字体、同一主材质、同样的 _GradientScale 和
            //   图集尺寸、fallback 都正确解析到思源宋体、子网格也都在 layer 5），
            //   可玩家那台就是整片实心块，作者机怎么测都正常。
            //
            //   但有一个事实从头到尾没变过：**克隆原版按钮的那个标签，两台机器都正常**。
            //   同一个窗口、同一帧、紧挨着的两个控件，一个好一个坏，
            //   区别只有"抄来的"和"新建的"。
            //
            //   TMP 组件上有几十个序列化字段，Instantiate 逐字节带过来，
            //   AddComponent 拿到的是一套默认值 —— 哪一个默认值在他那台机器上是错的，
            //   我没有能力从日志里穷举出来。所以不再构造，直接抄能用的那个。
            TextMeshProUGUI t = VanillaWidgets.CloneLabel(parent);
            GameObject go;
            if (t != null)
            {
                go = t.gameObject;
                go.name = "Label";
            }
            else
            {
                // 摘不到模板（还没进游戏、ESC 菜单没实例化）时的回退路径。
                // 不比改之前差 —— 那正是改之前的全部实现。
                go = NewUI("Label", parent);
                t = go.AddComponent<TextMeshProUGUI>();
                if (VanillaSkin.Font != null)
                {
                    // ★绝不指定 fontSharedMaterial★
                    //   打分函数 CoverageOf 把 fallback 链算进去了，于是一个自身
                    //   一个中文字形都没有的拉丁字体也能打满分被选中；再把它自己
                    //   那张（_MainTex 指向拉丁图集的）材质钉上去，中文字形的 UV
                    //   就会拿去错的图集上采样。只设 font、不碰材质，
                    //   TMP 会用 font.material（必然配套）并走正常的 fallback 子对象。
                    t.font = VanillaSkin.Font;
                }
            }
            go.AddComponent<LayerFixer>();   // TMP 事后加子网格时就地补层，见该类注释
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
                // 只在建完行之后的两三帧里跑；TMP 事后才生的子网格由
                // 标签自己挂的 LayerFixer 在**被添加的那一刻**处理，不靠轮询。
                //
                //   1.0.35 只在"建完行之后连补 3 帧"。实机结果是标题「卫队招募」好了，
                //   其余标签仍是块 —— 因为 TMP 是**按标签**在各自首次生成网格时才
                //   创建 fallback 子网格的，而那些时刻散落在窗口的整个生命周期里
                //   （刷新状态条、切分型重建右栏、鼠标悬停改文本……），
                //   任何固定长度的窗口都盖不全。
                //
                //   ★为什么看起来是"实心块"而不是"看不见"★
                //     掉在 layer 0 的是**文字的 fallback 子网格**，而行/徽章的
                //     背景贴图在 layer 5 照常渲染 —— 于是只剩一个和文字等宽的
                //     纯色块。标题没有背景，所以它一修好就直接显出字来。
                //
                //   ★开销★ 每 10 帧一次递归改 layer，树规模是几十个节点量级，
                //   远小于一次布局重建。相比"面板上偶发一片方块"，这个代价值得。
                if (RetinueUI._layerDirty > 0) { RetinueUI._layerDirty--; RetinueUI.ReapplyLayer(); }
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
