namespace Kingmaker.Visual.Particles.SnapController;

internal readonly struct PlaybackStateUpdateData(bool particleSystemVisible, CameraData cameraData)
{
	public readonly bool particleSystemVisible = particleSystemVisible;

	public readonly CameraData cameraData = cameraData;
}
