using System;
using Newtonsoft.Json;
using StateHasher.Core;
using UnityEngine;

namespace Kingmaker.AI.Learning.Collections;

[Serializable]
[HashRoot]
public readonly struct AttackData(string ability, int range, int damage) : IHashable
{
	[JsonProperty]
	public readonly string Ability = ability;

	[JsonProperty]
	public readonly int Range = range;

	[JsonProperty]
	public readonly int Damage = damage;

	public override string ToString()
	{
		return $"{GetType().Name}[{Ability}, Range: {Range}, Damage: {Damage}]";
	}

	public Hash128 GetHash128()
	{
		Hash128 result = default(Hash128);
		result.Append(Ability);
		int val = Range;
		result.Append(ref val);
		int val2 = Damage;
		result.Append(ref val2);
		return result;
	}
}
