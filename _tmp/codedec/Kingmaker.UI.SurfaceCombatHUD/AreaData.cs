namespace Kingmaker.UI.SurfaceCombatHUD;

public struct AreaData(uint flag, IAreaSource source, int intersectionFlagShift = 0, bool isStratagem = false)
{
	public uint flag = flag;

	public IAreaSource source = source;

	public int intersectionFlagShift = intersectionFlagShift;

	public bool isStratagem = isStratagem;
}
