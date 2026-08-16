using JetBrains.Annotations;
using Pathfinding;

namespace Kingmaker.Pathfinding;

public struct CustomConnection(GraphNode node, uint cost, byte shapeEdge = byte.MaxValue, INodeLink link = null)
{
	public GraphNode Node = node;

	[CanBeNull]
	public INodeLink Link = link;

	public uint Cost = cost;

	public byte ShapeEdge = shapeEdge;

	public override int GetHashCode()
	{
		return Node.GetHashCode() ^ (int)Cost;
	}

	public override bool Equals(object obj)
	{
		if (obj == null)
		{
			return false;
		}
		CustomConnection customConnection = (CustomConnection)obj;
		if (customConnection.Node == Node && customConnection.Cost == Cost && customConnection.ShapeEdge == ShapeEdge)
		{
			return customConnection.Link == Link;
		}
		return false;
	}
}
