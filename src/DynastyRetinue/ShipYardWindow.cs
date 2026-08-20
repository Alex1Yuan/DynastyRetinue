using System;
using System.Collections.Generic;
using UnityEngine;
using Kingmaker;
using Kingmaker.Enums;

namespace DynastyRetinue
{
    /// <summary>
    /// 船坞窗口：列出所有可换的船体、标价、当场改装 / 还原退款。
    ///
    /// 由对话里那条「（船坞）关于座舰的改装事宜……」打开，**对话不关闭** ——
    /// 关掉窗口就回到顾问面前，顾问的答复显示在窗口下方，
    /// 而不是把玩家一脚踢出对话（v0.38.0 就是那样，玩家提了）。
    ///
    /// ★可用性检查★ 有些船体只被 DLC 蓝图引用，玩家没装 DLC 时 bundle 里可能根本没有。
    /// ShipModelBundleHold.WhyUnusable() 会真的去试着加载并把原因带回来 ——
    /// 不可用的行**置灰并写明原因**，而不是让玩家点了才发现换不了。
    /// 这个探测有缓存，每个 prefab 只真加载一次。
    /// </summary>
    public static class ShipYardWindow
    {
        private static GameObject _host;
        private static Drawer _drawer;

        public static void Open()
        {
            try
            {
                if (_host == null)
                {
                    _host = new GameObject("DynastyRetinue.ShipYardWindow");
                    UnityEngine.Object.DontDestroyOnLoad(_host);
                    _drawer = _host.AddComponent<Drawer>();
                }
                _drawer.Show();
            }
            catch (Exception e) { Main.LogError("[船坞] 开窗失败: " + e); }
        }

        public static void Shutdown()
        {
            try { if (_host != null) UnityEngine.Object.Destroy(_host); }
            catch { }
            _host = null; _drawer = null;
        }

        private sealed class Drawer : MonoBehaviour
        {
            private bool _open;
            private Rect _rect = new Rect(160, 90, 720, 560);
            private Vector2 _scroll;
            private string _reply = "";        // 顾问的答复
            private static GUIStyle _sReply, _sHead, _sDim;

            public void Show()
            {
                _open = true;
                _reply = "";
                // 每次开窗刷新一次可用性缓存的判断口径（缓存本身在 BundleHold 里，不重复加载）
            }

            private void OnGUI()
            {
                if (!_open) return;
                EnsureStyles();
                _rect = GUI.Window(0x4B475921, _rect, Body, L.T("船坞 · 座舰改装"));
            }

            private static void EnsureStyles()
            {
                if (_sReply != null) return;
                // TextAnchor / FontStyle 在 UnityEngine.TextRenderingModule 里，csproj 没引用，
                // 直接写枚举名会 CS0012。改用 richText 标签达到同样效果，不引新程序集。
                _sReply = new GUIStyle(GUI.skin.box) { wordWrap = true, richText = true,
                                                       padding = new RectOffset(8, 8, 6, 6) };
                _sHead = new GUIStyle(GUI.skin.label) { richText = true };
                _sDim  = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = true };
                _sDim.normal.textColor = new Color(0.62f, 0.62f, 0.62f);
            }

            private void Body(int id)
            {
                var cur  = ShipDialog.Current();
                var orig = ShipDialog.OriginalSize();

                GUILayout.Space(4);
                GUILayout.Label(L.F("当前座舰：<b>{0}</b>　　原本：{1}　　废料：<b>{2}</b>",
                                    ShipDialog.SizeName(cur), ShipDialog.SizeName(orig), ShipDialog.Scrap()), _sHead);
                GUILayout.Label(L.T("升级只补差价 —— 已经花过的不重复收。还原按当前档全额退还。"), _sDim);
                GUILayout.Space(6);

                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(340));

                Size lastTier = (Size)(-999);
                foreach (var o in ShipDialog.Offers())
                {
                    if (o.Tier != lastTier)
                    {
                        lastTier = o.Tier;
                        GUILayout.Space(6);
                        GUILayout.Label(L.F("── {0}　总价 {1} 废料 ──",
                                            ShipDialog.SizeName(o.Tier), ShipDialog.TotalFor(o.Tier)), _sHead);
                    }
                    Row(o, cur);
                }
                GUILayout.EndScrollView();

                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                int refund = ShipDialog.RefundOnRevert();
                bool canRevert = cur != orig;
                GUI.enabled = canRevert;
                if (GUILayout.Button(canRevert
                        ? L.F("还原为原样（{0}，退还 {1} 废料）", ShipDialog.SizeName(orig), refund)
                        : L.F("已经是原样（{0}）", ShipDialog.SizeName(orig)), GUILayout.Height(26)))
                    _reply = ShipDialog.Revert();
                GUI.enabled = true;
                if (GUILayout.Button(L.T("关闭"), GUILayout.Width(90), GUILayout.Height(26))) _open = false;
                GUILayout.EndHorizontal();

                GUILayout.Space(6);
                GUILayout.Label(string.IsNullOrEmpty(_reply)
                    ? L.T("<i>「有什么需要，尽管吩咐。」</i>")
                    : L.F("<b>高阶顾问：</b>{0}", _reply), _sReply, GUILayout.MinHeight(58));

                GUI.DragWindow(new Rect(0, 0, 10000, 22));
            }

            private void Row(ShipDialog.Offer o, Size cur)
            {
                var m = o.Model;
                // ★可用性：真的去试加载★ 只被 DLC 引用的船体，没装 DLC 时 bundle 里可能没有。
                // WhyUnusable 带缓存，每个 prefab 只真加载一次。
                string why = null;
                try { why = ShipModelBundleHold.WhyUnusable(m.PrefabAssetId); } catch (Exception e) { why = e.Message; }
                bool usable = string.IsNullOrEmpty(why);

                bool isCurrent = string.Equals(StarshipViewTool.CurrentPrefab, m.PrefabAssetId,
                                               StringComparison.OrdinalIgnoreCase);
                int price = ShipDialog.PriceTo(o.Tier);
                bool afford = price <= 0 || ShipDialog.Scrap() >= price;

                GUILayout.BeginHorizontal();
                GUILayout.Label((isCurrent ? "▶ " : "   ") + m.HullName
                              + "　<size=11>" + m.Faction + (m.DlcOnlyReferenced ? " · DLC" : "") + "</size>",
                              GUILayout.Width(330));

                if (!usable)
                {
                    GUILayout.Label(L.F("<color=#b06060>不可用：{0}</color>", why), _sDim);
                }
                else if (isCurrent && o.Tier == cur)
                {
                    GUILayout.Label(L.T("<color=#80c880>当前座舰</color>"), GUILayout.Width(110));
                    GUILayout.FlexibleSpace();
                }
                else
                {
                    GUILayout.Label(ShipDialog.PriceLabel(o.Tier), GUILayout.Width(110));
                    GUI.enabled = afford;
                    if (GUILayout.Button(afford ? L.T("改装") : L.T("废料不足"), GUILayout.Width(90)))
                    {
                        // 目标档由船体决定；先设档再换模由 ApplyModelAtTier 内部保证顺序
                        _reply = BuyModel(o);
                    }
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }

            /// <summary>换船逻辑与 uGUI 版共用一份，别分叉。</summary>
            private static string BuyModel(ShipDialog.Offer o) { return ShipDialog.BuyOffer(o.Tier, o.Model); }

        }
    }
}
