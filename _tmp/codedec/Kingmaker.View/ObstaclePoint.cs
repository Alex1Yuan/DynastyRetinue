using System;
using UnityEngine;

namespace Kingmaker.View;

internal struct ObstaclePoint(float angle, ObstaclePointType type, ObstacleMode mode)
{
	public float Angle = angle;

	public ObstaclePointType Type = type;

	public ObstacleMode Mode = mode;

	public static Comparison<ObstaclePoint> Comparer { get; } = (ObstaclePoint a, ObstaclePoint b) => a.CompareTo(b);

	public int CompareTo(ObstaclePoint other)
	{
		if (Angle != other.Angle)
		{
			return Mathf.Abs(Angle).CompareTo(Mathf.Abs(other.Angle));
		}
		int type = (int)other.Type;
		return type.CompareTo((int)Type);
	}
}
