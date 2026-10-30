using static HomePodCast.UI.Native;

namespace HomePodCast.UI;

/// <summary>双缓冲画布：GDI+ 画形状（抗锯齿），GDI 画文字（ClearType）。</summary>
sealed class Canvas : IDisposable
{
    readonly nint _hdc, _mem, _bmp, _oldBmp, _g;
    readonly int _w, _h;
    public float Scale { get; }

    static nint _gdipToken;
    static readonly Dictionary<(int, int), nint> Fonts = [];

    public Canvas(nint target, int w, int h, float scale)
    {
        if (_gdipToken == 0) GdiplusStartup(out _gdipToken, new GdiplusStartupInput { Version = 1 }, 0);
        _hdc = target; _w = w; _h = h; Scale = scale;
        _mem = CreateCompatibleDC(target);
        _bmp = CreateCompatibleBitmap(target, w, h);
        _oldBmp = SelectObject(_mem, _bmp);
        GdipCreateFromHDC(_mem, out _g);
        GdipSetSmoothingMode(_g, 4);      // AntiAlias
        GdipSetPixelOffsetMode(_g, 4);    // Half
        SetBkMode(_mem, 1);               // TRANSPARENT
    }

    float S(float v) => v * Scale;

    public void Clear(uint argb) => FillRect(0, 0, _w / Scale, _h / Scale, argb);

    public void FillRect(float x, float y, float w, float h, uint argb)
    {
        GdipCreateSolidFill(argb, out var b);
        GdipFillRectangle(_g, b, S(x), S(y), S(w), S(h));
        GdipDeleteBrush(b);
    }

    public void FillRound(float x, float y, float w, float h, float r, uint argb)
    {
        var path = RoundPath(x, y, w, h, r);
        GdipCreateSolidFill(argb, out var b);
        GdipFillPath(_g, b, path);
        GdipDeleteBrush(b);
        GdipDeletePath(path);
    }

    public void StrokeRound(float x, float y, float w, float h, float r, uint argb, float width = 1)
    {
        var path = RoundPath(x + 0.5f, y + 0.5f, w - 1, h - 1, r);
        GdipCreatePen1(argb, S(width), 2, out var pen);
        GdipDrawPath(_g, pen, path);
        GdipDeletePen(pen);
        GdipDeletePath(path);
    }

    nint RoundPath(float x, float y, float w, float h, float r)
    {
        GdipCreatePath(0, out var p);
        float X = S(x), Y = S(y), W = S(w), H = S(h), D = Math.Min(S(r) * 2, Math.Min(W, H));
        GdipAddPathArc(p, X, Y, D, D, 180, 90);
        GdipAddPathArc(p, X + W - D, Y, D, D, 270, 90);
        GdipAddPathArc(p, X + W - D, Y + H - D, D, D, 0, 90);
        GdipAddPathArc(p, X, Y + H - D, D, D, 90, 90);
        GdipClosePathFigure(p);
        return p;
    }

    public void FillCircle(float cx, float cy, float r, uint argb)
    {
        GdipCreateSolidFill(argb, out var b);
        GdipFillEllipse(_g, b, S(cx - r), S(cy - r), S(r * 2), S(r * 2));
        GdipDeleteBrush(b);
    }

    public void Line(float x1, float y1, float x2, float y2, uint argb, float width)
    {
        GdipCreatePen1(argb, S(width), 2, out var pen);
        GdipSetPenLineCap197819(pen, 2, 2, 0); // 圆头
        GdipDrawLine(_g, pen, S(x1), S(y1), S(x2), S(y2));
        GdipDeletePen(pen);
    }

    public void Arc(float cx, float cy, float r, float start, float sweep, uint argb, float width)
    {
        GdipCreatePen1(argb, S(width), 2, out var pen);
        GdipSetPenLineCap197819(pen, 2, 2, 0);
        GdipDrawArc(_g, pen, S(cx - r), S(cy - r), S(r * 2), S(r * 2), start, sweep);
        GdipDeletePen(pen);
    }

    public const uint Left = 0x0, Center = 0x1, Right = 0x2, VCenter = 0x4, SingleLine = 0x20, Ellipsis = 0x8000, NoPrefix = 0x800;

    /// <summary>画一个 Windows 11 系统图标（Segoe Fluent Icons），居中于 (cx, cy)。</summary>
    public void Icon(char glyph, float cx, float cy, float size, uint argb) =>
        Text(glyph.ToString(), cx - size, cy - size, size * 2, size * 2, size, argb, align: Center, weight: -1);

    public void Text(string s, float x, float y, float w, float h, float size, uint argb, bool bold = false, uint align = Left, int weight = 0)
    {
        int px = (int)Math.Round(S(size));
        weight = weight == 0 ? (bold ? 600 : 400) : weight;
        if (!Fonts.TryGetValue((px, weight), out var font))
        {
            // weight 为 -1 表示图标字体；文字用 CLEARTYPE_QUALITY，中文由系统字体链接回退到微软雅黑
            font = weight < 0
                ? CreateFont(-px, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe Fluent Icons")
                : CreateFont(-px, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI Variable Text");
            Fonts[(px, weight)] = font;
        }
        var old = SelectObject(_mem, font);
        // GDI 颜色是 0x00BBGGRR；文字不透明度靠预先混好的颜色表达
        SetTextColor(_mem, ((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF));
        var r = new RECT { Left = (int)S(x), Top = (int)S(y), Right = (int)S(x + w), Bottom = (int)S(y + h) };
        DrawText(_mem, s, -1, ref r, align | VCenter | SingleLine | Ellipsis | NoPrefix);
        SelectObject(_mem, old);
    }

    public void Dispose()
    {
        BitBlt(_hdc, 0, 0, _w, _h, _mem, 0, 0, 0x00CC0020);
        GdipDeleteGraphics(_g);
        SelectObject(_mem, _oldBmp);
        DeleteObject(_bmp);
        DeleteDC(_mem);
    }
}
