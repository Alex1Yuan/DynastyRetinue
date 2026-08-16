using Owlcat.Runtime.Visual.OcclusionGeometryClip;
using Unity.Mathematics;
using UnityEngine;

namespace Owlcat.Runtime.Visual.SceneHelpers;

public sealed class StaticPrefabOcclusionClipGroupBoundsBox : MonoBehaviour
{
	public OBox GetBounds()
	{
		OBox oBox = new OBox
		{
			xAxis = new float3(1f, 0f, 0f),
			yAxis = new float3(0f, 1f, 0f),
			zAxis = new float3(0f, 0f, 1f),
			extents = new float3(0.5f)
		};
		return oBox.GetTransformedSafe((float4x4)base.transform.localToWorldMatrix);
	}

	private void OnDrawGizmos()
	{
		Gizmos.matrix = base.transform.localToWorldMatrix;
		Gizmos.color = new Color(0f, 1f, 1f, 1f);
		Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
	}
}
