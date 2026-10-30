using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HomePodCast.Protocol;

sealed record RtspResponse(int Code, Dictionary<string, string> Headers, byte[] Body)
{
    public object? Plist => Body.Length > 0 ? BPlist.Read(Body) : null;
}

/// <summary>到设备 7000 端口的 HTTP/RTSP 连接，配对完成后切换为 HAP 加密。</summary>
sealed class RtspConnection : IDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly object _lock = new();
    readonly byte[] _readBuf = new byte[16384];
    readonly List<byte> _plain = [];
    HapCipher? _cipher;
    int _cseq;

    public IPAddress LocalIp { get; }
    public IPAddress RemoteIp { get; }

    public uint SessionId { get; } = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
    readonly string _dacpId = Random.Shared.NextInt64().ToString("X16");
    readonly uint _activeRemote = (uint)Random.Shared.NextInt64(1, uint.MaxValue);

    public string Uri => $"rtsp://{LocalIp}/{SessionId}";

    public RtspConnection(IPAddress ip, int port)
    {
        _tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true, ReceiveTimeout = 6000, SendTimeout = 6000 };
        var connect = _tcp.ConnectAsync(ip, port);
        if (!connect.Wait(4000)) { _tcp.Dispose(); throw new TimeoutException("连接设备超时"); }
        if (connect.IsFaulted) throw connect.Exception!.InnerException!;
        _stream = _tcp.GetStream();
        LocalIp = ((IPEndPoint)_tcp.Client.LocalEndPoint!).Address;
        RemoteIp = ip;
    }

    public void EnableEncryption(byte[] outKey, byte[] inKey) => _cipher = new HapCipher(outKey, inKey);

    /// <summary>配对阶段使用的 HTTP 请求。</summary>
    public RtspResponse Http(string method, string path, byte[]? body, params (string, string)[] headers) =>
        Send(method, path, "HTTP/1.1", null, headers, body);

    /// <summary>带 CSeq 等会话头的 RTSP 请求；body 为字典时按二进制 plist 发送。</summary>
    public RtspResponse Rtsp(string method, string? uri = null, object? body = null, string? contentType = null,
        params (string, string)[] headers)
    {
        lock (_lock)
        {
            var hdrs = new List<(string, string)>
            {
                ("CSeq", (_cseq++).ToString()),
                ("DACP-ID", _dacpId),
                ("Active-Remote", _activeRemote.ToString()),
                ("Client-Instance", _dacpId),
            };
            hdrs.AddRange(headers);
            byte[]? data = body switch
            {
                null => null,
                byte[] b => b,
                string s => Encoding.UTF8.GetBytes(s),
                _ => BPlist.Write(body),
            };
            if (body is Dictionary<string, object>) contentType = "application/x-apple-binary-plist";
            return Send(method, uri ?? Uri, "RTSP/1.0", contentType, hdrs, data);
        }
    }

    RtspResponse Send(string method, string uri, string protocol, string? contentType,
        IEnumerable<(string Key, string Value)> headers, byte[]? body)
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            sb.Append($"{method} {uri} {protocol}\r\n");
            var hs = headers.ToList();
            if (!hs.Any(h => h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)))
                sb.Append("User-Agent: AirPlay/550.10\r\n");
            if (contentType != null) sb.Append($"Content-Type: {contentType}\r\n");
            if (body is { Length: > 0 }) sb.Append($"Content-Length: {body.Length}\r\n");
            foreach (var (k, v) in hs) sb.Append($"{k}: {v}\r\n");
            sb.Append("\r\n");

            var msg = Encoding.UTF8.GetBytes(sb.ToString());
            if (body is { Length: > 0 }) msg = [.. msg, .. body];
            var wire = _cipher != null ? _cipher.Encrypt(msg) : msg;
            _stream.Write(wire);
            return ReadResponse();
        }
    }

    RtspResponse ReadResponse()
    {
        while (true)
        {
            var resp = HttpParser.TryParse(_plain, isRequest: false);
            if (resp != null) return new RtspResponse(resp.Value.Code, resp.Value.Headers, resp.Value.Body);

            int n = _stream.Read(_readBuf);
            if (n <= 0) throw new IOException("设备断开了连接");
            _plain.AddRange(_cipher != null ? _cipher.Decrypt(_readBuf.AsSpan(0, n)) : _readBuf.AsSpan(0, n).ToArray());
        }
    }

    public void Dispose()
    {
        try { _tcp.Dispose(); } catch { }
        _cipher?.Dispose();
    }
}

static class HttpParser
{
    public readonly record struct Message(string FirstLine, int Code, Dictionary<string, string> Headers, byte[] Body);

    /// <summary>尝试从缓冲区头部取出一条完整的 HTTP/RTSP 消息，成功时从缓冲区移除。</summary>
    public static Message? TryParse(List<byte> buf, bool isRequest)
    {
        int end = -1;
        for (int i = 3; i < buf.Count; i++)
            if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r') { end = i + 1; break; }
        if (end < 0) return null;

        var head = Encoding.UTF8.GetString(buf.GetRange(0, end).ToArray());
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        int len = headers.TryGetValue("Content-Length", out var l) && int.TryParse(l, out var li) ? li : 0;
        if (buf.Count < end + len) return null;

        var body = buf.GetRange(end, len).ToArray();
        buf.RemoveRange(0, end + len);
        int code = 0;
        if (!isRequest)
        {
            var parts = lines[0].Split(' ');
            if (parts.Length > 1) int.TryParse(parts[1], out code);
        }
        return new Message(lines[0], code, headers, body);
    }
}
