using System.Net;
using System.Net.Sockets;
using ChorusCore.Protocol;

namespace ChorusCore.Network;

/// <summary>
/// TCP listener that accepts Speaker connections on the fixed control port.
/// This is the Windows equivalent of the Swift <c>PeerAdvertiser</c> without the
/// Bonjour broadcast (Speakers connect by manual IP — the Mac version has the same
/// fallback when Bonjour is blocked). mDNS broadcast can be layered on later.
/// </summary>
public sealed class HostListener : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private bool _running;
    private bool _disposed;

    public ushort ListeningPort { get; private set; } = SyncBonjour.ControlPort;
    public string? LocalIPv4 { get; private set; }
    public bool IsListening => _running;

    /// <summary>Raised on the listener thread when a new Speaker connects.</summary>
    public event Action<SyncConnection>? ConnectionAccepted;

    /// <summary>Raised whenever listening state or address changes.</summary>
    public event Action? StatusChanged;

    public void Start(ushort port = SyncBonjour.ControlPort)
    {
        Stop();
        ListeningPort = port;
        LocalIPv4 = LocalAddress.PrimaryIPv4();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start(); // 端口被占时抛 SocketException，由调用方捕获显示友好错误
        _running = true;
        StatusChanged?.Invoke();
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        var token = _cts.Token;
        while (_running && _listener != null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
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
        if (_disposed) return;
        _running = false;
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cts = new CancellationTokenSource();
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
