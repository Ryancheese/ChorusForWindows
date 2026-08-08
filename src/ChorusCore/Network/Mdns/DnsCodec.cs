using System.Net;
using System.Text;

namespace ChorusCore.Network.Mdns;

/// <summary>
/// Minimal DNS message codec for mDNS service advertising. Only implements the
/// subset needed to parse incoming questions and build PTR/SRV/TXT/A responses.
/// RFC 1035 wire format, big-endian.
/// </summary>
internal enum DnsRecordType : ushort
{
    A = 1,
    PTR = 12,
    TXT = 16,
    SRV = 33,
    ANY = 255,
}

internal sealed class DnsQuestion
{
    public string QName { get; set; } = "";
    public DnsRecordType QType { get; set; }
    public ushort QClass { get; set; } = 1;
}

internal sealed class DnsResourceRecord
{
    public string Name { get; set; } = "";
    public DnsRecordType Type { get; set; }
    public ushort Class { get; set; } = 1;
    public uint Ttl { get; set; }
    public byte[] Rdata { get; set; } = Array.Empty<byte>();
}

internal static class DnsCodec
{
    /// <summary>Parse the question section of a DNS packet. Returns empty + ok=false on any malformed input.</summary>
    public static (List<DnsQuestion> Questions, bool Ok) TryParseQuestions(byte[] data)
    {
        var questions = new List<DnsQuestion>();
        if (data.Length < 12) return (questions, false);
        ushort qdCount = (ushort)((data[4] << 8) | data[5]);
        int offset = 12;
        for (int i = 0; i < qdCount; i++)
        {
            if (!TryReadName(data, ref offset, out var name)) return (questions, false);
            if (offset + 4 > data.Length) return (questions, false);
            var qtype = (DnsRecordType)((data[offset] << 8) | data[offset + 1]);
            var qclass = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
            offset += 4;
            questions.Add(new DnsQuestion { QName = name, QType = qtype, QClass = qclass });
        }
        return (questions, true);
    }

    private static bool TryReadName(byte[] data, ref int offset, out string name)
    {
        name = "";
        var sb = new StringBuilder();
        int safety = 0;
        while (offset < data.Length && data[offset] != 0)
        {
            if (++safety > 128) return false;
            byte len = data[offset];
            if ((len & 0xC0) == 0xC0)
            {
                // Compression pointer — we don't follow it (questions don't normally use them).
                offset += 2;
                break;
            }
            offset++;
            if (offset + len > data.Length) return false;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(data, offset, len));
            offset += len;
        }
        if (offset < data.Length && data[offset] == 0) offset++;
        name = sb.ToString();
        return true;
    }

    /// <summary>Build a multicast DNS response (authoritative answer). QDCOUNT=0.</summary>
    public static byte[] BuildResponse(IReadOnlyList<DnsResourceRecord> answers, IReadOnlyList<DnsResourceRecord>? additionals = null)
    {
        additionals ??= Array.Empty<DnsResourceRecord>();
        using var ms = new MemoryStream(256);
        // Header
        WriteU16(ms, 0);        // ID = 0 (mDNS)
        WriteU16(ms, 0x8400);   // flags: QR=1, AA=1
        WriteU16(ms, 0);        // QDCOUNT
        WriteU16(ms, (ushort)answers.Count);
        WriteU16(ms, 0);        // NSCOUNT
        WriteU16(ms, (ushort)additionals.Count);
        foreach (var r in answers) WriteRecord(ms, r);
        foreach (var r in additionals) WriteRecord(ms, r);
        return ms.ToArray();
    }

    private static void WriteRecord(MemoryStream ms, DnsResourceRecord r)
    {
        WriteName(ms, r.Name);
        WriteU16(ms, (ushort)r.Type);
        WriteU16(ms, (ushort)(r.Class | 0x8000)); // cache-flush bit
        WriteU32(ms, r.Ttl);
        WriteU16(ms, (ushort)r.Rdata.Length);
        ms.Write(r.Rdata, 0, r.Rdata.Length);
    }

    private static void WriteName(MemoryStream ms, string name)
    {
        if (string.IsNullOrEmpty(name)) { ms.WriteByte(0); return; }
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length == 0 || bytes.Length > 63) continue;
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0);
    }

    private static void WriteU16(MemoryStream ms, ushort v)
    {
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }

    private static void WriteU32(MemoryStream ms, uint v)
    {
        ms.WriteByte((byte)(v >> 24));
        ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }

    public static byte[] BuildSrvRdata(string target, ushort port)
    {
        using var ms = new MemoryStream();
        WriteU16(ms, 0);   // priority
        WriteU16(ms, 0);   // weight
        WriteU16(ms, port);
        WriteName(ms, target);
        return ms.ToArray();
    }

    public static byte[] BuildPtrRdata(string target)
    {
        using var ms = new MemoryStream();
        WriteName(ms, target);
        return ms.ToArray();
    }

    public static byte[] BuildTxtRdata(IEnumerable<KeyValuePair<string, string>> kvps)
    {
        using var ms = new MemoryStream();
        bool any = false;
        foreach (var kv in kvps)
        {
            var s = $"{kv.Key}={kv.Value}";
            var bytes = Encoding.ASCII.GetBytes(s);
            if (bytes.Length > 255) continue;
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
            any = true;
        }
        if (!any) ms.WriteByte(0); // TXT requires at least one character-string
        return ms.ToArray();
    }

    public static byte[] BuildARdata(IPAddress addr) => addr.GetAddressBytes();

    /// <summary>Build a multicast DNS query for the given service name.</summary>
    public static byte[] BuildQuery(string qname, DnsRecordType qtype)
    {
        using var ms = new MemoryStream(64);
        WriteU16(ms, 0);            // ID = 0 (mDNS)
        WriteU16(ms, 0);            // flags: standard query
        WriteU16(ms, 1);            // QDCOUNT
        WriteU16(ms, 0);            // ANCOUNT
        WriteU16(ms, 0);            // NSCOUNT
        WriteU16(ms, 0);            // ARCOUNT
        WriteName(ms, qname);
        WriteU16(ms, (ushort)qtype);
        WriteU16(ms, 1);            // QCLASS = IN
        return ms.ToArray();
    }

    /// <summary>Parse Answer + Additional sections of a DNS packet. Returns both lists.</summary>
    public static (List<DnsResourceRecord> Answers, List<DnsResourceRecord> Additionals) TryParseAnswers(byte[] data)
    {
        var answers = new List<DnsResourceRecord>();
        var additionals = new List<DnsResourceRecord>();
        if (data.Length < 12) return (answers, additionals);

        ushort qdCount = (ushort)((data[4] << 8) | data[5]);
        ushort anCount = (ushort)((data[6] << 8) | data[7]);
        ushort arCount = (ushort)((data[10] << 8) | data[11]);

        int offset = 12;
        // skip questions
        for (int i = 0; i < qdCount; i++)
        {
            if (!TryReadName(data, ref offset, out _)) return (answers, additionals);
            if (offset + 4 > data.Length) return (answers, additionals);
            offset += 4;
        }
        // read answers
        for (int i = 0; i < anCount; i++)
        {
            if (!TryReadRecord(data, ref offset, out var rec)) return (answers, additionals);
            answers.Add(rec);
        }
        // read additionals
        for (int i = 0; i < arCount; i++)
        {
            if (!TryReadRecord(data, ref offset, out var rec)) return (answers, additionals);
            additionals.Add(rec);
        }
        return (answers, additionals);
    }

    private static bool TryReadRecord(byte[] data, ref int offset, out DnsResourceRecord rec)
    {
        rec = new DnsResourceRecord();
        if (!TryReadName(data, ref offset, out var name)) return false;
        if (offset + 10 > data.Length) return false;
        var type = (DnsRecordType)((data[offset] << 8) | data[offset + 1]);
        var cls = (ushort)((data[offset + 2] << 8) | data[offset + 3]);
        var ttl = (uint)((data[offset + 4] << 24) | (data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7]);
        ushort rdlen = (ushort)((data[offset + 8] << 8) | data[offset + 9]);
        offset += 10;
        if (offset + rdlen > data.Length) return false;
        var rdata = new byte[rdlen];
        Buffer.BlockCopy(data, offset, rdata, 0, rdlen);
        offset += rdlen;
        rec = new DnsResourceRecord { Name = name, Type = type, Class = cls, Ttl = ttl, Rdata = rdata };
        return true;
    }

    /// <summary>Parse SRV rdata → (priority, weight, port, target). Target may contain DNS compression pointers.</summary>
    public static bool TryParseSrv(byte[] rdata, byte[] packet, int packetOffset, out ushort port, out string target)
    {
        port = 0;
        target = "";
        if (rdata.Length < 7) return false;
        port = (ushort)((rdata[4] << 8) | rdata[5]);
        int offset = 6;
        return TryReadName(rdata, ref offset, out target);
    }

    /// <summary>Parse PTR rdata → instance name (may contain DNS compression pointers).</summary>
    public static bool TryParsePtr(byte[] rdata, out string name)
    {
        name = "";
        int offset = 0;
        return TryReadName(rdata, ref offset, out name);
    }
}
