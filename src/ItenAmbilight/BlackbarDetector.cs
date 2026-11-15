using System;

namespace ItenAmbilight
{
    /// <summary>
    /// Erkennt schwarze Balken (oben/unten bei 21:9-Filmen, links/rechts bei 4:3) im verkleinerten Bild.
    /// Balken werden nur als symmetrisches Paar gewertet – eine dunkle Bildhälfte (Nachthimmel) ist kein Balken.
    /// </summary>
    public sealed class BlackbarDetector
    {
        private const double MaxVertical = 0.30;     // höchstens 30 % oben und unten
        private const double MaxHorizontal = 0.40;   // höchstens 40 % links und rechts
        private const double GrowDelay = 1.0;        // Sekunden, bis ein grösserer Balken übernommen wird
        private const double ShrinkDelay = 0.3;      // Sekunden, bis ein kleinerer Balken übernommen wird

        private int _w, _h;
        private int _curV, _curH;                    // übernommene Balken in Pixel (oben = unten, links = rechts)
        private int _candV, _candH;
        private double _candAge;

        /// <summary>Balken oben und unten als Anteil der Bildhöhe (0..0.3).</summary>
        public double Vertical => _h > 0 ? (double)_curV / _h : 0;
        /// <summary>Balken links und rechts als Anteil der Bildbreite (0..0.4).</summary>
        public double Horizontal => _w > 0 ? (double)_curH / _w : 0;

        public void Reset() { _curV = _curH = _candV = _candH = 0; _candAge = 0; }

        /// <param name="px">BGRA, oben beginnend</param>
        /// <param name="threshold">bis zu diesem Wert (0..255) gilt ein Pixel als schwarz</param>
        /// <param name="dt">Zeit seit dem letzten Aufruf in Sekunden</param>
        public void Update(byte[] px, int w, int h, int threshold, double dt)
        {
            if (w < 8 || h < 8 || px.Length < w * h * 4) return;
            if (w != _w || h != _h) { _w = w; _h = h; Reset(); }

            int maxV = (int)(h * MaxVertical), maxH = (int)(w * MaxHorizontal);

            int top = 0, bottom = 0;
            while (top < maxV && RowBlack(px, w, top, threshold)) top++;
            while (bottom < maxV && RowBlack(px, w, h - 1 - bottom, threshold)) bottom++;
            if (top >= maxV && bottom >= maxV) { _candAge = 0; return; }      // dunkles Bild -> keine Aussage

            int v = Math.Min(top, bottom);
            int left = 0, right = 0;
            while (left < maxH && ColumnBlack(px, w, left, v, h - v, threshold)) left++;
            while (right < maxH && ColumnBlack(px, w, w - 1 - right, v, h - v, threshold)) right++;
            if (left >= maxH && right >= maxH) { _candAge = 0; return; }

            int hz = Math.Min(left, right);
            // Durch die Verkleinerung ist die Kante des Balkens eine Mischzeile -> 1 Pixel Sicherheitsrand
            if (v > 0) v++;
            if (hz > 0) hz++;

            if (Math.Abs(v - _curV) <= 1 && Math.Abs(hz - _curH) <= 1) { _candAge = 0; return; }   // unverändert

            if (Math.Abs(v - _candV) > 1 || Math.Abs(hz - _candH) > 1)
            {
                _candV = v; _candH = hz; _candAge = 0;                                       // neuer Kandidat
                return;
            }

            _candAge += dt;
            bool grows = _candV > _curV || _candH > _curH;
            if (_candAge >= (grows ? GrowDelay : ShrinkDelay))
            {
                _curV = _candV; _curH = _candH; _candAge = 0;
            }
        }

        private static bool RowBlack(byte[] px, int w, int y, int thr)
        {
            int allowed = w / 50;                     // 2 % dürfen hell sein (Mauszeiger, Rauschen)
            int bright = 0;
            int o = y * w * 4;
            for (int x = 0; x < w; x++, o += 4)
                if (px[o] > thr || px[o + 1] > thr || px[o + 2] > thr)
                    if (++bright > allowed) return false;
            return true;
        }

        private static bool ColumnBlack(byte[] px, int w, int x, int y0, int y1, int thr)
        {
            int allowed = (y1 - y0) / 50;
            int bright = 0;
            for (int y = y0; y < y1; y++)
            {
                int o = (y * w + x) * 4;
                if (px[o] > thr || px[o + 1] > thr || px[o + 2] > thr)
                    if (++bright > allowed) return false;
            }
            return true;
        }
    }
}
