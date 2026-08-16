using Newtonsoft.Json;

namespace Kingmaker.EntitySystem.Persistence;

public struct KnownAreaEntry(string areaGuid, string[] addStateScenes)
{
	[JsonProperty]
	public string AreaGuid = areaGuid;

	[JsonProperty]
	public string[] AddStateScenes = addStateScenes;
}
