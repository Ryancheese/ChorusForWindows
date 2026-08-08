using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChorusCore.Protocol;

/// <summary>Host vs Speaker role. Wire values are lowercase to match Swift Codable.</summary>
[JsonConverter(typeof(DeviceRoleConverter))]
public enum DeviceRole
{
    Host,
    Speaker,
}

/// <summary>Reads/writes <c>host</c>/<c>speaker</c> (not <c>Host</c>/<c>Speaker</c>).</summary>
public sealed class DeviceRoleConverter : JsonConverter<DeviceRole>
{
    public override DeviceRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString()?.Trim().ToLowerInvariant();
        return s switch
        {
            "host" => DeviceRole.Host,
            "speaker" => DeviceRole.Speaker,
            _ => throw new JsonException($"Unknown DeviceRole '{s}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, DeviceRole value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == DeviceRole.Host ? "host" : "speaker");
}

/// <summary>Device identity exchanged in hello/welcome handshake.</summary>
public sealed record DeviceInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] DeviceRole Role,
    [property: JsonPropertyName("protocolVersion")] ushort ProtocolVersion = SyncProtocol.Version);

/// <summary>Host -> Speaker clock probe.</summary>
public sealed record ClockPingData(
    [property: JsonPropertyName("pingID")] Guid PingId,
    [property: JsonPropertyName("hostSendTime")] double HostSendTime);

/// <summary>Speaker -> Host clock reply. Used by NTP-style offset estimation.</summary>
public sealed record ClockPongData(
    [property: JsonPropertyName("pingID")] Guid PingId,
    [property: JsonPropertyName("hostSendTime")] double HostSendTime,
    [property: JsonPropertyName("speakerReceiveTime")] double SpeakerReceiveTime,
    [property: JsonPropertyName("speakerSendTime")] double SpeakerSendTime);

/// <summary>Host tells Speaker the upcoming session audio format.</summary>
public sealed record PrepareSessionData(
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("sampleRate")] double SampleRate,
    [property: JsonPropertyName("channels")] ushort Channels,
    [property: JsonPropertyName("title")] string Title);

/// <summary>Host tells Speaker when (on host timeline) sample 0 should play.</summary>
public sealed record StartPlaybackData(
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("hostPlayAt")] double HostPlayAt,
    [property: JsonPropertyName("leadTime")] double LeadTime);

/// <summary>Header prepended to each PCM chunk. Carries the host-time anchor for precise scheduling.</summary>
public sealed record AudioChunkHeader(
    [property: JsonPropertyName("sessionID")] Guid SessionId,
    [property: JsonPropertyName("sequence")] ulong Sequence,
    [property: JsonPropertyName("sampleIndex")] ulong SampleIndex,
    [property: JsonPropertyName("sampleCount")] uint SampleCount,
    [property: JsonPropertyName("hostPlayAt")] double HostPlayAt);
