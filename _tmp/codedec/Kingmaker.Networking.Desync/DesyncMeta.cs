namespace Kingmaker.Networking.Desync;

public struct DesyncMeta(int tick, string roomId, int playersCount)
{
	public int Tick = tick;

	public string RoomId = roomId;

	public int PlayersCount = playersCount;
}
