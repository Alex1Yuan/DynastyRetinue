using System;
using System.Linq;
using Kingmaker.Controllers.Combat;
using Kingmaker.ElementsSystem.ContextData;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.EntitySystem.Entities.Base;
using Kingmaker.EntitySystem.Stats.Base;
using Kingmaker.Items;
using Kingmaker.Mechanics.Entities;
using Kingmaker.QA;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.RuleSystem.Rules.Damage;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility.DotNetExtensions;

namespace Kingmaker.EntitySystem.Properties;

public static class EntityPropertyExtension
{
	private static readonly int MaxValue;

	private static readonly Func<Entity, int?>[] Getters;

	static EntityPropertyExtension()
	{
		MaxValue = EnumUtils.GetMaxValue<EntityProperty>();
		Getters = new Func<Entity, int?>[MaxValue];
		Getter(EntityProperty.None, (Entity _) => 0);
		Getter(EntityProperty.BallisticSkill, (Entity e) => Stat(e, StatType.WarhammerBallisticSkill));
		Getter(EntityProperty.WeaponSkill, (Entity e) => Stat(e, StatType.WarhammerWeaponSkill));
		Getter(EntityProperty.Strength, (Entity e) => Stat(e, StatType.WarhammerStrength));
		Getter(EntityProperty.Toughness, (Entity e) => Stat(e, StatType.WarhammerToughness));
		Getter(EntityProperty.Agility, (Entity e) => Stat(e, StatType.WarhammerAgility));
		Getter(EntityProperty.Intelligence, (Entity e) => Stat(e, StatType.WarhammerIntelligence));
		Getter(EntityProperty.Willpower, (Entity e) => Stat(e, StatType.WarhammerWillpower));
		Getter(EntityProperty.Perception, (Entity e) => Stat(e, StatType.WarhammerPerception));
		Getter(EntityProperty.Fellowship, (Entity e) => Stat(e, StatType.WarhammerFellowship));
		Getter(EntityProperty.BallisticSkillBonus, (Entity e) => StatBonus(e, StatType.WarhammerBallisticSkill));
		Getter(EntityProperty.WeaponSkillBonus, (Entity e) => StatBonus(e, StatType.WarhammerWeaponSkill));
		Getter(EntityProperty.StrengthBonus, (Entity e) => StatBonus(e, StatType.WarhammerStrength));
		Getter(EntityProperty.ToughnessBonus, (Entity e) => StatBonus(e, StatType.WarhammerToughness));
		Getter(EntityProperty.AgilityBonus, (Entity e) => StatBonus(e, StatType.WarhammerAgility));
		Getter(EntityProperty.IntelligenceBonus, (Entity e) => StatBonus(e, StatType.WarhammerIntelligence));
		Getter(EntityProperty.WillpowerBonus, (Entity e) => StatBonus(e, StatType.WarhammerWillpower));
		Getter(EntityProperty.PerceptionBonus, (Entity e) => StatBonus(e, StatType.WarhammerPerception));
		Getter(EntityProperty.FellowshipBonus, (Entity e) => StatBonus(e, StatType.WarhammerFellowship));
		Getter(EntityProperty.Resolve, (Entity e) => Stat(e, StatType.Resolve));
		Getter(EntityProperty.Wounds, (Entity e) => Stat(e, StatType.HitPoints));
		Getter(EntityProperty.InitialAPBlue, (Entity e) => Stat(e, StatType.WarhammerInitialAPBlue));
		Getter(EntityProperty.InitialAPYellow, (Entity e) => Stat(e, StatType.WarhammerInitialAPYellow));
		Getter(EntityProperty.CurrentWeaponRateOfFire, GetCurrentWeaponRateOfFire);
		Getter(EntityProperty.EnemiesAdjacent, GetEnemiesAdjacent);
		Getter(EntityProperty.CurrentAPBlue, (Entity e) => (int)(e.GetOptional<PartUnitCombatState>()?.ActionPointsBlue ?? 0f));
		Getter(EntityProperty.CurrentAPYellow, (Entity e) => e.GetOptional<PartUnitCombatState>()?.ActionPointsYellow ?? 0);
		Getter(EntityProperty.SkillAthletics, (Entity e) => Stat(e, StatType.SkillAthletics));
		Getter(EntityProperty.SkillAwareness, (Entity e) => Stat(e, StatType.SkillAwareness));
		Getter(EntityProperty.SkillCarouse, (Entity e) => Stat(e, StatType.SkillCarouse));
		Getter(EntityProperty.SkillPersuasion, (Entity e) => Stat(e, StatType.SkillPersuasion));
		Getter(EntityProperty.SkillDemolition, (Entity e) => Stat(e, StatType.SkillDemolition));
		Getter(EntityProperty.SkillCoercion, (Entity e) => Stat(e, StatType.SkillCoercion));
		Getter(EntityProperty.SkillMedicae, (Entity e) => Stat(e, StatType.SkillMedicae));
		Getter(EntityProperty.SkillLoreXenos, (Entity e) => Stat(e, StatType.SkillLoreXenos));
		Getter(EntityProperty.SkillLoreWarp, (Entity e) => Stat(e, StatType.SkillLoreWarp));
		Getter(EntityProperty.SkillLoreImperium, (Entity e) => Stat(e, StatType.SkillLoreImperium));
		Getter(EntityProperty.SkillTechUse, (Entity e) => Stat(e, StatType.SkillTechUse));
		Getter(EntityProperty.SkillCommerce, (Entity e) => Stat(e, StatType.SkillCommerce));
		Getter(EntityProperty.SkillLogic, (Entity e) => Stat(e, StatType.SkillLogic));
		Getter(EntityProperty.PsyRating, (Entity e) => Stat(e, StatType.PsyRating));
		Getter(EntityProperty.Absorption, GetAbsorption);
		Getter(EntityProperty.Deflection, GetDeflection);
		Getter(EntityProperty.ArmourFore, (Entity e) => Stat(e, StatType.ArmourFore));
		Getter(EntityProperty.ArmourPort, (Entity e) => Stat(e, StatType.ArmourPort));
		Getter(EntityProperty.ArmourStarboard, (Entity e) => Stat(e, StatType.ArmourStarboard));
		Getter(EntityProperty.ArmourAft, (Entity e) => Stat(e, StatType.ArmourAft));
		Getter(EntityProperty.Inertia, (Entity e) => Stat(e, StatType.Inertia));
		Getter(EntityProperty.Evasion, (Entity e) => Stat(e, StatType.Evasion));
		Getter(EntityProperty.Morale, (Entity e) => Stat(e, StatType.Morale));
		Getter(EntityProperty.Crew, (Entity e) => Stat(e, StatType.Crew));
		Getter(EntityProperty.TurretRating, (Entity e) => Stat(e, StatType.TurretRating));
		Getter(EntityProperty.TurretRadius, (Entity e) => Stat(e, StatType.TurretRadius));
		Getter(EntityProperty.MilitaryRating, (Entity e) => Stat(e, StatType.MilitaryRating));
		static void Getter(EntityProperty propertyName, Func<Entity, int?> getter)
		{
			Getters[(int)propertyName] = getter;
		}
		static int? Stat(Entity entity, StatType type)
		{
			UnitPartStatsOverride optional = entity.GetOptional<UnitPartStatsOverride>();
			if (optional != null && optional.TryGetOverride(type, out var value))
			{
				return value;
			}
			return entity.GetOptional<PartStatsContainer>()?.GetStatOptional(type);
		}
		static int? StatBonus(Entity entity, StatType type)
		{
			return entity.GetOptional<PartStatsContainer>()?.GetAttributeOptional(type)?.Bonus;
		}
	}

	public static int GetValue(this EntityProperty property, Entity entity)
	{
		try
		{
			if (entity == null)
			{
				PFLog.Default.ErrorWithReport($"Can't get property {property} from null");
				return 0;
			}
			int? num = Getters[(int)property]?.Invoke(entity);
			if (!num.HasValue)
			{
				PFLog.Default.ErrorWithReport($"Can't get property {property} from {entity}");
			}
			return num.GetValueOrDefault();
		}
		catch (Exception exception)
		{
			PFLog.Default.ExceptionWithReport(exception, $"Exception in getter of property {property} ({entity})");
			return 0;
		}
	}

	private static int? GetCurrentWeaponRateOfFire(Entity e)
	{
		return (ContextData<MechanicsContext.Data>.Current?.Context.SourceAbilityContext?.Ability ?? ContextData<PropertyContextData>.Current?.Context.Ability ?? (ContextData<PropertyContextData>.Current?.Context.Rule as RuleCalculateDamage)?.Ability)?.GetWeaponStats().ResultRateOfFire;
	}

	private static int? GetEnemiesAdjacent(Entity e)
	{
		BaseUnitEntity unit = e as BaseUnitEntity;
		if (unit == null)
		{
			return null;
		}
		return Game.Instance.State.AllUnits.Count((AbstractUnitEntity p) => p.DistanceToInCells(unit) <= 1);
	}

	private static int? GetAbsorption(Entity e)
	{
		if (!(e is MechanicEntity initiator))
		{
			return null;
		}
		return Rulebook.Trigger(new RuleCalculateStatsArmor(initiator)).ResultAbsorption;
	}

	private static int? GetDeflection(Entity e)
	{
		if (!(e is MechanicEntity initiator))
		{
			return null;
		}
		return Rulebook.Trigger(new RuleCalculateStatsArmor(initiator)).ResultDeflection;
	}
}
