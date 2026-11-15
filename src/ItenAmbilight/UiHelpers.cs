using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ItenAmbilight
{
    /// <summary>Misst, wie viel Prozessor, Grafikkarte und Arbeitsspeicher dieses Programm selbst braucht.</summary>
    public sealed class UsageMonitor : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new Dictionary<string, PerformanceCounter>();
        private readonly System.Threading.Timer _timer;
        private TimeSpan _lastCpu;
        private DateTime _lastTime;
        private int _busy;
        private bool _gpuBroken;
        private int _tick;

        /// <summary>
        /// Nur messen, solange das Fenster sichtbar ist. Die Abfrage der Grafikkarten-Zähler liest jedes Mal
        /// die Daten aller Programme und belegt dabei viel Arbeitsspeicher – im Infobereich wäre das Verschwendung.
        /// </summary>
        public volatile bool Active;

        public double CpuPercent { get; private set; }
        public double RamMb { get; private set; }
        /// <summary>null = nicht verfügbar</summary>
        public double? GpuPercent { get; private set; }

        public UsageMonitor()
        {
            _timer = new System.Threading.Timer(_ => Sample(), null, 500, 2000);
        }

        private void Sample()
        {
            if (!Active) return;
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                _process.Refresh();
                DateTime now = DateTime.UtcNow;
                TimeSpan cpu = _process.TotalProcessorTime;
                if (_lastTime != default)
                {
                    double wall = (now - _lastTime).TotalMilliseconds * Environment.ProcessorCount;
                    if (wall > 0) CpuPercent = Math.Clamp((cpu - _lastCpu).TotalMilliseconds / wall * 100.0, 0, 100);
                }
                _lastCpu = cpu;
                _lastTime = now;
                RamMb = _process.WorkingSet64 / 1048576.0;
                if (_tick++ % 3 == 0) GpuPercent = ReadGpu();      // Grafikkarte nur alle 6 Sekunden (teure Abfrage)
            }
            catch { }
            finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }
        }

        /// <summary>Summe der Windows-Zähler "GPU Engine" für diesen Prozess (wie im Task-Manager).</summary>
        private double? ReadGpu()
        {
            if (_gpuBroken) return null;
            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                string marker = "pid_" + _process.Id + "_";
                var names = category.GetInstanceNames().Where(n => n.Contains(marker, StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (string gone in _gpuCounters.Keys.Where(k => !names.Contains(k)).ToList())
                {
                    _gpuCounters[gone].Dispose();
                    _gpuCounters.Remove(gone);
                }
                double sum = 0;
                foreach (string name in names)
                {
                    if (!_gpuCounters.TryGetValue(name, out PerformanceCounter? counter))
                    {
                        counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true);
                        _gpuCounters[name] = counter;
                        counter.NextValue();                    // erster Wert ist immer 0
                        continue;
                    }
                    try { sum += counter.NextValue(); } catch { }
                }
                return Math.Clamp(sum, 0, 100);
            }
            catch
            {
                _gpuBroken = true;                              // Zähler gibt es auf diesem System nicht
                return null;
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            foreach (var c in _gpuCounters.Values) c.Dispose();
            _gpuCounters.Clear();
        }
    }

    /// <summary>Blendet auf jedem Monitor kurz seine Nummer ein.</summary>
    public static class MonitorIdentifier
    {
        public static void Show()
        {
            int number = 1;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var b = screen.Bounds;
                int size = Math.Max(160, Math.Min(b.Width, b.Height) / 4);
                var form = new System.Windows.Forms.Form
                {
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    ShowInTaskbar = false,
                    TopMost = true,
                    BackColor = System.Drawing.Color.FromArgb(11, 13, 18),
                    Bounds = new System.Drawing.Rectangle(b.X + (b.Width - size) / 2, b.Y + (b.Height - size) / 2, size, size),
                };
                var label = new System.Windows.Forms.Label
                {
                    Text = number.ToString(),
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                    ForeColor = System.Drawing.Color.FromArgb(53, 208, 242),
                    Font = new System.Drawing.Font("Segoe UI", size * 0.5f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel),
                };
                form.Controls.Add(label);

                var timer = new System.Windows.Forms.Timer { Interval = 2500 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    form.Close();
                    form.Dispose();
                };
                form.Show();
                timer.Start();
                number++;
            }
        }
    }

    public static class UiNative
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Dunkle Titelleiste (Windows 10 ab 2004 und Windows 11). Auf älteren Systemen ohne Wirkung.</summary>
        public static void UseDarkTitleBar(Window window)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                int on = 1;
                DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
            }
            catch { }
        }

        /// <summary>Symbol einer .exe als Bild für die Oberfläche; null, wenn die Datei nicht lesbar ist.</summary>
        public static ImageSource? IconFor(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch { return null; }
        }
    }

    /// <summary>Eintrag in der Effekt-Übersicht.</summary>
    public sealed class EffectItem
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        public Brush? Brush { get; set; }
    }

    /// <summary>Zeile in der Regel-Liste.</summary>
    public sealed class RuleRow
    {
        public ImageSource? Icon { get; set; }
        public string Title { get; set; } = "";
        public string Action { get; set; } = "";
    }
}
