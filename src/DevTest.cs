using System.Net;
using HomePodCast.Audio;
using HomePodCast.Protocol;

namespace HomePodCast;

/// <summary>
/// 开发用的命令行测试：HomePodCast --test &lt;ip&gt; [秒数] [延迟ms] [tone]
/// 不带 tone 时推送系统声音，带 tone 时推送 440Hz 测试音。
/// </summary>
static class DevTest
{
    public static int Run(string[] args)
    {
        var ip = IPAddress.Parse(args[1]);
        int seconds = args.Length > 2 ? int.Parse(args[2]) : 5;
        int latency = args.Length > 3 ? int.Parse(args[3]) : 250;
        bool tone = args.Contains("tone");

        var ring = new AudioRing(44100);
        IDisposable source = tone ? StartTone(ring) : new LoopbackCapture(ring);
        (source as LoopbackCapture)?.Start();

        using var session = new AirPlaySession(ip, 7000, ring, latency);
        session.Failed += r => Console.WriteLine("FAILED: " + r);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        session.Start(volumePercent: 25);
        Console.WriteLine($"streaming started in {sw.ElapsedMilliseconds} ms (latency {latency} ms, {(tone ? "tone" : "loopback")})");
        Thread.Sleep(seconds * 1000);
        Console.WriteLine("stopping");
        session.Dispose();
        source.Dispose();
        return 0;
    }

    public static int Render(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new AppController();
        var a = new AirPlayDevice("1", "办公室", IPAddress.Loopback, 7000, "AudioAccessory6,1");
        var b = new AirPlayDevice("2", "客厅", IPAddress.Loopback, 7000, "AudioAccessory5,1");
        app.PreviewState([a, b], a, StreamState.Playing, null, false);
        UI.MainWindow.RenderToPng(app, Path.Combine(dir, "panel-dark.png"), true, 2f);
        UI.MainWindow.RenderToPng(app, Path.Combine(dir, "panel-light.png"), false, 2f);
        if (Environment.GetEnvironmentVariable("HPC_ZOOM") is { } zoom)
            UI.MainWindow.RenderToPng(app, zoom, true, 6f);
        return 0;
    }

    sealed class ToneSource(Thread t, Action stop) : IDisposable
    {
        public void Dispose() { stop(); t.Join(); }
    }

    static IDisposable StartTone(AudioRing ring)
    {
        bool run = true;
        var t = new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long written = 0;
            var buf = new short[441 * 2];
            while (run)
            {
                long target = (long)(sw.Elapsed.TotalSeconds * 44100);
                while (written + 441 <= target)
                {
                    for (int i = 0; i < 441; i++)
                    {
                        // 每秒响 0.5 秒，便于听出断续和延迟
                        long n = written + i;
                        short v = (n % 44100) < 22050 ? (short)(3000 * Math.Sin(2 * Math.PI * 440 * n / 44100)) : (short)0;
                        buf[2 * i] = buf[2 * i + 1] = v;
                    }
                    ring.Write(buf);
                    written += 441;
                }
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        t.Start();
        return new ToneSource(t, () => run = false);
    }
}
