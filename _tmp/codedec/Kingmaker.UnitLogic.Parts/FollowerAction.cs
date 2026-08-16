using UnityEngine;

namespace Kingmaker.UnitLogic.Parts;

public readonly struct FollowerAction(Vector3 position, float? orientation, FollowerActionType type)
{
	public readonly Vector3 Position = position;

	public readonly float? Orientation = orientation;

	public readonly FollowerActionType Type = type;
}
