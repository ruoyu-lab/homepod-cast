using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// 用 WASAPI 环回模式采集“默认播放设备”正在输出的声音，
/// 由系统转换为 44.1kHz/16bit/立体声后写入环形缓冲。
/// 直接调用 COM 虚表，不依赖任何第三方库。
/// </summary>
sealed unsafe partial class LoopbackCapture : IDisposable
{
    static Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    readonly AudioRing _ring;
    Thread? _thread;
    volatile bool _running;

    public LoopbackCapture(AudioRing ring) => _ring = ring;

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Run()
    {
        CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
        try
        {
            while (_running)
            {
                // 设备被拔出或默认设备切换后，重新打开新的默认设备
                try { CaptureUntilDeviceChanges(); }
                catch { }
                for (int i = 0; i < 10 && _running; i++) Thread.Sleep(50);
            }
        }
        finally { CoUninitialize(); }
    }

    void CaptureUntilDeviceChanges()
    {
        void* enumerator = null, device = null, client = null, capture = null;
        try
        {
            Check(CoCreateInstance(ref CLSID_MMDeviceEnumerator, 0, 1 /* CLSCTX_INPROC_SERVER */, ref IID_IMMDeviceEnumerator, out enumerator));
            // IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eConsole)
            Check(((delegate* unmanaged[Stdcall]<void*, int, int, void**, int>)VTable(enumerator, 4))(enumerator, 0, 0, &device));
            string deviceId = GetId(device);

            // IMMDevice::Activate
            var iidClient = IID_IAudioClient;
            Check(((delegate* unmanaged[Stdcall]<void*, Guid*, uint, void*, void**, int>)VTable(device, 3))(
                device, &iidClient, 0x17 /* CLSCTX_ALL */, null, &client));

            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = 44100, wBitsPerSample = 16,
                nBlockAlign = 4, nAvgBytesPerSec = 44100 * 4, cbSize = 0,
            };
            // IAudioClient::Initialize（共享模式，50ms 缓冲，由系统做格式转换）
            Check(((delegate* unmanaged[Stdcall]<void*, int, uint, long, long, WaveFormatEx*, Guid*, int>)VTable(client, 3))(
                client, 0, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                500_000, 0, &fmt, null));
            // IAudioClient::GetService(IAudioCaptureClient)
            var iidCapture = IID_IAudioCaptureClient;
            Check(((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)VTable(client, 14))(
                client, &iidCapture, &capture));
            // IAudioClient::Start
            Check(((delegate* unmanaged[Stdcall]<void*, int>)VTable(client, 10))(client));

            var getNextSize = (delegate* unmanaged[Stdcall]<void*, uint*, int>)VTable(capture, 5);
            var getBuffer = (delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, ulong*, ulong*, int>)VTable(capture, 3);
            var releaseBuffer = (delegate* unmanaged[Stdcall]<void*, uint, int>)VTable(capture, 4);

            long lastDeviceCheck = Environment.TickCount64;
            while (_running)
            {
                uint packet;
                Check(getNextSize(capture, &packet));
                while (packet > 0)
                {
                    byte* data; uint frames, flags; ulong pos, qpc;
                    Check(getBuffer(capture, &data, &frames, &flags, &pos, &qpc));
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) _ring.WriteSilence((int)frames);
                    else _ring.Write(new ReadOnlySpan<short>(data, (int)frames * 2));
                    Check(releaseBuffer(capture, frames));
                    Check(getNextSize(capture, &packet));
                }

                if (Environment.TickCount64 - lastDeviceCheck > 1500)
                {
                    lastDeviceCheck = Environment.TickCount64;
                    void* current = null;
                    if (((delegate* unmanaged[Stdcall]<void*, int, int, void**, int>)VTable(enumerator, 4))(enumerator, 0, 0, &current) >= 0)
                    {
                        string id = GetId(current);
                        Release(current);
                        if (id != deviceId) return;
                    }
                }
                Thread.Sleep(2);
            }
            ((delegate* unmanaged[Stdcall]<void*, int>)VTable(client, 11))(client); // Stop
        }
        finally
        {
            Release(capture); Release(client); Release(device); Release(enumerator);
        }
    }

    static string GetId(void* device)
    {
        char* id = null;
        // IMMDevice::GetId
        if (((delegate* unmanaged[Stdcall]<void*, char**, int>)VTable(device, 5))(device, &id) < 0 || id == null) return "";
        var s = new string(id);
        CoTaskMemFree(id);
        return s;
    }

    static void* VTable(void* obj, int index) => (*(void***)obj)[index];
    static void Release(void* obj) { if (obj != null) ((delegate* unmanaged[Stdcall]<void*, uint>)VTable(obj, 2))(obj); }
    static void Check(int hr) { if (hr < 0) throw new COMException("WASAPI", hr); }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint coInit);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial void CoTaskMemFree(void* p);
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint ctx, ref Guid iid, out void* obj);
}
