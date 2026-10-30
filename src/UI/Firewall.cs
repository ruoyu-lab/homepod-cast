using static HomePodCast.UI.Native;

namespace HomePodCast.UI;

/// <summary>
/// HomePod 需要回连电脑做时钟同步，Windows 防火墙默认会拦截。
/// 这里只在用户点击后弹出系统 UAC，为本程序添加一条“仅限专用网络”的入站放行规则。
/// </summary>
static unsafe class Firewall
{
    public static bool RequestAllow(nint owner)
    {
        var exe = Environment.ProcessPath!;
        var args = $"/c netsh advfirewall firewall delete rule name=\"HomePodCast\" >nul & " +
                   $"netsh advfirewall firewall add rule name=\"HomePodCast\" dir=in action=allow " +
                   $"program=\"{exe}\" enable=yes profile=private,domain";
        fixed (char* verb = "runas", file = "cmd.exe", param = args)
        {
            var info = new SHELLEXECUTEINFOW
            {
                cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                fMask = 0x40, // SEE_MASK_NOCLOSEPROCESS
                hwnd = owner,
                lpVerb = verb,
                lpFile = file,
                lpParameters = param,
                nShow = 0,
            };
            if (ShellExecuteEx(ref info) == 0) return false; // 用户取消了 UAC
            WaitForSingleObject(info.hProcess, 15000);
            GetExitCodeProcess(info.hProcess, out var code);
            CloseHandle(info.hProcess);
            return code == 0;
        }
    }
}
