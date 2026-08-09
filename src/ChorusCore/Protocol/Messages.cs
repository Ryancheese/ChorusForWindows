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
    [property: JsonPropertyName("protocolVersion")] ushort ProtocolVersion = SyncProtocol.Version,
    /// <summary>Optional platform hint: ios / android / windows / macos.</summary>
    [property: JsonPropertyName("platform")] string? Platform = null,
    /// <summary>Optional hardware model, e.g. "iPhone 15 Pro".</summary>
    [property: JsonPropertyName("model")] string? Model = null)
{
    /// <summary>Short platform chip for Host UI (not serialized).</summary>
    [JsonIgnore]
    public string PlatformLabel => Platform?.Trim().ToLowerInvariant() switch
    {
        "ios" => "iPhone",
        "ipados" => "iPad",
        "android" => "Android",
        "windows" => "Windows",
        "macos" or "mac" => "MacBook",
        _ => Role == DeviceRole.Host ? "Host" : "Speaker",
    };

    /// <summary>Name plus model when available (Host session list).</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var name = Name?.Trim() ?? "";
            var model = Model?.Trim() ?? "";
            var generic = name.Equals("iPhone", StringComparison.OrdinalIgnoreCase)
                || name.Equals("iPad", StringComparison.OrdinalIgnoreCase)
                || name.Equals("iPod", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Android", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Speaker", StringComparison.OrdinalIgnoreCase)
                || name.Equals("MacBook", StringComparison.OrdinalIgnoreCase);
            if (generic && model.Length > 0) return model;
            if (model.Length == 0) return name.Length > 0 ? name : PlatformLabel;
            if (name.Contains(model, StringComparison.OrdinalIgnoreCase)) return name;
            return $"{name} · {model}";
        }
    }

    /// <summary>Secondary chip under the name — platform family only.</summary>
    [JsonIgnore]
    public string DetailLabel => PlatformLabel;
}

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
