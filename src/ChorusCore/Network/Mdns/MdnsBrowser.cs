using System.Net;
using System.Net.Sockets;

namespace ChorusCore.Network.Mdns;

/// <summary>
/// A discovered peer on the LAN advertising a Chorus service. Resolved to a usable
/// IP+port combination that the Host can connect to.
/// </summary>
public sealed class DiscoveredPeer
{
    public required string InstanceName { get; init; } // e.g. "Chorus-DESKTOP-ABC"
    public required string HostName { get; init; }    // e.g. "DESKTOP-ABC.local"
    public required IPAddress IPAddress { get; init; }
    public required ushort Port { get; init; }
    public DateTime LastSeen { get; init; } = DateTime.UtcNow;

    public override string ToString() => $"{InstanceName} ({IPAddress})";
}

/// <summary>
/// Actively browses the LAN for <c>_chorus._tcp</c> services via mDNS. Sends a PTR
/// query periodically and parses responses (PTR + SRV + A) into <see cref="Peers"/>.
/// Stale entries are pruned after <see cref="PeerTimeout"/>.
/// </summary>
public sealed class MdnsBrowser : IDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MulticastPort = 5353;
    private const string ServiceType = "_chorus._tcp.local";

    private readonly object _lock = new();
    private UdpClient? _client;
    private Thread? _loopThread;
    private Timer? _queryTimer;
    private volatile bool _running;
    private readonly Dictionary<string, DiscoveredPeer> _peers = new();
    private readonly Dictionary<string, (string InstanceName, string HostName, ushort Port)> _pending = new();

    public IReadOnlyDictionary<string, DiscoveredPeer> Peers
    {
        get { lock (_lock) return new Dictionary<string, DiscoveredPeer>(_peers); }
    }

    public event Action? PeersChanged;
    public event Action<string>? ErrorOccurred;
    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public void Start()
    {
        if (_running) return;
        try
        {
            _client = new UdpClient();
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
            try { _client.JoinMulticastGroup(MulticastAddress); }
            catch (Exception ex) { ErrorOccurred?.Invoke($"mDNS join failed: {ex.Message}"); }

            _running = true;
            _loopThread = new Thread(ListenLoop) { IsBackground = true, Name = "chorus-mdns-browser" };
            _loopThread.Start();
            _queryTimer = new Timer(_ => SendQuery(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"mDNS browser start failed: {ex.Message}");
        }
    }

    private void SendQuery()
    {
        if (!_running || _client == null) return;

        // 清理过期 peer（超过 PeerTimeout 没收到响应的设备移除）
        bool changed = false;
        var cutoff = DateTime.UtcNow - PeerTimeout;
        lock (_lock)
        {
            var expired = _peers.Where(p => p.Value.LastSeen < cutoff).ToList();
            foreach (var (key, _) in expired)
            {
                _peers.Remove(key);
                _pending.Remove(key);
                changed = true;
            }
        }
        if (changed) PeersChanged?.Invoke();

        try
        {
            var packet = DnsCodec.BuildQuery(ServiceType, DnsRecordType.PTR);
            _client.Send(packet, packet.Length, new IPEndPoint(MulticastAddress, MulticastPort));
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"mDNS query failed: {ex.Message}");
        }
    }

    private void ListenLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running && _client != null)
        {
            byte[] data;
            try { data = _client.Receive(ref remote); }
            catch (SocketException) { break; }
            catch { break; }
            HandlePacket(data);
        }
    }

    private void HandlePacket(byte[] data)
    {
        var (answers, additionals) = DnsCodec.TryParseAnswers(data);
        if (answers.Count == 0 && additionals.Count == 0) return;

        var all = answers.Concat(additionals).ToList();
        bool changed = false;

        foreach (var rec in all)
        {
            if (rec.Class == 0) continue; // any cache flush ignored
            try
            {
                if (rec.Type == DnsRecordType.PTR)
                {
                    // PTR: _chorus._tcp.local → Chorus-DESKTOP-ABC._chorus._tcp.local
                    if (DnsCodec.TryParsePtr(rec.Rdata, out var instanceFull))
                    {
                        string key = rec.Name.ToLowerInvariant();
                        lock (_lock)
                        {
                            if (!_peers.TryGetValue(key, out var existing))
                            {
                                // Remember instance name; wait for SRV/A to complete
                                if (!_pending.TryGetValue(key, out var p)) p = ("", "", 0);
                                var inst = instanceFull.EndsWith("." + rec.Name, StringComparison.OrdinalIgnoreCase)
                                    ? instanceFull[..^(rec.Name.Length + 1)] : instanceFull;
                                _pending[key] = (inst, p.HostName, p.Port);
                            }
                        }
                    }
                }
                else if (rec.Type == DnsRecordType.SRV)
                {
                    // SRV: Chorus-DESKTOP-ABC._chorus._tcp.local → priority weight port target
                    if (DnsCodec.TryParseSrv(rec.Rdata, data, 0, out var port, out var target))
                    {
                        string key = rec.Name.ToLowerInvariant();
                        lock (_lock)
                        {
                            if (!_peers.TryGetValue(key, out var existing))
                            {
                                if (!_pending.TryGetValue(key, out var p)) p = ("", "", 0);
                                var inst = rec.Name.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase)
                                    ? rec.Name[..^(ServiceType.Length + 1)] : rec.Name;
                                _pending[key] = (inst, target, port);
                            }
                        }
                    }
                }
                else if (rec.Type == DnsRecordType.A)
                {
                    if (rec.Rdata.Length == 4)
                    {
                        var ip = new IPAddress(rec.Rdata);
                        // Match to pending by hostname
                        lock (_lock)
                        {
                            string? matchedKey = null;
                            foreach (var (k, p) in _pending)
                            {
                                if (p.HostName.Equals(rec.Name, StringComparison.OrdinalIgnoreCase) ||
                                    p.HostName.Equals(rec.Name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                                {
                                    matchedKey = k;
                                    if (!_peers.TryGetValue(k, out var ex))
                                    {
                                        _peers[k] = new DiscoveredPeer
                                        {
                                            InstanceName = p.InstanceName,
                                            HostName = p.HostName,
                                            IPAddress = ip,
                                            Port = p.Port,
                                            LastSeen = DateTime.UtcNow
                                        };
                                    }
                                    else
                                    {
                                        _peers[k] = new DiscoveredPeer
                                        {
                                            InstanceName = ex.InstanceName,
                                            HostName = ex.HostName,
                                            IPAddress = ip,
                                            Port = p.Port != 0 ? p.Port : ex.Port,
                                            LastSeen = DateTime.UtcNow
                                        };
                                    }
                                    changed = true;
                                    break;
                                }
                            }
                            if (matchedKey != null) _pending.Remove(matchedKey);
                        }
                    }
                }
            }
            catch { /* malformed packet — skip record */ }
        }

        if (changed) PeersChanged?.Invoke();
    }

    public void Dispose()
    {
        _running = false;
        _queryTimer?.Dispose();
        try { _client?.DropMulticastGroup(MulticastAddress); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}
