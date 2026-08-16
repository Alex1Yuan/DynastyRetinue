核验结论已并入下方。三项修法互相独立，可分批上线；建议顺序 **③无文字 → ②发白 → ①原版控件**（风险由低到高，每步都能单独回归）。

---

# 0. 一句话结论

| 问题 | 真因 | 修法 |
|---|---|---|
| **① 观感不像原版** | 摘单张 sprite 必然失败（Owlcat 按钮是**多层 layer + overrideSprite**，不是"一张图三态"） | 克隆常驻场景中 `EscMenuPCView` 的活按钮，**保留 `OwlcatButton`**，删掉其余原版 MonoBehaviour |
| **② 对话中开窗发白** | **不是 sortingOrder**。全游戏 **0 个 ScreenSpaceOverlay Canvas**；原版 UI 全走 `UICamera` 的 camera stack（自研 SRP `WaaaghPipeline`），我们的 Overlay 在那条链之外 | Canvas 改 `ScreenSpaceCamera` + `worldCamera = UICamera.Instance` + **`layer = 5`**（漏了这条 = 全黑窗）+ `sortingOrder = 32000` |
| **③ 按钮白框没字** | **不是锚点/宽度**。`Stretch(label, 6f)` 四边各缩 6 → h=34 的按钮只剩 22px；19pt 中文行高放不下 → TMP `Ellipsis` 分支把**整串字符清零** | 纵向 padding 改 0 + 按钮高 34→38 + `childControlHeight=true`，再加一个自愈 `TextGuard` |

---

# 1. 改动清单

| 文件 | 动作 |
|---|---|
| `D:\RT_RetinueMod\src\KgdRetinue\KgdRetinue.csproj` | **加 1 条**引用（`Owlcat.Runtime.UI.dll`） |
| `D:\RT_RetinueMod\src\KgdRetinue\VanillaWidgets.cs` | **新建**（原版控件工厂 + Dump + Reset） |
| `D:\RT_RetinueMod\src\KgdRetinue\Deferred.cs` | **新建**（延帧执行器） |
| `D:\RT_RetinueMod\src\KgdRetinue\RetinueUI.cs` | 改 8 处（见 §4） |
| `D:\RT_RetinueMod\src\KgdRetinue\RecruitDialog.cs` | 改第 725 行 1 处 |

> **不要新建带 `partial` 的 RetinueUI 文件** —— `RetinueUI.cs:150` 实际是 `public static class RetinueUI`（无 `partial`），另起 partial 文件必然 **CS0260**。已核实。

---

# 2. csproj：只加一条

`Owlcat.Runtime.UI.dll` 已确认存在于 `H:\SteamLibrary\...\WH40KRT_Data\Managed\`。加在 `Unity.TextMeshPro` 那一行之后：

```xml
    <!-- 原版 UI 控件（OwlcatButton / OwlcatSelectable / ViewBase<T>）。
         克隆原版按钮必须要它；不加会 CS0234 + CS0246。 -->
    <Reference Include="Owlcat.Runtime.UI"><HintPath>$(GameManaged)\Owlcat.Runtime.UI.dll</HintPath><Private>false</Private></Reference>
```

**不需要** `UniRx.dll` / `Owlcat.Runtime.UniRx.dll`（我们不用 `OnLeftClickAsObservable`）。
**不需要** `UnityEngine.TextCoreFontEngineModule.dll`（下面的 `TextGuard` 用 `textInfo.characterCount` 实测，不读 `FaceInfo`，绕开了 CS0012）。

---

# 3. 新建 `D:\RT_RetinueMod\src\KgdRetinue\VanillaWidgets.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Kingmaker.Code.UI.MVVM.View.MessageBox.PC;
using Kingmaker.UI.MVVM.View.EscMenu.PC;
using Owlcat.Runtime.UI.Controls.Button;
using Owlcat.Runtime.UI.Controls.Selectable;
using Owlcat.Runtime.UI.Controls.SelectableState;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace KgdRetinue.UI
{
    /// <summary>克隆体标记：SetInteractable 要靠它区分"原版克隆"和"程序生成"。</summary>
    internal sealed class KgdVanillaTag : MonoBehaviour
    {
        public OwlcatButton Owlcat;
        public TextMeshProUGUI Label;
    }

    /// <summary>
    /// 原版控件工厂。局部克隆常驻场景中的 EscMenuPCView（进游戏后一直存在、只是 SetActive(false)）。
    ///
    /// 三条红线全部规避：
    ///   · 不调 UIConfig.Instance.ViewConfigs / ViewPrefabPair.Load()（那第一句是 ForceUnload，会卸活资源）
    ///   · 不调 WidgetFactory.GetWidget()（池化，改脏会漏回原版界面）
    ///   · 不 Bind 任何 VM（不进 IFullScreenUIHandler 全屏栈）
    /// </summary>
    internal static class VanillaWidgets
    {
        // ---- 开关 ---------------------------------------------------------
        /// <summary>按钮：克隆原版。失败自动回退。</summary>
        public static bool UseVanillaButton = true;
        /// <summary>窗框：默认 **关**。它是三项里唯一没有离线证据的（ESC 框由 Paper 四件套
        /// 多图拼成，拉伸行为未知）。先跑一次 Dump() 看层级，确认后再打开。</summary>
        public static bool UseVanillaPanel = false;
        /// <summary>保留 OwlcatButton（白拿三态 + 原版音效）。极端保守时可关。</summary>
        public static bool KeepOwlcatBehaviour = true;

        // ---- inactive holder：克隆的唯一安全姿势 --------------------------
        // Instantiate 到 activeSelf=false 的父物体下，clone 在"实例化 + 裁剪组件"全程
        // activeInHierarchy 恒为 false，原版组件的 Awake/OnEnable/Start 一次都不会跑。
        private static GameObject _holder;
        private static Transform Holder
        {
            get
            {
                if (_holder == null)
                {
                    _holder = new GameObject("KGD_CloneHolder");
                    _holder.SetActive(false);              // ★ 顺序：先 SetActive(false) 再 DontDestroyOnLoad
                    UnityEngine.Object.DontDestroyOnLoad(_holder);
                }
                return _holder.transform;
            }
        }

        private static EscMenuPCView _escView;
        private static GameObject _btnTemplate;
        private static bool _btnTemplateTried;

        // ================================================================
        // 定位模板
        // ================================================================
        private static EscMenuPCView FindEscMenuView()
        {
            // Unity 的 == 重载：被 Destroy 过的对象为 true-null，读档重建后会自动重新查找
            if (_escView != null) return _escView;
            try
            {
                EscMenuPCView[] all = Resources.FindObjectsOfTypeAll<EscMenuPCView>();
                for (int i = 0; i < all.Length; i++)
                {
                    EscMenuPCView v = all[i];
                    if (v == null || v.gameObject == null) continue;
                    if (!v.gameObject.scene.IsValid()) continue;      // 排除 bundle 里的 prefab 资产
                    if ((v.gameObject.hideFlags & HideFlags.HideAndDontSave) != 0) continue;
                    _escView = v; break;
                }
            }
            catch (Exception e) { Main.LogError("[VW] 找 EscMenuPCView 失败: " + e.Message); }
            return _escView;
        }

        private static readonly string[] EscButtonFields =
        {
            "m_OptionsButton", "m_LoadButton", "m_SaveButton",
            "m_ModsButton", "m_MainMenuButton", "m_QuitButton", "m_FormationButton"
        };

        /// <summary>拿一个活着的原版按钮当模板（它是 inactive 的，正合适）。</summary>
        private static GameObject GetButtonTemplate()
        {
            if (_btnTemplateTried) return _btnTemplate;
            _btnTemplateTried = true;
            try
            {
                // 1) 首选：ESC 菜单（用户点名的风格模板）
                EscMenuPCView v = FindEscMenuView();
                if (v != null)
                {
                    Type baseT = typeof(EscMenuPCView).BaseType;      // EscMenuBaseView
                    for (int i = 0; i < EscButtonFields.Length; i++)
                    {
                        FieldInfo f = baseT.GetField(EscButtonFields[i],
                                        BindingFlags.Instance | BindingFlags.NonPublic);
                        if (f == null) continue;
                        OwlcatButton ob = f.GetValue(v) as OwlcatButton;
                        if (ob != null && ob.gameObject != null && HasVisual(ob.gameObject))
                        { _btnTemplate = ob.gameObject; break; }
                    }
                    if (_btnTemplate == null)
                    {
                        OwlcatButton any = v.GetComponentInChildren<OwlcatButton>(true);
                        if (any != null && HasVisual(any.gameObject)) _btnTemplate = any.gameObject;
                    }
                }
                // 2) 备胎：MessageBoxPCView，同样常驻场景且 inactive
                if (_btnTemplate == null)
                {
                    MessageBoxPCView[] mb = Resources.FindObjectsOfTypeAll<MessageBoxPCView>();
                    for (int i = 0; i < mb.Length; i++)
                    {
                        MessageBoxPCView m = mb[i];
                        if (m == null || m.gameObject == null || !m.gameObject.scene.IsValid()) continue;
                        FieldInfo f = typeof(MessageBoxPCView).GetField("m_AcceptButton",
                                        BindingFlags.Instance | BindingFlags.NonPublic);
                        OwlcatButton ob = (f != null) ? f.GetValue(m) as OwlcatButton : null;
                        if (ob == null) ob = m.GetComponentInChildren<OwlcatButton>(true);
                        if (ob != null && HasVisual(ob.gameObject)) { _btnTemplate = ob.gameObject; break; }
                    }
                    if (_btnTemplate != null) Main.Log("[VW] ESC 菜单不在场景，用 MessageBox 按钮当模板");
                }
                // 3) 绝不盲抓场景里任意 OwlcatButton —— 那正是"摘到光晕图"的老路。
                if (_btnTemplate == null) Main.Log("[VW] 未找到原版按钮模板，全部回退程序生成");
                else Main.Log("[VW] 按钮模板 = " + FullPath(_btnTemplate.transform));
            }
            catch (Exception e) { Main.LogError("[VW] 取按钮模板失败: " + e.Message); }
            return _btnTemplate;
        }

        private static bool HasVisual(GameObject go)
        {
            Image[] imgs = go.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < imgs.Length; i++)
                if (imgs[i] != null && imgs[i].sprite != null) return true;
            return false;
        }

        // ================================================================
        // 组件裁剪：白名单反向删除
        // ================================================================
        private static bool ShouldKeep(Component c, bool keepOwlcat)
        {
            if (c is Transform) return true;                 // RectTransform 也在内，删不掉
            if (c is CanvasRenderer) return true;
            if (c is Graphic) return true;                    // Image / RawImage / TMP_Text / TMP_SubMeshUI
            if (c is CanvasGroup) return true;                // OwlcatSelectable 的 CanvasGroup 过渡要用
            if (c is Mask || c is RectMask2D) return true;
            if (c is LayoutGroup || c is ContentSizeFitter
             || c is LayoutElement || c is AspectRatioFitter) return true;
            if (c is Shadow) return true;                     // Outline : Shadow
            if (keepOwlcat && c is OwlcatSelectable) return true;   // 含 OwlcatButton / OwlcatMultiButton
            return false;
            // ★ 刻意不保留 Canvas / GraphicRaycaster：
            //   嵌套 Canvas 上的 Graphic 只被它自己那个 Canvas 的 GraphicRaycaster 检测
            //   （GraphicRegistry.GetGraphicsForCanvas）。保留 Canvas 却删掉 Raycaster
            //   = 按钮看得见点不动。窗框不需要独立排序，直接连 Canvas 一起删最省事。
        }

        private static void Strip(GameObject root, bool keepOwlcat)
        {
            try
            {
                Component[] comps = root.GetComponentsInChildren<Component>(true);
                for (int pass = 0; pass < 2; pass++)          // 两遍：解开 RequireComponent 链
                {
                    for (int i = comps.Length - 1; i >= 0; i--)   // 倒序，先删叶子
                    {
                        Component c = comps[i];
                        if (c == null) continue;                  // 已删 / missing script
                        if (ShouldKeep(c, keepOwlcat)) continue;
                        try { UnityEngine.Object.DestroyImmediate(c); } catch { }
                    }
                }
            }
            catch (Exception e) { Main.LogError("[VW] 裁剪失败: " + e.Message); }
        }

        /// <summary>剥离后打**幸存清单**（比"删了什么"可信：RequireComponent 挡住的删除
        /// Unity 只打 Console 错误、不抛异常，"删了什么"的日志会骗人）。</summary>
        public static void DumpSurvivors(GameObject root, string tag)
        {
            try
            {
                HashSet<string> seen = new HashSet<string>();
                Component[] cs = root.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < cs.Length; i++)
                {
                    if (cs[i] == null || cs[i] is Transform) continue;
                    string n = cs[i].GetType().FullName;
                    if (seen.Add(n)) Main.Log("  [" + tag + " 幸存] " + n);
                }
            }
            catch { }
        }

        // ================================================================
        // ★ 工厂 1：按钮
        // ================================================================
        /// <summary>
        /// 造一个按钮。优先克隆原版 ESC 按钮；克隆不到返回 null，调用方回退程序生成。
        /// 返回的是 UnityEngine.UI.Button —— 克隆体上另挂一个 transition=None、
        /// 无监听器的 Button 当**句柄**，让现有调用点（SetInteractable / GetComponent&lt;Image&gt;）
        /// 一行不改就能编译。真正的点击走 OwlcatButton.OnLeftClick。
        /// </summary>
        public static Button MakeVanillaButton(Transform parent, string text,
                                               float w, float h, Action onClick)
        {
            if (!UseVanillaButton) return null;
            GameObject src = GetButtonTemplate();
            if (src == null) return null;

            GameObject clone = null;
            try
            {
                // ★ 顺序不可改：先进 inactive holder，裁剪完再 SetParent 到活动树。
                //   反过来 clone 会先 OnEnable，原版 View 在没有 VM 的情况下可能 NRE，
                //   甚至 EscMenuBaseView.BindViewImplementation 第一句就 RequestPauseUi(true)。
                clone = UnityEngine.Object.Instantiate(src, Holder);
                clone.name = "KgdBtn_" + text;
                clone.SetActive(true);                        // 只是 activeSelf；holder 仍 inactive

                bool keep = KeepOwlcatBehaviour;
                Strip(clone, keep);

                // 文字：保留原版字体/字号/材质/autoSize，只换内容。
                // ★ 千万别关 enableAutoSizing —— 原版按钮多半开着，它正好自动规避 §5 的裁字问题。
                TextMeshProUGUI label = clone.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label != null) { label.gameObject.SetActive(true); label.text = text; }
                else
                {
                    label = RetinueUI.MakeLabelPublic(clone.transform, text, 19f,
                                new Color(0.13f, 0.10f, 0.05f, 1f), TextAlignmentOptions.Center);
                    RetinueUI.StretchPadPublic(label.gameObject, 8f, 0f);
                }

                // 点击
                OwlcatButton ob = keep ? clone.GetComponent<OwlcatButton>() : null;
                if (ob != null)
                {
                    // UniRx 的 AddListener 是运行时的、非序列化，Instantiate 不复制 ——
                    // 所以克隆出来天然是"零回调"。这里只清 Inspector 里的 persistent listener。
                    try
                    {
                        ob.OnLeftClick.RemoveAllListeners();
                        int n = ob.OnLeftClick.GetPersistentEventCount();
                        for (int i = 0; i < n; i++)
                            ob.OnLeftClick.SetPersistentListenerState(i, UnityEventCallState.Off);
                    }
                    catch { }
                    Action cb = onClick;
                    ob.OnLeftClick.AddListener(delegate
                    {
                        try { if (cb != null) cb(); }
                        catch (Exception e) { Main.LogError("[VW] 按钮回调异常: " + e); }
                    });
                    ob.SetInteractable(true);
                }

                // 句柄 Button：transition=None、无监听器，纯粹为了 API 兼容
                Button handle = clone.GetComponent<Button>();
                if (handle == null) handle = clone.AddComponent<Button>();
                handle.transition = Selectable.Transition.None;
                handle.targetGraphic = clone.GetComponent<Image>();
                handle.onClick.RemoveAllListeners();
                if (ob == null)
                {
                    Action cb2 = onClick;
                    handle.onClick.AddListener(delegate
                    {
                        try { if (cb2 != null) cb2(); }
                        catch (Exception e) { Main.LogError("[VW] 按钮回调异常: " + e); }
                    });
                }

                KgdVanillaTag tag = clone.AddComponent<KgdVanillaTag>();
                tag.Owlcat = ob; tag.Label = label;

                clone.transform.SetParent(parent, false);     // 到这里才真正进场景
                RectTransform rt = (RectTransform)clone.transform;
                rt.sizeDelta = new Vector2(w > 0f ? w : rt.sizeDelta.x, h);
                return handle;
            }
            catch (Exception e)
            {
                Main.LogError("[VW] 克隆按钮失败，回退程序生成: " + e.Message);
                try { if (clone != null) UnityEngine.Object.Destroy(clone); } catch { }
                return null;
            }
        }

        // ================================================================
        // ★ 工厂 2：窗框
        // ================================================================
        /// <summary>
        /// 造窗框背景。返回的 Image 是铺满 parent 的"底"：
        ///   · 克隆成功 → 返回一张 **透明** Image（只作 raycast 拦截 + 句柄），
        ///     原版框art 作为 child 0 铺在它上面。调用方**不要**再 PaintPanel 它。
        ///   · 克隆失败 / 开关关闭 → 返回 null，调用方回退 PaintPanel(GenTex)。
        /// </summary>
        public static Image MakeVanillaPanel(Transform parent)
        {
            if (!UseVanillaPanel) return null;
            EscMenuPCView v = FindEscMenuView();
            if (v == null) { Main.Log("[VW] 无 ESC 菜单，窗框回退程序生成"); return null; }

            GameObject bg = null, art = null;
            try
            {
                bg = new GameObject("KgdPanelBg", typeof(RectTransform));
                bg.transform.SetParent(parent, false);
                RectTransform brt = (RectTransform)bg.transform;
                brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
                brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
                Image stub = bg.AddComponent<Image>();
                stub.color = new Color(0f, 0f, 0f, 0f);
                stub.raycastTarget = true;

                art = UnityEngine.Object.Instantiate(v.gameObject, Holder);
                art.name = "KgdPanelArt";
                art.SetActive(true);

                // ★ 顺序关键：先把"窗体面板"的 Transform 引用抓在手上，再删按钮/文字。
                //   反过来（先删再 Find 路径字符串）只要那个节点自己带 TMP/OwlcatSelectable
                //   或是被删节点的后代，Find 就返回 null，尺寸永远设不上。
                Transform panelNode = GuessPanelNode(art.transform);

                List<GameObject> kill = new List<GameObject>();
                OwlcatSelectable[] sels = art.GetComponentsInChildren<OwlcatSelectable>(true);
                for (int i = 0; i < sels.Length; i++)
                    if (sels[i] != null && sels[i].gameObject != art
                        && !IsAncestorOf(sels[i].transform, panelNode)) kill.Add(sels[i].gameObject);
                TMP_Text[] txt = art.GetComponentsInChildren<TMP_Text>(true);
                for (int i = 0; i < txt.Length; i++)
                    if (txt[i] != null && txt[i].gameObject != art
                        && !IsAncestorOf(txt[i].transform, panelNode)) kill.Add(txt[i].gameObject);
                for (int i = 0; i < kill.Count; i++)
                    if (kill[i] != null) UnityEngine.Object.DestroyImmediate(kill[i]);

                Strip(art, false);                            // 窗框不需要交互

                if (panelNode == null)
                {
                    Main.Log("[VW] 没定位到窗体面板节点，窗框回退程序生成（先跑 VanillaWidgets.Dump() 看层级）");
                    UnityEngine.Object.Destroy(art);
                    UnityEngine.Object.Destroy(bg);
                    return null;
                }

                // 把窗体面板那一层提到我们的 bg 下，抛弃 ESC 的全屏遮罩层
                panelNode.SetParent(bg.transform, false);
                RectTransform prt = (RectTransform)panelNode;
                prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
                prt.localScale = Vector3.one;
                panelNode.SetSiblingIndex(0);
                UnityEngine.Object.Destroy(art);              // 剩下的壳丢掉

                string why;
                if (!AssetsLookLoaded(bg, out why))
                {
                    Main.Log("[VW] 窗框资源未就绪(" + why + ")，回退程序生成");
                    UnityEngine.Object.Destroy(bg);
                    return null;
                }
                return stub;
            }
            catch (Exception e)
            {
                Main.LogError("[VW] 克隆窗框失败，回退程序生成: " + e.Message);
                try { if (art != null) UnityEngine.Object.Destroy(art); } catch { }
                try { if (bg != null) UnityEngine.Object.Destroy(bg); } catch { }
                return null;
            }
        }

        private static bool IsAncestorOf(Transform maybeAncestor, Transform node)
        {
            if (node == null || maybeAncestor == null) return false;
            Transform t = node;
            while (t != null) { if (t == maybeAncestor) return true; t = t.parent; }
            return false;
        }

        /// <summary>面积最大但不是全屏的带 Image 层 = 窗体面板。在**原版布局已生效**的克隆上算。</summary>
        private static Transform GuessPanelNode(Transform root)
        {
            float screen = Screen.width * (float)Screen.height;
            Transform best = null; float bestArea = 0f;
            Image[] imgs = root.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < imgs.Length; i++)
            {
                RectTransform rt = imgs[i].rectTransform;
                if (rt == root) continue;                    // 根自己不算
                Rect r = rt.rect;
                float a = Mathf.Abs(r.width * r.height);
                if (a <= 1f) continue;
                if (a > screen * 0.85f) continue;            // 全屏遮罩，跳过
                if (a > bestArea) { bestArea = a; best = rt; }
            }
            return best;
        }

        // ================================================================
        // 资源就绪校验（粉方块 / SpriteAtlas late-binding）
        // ================================================================
        public static bool AssetsLookLoaded(GameObject go, out string why)
        {
            why = null;
            try
            {
                Graphic[] gs = go.GetComponentsInChildren<Graphic>(true);
                for (int i = 0; i < gs.Length; i++)
                {
                    Graphic g = gs[i];
                    if (g == null) continue;
                    Material m = g.materialForRendering;
                    if (m == null) { why = "materialForRendering==null @ " + g.name; return false; }
                    Shader sh = m.shader;
                    if (sh == null || !sh.isSupported || sh.name == "Hidden/InternalErrorShader")
                    { why = "shader 不可用(粉方块) @ " + g.name; return false; }

                    Image img = g as Image;
                    if (img != null && img.sprite != null)
                    {
                        Texture2D tex = null;
                        try { tex = img.sprite.texture; } catch { }
                        if (tex == null)
                        { why = "sprite.texture==null(图集未绑定) '" + img.sprite.name + "'"; return false; }
                        // ★ 只警告不否决：ModalWindow_HoloLinePic_Tile 这类平铺线条图本来就很小，
                        //   按尺寸硬判会把成功的克隆误杀成"未就绪"，白白回退。
                        if (tex.width <= 8 || tex.height <= 8)
                            Main.Log("[VW] 提示：小尺寸贴图 " + tex.width + "x" + tex.height
                                     + " '" + img.sprite.name + "'（平铺图正常，不算失败）");
                    }
                }
            }
            catch (Exception e) { why = "校验异常: " + e.Message; return false; }
            return true;
        }

        /// <summary>自建 Canvas 必调：TMP 的 SDF shader 需要 TexCoord1，
        /// 不开 additionalShaderChannels 文字会渲染成实心块/看不见（这不是"材质没加载"）。</summary>
        public static void PrepareCanvas(Canvas canvas)
        {
            if (canvas == null) return;
            canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1
                                             | AdditionalCanvasShaderChannels.TexCoord2
                                             | AdditionalCanvasShaderChannels.Normal
                                             | AdditionalCanvasShaderChannels.Tangent;
        }

        // ================================================================
        // 交互态
        // ================================================================
        /// <summary>克隆体返回 true（已按原版方式处理）；非克隆返回 false，调用方走老逻辑。</summary>
        public static bool TrySetInteractable(Button b, bool on)
        {
            if (b == null) return false;
            KgdVanillaTag tag = b.GetComponent<KgdVanillaTag>();
            if (tag == null) return false;
            try
            {
                if (tag.Owlcat != null) tag.Owlcat.SetInteractable(on);   // 原版 Disabled 三态
                else
                {
                    b.interactable = on;
                    Image img = b.targetGraphic as Image;
                    if (img != null) img.color = on ? Color.white : new Color(0.45f, 0.45f, 0.45f, 0.6f);
                }
            }
            catch (Exception e) { Main.LogError("[VW] SetInteractable: " + e.Message); }
            return true;
        }

        // ================================================================
        // 清理：mod 禁用 / 热重载 / 读档
        // ================================================================
        public static void Reset()
        {
            _escView = null;
            _btnTemplate = null;
            _btnTemplateTried = false;
            try { if (_holder != null) UnityEngine.Object.Destroy(_holder); } catch { }
            _holder = null;
        }

        // ================================================================
        // ★ 权威产出：把 ESC 菜单真实结构打进 kgd_log.txt
        // ================================================================
        public static void Dump(int maxDepth = 12)
        {
            EscMenuPCView v = FindEscMenuView();
            if (v == null) { Main.Log("[VW.Dump] 场景中没有 EscMenuPCView（未进游戏？）"); return; }
            Main.Log("=========== ESC MENU DUMP begin ===========");
            Main.Log("root=" + v.gameObject.name + " scene=" + v.gameObject.scene.name
                   + " activeSelf=" + v.gameObject.activeSelf + " layer=" + v.gameObject.layer);
            DumpTr(v.transform, 0, maxDepth);
            Main.Log("=========== ESC MENU DUMP end =============");
        }

        private static void DumpTr(Transform t, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            string pad = new string(' ', depth * 2);
            RectTransform rt = t as RectTransform;
            Main.Log(pad + "> " + t.name + (t.gameObject.activeSelf ? "" : " [INACTIVE]")
                   + (rt != null ? ("  rect=" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0")) : ""));

            Component[] cs = t.GetComponents<Component>();
            for (int i = 0; i < cs.Length; i++)
            {
                if (cs[i] == null) { Main.Log(pad + "   . <MISSING SCRIPT>"); continue; }
                if (cs[i] is Transform) continue;
                Main.Log(pad + "   . " + cs[i].GetType().FullName);
            }

            Image img = t.GetComponent<Image>();
            if (img != null)
                Main.Log(pad + "   [Image] sprite=" + (img.sprite == null ? "null" : img.sprite.name)
                       + " type=" + img.type
                       + " border=" + (img.sprite == null ? "-" : img.sprite.border.ToString())
                       + " color=#" + ColorUtility.ToHtmlStringRGBA(img.color)
                       + " shader=" + (img.material == null || img.material.shader == null
                                       ? "null" : img.material.shader.name)
                       + " tex=" + (img.sprite == null || img.sprite.texture == null ? "null"
                                    : img.sprite.texture.name + " " + img.sprite.texture.width
                                      + "x" + img.sprite.texture.height));

            TextMeshProUGUI tm = t.GetComponent<TextMeshProUGUI>();
            if (tm != null)
                Main.Log(pad + "   [TMP] font=" + (tm.font == null ? "null" : tm.font.name)
                       + " mat=" + (tm.fontSharedMaterial == null ? "null" : tm.fontSharedMaterial.name)
                       + " size=" + tm.fontSize + " autoSize=" + tm.enableAutoSizing
                       + "[" + tm.fontSizeMin + "," + tm.fontSizeMax + "]"
                       + " color=#" + ColorUtility.ToHtmlStringRGBA(tm.color)
                       + " align=" + tm.alignment + " overflow=" + tm.overflowMode
                       + " text='" + (tm.text ?? "") + "'");

            OwlcatSelectable sel = t.GetComponent<OwlcatSelectable>();
            if (sel != null) DumpSelectable(sel, pad + "   ");

            for (int i = 0; i < t.childCount; i++) DumpTr(t.GetChild(i), depth + 1, maxDepth);
        }

        private static readonly FieldInfo FiLayers =
            typeof(OwlcatSelectable).GetField("m_CommonLayer",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static void DumpSelectable(OwlcatSelectable sel, string pad)
        {
            Main.Log(pad + "[OwlcatSelectable] " + sel.GetType().Name
                   + " interactable=" + sel.Interactable
                   + " hoverSnd=" + sel.HoverSoundType + " clickSnd=" + sel.ClickSoundType);
            if (FiLayers == null) { Main.Log(pad + "  (m_CommonLayer 反射失败)"); return; }
            System.Collections.IEnumerable layers = FiLayers.GetValue(sel) as System.Collections.IEnumerable;
            if (layers == null) { Main.Log(pad + "  (m_CommonLayer == null)"); return; }
            int idx = 0;
            foreach (object o in layers)
            {
                OwlcatSelectableLayerPart p = o as OwlcatSelectableLayerPart;
                if (p == null) continue;
                Main.Log(pad + "  Layer[" + idx + "] transition=" + p.Transition
                       + " target=" + (p.TargetGraphic == null ? "null" : p.TargetGraphic.gameObject.name)
                       + " cg=" + (p.CanvasGroup == null ? "null" : p.CanvasGroup.gameObject.name));
                if (p.Transition == OwlcatTransition.SpriteSwap)
                {
                    OwlcatSelectableSpriteSwapBlock sw = p.SpriteSwap;
                    Main.Log(pad + "     SpriteSwap  N=" + Sn(sw.normalSprite)
                           + " H=" + Sn(sw.highlightedSprite) + " P=" + Sn(sw.pressedSprite)
                           + " F=" + Sn(sw.focusedSprite) + " D=" + Sn(sw.disabledSprite)
                           + "   ★★★ 这就是三态 sprite 名 ★★★");
                }
                else if (p.Transition == OwlcatTransition.SpriteSwapLegacy)
                {
                    SpriteState ss = p.SpriteState;
                    Main.Log(pad + "     Legacy  H=" + Sn(ss.highlightedSprite)
                           + " P=" + Sn(ss.pressedSprite) + " S=" + Sn(ss.selectedSprite)
                           + " D=" + Sn(ss.disabledSprite) + "  (Normal 用 Image.sprite)");
                }
                else if (p.Transition == OwlcatTransition.ColorTint)
                {
                    ColorBlock cb = p.Colors;
                    Main.Log(pad + "     Colors N=#" + ColorUtility.ToHtmlStringRGBA(cb.normalColor)
                           + " H=#" + ColorUtility.ToHtmlStringRGBA(cb.highlightedColor)
                           + " P=#" + ColorUtility.ToHtmlStringRGBA(cb.pressedColor)
                           + " D=#" + ColorUtility.ToHtmlStringRGBA(cb.disabledColor)
                           + " mult=" + cb.colorMultiplier + " fade=" + cb.fadeDuration);
                }
                idx++;
            }
        }

        private static string Sn(Sprite s) { return s == null ? "-" : s.name; }

        private static string FullPath(Transform t)
        {
            StringBuilder sb = new StringBuilder(t.name);
            Transform p = t.parent; int g = 0;
            while (p != null && g++ < 16) { sb.Insert(0, p.name + "/"); p = p.parent; }
            return sb.ToString();
        }
    }
}
```

---

# 4. `RetinueUI.cs` 的 8 处改动

### 4.1 `Open()`：Canvas 走原版渲染路径（**发白的确切修法**）

把 `RetinueUI.cs:172-175` 的

```csharp
                Canvas c = _root.GetComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = 800;
```

改成（`ApplyVanillaRenderPath` 移到全树建好之后调）：

```csharp
                Canvas c = _root.GetComponent<Canvas>();
                VanillaWidgets.PrepareCanvas(c);       // TMP 需要 TexCoord1
```

然后在 `RebuildUnits();` 之后、`catch` 之前加：

```csharp
                ApplyVanillaRenderPath(c);   // ★ 放最后：此时整棵树已建好，SetLayerRecursive 一次盖到底
```

新增成员（贴进 `RetinueUI` 类体内，**不要**另起 partial 文件）：

```csharp
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

        private static void ApplyVanillaRenderPath(Canvas c)
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
```

在 `RebuildArchetypes()` 和 `RebuildUnits()` 的**末尾**各加一行 `ReapplyLayer();`。

### 4.2 `Stretch` 拆成横/纵（**无文字的根因修法**）

替换 `RetinueUI.cs:478-484`：

```csharp
        private static void Stretch(GameObject go, float pad) { StretchPad(go, pad, pad); }

        /// <summary>
        /// 拉伸铺满父物体，横纵内边距分开给。
        /// ★ 纵向内边距是雷区：TMP 在 Ellipsis/Truncate 下，一旦
        ///   rect.height &lt; fontSize×(ascent−descent)/pointSize，会把**整串**字符丢掉
        ///   （GenerateTextMesh L2003 判定 → L2033 分支 m_characterCount=0，unicode=3(ETX)）。
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

        // 给 VanillaWidgets 用的转发（工厂在同命名空间但不同类）
        internal static void StretchPadPublic(GameObject go, float padX, float padY)
        { StretchPad(go, padX, padY); }
        internal static TextMeshProUGUI MakeLabelPublic(Transform p, string s, float sz,
                                                        Color c, TextAlignmentOptions a)
        { return MakeLabel(p, s, sz, c, a); }
```

### 4.3 `MakeButton`：先试克隆，再回退；padY=0

替换 `RetinueUI.cs:609-635` 整个 `MakeButton`：

```csharp
        private static Button MakeButton(Transform parent, string text, float w, float h, Action onClick)
        {
            // ① 先试原版克隆
            Button v = VanillaWidgets.MakeVanillaButton(parent, text, w, h, onClick);
            if (v != null) return v;

            // ② 回退：程序生成（原逻辑，只改了标签的纵向 padding）
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
            // ★★★ 根因修复：纵向 padding 必须是 0 ★★★
            // 原来 Stretch(label, 6f) 四边各缩 6，h=34 的按钮只剩 22px，
            // 19pt 中文行高约 25~26px 放不下 → TMP 把整串字符清零 → 白框没字。
            StretchPad(label.gameObject, 8f, 0f);

            RectTransform rt = (RectTransform)go.transform;   // ★ 尺寸最后设，锚点已定型
            rt.sizeDelta = new Vector2(w > 0f ? w : 0f, h);
            return b;
        }
```

### 4.4 `SetInteractable`：克隆体走原版 Disabled 态

替换 `RetinueUI.cs` 的 `SetInteractable`：

```csharp
        private static void SetInteractable(Button b, bool on)
        {
            if (b == null) return;
            if (VanillaWidgets.TrySetInteractable(b, on)) return;   // 克隆体：交给 OwlcatButton
            b.interactable = on;
            Image img = b.targetGraphic as Image;
            if (img != null) PaintPanel(img, on ? ButtonTex() : ButtonDimTex(), VanillaSkin.Gold);
            TextMeshProUGUI t = b.GetComponentInChildren<TextMeshProUGUI>();
            if (t != null) t.color = on ? new Color(0.13f, 0.10f, 0.05f, 1f)
                                        : new Color(0.55f, 0.53f, 0.48f, 1f);
        }
```

### 4.5 按钮高度 34 → 38，区块标题框 30 → 34

- `RetinueUI.cs:272` 关闭按钮：`MakeButton(panel.transform, "关闭", 110f, 34f, Close)` → **`38f`**
- `RetinueUI.cs:420 / 426` 改装备 / 招募：`118f, 34f` → **`118f, 38f`**
- `MakeSectionLabel`（`RetinueUI.cs:599-607`）：
  ```csharp
            rt.offsetMin = new Vector2(14f, -40f);   // was -38 → 框高 30，22pt 装不下
            rt.offsetMax = new Vector2(-14f, -6f);   // now 高 34，仍在 topInset=44 内
  ```

### 4.6 `MakeScrollArea` 的 VLG + 行显式给高

`RetinueUI.cs:673` `vlg.childControlHeight = false;` → **`true`**。
（`HorizontalOrVerticalLayoutGroup.GetChildSizes` L240-247：`if (!controlSize) { min = child.sizeDelta[axis]; ... }` —— false 时 VLG 只读 `sizeDelta.y`，`LayoutElement.preferredHeight` **全程无效**。）

同时 `AddUnitRow` 开头补一行，别赌 RectTransform 默认尺寸：

```csharp
            GameObject row = NewUI("Row_" + (name ?? "?"), _unitContent);
            ((RectTransform)row.transform).sizeDelta = new Vector2(0f, 116f);   // ★ 新增
            LayoutElement le = row.AddComponent<LayoutElement>();
            le.minHeight = 116f; le.preferredHeight = 116f;
```

### 4.7 `UiHost`：渲染路径守卫 + **文字自愈守卫**

替换 `RetinueUI.cs:699-707` 的 `UiHost`：

```csharp
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
```

新增 `TextGuard`（贴进 `RetinueUI` 类体）：

```csharp
        /// <summary>
        /// 自愈守卫：布局稳定后实测每个标签**真实渲染了几个字符**。
        /// characterCount==0 而 text 非空 = TMP 因纵向放不下把整串丢了 —— 直接切 Overflow 抢救，
        /// 并把出事的控件打进日志。比反推字体度量(R)可靠，也不需要 TextCoreFontEngineModule 引用。
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
                           + " overflow=" + t.overflowMode + " autoSize=" + t.enableAutoSizing
                           + " @ " + t.transform.parent.name + " -> 已切 Overflow 抢救");
                    t.overflowMode = TextOverflowModes.Overflow;
                    t.ForceMeshUpdate();
                    fixedCount++;
                }
                if (fixedCount == 0) Main.Log("[UI] TextGuard: 全部标签正常渲染");
            }
            catch (Exception e) { Main.LogError("[UI] TextGuard: " + e.Message); }
        }
```

### 4.8 `Shutdown()`：清克隆 holder + 程序生成贴图

在 `RetinueUI.cs` 的 `Shutdown()` 里，`VanillaSkin.Reset();` 之后加：

```csharp
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
```

---

# 5. 延迟开窗：`Deferred.cs` + `RecruitDialog.cs`

新建 `D:\RT_RetinueMod\src\KgdRetinue\Deferred.cs`：

```csharp
using System;
using System.Collections;
using UnityEngine;

namespace KgdRetinue
{
    /// <summary>极小的延帧执行器。</summary>
    public static class Deferred
    {
        private sealed class Runner : MonoBehaviour
        {
            public void Go(int frames, Action a) { StartCoroutine(Co(frames, a)); }
            private IEnumerator Co(int frames, Action a)
            {
                for (int i = 0; i < frames; i++) yield return null;
                try { if (a != null) a(); }
                catch (Exception e) { Main.LogError("[Deferred] 回调异常: " + e); }
            }
        }

        private static Runner _runner;

        private static Runner Get()
        {
            if (_runner == null)
            {
                GameObject go = new GameObject("KgdRetinue_Deferred");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.DontSave;   // 不要 HideAndDontSave：那样场景卸载也回收不了
                _runner = go.AddComponent<Runner>();
            }
            return _runner;
        }

        public static void NextFrames(int frames, Action a)
        {
            try { Get().Go(frames < 1 ? 1 : frames, a); }
            catch (Exception e)
            {
                Main.LogError("[Deferred] 调度失败，改为立即执行: " + e.Message);
                if (a != null) a();
            }
        }

        public static void Shutdown()
        {
            try { if (_runner != null) UnityEngine.Object.Destroy(_runner.gameObject); } catch { }
            _runner = null;
        }
    }
}
```

`RecruitDialog.cs:725` 改：

```csharp
                    if (dc != null) dc.StopDialog();
                    else Main.LogError("[招募对话] 拿不到 DialogController，对话框不会自动关闭。");

                    // StopDialog() 同步派发 IDialogInteractionHandler，且 StopMode(Dialog) 是**延迟生效**的；
                    // 在 SelectAnswer 的 Harmony prefix 里同帧建 UI = 在 EventBus 派发中重入。推迟 2 帧跨过它。
                    Deferred.NextFrames(2, delegate { Main.OpenRecruitUI(null); });
```

**要不要等对话淡出播完？答：不用。**
- 真正的"对话结束"事件是 **`Kingmaker.PubSubSystem.IDialogFinishHandler.HandleDialogFinished(BlueprintDialog dialog, bool success)`**，在 `DialogController.OnStop()`（Code.dll 约 1183 行）抛，比 `StopDialog()` 晚 ≥1 tick。
- 字幕淡出是 `Kingmaker.UI.Common.Animations.FadeAnimator`，`m_DisappearTime = 0.2f`、`SetUpdate(isIndependentUpdate:true)`；`BackgroundBlurWithFade.DisableBlur` 挂在 `FadeAnimator.OnDisappearEvent` 上，所以 blur 要等这 0.2s 播完才关。
- 但改走 UICamera 路径后我们在 order **32000**，原版对话在 1000 段 —— **它在我们下面淡出，不影响观感**。等 0.2s 反而让"点了没反应"更明显。

如果你**一定**要严格对齐语义，可以订阅 `IDialogFinishHandler`（`EventBus` 在已引用的 `RogueTrader.GameCore.dll`，`using Kingmaker.PubSubSystem.Core;`）。但注意 `HandleDialogFinished(dialog, false)` 在"对话拒绝启动"分支（`DialogController` 约 370 行）也会抛，有误触发面。**保守做法就是上面的 2 帧。**

---

# 6. 验证方法：进游戏看什么算通过

### 第 0 步（先跑，5 分钟，只留档不改行为）
从 UMM 面板加个按钮调 `VanillaWidgets.Dump()`，在**游戏内**（不是主菜单）点一次。
看 `C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\KgdRetinue\kgd_log.txt`：

| 看什么 | 通过标准 |
|---|---|
| `=========== ESC MENU DUMP begin ===========` 出现 | ESC 菜单常驻场景成立 → 克隆路线可行 |
| 某层 `★★★ 这就是三态 sprite 名 ★★★` | 得到权威的按钮三态 sprite 名（离线拿不到） |
| 窗体面板那一层的 `rect=WxH` 和名字 | 用来判断 `UseVanillaPanel` 能不能开、要不要手填节点 |
| `[TMP] ... autoSize=True[min,max]` | 若原版按钮开了 autoSize，克隆按钮天然免疫 §5 的裁字问题 |

**若这一步没输出** → ESC 菜单不在场景（见 §8），走次优解。

### ③ 无文字（最先上）
1. 开窗，**关闭 / 改装备 / 招募** 三个按钮上要有中文字。
2. 日志出现 `[UI] TextGuard: 全部标签正常渲染` → **通过**。
3. 若出现 `[UI][文字被裁] '关闭' size=19 rect=110x22 ...` → 说明还有漏网的框高不足，按日志里的 `rect` 值直接抬那一处的高度即可（字已被 Overflow 抢救出来，不会白框）。
4. **决定性 A/B（可选，1 分钟）**：把关闭按钮留在 `34f`，只给它的 label 设 `overflowMode = Overflow`。若 34f 下 Overflow 有字、Ellipsis 没字 → 精确坐实是 `GenerateTextMesh` L2033 那个 switch 分支，而不是"高一点就好了"。

### ② 发白（第二上）
1. 日志必须出现：`[UI] Canvas->ScreenSpaceCamera cam=UICamera layer=5 camMask=32 plane=2765.174 order=32000`
   - **`layer` 不是 5 → 窗口必然完全不显示**，先修这个再看别的。
   - `camMask` 若不是 32，把实际值贴出来（说明我离线读到的 prefab 值需要修正）。
2. **走完整招募对话**触发开窗（不是从 UMM 面板"直接开窗"）：
   - 窗口背景应是**深墨色**，不是浅灰。
   - 金色标题应是**金色**，不是近白。
   - 窗口后面的场景不应整体发白。
3. 从 UMM 面板"直接开窗"（无对话、无 blur），颜色应与上一条**完全一致** —— 两种入口颜色一致 = 修好了。
4. 切区域 / 读档后再开窗，窗口仍可见可点（`TickRenderPathGuard` 生效）。

### ① 原版控件（最后上）
1. 日志 `[VW] 按钮模板 = .../EscMenu/.../OptionsButton`（路径里含 EscMenu）。
2. **鼠标悬停按钮**：应有原版的高亮变化 + **原版音效**（这是保留 `OwlcatButton` 白拿的，程序生成路径没有）。
3. **按下**：应有 pressed 态。
4. 「招募」在未解锁时应显示**原版 Disabled 外观**（不是我们那个灰色块）。
5. 点击必须真的触发招募 —— 若按钮"看着对但点不动"，99% 是嵌套 Canvas 的 `GraphicRaycaster` 被删（白名单已刻意连 `Canvas` 一起删来规避，若仍出现，把日志里 `DumpSurvivors` 的输出发出来）。
6. 关窗再开窗 10 次，不应出现原版界面异常（验证没有污染池化/共享资源）。
7. **禁用 mod**：`KGD_CloneHolder`、`KgdRetinue_Deferred`、`KgdRetinue_UI` 三个 GameObject 都应消失。

---

# 7. 已坐实 vs 需进游戏验证

### ✅ 已由反编译/解包坐实（不必再论证）

| 事实 | 来源 |
|---|---|
| `EscMenuPCView : EscMenuBaseView : ViewBase<EscMenuVM>`，`Initialize()` 就是 `gameObject.SetActive(false)`；10 个 `protected OwlcatButton m_*Button` 字段名全对 | 本次亲自 `ilspycmd -t` 复核 |
| `OwlcatButton.OnLeftClick`、`OwlcatSelectable.Interactable / SetInteractable(bool) / HoverSoundType / ClickSoundType` 全部 public 存在 | 本次复核 |
| `OwlcatSelectable : UIBehaviour`，`DoSetState` 只调 `UIKitSoundManager` + `DoSetCommonParts`，**无 EventBus / 无全屏栈 / 无池化** | 本次复核 |
| `ViewBase<T>` **没有** Awake/OnEnable/OnDestroy → `DestroyImmediate(EscMenuPCView)` 永远走不到 `DestroyViewImplementation()` 的 `RaiseEvent(IFullScreenUIHandler)` | 核验员反编译 |
| `OwlcatSelectable.OnEnable → DoSetState(instant:true)`，`IsActive()` 为 false 时跳过；state=Normal 不播音效 | 本次复核（inactive holder 姿势安全） |
| `OwlcatSelectableLayerPart` 的 `Transition/Colors/SpriteState/SpriteSwap/CanvasGroup/ActiveBlock` 全部 public | 本次复核（Dump 能编译） |
| `MessageBoxPCView.m_AcceptButton` 是 `protected OwlcatButton` | 本次复核 |
| `Owlcat.Runtime.UI.dll` / `UnityEngine.TextCoreFontEngineModule.dll` 存在于 Managed | 本次 `ls` |
| **全游戏 0 个 ScreenSpaceOverlay Canvas**（732 个 Canvas：rm=1 有 8、rm=2 有 724、rm=0 为 **0**） | UnityPy 扫 `Bundles/ui` |
| `UICamera.OnEnable → CameraStackManager.AddCamera(..., CameraStackType.Ui)`；`UICameraClaimer.OnEnable/OnDisable` 设/清 `worldCamera` | 本次复核 |
| 原版根 Canvas：CommonCanvas 1000 / FadeCanvas 1011 / GamepadConnected 1013 / BugReport 5000；最高嵌套 order 30000（设置页 DropdownPanel）、9999（CombatCursorCanvas） | 扫场景 + bundle |
| TMP 纵向裁字链：L1342 `m_marginHeight` → L2003 `if (num44 > num11+0.0001f)` → L2033-2043 `m_characterCount=0; unicode=3u` → L1990 `num6 != 3` 不可见。且 L2424 唯一的 `Push` 被 `num61 < num11` 门控，**证明**首字符时 `m_EllipsisInsertionCandidateStack.Count == 0` | 核验员反编译 |
| `Truncate` 模式同样致命（L2020-24 恢复 L1605 播种的 `(-1,-1)`）；只有 `Overflow` 能在框高不足时活下来 | 核验员反编译 |
| `RectTransform.offsetMin/offsetMax` setter 末态与 pivot 无关 → **锚点/宽度假说被证伪** | 核验员反编译 |
| `HorizontalOrVerticalLayoutGroup.GetChildSizes` L240-247 → `childControlHeight=false` 时 `LayoutElement` 完全无效 | 核验员反编译 |
| `RetinueUI.cs:150` 是 `public static class RetinueUI`（**无 partial**） | 本次 grep |
| `RetinueUI.cs` 全文**零** `.layer` 赋值 → 默认 layer 0 | 本次 grep |
| `DialogController.StopDialog()` 只同步派发 `IDialogInteractionHandler`；`IDialogFinishHandler.HandleDialogFinished` 在 `OnStop()` 抛 | 核验员反编译 |
| `EventBus` 在**已引用**的 `RogueTrader.GameCore.dll`（不是 Code.dll） | 核验员 |

### ⚠️ 需进游戏验证（**动手前必须跑 Dump / 看日志**）

| # | 待验证 | 若不成立怎么办 |
|---|---|---|
| V1 | **`UICamera.cullingMask == 32`**（离线从 `Bundles/blueprint.assets` 读出，未实机确认）。`near=1915 / far=3725 / depth=1 / clearFlags=Depth` 同理 | 日志会打 `camMask=` 实际值。若不是 32，按实际位设 layer；`planeDistance` 已 Clamp 到实际 near/far，不会越界 |
| V2 | **ESC 框到底由哪几张图拼成、能不能拉伸**。`ModalWindow_PaperBackEsc / PaperAquila / PaperTopArmor / PaperBottomArmor` 四件套确实存在于 `sharedassets0`（我复核过），但**没有直接证据**证明 `EscMenuPCView` 引用它们 | 所以 `UseVanillaPanel` **默认 false**。跑 Dump 看清层级后再开；开了若边框错位，用 Dump 里的 rect 手工指定面板节点 |
| V3 | **窗体面板节点的层级路径**。`GuessPanelNode` 是面积启发式，若框由多张平级 Image 拼成（Paper 四件套很可能就是），它会挑中其中一张而不是共同父节点 → 改尺寸后边框错位 | 同 V2。日志会打 `没定位到窗体面板节点` 并自动回退 |
| V4 | **按钮三态的具体 sprite 名**。`Bundles/ui`(1.2GB) 里 prefab 的 sprite 是跨 bundle PPtr，本机无 AssetRipper 级工具解不了 | 不影响动手 —— 我们克隆整个按钮，不需要 sprite 名。`Dump()` 的 `★★★` 那行就是权威答案 |
| V5 | **剥离后到底剩下什么组件**。白名单是反向裁剪，不是穷举；prefab 上可能挂着我没预见的组件 | 第一次跑开 `VanillaWidgets.DumpSurvivors(clone, "btn")`，把幸存清单看一遍。若里面有 `Animator` / 布局相关的，可能删多了导致塌 |
| V6 | **真实字体的行高比 R**。原方案反推 R∈(1.16,1.36]，但那个上界来自"用户没报区块标题坏" —— 那是"没有报告"不是"报告没有"。若字体是思源黑体（R=1.448），30px 的区块标题也是空的 | 所以我**不用 R**，改用 `TextGuard` 实测 `textInfo.characterCount`。这条因此**不再是风险**，只是解释为什么不采信 R |
| V7 | `sortingOrder = 32000` 会压住原版 DropdownPanel(30000) / BugReport(5000) / FadeCanvas(1011)。切区域/读档的全屏淡入淡出**盖不住**我们 | 必须在 `IAreaHandler` 之类时机主动 `Close()`。若你更希望原版模态能压住我们，把 `KgdSortingOrder` 改 **2000** |

### 🐛 顺带发现、本次未修（独立问题，建议单排）

- `VanillaSkin.EnsureFont()`（`RetinueUI.cs:68`）把原版**活体** TMP 的 `fontSharedMaterial` 直接赋给我们。那是**共享**材质，TMP 某些路径会往 shared material 写 `_ScaleRatioA`，可能**反向污染原版文字** —— 和"摘到光晕图"是同一类问题。建议改 `t.fontMaterial = new Material(VanillaSkin.FontMaterial)` 自持一份。
- `MakeScrollArea` 的 `Scroll` 物体**没有 Graphic**，滚轮 raycast 命中后向上冒泡，而列的 Image 是它的**父**物体 → 鼠标停在列表空白处滚不动。加一张 `alpha=0 / raycastTarget=true` 的 Image 到 `Scroll` 上即可。
- `VanillaSkin` 的 `PanelNames/ButtonNames/RowNames` 白名单（`RetinueUI.cs:36-48`）已是**死代码**（`PaintPanel` 全走 `GenTex`），连同 `EnsureSprites` 可整块删掉；`Font/FontMaterial/Gold/Ink/Text/TextDim` 要留给回退路径。

---

# 8. 做不到的事 & 次优解

### 8.1 「ESC 菜单在我们开窗时不在场景里」

**大概率不会发生**，但会：`RootUIContext.ResetUI()` / 读档会 `Destroy(m_CommonView.gameObject)` 再重建，中间有窗口期；主菜单阶段 `CommonPCView` 根本没创建。

**代码已按三级降级处理**（每级都会打日志）：

```
① EscMenuPCView 的 m_OptionsButton / m_LoadButton / …   ← 用户点名的风格模板
      ↓ 拿不到
② MessageBoxPCView.m_AcceptButton                         ← 同样常驻场景 + inactive，
                                                             同一套 Paper/Holo 风格 + OwlcatButton
      ↓ 拿不到
③ 现有的 GenTex 程序生成按钮                              ← 保证不崩、不白屏、不空窗
```

**不做**「盲抓场景里任意 OwlcatButton」—— 那正是上次摘到 `ModalWindow_HoloBorderWindow2` 光晕图的老路。宁可退到 ③。

`_escView` / `_btnTemplate` 的缓存用 Unity 的 `==` 重载判空，被 `Destroy` 后为 true-null，会自动重新查找；`Reset()` 也会清。

### 8.2 窗框：明确说做不到的部分

**离线无法确认 ESC 框的拼法**。`Bundles/ui` 是 1.2 GB UnityFS(LZ4HC/Unity 6000.0.64f1)，prefab 里 sprite 是跨 bundle PPtr，名字在另一个 99 MB 的 `ui_atlaselements_atlasvariant.ui_atlas` 里，本机无 Python UnityPy 之外的 AssetRipper 级工具能跨 bundle 解引用。

**最保守做法（已内置）**：`UseVanillaPanel = false`。
窗框先用现有 `GenTex`（深墨底 + 金边，已经不难看），**按钮用原版克隆** —— 按钮才是交互焦点，观感提升的性价比最高。跑完 Dump 确认层级后再把开关打开。

### 8.3 `Instantiate` 的一个已证伪的担心

我原本怀疑 `Instantiate(src, Holder)` 会因 Canvas 缩放把 scale 烘进 `localScale`。反编译 `UnityEngine.Object`（CoreModule L376-379）确认 `Instantiate(Object, Transform)` 内部就是 `Instantiate(original, parent, instantiateInWorldSpace: false)`，local transform 原样保留。**无缩放污染。**

### 8.4 存档安全

三项全部是**纯 UI 层**：不产生任何 `AssetId`、不写 `player.json` / `party.json`、不碰 `BookEventLog`。`RecruitDialog` 那条改动只是把 `Main.OpenRecruitUI(null)` 推迟 2 帧，`SelectedAnswersAdd` 的既有逻辑一字未动。**不触碰存档腐坏红线。**