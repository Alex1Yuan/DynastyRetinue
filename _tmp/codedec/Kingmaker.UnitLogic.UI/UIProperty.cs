using JetBrains.Annotations;
using Kingmaker.UnitLogic.Mechanics.Blueprints;

namespace Kingmaker.UnitLogic.UI;

public readonly struct UIProperty(UIPropertyName nameType, string name, string description, bool main, [CanBeNull] BlueprintMechanicEntityFact descriptionFact, int? propertyValue)
{
	public readonly UIPropertyName NameType = nameType;

	public readonly string Name = name;

	public readonly string Description = description;

	public readonly bool Main = main;

	[CanBeNull]
	public readonly BlueprintMechanicEntityFact DescriptionFact = descriptionFact;

	public readonly int? PropertyValue = propertyValue;
}
