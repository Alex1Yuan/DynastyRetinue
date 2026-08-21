using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Kingmaker;
using Kingmaker.Enums;

namespace DynastyRetinue.UI
{
    /// <summary>
    /// 船坞窗口（uGUI，和招募窗口同一套仿原版皮肤）。
    ///
    /// 复用 RetinueUI 里已经调好的那些构件 —— 字体、九宫格素材、按钮克隆、
    /// 渲染路径、以及那几个踩过坑才对的细节：
    ///   · TMP 在 Ellipsis/Truncate 下 rect 高度不够会把**整串**字符清零 ⇒ 文字纵向 padding 恒为 0
    ///   · Canvas 要走 VanillaWidgets.PrepareCanvas（TMP 需要 TexCoord1）
    ///   · ApplyVanillaRenderPath 必须**最后**调，此时整棵树建好才能一次盖到底
    /// 这些都不重新发明，直接调 internal 版本。
    ///
    /// 内容：按分档分组列出目录里全部船体，标出差价 / 当前座舰 / 可用性；
    /// 底部一条"还原为原样（退还 N 废料）"和顾问的答复。
    ///
    /// ★可用性★ ShipModelBundleHold.WhyUnusable() 会真的试加载并带回原因，
    /// 只被 DLC 引用的船体在没装 DLC 时会被置灰并写明原因，而不是点了才发现换不了。
    /// 探测带缓存，每个 prefab 只真加载一次。
    /// </summary>
    public static class ShipYardUI
    {
        private const string RootName = "KGD_ShipYardUI";

        private static GameObject _root;
        private static Transform _content;
        private static TextMeshProUGUI _header, _reply;
        private static Button _revertBtn;
        private static string _replyText = "";

        public static bool IsOpen { get { return _root != null; } }

        /// <summary>对话入口开窗。曾经想"对话结束自动关"，行不通 —— 见 RetinueUI.OpenFromDialog 的注释。</summary>
        public static void OpenFromDialog() { Open(); }

        public static void Open()
        {
            if (IsOpen) { Refresh(); return; }
            try
            {
                _replyText = "";
                _root = new GameObject(RootName, typeof(RectTransform), typeof(Canvas),
                                       typeof(CanvasScaler), typeof(GraphicRaycaster));
                UnityEngine.Object.DontDestroyOnLoad(_root);

                Canvas c = _root.GetComponent<Canvas>();
                VanillaWidgets.PrepareCanvas(c);

                CanvasScaler sc = _root.GetComponent<CanvasScaler>();
                sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                sc.referenceResolution = new Vector2(1920f, 1080f);
                sc.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                sc.matchWidthOrHeight = 0.5f;

                RetinueUI.BuildClickBlocker(_root.transform);
                RetinueUI.EnsureEventSystem(_root.transform);
                BuildFrame(_root.transform);
                Refresh();

                RetinueUI.ApplyVanillaRenderPath(c);   // ★放最后★
            }
            catch (Exception e) { Main.LogError("[船坞UI] 开窗失败: " + e); Close(); }
        }

        public static void Close()
        {
            try { if (_root != null) UnityEngine.Object.Destroy(_root); }
            catch (Exception e) { Main.LogError("[船坞UI] 关窗异常: " + e.Message); }
            _root = null; _content = null; _header = null; _reply = null; _revertBtn = null;
        }

        public static void Shutdown()
        {
            Close();
            try
            {
                foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
                    if (go != null && go.name == RootName && go.scene.IsValid())
                        UnityEngine.Object.Destroy(go);
            }
            catch { }
        }

        // ------------------------------------------------------------ 框架

        private static void BuildFrame(Transform parent)
        {
            GameObject panel = RetinueUI.NewUI("Panel", parent);
            Image bg = panel.AddComponent<Image>();
            RetinueUI.PaintPanel(bg, RetinueUI.PanelTex(), VanillaSkin.Ink);
            RectTransform prt = (RectTransform)panel.transform;
            prt.anchorMin = new Vector2(0.5f, 0.5f);
            prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot     = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(980f, 720f);
            prt.anchoredPosition = Vector2.zero;

            var title = RetinueUI.MakeLabelPublic(panel.transform, L.T("船坞 · 座舰改装"), 30f,
                                                  VanillaSkin.Gold, TextAlignmentOptions.Left);
            var trt = (RectTransform)title.transform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(1f, 1f);
            trt.pivot = new Vector2(0.5f, 1f);
            trt.offsetMin = new Vector2(32f, -70f); trt.offsetMax = new Vector2(-40f, -24f);

            _header = RetinueUI.MakeLabelPublic(panel.transform, "", 18f,
                                                VanillaSkin.Text, TextAlignmentOptions.Left);
            var hrt = (RectTransform)_header.transform;
            hrt.anchorMin = new Vector2(0f, 1f); hrt.anchorMax = new Vector2(1f, 1f);
            hrt.pivot = new Vector2(0.5f, 1f);
            hrt.offsetMin = new Vector2(32f, -104f); hrt.offsetMax = new Vector2(-40f, -72f);
            _header.overflowMode = TextOverflowModes.Overflow;   // 见 RetinueUI 里同款说明

            _content = RetinueUI.MakeScrollArea(panel.transform, 112f);
            // ★给底部留位★ MakeScrollArea 默认把滚动区拉到面板底边上方 10px，
            // 而按钮在 y=108、顾问答复在 y=24..96 —— 不留白就会叠在一起（玩家实测）。
            // content.parent = Viewport，再上一层才是 Scroll 本体。
            try
            {
                var scrollRT = (RectTransform)_content.parent.parent;
                scrollRT.offsetMin = new Vector2(scrollRT.offsetMin.x, 160f);
            }
            catch (Exception e) { Main.LogError("[船坞UI] 调整滚动区底边失败: " + e.Message); }

            // 底部：还原 + 关闭
            _revertBtn = RetinueUI.MakeButton(panel.transform, L.T("还原为原样"), 300f, 40f, OnRevert);
            var rrt = (RectTransform)_revertBtn.transform;
            rrt.anchorMin = new Vector2(0f, 0f); rrt.anchorMax = new Vector2(0f, 0f);
            rrt.pivot = new Vector2(0f, 0f);
            rrt.anchoredPosition = new Vector2(32f, 108f);

            Button close = RetinueUI.MakeButton(panel.transform, L.T("关闭"), 140f, 40f, Close);
            var crt = (RectTransform)close.transform;
            crt.anchorMin = new Vector2(1f, 0f); crt.anchorMax = new Vector2(1f, 0f);
            crt.pivot = new Vector2(1f, 0f);
            crt.anchoredPosition = new Vector2(-40f, 108f);

            _reply = RetinueUI.MakeLabelPublic(panel.transform, "", 18f,
                                               VanillaSkin.Text, TextAlignmentOptions.TopLeft);
            var qrt = (RectTransform)_reply.transform;
            qrt.anchorMin = new Vector2(0f, 0f); qrt.anchorMax = new Vector2(1f, 0f);
            qrt.pivot = new Vector2(0.5f, 0f);
            qrt.offsetMin = new Vector2(32f, 24f); qrt.offsetMax = new Vector2(-40f, 96f);
            _reply.overflowMode = TextOverflowModes.Overflow;
        }

        // ------------------------------------------------------------ 刷新

        public static void Refresh()
        {
            if (!IsOpen) return;
            try
            {
                Size cur = ShipDialog.Current(), orig = ShipDialog.OriginalSize();

                if (_header != null)
                    _header.text = L.F("当前座舰　<color=#c6a24e>{0}</color>　　原本　{1}　　废料　<color=#c6a24e>{2}</color>　　<size=15>升级只补差价，还原全额退还</size>",
                                       ShipDialog.SizeName(cur), ShipDialog.SizeName(orig), ShipDialog.Scrap());

                if (_revertBtn != null)
                {
                    int refund = ShipDialog.RefundOnRevert();
                    var t = _revertBtn.GetComponentInChildren<TextMeshProUGUI>();
                    if (t != null)
                        t.text = cur != orig
                            ? L.F("还原为{0}（退 {1}）", ShipDialog.SizeName(orig), refund)
                            : L.T("已是原样");
                    RetinueUI.SetInteractable(_revertBtn, cur != orig);
                }

                if (_reply != null)
                    _reply.text = string.IsNullOrEmpty(_replyText)
                        ? L.T("<color=#8d867a><i>「有什么需要，尽管吩咐。」</i></color>")
                        : L.F("<color=#c6a24e>高阶顾问：</color>{0}", _replyText);

                RebuildRows();
            }
            catch (Exception e) { Main.LogError("[船坞UI] 刷新失败: " + e); }
        }

        private static void RebuildRows()
        {
            if (_content == null) return;
            for (int i = _content.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);

            Size cur = ShipDialog.Current();
            // ★按"买到手的档"分组，不是按船体的原生档★
            // 大巡那档用的是放大后的 Dictator，而 Dictator 的 m.Tier 是 Cruiser_2x4 ——
            // 按 m.Tier 分组会把它排进巡洋舰那一栏，大巡栏则空着。
            Size lastTier = (Size)(-999);
            foreach (var o in ShipDialog.Offers())
            {
                if (o.Tier != lastTier)
                {
                    lastTier = o.Tier;
                    RetinueUI.MakeSectionLabel(_content,
                        L.F("{0}　总价 {1} 废料", ShipDialog.SizeName(o.Tier), ShipDialog.TotalFor(o.Tier)));
                }
                MakeRow(o, cur);
            }
        }

        private static void MakeRow(ShipDialog.Offer o, Size cur)
        {
            var m = o.Model;
            string why = null;
            try { why = ShipModelBundleHold.WhyUnusable(m.PrefabAssetId); }
            catch (Exception e) { why = e.Message; }
            bool usable = string.IsNullOrEmpty(why);

            bool isCurrent = string.Equals(StarshipViewTool.CurrentPrefab, m.PrefabAssetId,
                                           StringComparison.OrdinalIgnoreCase);
            bool supported = o.Supported;
            int price = ShipDialog.PriceTo(o.Tier);
            bool afford = price <= 0 || ShipDialog.Scrap() >= price;   // 负数是退款，永远点得动

            GameObject row = RetinueUI.NewUI("Row_" + m.Hull, _content);
            Image rbg = row.AddComponent<Image>();
            RetinueUI.PaintPanel(rbg, VanillaSkin.RowSprite, VanillaSkin.RowBg);
            var rt = (RectTransform)row.transform;
            rt.sizeDelta = new Vector2(0f, 56f);
            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 56f; le.preferredHeight = 56f;

            var name = RetinueUI.MakeLabelPublic(row.transform,
                (isCurrent ? "<color=#7ec87e>▶ </color>" : "") + m.HullName
                + "　<size=14><color=#8d867a>" + m.Faction
                + (m.DlcOnlyReferenced ? " · DLC" : "") + "</color></size>",
                20f, (usable && supported) ? VanillaSkin.Text : VanillaSkin.TextDim,
                TextAlignmentOptions.Left);
            var nrt = (RectTransform)name.transform;
            nrt.anchorMin = new Vector2(0f, 0f); nrt.anchorMax = new Vector2(1f, 1f);
            nrt.offsetMin = new Vector2(16f, 0f); nrt.offsetMax = new Vector2(-360f, 0f);
            name.overflowMode = TextOverflowModes.Overflow;

            if (!usable)
            {
                var w = RetinueUI.MakeLabelPublic(row.transform, L.F("<color=#b06060>不可用：{0}</color>", why),
                                                  16f, VanillaSkin.TextDim, TextAlignmentOptions.Right);
                var wrt = (RectTransform)w.transform;
                wrt.anchorMin = new Vector2(1f, 0f); wrt.anchorMax = new Vector2(1f, 1f);
                wrt.pivot = new Vector2(1f, 0.5f);
                wrt.sizeDelta = new Vector2(340f, 0f);
                wrt.anchoredPosition = new Vector2(-16f, 0f);
                w.overflowMode = TextOverflowModes.Overflow;
                return;
            }

            if (!supported)
            {
                var w = RetinueUI.MakeLabelPublic(row.transform,
                    "<color=#8d867a>" + ShipDialog.UnsupportedHint + "</color>",
                    15f, VanillaSkin.TextDim, TextAlignmentOptions.Right);
                var wrt = (RectTransform)w.transform;
                wrt.anchorMin = new Vector2(1f, 0f); wrt.anchorMax = new Vector2(1f, 1f);
                wrt.pivot = new Vector2(1f, 0.5f);
                wrt.sizeDelta = new Vector2(360f, 0f);
                wrt.anchoredPosition = new Vector2(-16f, 0f);
                w.overflowMode = TextOverflowModes.Overflow;
                return;
            }

            if (isCurrent && o.Tier == cur)
            {
                var w = RetinueUI.MakeLabelPublic(row.transform, L.T("<color=#7ec87e>当前座舰</color>"),
                                                  18f, VanillaSkin.Text, TextAlignmentOptions.Right);
                var wrt = (RectTransform)w.transform;
                wrt.anchorMin = new Vector2(1f, 0f); wrt.anchorMax = new Vector2(1f, 1f);
                wrt.pivot = new Vector2(1f, 0.5f);
                wrt.sizeDelta = new Vector2(300f, 0f);
                wrt.anchoredPosition = new Vector2(-16f, 0f);
                return;
            }

            var pl = RetinueUI.MakeLabelPublic(row.transform,
                ShipDialog.PriceLabel(o.Tier),
                18f, afford ? VanillaSkin.Text : VanillaSkin.TextDim, TextAlignmentOptions.Right);
            var prt = (RectTransform)pl.transform;
            prt.anchorMin = new Vector2(1f, 0f); prt.anchorMax = new Vector2(1f, 1f);
            prt.pivot = new Vector2(1f, 0.5f);
            prt.sizeDelta = new Vector2(150f, 0f);
            prt.anchoredPosition = new Vector2(-176f, 0f);

            var offer = o;   // 闭包捕获：别在 lambda 里用循环变量
            Button b = RetinueUI.MakeButton(row.transform, L.T("改装"), 140f, 34f,
                                            delegate { OnBuy(offer); });
            var brt = (RectTransform)b.transform;
            brt.anchorMin = new Vector2(1f, 0.5f); brt.anchorMax = new Vector2(1f, 0.5f);
            brt.pivot = new Vector2(1f, 0.5f);
            brt.anchoredPosition = new Vector2(-16f, 0f);
            RetinueUI.SetInteractable(b, afford);
        }

        // ------------------------------------------------------------ 动作

        // ★换船同样必须两台一起做★
        //   船体档位会改 State.Size、护盾、护甲、格子占位 —— 全是同步状态。
        //   只有一台改了，帧末对哈希必然不一致。
        //   参数取 PrefabAssetId：那是资源 id，跟着 mod 数据走，两台能各自查回同一条目。
        private static void OnBuy(ShipDialog.Offer o)
        {
            if (o == null || o.Model == null) return;
            CoopCommand.Send("refit",
                             ((int)o.Tier).ToString(System.Globalization.CultureInfo.InvariantCulture),
                             o.Model.PrefabAssetId ?? "");
        }

        private static void OnRevert()
        {
            CoopCommand.Send("shiprevert");
        }

        /// <summary>指令送达后真正换船 —— 两台机器都会走到这里。</summary>
        internal static void ExecuteRefit(int tier, string prefabAssetId)
        {
            var m = ShipModelCatalog.ByPrefab(prefabAssetId);
            if (m == null)
            {
                Main.LogError("[船坞] 找不到船模 " + prefabAssetId + " —— 两边的 mod 数据可能不是同一版。");
                return;
            }
            _replyText = ShipDialog.BuyOffer((Size)tier, m);
            // 换船会重建 view，延两帧再刷，让分档/价格读到新值
            if (IsOpen) { Refresh(); Deferred.NextFrames(2, Refresh); }
        }

        /// <summary>指令送达后真正还原 —— 两台机器都会走到这里。</summary>
        internal static void ExecuteRevert()
        {
            _replyText = ShipDialog.Revert();
            if (IsOpen) { Refresh(); Deferred.NextFrames(2, Refresh); }
        }

    }
}
