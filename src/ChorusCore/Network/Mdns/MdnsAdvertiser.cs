using System.Net;
using System.Net.Sockets;

namespace ChorusCore.Network.Mdns;

/// <summary>
/// Advertises a Bonjour/mDNS service on the LAN so Speakers can auto-discover the Host
/// without typing an IP. Pure .NET — does NOT depend on the system Bonjour service
/// (which Windows doesn't ship by default). Listens on UDP 5353 multicast 224.0.0.251,
/// responds to PTR queries for the service type, and sends periodic unsolicited
/// announcements. Compatible with iOS NWBrowser and Android NSD.
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MulticastPort = 5353;

    private readonly string _instanceName;   // e.g. "Chorus-DESKTOP-XYZ"
    private readonly string _serviceType;    // "_chorus._tcp.local"
    private readonly string _hostName;       // "DESKTOP-XYZ.local"
    private readonly IPAddress _address;
    private readonly ushort _port;

    private UdpClient? _client;
    private volatile bool _running;
    private Thread? _listenThread;
    private Timer? _announceTimer;

    public bool IsAdvertising => _running;
    public event Action<string>? ErrorOccurred;

    public MdnsAdvertiser(string instanceName, string hostName, IPAddress address, ushort port,
        string serviceType = "_chorus._tcp.local")
    {
        _instanceName = instanceName;
        _serviceType = serviceType;
        _hostName = hostName.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? hostName : hostName + ".local";
        _address = address;
        _port = port;
    }

    public void Start()
    {
        if (_running) return;
        _client = new UdpClient();
        _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _client.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        try { _client.JoinMulticastGroup(MulticastAddress); }
        catch (Exception ex) { ErrorOccurred?.Invoke($"mDNS multicast join failed: {ex.Message}"); }

        _running = true;
        _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "chorus-mdns" };
        _listenThread.Start();

        // Announce immediately so Speakers browsing right now pick us up.
        SendAnnounce();
        // Re-announce every 30s to refresh caches.
        _announceTimer = new Timer(_ => SendAnnounce(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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
        // Only respond to queries (QR bit = 0).
        if (data.Length < 12 || (data[2] & 0x80) != 0) return;

        var (questions, ok) = DnsCodec.TryParseQuestions(data);
        if (!ok) return;

        foreach (var q in questions)
        {
            bool matchService = q.QName.Equals(_serviceType, StringComparison.OrdinalIgnoreCase);
            bool matchInstance = q.QName.Equals($"{_instanceName}.{_serviceType}", StringComparison.OrdinalIgnoreCase);
            if (!matchService && !matchInstance) continue;

            if (matchService && (q.QType == DnsRecordType.PTR || q.QType == DnsRecordType.ANY))
            {
                SendResponse(ptrInAnswers: true);
            }
            else if (matchInstance)
            {
                SendResponse(ptrInAnswers: false);
            }
        }
    }

    private void SendResponse(bool ptrInAnswers)
    {
        var instanceFull = $"{_instanceName}.{_serviceType}";
        var answers = new List<DnsResourceRecord>();
        var additionals = new List<DnsResourceRecord>();

        if (ptrInAnswers)
        {
            answers.Add(new DnsResourceRecord
            {
                Name = _serviceType,
                Type = DnsRecordType.PTR,
                Class = 1,
                Ttl = 4500,
                Rdata = DnsCodec.BuildPtrRdata(instanceFull),
            });
        }

        additionals.Add(new DnsResourceRecord
        {
            Name = instanceFull,
            Type = DnsRecordType.SRV,
            Class = 1,
            Ttl = 120,
            Rdata = DnsCodec.BuildSrvRdata(_hostName, _port),
        });
        additionals.Add(new DnsResourceRecord
        {
            Name = instanceFull,
            Type = DnsRecordType.TXT,
            Class = 1,
            Ttl = 4500,
            Rdata = DnsCodec.BuildTxtRdata(new[] { new KeyValuePair<string, string>("host", _instanceName) }),
        });
        additionals.Add(new DnsResourceRecord
        {
            Name = _hostName,
            Type = DnsRecordType.A,
            Class = 1,
            Ttl = 120,
            Rdata = DnsCodec.BuildARdata(_address),
        });

        var packet = DnsCodec.BuildResponse(answers, additionals);
        MulticastSend(packet);
    }

    private void SendAnnounce()
    {
        var instanceFull = $"{_instanceName}.{_serviceType}";
        var answers = new List<DnsResourceRecord>
        {
            new() { Name = _serviceType, Type = DnsRecordType.PTR, Class = 1, Ttl = 4500,
                    Rdata = DnsCodec.BuildPtrRdata(instanceFull) },
            new() { Name = instanceFull, Type = DnsRecordType.SRV, Class = 1, Ttl = 120,
                    Rdata = DnsCodec.BuildSrvRdata(_hostName, _port) },
            new() { Name = instanceFull, Type = DnsRecordType.TXT, Class = 1, Ttl = 4500,
                    Rdata = DnsCodec.BuildTxtRdata(new[] { new KeyValuePair<string, string>("host", _instanceName) }) },
            new() { Name = _hostName, Type = DnsRecordType.A, Class = 1, Ttl = 120,
                    Rdata = DnsCodec.BuildARdata(_address) },
        };
        var packet = DnsCodec.BuildResponse(answers, null);
        MulticastSend(packet);
    }

    private void MulticastSend(byte[] packet)
    {
        if (_client == null || !_running) return;
        try { _client.Send(packet, packet.Length, new IPEndPoint(MulticastAddress, MulticastPort)); }
        catch (Exception ex) { ErrorOccurred?.Invoke($"mDNS send failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        _running = false;
        _announceTimer?.Dispose();
        try { _client?.DropMulticastGroup(MulticastAddress); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}
