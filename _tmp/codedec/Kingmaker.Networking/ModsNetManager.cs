using System.Collections.Generic;
using System.Linq;
using Kingmaker.Networking.Player;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.Utility.ModsInfo;
using Photon.Realtime;

namespace Kingmaker.Networking;

public class ModsNetManager
{
	private Dictionary<string, ModData[]> m_UserMods = new Dictionary<string, ModData[]>();

	private const string ModsPropertyName = "m";

	public bool IsSameMods
	{
		get
		{
			if (m_UserMods.Count < 2)
			{
				return true;
			}
			ModData[] value = m_UserMods.First().Value;
			foreach (KeyValuePair<string, ModData[]> item in m_UserMods.Skip(1))
			{
				if (value.Length != item.Value.Length)
				{
					return false;
				}
				if (!value.All(((IEnumerable<ModData>)item.Value).Contains<ModData>))
				{
					return false;
				}
			}
			return true;
		}
	}

	public bool TryGetModsData(string userId, out ModData[] mods)
	{
		return m_UserMods.TryGetValue(userId, out mods);
	}

	public void OnJoinedLobby()
	{
		ModData[] array = (from modInfo in UserModsData.Instance.UsedMods
			select new ModData
			{
				Id = modInfo.Id,
				Version = modInfo.Version
			} into m
			orderby m.Id
			select m).ToArray();
		PFLog.Net.Log("[Mod] Uploading Mod list...");
		PhotonManager.Instance.SetPlayerProperty("m", array);
		PFLog.Net.Log($"[Mod] Available Mods x{array.Length}");
		int num = 0;
		for (int num2 = array.Length; num < num2; num++)
		{
			PFLog.Net.Log($"[Mod]   {num}) {array[num]}");
		}
		foreach (PlayerInfo allPlayer in PhotonManager.Instance.AllPlayers)
		{
			if (PhotonManager.Instance.ActorNumberToPhotonPlayer(allPlayer.Player, out var player))
			{
				OnPlayerUpdate(player);
			}
		}
	}

	public void OnPlayerUpdate(Photon.Realtime.Player photonPlayer)
	{
		if (PhotonManager.Instance.GetPlayerProperty<ModData[]>(photonPlayer, "m", out var obj))
		{
			m_UserMods[photonPlayer.UserId] = obj;
			EventBus.RaiseEvent(delegate(INetCheckUsersModsHandler h)
			{
				h.HandleCheckUsersMods();
			});
			PFLog.Net.Log($"[Mod] OnPlayerUpdate {photonPlayer.UserId} x{obj.Length}");
			int num = 0;
			for (int num2 = obj.Length; num < num2; num++)
			{
				PFLog.Net.Log($"[Mod]   {num}) {obj[num]}");
			}
		}
	}

	public void OnPlayerLeftRoom(Photon.Realtime.Player otherPlayer)
	{
		m_UserMods.Remove(otherPlayer.UserId);
		EventBus.RaiseEvent(delegate(INetCheckUsersModsHandler h)
		{
			h.HandleCheckUsersMods();
		});
	}

	public void OnLeave()
	{
		m_UserMods.Clear();
		EventBus.RaiseEvent(delegate(INetCheckUsersModsHandler h)
		{
			h.HandleCheckUsersMods();
		});
	}
}
