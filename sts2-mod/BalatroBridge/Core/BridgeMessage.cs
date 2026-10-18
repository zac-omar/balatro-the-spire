using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BalatroBridge.Core;

/// <summary>
/// One line of the bridge protocol: {"type", "id"?, "re"?, "data"}. See protocol/PROTOCOL.md.
/// </summary>
public sealed class BridgeMessage
{
    public const int ProtocolVersion = 2;

    public required string Type { get; init; }

    /// <summary>Sender-assigned id; requests always have one so replies can reference it.</summary>
    public long? Id { get; init; }

    /// <summary>On a reply, the id of the request being answered.</summary>
    public long? Re { get; init; }

    public JsonObject Data { get; init; } = new();

    public static BridgeMessage Parse(string line)
    {
        JsonObject obj = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("message is not a JSON object");
        string type = obj["type"]?.GetValue<string>() ?? throw new FormatException("message has no 'type'");
        JsonNode? data = obj["data"];
        obj.Remove("data"); // detach so the node can live on without its parent
        return new BridgeMessage
        {
            Type = type,
            Id = ReadLong(obj["id"]),
            Re = ReadLong(obj["re"]),
            Data = data as JsonObject ?? new JsonObject(),
        };
    }

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", Type);
            if (Id is long id) writer.WriteNumber("id", id);
            if (Re is long re) writer.WriteNumber("re", re);
            writer.WritePropertyName("data");
            Data.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public override string ToString() => ToJson();

    private static long? ReadLong(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out double d) ? (long)d : null;
}
