using Kingmaker.Code.UI.MVVM.VM.Tooltip.Bricks;
using Kingmaker.Code.UI.MVVM.View.ServiceWindows.CharacterInfo.Sections.LevelClassScores.AbilityScores;
using UnityEngine;

namespace Kingmaker.Code.UI.MVVM.View.Tooltip.Bricks;

public class TooltipBrickAbilityScoresView : TooltipBaseBrickView<TooltipBrickAbilityScoresVM>
{
	[SerializeField]
	protected CharInfoAbilityScoresBlockBaseView m_AbilityScoresBlockView;

	protected override void BindViewImplementation()
	{
		m_AbilityScoresBlockView.Bind(base.ViewModel.AbilityScoresBlock);
	}
}
