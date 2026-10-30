using HomePodCast.UI;

namespace HomePodCast;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--test") return DevTest.Run(args);
        if (args.Length >= 1 && args[0] == "--scan")
        {
            foreach (var d in Protocol.Mdns.Browse()) Console.WriteLine($"{d.Name} | {d.ModelName} | {d.Address}:{d.Port} | {d.Id}");
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--render") return DevTest.Render(args[1]);

        // 只允许运行一个实例；再次打开时把已运行实例的面板弹出来
        Native.CreateMutex(0, 1, "HomePodCast-SingleInstance");
        if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 183 /* ERROR_ALREADY_EXISTS */)
        {
            var existing = MainWindow.FindExisting();
            if (existing != 0) MainWindow.ShowExisting(existing);
            return 0;
        }

        bool fromAutoStart = args.Contains("--tray");
        var app = new AppController();

        // 自动连回上次使用的 HomePod
        if (app.Settings.AutoConnect && app.Settings.LastDeviceId != "")
        {
            void TryAutoConnect()
            {
                var last = app.Devices.FirstOrDefault(d => d.Id == app.Settings.LastDeviceId);
                if (last != null && app.State == StreamState.Idle) app.Connect(last);
            }
            app.Changed += OnFirstScan;
            void OnFirstScan()
            {
                if (!app.ScannedOnce) return;
                app.Changed -= OnFirstScan;
                TryAutoConnect();
            }
        }

        MainWindow.Run(app, startHidden: fromAutoStart);
        return 0;
    }
}
