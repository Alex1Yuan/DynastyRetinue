using System.Collections.Generic;
using System.Linq;
using Kingmaker.Blueprints.Root.Strings;
using Kingmaker.Code.UI.MVVM.VM.Tooltip.Bricks;
using Kingmaker.Code.UI.MVVM.VM.Tooltip.Templates;
using Kingmaker.Code.UI.MVVM.VM.Vendor;
using Kingmaker.Code.UI.MVVM.View.Tooltip.Bricks;
using Kingmaker.UI.Common;
using Owlcat.Runtime.UI.MVVM;
using Owlcat.Runtime.UI.Utility;
using TMPro;
using UniRx;
using UnityEngine;

namespace Kingmaker.Code.UI.MVVM.View.ServiceWindows.CharacterInfo.Sections.FactionsReputation;

public abstract class CharInfoProfitFactorItemBaseView : ViewBase<ProfitFactorVM>, IWidgetView
{
	[Header("Texts")]
	[SerializeField]
	private TextMeshProUGUI m_Title;

	[SerializeField]
	private TextMeshProUGUI m_EmptyListDesc;

	[SerializeField]
	private TextMeshProUGUI m_ProfitFactorValue;

	[Header("Containers")]
	[SerializeField]
	protected ScrollRectExtended m_ColoniesInfoScroll;

	[SerializeField]
	private GameObject m_EmptyListContainer;

	[Header("Prefabs")]
	[SerializeField]
	private TooltipBrickIconStatValueView m_ColonyInfoPrefab;

	protected TooltipTemplateProfitFactor Tooltip;

	protected AccessibilityTextHelper TextHelper;

	private readonly List<TooltipBrickIconStatValueView> m_BricksList = new List<TooltipBrickIconStatValueView>();

	public MonoBehaviour MonoBehaviour => this;

	protected override void BindViewImplementation()
	{
		if (TextHelper == null)
		{
			TextHelper = new AccessibilityTextHelper(m_Title, m_EmptyListDesc);
		}
		m_Title.text = UIStrings.Instance.ProfitFactorTexts.Title;
		m_EmptyListDesc.text = UIStrings.Instance.ProfitFactorTexts.NoSourcesDesc;
		Tooltip = new TooltipTemplateProfitFactor(base.ViewModel);
		AssembleBricks();
		AddDisposable(base.ViewModel.TotalValue.Subscribe(delegate(float value)
		{
			m_ProfitFactorValue.text = value.ToString();
		}));
		AddDisposable(base.ViewModel.Modifiers.ObserveAdd().Subscribe(delegate
		{
			SetupModifiers();
		}));
		AddDisposable(base.ViewModel.Modifiers.ObserveRemove().Subscribe(delegate
		{
			SetupModifiers();
		}));
		SetupModifiers();
		TextHelper.UpdateTextSize();
	}

	protected override void DestroyViewImplementation()
	{
		m_BricksList.ForEach(delegate(TooltipBrickIconStatValueView brick)
		{
			brick.Unbind();
		});
		m_BricksList.Clear();
		TextHelper.Dispose();
	}

	private void AssembleBricks()
	{
		for (int i = 0; i < m_ColoniesInfoScroll.content.childCount; i++)
		{
			Transform child = m_ColoniesInfoScroll.content.GetChild(i);
			TooltipBrickIconStatValueView component = child.GetComponent<TooltipBrickIconStatValueView>();
			if ((bool)component)
			{
				m_BricksList.Add(component);
				child.gameObject.SetActive(value: false);
			}
			else
			{
				Object.Destroy(child.gameObject);
			}
		}
	}

	private void SetupModifiers()
	{
		IEnumerable<ProfitFactorModifierVM> enumerable = base.ViewModel.Modifiers.Where((ProfitFactorModifierVM mod) => mod.IsNegative);
		List<TooltipBrickIconStatValue> list = (from mod in base.ViewModel.Modifiers.Except(enumerable)
			select TooltipTemplateProfitFactor.GetModBrick(mod, isPositive: true)).ToList();
		list.AddRange(enumerable.Select((ProfitFactorModifierVM mod) => TooltipTemplateProfitFactor.GetModBrick(mod, isPositive: false)));
		m_EmptyListContainer.SetActive(list.Count == 0);
		for (int num = m_BricksList.Count; num < list.Count; num++)
		{
			TooltipBrickIconStatValueView item = Object.Instantiate(m_ColonyInfoPrefab, m_ColoniesInfoScroll.content, worldPositionStays: false);
			m_BricksList.Add(item);
		}
		for (int num2 = m_BricksList.Count; num2 < m_ColoniesInfoScroll.content.childCount; num2++)
		{
			m_ColoniesInfoScroll.content.GetChild(num2).gameObject.SetActive(value: false);
		}
		for (int num3 = 0; num3 < list.Count; num3++)
		{
			m_BricksList[num3].Bind(list[num3].GetVM() as TooltipBrickIconStatValueVM);
			m_BricksList[num3].gameObject.SetActive(value: true);
		}
	}

	public void BindWidgetVM(IViewModel vm)
	{
		Bind(vm as ProfitFactorVM);
	}

	public bool CheckType(IViewModel viewModel)
	{
		return viewModel is ProfitFactorVM;
	}
}
