using System.Net.Sockets;
using ChorusCore.Protocol;

namespace ChorusCore.Network;

/// <summary>
/// Bidirectional framed TCP session used by both host and speaker.
/// Wraps a <see cref="TcpClient"/> and dispatches framed control/audio events.
/// Mirrors the Swift <c>SyncConnection</c> semantics on top of Network.framework.
/// </summary>
public sealed class SyncConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly string _remoteLabel;
    private readonly FrameIO.Unpacker _unpacker = new();
    private readonly object _handlerLock = new();
    private readonly object _writeLock = new();
    private Action<SyncConnectionEvent>? _onEvent;
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    public string RemoteLabel => _remoteLabel;

    public SyncConnection(TcpClient client, string remoteLabel = "peer")
    {
        _client = client;
        _remoteLabel = remoteLabel;
    }

    public void Start(Action<SyncConnectionEvent> onEvent)
    {
        lock (_handlerLock) _onEvent = onEvent;
        Emit(new SyncConnectionEvent.Connected());
        _ = Task.Run(ReceiveLoopAsync);
    }

    public void Cancel() => Dispose();

    public void SendControl(ControlPayload payload)
    {
        try
        {
            var data = MessageCodec.EncodeControl(payload);
            SendFrame(data);
        }
        catch (Exception ex)
        {
            Emit(new SyncConnectionEvent.Disconnected($"encode control failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Sends an audio frame and awaits completion. Keeping only one frame in flight
    /// lets a subsequent control message (e.g. stopPlayback) reach the peer promptly
    /// instead of waiting behind a full track.
    /// </summary>
    public async Task SendAudioAsync(AudioChunkHeader header, byte[] pcm)
    {
        try
        {
            var data = MessageCodec.EncodeAudioFrame(header, pcm);
            await SendFrameAndWaitAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Emit(new SyncConnectionEvent.Disconnected($"encode audio failed: {ex.Message}"));
        }
    }

    private void SendFrame(byte[] payload)
    {
        var framed = FrameIO.Pack(payload);
        lock (_writeLock)
        {
            var stream = _client.GetStream();
            stream.Write(framed, 0, framed.Length);
            stream.Flush();
        }
    }

    private async Task SendFrameAndWaitAsync(byte[] payload)
    {
        var framed = FrameIO.Pack(payload);
        lock (_writeLock)
        {
            var stream = _client.GetStream();
            stream.Write(framed, 0, framed.Length);
            stream.Flush();
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            var stream = _client.GetStream();
            var buffer = new byte[256 * 1024];
            while (!_cts.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    Emit(new SyncConnectionEvent.Disconnected(null));
                    return;
                }
                var frames = _unpacker.Append(buffer.AsSpan(0, read));
                foreach (var frame in frames)
                    HandleFrame(frame);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Emit(new SyncConnectionEvent.Disconnected(ex.Message));
        }
    }

    private void HandleFrame(byte[] frame)
    {
        if (frame.Length == 0) return;
        if (frame[0] == (byte)MessageType.AudioChunk)
        {
            try
            {
                var (header, pcm) = MessageCodec.DecodeAudioFrame(frame);
                Emit(new SyncConnectionEvent.Audio(header, pcm));
            }
            catch (Exception ex)
            {
                Emit(new SyncConnectionEvent.Disconnected($"bad audio frame: {ex.Message}"));
            }
            return;
        }
        try
        {
            var payload = MessageCodec.DecodeControl(frame);
            Emit(new SyncConnectionEvent.Control(payload));
        }
        catch (Exception ex)
        {
            Emit(new SyncConnectionEvent.Disconnected($"bad control frame: {ex.Message}"));
        }
    }

    private void Emit(SyncConnectionEvent evt)
    {
        Action<SyncConnectionEvent>? handler;
        lock (_handlerLock) handler = _onEvent;
        try { handler?.Invoke(evt); }
        catch { /* swallow handler exceptions so receive loop survives */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}

/// <summary>Events emitted by <see cref="SyncConnection"/>. Mirrors Swift SyncConnectionEvent.</summary>
public abstract record SyncConnectionEvent
{
    public sealed record Connected : SyncConnectionEvent;
    public sealed record Disconnected(string? Reason) : SyncConnectionEvent;
    public sealed record Control(ControlPayload Payload) : SyncConnectionEvent;
    public sealed record Audio(AudioChunkHeader Header, byte[] Pcm) : SyncConnectionEvent;
}
