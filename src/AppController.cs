using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast;

enum StreamState { Idle, Connecting, Playing }

/// <summary>
/// 程序状态：设备列表、当前推流会话、音量与延迟。所有耗时操作都在后台线程完成，
/// 状态变化后通过 Changed 通知界面重绘。
/// </summary>
sealed class AppController
{
    readonly object _lock = new();
    readonly AudioRing _ring = new(44100);
    LoopbackCapture? _capture;
    AirPlaySession? _session;
    int _generation; // 每次连接/断开递增，用来丢弃过期的后台结果

    public Settings Settings { get; } = Settings.Load();
    public IReadOnlyList<AirPlayDevice> Devices { get; private set; } = [];
    public bool Scanning { get; private set; }
    public bool ScannedOnce { get; private set; }
    public StreamState State { get; private set; }
    public AirPlayDevice? Active { get; private set; }
    public string? Error { get; private set; }
    public bool NeedsFirewall { get; private set; }

    public event Action? Changed;
    void Notify() => Changed?.Invoke();

    // 音量调节合并发送：拖动滑块时只发最新值
    readonly AutoResetEvent _volumeSignal = new(false);

    public AppController()
    {
        new Thread(VolumeWorker) { IsBackground = true, Name = "volume" }.Start();
    }

    public void Scan(Action? done = null)
    {
        lock (_lock) { if (Scanning) return; Scanning = true; }
        Notify();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<AirPlayDevice> found;
            try { found = Mdns.Browse(1800); } catch { found = []; }
            lock (_lock)
            {
                // 正在播放的设备即便这次没扫到也保留在列表里
                if (Active != null && !found.Any(d => d.Id == Active.Id)) found.Insert(0, Active);
                Devices = found;
                Scanning = false;
                ScannedOnce = true;
            }
            Notify();
            done?.Invoke();
        });
    }

    public void Toggle(AirPlayDevice d)
    {
        if (Active?.Id == d.Id && State != StreamState.Idle) Disconnect();
        else Connect(d);
    }

    public void Connect(AirPlayDevice d, int attempt = 0)
    {
        int gen;
        lock (_lock)
        {
            StopSessionLocked();
            gen = ++_generation;
            Active = d;
            State = StreamState.Connecting;
            Error = null;
            NeedsFirewall = false;
            Settings.LastDeviceId = d.Id;
        }
        Settings.Save();
        Notify();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            AirPlaySession? session = null;
            try
            {
                _ring.Clear();
                lock (_lock)
                {
                    if (gen != _generation) return;
                    _capture ??= StartCapture();
                }
                session = new AirPlaySession(d.Address, d.Port, _ring, Settings.LatencyMs(Settings.Latency));
                session.Failed += reason => OnSessionFailed(gen, d, reason);
                session.Start(Settings.Volume);
                lock (_lock)
                {
                    if (gen != _generation) { session.Dispose(); return; }
                    _session = session;
                    State = StreamState.Playing;
                }
            }
            catch (Exception ex)
            {
                session?.Dispose();
                lock (_lock)
                {
                    if (gen != _generation) return;
                    StopSessionLocked();
                    State = StreamState.Idle;
                    NeedsFirewall = ex is FirewallBlockedException;
                    Error = NeedsFirewall ? "Windows 防火墙拦住了 HomePod 的回连" : "连接失败：" + Friendly(ex);
                }
            }
            Notify();
        });
    }

    void OnSessionFailed(int gen, AirPlayDevice d, string reason)
    {
        bool takenOver = reason.Contains("接管");
        lock (_lock)
        {
            if (gen != _generation) return;
            StopSessionLocked();
            State = StreamState.Idle;
            Error = reason;
        }
        Notify();
        // 网络抖动导致的断开自动重连一次；被别的设备接管则尊重对方
        if (!takenOver)
        {
            Thread.Sleep(1500);
            lock (_lock) { if (gen != _generation) return; }
            Connect(d);
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _generation++;
            StopSessionLocked();
            State = StreamState.Idle;
            Error = null;
        }
        Notify();
    }

    void StopSessionLocked()
    {
        var s = _session;
        _session = null;
        var c = _capture;
        _capture = null;
        if (s != null || c != null)
            ThreadPool.QueueUserWorkItem(_ => { s?.Dispose(); c?.Dispose(); });
    }

    LoopbackCapture StartCapture()
    {
        var c = new LoopbackCapture(_ring);
        c.Start();
        return c;
    }

    public void SetVolume(double v)
    {
        Settings.Volume = Math.Clamp(Math.Round(v), 0, 100);
        _volumeSignal.Set();
        Notify();
    }

    void VolumeWorker()
    {
        double sent = -1;
        while (true)
        {
            _volumeSignal.WaitOne();
            Thread.Sleep(60);
            var v = Settings.Volume;
            if (v == sent) continue;
            AirPlaySession? s;
            lock (_lock) s = _session;
            s?.SetVolume(v);
            sent = v;
            Settings.Save();
        }
    }

    public void SetLatency(LatencyMode m)
    {
        if (Settings.Latency == m) return;
        Settings.Latency = m;
        Settings.Save();
        // 延迟只能在建立会话时协商，正在播放就重新连一次
        if (State != StreamState.Idle && Active != null) Connect(Active);
        else Notify();
    }

    public void FirewallFixed()
    {
        NeedsFirewall = false;
        Error = null;
        if (Active != null) Connect(Active); else Notify();
    }

    public void Shutdown()
    {
        lock (_lock) { _generation++; }
        var s = _session; var c = _capture;
        _session = null; _capture = null;
        s?.Dispose();
        c?.Dispose();
    }

    /// <summary>开发用：直接设定界面要展示的状态。</summary>
    public void PreviewState(IReadOnlyList<AirPlayDevice> devices, AirPlayDevice? active, StreamState state, string? error, bool firewall)
    {
        Devices = devices; Active = active; State = state; Error = error; NeedsFirewall = firewall; ScannedOnce = true;
    }

    static string Friendly(Exception ex) => ex switch
    {
        TimeoutException => "设备没有响应",
        System.Net.Sockets.SocketException => "网络连接失败",
        IOException io when io.InnerException is System.Net.Sockets.SocketException => "设备没有响应",
        _ => ex.Message,
    };
}
