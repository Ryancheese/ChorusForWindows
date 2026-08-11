using System.Net;
using System.Net.Sockets;
using ChorusCore.Protocol;

namespace ChorusCore.Network;

/// <summary>
/// TCP listener that accepts dual-TCP Speaker/Host connections on the fixed control port.
/// </summary>
public sealed class HostListener : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private bool _running;
    private bool _disposed;

    public ushort ListeningPort { get; private set; } = SyncBonjour.ControlPort;
    public string? LocalIPv4 { get; private set; }
    public bool IsListening => _running && _listener != null;

    /// <summary>Raised on the listener thread when a new peer connects.</summary>
    public event Action<SyncConnection>? ConnectionAccepted;

    /// <summary>Raised whenever listening state or address changes.</summary>
    public event Action? StatusChanged;

    public void Start(ushort port = SyncBonjour.ControlPort)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        ListeningPort = port;
        LocalIPv4 = LocalAddress.PrimaryIPv4();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        try
        {
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Server.ExclusiveAddressUse = false;
        }
        catch { /* best-effort — some platforms reject these before Bind */ }

        _listener.Start();
        _running = true;
        StatusChanged?.Invoke();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        var listener = _listener;
        while (_running && listener != null && !_disposed)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }

            var label = client.Client.RemoteEndPoint?.ToString() ?? "peer";
            var conn = new SyncConnection(client, label);
            ConnectionAccepted?.Invoke(conn);
        }
        _running = false;
        StatusChanged?.Invoke();
    }

    public void Stop()
    {
        _running = false;
        try { _cts.Cancel(); } catch { }
        var listener = _listener;
        _listener = null;
        try { listener?.Stop(); } catch { }
        try { listener?.Server.Close(); } catch { }
        if (!_disposed)
            _cts = new CancellationTokenSource();
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _cts.Dispose(); } catch { }
    }
}
