using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Stats.Base;
using Kingmaker.Networking.Serialization;
using Kingmaker.QA;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility.DotNetExtensions;
using Newtonsoft.Json;
using StateHasher.Core;
using StateHasher.Core.Hashers;
using UnityEngine;

namespace Kingmaker.EntitySystem.Stats;

public class StatsContainer : IHashable
{
	private static readonly StatType?[] BaseTypes;

	public StatType BaseStatType;

	[NotNull]
	[ItemCanBeNull]
	[GameStateInclude]
	private readonly ModifiableValue[] m_Container;

	private MechanicEntity m_Owner;

	[JsonProperty]
	[UsedImplicitly]
	[GameStateIgnore("This is a runtime adapter for m_Container")]
	private Dictionary<StatType, ModifiableValue> ContainerConverter
	{
		get
		{
			Dictionary<StatType, ModifiableValue> dictionary = new Dictionary<StatType, ModifiableValue>();
			bool flag = false;
			ModifiableValue[] container = m_Container;
			foreach (ModifiableValue modifiableValue in container)
			{
				if (modifiableValue == null)
				{
					continue;
				}
				if (modifiableValue.Type == StatType.Unknown)
				{
					if (!flag)
					{
						PFLog.Default.ErrorWithReport("Stat with Unknown type! Something very bad happened with savable state: try to reproduce it and report to programmers.");
						flag = true;
					}
				}
				else if (dictionary.Get(modifiableValue.Type) != null)
				{
					PFLog.Default.ErrorWithReport($"Duplicated stat type: {modifiableValue.Type}! " + "Something very bad happened with savable state: try to reproduce it and report to programmers.");
				}
				else
				{
					dictionary.Add(modifiableValue.Type, modifiableValue);
				}
			}
			return dictionary;
		}
		set
		{
			foreach (var (statType2, modifiableValue2) in value)
			{
				int num = (int)statType2;
				if (num >= 0 && num < m_Container.Length)
				{
					m_Container[num] = modifiableValue2;
				}
			}
		}
	}

	public MechanicEntity Owner => m_Owner;

	public IEnumerable<ModifiableValue> AllStats => m_Container.Where((ModifiableValue i) => i != null);

	static StatsContainer()
	{
		BaseTypes = new StatType?[EnumUtils.GetMaxValue<StatType>() + 1];
		RegisterBaseTypes();
	}

	[JsonConstructor]
	private StatsContainer()
	{
		int num = EnumUtils.GetMaxValue<StatType>() + 1;
		m_Container = new ModifiableValue[num];
	}

	public StatsContainer(MechanicEntity owner)
		: this()
	{
		m_Owner = owner;
	}

	private static void RegisterBaseTypes()
	{
		RegisterBaseValue(StatType.SkillAthletics, StatType.WarhammerStrength);
		RegisterBaseValue(StatType.SkillCoercion, StatType.WarhammerFellowship);
		RegisterBaseValue(StatType.SkillPersuasion, StatType.WarhammerFellowship);
		RegisterBaseValue(StatType.SkillAwareness, StatType.WarhammerPerception);
		RegisterBaseValue(StatType.SkillCarouse, StatType.WarhammerToughness);
		RegisterBaseValue(StatType.SkillDemolition, StatType.WarhammerAgility);
		RegisterBaseValue(StatType.SkillTechUse, StatType.WarhammerIntelligence);
		RegisterBaseValue(StatType.SkillCommerce, StatType.WarhammerFellowship);
		RegisterBaseValue(StatType.SkillLogic, StatType.WarhammerIntelligence);
		RegisterBaseValue(StatType.SkillLoreWarp, StatType.WarhammerIntelligence);
		RegisterBaseValue(StatType.SkillLoreImperium, StatType.WarhammerIntelligence);
		RegisterBaseValue(StatType.SkillMedicae, StatType.WarhammerIntelligence);
		RegisterBaseValue(StatType.SkillLoreXenos, StatType.WarhammerIntelligence);
		RegisterBaseValue(StatType.CheckBluff, StatType.SkillPersuasion);
		RegisterBaseValue(StatType.CheckDiplomacy, StatType.SkillPersuasion);
		RegisterBaseValue(StatType.CheckIntimidate, StatType.SkillCoercion);
		RegisterBaseValue(StatType.SaveFortitude, StatType.WarhammerToughness);
		RegisterBaseValue(StatType.SaveReflex, StatType.WarhammerAgility);
		RegisterBaseValue(StatType.SaveWill, StatType.WarhammerWillpower);
		RegisterBaseValue(StatType.Initiative, StatType.WarhammerAgility);
		RegisterBaseValue(StatType.Resolve, StatType.WarhammerFellowship);
		RegisterBaseValue(StatType.HitPoints, StatType.WarhammerToughness);
		static void RegisterBaseValue(StatType type, StatType baseType)
		{
			if (BaseTypes[(int)type].HasValue)
			{
				throw new Exception($"Base Type for {type} is already registered ({BaseTypes[(int)type].Value})");
			}
			BaseTypes[(int)type] = baseType;
		}
	}

	public TModifiableValue Register<TModifiableValue>(StatType type) where TModifiableValue : ModifiableValue, new()
	{
		ModifiableValue modifiableValue = m_Container.Get((int)type);
		if (modifiableValue != null)
		{
			return (modifiableValue as TModifiableValue) ?? throw new Exception($"{Owner} modifiable value {type} already registered with different object type " + "(requested type: " + typeof(TModifiableValue).Name + ", existing type: " + modifiableValue.GetType().Name + ")");
		}
		TModifiableValue val = new TModifiableValue();
		m_Container[(int)type] = val;
		val.Initialize(this, type);
		InitializeBaseValue(val);
		return val;
	}

	private void InitializeBaseValue(ModifiableValue value)
	{
		StatBaseValue statBaseValue = Owner.GetStatBaseValue(value.OriginalType);
		if (value is ModifiableValueAttributeStat modifiableValueAttributeStat && statBaseValue.Enabled != modifiableValueAttributeStat.Enabled)
		{
			if (statBaseValue.Enabled)
			{
				modifiableValueAttributeStat.ReleaseDisabled();
			}
			else
			{
				modifiableValueAttributeStat.RetainDisabled();
			}
		}
		if (!(value is ModifiableValueAttributeStat { Enabled: false }))
		{
			value.BaseValue = statBaseValue.Value;
		}
		value.UpdateValue();
	}

	public void ReinitializeBaseValues()
	{
		ModifiableValue[] container = m_Container;
		foreach (ModifiableValue modifiableValue in container)
		{
			if (modifiableValue != null)
			{
				InitializeBaseValue(modifiableValue);
			}
		}
	}

	public ModifiableValue Register(StatType type)
	{
		return Register<ModifiableValue>(type);
	}

	public ModifiableValueAttributeStat RegisterAttribute(StatType type)
	{
		return Register<ModifiableValueAttributeStat>(type);
	}

	public ModifiableValueSkill RegisterSkill(StatType type)
	{
		return Register<ModifiableValueSkill>(type);
	}

	public StatType GetBaseStatType(StatType stat)
	{
		IReadOnlyDictionary<StatType, List<PartStatsContainer.StatOverrideData>> overridenBaseStat = Owner.GetRequired<PartStatsContainer>().OverridenBaseStat;
		if (overridenBaseStat != null && overridenBaseStat.ContainsKey(stat))
		{
			foreach (PartStatsContainer.StatOverrideData item in overridenBaseStat[stat])
			{
				if (item.CanBeUsed(Owner))
				{
					return item.OverrideType.TryGetOverride(Owner);
				}
			}
		}
		if (!BaseTypes[(int)stat].HasValue)
		{
			return StatType.Unknown;
		}
		return BaseTypes[(int)stat].Value.TryGetOverride(Owner);
	}

	[NotNull]
	public TModifiableValue RequireBaseValue<TModifiableValue>(StatType type) where TModifiableValue : ModifiableValue
	{
		TModifiableValue val = TryGetOverridenBaseType();
		if (val == null)
		{
			StatType? statType = BaseTypes.Get((int)type);
			if (statType.HasValue)
			{
				StatType valueOrDefault = statType.GetValueOrDefault();
				val = GetStat<TModifiableValue>(valueOrDefault);
			}
			else
			{
				val = null;
			}
			if (val == null)
			{
				throw new Exception($"Can't find base stat for {type}");
			}
		}
		return val;
		TModifiableValue TryGetOverridenBaseType()
		{
			IReadOnlyDictionary<StatType, List<PartStatsContainer.StatOverrideData>> overridenBaseStat = Owner.GetRequired<PartStatsContainer>().OverridenBaseStat;
			if (overridenBaseStat != null && overridenBaseStat.ContainsKey(type))
			{
				foreach (PartStatsContainer.StatOverrideData item in overridenBaseStat[type])
				{
					if (item.CanBeUsed(Owner))
					{
						return GetStatOptional<TModifiableValue>(item.OverrideType);
					}
				}
			}
			return null;
		}
	}

	public void PrePostLoad(MechanicEntity owner)
	{
		m_Owner = owner;
		for (int i = 0; i < m_Container.Length; i++)
		{
			StatType type = (StatType)i;
			m_Container[i]?.PrePostLoad(this, type);
		}
	}

	public void PostLoad()
	{
		foreach (ModifiableValue allStat in AllStats)
		{
			allStat.PostLoad();
		}
		foreach (ModifiableValue allStat2 in AllStats)
		{
			try
			{
				allStat2.UpdateValue();
			}
			catch (Exception ex)
			{
				PFLog.Default.Exception(ex);
			}
		}
	}

	public void CleanupModifiers()
	{
		AllStats.ForEach(delegate(ModifiableValue s)
		{
			s.CleanupModifiers();
		});
	}

	public void AddClassSkill(StatType stat)
	{
	}

	[CanBeNull]
	public ModifiableValue GetStat(StatType type, bool canBeNull = false)
	{
		ModifiableValue modifiableValue = m_Container.Get((int)type);
		if (modifiableValue == null && !canBeNull)
		{
			throw new Exception($"{Owner} doesn't have stat {type}");
		}
		return modifiableValue;
	}

	[NotNull]
	public TModifiableValue GetStat<TModifiableValue>(StatType type) where TModifiableValue : ModifiableValue
	{
		return (TModifiableValue)GetStat(type.TryGetOverride(Owner));
	}

	[NotNull]
	public ModifiableValueAttributeStat GetAttribute(StatType type)
	{
		return GetStat<ModifiableValueAttributeStat>(type);
	}

	[NotNull]
	public ModifiableValueSkill GetSkill(StatType type)
	{
		return GetStat<ModifiableValueSkill>(type);
	}

	[CanBeNull]
	public ModifiableValue GetStatOptional(StatType type)
	{
		int index = (int)type.TryGetOverride(Owner);
		return m_Container.Get(index);
	}

	[CanBeNull]
	public TModifiableValue GetStatOptional<TModifiableValue>(StatType type) where TModifiableValue : ModifiableValue
	{
		return GetStatOptional(type) as TModifiableValue;
	}

	[CanBeNull]
	public ModifiableValueAttributeStat GetAttributeOptional(StatType type)
	{
		return GetStatOptional<ModifiableValueAttributeStat>(type);
	}

	[CanBeNull]
	public ModifiableValueSkill GetSkillOptional(StatType type)
	{
		return GetStatOptional<ModifiableValueSkill>(type);
	}

	public virtual Hash128 GetHash128()
	{
		Hash128 result = default(Hash128);
		ModifiableValue[] container = m_Container;
		if (container != null)
		{
			for (int i = 0; i < container.Length; i++)
			{
				Hash128 val = ClassHasher<ModifiableValue>.GetHash128(container[i]);
				result.Append(ref val);
			}
		}
		return result;
	}
}
