using UnityEngine;

namespace Kingmaker.Utility;

public readonly struct SphereBounds(Vector3 center, float radius)
{
	public readonly Vector3 Center = center;

	public readonly float RadiusSqr = radius * radius;

	public bool Contains(in Vector3 point)
	{
		return Vector3.SqrMagnitude(Center - point) <= RadiusSqr;
	}
}
