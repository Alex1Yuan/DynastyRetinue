using System.Collections.Generic;
using UnityEngine;

namespace Kingmaker.Code.View.Mechanics;

public struct ScriptZonesData(Vector3 position, Vector3 scale)
{
	public Vector3 Position = position;

	public Vector3 Scale = scale;

	public List<Vector3> NodePositions = new List<Vector3>();
}
