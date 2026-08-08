using System.Buffers.Binary;

namespace ChorusCore.Protocol;

/// <summary>
/// Length-prefixed TCP framing shared by control and audio channels:
/// [4 byte length big-endian][payload bytes].
/// Thread-safe <see cref="Unpacker"/> accumulates streamed bytes and yields complete frames.
/// </summary>
public static class FrameIO
{
    public static byte[] Pack(ReadOnlySpan<byte> payload)
    {
        var output = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)payload.Length);
        payload.CopyTo(output.AsSpan(4));
        return output;
    }

    /// <summary>
    /// Accumulates received bytes and returns fully-arrived frames each call.
    /// Mirrors the Swift <c>FrameIO.Unpacker</c> semantics: append returns the list of
    /// frames that can be dispatched immediately.
    /// </summary>
    public sealed class Unpacker
    {
        private readonly object _lock = new();
        private byte[] _buffer = Array.Empty<byte>();
        private int _length;

        public List<byte[]> Append(ReadOnlySpan<byte> data)
        {
            var frames = new List<byte[]>();
            lock (_lock)
            {
                EnsureCapacity(data.Length);
                data.CopyTo(_buffer.AsSpan(_length));
                _length += data.Length;

                int offset = 0;
                while (_length - offset >= 4)
                {
                    uint frameLen = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(offset, 4));
                    // Guard against absurd lengths that would OOM.
                    if (frameLen > 16 * 1024 * 1024)
                        throw new MessageCodec.CodecException($"Frame too large: {frameLen}");
                    if (_length - offset < 4 + (int)frameLen)
                        break;
                    var frame = new byte[frameLen];
                    Buffer.BlockCopy(_buffer, offset + 4, frame, 0, (int)frameLen);
                    frames.Add(frame);
                    offset += 4 + (int)frameLen;
                }

                if (offset > 0)
                {
                    int remaining = _length - offset;
                    if (remaining > 0)
                        Buffer.BlockCopy(_buffer, offset, _buffer, 0, remaining);
                    _length = remaining;
                }
            }
            return frames;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _length = 0;
            }
        }

        private void EnsureCapacity(int incoming)
        {
            int needed = _length + incoming;
            if (_buffer.Length >= needed) return;
            int newSize = Math.Max(_buffer.Length * 2, 8192);
            while (newSize < needed) newSize *= 2;
            var newBuf = new byte[newSize];
            if (_length > 0)
                Buffer.BlockCopy(_buffer, 0, newBuf, 0, _length);
            _buffer = newBuf;
        }
    }
}
