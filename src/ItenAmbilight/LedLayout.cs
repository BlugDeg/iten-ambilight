using System;
using System.Collections.Generic;

namespace ItenAmbilight
{
    /// <summary>Messbereich einer LED, normiert 0..1 innerhalb des (beschnittenen) Bildes.</summary>
    public readonly struct LedRegion
    {
        public readonly double X0, Y0, X1, Y1;
        /// <summary>0 = oben, 1 = rechts, 2 = unten, 3 = links</summary>
        public readonly int Side;
        public LedRegion(double x0, double y0, double x1, double y1, int side) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; Side = side; }
    }

    public static class LedLayout
    {
        /// <summary>Berechnet aus Diagonale, Seitenverhältnis und LED-Dichte die LED-Anzahl pro Seite.</summary>
        public static (int horizontal, int vertical, double widthMm, double heightMm) Calculate(
            double diagonalInch, int pixelWidth, int pixelHeight, double ledsPerMeter)
        {
            if (diagonalInch <= 0 || pixelWidth <= 0 || pixelHeight <= 0 || ledsPerMeter <= 0)
                return (0, 0, 0, 0);
            double diagMm = diagonalInch * 25.4;
            double ratio = Math.Sqrt((double)pixelWidth * pixelWidth + (double)pixelHeight * pixelHeight);
            double w = diagMm * pixelWidth / ratio;
            double h = diagMm * pixelHeight / ratio;
            double pitch = 1000.0 / ledsPerMeter;
            return ((int)Math.Round(w / pitch), (int)Math.Round(h / pitch), w, h);
        }

        /// <summary>
        /// Baut die Messbereiche in der Reihenfolge, in der die LEDs am Strip verdrahtet sind.
        /// aspect = Breite / Höhe des Bildes (für gleich tiefe Messstreifen an allen Seiten).
        /// </summary>
        public static List<LedRegion> Build(AppSettings s, double aspect)
        {
            double dy = Math.Clamp(s.SampleDepth / 100.0, 0.01, 0.5);
            double dx = Math.Clamp(dy / Math.Max(aspect, 0.1), 0.005, 0.5);

            // Ring im Uhrzeigersinn (von vorne gesehen), beginnend oben links
            var ring = new List<LedRegion>(s.TotalLeds);
            for (int i = 0; i < s.LedsTop; i++)                       // oben: links -> rechts
                ring.Add(new LedRegion((double)i / s.LedsTop, 0, (double)(i + 1) / s.LedsTop, dy, 0));
            for (int i = 0; i < s.LedsRight; i++)                     // rechts: oben -> unten
                ring.Add(new LedRegion(1 - dx, (double)i / s.LedsRight, 1, (double)(i + 1) / s.LedsRight, 1));
            for (int i = s.LedsBottom - 1; i >= 0; i--)               // unten: rechts -> links
                ring.Add(new LedRegion((double)i / s.LedsBottom, 1 - dy, (double)(i + 1) / s.LedsBottom, 1, 2));
            for (int i = s.LedsLeft - 1; i >= 0; i--)                 // links: unten -> oben
                ring.Add(new LedRegion(0, (double)i / s.LedsLeft, dx, (double)(i + 1) / s.LedsLeft, 3));

            int n = ring.Count;
            if (n == 0) return ring;

            int offset = s.Start switch
            {
                StartCorner.TopLeft => 0,
                StartCorner.TopRight => s.LedsTop,
                StartCorner.BottomRight => s.LedsTop + s.LedsRight,
                _ => s.LedsTop + s.LedsRight + s.LedsBottom,           // BottomLeft
            };

            var result = new List<LedRegion>(n);
            for (int i = 0; i < n; i++) result.Add(ring[(offset + i) % n]);
            if (!s.Clockwise) result.Reverse();                        // gleiche Ecke, andere Richtung
            return result;
        }
    }
}
