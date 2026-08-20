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

namespace DynastyRetinue.UI
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
                if (label != null)
                {
                    label.gameObject.SetActive(true);
                    label.text = text;
                    // ★ 实测「关闭」被 TextGuard 抢救过：rect=102x31、overflow=Ellipsis、18pt。
                    //   TMP 在 Ellipsis/Truncate 下，纵向放不下时会把**整串**字符清零（不是截断）。
                    //   按钮标签就一两个词，Overflow 没有任何副作用，直接在源头关掉这个雷。
                    label.overflowMode = TextOverflowModes.Overflow;
                }
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
        // ★ 权威产出：把 ESC 菜单真实结构打进 dynasty_log.txt
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
