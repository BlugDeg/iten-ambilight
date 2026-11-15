using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Diagnostics;

namespace ItenAmbilight
{
    /// <summary>
    /// Kalibrier-Assistent im Vollbild. Das Ambilight läuft dabei ganz normal mit: die LEDs zeigen,
    /// was sie aus dem Testbild machen. So wird genau das abgeglichen, was später auch gilt.
    /// </summary>
    public partial class CalibrationWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private const int StepCorners = 4, StepWhite = 5, StepGrey = 6, StepColors = 7, StepBlack = 8, StepDelay = 9, StepDone = 10;

        // Reihenfolge im Assistenten und Platz im Farbabgleich (0 Rot, 1 Gelb, 2 Grün, 3 Cyan, 4 Blau, 5 Magenta)
        private static readonly (string Name, string Hex, int Index)[] ColorSteps =
        {
            ("Rot", "#FF0000", 0), ("Grün", "#00FF00", 2), ("Blau", "#0000FF", 4),
            ("Gelb", "#FFFF00", 1), ("Cyan", "#00FFFF", 3), ("Magenta", "#FF00FF", 5),
        };

        private readonly App _app = (App)Application.Current;
        private readonly System.Windows.Forms.Screen _screen;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ColorProfile _profile;
        private int _step;
        private int _colorStep;
        private double _animStart;                      // Sekunden auf _clock
        private Ellipse? _ball;
        private readonly TranslateTransform _ballMove = new TranslateTransform();
        private Rectangle? _blinkBar;

        // --- Abschluss-Film ---
        private sealed class ShowBall
        {
            public Ellipse Shape = new Ellipse();
            public TranslateTransform Move = new TranslateTransform();
            public double X, Y, VX, VY, R;
            public double Born;
            public int Path = -1;                       // -1 = frei fliegend, sonst fester Weg (Szene 1)
        }
        private readonly List<ShowBall> _showBalls = new List<ShowBall>();
        private readonly Random _random = new Random();
        private bool _showRunning;
        private double _showStart, _lastFrame;
        private int _showLaunched;
        private bool _showSwarm;
        private Rectangle? _showWash;
        private RotateTransform? _showWashTurn;

        /// <summary>true = es wurde der HDR-Abgleich bearbeitet, sonst der für normales Bild.</summary>
        public bool EditedHdr { get; }

        public CalibrationWindow(System.Windows.Forms.Screen screen)
        {
            InitializeComponent();
            _screen = screen;

            AppSettings s = _app.Settings;
            s.Normalize();
            EditedHdr = _app.Engine.IsHdr;
            _profile = EditedHdr ? s.Hdr! : s.Sdr!;
            ProfileText.Text = EditedHdr ? "Abgleich für HDR" : "Abgleich für normales Bild";

            Left = screen.Bounds.X;
            Top = screen.Bounds.Y;
            Width = 800;
            Height = 600;

            CancelButton.Click += (_, _) => Close();
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
                else if (_showRunning && (e.Key == Key.Space || e.Key == Key.Enter)) EndShow();
            };
            MouseLeftButtonDown += (_, _) => { if (_showRunning) EndShow(); };
            SizeChanged += (_, _) => DrawStage();
            // Bewegung im Takt des Bildschirms (ein Schritt pro Bild) statt über einen ungenauen Zeitgeber
            CompositionTarget.Rendering += OnFrame;
            Closed += (_, _) => { CompositionTarget.Rendering -= OnFrame; _app.Engine.Calibration = 0; };

            ShowStep(0);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // genau über den gewählten Monitor legen (Angaben in echten Bildpunkten, unabhängig von der Skalierung)
            var b = _screen.Bounds;
            SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), b.X, b.Y, b.Width, b.Height, 0x0040);
        }

        private void Apply() => _app.ApplySettings(save: false);

        // ------------------------------------------------------------------ Schritte
        private void ShowStep(int step)
        {
            _step = Math.Clamp(step, 0, StepDone);
            _app.Engine.Calibration = _step == StepCorners ? 2 : 1;
            ControlsPanel.Children.Clear();
            ButtonsPanel.Children.Clear();
            HintText.Text = "";
            AppSettings s = _app.Settings;

            if (_step <= 3)
            {
                string[] side = { "oben", "rechts", "unten", "links" };
                string[] path = { "oben von links nach rechts", "rechts von oben nach unten", "unten von rechts nach links", "links von unten nach oben" };
                StepText.Text = "Lage · Seite " + (_step + 1) + " von 4: " + side[_step];
                TitleText.Text = "Ist das Licht hinter dem Monitor dem Ball gefolgt?";
                BodyText.Text = "Der Ball läuft " + path[_step] + " und bleibt am Ende stehen. "
                              + "Das rote Licht an der Wand sollte genau mitwandern und an derselben Stelle stehen bleiben.";
                AddStepper("LEDs " + side[_step], _step);
                AddButton("Ja, stimmt · weiter", true, () => ShowStep(_step + 1));
                AddButton("Nochmals zeigen", false, Replay);
                AddButton("Lief in die falsche Richtung", false, () => { s.Clockwise = !s.Clockwise; Apply(); Replay(); });
                AddButton("War an einer anderen Seite", false, () =>
                {
                    s.Start = (StartCorner)(((int)s.Start + 1) % 4);
                    Apply();
                    Replay();
                });
                if (_step > 0) AddButton("Zurück", false, () => ShowStep(_step - 1));
                HintText.Text = "«Andere Seite» dreht die Startecke um eine Ecke weiter; wiederhole es, bis das Licht auf der richtigen Seite läuft. "
                              + "Bleibt das Licht zu früh oder zu spät stehen, stimmt die LED-Anzahl dieser Seite nicht: mit − und + anpassen.";
                Replay();
            }
            else if (_step == StepCorners)
            {
                StepText.Text = "Ecken";
                TitleText.Text = "Sitzt das Licht genau in den vier Ecken?";
                BodyText.Text = "Jetzt leuchten nur die LEDs direkt neben den Ecken, je zwei pro Ecke. Auf dem Bildschirm sind die Ecken weiss markiert. "
                              + "Liegt ein Lichtpunkt neben statt in der Ecke, hat eine Seite zu viele oder zu wenige LEDs.";
                for (int i = 0; i < 4; i++) AddStepper("LEDs " + new[] { "oben", "rechts", "unten", "links" }[i], i);
                AddButton("Passt · weiter", true, () => ShowStep(_step + 1));
                AddButton("Zurück", false, () => ShowStep(_step - 1));
            }
            else if (_step == StepWhite)
            {
                StepText.Text = "Weiss";
                TitleText.Text = "Wirkt das Licht oben so weiss wie der Balken?";
                BodyText.Text = "LED-Streifen sind meist bläulich oder grünlich. Nimm die Farbe zurück, die zu stark ist, bis das Licht an der Wand neutral wirkt. "
                              + "Dieser Schritt kommt zuerst, weil er alle anderen Farben mitverschiebt.";
                AddSlider("Rot", 0, 100, _profile.GainRed, "0 '%'", v => _profile.GainRed = Math.Round(v));
                AddSlider("Grün", 0, 100, _profile.GainGreen, "0 '%'", v => _profile.GainGreen = Math.Round(v));
                AddSlider("Blau", 0, 100, _profile.GainBlue, "0 '%'", v => _profile.GainBlue = Math.Round(v));
                AddButton("Passt · weiter", true, () => ShowStep(_step + 1));
                AddButton("Zurück", false, () => ShowStep(_step - 1));
            }
            else if (_step == StepGrey)
            {
                StepText.Text = "Graustufen";
                TitleText.Text = "Sind die drei Felder oben gleich abgestuft wie auf dem Bildschirm?";
                BodyText.Text = "Links dunkel, in der Mitte mittel, rechts hell. Wirkt das Licht überall fast gleich hell und flau, stell Gamma höher. "
                              + "Verschwindet das dunkle Feld ganz, stell Gamma tiefer.";
                AddSlider("Gamma", 1, 3, _profile.Gamma, "0.00", v => _profile.Gamma = Math.Round(v, 2));
                AddButton("Passt · weiter", true, () => { _colorStep = 0; ShowStep(_step + 1); });
                AddButton("Zurück", false, () => ShowStep(_step - 1));
            }
            else if (_step == StepColors)
            {
                var c = ColorSteps[_colorStep];
                int k = c.Index;
                StepText.Text = "Farbe " + (_colorStep + 1) + " von " + ColorSteps.Length + ": " + c.Name;
                TitleText.Text = "Stimmt das Licht oben mit dem Balken überein?";
                BodyText.Text = "Schieb die Regler, bis das Licht an der Wand wie der Balken aussieht. Die Änderung siehst du sofort.";
                AddSlider("Farbton", -30, 30, _profile.HueShift[k], "0", v => _profile.HueShift[k] = Math.Round(v));
                AddSlider("Kräftigkeit", 50, 130, _profile.SatScale[k], "0 '%'", v => _profile.SatScale[k] = Math.Round(v));
                AddSlider("Helligkeit", 50, 130, _profile.ValScale[k], "0 '%'", v => _profile.ValScale[k] = Math.Round(v));
                AddButton(_colorStep < ColorSteps.Length - 1 ? "Passt · nächste Farbe" : "Passt · weiter", true, () =>
                {
                    if (_colorStep < ColorSteps.Length - 1) { _colorStep++; ShowStep(StepColors); }
                    else ShowStep(_step + 1);
                });
                AddButton("Zurück", false, () =>
                {
                    if (_colorStep > 0) { _colorStep--; ShowStep(StepColors); }
                    else ShowStep(_step - 1);
                });
                AddButton("Diese Farbe zurücksetzen", false, () =>
                {
                    _profile.HueShift[k] = 0; _profile.SatScale[k] = 100; _profile.ValScale[k] = 100;
                    Apply();
                    ShowStep(StepColors);
                });
                HintText.Text = "Reine Farben lassen sich nur blasser oder dunkler machen, nicht kräftiger: «Kräftigkeit» über 100 % wirkt erst bei Mischfarben.";
            }
            else if (_step == StepBlack)
            {
                StepText.Text = "Schwarz-Schwelle";
                TitleText.Text = "Glimmen die LEDs bei fast schwarzem Bild?";
                BodyText.Text = "Oben sind drei fast schwarze Felder. Leuchten die LEDs dort schwach oder farbig, erhöhe die Schwelle, bis sie aus bleiben. "
                              + "Zu hoch eingestellt, gehen auch dunkle Filmszenen verloren.";
                AddSlider("Schwelle", 0, 40, _profile.BlackLevel, "0", v => _profile.BlackLevel = Math.Round(v));
                AddButton("Passt · weiter", true, () => ShowStep(_step + 1));
                AddButton("Zurück", false, () => { _colorStep = ColorSteps.Length - 1; ShowStep(_step - 1); });
            }
            else if (_step == StepDelay)
            {
                StepText.Text = "Verzögerung";
                TitleText.Text = "Folgt das Licht dem Blinken ohne spürbare Verspätung?";
                BodyText.Text = "Der Balken oben blinkt. Zieht das Licht deutlich nach, stell die Glättung tiefer. Flackert das Licht in Filmen und Spielen zu nervös, stell sie höher.";
                AddSlider("Glättung", 0, 95, s.Smoothing, "0", v => s.Smoothing = Math.Round(v));
                AddButton("Passt · weiter", true, () => ShowStep(_step + 1));
                AddButton("Zurück", false, () => ShowStep(_step - 1));
            }
            else
            {
                StepText.Text = "Fertig";
                TitleText.Text = "Glückwunsch, dein Ambilight ist abgeglichen.";
                BodyText.Text = "Die Werte sind als " + (EditedHdr ? "HDR-Abgleich" : "Abgleich für normales Bild") + " gespeichert. "
                              + (EditedHdr ? "Für normales Bild (HDR aus) gilt ein eigener Abgleich: Assistent bei ausgeschaltetem HDR nochmals starten."
                                           : "Für HDR gilt ein eigener Abgleich: HDR einschalten und den Assistenten nochmals starten.");
                AddButton("Fertig", true, Close);
                AddButton("Film nochmals ansehen", false, StartShow);
                AddButton("Zurück", false, () => ShowStep(_step - 1));
                if (!_showRunning && !_showSeen) { _showSeen = true; StartShow(); }
            }

            DrawSteps();
            DrawStage();
        }

        private bool _showSeen;

        private void Replay()
        {
            _animStart = _clock.Elapsed.TotalSeconds;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (_showRunning) ShowFrame(); else Animate();
        }

        // ------------------------------------------------------------------ Abschluss-Film
        private static Color HueColor(double hue)
        {
            Effects.Hsv(hue, 1, 1, out double r, out double g, out double b);
            return Color.FromRgb((byte)r, (byte)g, (byte)b);
        }

        /// <summary>Leuchtende Kugel mit weichem Rand.</summary>
        private ShowBall NewBall(Color color, double radius)
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 255), 0));
            brush.GradientStops.Add(new GradientStop(color, 0.25));
            brush.GradientStops.Add(new GradientStop(color, 0.6));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            brush.Freeze();
            var ball = new ShowBall { R = radius };
            ball.Shape = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = brush, RenderTransform = ball.Move, IsHitTestVisible = false };
            Stage.Children.Add(ball.Shape);
            _showBalls.Add(ball);
            return ball;
        }

        /// <summary>Kurzer Film zum Abschluss: farbige Kugeln fliegen durchs Bild, damit man das Ambilight erlebt.</summary>
        private void StartShow()
        {
            if (ActualWidth < 100 || ActualHeight < 100) return;
            _app.Engine.Calibration = 1;
            _showRunning = true;
            _showStart = _lastFrame = _clock.Elapsed.TotalSeconds;
            _showLaunched = 0;
            _showSwarm = false;
            _showBalls.Clear();
            _ball = null;
            _blinkBar = null;
            Stage.Children.Clear();

            // Farbflut für die letzte Szene (liegt zuunterst, anfangs unsichtbar)
            _showWashTurn = new RotateTransform(0, 0.5, 0.5);
            var wash = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), RelativeTransform = _showWashTurn };
            string[] colors = { "#FF3D6E", "#FF9A3D", "#F5E14B", "#3DE08A", "#35D0F2", "#6C6BFF", "#D04BFF", "#FF3D6E" };
            for (int i = 0; i < colors.Length; i++)
                wash.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]), (double)i / (colors.Length - 1)));
            _showWash = new Rectangle { Width = ActualWidth, Height = ActualHeight, Fill = wash, Opacity = 0, IsHitTestVisible = false };
            Stage.Children.Add(_showWash);

            QuestionPanel.Visibility = Visibility.Collapsed;
            StepsPanel.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            ShowHint.Visibility = Visibility.Visible;
        }

        private void EndShow()
        {
            _showRunning = false;
            _showBalls.Clear();
            _showWash = null;
            Stage.Children.Clear();
            QuestionPanel.Visibility = Visibility.Visible;
            StepsPanel.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Visible;
            ShowHint.Visibility = Visibility.Collapsed;
        }

        private void ShowFrame()
        {
            double w = ActualWidth, h = ActualHeight;
            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.05, now - _lastFrame);
            _lastFrame = now;
            double t = now - _showStart;

            // --- Szene 1 (0 bis 5.5 s): fünf grosse Kugeln ziehen nacheinander an den Rändern entlang ---
            double[] hues = { 0.0, 0.14, 0.36, 0.52, 0.83 };
            while (_showLaunched < 5 && t >= _showLaunched * 0.85)
            {
                ShowBall b = NewBall(HueColor(hues[_showLaunched]), h * 0.16);
                b.Path = _showLaunched;
                b.Born = t;
                _showLaunched++;
            }

            // --- Szene 2 (5.5 bis 12.5 s): ein Schwarm bunter Kugeln prallt durchs Bild ---
            if (!_showSwarm && t >= 5.5)
            {
                _showSwarm = true;
                for (int i = 0; i < 12; i++)
                {
                    ShowBall b = NewBall(HueColor(i / 12.0), h * (0.08 + 0.07 * _random.NextDouble()));
                    b.X = w * (0.1 + 0.8 * _random.NextDouble());
                    b.Y = h * (0.1 + 0.8 * _random.NextDouble());
                    double angle = _random.NextDouble() * Math.PI * 2, speed = w * (0.28 + 0.3 * _random.NextDouble());
                    b.VX = Math.Cos(angle) * speed;
                    b.VY = Math.Sin(angle) * speed;
                    b.Born = t;
                }
            }

            for (int i = _showBalls.Count - 1; i >= 0; i--)
            {
                ShowBall b = _showBalls[i];
                double age = t - b.Born;
                double opacity = 1;
                if (b.Path >= 0)
                {
                    double p = age / 2.6;
                    if (p >= 1) { Stage.Children.Remove(b.Shape); _showBalls.RemoveAt(i); continue; }
                    double m = h * 0.08, s = p * p * (3 - 2 * p);          // weich anfahren und abbremsen
                    double x0, y0, x1, y1;
                    switch (b.Path)
                    {
                        case 0: x0 = -b.R; y0 = m; x1 = w + b.R; y1 = m; break;                 // oben entlang
                        case 1: x0 = w - m; y0 = -b.R; x1 = w - m; y1 = h + b.R; break;         // rechts hinunter
                        case 2: x0 = w + b.R; y0 = h - m; x1 = -b.R; y1 = h - m; break;         // unten zurück
                        case 3: x0 = m; y0 = h + b.R; x1 = m; y1 = -b.R; break;                 // links hinauf
                        default: x0 = -b.R; y0 = h + b.R; x1 = w + b.R; y1 = -b.R; break;       // quer durchs Bild
                    }
                    b.X = x0 + (x1 - x0) * s;
                    b.Y = y0 + (y1 - y0) * s;
                }
                else
                {
                    b.X += b.VX * dt;
                    b.Y += b.VY * dt;
                    if (b.X < 0 && b.VX < 0 || b.X > w && b.VX > 0) b.VX = -b.VX;               // bis ganz an den Rand, dann zurück
                    if (b.Y < 0 && b.VY < 0 || b.Y > h && b.VY > 0) b.VY = -b.VY;
                    opacity = Math.Clamp(age / 0.8, 0, 1) * Math.Clamp((12.5 - t) / 1.0, 0, 1);
                    if (t >= 12.5) { Stage.Children.Remove(b.Shape); _showBalls.RemoveAt(i); continue; }
                }
                b.Shape.Opacity = opacity;
                b.Move.X = b.X - b.R;
                b.Move.Y = b.Y - b.R;
            }

            // --- Szene 3 (12 bis 16.5 s): drehende Farbflut über das ganze Bild, dann ausblenden ---
            if (_showWash != null && _showWashTurn != null)
            {
                double p = (t - 12.0) / 4.5;
                _showWash.Opacity = p <= 0 || p >= 1 ? 0 : Math.Sin(Math.PI * p);
                _showWashTurn.Angle = (t - 12.0) * 70;
            }

            if (t >= 16.5) EndShow();
        }

        // ------------------------------------------------------------------ Bausteine für Regler und Knöpfe
        private void AddButton(string text, bool accent, Action action)
        {
            var b = new Button { Content = text, MinHeight = 44, Margin = new Thickness(0, 0, 10, 10) };
            if (accent) b.Style = (Style)FindResource("AccentButton");
            b.Click += (_, _) => action();
            ButtonsPanel.Children.Add(b);
        }

        private void AddSlider(string label, double min, double max, double value, string format, Action<double> set)
        {
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };
            var shown = new TextBlock
            {
                Text = slider.Value.ToString(format, CultureInfo.InvariantCulture),
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, FontFamily = new FontFamily("Consolas"),
            };
            slider.ValueChanged += (_, _) =>
            {
                set(slider.Value);
                shown.Text = slider.Value.ToString(format, CultureInfo.InvariantCulture);
                Apply();
            };
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(shown, 2);
            grid.Children.Add(name);
            grid.Children.Add(slider);
            grid.Children.Add(shown);
            ControlsPanel.Children.Add(grid);
        }

        /// <summary>LED-Anzahl einer Seite mit − und + (0 oben, 1 rechts, 2 unten, 3 links).</summary>
        private void AddStepper(string label, int side)
        {
            AppSettings s = _app.Settings;
            int Get() => side == 0 ? s.LedsTop : side == 1 ? s.LedsRight : side == 2 ? s.LedsBottom : s.LedsLeft;
            void Set(int v)
            {
                v = Math.Clamp(v, 0, 300);
                if (side == 0) s.LedsTop = v; else if (side == 1) s.LedsRight = v; else if (side == 2) s.LedsBottom = v; else s.LedsLeft = v;
                s.AutoLedCount = false;                     // von Hand angepasst -> nicht mehr automatisch berechnen
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var name = new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock
            {
                Text = Get().ToString(), Width = 60, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Consolas"), FontSize = 16,
            };
            var minus = new Button { Content = "−", Width = 44, MinHeight = 40 };
            var plus = new Button { Content = "+", Width = 44, MinHeight = 40 };
            void Change(int delta)
            {
                if (s.TotalLeds + delta > 300 || s.TotalLeds + delta < 1) return;
                Set(Get() + delta);
                count.Text = Get().ToString();
                Apply();
                if (_step <= 3) Replay();
            }
            minus.Click += (_, _) => Change(-1);
            plus.Click += (_, _) => Change(+1);
            row.Children.Add(name);
            row.Children.Add(minus);
            row.Children.Add(count);
            row.Children.Add(plus);
            ControlsPanel.Children.Add(row);
        }

        private void DrawSteps()
        {
            string[] names = { "Lage", "Ecken", "Weiss", "Graustufen", "Farben", "Schwarz", "Verzögerung" };
            int current = _step <= 3 ? 0 : Math.Min(_step - 3, names.Length);      // Fertig = hinter dem letzten
            StepsPanel.Children.Clear();
            for (int i = 0; i < names.Length; i++)
            {
                bool now = i == current;
                var chip = new Border
                {
                    Height = 32, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(4, 0, 4, 0),
                    Background = now ? (Brush)FindResource("TextBrush") : new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1D)),
                    BorderBrush = (Brush)FindResource("Line2Brush"), BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = names[i] + (i < current ? "  ✓" : ""), VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
                        Foreground = now ? (Brush)FindResource("DarkTextBrush") : (Brush)FindResource("TextDimBrush"),
                    },
                };
                StepsPanel.Children.Add(chip);
            }
        }

        // ------------------------------------------------------------------ Testbild
        private static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private void AddRect(double x, double y, double w, double h, Brush fill)
        {
            var r = new Rectangle { Width = Math.Max(1, w), Height = Math.Max(1, h), Fill = fill };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            Stage.Children.Add(r);
        }

        private void DrawStage()
        {
            double w = ActualWidth, h = ActualHeight;
            if (_showRunning) return;                        // der Film zeichnet selbst
            Stage.Children.Clear();
            _ball = null;
            _blinkBar = null;
            if (w < 100 || h < 100) return;
            double bar = h * 0.16;                           // deutlich höher als die Messtiefe der LEDs

            if (_step <= 3)
            {
                double d = h * 0.11;
                _ball = new Ellipse { Width = d, Height = d, Fill = Solid("#FF1F3D"), RenderTransform = _ballMove };
                Stage.Children.Add(_ball);
                Animate();
            }
            else if (_step == StepCorners)
            {
                double c = h * 0.09;
                AddRect(0, 0, c, c, Brushes.White);
                AddRect(w - c, 0, c, c, Brushes.White);
                AddRect(0, h - c, c, c, Brushes.White);
                AddRect(w - c, h - c, c, c, Brushes.White);
            }
            else if (_step == StepWhite)
            {
                AddRect(0, 0, w, bar, Brushes.White);
            }
            else if (_step == StepGrey)
            {
                AddRect(0, 0, w / 3, bar, Solid("#404040"));
                AddRect(w / 3, 0, w / 3, bar, Solid("#808080"));
                AddRect(2 * w / 3, 0, w / 3 + 1, bar, Solid("#BFBFBF"));
            }
            else if (_step == StepColors)
            {
                AddRect(0, 0, w, bar, Solid(ColorSteps[_colorStep].Hex));
            }
            else if (_step == StepBlack)
            {
                AddRect(0, 0, w / 3, bar, Solid("#050505"));
                AddRect(w / 3, 0, w / 3, bar, Solid("#0D0D0D"));
                AddRect(2 * w / 3, 0, w / 3 + 1, bar, Solid("#1A1A1A"));
            }
            else if (_step == StepDelay)
            {
                _blinkBar = new Rectangle { Width = w, Height = bar, Fill = Brushes.White };
                Stage.Children.Add(_blinkBar);
            }
        }

        /// <summary>Bewegt den Ball (3 Sekunden Fahrt, dann Stillstand) und lässt den Balken blinken.</summary>
        private void Animate()
        {
            double w = ActualWidth, h = ActualHeight;
            double seconds = _clock.Elapsed.TotalSeconds - _animStart;

            if (_ball != null)
            {
                double t = Math.Clamp((seconds - 0.4) / 3.0, 0, 1);      // kurze Pause am Start
                t = t * t * (3 - 2 * t);                                 // weich anfahren und abbremsen
                double m = h * 0.06;                                     // Abstand der Ballmitte vom Rand
                double x0, y0, x1, y1;
                switch (_step)
                {
                    case 0: x0 = m; y0 = m; x1 = w - m; y1 = m; break;                    // oben: links -> rechts
                    case 1: x0 = w - m; y0 = m; x1 = w - m; y1 = h - m; break;            // rechts: oben -> unten
                    case 2: x0 = w - m; y0 = h - m; x1 = m; y1 = h - m; break;            // unten: rechts -> links
                    default: x0 = m; y0 = h - m; x1 = m; y1 = m; break;                   // links: unten -> oben
                }
                // nur verschieben (kein neues Layout pro Bild) -> läuft flüssig
                _ballMove.X = x0 + (x1 - x0) * t - _ball.Width / 2;
                _ballMove.Y = y0 + (y1 - y0) * t - _ball.Height / 2;
            }
            if (_blinkBar != null)
                _blinkBar.Visibility = ((int)(seconds / 0.7) % 2 == 0) ? Visibility.Visible : Visibility.Hidden;
        }
    }
}
