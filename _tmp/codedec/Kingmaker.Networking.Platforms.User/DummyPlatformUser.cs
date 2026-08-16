using System;
using System.Threading.Tasks;
using Kingmaker.Networking.Player;
using UnityEngine;

namespace Kingmaker.Networking.Platforms.User;

public class DummyPlatformUser : IPlatformUser
{
	private Texture2D m_Icon;

	string IPlatformUser.NickName => PhotonManager.Instance.LocalPlayerUserId;

	PlayerAvatar IPlatformUser.LargeIcon => PlayerAvatar.Invalid;

	public Task Initialize()
	{
		return Task.CompletedTask;
	}

	public void GetLargeIcon(string userId, Action<PlayerAvatar> callback)
	{
		Delay(userId, callback);
		static async Task Delay(string text, Action<PlayerAvatar> action)
		{
			PFLog.Net.Log("DummyPlatformUser.GetLargeIcon " + text + " start");
			await Task.Delay(TimeSpan.FromSeconds(1.0));
			PFLog.Net.Log("DummyPlatformUser.GetLargeIcon " + text + " finish");
			action(PlayerAvatar.Invalid);
		}
	}
}
