using System.Linq;
using Pathfinding;
using Pathfinding.Util;
using UnityEngine;

namespace Kingmaker.Pathfinding;

public readonly struct GridCuts
{
	private readonly Rect[] m_Adds;

	private readonly Rect[] m_Cuts;

	private GridCuts(Rect[] adds, Rect[] cuts)
	{
		m_Adds = adds;
		m_Cuts = cuts;
	}

	public static GridCuts Create(GraphTransform transform)
	{
		Rect[] adds = (from v in NavmeshClipper.allEnabled.OfType<NavmeshAdd>()
			where v.type != NavmeshAdd.MeshType.CustomMesh
			select v.GetBounds(transform)).ToArray();
		Rect[] cuts = (from v in NavmeshClipper.allEnabled.OfType<NavmeshCut>()
			where v.type != NavmeshCut.MeshType.CustomMesh
			select v.GetBounds(transform)).ToArray();
		return new GridCuts(adds, cuts);
	}

	public bool IsAdded(int x, int z)
	{
		Vector2 position = new Vector2(x, z);
		Rect other = new Rect(position, new Vector2(1f, 1f));
		for (int i = 0; i < m_Adds.Length; i++)
		{
			if (m_Adds[i].Overlaps(other))
			{
				return true;
			}
		}
		return false;
	}

	public bool IsRemoved(int x, int z)
	{
		Vector2 position = new Vector2(x, z);
		Rect other = new Rect(position, new Vector2(1f, 1f));
		for (int i = 0; i < m_Cuts.Length; i++)
		{
			if (m_Cuts[i].Overlaps(other))
			{
				return true;
			}
		}
		return false;
	}
}
