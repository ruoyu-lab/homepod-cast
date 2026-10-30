using Microsoft.Win32;

namespace HomePodCast.UI;

/// <summary>跟随系统的深浅色和强调色。</summary>
sealed class Theme
{
    public bool Dark;
    public uint Bg, Card, CardHover, CardStroke, Text, Sub, Track, Divider, Accent, OnAccent, ErrorBg, ErrorText, Neutral;

    public static Theme Load(bool? forceDark = null)
    {
        bool dark = true;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = k?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
        }
        catch { }
        dark = forceDark ?? dark;

        uint accent = dark ? 0xFF4CC2FF : 0xFF005FB8;
        try
        {
            // AccentPalette：8 个 RGBA，依次为 浅3 浅2 浅1 基色 深1 深2 深3
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
            {
                int i = dark ? 1 : 4;
                accent = 0xFF000000 | (uint)p[i * 4] << 16 | (uint)p[i * 4 + 1] << 8 | p[i * 4 + 2];
            }
        }
        catch { }

        return dark
            ? new Theme
            {
                Dark = true, Bg = 0xFF1F1F1F, Card = 0xFF2B2B2B, CardHover = 0xFF323232, CardStroke = 0xFF303030,
                Text = 0xFFFFFFFF, Sub = 0xFFA0A0A0, Track = 0xFF4A4A4A, Divider = 0xFF2E2E2E,
                Accent = accent, OnAccent = 0xFF000000, ErrorBg = 0xFF442726, ErrorText = 0xFFFF99A4, Neutral = 0xFF3A3A3A,
            }
            : new Theme
            {
                Dark = false, Bg = 0xFFF3F3F3, Card = 0xFFFFFFFF, CardHover = 0xFFF8F8F8, CardStroke = 0xFFE5E5E5,
                Text = 0xFF1B1B1B, Sub = 0xFF616161, Track = 0xFFC8C8C8, Divider = 0xFFE5E5E5,
                Accent = accent, OnAccent = 0xFFFFFFFF, ErrorBg = 0xFFFDE7E9, ErrorText = 0xFFC42B1C, Neutral = 0xFFEBEBEB,
            };
    }

    /// <summary>在背景色上按透明度混色（文字用 GDI 绘制，只能用不透明颜色）。</summary>
    public static uint Blend(uint fg, uint bg, float alpha)
    {
        uint Ch(int s) => (uint)Math.Round(((fg >> s) & 0xFF) * alpha + ((bg >> s) & 0xFF) * (1 - alpha));
        return 0xFF000000 | Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }
}
