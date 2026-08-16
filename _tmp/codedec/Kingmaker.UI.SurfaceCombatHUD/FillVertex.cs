using Unity.Burst;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
public readonly struct FillVertex(float3 position, float4 color, float2 areaUv, float2 areaSize)
{
	public static readonly VertexAttributeDescriptor[] kAttributes = new VertexAttributeDescriptor[4]
	{
		new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
		new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float16, 4),
		new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
		new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2)
	};

	private readonly float3 m_Position = position;

	private readonly half4 m_Color = (half4)color;

	private readonly float2 m_AreaUv = areaUv;

	private readonly float2 m_AreaSize = areaSize;
}
