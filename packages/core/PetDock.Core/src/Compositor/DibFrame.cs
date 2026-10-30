// 实现 原规格 12 §2.2「GDI+ + UpdateLayeredWindow」：32 位自上而下 DIB section + 预乘 alpha 帧缓冲
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PetDock.Core.Compositor;

/// <summary>
/// 一张 32 位、biHeight 为负（自上而下）的 BI_RGB DIB section，包一层 Graphics.FromHdc。
/// 写入时按 a/255 预乘 R/G/B —— UpdateLayeredWindow 的 AC_SRC_ALPHA 语义要求源位图已预乘。
/// </summary>
internal sealed class DibFrame : IDisposable
{
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _bits;
    private readonly int _width;
    private readonly int _height;
    private Graphics? _graphics;
    private bool _disposed;

    private DibFrame(int width, int height, IntPtr screenDc, IntPtr memoryDc, IntPtr dib, IntPtr bits)
    {
        _width = width;
        _height = height;
        _screenDc = screenDc;
        _memoryDc = memoryDc;
        _dib = dib;
        _bits = bits;
    }

    /// <summary>创建一张 w×h 的 32 位 ARGB 自上而下 DIB。</summary>
    public static DibFrame Create(int w, int h)
    {
        if (w <= 0 || h <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(w), "DIB 尺寸必须为正");
        }

        var info = new BITMAPINFO
        {
            bmiHeader =
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // 负 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32.BI_RGB,
                biSizeImage = (uint)(w * h * 4),
            },
        };

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(screen) 失败");
        }

        IntPtr dib = Win32.CreateDIBSection(screenDc, ref info, Win32.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException($"CreateDIBSection 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
        }

        IntPtr memoryDc = Win32.CreateCompatibleDC(screenDc);
        if (memoryDc == IntPtr.Zero)
        {
            Win32.DeleteObject(dib);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("CreateCompatibleDC 失败");
        }

        Win32.SelectObject(memoryDc, dib);
        return new DibFrame(w, h, screenDc, memoryDc, dib, bits);
    }

    /// <summary>承载这张 DIB 的窗口句柄（Present 的目标）。</summary>
    public IntPtr Hwnd { get; set; }

    public int Width => _width;

    public int Height => _height;

    /// <summary>DIB 首字节指针，可直接 unsafe 访问（BGRA，自上而下）。</summary>
    public IntPtr Bits => _bits;

    /// <summary>读 DIB 内某点的四个通道（自检诊断用：确认写进去的 alpha 与预期一致）。</summary>
    public (byte B, byte G, byte R, byte A) SamplePixel(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (x < 0 || y < 0 || x >= _width || y >= _height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) 超出 DIB {_width}x{_height}");
        }

        unsafe
        {
            byte* p = (byte*)_bits.ToPointer() + ((y * _width) + x) * 4;
            return (p[0], p[1], p[2], p[3]);
        }
    }

    /// <summary>取一张可画进 DIB 的 GDI+ Graphics。</summary>
    public Graphics BeginDraw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _graphics ??= Graphics.FromHdc(_memoryDc);
        return _graphics;
    }

    /// <summary>
    /// <summary>
    /// 把一张 32bpp 位图以预乘方式写入 DIB（尺寸必须一致；逐像素 LockBits + unsafe 循环）。
    /// **接受两种 32bpp 输入，因为上游有两层各自合法的约定**（原规格 11:49 与 M0 的过渡面）：
    ///   - `Format32bppPArgb`（**预乘** alpha）：原规格 11 §2.1 对帧的硬要求（ABI 视图实现交付的就是它）；
    ///   - `Format32bppArgb`（直通 alpha）：M0 的 `IFrameContentSource` 交付的形态（sample-pet 走这条）。
    /// 本方法本来就是「按 a 逐通道相乘」写进 DIB，所以：
    /// PArgb 输入**直接用**（已是预乘值，再乘一次会二次变暗 —— 这是必须区分的根本原因）；
    /// Argb 输入按 a 预乘（M0 的既有语义，字节不变）。
    /// 其他 32bpp 变体（Rgb/Xrgb）仍拒绝：它们的 alpha 通道不可信，写进去会让整窗变成实心。
    /// </summary>
    public void DrawBitmap(Bitmap bitmap)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bitmap);

        if (bitmap.Width != _width || bitmap.Height != _height)
        {
            throw new ArgumentException($"位图尺寸 {bitmap.Width}x{bitmap.Height} 与 DIB {_width}x{_height} 不一致", nameof(bitmap));
        }

        if (bitmap.PixelFormat != PixelFormat.Format32bppArgb
            && bitmap.PixelFormat != PixelFormat.Format32bppPArgb)
        {
            throw new ArgumentException(
                $"需要 32bpp 带 alpha 的位图（Argb 或 PArgb），实际 {bitmap.PixelFormat}", nameof(bitmap));
        }

        bool alreadyPremultiplied = bitmap.PixelFormat == PixelFormat.Format32bppPArgb;
        var rect = new Rectangle(0, 0, _width, _height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* src = (byte*)data.Scan0.ToPointer();
                byte* dst = (byte*)_bits.ToPointer();
                int stride = data.Stride;
                for (int y = 0; y < _height; y++)
                {
                    byte* s = src + (y * stride);
                    byte* d = dst + (y * _width * 4);
                    for (int x = 0; x < _width; x++)
                    {
                        byte a = s[3];
                        if (alreadyPremultiplied)
                        {
                            d[0] = s[0];
                            d[1] = s[1];
                            d[2] = s[2];
                        }
                        else
                        {
                            d[0] = Premultiply(s[0], a);
                            d[1] = Premultiply(s[1], a);
                            d[2] = Premultiply(s[2], a);
                        }

                        d[3] = a;
                        s += 4;
                        d += 4;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>用纯色填满整张 DIB（贴图缺失时的兜底路径）。</summary>
    public void FillOpaque(Color color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        unsafe
        {
            byte* d = (byte*)_bits.ToPointer();
            for (int i = 0; i < _width * _height; i++)
            {
                d[0] = color.B;
                d[1] = color.G;
                d[2] = color.R;
                d[3] = 255;
                d += 4;
            }
        }
    }

    /// <summary>
    /// 把当前 DIB 内容以 ULW_ALPHA 提交到屏幕上的 destination。
    /// 目标位置与尺寸都必须显式给出：本机实测（2026-10-15，见 .dsh-code/mistakes.md）
    /// UpdateLayeredWindow 传 pptDst=NULL + psize=NULL 时返回 TRUE 却整层不合成，
    /// 窗口保持全透明 —— 表现就是「像素画对了、自检全绿、屏幕上一个像素都没有」。
    /// 尺寸一律取 DIB 自身（_width×_height），调用方无从传错：帧尺寸只有 DIB 一个 owner。
    /// </summary>
    public void Present(Point destination, byte alphaConstant)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("DibFrame.Hwnd 未设置，无法调用 UpdateLayeredWindow");
        }

        var blend = new BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = alphaConstant,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };

        var target = new POINT { X = destination.X, Y = destination.Y };
        var size = new SIZE { cx = _width, cy = _height };
        var source = new POINT { X = 0, Y = 0 };

        if (!Win32.UpdateLayeredWindow(
                Hwnd,
                IntPtr.Zero,
                ref target,
                ref size,
                _memoryDc,
                ref source,
                0,
                ref blend,
                Win32.ULW_ALPHA))
        {
            throw new InvalidOperationException($"UpdateLayeredWindow 失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
        }
    }

    private static byte Premultiply(byte channel, byte alpha)
    {
        return (byte)(((channel * alpha) + 127) / 255);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _graphics?.Dispose();
        _graphics = null;

        if (_memoryDc != IntPtr.Zero)
        {
            Win32.DeleteDC(_memoryDc);
        }

        if (_dib != IntPtr.Zero)
        {
            Win32.DeleteObject(_dib);
        }

        if (_screenDc != IntPtr.Zero)
        {
            Win32.ReleaseDC(IntPtr.Zero, _screenDc);
        }
    }
}
