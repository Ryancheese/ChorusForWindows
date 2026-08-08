namespace ChorusCore.Protocol;

/// <summary>
/// Bonjour/mDNS service parameters for LAN discovery (must match Mac version exactly).
/// </summary>
public static class SyncBonjour
{
    public const string Type = "_chorus._tcp";
    public const string Domain = "local.";
    public const ushort ControlPort = 17_482;
}

/// <summary>
/// Wire protocol constants. Bump <see cref="Version"/> when message layout changes.
/// </summary>
public static class SyncProtocol
{
    public const ushort Version = 1;

    /// <summary>Playback buffer ahead of host time (seconds). A longer lead absorbs Wi-Fi jitter.</summary>
    public const double DefaultLeadTime = 1.2;

    /// <summary>PCM format: mono Float32 @ 44.1 kHz for demo simplicity (matches Mac version).</summary>
    public const double SampleRate = 44_100.0;

    public const ushort Channels = 1;
    public const int BytesPerSample = 4;
}

/// <summary>
/// Wire protocol message type. Serialized as a raw byte in JSON and binary audio frames.
/// Values must match the Swift <c>MessageType</c> enum exactly.
/// </summary>
public enum MessageType : byte
{
    Hello = 1,
    Welcome = 2,
    ClockPing = 3,
    ClockPong = 4,
    PrepareSession = 5,
    StartPlayback = 6,
    StopPlayback = 7,
    AudioChunk = 8,
    Heartbeat = 9,
    Goodbye = 10,
    AudioChannelHello = 11,
    StopAcknowledged = 12,
    ClockOffset = 13,
}
