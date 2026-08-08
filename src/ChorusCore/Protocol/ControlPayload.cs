using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChorusCore.Protocol;

/// <summary>
/// Discriminated control message. Wire format: <c>{"type": &lt;byte&gt;, "payload": {...}}</c>.
/// Byte-compatible with the Mac (Swift) ChorusCore implementation.
/// </summary>
[JsonConverter(typeof(ControlPayloadConverter))]
public abstract record ControlPayload
{
    public abstract MessageType Type { get; }

    public sealed record Hello(DeviceInfo Info) : ControlPayload
    {
        public override MessageType Type => MessageType.Hello;
    }

    public sealed record Welcome(DeviceInfo Info) : ControlPayload
    {
        public override MessageType Type => MessageType.Welcome;
    }

    public sealed record ClockPing(ClockPingData Ping) : ControlPayload
    {
        public override MessageType Type => MessageType.ClockPing;
    }

    public sealed record ClockPong(ClockPongData Pong) : ControlPayload
    {
        public override MessageType Type => MessageType.ClockPong;
    }

    public sealed record PrepareSession(PrepareSessionData Session) : ControlPayload
    {
        public override MessageType Type => MessageType.PrepareSession;
    }

    public sealed record StartPlayback(StartPlaybackData Start) : ControlPayload
    {
        public override MessageType Type => MessageType.StartPlayback;
    }

    public sealed record StopPlayback(Guid SessionId) : ControlPayload
    {
        public override MessageType Type => MessageType.StopPlayback;
    }

    public sealed record AudioChannelHello(string DeviceId) : ControlPayload
    {
        public override MessageType Type => MessageType.AudioChannelHello;
    }

    public sealed record StopAcknowledged(Guid SessionId) : ControlPayload
    {
        public override MessageType Type => MessageType.StopAcknowledged;
    }

    /// <summary>Host pushes the latest estimated clock offset to a Speaker.</summary>
    public sealed record ClockOffset(double Seconds) : ControlPayload
    {
        public override MessageType Type => MessageType.ClockOffset;
    }

    public sealed record Heartbeat(string DeviceId) : ControlPayload
    {
        public override MessageType Type => MessageType.Heartbeat;
    }

    public sealed record Goodbye(string DeviceId) : ControlPayload
    {
        public override MessageType Type => MessageType.Goodbye;
    }
}

/// <summary>
/// Hand-written converter to match the Swift wire format exactly:
/// <c>{"type": &lt;number&gt;, "payload": &lt;object|string|number&gt;}</c>.
/// Note: <c>stopPlayback</c>/<c>stopAcknowledged</c> payload is <c>{"sessionID":"..."}</c>;
/// <c>audioChannelHello</c>/<c>heartbeat</c>/<c>goodbye</c> payload is <c>{"deviceID":"..."}</c>;
/// <c>clockOffset</c> payload is a bare number.
/// </summary>
public sealed class ControlPayloadConverter : JsonConverter<ControlPayload>
{
    public override ControlPayload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl))
            throw new JsonException("Missing 'type' field");
        var type = (MessageType)typeEl.GetByte();
        if (!root.TryGetProperty("payload", out var payloadEl))
            throw new JsonException("Missing 'payload' field");

        return type switch
        {
            MessageType.Hello => new ControlPayload.Hello(Deserialize<DeviceInfo>(payloadEl, options)),
            MessageType.Welcome => new ControlPayload.Welcome(Deserialize<DeviceInfo>(payloadEl, options)),
            MessageType.ClockPing => new ControlPayload.ClockPing(Deserialize<ClockPingData>(payloadEl, options)),
            MessageType.ClockPong => new ControlPayload.ClockPong(Deserialize<ClockPongData>(payloadEl, options)),
            MessageType.PrepareSession => new ControlPayload.PrepareSession(Deserialize<PrepareSessionData>(payloadEl, options)),
            MessageType.StartPlayback => new ControlPayload.StartPlayback(Deserialize<StartPlaybackData>(payloadEl, options)),
            MessageType.StopPlayback => new ControlPayload.StopPlayback(ReadSessionId(payloadEl)),
            MessageType.AudioChannelHello => new ControlPayload.AudioChannelHello(ReadDeviceId(payloadEl)),
            MessageType.StopAcknowledged => new ControlPayload.StopAcknowledged(ReadSessionId(payloadEl)),
            MessageType.ClockOffset => new ControlPayload.ClockOffset(payloadEl.GetDouble()),
            MessageType.Heartbeat => new ControlPayload.Heartbeat(ReadDeviceId(payloadEl)),
            MessageType.Goodbye => new ControlPayload.Goodbye(ReadDeviceId(payloadEl)),
            MessageType.AudioChunk => throw new JsonException("audioChunk is binary, not JSON"),
            _ => throw new JsonException($"Unknown message type {type}")
        };
    }

    public override void Write(Utf8JsonWriter writer, ControlPayload value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("type");
        writer.WriteNumberValue((byte)value.Type);
        writer.WritePropertyName("payload");

        switch (value)
        {
            case ControlPayload.Hello h:
                JsonSerializer.Serialize(writer, h.Info, options);
                break;
            case ControlPayload.Welcome w:
                JsonSerializer.Serialize(writer, w.Info, options);
                break;
            case ControlPayload.ClockPing cp:
                JsonSerializer.Serialize(writer, cp.Ping, options);
                break;
            case ControlPayload.ClockPong cp:
                JsonSerializer.Serialize(writer, cp.Pong, options);
                break;
            case ControlPayload.PrepareSession ps:
                JsonSerializer.Serialize(writer, ps.Session, options);
                break;
            case ControlPayload.StartPlayback sp:
                JsonSerializer.Serialize(writer, sp.Start, options);
                break;
            case ControlPayload.StopPlayback sp:
                WriteStringObject(writer, "sessionID", sp.SessionId.ToString());
                break;
            case ControlPayload.AudioChannelHello ah:
                WriteStringObject(writer, "deviceID", ah.DeviceId);
                break;
            case ControlPayload.StopAcknowledged sa:
                WriteStringObject(writer, "sessionID", sa.SessionId.ToString());
                break;
            case ControlPayload.ClockOffset co:
                writer.WriteNumberValue(co.Seconds);
                break;
            case ControlPayload.Heartbeat hb:
                WriteStringObject(writer, "deviceID", hb.DeviceId);
                break;
            case ControlPayload.Goodbye gb:
                WriteStringObject(writer, "deviceID", gb.DeviceId);
                break;
            default:
                throw new JsonException($"Unknown payload type {value.GetType()}");
        }

        writer.WriteEndObject();
    }

    private static T Deserialize<T>(JsonElement element, JsonSerializerOptions options)
        => element.Deserialize<T>(options) ?? throw new JsonException($"Failed to decode {typeof(T).Name}");

    private static Guid ReadSessionId(JsonElement payload)
    {
        if (payload.TryGetProperty("sessionID", out var idEl) &&
            idEl.ValueKind == JsonValueKind.String &&
            Guid.TryParse(idEl.GetString(), out var id))
            return id;
        throw new JsonException("Missing/invalid sessionID");
    }

    private static string ReadDeviceId(JsonElement payload)
        => payload.TryGetProperty("deviceID", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? string.Empty
            : string.Empty;

    private static void WriteStringObject(Utf8JsonWriter writer, string key, string value)
    {
        writer.WriteStartObject();
        writer.WriteString(key, value);
        writer.WriteEndObject();
    }
}
