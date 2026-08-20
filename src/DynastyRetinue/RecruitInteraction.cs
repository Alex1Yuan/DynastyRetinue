using System;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Mechanics.Entities;
using Kingmaker.UnitLogic.Commands.Base;
using Kingmaker.UnitLogic.Interaction;

namespace DynastyRetinue
{
    /// <summary>
    /// 招募入口：挂在船上某个 NPC 身上的可点击交互。
    ///
    /// 为什么用 IUnitInteraction 而不是自建对话蓝图：
    ///   UnitPartInteractions.m_Interactions 是 `private readonly List` 且**没有 [JsonProperty]**
    ///   （UnitPartInteractions.cs:21-22），而存档序列化器用 OptInContractResolver ——
    ///   没标记的成员一律不写。所以这个交互**不进存档**，卸载 mod 后没有任何残留可以解析失败。
    ///   对比之下自建 BlueprintDialog/Answer 虽然也基本安全（对话历史只存 GUID 字符串），
    ///   但要新增 AssetId；而自建 NPC 的 BlueprintUnit 是**致命**的。
    ///
    /// 体验上是完全原生的：光标反馈、单位高亮、点击派发走的都是同一套
    ///（ClickUnitHandler.cs:192 / CursorController.cs:291 / AbstractUnitEntityView.cs:916）。
    ///
    /// 三条已验证的约束：
    ///   1. 只在**非战斗**时触发（ClickUnitHandler.cs:193）
    ///   2. 目标的 view 需要有 Collider
    ///   3. 目标不能是可直控的队伍成员（SurfaceMainInputLayer.cs:342-345 会整个跳过）
    /// </summary>
    public sealed class RecruitInteraction : IUnitInteraction
    {
        /// <summary>交互距离（格）。走近才触发，和原版对话一致。</summary>
        public int Distance { get { return 2; } }

        /// <summary>true = 需要走过去；对话类交互都是 true。</summary>
        public bool IsApproach { get { return true; } }

        public float ApproachCooldown { get { return 0f; } }

        /// <summary>优先让主角去交互，而不是随便哪个队友。</summary>
        public bool MainPlayerPreferred { get { return true; } }

        public bool IsAvailable(BaseUnitEntity initiator, AbstractUnitEntity target)
        {
            try { return Main.Enabled && Main.Settings != null && Main.Settings.NpcRecruitEntry; }
            catch { return false; }
        }

        public AbstractUnitCommand.ResultType Interact(BaseUnitEntity user, AbstractUnitEntity target)
        {
            try
            {
                string who = "?";
                try { who = target != null && target.Blueprint != null ? target.Blueprint.name : "?"; } catch { }
                Main.Log("[招募] 交互触发，NPC=" + who);
                Main.OpenRecruitUI(target as BaseUnitEntity);
                return AbstractUnitCommand.ResultType.Success;
            }
            catch (Exception e)
            {
                Main.LogError("[招募] 交互失败: " + e.Message);
                return AbstractUnitCommand.ResultType.Fail;
            }
        }
    }
}
