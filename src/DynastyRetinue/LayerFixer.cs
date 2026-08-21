using TMPro;
using UnityEngine;

namespace DynastyRetinue.UI
{
    /// <summary>
    /// 把 TMP **事后**新建的子物体拉到和宿主同一层。
    ///
    /// ★要解决的问题★
    ///   游戏里没有任何一个 TMP 字体自带汉字（实测 ScreenFont / PaperFont /
    ///   HeaderFont / HeaderFont_Digital 一律「自身 0/55、含 fallback 55/55」），
    ///   所以界面上每个汉字都由 **fallback** 渲染。而 TMP 的 fallback 不画在主网格上 ——
    ///   它会给标签**新建 TMP_SubMeshUI 子物体**，每个 fallback 字体一个。
    ///
    ///   我们的窗口抄了原版渲染路径（ScreenSpaceCamera + UICamera，cullingMask 只含
    ///   layer 5），而这些子物体出生时是 layer 0，UICamera 收不到 ——
    ///   于是文字隐形，只剩下行/徽章那层在 layer 5 的背景贴图，
    ///   看上去就是一个个和文字等宽的纯色块。
    ///
    /// ★为什么用 OnTransformChildrenChanged 而不是定时扫★
    ///   子网格的创建时机散落在窗口整个生命周期里（首次布局、状态条刷新、
    ///   切分型重建右栏、文本变化……），任何固定长度的时间窗口都盖不全 ——
    ///   1.0.35 只补了开窗那几帧，结果只有标题好了，其余照旧。
    ///   而 Unity 在**子物体被添加/移除时**恰好会回调这个方法，
    ///   正是子网格出生的那一刻。事件驱动，零轮询，也不用去 patch TMP 内部。
    ///
    /// ★为什么不递归★
    ///   TMP 的子网格是标签的**直接子物体**，没有更深的层级。
    ///   只扫一层，回调本身就是 O(子物体数)，通常是 0~3 个。
    /// </summary>
    internal sealed class LayerFixer : MonoBehaviour
    {
        private void OnTransformChildrenChanged()
        {
            int layer = gameObject.layer;
            var t = transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                var go = child.gameObject;
                if (go.layer != layer) go.layer = layer;

                // ★不要碰子网格的材质★（1.0.38 撤回 1.0.37 的做法）
                //
                //   1.0.37 在这里写过 `sub.sharedMaterial = sub.fontAsset.material`，
                //   想法是"用 fallback 字体自己的原生材质，参数必然自洽"。
                //   实机日志推翻了前提：正常的「卫队招募」和方块的「分型」
                //   字体、材质、_GradientScale、图集尺寸、层**全部一致** ——
                //   参数从来就没错过。
                //
                //   而那行赋值本身有害：fontAsset.material 是该字体**唯一**的共享材质，
                //   强行指过去等于让所有标签共用同一个材质对象。TMP 会把
                //   `_ScaleRatioA` 这类**跟字号相关**的值写到材质上，共用就意味着
                //   最后写的那个赢 —— 字号不同的标签互相打架，正好是
                //   "大字号的标题正常、小字号的条目糊成块"这个症状。
                //
                //   所以这里只管层，材质交回 TMP 自己的 MaterialManager。

            }
        }
    }
}
