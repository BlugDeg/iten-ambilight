using System;

namespace ItenAmbilight
{
    /// <summary>Quelle für das (verkleinerte) Bildschirmbild.</summary>
    public interface IScreenSource : IDisposable
    {
        string Name { get; }
        int Width { get; }
        int Height { get; }
        /// <summary>true = das Bild kommt als HDR (und wurde bereits auf den normalen Bereich umgerechnet).</summary>
        bool IsHdr { get; }
        /// <summary>BGRA, 4 Byte pro Pixel, Zeile für Zeile, oben beginnend.</summary>
        byte[] Pixels { get; }
        /// <summary>Nimmt ein Bild auf. waitMs = wie lange höchstens auf ein neues Bild gewartet wird.</summary>
        bool Capture(int waitMs);
    }
}
