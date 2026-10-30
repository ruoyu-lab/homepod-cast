using System.Runtime.InteropServices;
using HomePodCast.Protocol;
using static HomePodCast.UI.Native;

namespace HomePodCast.UI;

/// <summary>托盘图标 + 点击后弹出的面板（全部自绘，风格贴近 Windows 11 快速设置）。</summary>
static unsafe partial class MainWindow
{
    const string ClassName = "HomePodCastFlyout";
    const uint WM_TRAY = WM_APP + 1, WM_STATE = WM_APP + 2, WM_SHOWFLYOUT = WM_APP + 3;
    const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
    const int TimerAnim = 1, TimerRescan = 2;
    const float W = 340, P = 14;

    static nint _hwnd, _icon;
    static uint _taskbarCreated;
    static AppController _app = null!;
    static Theme _theme = Theme.Load();
    static float _scale = 1;
    static long _hiddenAt;
    static bool _tracking, _dragging;
    static string? _hover, _pressed;
    static readonly List<(string Id, float X, float Y, float W, float H)> _hits = [];
    static float _sliderX, _sliderW;
    static int _height;

    public static nint FindExisting() => FindWindow(ClassName, null);
    public static void ShowExisting(nint hwnd) => PostMessage(hwnd, WM_SHOWFLYOUT, 0, 0);

    public static void Run(AppController app, bool startHidden)
    {
        _app = app;
        var inst = GetModuleHandle(0);
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = inst,
                hCursor = LoadCursor(0, 32512),
                lpszClassName = cls,
            };
            RegisterClassEx(&wc);
        }
        _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, ClassName, "HomePodCast", WS_POPUP, 0, 0, 10, 10, 0, 0, inst, 0);
        ApplyDwm();

        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = LoadImage(inst, 32512, 1 /* IMAGE_ICON */, 0, 0, 0x40 /* LR_DEFAULTSIZE */);
        AddTray();

        _app.Changed += () => PostMessage(_hwnd, WM_STATE, 0, 0);
        _app.Scan();
        SetTimer(_hwnd, TimerRescan, 60_000, 0);
        if (!startHidden) ShowFlyout();

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessage(msg);
        }
        RemoveTray();
    }

    static void ApplyDwm()
    {
        int round = 2;           // DWMWCP_ROUND
        int dark = _theme.Dark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, 33, &round, 4);
        DwmSetWindowAttribute(_hwnd, 20, &dark, 4);
    }

    // ---------------- 托盘 ----------------

    static NOTIFYICONDATAW TrayData()
    {
        var d = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = 0x1 | 0x2 | 0x4 | 0x80, // MESSAGE | ICON | TIP | SHOWTIP
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
            uVersion = 4,
        };
        var tip = TrayTip();
        for (int i = 0; i < Math.Min(tip.Length, 127); i++) d.szTip[i] = tip[i];
        return d;
    }

    static string TrayTip() => _app.State switch
    {
        StreamState.Playing => $"HomePodCast · 正在播放到 {_app.Active?.Name}",
        StreamState.Connecting => $"HomePodCast · 正在连接 {_app.Active?.Name}",
        _ => "HomePodCast · 未连接",
    };

    static void AddTray()
    {
        var d = TrayData();
        Shell_NotifyIcon(0 /* NIM_ADD */, ref d);
        Shell_NotifyIcon(4 /* NIM_SETVERSION */, ref d);
    }

    static void UpdateTray() { var d = TrayData(); Shell_NotifyIcon(1 /* NIM_MODIFY */, ref d); }
    static void RemoveTray() { var d = TrayData(); Shell_NotifyIcon(2 /* NIM_DELETE */, ref d); }

    static void ShowTrayMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, 1, "打开面板");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, 2, "退出 HomePodCast");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(menu, 0x100 /* TPM_RETURNCMD */ | 0x20 /* TPM_BOTTOMALIGN */, pt.X, pt.Y, 0, _hwnd, 0);
        DestroyMenu(menu);
        if (cmd == 1) ShowFlyout();
        else if (cmd == 2) Quit();
    }

    // ---------------- 面板显示 ----------------

    static void ToggleFlyout()
    {
        if (IsWindowVisible(_hwnd) != 0) { Hide(); return; }
        // 点击托盘图标本身会让面板失去焦点而隐藏，紧接着的这次点击不应再打开
        if (Environment.TickCount64 - _hiddenAt < 300) return;
        ShowFlyout();
    }

    static void ShowFlyout()
    {
        GetCursorPos(out var cursor);
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = 1 };
        var anchor = Shell_NotifyIconGetRect(ref id, out var iconRect) == 0
            ? new POINT { X = (iconRect.Left + iconRect.Right) / 2, Y = (iconRect.Top + iconRect.Bottom) / 2 }
            : cursor;

        var mon = MonitorFromPoint(anchor, 2 /* NEAREST */);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfo(mon, ref mi);
        GetDpiForMonitor(mon, 0, out uint dpi, out _);
        _scale = dpi / 96f;

        Layout(measureOnly: true);
        int w = (int)(W * _scale), h = _height;
        int margin = (int)(12 * _scale);
        var work = mi.rcWork;
        var full = mi.rcMonitor;
        int x, y;
        if (work.Left > full.Left) { x = work.Left + margin; y = Math.Clamp(anchor.Y - h / 2, work.Top + margin, work.Bottom - h - margin); }
        else if (work.Right < full.Right) { x = work.Right - w - margin; y = Math.Clamp(anchor.Y - h / 2, work.Top + margin, work.Bottom - h - margin); }
        else if (work.Top > full.Top) { x = work.Right - w - margin; y = work.Top + margin; }
        else { x = work.Right - w - margin; y = work.Bottom - h - margin; }

        _theme = Theme.Load();
        ApplyDwm();
        SetWindowPos(_hwnd, -1 /* HWND_TOPMOST */, x, y, w, h, 0x40 /* SHOWWINDOW */);
        SetForegroundWindow(_hwnd);
        InvalidateRect(_hwnd, 0, 0);
        if (!_app.Scanning && (DateTime.Now - _lastScan).TotalSeconds > 8) Rescan();
        UpdateAnimTimer();
    }

    static DateTime _lastScan = DateTime.Now;
    static void Rescan() { _lastScan = DateTime.Now; _app.Scan(); }

    static void Hide()
    {
        ShowWindow(_hwnd, SW_HIDE);
        _hiddenAt = Environment.TickCount64;
        _hover = _pressed = null;
        _dragging = false;
    }

    static void Resize()
    {
        int old = _height;
        Layout(measureOnly: true);
        if (old == _height || IsWindowVisible(_hwnd) == 0) return;
        GetWindowRectBottom(out int bottom, out int left);
        SetWindowPos(_hwnd, 0, left, bottom - _height, (int)(W * _scale), _height, 0x14 /* NOZORDER|NOACTIVATE */);
    }

    static void GetWindowRectBottom(out int bottom, out int left)
    {
        GetWindowRect(_hwnd, out var r);
        bottom = r.Bottom; left = r.Left;
    }

    static void UpdateAnimTimer()
    {
        if (IsWindowVisible(_hwnd) != 0 && (_app.Scanning || _app.State == StreamState.Connecting))
            SetTimer(_hwnd, TimerAnim, 16, 0);
        else KillTimer(_hwnd, TimerAnim);
    }

    static void Quit()
    {
        Hide();
        _app.Shutdown();
        PostQuitMessage(0);
    }

    // ---------------- 布局与绘制 ----------------

    /// <summary>同一套代码既用于计算高度，也用于实际绘制和记录可点击区域。</summary>
    static void Layout(bool measureOnly, Canvas? c = null)
    {
        var t = _theme;
        _hits.Clear();
        float y = 0;

        // 标题
        string status = _app.State switch
        {
            StreamState.Playing => $"正在播放到 {_app.Active?.Name}",
            StreamState.Connecting => $"正在连接 {_app.Active?.Name}…",
            _ => "选择一台 HomePod 开始播放电脑的声音",
        };
        if (c != null)
        {
            c.Text("HomePodCast", P + 4, 14, 200, 26, 19, t.Text, bold: true);
            c.Text(status, P + 4, 40, W - 2 * P - 50, 18, 12.5f, _app.State == StreamState.Playing ? t.Accent : t.Sub);
            DrawRefresh(c, W - P - 32, 18);
        }
        _hits.Add(("refresh", W - P - 34, 16, 36, 36));
        y = 70;

        // 设备列表
        var devices = _app.Devices;
        if (devices.Count == 0)
        {
            if (c != null)
            {
                c.FillRound(P, y, W - 2 * P, 64, 8, t.Card);
                bool searching = _app.Scanning || !_app.ScannedOnce;
                c.Text(searching ? "正在搜索 HomePod…" : "没有找到 HomePod", P, y + 12, W - 2 * P, 20, 13.5f, t.Text, align: Canvas.Center);
                c.Text(searching ? "请稍候" : "请确认电脑和 HomePod 在同一个网络", P, y + 33, W - 2 * P, 18, 12, t.Sub, align: Canvas.Center);
            }
            y += 64 + 8;
        }
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            string id = "dev:" + i;
            if (c != null) DrawDevice(c, d, P, y, id);
            _hits.Add((id, P, y, W - 2 * P, 60));
            y += 60 + 4;
        }
        y += 4;

        // 错误提示
        if (_app.Error is { } err)
        {
            float h = _app.NeedsFirewall ? 76 : 40;
            if (c != null)
            {
                c.FillRound(P, y, W - 2 * P, h, 8, t.ErrorBg);
                c.Text(err, P + 14, y + 10, W - 2 * P - 28, 20, 12.5f, t.ErrorText);
                if (_app.NeedsFirewall)
                {
                    bool hv = _hover == "firewall";
                    c.FillRound(P + 14, y + 38, 150, 28, 14, hv ? Theme.Blend(t.Accent, t.ErrorBg, 0.85f) : t.Accent);
                    c.Text("允许并重新连接", P + 14, y + 38, 150, 28, 12.5f, t.OnAccent, bold: true, align: Canvas.Center);
                }
            }
            if (_app.NeedsFirewall) _hits.Add(("firewall", P + 14, y + 38, 150, 28));
            y += h + 8;
        }

        // 音量
        if (c != null)
        {
            c.FillRound(P, y, W - 2 * P, 52, 8, t.Card);
            DrawSpeaker(c, P + 22, y + 26, _app.Settings.Volume);
        }
        _sliderX = P + 48; _sliderW = W - 2 * P - 48 - 50;
        if (c != null)
        {
            float frac = (float)(_app.Settings.Volume / 100);
            float cy = y + 26;
            c.Line(_sliderX, cy, _sliderX + _sliderW, cy, t.Track, 4);
            if (frac > 0) c.Line(_sliderX, cy, _sliderX + _sliderW * frac, cy, t.Accent, 4);
            float kx = _sliderX + _sliderW * frac;
            bool hv = _hover == "slider" || _dragging;
            c.FillCircle(kx, cy, 10, t.Dark ? 0xFF454545 : 0xFFFFFFFF);
            if (!t.Dark) c.Arc(kx, cy, 10, 0, 360, 0x22000000, 1);
            c.FillCircle(kx, cy, hv ? 7 : 5, t.Accent);
            c.Text(((int)_app.Settings.Volume).ToString(), W - P - 46, y, 34, 52, 13, t.Text, align: Canvas.Right);
        }
        _hits.Add(("slider", _sliderX - 10, y, _sliderW + 20, 52));
        y += 52 + 8;

        // 延迟
        if (c != null)
        {
            c.FillRound(P, y, W - 2 * P, 52, 8, t.Card);
            c.Text("延迟", P + 16, y, 60, 52, 13.5f, t.Text);
        }
        string[] labels = ["低", "标准", "稳定"];
        float segW = 60, segH = 30, segX = W - P - 12 - segW * 3, segY = y + 11;
        if (c != null) c.FillRound(segX - 2, segY - 2, segW * 3 + 4, segH + 4, 9, t.Dark ? 0xFF1F1F1F : 0xFFEDEDED);
        for (int i = 0; i < 3; i++)
        {
            string id = "lat:" + i;
            bool sel = (int)_app.Settings.Latency == i;
            if (c != null)
            {
                if (sel) c.FillRound(segX + i * segW, segY, segW, segH, 7, t.Accent);
                else if (_hover == id) c.FillRound(segX + i * segW, segY, segW, segH, 7, t.Dark ? 0xFF2E2E2E : 0xFFF7F7F7);
                c.Text(labels[i], segX + i * segW, segY, segW, segH, 12.5f, sel ? t.OnAccent : t.Sub, bold: sel, align: Canvas.Center);
            }
            _hits.Add((id, segX + i * segW, segY, segW, segH));
        }
        y += 52;
        if (c != null)
            c.Text(LatencyHint(), P + 4, y + 4, W - 2 * P, 18, 11.5f, t.Sub);
        y += 26;

        // 底栏
        if (c != null) c.FillRect(0, y, W, 1, t.Divider);
        y += 1;
        float fy = y + 8;
        bool auto = AutoStart.Enabled;
        if (c != null)
        {
            if (_hover == "autostart") c.FillRound(P - 4, fy, 140, 32, 6, t.Dark ? 0xFF2A2A2A : 0xFFEAEAEA);
            DrawCheck(c, P + 8, fy + 16, auto);
            c.Text("开机自动启动", P + 22, fy, 110, 32, 12.5f, t.Text);
            if (_hover == "quit") c.FillRound(W - P - 64, fy, 68, 32, 6, t.Dark ? 0xFF2A2A2A : 0xFFEAEAEA);
            c.Text("退出", W - P - 64, fy, 68, 32, 12.5f, t.Text, align: Canvas.Center);
        }
        _hits.Add(("autostart", P - 4, fy, 140, 32));
        _hits.Add(("quit", W - P - 64, fy, 68, 32));
        y = fy + 32 + 8;

        _height = (int)Math.Ceiling(y * _scale);
    }

    static string LatencyHint() => _app.Settings.Latency switch
    {
        LatencyMode.Low => "约 0.15 秒，适合看视频；网络差时可能断续",
        LatencyMode.Stable => "约 0.8 秒，最不容易断续，适合听音乐",
        _ => "约 0.3 秒，兼顾流畅和延迟",
    };

    static void DrawDevice(Canvas c, AirPlayDevice d, float x, float y, string id)
    {
        var t = _theme;
        bool active = _app.Active?.Id == d.Id && _app.State != StreamState.Idle;
        bool playing = active && _app.State == StreamState.Playing;
        bool connecting = active && _app.State == StreamState.Connecting;
        float w = W - 2 * P;

        c.FillRound(x, y, w, 60, 8, _hover == id ? t.CardHover : t.Card);
        if (!t.Dark) c.StrokeRound(x, y, w, 60, 8, t.CardStroke);

        // 图标：圆底 + HomePod 剪影
        float cx = x + 30, cy = y + 30;
        uint bg = playing ? t.Accent : t.Neutral;
        c.FillCircle(cx, cy, 18, bg);
        DrawHomePod(c, cx, cy, d.Model.StartsWith("AudioAccessory5,"), playing ? t.OnAccent : t.Sub, bg);

        c.Text(d.Name, x + 60, y + 10, w - 130, 22, 14, t.Text, bold: true);
        string sub = playing ? "正在播放" : connecting ? "正在连接…" : d.ModelName;
        c.Text(sub, x + 60, y + 31, w - 130, 18, 12, playing ? t.Accent : t.Sub);

        // 开关
        float sw = 40, sh = 20, sx = x + w - 14 - sw, sy = y + 20;
        if (active)
        {
            c.FillRound(sx, sy, sw, sh, 10, connecting ? Theme.Blend(t.Accent, t.Card, 0.5f) : t.Accent);
            c.FillCircle(sx + sw - 10, sy + 10, 6, t.OnAccent);
        }
        else
        {
            c.StrokeRound(sx, sy, sw, sh, 10, t.Sub, 1);
            c.FillCircle(sx + 10, sy + 10, 5, t.Sub);
        }
    }

    static void DrawRefresh(Canvas c, float x, float y)
    {
        var t = _theme;
        float cx = x + 16, cy = y + 16;
        if (_hover == "refresh") c.FillRound(x, y, 32, 32, 6, t.Dark ? 0xFF2D2D2D : 0xFFE6E6E6);
        if (_app.Scanning)
        {
            // Windows 11 风格的转圈：弧长随时间伸缩，同时整体旋转
            double ms = Environment.TickCount64 % 2000 / 2000.0;
            float sweep = 40 + 200 * (float)Math.Abs(Math.Sin(ms * Math.PI));
            float start = (float)(ms * 720) - 90;
            c.Arc(cx, cy, 7.5f, start, sweep, t.Accent, 2f);
        }
        else c.Icon('', cx, cy, 14, t.Text); // Refresh
    }

    /// <summary>HomePod 剪影：大号是带圆角的圆柱，mini 是球形；顶部都有一块平的触控面。</summary>
    static void DrawHomePod(Canvas c, float cx, float cy, bool mini, uint body, uint bg)
    {
        uint top = Theme.Blend(bg, body, 0.4f);
        if (mini)
        {
            // 球体，顶部一块椭圆触控面
            c.FillCircle(cx, cy + 0.5f, 9.5f, body);
            c.FillRound(cx - 5.2f, cy - 7.4f, 10.4f, 2.6f, 1.3f, top);
        }
        else
        {
            // 圆角圆柱，顶部椭圆触控面
            c.FillRound(cx - 8, cy - 9.5f, 16, 19.5f, 5, body);
            c.FillRound(cx - 6.4f, cy - 9.1f, 12.8f, 2.8f, 1.4f, top);
        }
    }

    static void DrawSpeaker(Canvas c, float cx, float cy, double volume)
    {
        // Mute / Volume0..3
        char g = volume <= 0 ? '' : volume < 34 ? '' : volume < 67 ? '' : '';
        c.Icon(g, cx, cy, 20, _theme.Text);
    }

    static void DrawCheck(Canvas c, float cx, float cy, bool on)
    {
        var t = _theme;
        if (on)
        {
            c.FillRound(cx - 9, cy - 9, 18, 18, 4, t.Accent);
            c.Icon('', cx, cy, 11, t.OnAccent); // CheckMark
        }
        else c.StrokeRound(cx - 9, cy - 9, 18, 18, 4, t.Sub, 1);
    }

    static void Paint()
    {
        var hdc = BeginPaint(_hwnd, out var ps);
        GetClientRect(_hwnd, out var rc);
        using (var c = new Canvas(hdc, rc.Width, rc.Height, _scale))
        {
            c.Clear(_theme.Bg);
            Layout(measureOnly: false, c);
        }
        EndPaint(_hwnd, ps);
    }

    // ---------------- 交互 ----------------

    static string? HitTest(nint lParam)
    {
        float x = LoWord(lParam) / _scale, y = HiWord(lParam) / _scale;
        foreach (var h in _hits)
            if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) return h.Id;
        return null;
    }

    static void SetVolumeFromX(nint lParam)
    {
        float x = LoWord(lParam) / _scale;
        _app.SetVolume(Math.Clamp((x - _sliderX) / _sliderW, 0, 1) * 100);
    }

    static void Click(string id)
    {
        if (id == "refresh") Rescan();
        else if (id == "quit") Quit();
        else if (id == "autostart") { try { AutoStart.Enabled = !AutoStart.Enabled; } catch { } }
        else if (id == "firewall")
        {
            Hide();
            if (Firewall.RequestAllow(_hwnd)) _app.FirewallFixed();
            ShowFlyout();
        }
        else if (id.StartsWith("lat:")) _app.SetLatency((LatencyMode)int.Parse(id[4..]));
        else if (id.StartsWith("dev:") && int.Parse(id[4..]) is var i && i < _app.Devices.Count) _app.Toggle(_app.Devices[i]);
        InvalidateRect(_hwnd, 0, 0);
    }

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try { return Handle(hwnd, msg, wParam, lParam); }
        catch { return DefWindowProc(hwnd, msg, wParam, lParam); }
    }

    static nint Handle(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0) { AddTray(); return 0; }
        switch (msg)
        {
            case WM_TRAY:
                switch ((uint)LoWord(lParam))
                {
                    case NIN_SELECT: case NIN_KEYSELECT: ToggleFlyout(); break;
                    case WM_CONTEXTMENU: ShowTrayMenu(); break;
                }
                return 0;
            case WM_SHOWFLYOUT: ShowFlyout(); return 0;
            case WM_STATE:
                UpdateTray();
                Resize();
                UpdateAnimTimer();
                InvalidateRect(hwnd, 0, 0);
                return 0;
            case WM_TIMER:
                if (wParam == TimerAnim) InvalidateRect(hwnd, 0, 0);
                else if (wParam == TimerRescan && !_app.Scanning) _app.Scan();
                return 0;
            case WM_PAINT: Paint(); return 0;
            case WM_ERASEBKGND: return 1;
            case WM_ACTIVATE:
                if (LoWord(wParam) == 0 && IsWindowVisible(hwnd) != 0) Hide();
                return 0;
            case WM_KEYDOWN:
                if (wParam == 0x1B) Hide();
                return 0;
            case WM_MOUSEMOVE:
                {
                    if (!_tracking)
                    {
                        var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = 0x2, hwndTrack = hwnd };
                        TrackMouseEvent(ref tme);
                        _tracking = true;
                    }
                    if (_dragging) { SetVolumeFromX(lParam); return 0; }
                    var h = HitTest(lParam);
                    if (h != _hover) { _hover = h; InvalidateRect(hwnd, 0, 0); }
                    return 0;
                }
            case WM_MOUSELEAVE:
                _tracking = false;
                if (_hover != null && !_dragging) { _hover = null; InvalidateRect(hwnd, 0, 0); }
                return 0;
            case WM_SETCURSOR:
                SetCursor(LoadCursor(0, _hover != null ? 32649 /* IDC_HAND */ : 32512));
                return 1;
            case WM_LBUTTONDOWN:
                _pressed = HitTest(lParam);
                if (_pressed == "slider") { _dragging = true; SetCapture(hwnd); SetVolumeFromX(lParam); }
                return 0;
            case WM_LBUTTONUP:
                if (_dragging) { _dragging = false; ReleaseCapture(); InvalidateRect(hwnd, 0, 0); return 0; }
                var up = HitTest(lParam);
                if (up != null && up == _pressed) Click(up);
                _pressed = null;
                return 0;
            case WM_MOUSEWHEEL:
                _app.SetVolume(_app.Settings.Volume + Math.Sign(HiWord(wParam)) * 2);
                return 0;
            case WM_SETTINGCHANGE:
            case WM_THEMECHANGED:
                _theme = Theme.Load();
                ApplyDwm();
                InvalidateRect(hwnd, 0, 0);
                return 0;
            case WM_DPICHANGED:
                _scale = HiWord(wParam) / 96f;
                Resize();
                InvalidateRect(hwnd, 0, 0);
                return 0;
            case WM_CLOSE:
                Hide();
                return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>开发用：把面板离屏渲染成 PNG，便于检查界面。</summary>
    public static void RenderToPng(AppController app, string path, bool dark, float scale, string? hover = null)
    {
        _app = app;
        _theme = Theme.Load(dark);
        _scale = scale;
        _hover = hover;
        Layout(measureOnly: true);
        int w = (int)(W * scale), h = _height;
        var screen = CreateCompatibleDC(0);
        var bmi = stackalloc byte[40];
        new Span<byte>(bmi, 40).Clear();
        *(int*)bmi = 40; *(int*)(bmi + 4) = w; *(int*)(bmi + 8) = -h; *(short*)(bmi + 12) = 1; *(short*)(bmi + 14) = 32;
        void* bits;
        var dib = CreateDIBSection(screen, bmi, 0, &bits, 0, 0);
        var old = SelectObject(screen, dib);
        using (var c = new Canvas(screen, w, h, scale))
        {
            c.Clear(_theme.Bg);
            Layout(measureOnly: false, c);
        }
        var px = new ReadOnlySpan<byte>(bits, w * h * 4);
        WritePng(path, w, h, px, 8 * scale);
        SelectObject(screen, old);
        DeleteObject(dib);
        DeleteDC(screen);
    }

    static void WritePng(string path, int w, int h, ReadOnlySpan<byte> bgra, float radius)
    {
        // 输出 RGBA，四角按窗口圆角做抗锯齿透明
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float dx = Math.Max(Math.Max(radius - (x + 0.5f), x + 0.5f - (w - radius)), 0);
                float dy = Math.Max(Math.Max(radius - (y + 0.5f), y + 0.5f - (h - radius)), 0);
                float a = Math.Clamp(radius + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                raw.WriteByte(bgra[i + 2]); raw.WriteByte(bgra[i + 1]); raw.WriteByte(bgra[i]);
                raw.WriteByte((byte)(a * 255));
            }
        }
        var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) raw.WriteTo(zs);
        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            f.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            f.Write(td);
            uint crc = 0xFFFFFFFF;
            foreach (var b in td)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
            f.Write(len);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
    }

    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint hdc, void* bmi, uint usage, void** bits, nint section, uint offset);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint mon, int type, out uint x, out uint y);
    [LibraryImport("user32.dll")] private static partial int GetWindowRect(nint h, out RECT r);
}
