using System.Collections.Generic;
using Kingmaker.Code.UI.MVVM.VM.Tooltip.Bricks;
using Owlcat.Runtime.UI.Tooltips;
using UnityEngine;

namespace Kingmaker.Code.UI.MVVM.View.Tooltip.Bricks;

public class TooltipBrickSeparatorView : TooltipBaseBrickView<TooltipBrickSeparatorVM>
{
	[SerializeField]
	private List<GameObject> m_SeparatorTypes = new List<GameObject>();

	[SerializeField]
	private GameObject m_AugmentHeaderSeparator;

	protected override void BindViewImplementation()
	{
		if (base.ViewModel.IsAugmentHeader)
		{
			m_SeparatorTypes.ForEach(delegate(GameObject s)
			{
				s.SetActive(value: false);
			});
			m_AugmentHeaderSeparator.SetActive(value: true);
			return;
		}
		m_AugmentHeaderSeparator.SetActive(value: false);
		for (int num = 0; num < m_SeparatorTypes.Count; num++)
		{
			m_SeparatorTypes[num].SetActive(base.ViewModel.Type == (TooltipBrickElementType)num);
		}
	}
}
