using System.Globalization;
using Microsoft.Win32;

namespace HomePodCast;

enum LatencyMode { Low, Normal, Stable }

/// <summary>设置保存在 %APPDATA%\HomePodCast\settings.ini，纯文本，可随时删除。</summary>
sealed class Settings
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomePodCast");
    static readonly string FilePath = Path.Combine(Dir, "settings.ini");

    public string LastDeviceId { get; set; } = "";
    public double Volume { get; set; } = 40;
    public LatencyMode Latency { get; set; } = LatencyMode.Normal;
    public bool AutoConnect { get; set; } = true;

    public static int LatencyMs(LatencyMode m) => m switch
    {
        LatencyMode.Low => 120,
        LatencyMode.Stable => 800,
        _ => 300,
    };

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var (k, v) = (line[..eq].Trim(), line[(eq + 1)..].Trim());
                switch (k)
                {
                    case "device": s.LastDeviceId = v; break;
                    case "volume": if (double.TryParse(v, CultureInfo.InvariantCulture, out var vol)) s.Volume = Math.Clamp(vol, 0, 100); break;
                    case "latency": if (Enum.TryParse<LatencyMode>(v, out var lm)) s.Latency = lm; break;
                    case "autoconnect": s.AutoConnect = v == "1"; break;
                }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                $"device={LastDeviceId}\nvolume={Volume.ToString("F0", CultureInfo.InvariantCulture)}\nlatency={Latency}\nautoconnect={(AutoConnect ? 1 : 0)}\n");
        }
        catch { }
    }
}

/// <summary>开机启动：写入当前用户的 Run 键，不需要管理员权限。</summary>
static class AutoStart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "HomePodCast";

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string v && v.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue(Name, Command);
            else k.DeleteValue(Name, false);
        }
    }
}
