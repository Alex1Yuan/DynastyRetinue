using System;
using System.Collections.Generic;

namespace DynastyRetinue
{
    /// <summary>
    /// 当前游戏存档的海战舰队名册，随原版 InGameSettings 保存快照。
    /// GameId 只校验战役身份，不作为跨存档共用名册的缓存键。
    /// 不把临时战斗舰塞进 Player.CrossSceneState。
    /// </summary>
    [Serializable]
    public sealed class SpaceFleetRosterState
    {
        public int DataVersion;
        public string GameId = "";
        public bool Initialized;
        public int NextId = 1;
        public List<string> KnownShipItemGuids = new List<string>();
        public List<SpaceFleetEntry> Entries = new List<SpaceFleetEntry>();
    }

    /// <summary>一艘长期名册舰。全部身份均为裸字符串或原版蓝图 GUID。</summary>
    [Serializable]
    public sealed class SpaceFleetEntry
    {
        public string Id = "";
        public string BlueprintGuid = "";
        public string Name = "";
        public int BroadsideExtraShots;
        public int NonBroadsideExtraShots;
        public int BroadsideExtraRange;
        public int NonBroadsideExtraRange;
        public List<SpaceFleetLoadoutChoice> Loadout = new List<SpaceFleetLoadoutChoice>();
    }

    /// <summary>
    /// 虚拟装配选择。SlotKey 例如 weapon:Port:0；ItemGuid 只能是原版舰装蓝图。
    /// 首版整舰预设下列表为空，直接使用船体原装。
    /// </summary>
    [Serializable]
    public sealed class SpaceFleetLoadoutChoice
    {
        public string SlotKey = "";
        public string ItemGuid = "";
    }
}
