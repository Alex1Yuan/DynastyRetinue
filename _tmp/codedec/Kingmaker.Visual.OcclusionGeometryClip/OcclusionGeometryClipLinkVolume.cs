using UnityEngine;

namespace Kingmaker.Visual.OcclusionGeometryClip;

public sealed class OcclusionGeometryClipLinkVolume : MonoBehaviour
{
	[SerializeField]
	private Renderer m_LinkedRenderer;

	public Renderer LinkedRenderer => m_LinkedRenderer;

	public PlaneBox GetBounds()
	{
		Transform transform = base.transform;
		return new PlaneBox(transform.position, transform.rotation, transform.localScale);
	}
}
