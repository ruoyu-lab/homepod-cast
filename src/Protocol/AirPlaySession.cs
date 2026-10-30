using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HomePodCast.Audio;

namespace HomePodCast.Protocol;

/// <summary>
/// 一次到 HomePod 的 AirPlay 2 实时音频推流会话：
/// 临时配对 → 加密 RTSP → SETUP 事件/音频通道 → 按时钟节拍发送加密 RTP 包。
/// </summary>
sealed partial class AirPlaySession : IDisposable
{
    const int SampleRate = 44100;
    const int FramesPerPacket = 352;
    const int BacklogSize = 1024;

    readonly IPAddress _ip;
    readonly int _port;
    readonly AudioRing _ring;
    readonly int _latencyFrames;

    RtspConnection? _rtsp;
    TcpClient? _eventTcp;
    Socket? _timing, _control, _audio;
    ChaCha20Poly1305? _audioCipher;
    IPEndPoint? _remoteControl;
    readonly byte[][] _backlog = new byte[BacklogSize][];
    readonly ushort[] _backlogSeq = new ushort[BacklogSize];

    volatile bool _running;
    readonly List<Thread> _threads = [];
    int _failed, _timingRequests;

    /// <summary>会话意外中断时触发（参数为原因），只会触发一次。</summary>
    public event Action<string>? Failed;

    public AirPlaySession(IPAddress ip, int port, AudioRing ring, int latencyMs)
    {
        _ip = ip;
        _port = port;
        _ring = ring;
        _latencyFrames = Math.Clamp(latencyMs, 20, 2000) * SampleRate / 1000;
    }

    public void Start(double volumePercent)
    {
        _rtsp = new RtspConnection(_ip, _port);
        PairTransient(_rtsp, out var sharedSecret);

        _timing = UdpBind(_rtsp.LocalIp);
        _control = UdpBind(_rtsp.LocalIp);

        // HomePod 在回应 SETUP 之前会先来对时，所以时间同步和控制端口要先就绪
        _running = true;
        StartThread(TimingLoop, "timing");
        StartThread(ControlLoop, "control");

        // 第一次 SETUP：建立会话与事件通道
        RtspResponse setup;
        try { setup = SetupSession(); }
        catch (IOException) when (_timingRequests == 0)
        {
            // 设备迟迟不回应，而且一次对时请求都没收到：几乎可以确定是被防火墙拦了
            throw new FirewallBlockedException();
        }
        Expect(setup, "SETUP");
        var eventPort = Convert.ToInt32((setup.Plist as Dictionary<string, object>)?.GetValueOrDefault("eventPort") ?? 0L);
        StartEventChannel(sharedSecret, eventPort);

        // 第二次 SETUP：实时 PCM 音频流
        var shk = RandomNumberGenerator.GetBytes(32);
        var streamSetup = _rtsp.Rtsp("SETUP", body: new Dictionary<string, object>
        {
            ["streams"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["audioFormat"] = 0x800,          // PCM 44100Hz 16bit 立体声
                    ["audioMode"] = "default",
                    ["controlPort"] = ((IPEndPoint)_control.LocalEndPoint!).Port,
                    ["ct"] = 1,                       // 原始 PCM，不压缩
                    ["isMedia"] = true,
                    ["latencyMax"] = 88200,
                    ["latencyMin"] = Math.Min(_latencyFrames, 11025),
                    ["shk"] = shk,
                    ["spf"] = FramesPerPacket,
                    ["sr"] = SampleRate,
                    ["type"] = 0x60,                  // 实时音频
                    ["supportsDynamicStreamID"] = false,
                    ["streamConnectionID"] = (long)_rtsp.SessionId,
                },
            },
        });
        Expect(streamSetup, "SETUP stream");
        var stream = ((streamSetup.Plist as Dictionary<string, object>)?["streams"] as List<object>)?[0] as Dictionary<string, object>
            ?? throw new IOException("设备没有返回音频端口");
        int dataPort = Convert.ToInt32(stream["dataPort"]);
        int controlPort = Convert.ToInt32(stream["controlPort"]);
        _remoteControl = new IPEndPoint(_ip, controlPort);
        _audioCipher = new ChaCha20Poly1305(shk);

        _audio = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _audio.Connect(new IPEndPoint(_ip, dataPort));

        Expect(_rtsp.Rtsp("RECORD"), "RECORD");

        var seq = (ushort)Random.Shared.Next(65536);
        Expect(_rtsp.Rtsp("FLUSH", headers: [("Range", "npt=0-"), ("Session", "0"),
            ("RTP-Info", $"seq={seq};rtptime={(uint)_latencyFrames}")]), "FLUSH");

        SetVolume(volumePercent);

        var audio = new Thread(() => AudioLoop(seq)) { IsBackground = true, Name = "audio", Priority = ThreadPriority.Highest };
        _threads.Add(audio);
        audio.Start();
        StartThread(FeedbackLoop, "feedback");
    }

    RtspResponse SetupSession() =>
        _rtsp!.Rtsp("SETUP", body: new Dictionary<string, object>
        {
            ["deviceID"] = "AA:BB:CC:DD:EE:FF",
            ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
            ["timingPort"] = ((IPEndPoint)_timing!.LocalEndPoint!).Port,
            ["timingProtocol"] = "NTP",
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["macAddress"] = "AA:BB:CC:DD:EE:FF",
            ["model"] = "iPhone14,3",
            ["name"] = "HomePodCast",
            ["osBuildVersion"] = "20F66",
            ["osName"] = "iPhone OS",
            ["osVersion"] = "16.5",
            ["senderSupportsRelay"] = false,
            ["sourceVersion"] = "690.7.1",
            ["statsCollectionEnabled"] = false,
        });

    public void SetVolume(double percent)
    {
        // AirPlay 音量单位是 dB：-30 到 0，-144 表示静音
        double db = percent <= 0.01 ? -144.0 : -30.0 + 30.0 * Math.Clamp(percent, 0, 100) / 100.0;
        try
        {
            _rtsp?.Rtsp("SET_PARAMETER", body: $"volume: {db.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}",
                contentType: "text/parameters");
        }
        catch (Exception ex) { Fail("音量设置失败：" + ex.Message); }
    }

    // ---------------- 配对 ----------------

    static readonly (string, string)[] PairHeaders =
    [
        ("User-Agent", "AirPlay/320.20"),
        ("Connection", "keep-alive"),
        ("X-Apple-HKP", "4"),
        ("Content-Type", "application/octet-stream"),
    ];

    /// <summary>HomePod 的临时配对：固定 PIN 3939 的 SRP（M1-M4），之后整条连接加密。</summary>
    static void PairTransient(RtspConnection c, out byte[] sharedSecret)
    {
        c.Http("POST", "/pair-pin-start", null, PairHeaders);

        var m2 = c.Http("POST", "/pair-setup",
            Tlv8.Write((Tlv8.Method, [0x00]), (Tlv8.SeqNo, [0x01]), (Tlv8.Flags, [0x10])), PairHeaders);
        var t2 = Tlv8.Read(m2.Body);
        if (t2.ContainsKey(Tlv8.Error) || !t2.ContainsKey(Tlv8.Salt))
            throw new IOException("设备拒绝了配对（可能需要在“家庭”App 中允许所有人使用隔空播放）");

        var srp = new SrpClient("Pair-Setup", "3939");
        var proof = srp.ComputeProof(t2[Tlv8.Salt], t2[Tlv8.PublicKey]);
        var m4 = c.Http("POST", "/pair-setup",
            Tlv8.Write((Tlv8.SeqNo, [0x03]), (Tlv8.PublicKey, srp.PublicKey), (Tlv8.Proof, proof)), PairHeaders);
        var t4 = Tlv8.Read(m4.Body);
        if (t4.ContainsKey(Tlv8.Error) || (t4.TryGetValue(Tlv8.Proof, out var m2Proof) && !srp.VerifyServerProof(m2Proof)))
            throw new IOException("配对校验失败");

        sharedSecret = srp.SessionKey;
        c.EnableEncryption(
            Hap.Hkdf(sharedSecret, "Control-Salt", "Control-Write-Encryption-Key"),
            Hap.Hkdf(sharedSecret, "Control-Salt", "Control-Read-Encryption-Key"));
    }

    // ---------------- 事件通道 ----------------

    void StartEventChannel(byte[] secret, int port)
    {
        if (port == 0) return;
        _eventTcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        for (int attempt = 0; ; attempt++)
        {
            try { _eventTcp.Connect(_ip, port); break; }
            catch (SocketException) when (attempt < 4) { Thread.Sleep(300); }
        }
        // 方向与控制通道相反：设备写、我们读
        var cipher = new HapCipher(
            Hap.Hkdf(secret, "Events-Salt", "Events-Read-Encryption-Key"),
            Hap.Hkdf(secret, "Events-Salt", "Events-Write-Encryption-Key"));
        var tcp = _eventTcp;
        StartThread(() =>
        {
            var stream = tcp.GetStream();
            var buf = new byte[8192];
            var plain = new List<byte>();
            try
            {
                while (_running)
                {
                    int n = stream.Read(buf);
                    if (n <= 0) break;
                    plain.AddRange(cipher.Decrypt(buf.AsSpan(0, n)));
                    while (HttpParser.TryParse(plain, isRequest: true) is { } req)
                    {
                        var proto = req.FirstLine.Split(' ').LastOrDefault() ?? "RTSP/1.0";
                        var sb = new StringBuilder($"{proto} 200 OK\r\nContent-Length: 0\r\nAudio-Latency: 0\r\n");
                        if (req.Headers.TryGetValue("Server", out var server)) sb.Append($"Server: {server}\r\n");
                        if (req.Headers.TryGetValue("CSeq", out var cseq)) sb.Append($"CSeq: {cseq}\r\n");
                        sb.Append("\r\n");
                        stream.Write(cipher.Encrypt(Encoding.UTF8.GetBytes(sb.ToString())));
                    }
                }
            }
            catch when (!_running) { }
            catch { }
            if (_running) Fail("HomePod 结束了这次播放（可能被其他设备接管）");
        }, "events");
    }

    // ---------------- 时间同步 / 控制 ----------------

    void TimingLoop()
    {
        var buf = new byte[128];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var resp = new byte[32];
        while (_running)
        {
            int n;
            try { n = _timing!.ReceiveFrom(buf, ref from); } catch { if (!_running) return; continue; }
            if (n < 32) continue;
            Interlocked.Increment(ref _timingRequests);
            ulong now = NtpClock.Now;
            resp[0] = buf[0];
            resp[1] = 0x53 | 0x80;
            resp[2] = 0; resp[3] = 7;
            resp.AsSpan(4, 4).Clear();
            buf.AsSpan(24, 8).CopyTo(resp.AsSpan(8));          // 参考时间 = 请求的发送时间
            BinaryPrimitives.WriteUInt64BigEndian(resp.AsSpan(16), now); // 接收时间
            BinaryPrimitives.WriteUInt64BigEndian(resp.AsSpan(24), now); // 发送时间
            try { _timing.SendTo(resp, from); } catch { }
        }
    }

    void ControlLoop()
    {
        var buf = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            int n;
            try { n = _control!.ReceiveFrom(buf, ref from); } catch { if (!_running) return; continue; }
            // 0x55：设备请求重传丢失的包
            if (n < 8 || (buf[1] & 0x7F) != 0x55) continue;
            ushort lost = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4));
            int count = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
            Debug.Log($"retransmit request seq={lost} count={count}");
            for (int i = 0; i < Math.Min(count, 256); i++)
            {
                ushort s = (ushort)(lost + i);
                var pkt = _backlog[s % BacklogSize];
                if (pkt == null || _backlogSeq[s % BacklogSize] != s) continue;
                var resend = new byte[4 + pkt.Length];
                resend[0] = 0x80; resend[1] = 0xD6;
                pkt.AsSpan(2, 2).CopyTo(resend.AsSpan(2));
                pkt.CopyTo(resend, 4);
                try { _control.SendTo(resend, from); } catch { }
            }
        }
    }

    void FeedbackLoop()
    {
        while (_running)
        {
            for (int i = 0; i < 20 && _running; i++) Thread.Sleep(100);
            if (!_running) return;
            try { _rtsp!.Rtsp("POST", "/feedback"); }
            catch (Exception ex) { Fail("与 HomePod 的连接断开了：" + ex.Message); return; }
        }
    }

    // ---------------- 音频发送 ----------------

    void AudioLoop(ushort seq)
    {
        TimeBeginPeriod(1);
        try { AudioLoopCore(seq); }
        catch (Exception ex) { Fail("音频发送失败：" + ex.Message); }
        finally { TimeEndPeriod(1); }
    }

    void AudioLoopCore(ushort seq)
    {
        uint ssrc = _rtsp!.SessionId;
        int latency = _latencyFrames;
        long startTs = NtpClock.ToFrames(NtpClock.Now, SampleRate);

        // 发送端缓冲：攒够少量数据再开始，用来吸收采集抖动；过多则丢弃以免延迟累积
        const int prebuffer = 882;      // 20ms
        const int maxBuffered = 4410;   // 100ms

        var pcm = new short[FramesPerPacket * 2];
        var payload = MemoryMarshal.AsBytes(pcm.AsSpan());
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> header = stackalloc byte[12];
        ulong counter = 0;
        bool buffering = true, first = true;

        var sw = Stopwatch.StartNew();
        long k = 0; // 已发送的包数，时间戳严格等于 startTs + k*352
        double nextSync = 0;
        const double packetSeconds = (double)FramesPerPacket / SampleRate;

        while (_running)
        {
            double now = sw.Elapsed.TotalSeconds;
            double due = k * packetSeconds;

            // 长时间卡顿（如睡眠唤醒）后直接跳到当前时刻，不去补发旧包
            if (now - due > 0.25)
            {
                k = (long)(now / packetSeconds);
                due = k * packetSeconds;
                buffering = true;
            }

            long headTs = startTs + k * FramesPerPacket;
            uint rtpTime = (uint)(headTs - startTs + latency);

            if (now >= nextSync)
            {
                SendSync(first, headTs, rtpTime, latency);
                nextSync = now + 1.0;
            }

            if (now < due)
            {
                double wait = due - now;
                if (wait > 0.0015) Thread.Sleep(1); else Thread.SpinWait(200);
                continue;
            }

            int available = _ring.Available;
            if (available > maxBuffered) { _ring.Trim(prebuffer); available = prebuffer; }
            if (buffering && available >= prebuffer) buffering = false;
            if (buffering || !_ring.Read(pcm))
            {
                Array.Clear(pcm);
                buffering = true;
            }
            // AirPlay 的 PCM 是大端（网络字节序），Windows 采集到的是小端
            BinaryPrimitives.ReverseEndianness(pcm, pcm);

            // RTP 头：时间戳和 SSRC 同时作为加密的附加认证数据
            header[0] = 0x80;
            header[1] = first ? (byte)0xE0 : (byte)0x60;
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], seq);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], rtpTime);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], ssrc);

            var packet = new byte[12 + payload.Length + 16 + 8];
            header.CopyTo(packet);
            Hap.Nonce(nonce, counter);
            _audioCipher!.Encrypt(nonce, payload, packet.AsSpan(12, payload.Length),
                packet.AsSpan(12 + payload.Length, 16), header[4..12]);
            nonce[4..].CopyTo(packet.AsSpan(12 + payload.Length + 16));
            counter++;

            try { _audio!.Send(packet); } catch (SocketException) { }
            _backlog[seq % BacklogSize] = packet;
            _backlogSeq[seq % BacklogSize] = seq;

            seq++;
            k++;
            first = false;
        }
    }

    void SendSync(bool first, long headTs, uint rtpTime, int latency)
    {
        Span<byte> p = stackalloc byte[20];
        p[0] = first ? (byte)0x90 : (byte)0x80;
        p[1] = 0xD4;
        BinaryPrimitives.WriteUInt16BigEndian(p[2..], 7);
        BinaryPrimitives.WriteUInt32BigEndian(p[4..], (uint)(rtpTime - latency));
        BinaryPrimitives.WriteUInt64BigEndian(p[8..], NtpClock.FromFrames(headTs, SampleRate));
        BinaryPrimitives.WriteUInt32BigEndian(p[16..], rtpTime);
        try { _control!.SendTo(p, _remoteControl!); } catch { }
    }

    // ---------------- 工具 ----------------

    static Socket UdpBind(IPAddress local)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(local, 0));
        // 关闭“端口不可达导致 ReceiveFrom 抛异常”的 Windows 行为
        const int SIO_UDP_CONNRESET = -1744830452;
        try { s.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
        return s;
    }

    void StartThread(Action body, string name)
    {
        var t = new Thread(() => body()) { IsBackground = true, Name = name };
        _threads.Add(t);
        t.Start();
    }

    static void Expect(RtspResponse r, string what)
    {
        if (r.Code != 200) throw new IOException($"{what} 失败（{r.Code}）");
    }

    void Fail(string reason)
    {
        if (!_running || Interlocked.Exchange(ref _failed, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ => Failed?.Invoke(reason));
    }

    public void Dispose()
    {
        bool wasRunning = _running;
        _running = false;
        if (wasRunning && _rtsp != null)
        {
            try { _rtsp.Rtsp("TEARDOWN", headers: [("Session", "0")]); } catch { }
        }
        try { _eventTcp?.Dispose(); } catch { }
        _timing?.Dispose();
        _control?.Dispose();
        _audio?.Dispose();
        _rtsp?.Dispose();
        foreach (var t in _threads) if (t != Thread.CurrentThread) t.Join(500);
        _audioCipher?.Dispose();
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static partial uint TimeBeginPeriod(uint ms);
    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static partial uint TimeEndPeriod(uint ms);
}

sealed class FirewallBlockedException() : IOException("Windows 防火墙拦截了 HomePod 的连接");

/// <summary>单调递增的 NTP 时钟（启动时以系统时间为基准，之后用高精度计时器推进）。</summary>
static class NtpClock
{
    const ulong UnixToNtp = 0x83AA7E80;
    static readonly long BaseUnixTicks = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
    static readonly long BaseStamp = Stopwatch.GetTimestamp();

    public static ulong Now
    {
        get
        {
            long ticks = BaseUnixTicks + (long)((Stopwatch.GetTimestamp() - BaseStamp) * (10_000_000.0 / Stopwatch.Frequency));
            ulong sec = (ulong)(ticks / 10_000_000) + UnixToNtp;
            ulong frac = (ulong)(ticks % 10_000_000) * (1UL << 32) / 10_000_000UL;
            return (sec << 32) | frac;
        }
    }

    public static long ToFrames(ulong ntp, int rate) =>
        (long)(ntp >> 32) * rate + (long)(((ntp & 0xFFFFFFFF) * (ulong)rate) >> 32);

    public static ulong FromFrames(long frames, int rate) =>
        ((ulong)(frames / rate) << 32) | ((ulong)(frames % rate) << 32) / (ulong)rate;
}
