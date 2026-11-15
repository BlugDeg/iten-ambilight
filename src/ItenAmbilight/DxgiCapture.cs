using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ItenAmbilight
{
    /// <summary>
    /// Bildschirmaufnahme über die Desktop Duplication API (wie OBS "Bildschirmaufnahme").
    /// Das Bild bleibt auf der Grafikkarte, wird dort über Mipmaps verkleinert und erst
    /// dann (ca. 240 x 135 Pixel) in den Arbeitsspeicher kopiert. Greift nicht in Spiele ein.
    ///
    /// HDR: Ist HDR in Windows eingeschaltet, liefert Windows das Bild als 16-Bit-Gleitkomma
    /// (scRGB, linear, 1.0 = 80 nits). Das wird hier auf den normalen Bereich umgerechnet,
    /// damit die Farben nicht ausgewaschen oder überstrahlt sind.
    /// </summary>
    public sealed class DxgiCapture : IScreenSource
    {
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutputDuplication? _duplication;
        private ID3D11Texture2D? _mipTexture;
        private ID3D11ShaderResourceView? _mipView;
        private ID3D11Texture2D? _staging;
        private Format _format = Format.Unknown;
        private int _fullW, _fullH;
        private int _mipLevel;
        private bool _hasFrame;
        private readonly int _maxWidth;

        private readonly byte[] _hdrLut = new byte[65536];   // Half-Float-Bits -> sRGB-Byte
        private double _lutPaperWhite = -1;

        public string Name => IsHdr ? "Desktop Duplication, HDR" : "Desktop Duplication";
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsHdr => _format == Format.R16G16B16A16_Float;
        public byte[] Pixels { get; private set; } = Array.Empty<byte>();

        /// <summary>Helligkeit in nits, die als normales Weiss (255) ausgegeben wird. Nur bei HDR von Bedeutung.</summary>
        public double PaperWhiteNits { get; set; } = 200;

        /// <param name="deviceName">Windows-Name des Monitors, z. B. \\.\DISPLAY1</param>
        /// <param name="allowHdr">true = bei eingeschaltetem HDR das HDR-Bild anfordern</param>
        /// <param name="maxWidth">gewünschte Breite des verkleinerten Bildes (wird in Zweierschritten angenähert)</param>
        public DxgiCapture(string deviceName, bool allowHdr, int maxWidth = 320)
        {
            _maxWidth = Math.Clamp(maxWidth, 64, 1920);
            try { Init(deviceName, allowHdr); }
            catch { Dispose(); throw; }
        }

        private void Init(string deviceName, bool allowHdr)
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            IDXGIAdapter1? adapter = null;
            IDXGIOutput? output = null;
            for (uint a = 0; output == null && factory.EnumAdapters1(a, out IDXGIAdapter1 ad).Success; a++)
            {
                for (uint o = 0; ad.EnumOutputs(o, out IDXGIOutput op).Success; o++)
                {
                    if (string.Equals(op.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        output = op;
                        adapter = ad;
                        break;
                    }
                    op.Dispose();
                }
                if (output == null) ad.Dispose();
            }
            if (adapter == null || output == null)
                throw new InvalidOperationException("Monitor " + deviceName + " nicht gefunden");

            try
            {
                FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
                                        out _device, out _context).CheckError();

                // Neuere Schnittstelle (Windows 10 1803+): liefert bei HDR das unverfälschte HDR-Bild
                if (allowHdr)
                {
                    try
                    {
                        using IDXGIOutput5? output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
                        if (output5 != null)
                            _duplication = output5.DuplicateOutput1(_device!,
                                new[] { Format.R16G16B16A16_Float, Format.B8G8R8A8_UNorm });
                    }
                    catch { _duplication = null; }
                }
                if (_duplication == null)
                {
                    using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                    _duplication = output1.DuplicateOutput(_device!);
                }
            }
            finally
            {
                output.Dispose();
                adapter.Dispose();
            }
        }

        /// <summary>Legt die Hilfstexturen passend zum gelieferten Bild an (Format und Grösse).</summary>
        private void CreateTextures(Texture2DDescription frame)
        {
            _staging?.Dispose(); _staging = null;
            _mipView?.Dispose(); _mipView = null;
            _mipTexture?.Dispose(); _mipTexture = null;

            _format = frame.Format;
            _fullW = (int)frame.Width;
            _fullH = (int)frame.Height;

            // Mip-Stufe wählen, bei der das Bild höchstens _maxWidth Pixel breit ist
            _mipLevel = 0;
            while ((_fullW >> _mipLevel) > _maxWidth && (_fullH >> (_mipLevel + 1)) >= 1) _mipLevel++;
            Width = Math.Max(1, _fullW >> _mipLevel);
            Height = Math.Max(1, _fullH >> _mipLevel);
            Pixels = new byte[Width * Height * 4];

            _mipTexture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_fullW,
                Height = (uint)_fullH,
                MipLevels = (uint)(_mipLevel + 1),
                ArraySize = 1,
                Format = _format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.GenerateMips,
            });
            _mipView = _device.CreateShaderResourceView(_mipTexture);

            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = _format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        /// <summary>
        /// Tabelle HDR -> normaler Bereich: Helligkeit auf "Papierweiss" beziehen, Spitzlichter weich
        /// begrenzen, dann die übliche sRGB-Kurve. Pro Farbkanal, darum reicht eine Tabelle.
        /// </summary>
        private void BuildHdrLut(double paperWhite)
        {
            paperWhite = Math.Clamp(paperWhite, 80, 1000);
            const double knee = 0.75;                           // bis hier 1:1, darüber weich Richtung 1.0
            for (int i = 0; i < 65536; i++)
            {
                double v = (double)BitConverter.UInt16BitsToHalf((ushort)i);
                if (double.IsNaN(v) || v <= 0) { _hdrLut[i] = 0; continue; }     // negativ = ausserhalb sRGB
                double x = v * 80.0 / paperWhite;                               // 1.0 = Papierweiss
                double y = x <= knee ? x : knee + (1 - knee) * (1 - Math.Exp(-(x - knee) / (1 - knee)));
                double srgb = y <= 0.0031308 ? 12.92 * y : 1.055 * Math.Pow(y, 1 / 2.4) - 0.055;
                _hdrLut[i] = (byte)Math.Clamp(srgb * 255.0 + 0.5, 0, 255);
            }
            _lutPaperWhite = paperWhite;
        }

        public unsafe bool Capture(int waitMs)
        {
            if (_duplication == null || _context == null) return false;

            var result = _duplication.AcquireNextFrame((uint)Math.Max(0, waitMs), out _, out IDXGIResource? resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                return _hasFrame;                       // Bild unverändert -> altes Bild weiterverwenden
            if (result.Failure || resource == null)
                return false;                           // z. B. AccessLost -> Aufrufer baut die Quelle neu auf

            try
            {
                using ID3D11Texture2D frame = resource.QueryInterface<ID3D11Texture2D>();
                Texture2DDescription fd = frame.Description;
                if (fd.Format != Format.B8G8R8A8_UNorm && fd.Format != Format.R16G16B16A16_Float) return false;
                if (_mipTexture == null || fd.Format != _format || (int)fd.Width != _fullW || (int)fd.Height != _fullH)
                    CreateTextures(fd);

                _context.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, frame, 0);
                _context.GenerateMips(_mipView!);
                _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, (uint)_mipLevel);

                MappedSubresource map = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    if (!IsHdr)
                    {
                        int rowBytes = Width * 4;
                        for (int y = 0; y < Height; y++)
                            Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, Pixels, y * rowBytes, rowBytes);
                    }
                    else
                    {
                        if (Math.Abs(_lutPaperWhite - Math.Clamp(PaperWhiteNits, 80, 1000)) > 0.5)
                            BuildHdrLut(PaperWhiteNits);
                        byte[] lut = _hdrLut;
                        byte[] dst = Pixels;
                        int o = 0;
                        for (int y = 0; y < Height; y++)
                        {
                            ushort* src = (ushort*)((byte*)map.DataPointer + y * (int)map.RowPitch);   // R G B A
                            for (int x = 0; x < Width; x++, src += 4, o += 4)
                            {
                                dst[o] = lut[src[2]];
                                dst[o + 1] = lut[src[1]];
                                dst[o + 2] = lut[src[0]];
                                dst[o + 3] = 255;
                            }
                        }
                    }
                }
                finally { _context.Unmap(_staging!, 0); }
                _hasFrame = true;
                return true;
            }
            finally
            {
                resource.Dispose();
                _duplication.ReleaseFrame();
            }
        }

        public void Dispose()
        {
            _staging?.Dispose(); _staging = null;
            _mipView?.Dispose(); _mipView = null;
            _mipTexture?.Dispose(); _mipTexture = null;
            _duplication?.Dispose(); _duplication = null;
            _context?.Dispose(); _context = null;
            _device?.Dispose(); _device = null;
        }
    }
}
