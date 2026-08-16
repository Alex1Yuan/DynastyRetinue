using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Kingmaker.Networking.Serialization.Converters;

public class QuaternionAsIntConverter : JsonConverter
{
	public override bool CanConvert(Type objectType)
	{
		return typeof(Quaternion) == objectType;
	}

	public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
	{
		JArray jArray = JArray.Load(reader);
		return new Quaternion
		{
			x = BitConverter.Int32BitsToSingle((int)jArray[0]),
			y = BitConverter.Int32BitsToSingle((int)jArray[1]),
			z = BitConverter.Int32BitsToSingle((int)jArray[2]),
			w = BitConverter.Int32BitsToSingle((int)jArray[3])
		};
	}

	public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
	{
		Quaternion quaternion = (Quaternion)value;
		writer.WriteStartArray();
		writer.WriteValue(BitConverter.SingleToInt32Bits(quaternion.x));
		writer.WriteValue(BitConverter.SingleToInt32Bits(quaternion.y));
		writer.WriteValue(BitConverter.SingleToInt32Bits(quaternion.z));
		writer.WriteValue(BitConverter.SingleToInt32Bits(quaternion.w));
		writer.WriteEnd();
	}
}
