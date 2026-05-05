using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerlessPatcher.Profiles;

/// <summary>
/// Allows <c>byte[]</c> fields in JSON profiles to be written as plain number arrays
/// (e.g. <c>[57, 142, 227, 63]</c>) rather than base64 strings.
/// Also accepts base64 strings for compatibility.
/// </summary>
public sealed class ByteArrayNumberArrayConverter : JsonConverter<byte[]>
{
    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
            return reader.GetBytesFromBase64(); // base64 fallback

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<byte>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number)
                    throw new JsonException($"Expected number in byte array, got {reader.TokenType}.");
                list.Add(reader.GetByte());
            }
            return [.. list];
        }

        throw new JsonException($"Cannot deserialize {reader.TokenType} as byte[].");
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var b in value)
            writer.WriteNumberValue(b);
        writer.WriteEndArray();
    }
}
