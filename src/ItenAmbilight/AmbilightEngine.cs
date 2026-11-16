using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ItenAmbilight
{
    /// <summary>Eintrag im Verlauf (Seite Gerät).</summary>
    public sealed class LogEntry
    {
        public DateTime Time { get; set; }
        public string Source { get; set; } = "";       // Taster | Programm | Gerät | Regel
        public string Text { get; set; } = "";
        public string TimeText => Time.ToString("HH:mm:ss");
    }

    /// <summary>Hintergrund-Schleife: Bildschirm aufnehmen -> LED-Farben berechnen -> glätten -> senden.</summary>
    public sealed class AmbilightEngine : IDisposable
    {
        private readonly object _lock = new object();
        private AppSettings _settings;
        private Thread? _thread;
        private volatile bool _run;
        private byte[] _lastColors = Array.Empty<byte>();

        public AmbilightEngine(AppSettings settings) { _settings = settings.Clone(); }

        // --- Status für die Oberfläche ---
        public double Fps { get; private set; }
        public string Status { get; private set; } = "gestoppt";
        public string PortName { get; private set; } = "";
        public string CaptureName { get; private set; } = "";
        public bool IsHdr { get; private set; }
        /// <summary>true = das letzte Bild konnte ans Gerät gesendet werden.</summary>
        public bool DeviceOk { get; private set; }
        /// <summary>Kennung der Firmware, leer bei älterer Firmware.</summary>
        public string FirmwareId { get; private set; } = "";
        /// <summary>Programm im Vordergrund, für das gerade eine Regel gilt (leer = keine Regel aktiv).</summary>
        public string ActiveRule { get; private set; } = "";

        /// <summary>Fehlermeldung des laufenden eigenen Effekts (Lua); leer = kein Fehler.</summary>
        public string ScriptError { get; private set; } = "";

        /// <summary>
        /// Kalibrier-Assistent: 0 = aus, 1 = Ambilight erzwingen (ohne Regeln, ohne Balken-Erkennung),
        /// 2 = nur die Eck-LEDs leuchten weiss.
        /// </summary>
        public volatile int Calibration;

        /// <summary>
        /// Zustand des PCs für die Szenen: 0 = normal, 1 = gesperrt, 2 = Ruhezustand oder Bildschirm aus.
        /// Wird vom Programm (App) gesetzt.
        /// </summary>
        public volatile int SystemState;

        private volatile bool _holdRequest, _held;
        private readonly ManualResetEventSlim _holdDone = new ManualResetEventSlim(false);

        /// <summary>
        /// Der PC geht gleich schlafen: Szene «Ruhezustand» einmal senden und dem Gerät sagen, dass es dieses Bild
        /// halten soll (Firmware ab v5.1). Wartet höchstens kurz, damit Windows nicht aufgehalten wird.
        /// </summary>
        public void PrepareSleep()
        {
            if (_thread == null) return;
            _holdDone.Reset();
            _holdRequest = true;
            _holdDone.Wait(800);
        }

        /// <summary>Der PC ist wieder wach: normal weitermachen.</summary>
        public void Resume()
        {
            _holdRequest = false;
            _held = false;
        }

        // --- Meldungen und Befehle der Firmware (ab v5.0) ---
        private readonly ConcurrentQueue<string> _commands = new ConcurrentQueue<string>();
        private readonly List<LogEntry> _log = new List<LogEntry>();
        private DeviceTelemetry _telemetry = new DeviceTelemetry();

        /// <summary>Letzter vom Gerät gemeldeter Zustand (Kopie).</summary>
        public DeviceTelemetry Telemetry { get { lock (_lock) return _telemetry.Copy(); } }

        /// <summary>Befehl ans Gerät: "OT" = Ein/Aus umschalten, "BN" = nächste Dimmstufe.</summary>
        public void SendDeviceCommand(string command, string description)
        {
            _commands.Enqueue(command);
            AddLog("Programm", description);
        }

        public void AddLog(string source, string text)
        {
            lock (_log)
            {
                _log.Insert(0, new LogEntry { Time = DateTime.Now, Source = source, Text = text });
                if (_log.Count > 200) _log.RemoveRange(200, _log.Count - 200);
            }
        }

        /// <summary>Verlauf, neuester Eintrag zuerst (Kopie).</summary>
        public List<LogEntry> GetLog() { lock (_log) return new List<LogEntry>(_log); }

        /// <summary>Farbkorrektur für die sechs Grundfarben (Rot, Gelb, Grün, Cyan, Blau, Magenta), Werte 0..255.</summary>
        public static void HueCorrect(ColorProfile p, ref double r, ref double g, ref double b)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max <= 0.5) return;
            double s = (max - min) / max;
            if (s < 0.02) return;                               // Grautöne bleiben unberührt
            double d = max - min, h;
            if (max == r) h = ((g - b) / d + 6) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;

            int k = (int)h % 6, k2 = (k + 1) % 6;
            double f = h - (int)h;
            double shift = p.HueShift[k] + (p.HueShift[k2] - p.HueShift[k]) * f;
            double ks = (p.SatScale[k] + (p.SatScale[k2] - p.SatScale[k]) * f) / 100.0;
            double kv = (p.ValScale[k] + (p.ValScale[k2] - p.ValScale[k]) * f) / 100.0;
            if (shift == 0 && ks == 1 && kv == 1) return;

            h = (h + shift / 60.0 * s + 6) % 6;                 // je kräftiger die Farbe, desto stärker die Korrektur
            s = Math.Clamp(s * ks, 0, 1);
            double v = Math.Clamp(max * (1 + (kv - 1) * s), 0, 255);

            int i = (int)h;
            double ff = h - i, pp = v * (1 - s), q = v * (1 - s * ff), tt = v * (1 - s * (1 - ff));
            switch (i % 6)
            {
                case 0: r = v; g = tt; b = pp; break;
                case 1: r = q; g = v; b = pp; break;
                case 2: r = pp; g = v; b = tt; break;
                case 3: r = pp; g = q; b = v; break;
                case 4: r = tt; g = pp; b = v; break;
                default: r = v; g = pp; b = q; break;
            }
        }

        /// <summary>Testfarbe für den Weissabgleich: 0 = aus, 1 = Weiss, 2 = Rot, 3 = Grün, 4 = Blau. Wird nicht gespeichert.</summary>
        public volatile int TestColor;

        private double _effLeft, _effRight, _effTop, _effBottom;

        /// <summary>Tatsächlich verwendeter Beschnitt (eingestellter Rand oder erkannter Balken), Anteile 0..1.</summary>
        public void GetEffectiveCrop(out double left, out double right, out double top, out double bottom)
        {
            lock (_lock) { left = _effLeft; right = _effRight; top = _effTop; bottom = _effBottom; }
        }

        private byte[] _lastFrame = Array.Empty<byte>();
        private int _lastFrameW, _lastFrameH;

        /// <summary>Kopie des zuletzt aufgenommenen (verkleinerten) Bildes, BGRA.</summary>
        public byte[] GetLastFrame(out int width, out int height)
        {
            lock (_lock)
            {
                width = _lastFrameW; height = _lastFrameH;
                return (byte[])_lastFrame.Clone();
            }
        }

        private static IScreenSource CreateSource(System.Windows.Forms.Screen screen, string method, bool allowHdr, int width, out string note)
        {
            note = "";
            if (!string.Equals(method, "gdi", StringComparison.OrdinalIgnoreCase))
            {
                try { return new DxgiCapture(screen.DeviceName, allowHdr, width); }
                catch (Exception ex) { note = "Desktop Duplication nicht möglich (" + ex.Message + ")"; }
            }
            var b = screen.Bounds;
            int h = Math.Max(1, (int)Math.Round((double)width * b.Height / Math.Max(1, b.Width)));
            return new GdiCapture(b.X, b.Y, b.Width, b.Height, width, h);
        }

        /// <summary>Kopie der zuletzt gesendeten Farben (3 Byte pro LED), für die Vorschau.</summary>
        public byte[] GetLastColors() { lock (_lock) return (byte[])_lastColors.Clone(); }

        /// <summary>Neue Einstellungen übernehmen (wirkt sofort, ohne Neustart).</summary>
        public void Apply(AppSettings settings) { lock (_lock) _settings = settings.Clone(); }

        public void Start()
        {
            if (_thread != null) return;
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Ambilight", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
            _thread?.Join(2000);
            _thread = null;
        }

        public void Dispose() => Stop();

        private static System.Windows.Forms.Screen FindScreen(string deviceName)
        {
            var all = System.Windows.Forms.Screen.AllScreens;
            return all.FirstOrDefault(s => s.DeviceName == deviceName)
                   ?? System.Windows.Forms.Screen.PrimaryScreen
                   ?? all[0];
        }

        private static string ModeTitle(string mode, string effect)
        {
            switch (mode)
            {
                case "effect": return "Effekt " + Effects.TitleOf(effect);
                case "color": return "feste Farbe";
                case "off": return "aus";
                default: return "Ambilight";
            }
        }

        // Windows-Zeitgeber: ohne das dauert Thread.Sleep(1) bis zu 15.6 ms -> die Bildrate halbiert sich bei ruhigem Bild
        [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

        private void Loop()
        {
            try { timeBeginPeriod(1); } catch { }
            List<LedRegion> layout = new List<LedRegion>();
            string layoutKey = "";
            IScreenSource? capture = null;
            string captureKey = "";
            string captureNote = "";
            int captureFails = 0;
            bool forceGdi = false;
            using var device = new AdalightDevice();
            var sw = Stopwatch.StartNew();
            double[] smooth = Array.Empty<double>();
            double[] target = Array.Empty<double>();
            byte[] outBuf = Array.Empty<byte>();
            long lastTicks = sw.ElapsedTicks;
            int frames = 0;
            long fpsStart = sw.ElapsedMilliseconds;
            bool sentBlack = false;
            var blackbar = new BlackbarDetector();
            var effects = new Effects();
            var lua = new LuaEffect();
            long lastPoll = -10000;
            bool wasConnected = false;
            string lastRuleName = "";
            AddLog("Programm", "Gestartet");
            long lastRuleCheck = -10000;
            AppRule? rule = null;
            ScheduleEntry? plan = null;
            string lastOverlay = "";
            int heldLoops = 0;

            while (_run)
            {
                long frameStart = sw.ElapsedMilliseconds;
                AppSettings s;
                lock (_lock) s = _settings;

                // ---------- PC schläft: nichts senden, sonst würde das gehaltene Bild überschrieben ----------
                if (_held)
                {
                    // Sicherheit: läuft das Programm 60 s weiter, ohne dass Windows "wieder wach" meldet, normal weitermachen.
                    // (Im echten Ruhezustand steht dieser Thread still, der Zähler läuft also nicht.)
                    if (++heldLoops > 1200) { _held = false; heldLoops = 0; continue; }
                    Status = "Ruhezustand";
                    Fps = 0;
                    lastTicks = sw.ElapsedTicks;
                    Thread.Sleep(50);
                    continue;
                }

                heldLoops = 0;

                int n = s.TotalLeds;
                if (n <= 0 || n > 300) { Status = "LED-Anzahl muss zwischen 1 und 300 liegen"; Thread.Sleep(200); continue; }
                if (smooth.Length != n * 3) { smooth = new double[n * 3]; target = new double[n * 3]; outBuf = new byte[n * 3]; }

                // ---------- Gerät: verbinden, Befehle senden, Meldungen lesen ----------
                int calibration = Calibration;
                device.Connect(s.Port);
                if (device.IsOpen != wasConnected)
                {
                    wasConnected = device.IsOpen;
                    AddLog("Gerät", wasConnected
                        ? "Verbunden an " + device.PortName + (device.FirmwareId.Length > 0 ? " · " + device.FirmwareId : "")
                        : "Verbindung getrennt");
                }
                if (frameStart - lastPoll >= 2000) { lastPoll = frameStart; _commands.Enqueue("T"); }
                while (_commands.TryDequeue(out string? command)) device.SendCommand(command);
                device.ReadIncoming();
                while (device.ButtonEvents.Count > 0)
                {
                    string ev = device.ButtonEvents.Dequeue();
                    DeviceTelemetry dt2 = device.Telemetry;
                    AddLog("Taster", ev == "L"
                        ? "Lang gedrückt · Dimmstufe " + (dt2.DimIndex + 1) + " von 3 (" + (int)Math.Round(dt2.DimValue / 2.55) + " %)"
                        : "Kurz gedrückt · LEDs " + (dt2.On ? "ein" : "aus"));
                }
                lock (_lock) _telemetry = device.Telemetry.Copy();

                // ---------- Regel pro Programm: gilt, solange das Programm im Vordergrund ist ----------
                if (frameStart - lastRuleCheck >= 400)
                {
                    lastRuleCheck = frameStart;
                    rule = null;
                    if (s.Rules.Count > 0)
                    {
                        string fg = ForegroundApp.ProcessName();
                        if (fg.Length > 0)
                            rule = s.Rules.FirstOrDefault(r => string.Equals(ForegroundApp.Normalize(r.Process), fg,
                                                                              StringComparison.OrdinalIgnoreCase));
                    }
                    ActiveRule = rule != null ? ForegroundApp.Normalize(rule.Process) : "";
                    plan = s.ScheduleEnabled ? Automation.FindSchedule(s.Schedule, DateTime.Now) : null;
                    if (ActiveRule != lastRuleName)
                    {
                        lastRuleName = ActiveRule;
                        AddLog("Regel", rule != null ? ActiveRule + " im Vordergrund · " + ModeTitle(rule.Mode, s.EffectTitle(rule.Effect))
                                                     : "Keine Regel mehr aktiv");
                    }
                }
                if (calibration != 0) rule = null;              // Assistent: keine Regeln
                string mode = rule != null ? rule.Mode : s.Mode;
                string effect = rule != null ? rule.Effect : s.Effect;
                string staticColor = rule != null ? rule.Color : s.StaticColor;
                if (calibration != 0) mode = "ambilight";       // Assistent: immer der Bildschirm
                string ruleNote = rule != null ? "  –  Regel «" + ActiveRule + "»: " + ModeTitle(mode, s.EffectTitle(effect)) : "";

                // ---------- Szene (gesperrt / Ruhezustand) und Zeitplan gehen vor ----------
                // Reihenfolge: Assistent > Szene > Zeitplan > Regel > Betriebsart. Ist das Programm ausgeschaltet, bleibt es aus.
                bool holdNow = _holdRequest;
                int system = calibration != 0 ? 0 : holdNow ? 2 : SystemState;
                Scene? scene = system == 2 ? s.SceneSleep : system == 1 ? s.SceneLock : null;
                int brightness = s.Brightness;
                string overlay = "";
                if (scene != null)
                {
                    mode = scene.Mode == "color" || scene.Mode == "effect" ? scene.Mode : "off";
                    effect = scene.Effect;
                    staticColor = scene.Color;
                    brightness = scene.Brightness;
                    overlay = "Szene «" + (system == 2 ? "Ruhezustand" : "Gesperrt") + "»: " + ModeTitle(mode, s.EffectTitle(effect));
                }
                else if (plan != null && calibration == 0)
                {
                    if (plan.Action == "brightness") brightness = plan.Brightness; else mode = "off";
                    overlay = "Zeitplan " + plan.From + "–" + plan.To + ": "
                              + (plan.Action == "brightness" ? "Helligkeit " + Math.Clamp(plan.Brightness, 0, 100) + " %" : "aus");
                }
                if (overlay.Length > 0) ruleNote = (scene != null ? "" : ruleNote) + "  –  " + overlay;
                if (overlay != lastOverlay)
                {
                    lastOverlay = overlay;
                    AddLog("Automatik", overlay.Length > 0 ? overlay : "Wieder normale Betriebsart");
                }

                // ---------- Ausgeschaltet: einmal schwarz senden, dann warten ----------
                if ((!s.Enabled && calibration == 0) || mode == "off")
                {
                    if (capture != null) { capture.Dispose(); capture = null; captureKey = ""; }
                    if (!sentBlack)
                    {
                        Array.Clear(outBuf, 0, outBuf.Length);
                        Array.Clear(smooth, 0, smooth.Length);
                        sentBlack = device.Send(s.Port, outBuf, n);
                        DeviceOk = sentBlack;
                        PortName = device.PortName;
                        FirmwareId = device.FirmwareId;
                        lock (_lock) _lastColors = (byte[])outBuf.Clone();
                    }
                    if (holdNow) { _holdRequest = false; _held = true; _holdDone.Set(); }   // schlafen mit dunklen LEDs
                    Status = "aus" + (s.Enabled ? ruleNote : "");
                    Fps = 0;
                    lastTicks = sw.ElapsedTicks;
                    Thread.Sleep(100);
                    continue;
                }
                sentBlack = false;

                var screen = FindScreen(s.MonitorDeviceName);
                var b = screen.Bounds;
                int frameMs = 1000 / Math.Clamp(s.TargetFps, 5, 240);

                long nowTicks = sw.ElapsedTicks;
                double dt = (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
                lastTicks = nowTicks;

                bool ambilight = mode != "effect" && mode != "color";
                string sourceName;

                if (ambilight)
                {
                    // ---------- Aufnehmen ----------
                    int capWidth = s.CaptureWidth <= 160 ? 160 : s.CaptureWidth >= 640 ? 640 : 320;
                    string key = screen.DeviceName + "|" + s.CaptureMethod + "|" + b.Width + "x" + b.Height + "|" + s.HdrCapture + "|" + capWidth;
                    if (key != captureKey) { capture?.Dispose(); capture = null; captureKey = key; captureFails = 0; forceGdi = false; }
                    if (capture == null)
                    {
                        capture = CreateSource(screen, forceGdi ? "gdi" : s.CaptureMethod, s.HdrCapture, capWidth, out captureNote);
                        CaptureName = capture.Name;
                    }
                    if (capture is DxgiCapture dx) dx.PaperWhiteNits = s.HdrPaperWhite;

                    if (!capture.Capture(frameMs))
                    {
                        // z. B. Auflösungswechsel, Vollbild-Umschaltung, Sperrbildschirm -> Quelle neu aufbauen
                        capture.Dispose();
                        capture = null;
                        captureFails++;
                        if (captureFails >= 5 && !string.Equals(s.CaptureMethod, "dxgi", StringComparison.OrdinalIgnoreCase))
                            forceGdi = true;
                        Status = "Bildschirmaufnahme wird neu gestartet …";
                        Thread.Sleep(250);
                        continue;
                    }
                    captureFails = 0;
                    IsHdr = capture.IsHdr;
                    sourceName = capture.Name;
                    lock (_lock)
                    {
                        if (_lastFrame.Length != capture.Pixels.Length) _lastFrame = new byte[capture.Pixels.Length];
                        Buffer.BlockCopy(capture.Pixels, 0, _lastFrame, 0, _lastFrame.Length);
                        _lastFrameW = capture.Width; _lastFrameH = capture.Height;
                    }

                    // ---------- LED-Farben aus dem Bild ----------
                    // Messbereiche nur neu berechnen, wenn sich die Anordnung ändert (nicht bei jedem Bild)
                    double aspect = (double)b.Width / Math.Max(1, b.Height);
                    string wantedLayout = s.LedsTop + "|" + s.LedsRight + "|" + s.LedsBottom + "|" + s.LedsLeft + "|" + s.Start + "|" + s.Clockwise + "|" + s.SampleDepth + "|" + aspect;
                    if (wantedLayout != layoutKey) { layout = LedLayout.Build(s, aspect); layoutKey = wantedLayout; }
                    List<LedRegion> regions = layout;

                    int W = capture.Width, H = capture.Height;
                    double cl = Math.Clamp(s.CropLeft, 0, 45) / 100.0, cr = Math.Clamp(s.CropRight, 0, 45) / 100.0;
                    double ct = Math.Clamp(s.CropTop, 0, 45) / 100.0, cb = Math.Clamp(s.CropBottom, 0, 45) / 100.0;

                    // Schwarze Balken: der grössere Wert aus eingestelltem Rand und erkanntem Balken gilt
                    if (s.BlackbarEnabled && calibration == 0)
                    {
                        blackbar.Update(capture.Pixels, W, H, Math.Clamp(s.BlackbarThreshold, 0, 100), dt);
                        cl = Math.Max(cl, blackbar.Horizontal); cr = Math.Max(cr, blackbar.Horizontal);
                        ct = Math.Max(ct, blackbar.Vertical); cb = Math.Max(cb, blackbar.Vertical);
                    }
                    else blackbar.Reset();
                    lock (_lock) { _effLeft = cl; _effRight = cr; _effTop = ct; _effBottom = cb; }

                    double ax = cl * W, ay = ct * H;                      // Ursprung des beschnittenen Bildes
                    double aw = (1 - cl - cr) * W, ah = (1 - ct - cb) * H;

                    byte[] px = capture.Pixels;
                    for (int i = 0; i < n; i++)
                    {
                        LedRegion r = regions[i];
                        int x0 = (int)(ax + r.X0 * aw), x1 = (int)Math.Ceiling(ax + r.X1 * aw);
                        int y0 = (int)(ay + r.Y0 * ah), y1 = (int)Math.Ceiling(ay + r.Y1 * ah);
                        x0 = Math.Clamp(x0, 0, W - 1); x1 = Math.Clamp(x1, x0 + 1, W);
                        y0 = Math.Clamp(y0, 0, H - 1); y1 = Math.Clamp(y1, y0 + 1, H);

                        long sr = 0, sg = 0, sb = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            int o = (y * W + x0) * 4;
                            for (int x = x0; x < x1; x++, o += 4)
                            {
                                sb += px[o]; sg += px[o + 1]; sr += px[o + 2];
                            }
                        }
                        int cnt = (x1 - x0) * (y1 - y0);
                        target[i * 3] = (double)sr / cnt;
                        target[i * 3 + 1] = (double)sg / cnt;
                        target[i * 3 + 2] = (double)sb / cnt;
                    }
                }
                else
                {
                    // ---------- Effekt oder feste Farbe: keine Aufnahme nötig ----------
                    if (capture != null) { capture.Dispose(); capture = null; captureKey = ""; }
                    IsHdr = false;
                    blackbar.Reset();
                    lock (_lock)
                    {
                        _effLeft = _effRight = _effTop = _effBottom = 0;
                        Array.Clear(_lastFrame, 0, _lastFrame.Length);          // Vorschau: schwarzes Bild
                    }

                    if (mode == "color")
                    {
                        Effects.ParseColor(staticColor, out double fr, out double fg2, out double fb);
                        for (int i = 0; i < n; i++) { target[i * 3] = fr; target[i * 3 + 1] = fg2; target[i * 3 + 2] = fb; }
                        sourceName = "feste Farbe";
                    }
                    else
                    {
                        CustomEffect? custom = s.FindCustom(effect);
                        if (custom != null)
                        {
                            lua.Render(custom.Script, target, n, dt, s.GetEffectParams(effect));
                            ScriptError = lua.Error;
                        }
                        else
                        {
                            effects.Render(effect, target, n, dt, s.GetEffectParams(effect));
                            ScriptError = "";
                        }
                        sourceName = "Effekt " + s.EffectTitle(effect);
                    }
                }

                // ---------- Assistent: nur die Eck-LEDs ----------
                if (calibration == 2)
                {
                    List<LedRegion> ring = LedLayout.Build(s, (double)b.Width / Math.Max(1, b.Height));
                    for (int i = 0; i < n; i++)
                    {
                        int side = ring[i].Side;
                        bool corner = ring[(i + 1) % n].Side != side || ring[(i + n - 1) % n].Side != side;
                        target[i * 3] = target[i * 3 + 1] = target[i * 3 + 2] = corner ? 255 : 0;
                    }
                }

                // ---------- Glätten und kalibrieren ----------
                // Glättung: 0 = sofort, 95 = sehr träge (Zeitkonstante bis ca. 0.5 s). Effekte: kurz und fest.
                double tau = ambilight ? Math.Clamp(s.Smoothing, 0, 95) / 95.0 * 0.5 : 0.06;
                if (calibration == 2 || holdNow) tau = 0;       // vor dem Ruhezustand: sofort das fertige Bild
                double alpha = tau <= 0.001 ? 1.0 : 1.0 - Math.Exp(-dt / tau);
                double bright = Math.Clamp(brightness, 0, 100) / 100.0;

                // Farbabgleich: eigener Satz für HDR, sonst der für normales Bild
                ColorProfile prof = (ambilight && IsHdr ? s.Hdr : s.Sdr) ?? new ColorProfile();
                double sat = ambilight ? Math.Clamp(prof.Saturation, 0, 200) / 100.0 : 1.0;
                double gamma = Math.Clamp(prof.Gamma, 0.5, 4.0);
                double gainR = Math.Clamp(prof.GainRed, 0, 100) / 100.0 * bright;
                double gainG = Math.Clamp(prof.GainGreen, 0, 100) / 100.0 * bright;
                double gainB = Math.Clamp(prof.GainBlue, 0, 100) / 100.0 * bright;
                double black = ambilight ? Math.Clamp(prof.BlackLevel, 0, 60) : 0;
                int test = TestColor;
                bool raw = test != 0 || calibration == 2;        // feste Prüffarbe: ohne Sättigung, Farbkorrektur und Gamma

                for (int i = 0; i < n; i++)
                {
                    int k = i * 3;
                    double tr = target[k], tg = target[k + 1], tb = target[k + 2];
                    if (test != 0 && calibration != 2)
                    {
                        tr = test == 1 || test == 2 ? 255 : 0;
                        tg = test == 1 || test == 3 ? 255 : 0;
                        tb = test == 1 || test == 4 ? 255 : 0;
                    }
                    smooth[k] += (tr - smooth[k]) * alpha;
                    smooth[k + 1] += (tg - smooth[k + 1]) * alpha;
                    smooth[k + 2] += (tb - smooth[k + 2]) * alpha;

                    // Sättigung -> Farbkorrektur -> Schwarz-Schwelle -> Gamma -> Weissabgleich und Helligkeit
                    double cr2 = smooth[k], cg2 = smooth[k + 1], cb2 = smooth[k + 2];
                    if (!raw)
                    {
                        double luma = 0.2126 * cr2 + 0.7152 * cg2 + 0.0722 * cb2;
                        cr2 = Math.Clamp(luma + (cr2 - luma) * sat, 0, 255);
                        cg2 = Math.Clamp(luma + (cg2 - luma) * sat, 0, 255);
                        cb2 = Math.Clamp(luma + (cb2 - luma) * sat, 0, 255);
                        if (ambilight) HueCorrect(prof, ref cr2, ref cg2, ref cb2);
                        if (black > 0)
                        {
                            double top = Math.Max(cr2, Math.Max(cg2, cb2));
                            double keep = Math.Clamp((top - black) / (black * 0.5 + 1), 0, 1);   // weicher Übergang statt hartem Sprung
                            cr2 *= keep; cg2 *= keep; cb2 *= keep;
                        }
                        cr2 = 255.0 * Math.Pow(cr2 / 255.0, gamma);
                        cg2 = 255.0 * Math.Pow(cg2 / 255.0, gamma);
                        cb2 = 255.0 * Math.Pow(cb2 / 255.0, gamma);
                    }
                    outBuf[k] = (byte)Math.Clamp(cr2 * gainR + 0.5, 0, 255);
                    outBuf[k + 1] = (byte)Math.Clamp(cg2 * gainG + 0.5, 0, 255);
                    outBuf[k + 2] = (byte)Math.Clamp(cb2 * gainB + 0.5, 0, 255);
                }

                // ---------- Senden ----------
                bool ok = device.Send(s.Port, outBuf, n);
                if (holdNow)
                {
                    // Bild halten, bis wieder Daten kommen (Firmware ab v5.1; ältere blendet nach 5 s aus)
                    if (ok) device.SendCommand("H");
                    _holdRequest = false; _held = true; _holdDone.Set();
                }
                DeviceOk = ok;
                PortName = device.PortName;
                FirmwareId = device.FirmwareId;
                Status = ok ? "läuft (" + device.PortName + ", " + sourceName + ")" : "kein Gerät: " + device.LastError;
                if (ok && ambilight && captureNote.Length > 0) Status += "  –  " + captureNote;
                if (ok) Status += ruleNote;
                lock (_lock)
                {
                    if (_lastColors.Length != outBuf.Length) _lastColors = new byte[outBuf.Length];
                    Buffer.BlockCopy(outBuf, 0, _lastColors, 0, outBuf.Length);
                }

                // ---------- FPS + Takt ----------
                frames++;
                long nowMs = sw.ElapsedMilliseconds;
                if (nowMs - fpsStart >= 1000)
                {
                    Fps = frames * 1000.0 / (nowMs - fpsStart);
                    frames = 0;
                    fpsStart = nowMs;
                }
                // Rest des Bildtakts abwarten. Die Aufnahme hat bei ruhigem Bild schon gewartet -> dann bleibt hier nichts übrig.
                int wait = frameMs - (int)(sw.ElapsedMilliseconds - frameStart);
                if (wait > 1) Thread.Sleep(wait);
            }

            capture?.Dispose();
            try { timeEndPeriod(1); } catch { }

            // Beim Beenden LEDs ausschalten
            try
            {
                AppSettings s;
                lock (_lock) s = _settings;
                if (s.TotalLeds > 0) device.Send(s.Port, new byte[s.TotalLeds * 3], s.TotalLeds);
            }
            catch { }
            Status = "gestoppt";
        }
    }
}
