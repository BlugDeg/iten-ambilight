using System;
using System.Runtime.InteropServices;

namespace ItenAmbilight
{
    /// <summary>
    /// Bildschirmaufnahme über GDI (StretchBlt). Liefert ein stark verkleinertes Bild (BGRA).
    /// Greift nicht in andere Programme ein. Rückfall, wenn die Desktop Duplication API nicht verfügbar ist.
    /// </summary>
    public sealed class GdiCapture : IScreenSource
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }           // BGRA, Zeile für Zeile, oben beginnend

        private IntPtr _screenDc, _memDc, _bitmap;
        private BITMAPINFO _bmi;

        private readonly int _sx, _sy, _sw, _sh;
        public string Name => "GDI";
        public bool IsHdr => false;

        /// <param name="x">Bereich des Monitors in Bildschirmpixeln</param>
        public GdiCapture(int x, int y, int w, int h, int width = 320, int height = 180)
        {
            _sx = x; _sy = y; _sw = w; _sh = h;
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];

            _screenDc = GetDC(IntPtr.Zero);
            _memDc = CreateCompatibleDC(_screenDc);
            _bitmap = CreateCompatibleBitmap(_screenDc, width, height);
            SetStretchBltMode(_memDc, COLORONCOLOR);

            _bmi = new BITMAPINFO();
            _bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            _bmi.bmiHeader.biWidth = width;
            _bmi.bmiHeader.biHeight = -height;   // negativ = oben beginnend
            _bmi.bmiHeader.biPlanes = 1;
            _bmi.bmiHeader.biBitCount = 32;
            _bmi.bmiHeader.biCompression = 0;    // BI_RGB
        }

        public bool Capture(int waitMs)
        {
            int x = _sx, y = _sy, w = _sw, h = _sh;
            if (w <= 0 || h <= 0) return false;
            IntPtr old = SelectObject(_memDc, _bitmap);
            bool ok = StretchBlt(_memDc, 0, 0, Width, Height, _screenDc, x, y, w, h, SRCCOPY);
            SelectObject(_memDc, old);           // Bitmap darf für GetDIBits nicht ausgewählt sein
            if (!ok) return false;
            int lines = GetDIBits(_memDc, _bitmap, 0, (uint)Height, Pixels, ref _bmi, 0);
            return lines == Height;
        }

        public void Dispose()
        {
            if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
            if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_screenDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _screenDc); _screenDc = IntPtr.Zero; }
        }

        // ---------- Win32 ----------
        private const int SRCCOPY = 0x00CC0020;
        private const int COLORONCOLOR = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hDc, int mode);
        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh,
                                              IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hDc, IntPtr hBmp, uint start, uint lines,
                                            [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);
    }
}
