using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace HomePodCast.Protocol;

sealed record AirPlayDevice(string Id, string Name, IPAddress Address, int Port, string Model)
{
    public string ModelName => Model switch
    {
        var m when m.StartsWith("AudioAccessory1,") => "HomePod",
        var m when m.StartsWith("AudioAccessory5,") => "HomePod mini",
        var m when m.StartsWith("AudioAccessory6,") => "HomePod（第二代）",
        var m when m.StartsWith("AudioAccessory") => "HomePod",
        _ => Model,
    };
}

/// <summary>在局域网中用 mDNS 查询 _airplay._tcp，筛出 HomePod。</summary>
static class Mdns
{
    const string Service = "_airplay._tcp.local";

    public static List<AirPlayDevice> Browse(int timeoutMs = 1500)
    {
        var query = BuildQuery(Service);
        var sockets = new List<Socket>();
        foreach (var local in LocalAddresses())
        {
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(local, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(s);
            }
            catch { }
        }

        var records = new Records();
        var target = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        var deadline = Environment.TickCount64 + timeoutMs;
        long nextSend = 0;
        var buf = new byte[9000];
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                if (Environment.TickCount64 >= nextSend)
                {
                    foreach (var s in sockets) try { s.SendTo(query, target); } catch { }
                    nextSend = Environment.TickCount64 + 500;
                }
                var ready = sockets.ToList();
                Socket.Select(ready, null, null, 100_000);
                foreach (var s in ready)
                {
                    try
                    {
                        int n = s.Receive(buf);
                        Parse(buf.AsSpan(0, n), records);
                    }
                    catch { }
                }
            }
        }
        finally { foreach (var s in sockets) s.Dispose(); }

        var devices = new List<AirPlayDevice>();
        foreach (var instance in records.Instances)
        {
            if (!records.Srv.TryGetValue(instance, out var srv)) continue;
            if (!records.A.TryGetValue(srv.Host, out var ip)) continue;
            records.Txt.TryGetValue(instance, out var txt);
            txt ??= [];
            var model = txt.GetValueOrDefault("model", "");
            if (!model.StartsWith("AudioAccessory")) continue;
            var name = instance.EndsWith("." + Service) ? instance[..^(Service.Length + 1)] : instance;
            var id = txt.GetValueOrDefault("deviceid", name);
            devices.Add(new AirPlayDevice(id, Unescape(name), ip, srv.Port, model));
        }
        return devices.DistinctBy(d => d.Id).OrderBy(d => d.Name, StringComparer.CurrentCulture).ToList();
    }

    static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) yield return ua.Address;
        }
    }

    static byte[] BuildQuery(string name)
    {
        var ms = new MemoryStream();
        ms.Write([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        ms.Write([0, 12, 0, 1]); // PTR, IN
        return ms.ToArray();
    }

    sealed class Records
    {
        public HashSet<string> Instances = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, (string Host, int Port)> Srv = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> Txt = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IPAddress> A = new(StringComparer.OrdinalIgnoreCase);
    }

    static void Parse(ReadOnlySpan<byte> p, Records r)
    {
        if (p.Length < 12) return;
        int qd = p[4] << 8 | p[5];
        int total = (p[6] << 8 | p[7]) + (p[8] << 8 | p[9]) + (p[10] << 8 | p[11]);
        int pos = 12;
        for (int i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }
        for (int i = 0; i < total && pos + 10 <= p.Length; i++)
        {
            var name = ReadName(p, ref pos);
            int type = p[pos] << 8 | p[pos + 1];
            int len = p[pos + 8] << 8 | p[pos + 9];
            pos += 10;
            if (pos + len > p.Length) return;
            int rd = pos;
            switch (type)
            {
                case 12: // PTR
                    if (name.Equals(Service, StringComparison.OrdinalIgnoreCase)) r.Instances.Add(ReadName(p, ref rd));
                    break;
                case 33: // SRV
                    r.Srv[name] = (ReadSrvHost(p, pos), p[pos + 4] << 8 | p[pos + 5]);
                    break;
                case 16: // TXT
                    var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int t = pos; t < pos + len;)
                    {
                        int l = p[t];
                        var s = Encoding.UTF8.GetString(p.Slice(t + 1, Math.Min(l, pos + len - t - 1)));
                        int eq = s.IndexOf('=');
                        if (eq > 0) kv[s[..eq]] = s[(eq + 1)..];
                        t += 1 + l;
                    }
                    r.Txt[name] = kv;
                    break;
                case 1: // A
                    if (len == 4) r.A[name] = new IPAddress(p.Slice(pos, 4));
                    break;
            }
            pos += len;
        }
    }

    static string ReadSrvHost(ReadOnlySpan<byte> p, int rdata)
    {
        int pos = rdata + 6;
        return ReadName(p, ref pos);
    }

    static string ReadName(ReadOnlySpan<byte> p, ref int pos)
    {
        var labels = new List<string>();
        int cur = pos;
        bool jumped = false;
        for (int guard = 0; guard < 64 && cur < p.Length; guard++)
        {
            int len = p[cur];
            if (len == 0) { cur++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (cur + 1 >= p.Length) break;
                int ptr = (len & 0x3F) << 8 | p[cur + 1];
                if (!jumped) pos = cur + 2;
                jumped = true;
                cur = ptr;
                continue;
            }
            if (cur + 1 + len > p.Length) break;
            labels.Add(Encoding.UTF8.GetString(p.Slice(cur + 1, len)));
            cur += 1 + len;
        }
        if (!jumped) pos = cur;
        return string.Join('.', labels);
    }

    static string Unescape(string s) => s.Replace("\\032", " ").Replace("\\.", ".");
}
