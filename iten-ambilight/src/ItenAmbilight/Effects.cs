using System;
using System.Collections.Generic;
using System.Globalization;

namespace ItenAmbilight
{
    /// <summary>Einstellungen eines Effekts (werden pro Effekt gespeichert).</summary>
    public sealed class EffectParams
    {
        public int Speed { get; set; } = 50;            // 1..100
        public int Intensity { get; set; } = 60;        // 1..100 (Dichte, Stärke oder Breite – je nach Effekt)
        public string Color1 { get; set; } = "#FF6A00";
        public string Color2 { get; set; } = "#35D0F2";
        public string Color3 { get; set; } = "#D04BFF";

        public EffectParams Clone() => (EffectParams)MemberwiseClone();
    }

    /// <summary>Beschreibung eines mitgelieferten Effekts.</summary>
    public sealed class EffectDef
    {
        public string Id = "";
        public string Title = "";
        public string Category = "";
        /// <summary>Wie viele der drei Farben der Effekt verwendet (0 = feste Farben).</summary>
        public int ColorCount;
        public string[] Colors = { "#FF6A00", "#35D0F2", "#D04BFF" };
        public int Speed = 50, Intensity = 60;
        public Action<FxContext> Render = _ => { };

        public EffectParams Defaults() => new EffectParams
        {
            Speed = Speed, Intensity = Intensity, Color1 = Colors[0], Color2 = Colors[1], Color3 = Colors[2],
        };
    }

    /// <summary>Alles, was ein Effekt zum Zeichnen eines Bildes braucht.</summary>
    public sealed class FxContext
    {
        public int N;
        public double T;                    // Zeit in Sekunden, bereits mit der Geschwindigkeit verrechnet
        public double Dt;                   // Zeitschritt, mit Geschwindigkeit
        public double RealDt;               // Zeitschritt ohne Geschwindigkeit
        public double Intensity;            // 0..1
        public double[] C1 = { 255, 255, 255 }, C2 = { 255, 255, 255 }, C3 = { 255, 255, 255 };
        public double[] Out = Array.Empty<double>();
        public double[] A = Array.Empty<double>(), B = Array.Empty<double>();   // Speicher pro LED
        public double[] G = new double[16];                                     // Speicher für den ganzen Effekt
        public Random Rng = new Random();

        public void Set(int i, double r, double g, double b) { Out[i * 3] = r; Out[i * 3 + 1] = g; Out[i * 3 + 2] = b; }
        public void Set(int i, double[] c, double v = 1) { Out[i * 3] = c[0] * v; Out[i * 3 + 1] = c[1] * v; Out[i * 3 + 2] = c[2] * v; }
        public void Add(int i, double[] c, double v)
        {
            Out[i * 3] = Math.Min(255, Out[i * 3] + c[0] * v);
            Out[i * 3 + 1] = Math.Min(255, Out[i * 3 + 1] + c[1] * v);
            Out[i * 3 + 2] = Math.Min(255, Out[i * 3 + 2] + c[2] * v);
        }
        public void Fill(double[] c, double v = 1) { for (int i = 0; i < N; i++) Set(i, c, v); }
    }

    /// <summary>Lichteffekte, die ohne Bildschirmaufnahme laufen. Die LEDs zählen als Ring rund um den Monitor.</summary>
    public sealed class Effects
    {
        public static readonly string[] Categories = { "Ruhig", "Bewegung", "Natur", "Gaming", "Party" };
        public static readonly List<EffectDef> All = Build();
        private static readonly Dictionary<string, EffectDef> ById = Index();

        private static Dictionary<string, EffectDef> Index()
        {
            var d = new Dictionary<string, EffectDef>();
            foreach (EffectDef e in All) d[e.Id] = e;
            return d;
        }

        public static EffectDef? Find(string id) => ById.TryGetValue(id ?? "", out EffectDef? e) ? e : null;
        public static string TitleOf(string id) => Find(id)?.Title ?? id;

        // ------------------------------------------------------------------ Hilfsfunktionen
        private static readonly double[] White = { 255, 255, 255 };
        private const double Tau = Math.PI * 2;

        /// <summary>Wandelt "#RRGGBB" in R, G, B (0..255). Ungültige Angabe = Weiss.</summary>
        public static void ParseColor(string? hex, out double r, out double g, out double b)
        {
            r = g = b = 255;
            if (string.IsNullOrWhiteSpace(hex)) return;
            string h = hex.Trim().TrimStart('#');
            if (h.Length != 6) return;
            if (int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            {
                r = (v >> 16) & 0xFF; g = (v >> 8) & 0xFF; b = v & 0xFF;
            }
        }

        public static double[] Rgb(string hex)
        {
            ParseColor(hex, out double r, out double g, out double b);
            return new[] { r, g, b };
        }

        public static void Hsv(double h, double s, double v, out double r, out double g, out double b)
        {
            h -= Math.Floor(h);
            double x = h * 6;
            int i = (int)x;
            double f = x - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            r *= 255; g *= 255; b *= 255;
        }

        private static void SetHsv(FxContext c, int i, double h, double s, double v)
        {
            Hsv(h, s, v, out double r, out double g, out double b);
            c.Set(i, r, g, b);
        }

        private static double Frac(double x) => x - Math.Floor(x);
        private static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

        private static double Hash(int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (double)0xFFFFFF;
            }
        }

        /// <summary>Weiches Rauschen 0..1 in drei Richtungen.</summary>
        public static double Noise3(double x, double y, double z)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
            double fx = x - xi, fy = y - yi, fz = z - zi;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
            double L(double a, double b, double t) => a + (b - a) * t;
            double x00 = L(Hash(xi, yi, zi), Hash(xi + 1, yi, zi), fx);
            double x10 = L(Hash(xi, yi + 1, zi), Hash(xi + 1, yi + 1, zi), fx);
            double x01 = L(Hash(xi, yi, zi + 1), Hash(xi + 1, yi, zi + 1), fx);
            double x11 = L(Hash(xi, yi + 1, zi + 1), Hash(xi + 1, yi + 1, zi + 1), fx);
            return L(L(x00, x10, fy), L(x01, x11, fy), fz);
        }

        /// <summary>Rauschen entlang des LED-Rings, ohne Naht zwischen letzter und erster LED.</summary>
        public static double RingNoise(double pos, double scale, double time)
        {
            double a = pos * Tau;
            double v = Noise3(Math.Cos(a) * scale + 50, Math.Sin(a) * scale + 50, time) * 0.65
                     + Noise3(Math.Cos(a) * scale * 2.1 + 90, Math.Sin(a) * scale * 2.1 + 90, time * 1.7) * 0.35;
            return Clamp01((v - 0.22) / 0.56);
        }

        private static void Palette(double[][] stops, double x, out double r, out double g, out double b)
        {
            x = Clamp01(x) * (stops.Length - 1);
            int i = Math.Min((int)x, stops.Length - 2);
            double f = x - i;
            r = stops[i][0] + (stops[i + 1][0] - stops[i][0]) * f;
            g = stops[i][1] + (stops[i + 1][1] - stops[i][1]) * f;
            b = stops[i][2] + (stops[i + 1][2] - stops[i][2]) * f;
        }

        private static double[][] Stops(params string[] hex)
        {
            var s = new double[hex.Length][];
            for (int i = 0; i < hex.Length; i++) s[i] = Rgb(hex[i]);
            return s;
        }

        // ------------------------------------------------------------------ Bausteine
        /// <summary>Wolkige, langsam wandernde Farbflächen aus einer Farbpalette.</summary>
        private static void PaletteNoise(FxContext c, double[][] stops, double scale, double rate)
        {
            double sc = scale * (0.5 + c.Intensity);
            for (int i = 0; i < c.N; i++)
            {
                Palette(stops, RingNoise((double)i / c.N, sc, c.T * rate), out double r, out double g, out double b);
                c.Set(i, r, g, b);
            }
        }

        /// <summary>Kometen mit Schweif. dir: +1 im Uhrzeigersinn, -1 dagegen.</summary>
        private static void Comet(FxContext c, double headPos, int dir, double tail, double[] color)
        {
            double head = Frac(headPos) * c.N;
            for (int i = 0; i < c.N; i++)
            {
                double behind = dir > 0 ? head - i : i - head;
                if (behind < 0) behind += c.N;
                double v = Math.Exp(-behind / Math.Max(1, c.N * tail));
                if (v > 0.01) c.Add(i, color, v);
            }
        }

        /// <summary>Zufällig aufblitzende Punkte, die wieder verlöschen. A = Helligkeit, B = Farbe (gepackt).</summary>
        private static void Particles(FxContext c, double perLedPerSecond, double fadeSeconds, Func<FxContext, double[]> color, bool soft)
        {
            double expected = perLedPerSecond * c.N * c.Dt;
            int spawn = (int)expected + (c.Rng.NextDouble() < expected - (int)expected ? 1 : 0);
            for (int k = 0; k < spawn; k++)
            {
                int i = c.Rng.Next(c.N);
                if (c.A[i] > 0.3) continue;
                double[] col = color(c);
                c.A[i] = 1;
                c.B[i] = ((int)col[0] << 16) | ((int)col[1] << 8) | (int)col[2];
            }
            double fade = c.Dt / Math.Max(0.05, fadeSeconds);
            for (int i = 0; i < c.N; i++)
            {
                if (c.A[i] <= 0) continue;
                int p = (int)c.B[i];
                double v = soft ? Math.Sin(Math.PI * c.A[i]) : c.A[i] * c.A[i];
                c.Out[i * 3] = Math.Min(255, c.Out[i * 3] + ((p >> 16) & 255) * v);
                c.Out[i * 3 + 1] = Math.Min(255, c.Out[i * 3 + 1] + ((p >> 8) & 255) * v);
                c.Out[i * 3 + 2] = Math.Min(255, c.Out[i * 3 + 2] + (p & 255) * v);
                c.A[i] = Math.Max(0, c.A[i] - fade);
            }
        }

        private static double Gauss(double x, double centre, double width)
        {
            double d = (x - centre) / width;
            return Math.Exp(-d * d);
        }

        // ------------------------------------------------------------------ Die Effekte
        private static List<EffectDef> Build()
        {
            var list = new List<EffectDef>();
            void Add(string id, string title, string cat, int colors, string[]? defaults, Action<FxContext> render, int speed = 50, int intensity = 60)
            {
                list.Add(new EffectDef
                {
                    Id = id, Title = title, Category = cat, ColorCount = colors,
                    Colors = defaults ?? new[] { "#FF6A00", "#35D0F2", "#D04BFF" },
                    Speed = speed, Intensity = intensity, Render = render,
                });
            }

            // ---------------- Ruhig ----------------
            Add("cycle", "Farbwechsel", "Ruhig", 0, null, c =>
            {
                Hsv(c.T * 0.05, 1, 1, out double r, out double g, out double b);
                for (int i = 0; i < c.N; i++) c.Set(i, r, g, b);
            });
            Add("breathe", "Atmen", "Ruhig", 1, new[] { "#FF6A00", "#000000", "#000000" }, c =>
                c.Fill(c.C1, 0.04 + 0.96 * (0.5 - 0.5 * Math.Cos(c.T * Tau * 0.25))));
            Add("breathe2", "Atmen zweifarbig", "Ruhig", 2, new[] { "#35D0F2", "#D04BFF", "#000000" }, c =>
            {
                double x = 0.5 - 0.5 * Math.Cos(c.T * Tau * 0.12);
                for (int i = 0; i < c.N; i++)
                    c.Set(i, c.C1[0] + (c.C2[0] - c.C1[0]) * x, c.C1[1] + (c.C2[1] - c.C1[1]) * x, c.C1[2] + (c.C2[2] - c.C1[2]) * x);
            });
            Add("candle", "Kerze", "Ruhig", 1, new[] { "#FF9329", "#000000", "#000000" }, c =>
            {
                // gemeinsames Flackern (G[0]) plus leichte Unterschiede pro LED
                c.G[0] += (c.Rng.NextDouble() - 0.5) * c.Dt * 5;
                c.G[0] += (0.8 - c.G[0]) * Math.Min(1, c.Dt * 2);
                c.G[0] = Math.Clamp(c.G[0], 0.45, 1);
                for (int i = 0; i < c.N; i++)
                    c.Set(i, c.C1, c.G[0] * (0.85 + 0.15 * RingNoise((double)i / c.N, 2, c.T * 1.5)));
            });
            Add("gradient_static", "Verlauf (ruhend)", "Ruhig", 2, new[] { "#1E6BFF", "#D04BFF", "#000000" }, c =>
            {
                for (int i = 0; i < c.N; i++)
                {
                    double x = 0.5 - 0.5 * Math.Cos((double)i / c.N * Tau);      // hin und zurück, ohne Naht
                    c.Set(i, c.C1[0] + (c.C2[0] - c.C1[0]) * x, c.C1[1] + (c.C2[1] - c.C1[1]) * x, c.C1[2] + (c.C2[2] - c.C1[2]) * x);
                }
            });
            var sunset = Stops("#2B1055", "#D04BFF", "#FF3D6E", "#FF9A3D", "#F5E14B");
            Add("sunset", "Sonnenuntergang", "Ruhig", 0, null, c => PaletteNoise(c, sunset, 1.0, 0.12), 40);
            var ice = Stops("#061A40", "#1E6BFF", "#7FD4FF", "#FFFFFF");
            Add("ice", "Eis", "Ruhig", 0, null, c => PaletteNoise(c, ice, 1.4, 0.15), 40);
            Add("stars", "Sternenhimmel", "Ruhig", 1, new[] { "#DCE8FF", "#000000", "#000000" }, c =>
            {
                for (int i = 0; i < c.N; i++) c.Set(i, 0, 0, 10);
                Particles(c, 0.10 + 0.5 * c.Intensity, 2.5, x => x.C1, soft: true);
            }, 40, 40);
            Add("fireflies", "Glühwürmchen", "Ruhig", 1, new[] { "#C8FF3C", "#000000", "#000000" }, c =>
            {
                for (int i = 0; i < c.N; i++) c.Set(i, 0, 4, 0);
                Particles(c, 0.03 + 0.2 * c.Intensity, 3.5, x => x.C1, soft: true);
            }, 40, 40);

            // ---------------- Bewegung ----------------
            Add("rainbow", "Regenbogen", "Bewegung", 0, null, c =>
            {
                for (int i = 0; i < c.N; i++) SetHsv(c, i, (double)i / c.N + c.T * 0.10, 1, 1);
            });
            Add("pastel", "Regenbogen pastell", "Bewegung", 0, null, c =>
            {
                for (int i = 0; i < c.N; i++) SetHsv(c, i, (double)i / c.N + c.T * 0.06, 0.45, 1);
            });
            Add("gradient", "Verlauf (drehend)", "Bewegung", 3, new[] { "#FF3D6E", "#6C6BFF", "#35D0F2" }, c =>
            {
                double[][] stops = { c.C1, c.C2, c.C3, c.C1 };
                for (int i = 0; i < c.N; i++)
                {
                    Palette(stops, Frac((double)i / c.N + c.T * 0.05), out double r, out double g, out double b);
                    c.Set(i, r, g, b);
                }
            });
            Add("comet", "Lauflicht", "Bewegung", 1, new[] { "#FF6A00", "#000000", "#000000" }, c =>
            {
                c.Fill(White, 0);
                Comet(c, c.T * 0.25, 1, 0.04 + 0.16 * c.Intensity, c.C1);
            });
            Add("comet2", "Lauflicht gegenläufig", "Bewegung", 2, new[] { "#35D0F2", "#FF3D6E", "#000000" }, c =>
            {
                c.Fill(White, 0);
                Comet(c, c.T * 0.25, 1, 0.04 + 0.12 * c.Intensity, c.C1);
                Comet(c, 1 - Frac(c.T * 0.25), -1, 0.04 + 0.12 * c.Intensity, c.C2);
            });
            Add("comet_rainbow", "Regenbogen-Kometen", "Bewegung", 0, null, c =>
            {
                c.Fill(White, 0);
                for (int k = 0; k < 3; k++)
                {
                    Hsv(k / 3.0 + c.T * 0.03, 1, 1, out double r, out double g, out double b);
                    Comet(c, c.T * 0.2 + k / 3.0, 1, 0.03 + 0.1 * c.Intensity, new[] { r, g, b });
                }
            });
            Add("wave", "Welle", "Bewegung", 1, new[] { "#35D0F2", "#000000", "#000000" }, c =>
            {
                int waves = 1 + (int)Math.Round(c.Intensity * 5);
                for (int i = 0; i < c.N; i++)
                    c.Set(i, c.C1, 0.06 + 0.94 * (0.5 + 0.5 * Math.Sin(Tau * ((double)i / c.N * waves - c.T * 0.25))));
            });
            Add("dual_wave", "Welle zweifarbig", "Bewegung", 2, new[] { "#FF3D6E", "#35D0F2", "#000000" }, c =>
            {
                int waves = 1 + (int)Math.Round(c.Intensity * 5);
                for (int i = 0; i < c.N; i++)
                {
                    double x = 0.5 + 0.5 * Math.Sin(Tau * ((double)i / c.N * waves - c.T * 0.2));
                    c.Set(i, c.C1[0] + (c.C2[0] - c.C1[0]) * x, c.C1[1] + (c.C2[1] - c.C1[1]) * x, c.C1[2] + (c.C2[2] - c.C1[2]) * x);
                }
            });
            Add("chase", "Lichterkette", "Bewegung", 2, new[] { "#FFB14A", "#3A1E00", "#000000" }, c =>
            {
                int shift = (int)(c.T * 6);
                for (int i = 0; i < c.N; i++) c.Set(i, (i + shift) % 3 == 0 ? c.C1 : c.C2);
            });
            Add("blocks", "Farbblöcke", "Bewegung", 3, new[] { "#FF3D6E", "#F5E14B", "#35D0F2" }, c =>
            {
                double[][] cols = { c.C1, c.C2, c.C3 };
                int blocks = 3 * (1 + (int)Math.Round(c.Intensity * 3));
                for (int i = 0; i < c.N; i++)
                    c.Set(i, cols[(int)(Frac((double)i / c.N + c.T * 0.05) * blocks) % 3]);
            });
            Add("wipe", "Farbwischer", "Bewegung", 2, new[] { "#6C6BFF", "#3DE08A", "#000000" }, c =>
            {
                double p = Frac(c.T * 0.15) * 2;
                for (int i = 0; i < c.N; i++)
                {
                    double pos = (double)i / c.N;
                    bool first = p < 1 ? pos < p : pos >= p - 1;
                    c.Set(i, first ? c.C1 : c.C2);
                }
            });
            Add("pulse", "Pulswelle", "Bewegung", 1, new[] { "#D04BFF", "#000000", "#000000" }, c =>
            {
                for (int i = 0; i < c.N; i++)
                {
                    double pos = (double)i / c.N;
                    double d = Math.Min(pos, 1 - pos) * 2;                       // Abstand von LED 1, in beide Richtungen
                    double v = 0.5 + 0.5 * Math.Sin(Tau * (d * (1 + 3 * c.Intensity) - c.T * 0.4));
                    c.Set(i, c.C1, 0.04 + 0.96 * v * v);
                }
            });

            // ---------------- Natur ----------------
            Add("fire", "Feuer", "Natur", 0, null, c =>
            {
                for (int i = 0; i < c.N; i++)
                {
                    if (c.A[i] <= 0) c.A[i] = 0.7;
                    double h = c.A[i] + (c.Rng.NextDouble() - 0.5) * c.Dt * 7;
                    h += (c.A[(i + 1) % c.N] - h) * 0.08;                        // Nachbarn angleichen = Flammen statt Rauschen
                    h = Math.Clamp(h, 0.35, 1.0);
                    c.A[i] = h;
                    c.Set(i, 255 * h, 120 * h * h, 12 * h * h * h);
                }
            });
            var lava = Stops("#200000", "#8B0000", "#FF4500", "#FFB000");
            Add("lava", "Lava", "Natur", 0, null, c => PaletteNoise(c, lava, 1.3, 0.12), 40);
            var ocean = Stops("#001030", "#004080", "#00A0C0", "#80F0FF");
            Add("ocean", "Ozean", "Natur", 0, null, c => PaletteNoise(c, ocean, 1.2, 0.18));
            Add("aurora", "Polarlicht", "Natur", 0, null, c =>
            {
                for (int i = 0; i < c.N; i++)
                {
                    double pos = (double)i / c.N;
                    double hue = 0.45 + 0.18 * Math.Sin(pos * 2 * Tau + c.T * 0.30) + 0.10 * Math.Sin(pos * 3 * Tau - c.T * 0.17);
                    SetHsv(c, i, hue, 0.9, 0.6 + 0.4 * Math.Sin(pos * Tau + c.T * 0.21));
                }
            });
            var forest = Stops("#021A06", "#0B5A1E", "#3FAE3F", "#B6E36A");
            Add("forest", "Wald", "Natur", 0, null, c => PaletteNoise(c, forest, 1.5, 0.10), 40);
            Add("rain", "Regen", "Natur", 2, new[] { "#7FD4FF", "#06183A", "#000000" }, c =>
            {
                c.Fill(c.C2);
                Particles(c, 0.4 + 2.5 * c.Intensity, 0.6, x => x.C1, soft: false);
            });
            Add("lightning", "Gewitter", "Natur", 0, null, c =>
            {
                // G[0] = Helligkeit des Blitzes, G[1] = Ort, G[2] = Breite
                if (c.Rng.NextDouble() < c.Dt * (0.15 + 0.8 * c.Intensity))
                {
                    c.G[0] = 1; c.G[1] = c.Rng.NextDouble(); c.G[2] = 0.08 + c.Rng.NextDouble() * 0.3;
                }
                for (int i = 0; i < c.N; i++)
                {
                    double pos = (double)i / c.N;
                    double cloud = 0.3 + 0.7 * RingNoise(pos, 1.2, c.T * 0.2);
                    double d = Math.Abs(pos - c.G[1]); d = Math.Min(d, 1 - d);
                    double flash = c.G[0] * Gauss(d, 0, Math.Max(0.02, c.G[2]));
                    c.Set(i, Math.Min(255, 8 * cloud + 255 * flash), Math.Min(255, 12 * cloud + 255 * flash), Math.Min(255, 40 * cloud + 255 * flash));
                }
                c.G[0] = Math.Max(0, c.G[0] - c.RealDt * 5);
            });

            // ---------------- Gaming ----------------
            Add("scanner", "Scanner", "Gaming", 1, new[] { "#FF1F3D", "#000000", "#000000" }, c =>
            {
                double tri = Math.Abs(Frac(c.T * 0.3) * 2 - 1);                  // 0..1..0
                double head = tri * (c.N - 1);
                double width = Math.Max(1.5, c.N * (0.02 + 0.08 * c.Intensity));
                for (int i = 0; i < c.N; i++) c.Set(i, c.C1, Gauss(i, head, width));
            });
            Add("police", "Blaulicht", "Gaming", 2, new[] { "#FF1010", "#1040FF", "#000000" }, c =>
            {
                int f = (int)(c.T * 6) % 6;
                bool left = f == 0 || f == 2, right = f == 3 || f == 5;
                for (int i = 0; i < c.N; i++)
                {
                    bool firstHalf = i < c.N / 2;
                    if (firstHalf) c.Set(i, c.C1, left ? 1 : 0); else c.Set(i, c.C2, right ? 1 : 0);
                }
            });
            Add("heartbeat", "Herzschlag", "Gaming", 1, new[] { "#FF1030", "#000000", "#000000" }, c =>
            {
                double p = Frac(c.T * 0.55);
                c.Fill(c.C1, 0.05 + 0.95 * Math.Min(1, Gauss(p, 0.10, 0.06) + 0.7 * Gauss(p, 0.30, 0.07)));
            });
            Add("matrix", "Matrix", "Gaming", 1, new[] { "#20FF50", "#000000", "#000000" }, c =>
            {
                c.Fill(White, 0);
                double[] speeds = { 0.21, 0.34, 0.15, 0.27, 0.40 };
                for (int k = 0; k < speeds.Length; k++)
                    Comet(c, c.T * speeds[k] + k * 0.23, 1, 0.02 + 0.07 * c.Intensity, c.C1);
            });
            Add("meteors", "Meteorschauer", "Gaming", 1, new[] { "#BFD8FF", "#000000", "#000000" }, c =>
            {
                c.Fill(White, 0);
                double[] speeds = { 0.45, -0.3, 0.62 };
                for (int k = 0; k < speeds.Length; k++)
                    Comet(c, Frac(c.T * speeds[k] + k * 0.37), speeds[k] > 0 ? 1 : -1, 0.03 + 0.08 * c.Intensity, c.C1);
            });
            Add("plasma", "Plasma", "Gaming", 0, null, c =>
            {
                for (int i = 0; i < c.N; i++)
                {
                    double pos = (double)i / c.N;
                    double v = Math.Sin(pos * 2 * Tau + c.T * 0.6) + Math.Sin(pos * 3 * Tau - c.T * 0.37) + Math.Sin(c.T * 0.2);
                    SetHsv(c, i, v / 6 + 0.5, 1, 1);
                }
            });
            Add("synthwave", "Synthwave", "Gaming", 3, new[] { "#FF2E97", "#7A2CFF", "#19E6FF" }, c =>
            {
                double[][] stops = { c.C1, c.C2, c.C3, c.C2, c.C1 };
                for (int i = 0; i < c.N; i++)
                {
                    Palette(stops, Frac((double)i / c.N * 2 + c.T * 0.07), out double r, out double g, out double b);
                    c.Set(i, r, g, b);
                }
            });
            Add("cyberpunk", "Cyberpunk", "Gaming", 3, new[] { "#FCEE0A", "#00F0FF", "#FF003C" }, c =>
            {
                double[][] cols = { c.C1, c.C2, c.C3 };
                for (int i = 0; i < c.N; i++)
                {
                    double pos = (double)i / c.N;
                    int pick = (int)(RingNoise(pos, 1.6, c.T * 0.25) * 2.999);
                    // kurze schwarze Störungen
                    bool glitch = Noise3(pos * 40, Math.Floor(c.T * 8), 7) > 0.93 - 0.1 * c.Intensity;
                    c.Set(i, cols[pick], glitch ? 0 : 1);
                }
            });
            Add("neon", "Neonröhren", "Gaming", 2, new[] { "#FF2E97", "#19E6FF", "#000000" }, c =>
            {
                int segments = 4 + 2 * (int)Math.Round(c.Intensity * 4);
                for (int i = 0; i < c.N; i++)
                {
                    int seg = i * segments / c.N;
                    double v = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(c.T * 1.2 + seg * Math.PI));
                    c.Set(i, seg % 2 == 0 ? c.C1 : c.C2, v);
                }
            });

            // ---------------- Party ----------------
            Add("strobe", "Stroboskop", "Party", 1, new[] { "#FFFFFF", "#000000", "#000000" }, c =>
                c.Fill(c.C1, Frac(c.T * 2) < 0.15 ? 1 : 0));                     // höchstens 4 Blitze pro Sekunde
            Add("sparkle", "Funkeln", "Party", 1, new[] { "#6C6BFF", "#000000", "#000000" }, c =>
            {
                c.Fill(c.C1, 0.25);
                Particles(c, 0.5 + 3 * c.Intensity, 0.35, _ => White, soft: false);
            });
            Add("confetti", "Konfetti", "Party", 0, null, c =>
            {
                c.Fill(White, 0);
                Particles(c, 0.6 + 3 * c.Intensity, 0.8, x =>
                {
                    Hsv(x.Rng.NextDouble(), 1, 1, out double r, out double g, out double b);
                    return new[] { r, g, b };
                }, soft: false);
            });
            var halloween = Stops("#000000", "#3A0A5A", "#FF6A00", "#FF9A3D");
            Add("halloween", "Halloween", "Party", 0, null, c => PaletteNoise(c, halloween, 1.6, 0.35));
            Add("xmas", "Weihnachten", "Party", 2, new[] { "#FF1010", "#10C030", "#000000" }, c =>
            {
                for (int i = 0; i < c.N; i++) c.Set(i, (i / 3) % 2 == 0 ? c.C1 : c.C2, 0.45);
                Particles(c, 0.3 + 1.5 * c.Intensity, 0.9, _ => White, soft: true);
            });

            return list;
        }

        // ------------------------------------------------------------------ Zustand und Zeichnen
        private readonly FxContext _ctx = new FxContext();
        private string _lastId = "";
        private string _lastColors = "";

        /// <summary>Schreibt die Zielfarben (3 Werte pro LED, 0..255) für den gewählten Effekt.</summary>
        public void Render(string id, double[] target, int n, double dt, EffectParams p)
        {
            EffectDef def = Find(id) ?? All[0];
            FxContext c = _ctx;
            if (id != _lastId || c.N != n)
            {
                _lastId = id;
                c.N = n;
                c.A = new double[n];
                c.B = new double[n];
                Array.Clear(c.G, 0, c.G.Length);
                c.T = 0;
            }
            string colors = p.Color1 + p.Color2 + p.Color3;
            if (colors != _lastColors)
            {
                _lastColors = colors;
                c.C1 = Rgb(p.Color1); c.C2 = Rgb(p.Color2); c.C3 = Rgb(p.Color3);
            }
            double step = Math.Min(dt, 0.2);
            c.RealDt = step;
            c.Dt = step * Math.Clamp(p.Speed, 1, 100) / 50.0;
            c.T += c.Dt;
            if (c.T > 1e6) c.T = 0;
            c.Intensity = Math.Clamp(p.Intensity, 1, 100) / 100.0;
            c.Out = target;
            def.Render(c);
        }
    }
}
