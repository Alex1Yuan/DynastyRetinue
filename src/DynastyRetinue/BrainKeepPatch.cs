using System;
using HarmonyLib;
using Kingmaker.AI.Blueprints;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic;

namespace DynastyRetinue
{
    /// <summary>
    /// 守住给卫兵配的 brain，别被原版各处的「换脑」重新盖掉。
    ///
    /// ================= 这个 bug 让好几轮实验作废 =================
    /// 现象：给分型换了 brain，日志明明写着设置成功，过一会儿再看又变回单位蓝图自带的。
    /// 于是"换了 brain 没变化"被误读成"这个 brain 选得不好"，连着换了三次，
    /// 每次都在测一个**已经被还原掉的改动**。
    ///
    /// ★关键对照★（实机日志）
    ///   普通那批：生成 → 打了一仗 → Dump ⇒ 连射/灵能已回退成原生 brain
    ///   精英那批：生成 → 6 秒后 Dump      ⇒ 十个全没回退
    /// 一个只在战斗中触发的 bug，用"生成完立刻看"的方法永远抓不到。
    ///
    /// ================= 两条回退路径，都汇合到 SetBrain =================
    /// A. PartUnitBrain.OnAttachOrPostLoad()（PartUnitBrain.cs:298-303）
    ///    **无条件** SetBrain(Blueprint.DefaultBrain)，由 EntityPart 的 Attach 和 PostLoad 调用。
    ///    ⇒ brain 虽然带 [JsonProperty] 进存档，**读档后立刻被盖回蓝图默认值**，存了等于没存。
    ///
    /// B. WarhammerSetBrain（ContextAction，WarhammerSetBrain.cs:26-36）
    ///    SetBrain(Blueprint.DefaultBrain) 或 AlternativeBrains[i]，由单位自带的
    ///    React 类 feature 经 TurnBasedModeEventsTrigger 驱动，触发口有六个：
    ///    战斗开始/结束、回合开始/结束、单位回合开始/结束。
    ///    ★判据是「单位蓝图有没有带 WarhammerSetBrain 的 React feature」★
    ///    连射(Sororitas)和灵能(Inquisitor)的单位各挂 3 个 WarhammerSetBrain，
    ///    近战/狙击/军官三个单位一个都没有 —— 这就是"只有那两条回退"的原因，
    ///    和 brain 本身、DLC 归属、GUID 解析都无关。
    ///
    /// 两条路径都调 PartUnitBrain.SetBrain，所以补在这里一次堵死
    /// （另含 AiOverrideBrain 的三处，虽然当前单位没挂）。
    ///
    /// ================= 为什么是「改写入参」而不是「拦下不执行」 =================
    /// SetBrain 内部第二句是 UpdateBehaviourTree()（PartUnitBrain.cs:185），
    /// 而那是 BehaviourTree 的**唯一构造点**（:188-191）。返回 false 会让
    /// BehaviourTree 保持 null，随后 Init() / Tick() 直接 NRE ——
    /// 而 :301 那次调用恰恰是首次构造，拦掉等于让卫兵根本没有行为树。
    /// 所以：把参数换成我们要的 brain，然后**照常放行**。
    ///
    /// 也不要用 postfix 再调一次 SetBrain —— 那会自递归，得加重入锁，
    /// 还白跑一次 UpdateBehaviourTree。
    ///
    /// ================= 开销 =================
    /// SetBrain 是事件驱动的，**不在帧循环里**（每场战斗数次）。
    /// ★不要改成"每帧检查并重设"★ 那正是这个 mod 之前卡死过的形状。
    /// </summary>
    [HarmonyPatch(typeof(PartUnitBrain), "SetBrain")]
    public static class BrainKeepPatch
    {
        private static bool _busy;

        private static void Prefix(PartUnitBrain __instance, ref BlueprintBrainBase brain)
        {
            if (_busy) return;
            try
            {
                _busy = true;

                var u = __instance != null ? __instance.Owner as BaseUnitEntity : null;
                if (u == null || !RetinueRegistry.IsGuard(u)) return;

                string want = DesiredBrainId(u);
                if (string.IsNullOrEmpty(want)) return;

                // 已经是想要的就别动 —— 省掉一次蓝图解析
                if (brain != null && string.Equals(brain.AssetGuid.ToString(), want,
                        StringComparison.OrdinalIgnoreCase)) return;

                var bp = ResourcesLibrary.TryGetBlueprint<BlueprintBrainBase>(want);
                if (bp == null) return;      // 解析不到就放行原值，总比没有行为树强

                if (Main.Settings != null && Main.Settings.WatchMomentum)
                    Main.Log("[brain] 挡下一次换脑：" + (brain != null ? brain.name : "null")
                           + " → 保持 " + bp.name);
                brain = bp;
            }
            catch { }        // 补丁自身出错绝不能影响原版流程
            finally { _busy = false; }
        }

        /// <summary>
        /// 这名卫兵**应该**用哪个 brain。精英优先用自己的，没配就用分型的。
        /// 与 RetinueTest 的 c2 段同一套判据 —— 那边负责主动套，这里负责别被冲掉。
        /// </summary>
        private static string DesiredBrainId(BaseUnitEntity u)
        {
            try
            {
                int ai = RetinueRegistry.ArchetypeOf(u);
                if (ai < 0) return null;                 // Attach 期间还没入册，放行
                var a = Archetypes.Get(ai);
                if (a == null) return null;

                try
                {
                    var ed = GearTool.EliteDefOf(u, a);
                    if (ed != null && !string.IsNullOrEmpty(ed.BrainId)) return ed.BrainId;
                }
                catch { }
                return a.BrainId;
            }
            catch { return null; }
        }
    }
}
