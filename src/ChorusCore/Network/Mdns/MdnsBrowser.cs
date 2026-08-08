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
/// Compatible with iOS Bonjour / NWListener advertisements.
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
    /// <summary>Key = instance FQDN (lower), e.g. "chorus-iphone._chorus._tcp.local".</summary>
    private readonly Dictionary<string, PendingPeer> _pending = new();

    private sealed class PendingPeer
    {
        public string InstanceName = "";
        public string HostName = "";
        public ushort Port;
    }

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

        bool changed = false;
        var cutoff = DateTime.UtcNow - PeerTimeout;
        lock (_lock)
        {
            var expired = _peers.Where(p => p.Value.LastSeen < cutoff).Select(p => p.Key).ToList();
            foreach (var key in expired)
            {
                _peers.Remove(key);
                _pending.Remove(key);
                changed = true;
            }
        }
        if (changed) PeersChanged?.Invoke();

        try
        {
            var endpoint = new IPEndPoint(MulticastAddress, MulticastPort);
            var ptr = DnsCodec.BuildQuery(ServiceType, DnsRecordType.PTR);
            _client.Send(ptr, ptr.Length, endpoint);

            // Follow up on incomplete discoveries (common when the first reply is PTR-only).
            List<(string Name, DnsRecordType Type)> followUps;
            lock (_lock)
            {
                followUps = new List<(string, DnsRecordType)>();
                foreach (var (key, p) in _pending)
                {
                    if (_peers.ContainsKey(key)) continue;
                    if (p.Port == 0)
                        followUps.Add((key, DnsRecordType.SRV));
                    else if (!string.IsNullOrEmpty(p.HostName))
                        followUps.Add((p.HostName, DnsRecordType.A));
                }
            }
            foreach (var (name, type) in followUps)
            {
                var q = DnsCodec.BuildQuery(name, type);
                _client.Send(q, q.Length, endpoint);
            }
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
        var aRecords = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        // Pass 1: collect A records (hostname → IPv4).
        foreach (var rec in all)
        {
            if (rec.Type != DnsRecordType.A || rec.Rdata.Length != 4) continue;
            if ((rec.Class & 0x7FFF) == 0) continue;
            var host = NormalizeHost(rec.Name);
            if (host.Length == 0) continue;
            aRecords[host] = new IPAddress(rec.Rdata);
        }

        // Pass 2: PTR / SRV → pending keyed by instance FQDN (not service type).
        foreach (var rec in all)
        {
            if ((rec.Class & 0x7FFF) == 0) continue;
            try
            {
                if (rec.Type == DnsRecordType.PTR)
                {
                    if (!IsServiceType(rec.Name)) continue;
                    if (!DnsCodec.TryParsePtr(rec.Rdata, data, rec.RdataOffset, out var instanceFull)) continue;
                    instanceFull = NormalizeHost(instanceFull);
                    if (instanceFull.Length == 0 || !IsChorusInstance(instanceFull)) continue;

                    string key = instanceFull.ToLowerInvariant();
                    string inst = InstanceLabel(instanceFull);
                    lock (_lock)
                    {
                        if (!_pending.TryGetValue(key, out var p))
                            _pending[key] = p = new PendingPeer();
                        if (!string.IsNullOrEmpty(inst)) p.InstanceName = inst;
                        TouchPeer(key);
                    }
                }
                else if (rec.Type == DnsRecordType.SRV)
                {
                    var instanceFull = NormalizeHost(rec.Name);
                    if (!IsChorusInstance(instanceFull)) continue;
                    if (!DnsCodec.TryParseSrv(rec.Rdata, data, rec.RdataOffset, out var port, out var target)) continue;

                    string key = instanceFull.ToLowerInvariant();
                    string inst = InstanceLabel(instanceFull);
                    string host = NormalizeHost(target);
                    lock (_lock)
                    {
                        if (!_pending.TryGetValue(key, out var p))
                            _pending[key] = p = new PendingPeer();
                        if (!string.IsNullOrEmpty(inst)) p.InstanceName = inst;
                        if (!string.IsNullOrEmpty(host)) p.HostName = host;
                        if (port != 0) p.Port = port;

                        if (_peers.TryGetValue(key, out var existing))
                        {
                            var ip = existing.IPAddress;
                            if (!string.IsNullOrEmpty(host) && aRecords.TryGetValue(host, out var fromPacket))
                                ip = fromPacket;
                            var updated = new DiscoveredPeer
                            {
                                InstanceName = string.IsNullOrEmpty(p.InstanceName) ? existing.InstanceName : p.InstanceName,
                                HostName = string.IsNullOrEmpty(host) ? existing.HostName : host,
                                IPAddress = ip,
                                Port = port != 0 ? port : existing.Port,
                                LastSeen = DateTime.UtcNow,
                            };
                            if (!SameEndpoint(existing, updated)) changed = true;
                            _peers[key] = updated;
                        }
                    }
                }
            }
            catch { /* malformed record — skip */ }
        }

        // Pass 3: promote pending → peers when we have HostName + Port + A.
        lock (_lock)
        {
            foreach (var (key, p) in _pending.ToList())
            {
                if (p.Port == 0 || string.IsNullOrEmpty(p.HostName)) continue;
                if (!aRecords.TryGetValue(p.HostName, out var ip)) continue;

                var inst = string.IsNullOrEmpty(p.InstanceName) ? key : p.InstanceName;
                var next = new DiscoveredPeer
                {
                    InstanceName = inst,
                    HostName = p.HostName,
                    IPAddress = ip,
                    Port = p.Port,
                    LastSeen = DateTime.UtcNow,
                };
                if (!_peers.TryGetValue(key, out var existing) || !SameEndpoint(existing, next))
                    changed = true;
                _peers[key] = next;
            }

            // Refresh LastSeen / IP for known peers when their A appears again.
            foreach (var (key, peer) in _peers.ToList())
            {
                if (!aRecords.TryGetValue(peer.HostName, out var ip)) continue;
                var next = new DiscoveredPeer
                {
                    InstanceName = peer.InstanceName,
                    HostName = peer.HostName,
                    IPAddress = ip,
                    Port = peer.Port,
                    LastSeen = DateTime.UtcNow,
                };
                if (!peer.IPAddress.Equals(ip)) changed = true;
                _peers[key] = next;
            }
        }

        if (changed) PeersChanged?.Invoke();
    }

    private void TouchPeer(string key)
    {
        if (!_peers.TryGetValue(key, out var existing)) return;
        _peers[key] = new DiscoveredPeer
        {
            InstanceName = existing.InstanceName,
            HostName = existing.HostName,
            IPAddress = existing.IPAddress,
            Port = existing.Port,
            LastSeen = DateTime.UtcNow,
        };
    }

    private static bool SameEndpoint(DiscoveredPeer a, DiscoveredPeer b) =>
        a.InstanceName == b.InstanceName
        && a.HostName.Equals(b.HostName, StringComparison.OrdinalIgnoreCase)
        && a.IPAddress.Equals(b.IPAddress)
        && a.Port == b.Port;

    private static bool IsServiceType(string name) =>
        NormalizeHost(name).Equals(ServiceType, StringComparison.OrdinalIgnoreCase);

    private static bool IsChorusInstance(string name) =>
        NormalizeHost(name).EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHost(string name) => (name ?? "").Trim().TrimEnd('.');

    private static string InstanceLabel(string instanceFull)
    {
        var n = NormalizeHost(instanceFull);
        if (n.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase))
            return n[..^(ServiceType.Length + 1)];
        return n;
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
