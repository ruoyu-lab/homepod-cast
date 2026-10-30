using System.Runtime.InteropServices;

namespace HomePodCast.UI;

[StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }

[StructLayout(LayoutKind.Sequential)]
struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public POINT pt; }

[StructLayout(LayoutKind.Sequential)]
unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; public long r0, r1, r2, r3; }

[StructLayout(LayoutKind.Sequential)]
struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

[StructLayout(LayoutKind.Sequential)]
struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public nint hwndTrack; public uint dwHoverTime; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public nint hWnd;
    public uint uID, uFlags, uCallbackMessage;
    public nint hIcon;
    public fixed char szTip[128];
    public uint dwState, dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
struct NOTIFYICONIDENTIFIER { public uint cbSize; public nint hWnd; public uint uID; public Guid guidItem; }

[StructLayout(LayoutKind.Sequential)]
unsafe struct SHELLEXECUTEINFOW
{
    public uint cbSize, fMask;
    public nint hwnd;
    public char* lpVerb, lpFile, lpParameters, lpDirectory;
    public int nShow;
    public nint hInstApp, lpIDList;
    public char* lpClass;
    public nint hkeyClass;
    public uint dwHotKey;
    public nint hIcon, hProcess;
}

[StructLayout(LayoutKind.Sequential)]
struct GdiplusStartupInput { public uint Version; public nint DebugEventCallback; public int SuppressBackgroundThread, SuppressExternalCodecs; }

static unsafe partial class Native
{
    public const uint WM_DESTROY = 0x0002, WM_ACTIVATE = 0x0006, WM_PAINT = 0x000F, WM_CLOSE = 0x0010,
        WM_ERASEBKGND = 0x0014, WM_SETTINGCHANGE = 0x001A, WM_SETCURSOR = 0x0020, WM_KEYDOWN = 0x0100,
        WM_COMMAND = 0x0111, WM_TIMER = 0x0113, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MOUSEWHEEL = 0x020A, WM_MOUSELEAVE = 0x02A3,
        WM_DPICHANGED = 0x02E0, WM_CONTEXTMENU = 0x007B, WM_USER = 0x0400, WM_APP = 0x8000,
        WM_THEMECHANGED = 0x031A;

    public const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    public const int SW_HIDE = 0, SW_SHOW = 5;

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")] public static partial ushort RegisterClassEx(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] public static partial nint DefWindowProc(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] public static partial int GetMessage(out MSG msg, nint h, uint min, uint max);
    [LibraryImport("user32.dll")] public static partial int TranslateMessage(in MSG msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] public static partial nint DispatchMessage(in MSG msg);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] public static partial int PostMessage(nint h, uint m, nint w, nint l);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] public static partial int ShowWindow(nint h, int cmd);
    [LibraryImport("user32.dll")] public static partial int IsWindowVisible(nint h);
    [LibraryImport("user32.dll")] public static partial int SetWindowPos(nint h, nint after, int x, int y, int w, int hgt, uint flags);
    [LibraryImport("user32.dll")] public static partial int InvalidateRect(nint h, nint rect, int erase);
    [LibraryImport("user32.dll")] public static partial nint BeginPaint(nint h, out PAINTSTRUCT ps);
    [LibraryImport("user32.dll")] public static partial int EndPaint(nint h, in PAINTSTRUCT ps);
    [LibraryImport("user32.dll")] public static partial int GetClientRect(nint h, out RECT r);
    [LibraryImport("user32.dll")] public static partial int SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] public static partial int GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] public static partial nint MonitorFromPoint(POINT p, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] public static partial int GetMonitorInfo(nint mon, ref MONITORINFO mi);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(nint h);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")] public static partial nint LoadCursor(nint inst, nint id);
    [LibraryImport("user32.dll")] public static partial nint SetCursor(nint c);
    [LibraryImport("user32.dll")] public static partial int TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [LibraryImport("user32.dll")] public static partial nint SetCapture(nint h);
    [LibraryImport("user32.dll")] public static partial int ReleaseCapture();
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int AppendMenu(nint menu, uint flags, nint id, string? text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint h, nint rect);
    [LibraryImport("user32.dll")] public static partial int DestroyMenu(nint menu);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")] public static partial nint LoadImage(nint inst, nint name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] public static partial int DestroyIcon(nint icon);
    [LibraryImport("user32.dll")] public static partial nint SetTimer(nint h, nint id, uint ms, nint proc);
    [LibraryImport("user32.dll")] public static partial int KillTimer(nint h, nint id);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string cls, string? title);
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint h, string text, string caption, uint type);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("user32.dll")] public static partial int SetProcessDpiAwarenessContext(nint ctx);

    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] public static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] public static partial int DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")] public static partial int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint hdc, int mode);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint hdc, uint color);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int DrawText(nint hdc, string text, int len, ref RECT r, uint format);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] public static partial int Shell_NotifyIcon(uint msg, ref NOTIFYICONDATAW data);
    [LibraryImport("shell32.dll")] public static partial int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)] public static partial int ShellExecuteEx(ref SHELLEXECUTEINFOW info);

    [LibraryImport("dwmapi.dll")] public static partial int DwmSetWindowAttribute(nint h, uint attr, void* value, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] public static partial nint GetModuleHandle(nint name);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateMutex(nint attr, int owner, string name);
    [LibraryImport("kernel32.dll")] public static partial uint WaitForSingleObject(nint h, uint ms);
    [LibraryImport("kernel32.dll")] public static partial int GetExitCodeProcess(nint h, out uint code);
    [LibraryImport("kernel32.dll")] public static partial int CloseHandle(nint h);

    // GDI+（只用来画抗锯齿的形状）
    [LibraryImport("gdiplus.dll")] public static partial int GdiplusStartup(out nint token, in GdiplusStartupInput input, nint output);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreateFromHDC(nint hdc, out nint g);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDeleteGraphics(nint g);
    [LibraryImport("gdiplus.dll")] public static partial int GdipSetSmoothingMode(nint g, int mode);
    [LibraryImport("gdiplus.dll")] public static partial int GdipSetPixelOffsetMode(nint g, int mode);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreateSolidFill(uint argb, out nint brush);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDeleteBrush(nint brush);
    [LibraryImport("gdiplus.dll")] public static partial int GdipFillEllipse(nint g, nint brush, float x, float y, float w, float h);
    [LibraryImport("gdiplus.dll")] public static partial int GdipFillRectangle(nint g, nint brush, float x, float y, float w, float h);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreatePath(int fill, out nint path);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDeletePath(nint path);
    [LibraryImport("gdiplus.dll")] public static partial int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);
    [LibraryImport("gdiplus.dll")] public static partial int GdipAddPathLine(nint path, float x1, float y1, float x2, float y2);
    [LibraryImport("gdiplus.dll")] public static partial int GdipClosePathFigure(nint path);
    [LibraryImport("gdiplus.dll")] public static partial int GdipFillPath(nint g, nint brush, nint path);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDrawPath(nint g, nint pen, nint path);
    [LibraryImport("gdiplus.dll")] public static partial int GdipCreatePen1(uint argb, float width, int unit, out nint pen);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDeletePen(nint pen);
    [LibraryImport("gdiplus.dll")] public static partial int GdipSetPenLineCap197819(nint pen, int start, int end, int dash);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDrawLine(nint g, nint pen, float x1, float y1, float x2, float y2);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDrawArc(nint g, nint pen, float x, float y, float w, float h, float start, float sweep);
    [LibraryImport("gdiplus.dll")] public static partial int GdipDrawEllipse(nint g, nint pen, float x, float y, float w, float h);

    public static int LoWord(nint v) => (short)((long)v & 0xFFFF);
    public static int HiWord(nint v) => (short)(((long)v >> 16) & 0xFFFF);
}
