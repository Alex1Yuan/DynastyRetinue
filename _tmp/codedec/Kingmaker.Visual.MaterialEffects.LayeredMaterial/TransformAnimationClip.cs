using UnityEngine;

namespace Kingmaker.Visual.MaterialEffects.LayeredMaterial;

internal sealed class TransformAnimationClip : IAnimationClip
{
	private readonly PropertyIdentifier? m_WorldToLocalPropertyIdentifier;

	private readonly PropertyIdentifier? m_LocalToWorldPropertyIdentifier;

	private readonly Transform m_Transform;

	public TransformAnimationClip(string worldToLocalPropertyName, string localToWorldPropertyName, Transform transform)
	{
		m_WorldToLocalPropertyIdentifier = (string.IsNullOrWhiteSpace(worldToLocalPropertyName) ? ((PropertyIdentifier?)null) : new PropertyIdentifier?(new PropertyIdentifier(worldToLocalPropertyName)));
		m_LocalToWorldPropertyIdentifier = (string.IsNullOrWhiteSpace(localToWorldPropertyName) ? ((PropertyIdentifier?)null) : new PropertyIdentifier?(new PropertyIdentifier(localToWorldPropertyName)));
		m_Transform = transform;
	}

	public void Sample(in PropertyBlock properties, float time)
	{
		if (!(m_Transform == null))
		{
			if (m_WorldToLocalPropertyIdentifier.HasValue)
			{
				properties.SetMatrix(m_WorldToLocalPropertyIdentifier.Value, m_Transform.worldToLocalMatrix);
			}
			if (m_LocalToWorldPropertyIdentifier.HasValue)
			{
				properties.SetMatrix(m_LocalToWorldPropertyIdentifier.Value, m_Transform.localToWorldMatrix);
			}
		}
	}

	void IAnimationClip.Sample(in PropertyBlock properties, float time)
	{
		Sample(in properties, time);
	}
}
