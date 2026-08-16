## 一、路线决策：**B 为主（自建 uGUI）+ 运行时"摘"原版字体/贴图 + A1 仅作可选局部增强。C 排除。**

### 对比（三项都按核验后的事实，不是调查员原话）

| | A 复用原版 prefab | **B 自建 uGUI（选它）** | C IMGUI 换皮 |
|---|---|---|---|
| **观感上限** | 高——但**原版没有任何叶子 View 是"立绘+两个按钮"**（最接近的 `CharGenPregenSelectorItemView` 只有名字+立绘+整块单击），第二个按钮必须手写 uGUI，即已经在写 B | **高**。多层 Image 叠框/角饰/扫描线，和原版同一套做法；字体贴图直接用原版对象 | **低**。`GUIStyle` 每状态只有 **1 张** background，原版窗口是 5~8 层 Image 叠的。且 **IMGUI 只吃 `UnityEngine.Font`，原版 UI 全是 `TMP_FontAsset`(SDF)**——"用游戏字体"这个核心卖点直接落空 |
| **工作量** | 1300~2000 行，**且绝大部分是反射私有字段**；真实成本在"改代码→重启游戏→看日志"的试错循环 | **~1500 行**（骨架已写完 570 行，见下） | 600~900 行（调查员报的 200~400 低估了：光素材层 `VanillaTheme.cs` 就 450 行） |
| **版本脆弱度** | **最高**。且 `ViewBase.Bind()` 把 `BindViewImplementation()` 包在 try/catch 里**只记一条日志**——更新后不是崩、不是空窗，是**半绑定窗口**，诊断成本最差 | **最低**。只依赖 `UnityEngine.UI` / `TMPro` 公开 API。摘不到字体/贴图 → 退纯色，"变丑不变崩" | 低（纯 Unity API） |
| **ToyBox 冲突** | 低 | **零**。独立 Canvas，与 IMGUI 分属两套渲染栈 | **最高**。`GUI.skin` 是进程级 static（`GUI.s_Skin`，只在 `CleanupRoots()` 置 null），不 try/finally 还原会污染 ToyBox 的 14 个 tab 和 UMM 自身 |
| **清理** | 中。**绝不能碰 `ForceUnload()`**（`UIDestroyViewLink.Unbind()` 会 `ResourcesLibrary.ForceUnloadResource(AssetId)`，卸掉游戏正在用的包） | **最干净**。`Destroy(_root)` 一把清空 | 干净 |

### 三条否决 A 的硬事实
1. **窗口级复用不可行**：`VendorVM` 无参 ctor 第一件事就是 `EventBus.RaiseEvent(IFullScreenUIHandler → HandleFullScreenUiChanged(true, ...))` 并绑交易会话；`GroupChangerBaseView.BindViewImplementation` 同样发全屏事件。**绑定即入原版全屏栈**，无绕法。
2. **`UIConfig.Instance.ViewConfigs` 那 5 个正门是破坏性的**：`ViewPrefabPair.Load()` 第一句就是 `prefabLink.ForceUnload()`。运行时调 = 卸掉玩家正在用的 HUD。
3. 元素级可行但会退化成 B，还多背一层反射债。

### "混"怎么混（明确边界）
- **外壳、布局、按钮、滚动 = 100% 自建**（B）。
- **观感 = 运行时从活着的原版 UI 上摘**：`TMP_FontAsset` + `fontSharedMaterial` 从活的 `TextMeshProUGUI` 上读；九宫格 `Sprite` 按**名字白名单**从活的 `Image` 上读。摘活对象的关键好处：材质/图集/SDF 都是游戏自己加载好的，**不需要 BodyGuardV2 那套 `Shader.Find("TextMeshPro/Distance Field")` 粉方块修复**（那是 AssetBundle 路线才有的病）。
- **A1（`Object.Instantiate` 活的原版控件）只在第 5 步之后、且只对局部**（比如头像外框）考虑。用的话三条铁律缺一不可：① `Destroy` 掉克隆体上的原版 MonoBehaviour（`GroupChangerCharacterBaseView` / `m_PortraitPartView` / **`m_BuffPartView`** 共三处）；② 交互盖透明 `Image{clear}+Button{transition=None}`，不动原版控件；③ 失败必须能退回手搓。**绝不用 `WidgetFactory.GetWidget()`**——它是池化的（`s_Widgets`/`s_DisposedWidgets`/`s_StrictMatchingWidgets`），你改脏的实例会漏回原版界面。

> 注：BodyGuardV2 最终用的是 **UI Toolkit**，别跟。游戏自身 `UnityEngine.UIElements` 引用数 = **0**（`Code.dll` 与 `Owlcat.Runtime.UI.dll` 均为 0），`FindObjectsOfTypeAll<PanelSettings>()` 必空，`ThemeStyleSheet` 是编译产物运行时造不出。而且它是 **OwlcatModification 不是 UMM**，自带 AssetBundle 才走得通——它那条路的前提你没有。

---

## 二、可编译骨架

**已实测编译通过：0 警告 0 错误。** 文件在 `C:\tmp\kgdui\RetinueUI.cs`（在你项目的完整副本里编的，引用了 `Archetypes` / `GearTool` / `ChainProbe` / `RetinueTest` / `UnitPortraits` / `Main`）。

```bash
cp /c/tmp/kgdui/RetinueUI.cs "D:/RT_RetinueMod/src/KgdRetinue/RetinueUI.cs"
```

### csproj 新增引用（**实测只需这 3 个**）

插到 `D:\RT_RetinueMod\src\KgdRetinue\KgdRetinue.csproj` 第 44 行 `UnityEngine.InputLegacyModule` 之后：

```xml
    <!-- 游戏内 uGUI 窗口（路线 B）新增的三个 -->
    <Reference Include="UnityEngine.UI"><HintPath>$(GameManaged)\UnityEngine.UI.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine.UIModule"><HintPath>$(GameManaged)\UnityEngine.UIModule.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Unity.TextMeshPro"><HintPath>$(GameManaged)\Unity.TextMeshPro.dll</HintPath><Private>false</Private></Reference>
```

物理路径均为 `H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\WH40KRT_Data\Managed\`，三个 dll 已确认存在。

| dll | 提供 |
|---|---|
| `UnityEngine.UI.dll` | `Image` `Button` `Selectable` `ScrollRect` `RectMask2D` `VerticalLayoutGroup` `ContentSizeFitter` `LayoutElement` `CanvasScaler` `GraphicRaycaster` **`EventSystem` `StandaloneInputModule`**（后两个也在这个 dll，不是单独模块） |
| `UnityEngine.UIModule.dll` | `Canvas` `CanvasRenderer` `RenderMode`（不加报 `CS1069 ... 已转发到 UnityEngine.UIModule`） |
| `Unity.TextMeshPro.dll` | `TextMeshProUGUI` `TMP_FontAsset` `TextAlignmentOptions` `TextWrappingModes` |

**`UnityEngine.TextRenderingModule` 不需要**——我删掉它重编依然成功。BodyGuardV2 的 csproj 引了它，是它别处用到，别照抄。
`Sprite` / `RectTransform` 在你已引的 `UnityEngine.CoreModule` 里。

### 骨架全文

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KgdRetinue.UI
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

        // 兜底配色（摘不到贴图时用纯色；后续用 UIConfig dump 校准）
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

        private static void EnsureFont()
        {
            if (_fontTried) return;
            _fontTried = true;
            try
            {
                // 只认场景里活着的 TMP 文本 —— FindObjectsOfTypeAll 会返回未实例化的 prefab，
                // 它们的材质可能没加载，用了就是粉方块。
                TextMeshProUGUI live = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()
                    .FirstOrDefault(t => t != null && t.font != null
                                      && t.fontSharedMaterial != null
                                      && t.gameObject.scene.IsValid());
                if (live != null) { _font = live.font; _fontMat = live.fontSharedMaterial; return; }

                TMP_FontAsset any = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                    .FirstOrDefault(f => f != null);
                if (any != null) { _font = any; _fontMat = any.material; return; }

                Main.Log("[UI] 未摘到原版 TMP 字体，退回 TMP 默认字体（能用，观感退化）");
            }
            catch (Exception e) { Main.LogError("[UI] 摘字体失败: " + e.Message); }
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
        private const string RootName = "KgdRetinue_UI";

        private static GameObject _root;
        private static UiHost _host;
        private static Transform _archContent;
        private static Transform _unitContent;
        private static TextMeshProUGUI _titleRight;
        private static int _selected = -1;

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
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = 800;

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
                RebuildArchetypes();
                RebuildUnits();
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

        public static void Refresh() { RebuildArchetypes(); RebuildUnits(); }

        // ------------------------------------------------------------- 骨架搭建
        private static void BuildClickBlocker(Transform parent)
        {
            GameObject go = NewUI("ClickBlocker", parent);
            Stretch(go, 0f);
            Image img = go.AddComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.55f);
            img.raycastTarget = true;    // 吃掉点击，避免点穿到世界
        }

        private static void EnsureEventSystem(Transform parent)
        {
            if (EventSystem.current != null) return;
            // 挂在自己根下 -> 关窗一起销毁，不污染全局
            GameObject es = new GameObject("KgdRetinue_EventSystem",
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
            PaintPanel(panel.AddComponent<Image>(), VanillaSkin.PanelSprite, VanillaSkin.Ink);

            // 标题栏
            TextMeshProUGUI title = MakeLabel(panel.transform, "卫队招募", 34f, VanillaSkin.Gold,
                                              TextAlignmentOptions.Left);
            RectTransform trt = (RectTransform)title.transform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.offsetMin = new Vector2(32f, -76f); trt.offsetMax = new Vector2(-180f, -20f);

            // 关闭按钮
            Button close = MakeButton(panel.transform, "关闭", 120f, 44f, Close);
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
            lrt.offsetMax = new Vector2(28f + 320f, -92f);
            PaintPanel(left.AddComponent<Image>(), VanillaSkin.RowSprite, VanillaSkin.RowBg);
            MakeSectionLabel(left.transform, "分型");
            _archContent = MakeScrollArea(left.transform, 44f);

            // 右列：该分型下的单位
            GameObject right = NewUI("RightColumn", panel.transform);
            RectTransform rrt = (RectTransform)right.transform;
            rrt.anchorMin = new Vector2(0f, 0f); rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0.5f, 0.5f);
            rrt.offsetMin = new Vector2(28f + 320f + 16f, 28f);
            rrt.offsetMax = new Vector2(-28f, -92f);
            PaintPanel(right.AddComponent<Image>(), VanillaSkin.RowSprite, VanillaSkin.RowBg);
            _titleRight = MakeSectionLabel(right.transform, "请先选择左侧分型");
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
                MakeLabel(_archContent, "没有可用分型（archetypes.json 没载入？）", 20f, VanillaSkin.TextDim,
                          TextAlignmentOptions.Left);
                return;
            }

            for (int i = 0; i < all.Length; i++)
            {
                int idx = i;   // 闭包捕获
                ChainProbe.Archetype a = all[i];
                Button b = MakeButton(_archContent, a.Name, 0f, 52f, () => { _selected = idx; RebuildUnits(); });
                LayoutElement le = b.gameObject.AddComponent<LayoutElement>();
                le.minHeight = 52f; le.preferredHeight = 52f;
                if (idx == _selected)
                {
                    Image bg = b.GetComponent<Image>();
                    if (bg != null) bg.color = new Color(VanillaSkin.Gold.r, VanillaSkin.Gold.g,
                                                         VanillaSkin.Gold.b, 0.28f);
                }
            }
        }

        private static void RebuildUnits()
        {
            if (_unitContent == null) return;
            ClearChildren(_unitContent);

            ChainProbe.Archetype[] all = null;
            try { all = Archetypes.All; } catch { }
            if (all == null || _selected < 0 || _selected >= all.Length)
            {
                if (_titleRight != null) _titleRight.text = "请先选择左侧分型";
                return;
            }

            ChainProbe.Archetype arch = all[_selected];
            if (_titleRight != null) _titleRight.text = arch.Name + " — 可招募单位";

            // 第一行：普通卫兵
            AddUnitRow(NormalUnitId(arch), "普通卫兵", "无限制", _selected, null);

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
        }

        private static string NormalUnitId(ChainProbe.Archetype a)
        {
            if (a != null && !string.IsNullOrEmpty(a.UnitId)) return a.UnitId;
            try { return Main.Settings != null ? Main.Settings.UnitAssetId : null; }
            catch { return null; }
        }

        private static string EliteSubtitle(int archIndex, ChainProbe.EliteDef ed)
        {
            try
            {
                ChainProbe.EliteDef next = GearTool.NextElite(archIndex);
                if (next != null && ReferenceEquals(next, ed)) return "可招募";
                if (!GearTool.EliteUnlocked(archIndex))
                    return "未解锁 — 需本路线卫兵练到 T3 职业";
                return "已招募 / 排队中";
            }
            catch { return ""; }
        }

        private static void AddUnitRow(string unitId, string name, string subtitle,
                                       int archIndex, ChainProbe.EliteDef elite)
        {
            GameObject row = NewUI("Row_" + (name ?? "?"), _unitContent);
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = 116f; le.preferredHeight = 116f;
            PaintPanel(row.AddComponent<Image>(), VanillaSkin.RowSprite, VanillaSkin.RowBg);

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
            TextMeshProUGUI nameTxt = MakeLabel(row.transform, name ?? "(未命名)", 24f,
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
            Button gear = MakeButton(row.transform, "改装备", 130f, 44f, () => OnEditGear(archIndex, elite));
            RectTransform grt = (RectTransform)gear.transform;
            grt.anchorMin = grt.anchorMax = new Vector2(1f, 0.5f);
            grt.pivot = new Vector2(1f, 0.5f);
            grt.anchoredPosition = new Vector2(-152f, 0f);

            Button hire = MakeButton(row.transform, "招募", 130f, 44f, () => OnRecruit(archIndex, elite));
            RectTransform hrt = (RectTransform)hire.transform;
            hrt.anchorMin = hrt.anchorMax = new Vector2(1f, 0.5f);
            hrt.pivot = new Vector2(1f, 0.5f);
            hrt.anchoredPosition = new Vector2(-12f, 0f);

            if (elite != null)
            {
                bool ok = false;
                try
                {
                    ChainProbe.EliteDef next = GearTool.NextElite(archIndex);
                    ok = next != null && ReferenceEquals(next, elite);
                }
                catch { }
                SetInteractable(hire, ok);
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
                RebuildUnits();
            }
            catch (Exception e) { Main.LogError("[招募] 失败: " + e.Message); }
        }

        private static void OnEditGear(int archIndex, ChainProbe.EliteDef elite)
        {
            // TODO: 接到现有的装备编辑逻辑（GearTool / Archetypes.AddPlayerGear）
            Main.Log("[装备] 待接入: archIndex=" + archIndex
                     + " elite=" + (elite != null ? elite.Name : "(普通)"));
        }

        // ------------------------------------------------------------- 小工具
        private static GameObject NewUI(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Stretch(GameObject go, float pad)
        {
            RectTransform rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
        }

        private static void PaintPanel(Image img, Sprite sprite, Color fallback)
        {
            if (sprite != null)
            {
                img.sprite = sprite;
                img.type = sprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
                img.color = Color.white;
            }
            else img.color = fallback;
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

        private static TextMeshProUGUI MakeSectionLabel(Transform parent, string text)
        {
            TextMeshProUGUI t = MakeLabel(parent, text, 22f, VanillaSkin.Gold, TextAlignmentOptions.Left);
            RectTransform rt = (RectTransform)t.transform;
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(14f, -38f); rt.offsetMax = new Vector2(-14f, -8f);
            return t;
        }

        private static Button MakeButton(Transform parent, string text, float w, float h, Action onClick)
        {
            GameObject go = NewUI("Button_" + text, parent);
            RectTransform rt = (RectTransform)go.transform;
            if (w > 0f) rt.sizeDelta = new Vector2(w, h); else rt.sizeDelta = new Vector2(0f, h);

            Image img = go.AddComponent<Image>();
            PaintPanel(img, VanillaSkin.ButtonSprite, new Color(VanillaSkin.Gold.r * 0.30f,
                                                                VanillaSkin.Gold.g * 0.28f,
                                                                VanillaSkin.Gold.b * 0.18f, 0.95f));
            Button b = go.AddComponent<Button>();
            b.targetGraphic = img;
            b.transition = Selectable.Transition.ColorTint;
            ColorBlock cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.15f, 1.12f, 1.0f, 1f);
            cb.pressedColor = new Color(0.75f, 0.72f, 0.62f, 1f);
            cb.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            b.colors = cb;
            if (onClick != null) b.onClick.AddListener(new UnityEngine.Events.UnityAction(onClick));

            TextMeshProUGUI label = MakeLabel(go.transform, text, 20f, VanillaSkin.Text,
                                              TextAlignmentOptions.Center);
            Stretch(label.gameObject, 6f);
            return b;
        }

        private static void SetInteractable(Button b, bool on)
        {
            if (b == null) return;
            b.interactable = on;
            TextMeshProUGUI t = b.GetComponentInChildren<TextMeshProUGUI>();
            if (t != null) t.color = on ? VanillaSkin.Text : VanillaSkin.TextDim;
        }

        /// <summary>建一个纵向滚动区，返回 content。topInset 给区块标题让位。</summary>
        private static Transform MakeScrollArea(Transform parent, float topInset)
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
            vlg.childControlHeight = false;
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
            private void Update()
            {
                if (!Main.Enabled) { RetinueUI.Close(); return; }
                if (Input.GetKeyDown(KeyCode.Escape)) RetinueUI.Close();
            }
        }
    }
}
```

### 干净销毁（三重保险）
1. **`Destroy(_root)`** —— 所有 UI（Panel / 两列 / 滚动 / 所有行 / ClickBlocker / 我们建的 EventSystem）都是 `_root` 的**子节点**，一次全没。EventSystem 刻意挂在自己根下就是为了这个（`EnsureEventSystem` 里 `SetParent(parent, false)`）。
2. **`Shutdown()`** 额外做：`VanillaSkin.Reset()`（丢引用，不 Destroy 原版资源）+ `UnitPortraits.Cleanup()`（归还 hold 的资源句柄）+ **按名字 `KgdRetinue_UI` 全场景扫一遍兜底**（防热重载残留）。
3. **`UiHost.Update()` 自毁**：`Main.Enabled == false` 时自己 `Close()`——即使 `OnToggle` 忘了调也不会留活窗。

### 不碰存档（逐条）
- 只读：`Archetypes.All`、`GearTool.NextElite/EliteUnlocked`、`Main.Settings.UnitAssetId`、`BlueprintUnit` 上的立绘引用。
- **不写任何类型化标量字段**，不新增任何 AssetId。
- 唯一写操作是点「招募」→ `RetinueTest.SpawnOne(...)`，那是既有逻辑，UI 只是个触发器。
- 引用的 `Sprite` / `TMP_FontAsset` / `Material` 全是游戏自己的对象，**我们只持引用、绝不 Destroy**。
- 克隆原版控件（如果第 5 步用 A1）只克隆场景 GameObject，与序列化完全无关。

---

## 三、立绘方案

### 3.1 现状：`UnitPortraits.cs` 已在项目里且能编译，但有 3 处必须修

**修 1（性能，路线 B/C 都要）— 负缓存失效**
第 127 行 `Cache[key] = s;` 会把 `null` 也存进去，但第 101 行读取判据是 `cached != null`。结果：**永久拿不到图的条目每次重建列表都重跑完整蓝图+资源查找**。

```csharp
// 类字段旁加：
private static readonly HashSet<string> Misses = new HashSet<string>();

// Get() 开头（第 99~101 行附近）改为：
if (Misses.Contains(key)) return null;
Sprite cached;
if (Cache.TryGetValue(key, out cached) && cached != null) return cached;

// Get() 结尾（第 127 行）改为：
if (s != null) Cache[key] = s; else Misses.Add(key);

// Cleanup() 里加：
Misses.Clear();
```

**修 2（资源泄漏）— hold/free 计数不对称**
`Held` 是 `HashSet<string>`，`Cleanup()` 对每个 assetId 只 `FreeResourceRequest` 一次；但每次缓存自愈都会再 `TryGetResource(..., hold:true)` 让 `HandleCounter++`。净效果是计数单向漂移、本会话永远归不了零。

```csharp
// 把 HashSet<string> Held 换成：
private static readonly Dictionary<string, int> Held = new Dictionary<string, int>();

// SpriteOf 里 Held.Add(link.AssetId) 换成：
int n; Held.TryGetValue(link.AssetId, out n); Held[link.AssetId] = n + 1;

// Cleanup 里循环换成：
foreach (var kv in Held)
    for (int i = 0; i < kv.Value; i++)
        { try { ResourcesLibrary.FreeResourceRequest(kv.Key, true); } catch { } }
Held.Clear();
```

**修 3（会把异常抛给 UI 层）— 两个 public 方法裸奔**
`ResourcesLibrary.TryGetBlueprint<T>` 内部是**硬转换** `(TBlueprint)TryGetBlueprint(assetId)`，不是 `as` —— 类型不符会抛 `InvalidCastException`。而 `HasOwnPortrait()` / `GetPortraitBlueprint()` 完全没有 try/catch。给 `GetPortraitBlueprint` 的第 145 行套上：

```csharp
BlueprintUnit bp = null;
try { bp = ResourcesLibrary.TryGetBlueprint<BlueprintUnit>(unitAssetId); }
catch (Exception e) { Main.LogError("[立绘] 蓝图 " + unitAssetId + " 取用失败: " + e.Message); return null; }
if (bp == null) return null;
```

### 3.2 回退链（已在代码里，三级）
```
① BlueprintUnit.m_Portrait（反射私有字段）      ← 精英 pregen 走这条
② UnitPortraitOverride 借脸表                  ← 普通卫兵走这条
③ Empty_Portrait df493e2556e83f347beaa5597ca73abe  ← 最终兜底（这个有真图）
④ 还是 null → UI 画深灰方块（RetinueUI 里已处理）
```

**★ 最容易踩的坑：`BlueprintUnit.PortraitSafe` 不能当"图"用。** 它在 `m_Portrait` 为空时返回 `Placeholder_Male/Female_Portrait`，而这**两个蓝图的三条 SpriteLink 全空**（blob 仅 103/105 字节，已 hexdump 逐字节确认，核验方独立解包复现）。更糟：`BlueprintReferenceBase.IsEmpty()` 是**实例方法**要解引用 `guid`，对那 6 个 blob 里根本没有 `m_Portrait` 字段的卫兵，该字段大概率是 **null → `PortraitSafe` 直接抛 NRE**。所以既不能靠它拿图，也不能不 try/catch 地调它。

### 3.3 单位立绘对照表（已离线实测，方法：解包 `blueprints-pack.bbp` + 对照实验）

| unitId | 蓝图名 | 自带立绘 | 用什么 |
|---|---|---|---|
| `270b3e09cf424209b126b236f1655108` | DLC3_DL_Guard_Melee_Ally_Unit | ✗ | 借 AstraMilitarumMale `1b9082909e854f6d97c366358a280102` |
| `36e39788d1c648c8a75fbf36371f35f9` | DLC3_DL_Guard_Sniper_Unit | ✗ | 借 ArbitesMale `f0d5da655acb4b47846e52e8e97a5254` |
| `5fc80452fb6a4e2db02cd0a305715446` | DLC3_DL_Sororitas_HBolter_Unit | ✗ | 借 AdeptusMinistorumFemale `e5fa9cc788be4459bc0b9c6a74968da6` |
| `5bc8b3a8fb834977a3692a2325aff0f6` | DLC3_DL_Inquisitor_Unit | ✗ | 借 PsykerMale `0114a2db302c45a9bc780593d0ec5134` |
| `1fb60c0ef5fe459980c34a271dfad088` | OfficersDeckGuard | ✗ | 借 ImperialNavyMale `d03e6b0de6994d8f8b10a8ad16ebd94e` |
| `02094127ee4c402fbedbce1aff086e62` | DLC3_DL_Guard_Ranged_Ally_Unit（面板默认值） | ✗ | 借 ImperialNavyFemale `8d19acfeea77464783d579110e4a89e4` |
| `6176bbc9298646d9a371999be2f02e64` | StartGame_Pregen_Fighter | **✓** | AdeptusMinistorumFemale |
| `2e0da150605f4772bff485952fec3319` | StartGame_Pregen_Adept | **✓** | CriminalMale |
| `478bd2c6192d4867a3145e463bc363d6` | StartGame_Pregen_Soldier | **✓** | AstraMilitarumFemale |
| `f9d594a1094247b69f711774dd4d954e` | StartGame_Pregen_Leader | **✓** | ComissarMale |

**借脸源全部来自 `CharGenRoot` 的 32 个原版建卡立绘**（`92f5ea9d9306402588dc8418d4a794aa` 引用，含 ArbitesMale/Female、AstraMilitarum、Comissar、Criminal、AdeptusMinistorum、ImperialNavy、Nobility、Psyker、Decadence、Neutral×7 等），风格与原版一致、零新增资源、只读引用不写存档。

**尺寸**：Small 185×242 / HalfLength 330×432 / FullLength 692×1024。行高 96px 时 Small 约 73px 宽 —— 骨架里给的是 76×96 + `preserveAspect`。**注意这组尺寸来自玩家自定义立绘目录（`Portraits\0001`），bundle 内 sprite 的真实 `textureRect` 需进游戏用 `UnitPortraits.DumpTable()` 坐实（1 分钟）。**

### 3.4 ⚠ 一个需要你决策的数据问题
`archetypes.json` 里 **10 个 elites 只用了 4 个 pregen 单位 GUID**（文件自己的 `_eliteUnit待定` 注释承认是占位）：`6176bbc9…` 被 **4** 个精英共用、`478bd2c6…` 被 **3** 个、`f9d594a1…` 被 **2** 个。结果是**同一分型下的两个精英会显示同一张脸**。两条出路：换成不同的单位蓝图，或在 `UnitPortraitOverride` 之外再加一层"按精英名指定立绘"的覆盖表（改动很小）。这是数据问题不是 UI 问题，UI 层照实显示。

---

## 四、分步实施计划（每步可独立验证）

### 第 1 步 — 空壳窗口能以原版配色和字体显示出来（**最小可见成果**）
**做**：加 3 个 Reference，把 `RetinueUI.cs` 拷进项目。临时在 `Main.OnUpdate` 的热键块里加 `RetinueUI.Toggle()`（比如 F9）。**先不接** `RecruitInteraction`。
**进游戏看什么算通过**：
- 载入存档后按 F9，屏幕中央出现 1280×780 的深色面板，背景压暗，左列标题「分型」、右列「请先选择左侧分型」、右上「关闭」按钮。
- **文字不是方块、不是粉色**——这是"摘字体"成功的判据。
- UMM 日志里出现 `[UI] 摘贴图: panel=… button=… row=… (候选池 N)`。**panel 不是 null** 就说明白名单命中了原版九宫格。
- 点「关闭」窗口消失；再按 F9 又出来。
- **失败也算通过**：如果 panel=null，窗口是纯色的但布局正常 —— 说明兜底链工作，进第 2 步修白名单即可。

### 第 2 步 — 校准素材（把"能显示"变成"像原版"）
**做**：在 UMM 面板加一个临时按钮调 `VanillaSkin.DumpNineSliceCandidates()` 和 `VanillaTheme.DumpUIConfigColors()`（后者见 assets 调查的产物 `C:\tmp\kgdprobe\VanillaTheme.cs`，注意它的 `HasHanzi()` 有 bug，见风险 R6）。
**通过判据**：日志里能看到带 `border=(x,y,z,w)` 非零的 sprite 名单；从中挑 2~3 个填进 `PanelNames`/`ButtonNames`/`RowNames` 白名单前排；重进游戏后面板出现**真正的九宫格边框**（拉伸窗口尺寸时四角不变形）。同时把 dump 出的 hex 颜色抄成 `VanillaSkin` 里的常量。

### 第 3 步 — 两级列表接真数据 + 立绘
**做**：先打 3.1 的三个补丁，再打开 `RebuildArchetypes`/`RebuildUnits`（骨架已写好，本来就通）。跑一次 `UnitPortraits.DumpTable(...)`。
**通过判据**：
- 左列出现全部分型名；点一个，右列出现「普通卫兵」+ 该分型的精英行。
- **每行左侧有一张脸**，不是灰方块。精英行显示 pregen 的脸、普通行显示借来的脸。
- 日志里 `DumpTable` 打出的 Small 实际像素能坐实 185×242（或给出真实值）。
- 不可招的精英「招募」按钮是灰的，副标题写明理由。

### 第 4 步 — 接触发点 + ESC
**做**：把 `RecruitInteraction` / `RecruitWindow.Open(npc)` 的调用改成 `RetinueUI.Open()`；加骨架末尾注释里的 `EscMenuContextVM.RequestEscMenu` Harmony prefix。
**通过判据**：点 NPC 开出的是新窗口不是灰窗；按 ESC **只关我们的窗口、不弹游戏主菜单**；关掉后再按 ESC 游戏菜单正常弹出（说明 prefix 只在窗口开着时吃事件）。

### 第 5 步 — 清理与共存回归
**做**：删掉旧 `RecruitWindow.cs` 的 IMGUI 绘制（或保留 `Shutdown()` 空转），在 `Main.OnToggle(false)` 里把 `RecruitWindow.Shutdown()` 换成 `RetinueUI.Shutdown()`。
**通过判据**：
- 开着窗口时在 UMM 里禁用 mod → 窗口立刻消失；`Find` 不到名为 `KgdRetinue_UI` 的 GameObject。
- 再启用 → 窗口能正常重开（素材重摘成功）。
- **同时开 ToyBox 的 14 个 tab，UI 无异常**（uGUI 与 IMGUI 互不干扰）。
- 存档→读档→窗口重开正常；`RetinueRegistry.Count` 不变（证明 UI 没碰存档）。

### 第 6 步（可选）— 局部 A1 增强
只有当第 2 步的九宫格观感仍不达标时才做，且只针对头像外框那种小件。三条铁律见第一节。

---

## 五、风险清单

| # | 风险 | 状态 | 处置 |
|---|---|---|---|
| R1 | **摘 vanilla TMP 字体这条路没有先例** —— BodyGuardV2 是从**自己的 AssetBundle** `LoadAllAssets<TMP_FontAsset>()` 装字体的，"从活对象摘"是我们的外推 | **推测，需进游戏验证（第 1 步的核心判据）** | 已有两级兜底（活对象 → 任意 TMP_FontAsset → TMP 默认）。失败表现是"字丑"不是崩。若粉方块，照 `TmpFontLoader.FixShader` 补 `Shader.Find("TextMeshPro/Distance Field")` |
| R2 | 九宫格 sprite **名字对但当时没加载 / 不在有效场景**，白名单全落空 | **半实测**：名字确认存在于 `sharedassets0.assets`；"打开窗口那一刻是否 scene-valid" 未验证 | `DumpNineSliceCandidates()` 探针 + 纯色兜底。绝不用面积排序猜（那大概率挑到暗角/血条底） |
| R3 | 某些 sprite `border == (0,0,0,0)`（整图不切片），拉伸会变形 | **推测，需验证** | `PaintPanel` 已按 border 自动在 Sliced/Simple 间切换；探针会打出 border 值供筛选 |
| R4 | **我们的 Canvas(sortingOrder 800) 与 UMM/ToyBox 的 IMGUI 谁在上** | **推测，需进游戏验证** | 若被 IMGUI 盖住无所谓（那是管理器窗口）；若我们盖住 UMM 面板导致关不掉 mod，把 sortingOrder 降到 100~300 |
| R5 | `UnitPortraits` 的负缓存 / hold 计数 / 裸 public 方法三处缺陷 | **已实测**（核验方逐行反编译 + 我复核） | 见 3.1 三个补丁，**第 3 步之前必须打** |
| R6 | assets 调查产出的 `C:\tmp\kgdprobe\VanillaTheme.cs` 里 `HasHanzi()` 用 `HasCharacter(c, false, **false**)` —— Dynamic 字体那是查"已烘焙字典"不是查字面，可能选错字体导致满屏方块 | **已实测**（`TMP_FontAsset.HasCharacter` 第 974 行只查 `m_CharacterLookupDictionary`） | 若要用那个文件的 dump 功能：改 `tryAddCharacter: true`，或用 `UnityEngine.Font.HasCharacter(char)`（查真实 face），并以字体名 `SourceHanSerifSC` 作主选择器。**本骨架不依赖它** |
| R7 | 185×242 这组尺寸来自玩家自定义立绘目录，bundle 内可能被重打包 | **推测，需验证（1 分钟）** | `UnitPortraits.DumpTable()` 打真实 `textureRect`；`preserveAspect=true` 已让 UI 层不依赖具体数值 |
| R8 | 10 个精英只有 4 张脸（数据占位问题） | **已实测** | 见 3.4，需你决策 |
| R9 | ToyBox 共存 | **已实测**（ToyBox 1.7.34 是纯 IMGUI，`Main.tabs` 全是 `OnGUI` 委托，不碰 Canvas/EventSystem/uGUI） | 路线 B 冲突面近乎为零。**这正是排除 C 的主要理由之一** |
| R10 | 存档红线 | **已实测**（本方案只读，不写任何类型化标量字段，不新增 AssetId） | 无风险。若将来为 3D 立绘走 `EntitySpawner.SpawnUnit`，才会进入红线相邻区 —— 本方案用静态 Sprite，完全不碰 entity |
| R11 | `EscMenuContextVM.RequestEscMenu` 的存在与命名空间 | **已实测**（我刚跑的 ilspycmd：`Kingmaker.Code.UI.MVVM.VM.EscMenu.EscMenuContextVM`，`private void RequestEscMenu()`，注意有 `.Code` 那一层，两份调查里有一份写错了） | 直接用 |
| R12 | 全屏 ClickBlocker 可能挡不住"世界点击/直接操控" | **推测，需验证** | BodyGuardV2 除 ClickBlocker 外还额外打了 `GuardPadModalInputPatch` / `GuardPadModalDirectControlPatch` 两个 patch —— 但那两个文件的 `[HarmonyPatch(...)]` 属性在反编译产物里**丢失了，抄不了**。若第 4 步发现点窗口会指挥队伍走路，需要自己去 `Code.dll` 找那两个返回 bool 的判定。这是调研项不是抄写项 |
| R13 | 3 个新增 Reference 是否够 / 是否需要 `TextRenderingModule` | **已实测**：只需 `UnityEngine.UI` + `UnityEngine.UIModule` + `Unity.TextMeshPro`；**不需要** `TextRenderingModule`（删掉重编依然 0 错误） | 直接用 |

**若你只想先看一眼效果**：第 1 步的最保守做法 —— 只加 3 个 Reference、拷入 `RetinueUI.cs`、临时绑个 F9 热键，**不改任何现有文件的逻辑**。窗口出不来或长得丑都不影响现有功能，删掉这一个文件就完全回退。