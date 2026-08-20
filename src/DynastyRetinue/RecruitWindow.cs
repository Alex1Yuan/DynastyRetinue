using System;
using System.Collections.Generic;
using UnityEngine;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;

namespace DynastyRetinue
{
    /// <summary>
    /// 游戏内招募窗口。
    ///
    /// 为什么自己画而不是用原生对话：原生对话要新建 BlueprintDialog/Cue/Answer，
    /// 那是新增 AssetId；而且真正的价值（选分型、看数量上限、确认花费）用列表比对话树清楚得多。
    /// 触发点是原生的（走 IUnitInteraction，光标/高亮/点击都是游戏自己那套），
    /// 只有这个面板是我们画的。
    ///
    /// UMM 的 modEntry.OnGUI 只在 mod 管理器窗口里渲染，游戏内画不出来，
    /// 所以挂一个自己的 MonoBehaviour。游戏是 Mono 不是 IL2CPP，可以直接 new 组件。
    /// </summary>
    public static class RecruitWindow
    {
        private static GameObject _host;
        private static Drawer _drawer;

        public static bool IsOpen { get { return _drawer != null && _drawer.Show; } }

        public static void Open(BaseUnitEntity npc)
        {
            try
            {
                if (_host == null)
                {
                    _host = new GameObject("DynastyRetinue.RecruitWindow");
                    UnityEngine.Object.DontDestroyOnLoad(_host);
                    _drawer = _host.AddComponent<Drawer>();
                }
                _drawer.Npc = npc;
                _drawer.Show = true;
            }
            catch (Exception e) { Main.LogError("[招募] 开窗失败: " + e.Message); }
        }

        public static void Close() { if (_drawer != null) _drawer.Show = false; }

        /// <summary>禁用 mod 时把宿主一并销毁，不留残留 GameObject。</summary>
        public static void Shutdown()
        {
            try
            {
                if (_host != null) UnityEngine.Object.Destroy(_host);
                _host = null; _drawer = null;
            }
            catch { }
        }

        private sealed class Drawer : MonoBehaviour
        {
            public bool Show;
            public BaseUnitEntity Npc;
            private Rect _rect = new Rect(120, 120, 520, 420);
            private Vector2 _scroll;

            private void OnGUI()
            {
                if (!Show || !Main.Enabled) return;
                // 标题写清楚是谁 —— 它跟 UMM 用的是同一套 IMGUI，默认皮肤长得一模一样，
                // 实测用户会以为点出来的是 UMM 菜单。
                _rect = GUILayout.Window(0x4B674452, _rect, Body, L.T("卫队招募 — DynastyRetinue"));
                GUI.BringWindowToFront(0x4B674452);
            }

            private void Body(int id)
            {
                try
                {
                    var archs = Archetypes.All;
                    if (archs == null || archs.Length == 0)
                    {
                        GUILayout.Label(L.T("没有可用分型 —— archetypes.json 没载入？"));
                    }
                    else
                    {
                        int cur = 0;
                        try { cur = RetinueRegistry.Count; } catch { }
                        GUILayout.Label(L.F("在册卫兵 {0} 名。选择要招募的分型：", cur));
                        GUILayout.Space(6);

                        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(300));
                        for (int i = 0; i < archs.Length; i++)
                        {
                            var a = archs[i];
                            GUILayout.BeginHorizontal("box");
                            GUILayout.BeginVertical();
                            GUILayout.Label("<b>" + a.Name + "</b>");
                            var ed = GearTool.NextElite(i);
                            // ★ 灰按钮必须给理由 ★ 之前只置灰不解释，看起来像坏了
                            // 灰色标签整条（含 <color> 标签）交给译者，别把标签拆出来拼 —— 拆了就成片段
                            string why = null;
                            if (ed == null)
                            {
                                if (a.Elites == null || a.Elites.Length == 0)
                                    why = L.T("<color=#aaaaaa>本分型没有配精英</color>");
                                else if (!GearTool.EliteUnlocked(i))
                                    why = L.T("<color=#aaaaaa>精英未解锁 —— 需先有本路线的卫兵练到 T3 职业（面板可勾「无视 T3 解锁条件」）</color>");
                                else
                                    why = L.T("<color=#aaaaaa>本分型精英已招满（面板可勾「解除精英数量上限」）</color>");
                            }
                            GUILayout.Label(ed != null ? L.F("下一个精英: {0}", ed.Name) : why);
                            GUILayout.EndVertical();

                            if (GUILayout.Button(L.T("招募 普通"), GUILayout.Width(90)))
                                Recruit(i, null);
                            GUI.enabled = ed != null;
                            if (GUILayout.Button(L.T("招募 精英"), GUILayout.Width(90)))
                                Recruit(i, ed);
                            GUI.enabled = true;
                            GUILayout.EndHorizontal();
                        }
                        GUILayout.EndScrollView();
                    }

                    GUILayout.Space(6);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(L.T("遣散全部"), GUILayout.Width(100)))
                    {
                        try { RetinueRegistry.DismissAll(); } catch (Exception e) { Main.LogError(e.Message); }
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(L.T("关闭"), GUILayout.Width(80))) Show = false;
                    GUILayout.EndHorizontal();
                }
                catch (Exception e) { GUILayout.Label(L.F("窗口异常: {0}", e.Message)); }
                GUI.DragWindow(new Rect(0, 0, 10000, 20));
            }

            private void Recruit(int archIndex, ChainProbe.EliteDef elite)
            {
                try
                {
                    var g = RetinueTest.SpawnOne(archIndex, elite, false, elite == null);
                    Main.Log(g != null
                        ? "[招募] 成功: " + (elite != null ? elite.Name : "普通卫兵")
                        : "[招募] 未生成（可能受数量上限或解锁条件限制，看日志）");
                }
                catch (Exception e) { Main.LogError("[招募] 失败: " + e.Message); }
            }
        }
    }
}
