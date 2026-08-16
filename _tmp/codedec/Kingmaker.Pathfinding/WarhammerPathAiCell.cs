using Pathfinding;
using UnityEngine;

namespace Kingmaker.Pathfinding;

public readonly struct WarhammerPathAiCell(Vector3 position, int diagonalsCount, float length, GraphNode node, GraphNode parentNode, bool isCanStand, int enteredAoE, int stepsInsideDamagingAoE, int provokedAttacks)
{
	public readonly Vector3 Position = position;

	public readonly int DiagonalsCount = diagonalsCount;

	public readonly float Length = length;

	public readonly GraphNode Node = node;

	public readonly GraphNode ParentNode = parentNode;

	public readonly bool IsCanStand = isCanStand;

	public readonly int EnteredAoE = enteredAoE;

	public readonly int StepsInsideDamagingAoE = stepsInsideDamagingAoE;

	public readonly int ProvokedAttacks = provokedAttacks;

	public override string ToString()
	{
		return $"{Node} ({Position}) [Len={Length}, CanStand={IsCanStand}, DiagCount={DiagonalsCount}]";
	}
}
