using System.Collections.Generic;
using System.Linq;
using Kingmaker.Code.UI.MVVM.VM.Tooltip.Utils;
using Kingmaker.Code.UI.MVVM.View.InfoWindow.Console;
using Kingmaker.Code.UI.MVVM.View.Tooltip.Console.Bricks;
using Owlcat.Runtime.UI.ConsoleTools;
using Owlcat.Runtime.UI.ConsoleTools.NavigationTool;
using Owlcat.Runtime.UI.ConsoleTools.NavigationTool.TMPLinkNavigation;
using Owlcat.Runtime.UI.Controls.Button;
using Owlcat.Runtime.UI.Utility;
using Owlcat.Runtime.UniRx;
using UnityEngine;

namespace Kingmaker.Code.UI.MVVM.View.Tooltip.Bricks;

public class TooltipBrickPetInfoConsoleView : TooltipBrickPetInfoView, IConsoleTooltipBrick
{
	[SerializeField]
	private OwlcatMultiButton m_FirstFocus;

	[SerializeField]
	private OwlcatMultiButton m_SecondFocus;

	private GridConsoleNavigationBehaviour m_NavigationBehaviour;

	protected override void BindViewImplementation()
	{
		base.BindViewImplementation();
		CreateNavigationBehaviour();
	}

	public IConsoleEntity GetConsoleEntity()
	{
		return m_NavigationBehaviour;
	}

	private void CreateNavigationBehaviour()
	{
		m_NavigationBehaviour = new GridConsoleNavigationBehaviour();
		List<TooltipBrickIconAndNameConsoleView> list = m_AbilitiesList.Entries.Select((IWidgetView e) => e as TooltipBrickIconAndNameConsoleView).ToList();
		for (int num = 0; num < list.Count / 2; num++)
		{
			if (num * 2 + 1 < list.Count)
			{
				m_NavigationBehaviour.AddRow<IConsoleEntity>(list[num * 2].GetConsoleEntity(), list[num * 2 + 1].GetConsoleEntity());
			}
			else
			{
				m_NavigationBehaviour.AddRow<IConsoleEntity>(list[num * 2].GetConsoleEntity());
			}
		}
		List<IFloatConsoleNavigationEntity> entities = TMPLinkNavigationGenerator.GenerateEntityList(m_NarrativeDescription, m_FirstFocus, m_SecondFocus, null, OnLinkFocused, TooltipHelper.GetLinkTooltipTemplate);
		m_NavigationBehaviour.AddRow(entities);
	}

	private void OnLinkFocused(string key)
	{
		DelayedInvoker.InvokeInFrames(delegate
		{
			m_FirstFocus.ShowLinkTooltip(key);
		}, 1);
	}
}
