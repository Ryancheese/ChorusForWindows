using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChorusCore.Protocol;

/// <summary>
/// Encodes/decodes control messages (JSON) and audio frames (binary) on the wire.
/// Audio frame layout: [1 byte type][4 byte headerLen big-endian][header JSON][pcm bytes].
/// </summary>
public static class MessageCodec
{
    /// <summary>
    /// Shared JSON options. CamelCase naming + DeviceRole as string.
    /// Field names are pinned via <c>[JsonPropertyName]</c> on each record, so this mainly
    /// affects any unmapped fields and keeps enum/number formatting consistent.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] EncodeControl(ControlPayload payload)
        => JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

    public static ControlPayload DecodeControl(ReadOnlySpan<byte> data)
        => JsonSerializer.Deserialize<ControlPayload>(data, JsonOptions)
           ?? throw new JsonException("Failed to decode control payload");

    /// <summary>Binary audio frame: [1 byte type][4 byte headerLen big-endian][header JSON][pcm bytes].</summary>
    public static byte[] EncodeAudioFrame(AudioChunkHeader header, ReadOnlySpan<byte> pcm)
    {
        var headerData = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var output = new byte[1 + 4 + headerData.Length + pcm.Length];
        output[0] = (byte)MessageType.AudioChunk;
        WriteUInt32BigEndian(output, 1, (uint)headerData.Length);
        Buffer.BlockCopy(headerData, 0, output, 5, headerData.Length);
        pcm.CopyTo(output.AsSpan(5 + headerData.Length));
        return output;
    }

    public static (AudioChunkHeader Header, byte[] Pcm) DecodeAudioFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[0] != (byte)MessageType.AudioChunk)
            throw new CodecException("Invalid frame: bad magic byte");
        int headerLen = (int)ReadUInt32BigEndian(data, 1);
        int headerEnd = 5 + headerLen;
        if (data.Length < headerEnd)
            throw new CodecException("Invalid frame: truncated header");
        var header = JsonSerializer.Deserialize<AudioChunkHeader>(data.Slice(5, headerLen), JsonOptions)
                     ?? throw new CodecException("Bad header JSON");
        var pcm = data.Slice(headerEnd).ToArray();
        return (header, pcm);
    }

    internal static void WriteUInt32BigEndian(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    internal static uint ReadUInt32BigEndian(ReadOnlySpan<byte> buf, int offset)
        => (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);

    public sealed class CodecException : Exception
    {
        public CodecException(string message) : base(message) { }
    }
}
