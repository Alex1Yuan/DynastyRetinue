using Pathfinding;
using UnityEngine;

namespace Kingmaker.Pathfinding;

public readonly struct WarhammerPathChargeCell(Vector3 position, int diagonalsCount, float length, GraphNode node, GraphNode parentNode, bool isCanStand)
{
	public readonly Vector3 Position = position;

	public readonly int DiagonalsCount = diagonalsCount;

	public readonly float Length = length;

	public readonly GraphNode Node = node;

	public readonly GraphNode ParentNode = parentNode;

	public readonly bool IsCanStand = isCanStand;

	public override string ToString()
	{
		return $"{Node} ({Position}) [Len={Length}, CanStand={IsCanStand}, DiagCount={DiagonalsCount}]";
	}
}
