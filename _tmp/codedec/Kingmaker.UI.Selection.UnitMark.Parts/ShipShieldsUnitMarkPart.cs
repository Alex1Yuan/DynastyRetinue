using System;
using System.Collections.Generic;
using DG.Tweening;
using Kingmaker.SpaceCombat.StarshipLogic.Parts;
using TMPro;
using UnityEngine;

namespace Kingmaker.UI.Selection.UnitMark.Parts;

public class ShipShieldsUnitMarkPart : MonoBehaviour, IDisposable
{
	[Serializable]
	public class ShieldsDecalMaterialSet
	{
		public Material HighShieldsMaterial;

		public Material MediumShieldsMaterial;

		public Material LowShieldsMaterial;
	}

	private class ShieldDecalData
	{
		public MeshRenderer Renderer;

		public int ShieldValue;

		public bool IsReinforced;

		public bool IsDead;

		public bool IsHitHighlighted;

		public ShieldsDecalMaterialSet MaterialSet;

		public TextMeshPro ShieldValueText;
	}

	[SerializeField]
	private bool m_IsDirect;

	[Header("Textures")]
	[SerializeField]
	private ShieldsDecalMaterialSet[] m_ShieldMaterialSets;

	[SerializeField]
	private MeshRenderer[] m_ShieldsDecals;

	[SerializeField]
	private MeshRenderer[] m_ShieldReinforcementDecals;

	[SerializeField]
	private TextMeshPro[] m_ShieldValueTexts;

	private PartStarshipShields m_ShieldsPart;

	private readonly Dictionary<StarshipSectorShieldsType, ShieldDecalData> m_Shields = new Dictionary<StarshipSectorShieldsType, ShieldDecalData>();

	private ShipDecalConfig m_ShipDecalConfig;

	private bool m_ColorsDirty;

	private bool m_IsShieldHitHighlightVisible;

	private Tweener m_HitHighlightTweener;

	public bool IsDirect => m_IsDirect;

	public void Initialize(PartStarshipShields starshipShields, ShipDecalConfig shipDecalConfig)
	{
		m_ShieldsPart = starshipShields;
		m_ShipDecalConfig = shipDecalConfig;
		InitializeShields();
	}

	public void MarkHighlightShieldHit(StarshipSectorShields shield)
	{
		if (m_Shields.TryGetValue(shield.Sector, out var value))
		{
			value.IsHitHighlighted = true;
		}
	}

	public void ClearHighlightShieldHit()
	{
		foreach (KeyValuePair<StarshipSectorShieldsType, ShieldDecalData> shield in m_Shields)
		{
			shield.Deconstruct(out var _, out var value);
			value.IsHitHighlighted = false;
		}
		m_ColorsDirty = true;
	}

	public void SetShieldHitHighlightVisible(bool isVisible)
	{
		m_IsShieldHitHighlightVisible = isVisible;
		m_ColorsDirty = true;
	}

	public void ShowShieldValues(bool isVisible)
	{
		foreach (KeyValuePair<StarshipSectorShieldsType, ShieldDecalData> shield in m_Shields)
		{
			shield.Value.ShieldValueText.gameObject.SetActive(isVisible);
		}
	}

	public void Dispose()
	{
		m_HitHighlightTweener?.Kill();
		m_ShieldsPart = null;
		m_Shields.Clear();
	}

	protected void Update()
	{
		UpdateShieldsState();
	}

	private void InitializeShields()
	{
		for (int i = 0; i < m_ShieldsDecals.Length; i++)
		{
			m_Shields[(StarshipSectorShieldsType)i] = new ShieldDecalData
			{
				Renderer = m_ShieldsDecals[i],
				MaterialSet = m_ShieldMaterialSets[i],
				ShieldValueText = m_ShieldValueTexts[i]
			};
			if (m_ShieldReinforcementDecals.Length != 0)
			{
				m_ShieldReinforcementDecals[i].gameObject.SetActive(m_Shields[(StarshipSectorShieldsType)i].IsReinforced);
			}
		}
	}

	private void UpdateShieldsState()
	{
		bool isAnyShieldHighlighted = IsAnyShieldHighlighted();
		foreach (var (sector, shieldDecalData2) in m_Shields)
		{
			StarshipSectorShields shields = m_ShieldsPart.GetShields(sector);
			if (shieldDecalData2.ShieldValue != shields.Current)
			{
				float capacity = (float)shields.Current / (float)shields.Max;
				SetShieldMaterial(shieldDecalData2, capacity);
				shieldDecalData2.ShieldValue = shields.Current;
				if (shieldDecalData2.ShieldValueText != null)
				{
					shieldDecalData2.ShieldValueText.text = shields.Current.ToString();
				}
			}
			if (shieldDecalData2.IsReinforced != shields.Reinforced)
			{
				shieldDecalData2.IsReinforced = shields.Reinforced;
				m_ColorsDirty = true;
			}
			if (m_ColorsDirty)
			{
				SetShieldColor(shieldDecalData2);
				AnimateHit(shieldDecalData2, isAnyShieldHighlighted);
			}
			int current = shields.Current;
			if (current <= 0)
			{
				if (current == 0 && !shieldDecalData2.IsDead)
				{
					shieldDecalData2.Renderer.gameObject.SetActive(value: false);
					shieldDecalData2.IsDead = true;
				}
			}
			else if (shieldDecalData2.IsDead)
			{
				shieldDecalData2.Renderer.gameObject.SetActive(value: true);
				shieldDecalData2.IsDead = false;
			}
			if (m_ShieldReinforcementDecals.Length != 0)
			{
				m_ShieldReinforcementDecals[(int)shields.Sector].gameObject.SetActive(shields.Reinforced);
			}
		}
		m_ColorsDirty = false;
	}

	private void SetShieldMaterial(ShieldDecalData decalData, float capacity)
	{
		if (capacity >= m_ShipDecalConfig.MediumShieldCapacityThreshold)
		{
			decalData.Renderer.material = decalData.MaterialSet.HighShieldsMaterial;
		}
		else if (capacity >= m_ShipDecalConfig.LowShieldCapacityThreshold)
		{
			decalData.Renderer.material = decalData.MaterialSet.MediumShieldsMaterial;
		}
		else
		{
			decalData.Renderer.material = decalData.MaterialSet.LowShieldsMaterial;
		}
	}

	private void SetShieldColor(ShieldDecalData decalData)
	{
		ShipDecalConfig.ShieldDecalColorSet shieldDecalColorSet = ((decalData.IsHitHighlighted && m_IsShieldHitHighlightVisible) ? m_ShipDecalConfig.HighlightHitShieldColor : m_ShipDecalConfig.DefaultShieldColor);
		decalData.Renderer.material.color = shieldDecalColorSet.ShieldColor;
	}

	private void AnimateHit(ShieldDecalData decalData, bool isAnyShieldHighlighted)
	{
		if (decalData.IsHitHighlighted && m_IsShieldHitHighlightVisible)
		{
			m_HitHighlightTweener?.Kill();
			m_HitHighlightTweener = decalData.Renderer.material.DOFade(m_ShipDecalConfig.HitHighlightBlinkAlpha, 0.4f).SetLoops(-1, LoopType.Yoyo).SetUpdate(isIndependentUpdate: true);
			m_HitHighlightTweener.Play();
		}
		else if (!isAnyShieldHighlighted || !m_IsShieldHitHighlightVisible)
		{
			m_HitHighlightTweener?.Kill();
		}
	}

	private bool IsAnyShieldHighlighted()
	{
		foreach (KeyValuePair<StarshipSectorShieldsType, ShieldDecalData> shield in m_Shields)
		{
			shield.Deconstruct(out var _, out var value);
			if (value.IsHitHighlighted)
			{
				return true;
			}
		}
		return false;
	}
}
