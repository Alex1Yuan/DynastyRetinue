using UnityEngine;

namespace Pathfinding;

public readonly struct GridNavmeshFence(Rect bounds, int height)
{
	public readonly Rect bounds = bounds;

	public readonly int height = height;
}
