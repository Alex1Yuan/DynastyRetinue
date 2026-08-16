using System;
using UnityEngine;

namespace Kingmaker.UI.SurfaceCombatHUD;

public readonly struct MaterialOverrides(Texture2D iconOverride, HighlightData highlightOverride) : IEquatable<MaterialOverrides>
{
	public readonly Texture2D iconOverride = iconOverride;

	public readonly HighlightData highlightOverride = highlightOverride;

	public bool Equals(MaterialOverrides other)
	{
		if (object.Equals(iconOverride, other.iconOverride))
		{
			return highlightOverride.Equals(other.highlightOverride);
		}
		return false;
	}

	public override bool Equals(object obj)
	{
		if (obj is MaterialOverrides other)
		{
			return Equals(other);
		}
		return false;
	}

	public override int GetHashCode()
	{
		return (((iconOverride != null) ? iconOverride.GetHashCode() : 0) * 397) ^ highlightOverride.GetHashCode();
	}
}
