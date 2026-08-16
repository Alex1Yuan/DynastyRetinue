using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Kingmaker.Networking.Serialization.Converters;

public class Vector3AsIntConverter : JsonConverter
{
	public override bool CanConvert(Type objectType)
	{
		return typeof(Vector3) == objectType;
	}

	public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
	{
		JArray jArray = JArray.Load(reader);
		return new Vector3
		{
			x = BitConverter.Int32BitsToSingle((int)jArray[0]),
			y = BitConverter.Int32BitsToSingle((int)jArray[1]),
			z = BitConverter.Int32BitsToSingle((int)jArray[2])
		};
	}

	public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
	{
		Vector3 vector = (Vector3)value;
		writer.WriteStartArray();
		writer.WriteValue(BitConverter.SingleToInt32Bits(vector.x));
		writer.WriteValue(BitConverter.SingleToInt32Bits(vector.y));
		writer.WriteValue(BitConverter.SingleToInt32Bits(vector.z));
		writer.WriteEnd();
	}
}
