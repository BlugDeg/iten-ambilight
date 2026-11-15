using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using Microsoft.Win32;

namespace ItenAmbilight
{
    /// <summary>Zustand, den die Firmware (ab v5.0) meldet.</summary>
    public sealed class DeviceTelemetry
    {
        public bool Valid;                 // false = noch keine Meldung erhalten (ältere Firmware)
        public bool On = true;             // am Gerät ein- oder ausgeschaltet (Taster)
        public int DimIndex;               // 0, 1, 2
        public int DimValue = 255;         // 255, 153, 64
        public double TempC;               // Chip-Temperatur
        public double PowerMw;             // geschätzte LED-Leistung
        public int Leds;
        public long UptimeSeconds;
        public DateTime Received;

        public DeviceTelemetry Copy() => (DeviceTelemetry)MemberwiseClone();
    }

    /// <summary>Sendet Bilder im Adalight-Format ans ESP32 (gleiches Protokoll wie HyperHDR).</summary>
    public sealed class AdalightDevice : IDisposable
    {
        private SerialPort? _port;
        private byte[] _buffer = Array.Empty<byte>();
        private DateTime _nextTry = DateTime.MinValue;
        private readonly System.Text.StringBuilder _rx = new System.Text.StringBuilder();

        /// <summary>Letzter gemeldeter Zustand des Geräts.</summary>
        public DeviceTelemetry Telemetry { get; } = new DeviceTelemetry();
        /// <summary>Tasterdrücke seit dem letzten Abholen: "S" = kurz, "L" = lang.</summary>
        public Queue<string> ButtonEvents { get; } = new Queue<string>();

        /// <summary>Schickt einen Befehl an die Firmware (ab v5.0), z. B. "T", "OT", "BN". Ältere Firmware ignoriert ihn.</summary>
        public bool SendCommand(string command)
        {
            if (!IsOpen) return false;
            try { _port!.Write("!" + command + "\n"); return true; }
            catch (Exception ex) { LastError = PortName + ": " + ex.Message; Close(); return false; }
        }

        /// <summary>Liest Meldungen der Firmware (Zeilen, die mit '#' beginnen). Muss regelmässig aufgerufen werden.</summary>
        public void ReadIncoming()
        {
            if (!IsOpen) return;
            try
            {
                if (_port!.BytesToRead <= 0) return;
                _rx.Append(_port.ReadExisting());
                if (_rx.Length > 4000) _rx.Remove(0, _rx.Length - 1000);
                while (true)
                {
                    string all = _rx.ToString();
                    int end = all.IndexOf('\n');
                    if (end < 0) break;
                    string line = all.Substring(0, end).Trim();
                    _rx.Remove(0, end + 1);
                    if (line.StartsWith("#", StringComparison.Ordinal)) ParseLine(line);
                }
            }
            catch { /* Lesefehler sind nicht schlimm; das Senden merkt, wenn die Verbindung weg ist */ }
        }

        private void ParseLine(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            if (parts[0] == "#EV" && parts.Length > 1) ButtonEvents.Enqueue(parts[1]);
            if (parts[0] != "#EV" && parts[0] != "#ST") return;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (string part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq), val = part.Substring(eq + 1);
                if (!double.TryParse(val, System.Globalization.NumberStyles.Float, inv, out double v)) continue;
                switch (key)
                {
                    case "on": Telemetry.On = v != 0; break;
                    case "bi": Telemetry.DimIndex = (int)v; break;
                    case "br": Telemetry.DimValue = (int)v; break;
                    case "t": Telemetry.TempC = v; break;
                    case "mw": Telemetry.PowerMw = v; break;
                    case "n": Telemetry.Leds = (int)v; break;
                    case "up": Telemetry.UptimeSeconds = (long)v; break;
                }
            }
            Telemetry.Valid = true;
            Telemetry.Received = DateTime.UtcNow;
        }

        /// <summary>Öffnet den Anschluss, falls nötig (höchstens alle 2 Sekunden ein Versuch).</summary>
        public bool Connect(string wantedPort) => EnsureOpen(wantedPort);

        public string PortName { get; private set; } = "";
        public bool IsOpen => _port != null && _port.IsOpen;
        public string LastError { get; private set; } = "";
        /// <summary>Kennung der Firmware (ab v4.0), z. B. "IT-EN Ambilight v4.0;max=300". Leer, wenn keine Antwort kam.</summary>
        public string FirmwareId { get; private set; } = "";

        /// <summary>Fragt die Firmware mit '?' nach ihrer Kennung. Ältere Firmware antwortet nicht – das ist kein Fehler.</summary>
        private void QueryFirmware()
        {
            FirmwareId = "";
            try
            {
                _port!.DiscardInBuffer();
                _port.Write("?");
                string text = "";
                var until = DateTime.UtcNow.AddMilliseconds(250);
                while (DateTime.UtcNow < until)
                {
                    text += _port.ReadExisting();
                    int a = text.IndexOf("IT-EN", StringComparison.Ordinal);
                    int e = a >= 0 ? text.IndexOf('\n', a) : -1;
                    if (e > a) { FirmwareId = text.Substring(a, e - a).Trim(); return; }
                    System.Threading.Thread.Sleep(10);
                }
            }
            catch { /* keine Antwort */ }
        }

        /// <summary>COM-Ports von ESP32-S3-Boards (USB-Serial/JTAG, VID 303A / PID 1001) aus der Registry.</summary>
        public static List<string> FindEspPorts()
        {
            var found = new List<string>();
            var existing = SerialPort.GetPortNames();
            try
            {
                using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usb == null) return found;
                foreach (var dev in usb.GetSubKeyNames())
                {
                    if (!dev.StartsWith("VID_303A&PID_1001", StringComparison.OrdinalIgnoreCase)) continue;
                    using var devKey = usb.OpenSubKey(dev);
                    if (devKey == null) continue;
                    foreach (var inst in devKey.GetSubKeyNames())
                    {
                        using var par = devKey.OpenSubKey(inst + @"\Device Parameters");
                        if (par?.GetValue("PortName") is string name &&
                            existing.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                            !found.Contains(name, StringComparer.OrdinalIgnoreCase))
                            found.Add(name);
                    }
                }
            }
            catch { /* kein Zugriff -> leere Liste */ }
            return found;
        }

        private bool EnsureOpen(string wanted)
        {
            if (IsOpen) return true;
            if (DateTime.UtcNow < _nextTry) return false;       // nicht bei jedem Bild neu versuchen
            _nextTry = DateTime.UtcNow.AddSeconds(2);

            string? name = wanted;
            if (string.IsNullOrWhiteSpace(wanted) || wanted.Equals("auto", StringComparison.OrdinalIgnoreCase))
                name = FindEspPorts().FirstOrDefault();
            if (string.IsNullOrEmpty(name)) { LastError = "Kein ESP32 gefunden"; return false; }

            try
            {
                Close();
                _port = new SerialPort(name, 2000000)
                {
                    WriteTimeout = 500,
                    DtrEnable = false,          // DTR/RTS würden das ESP32 zurücksetzen
                    RtsEnable = false,
                };
                _port.Open();
                PortName = name;
                LastError = "";
                QueryFirmware();
                return true;
            }
            catch (Exception ex)
            {
                LastError = name + ": " + ex.Message;
                Close();
                return false;
            }
        }

        /// <summary>Sendet ein Bild. rgb = 3 Byte pro LED (R, G, B).</summary>
        public bool Send(string wantedPort, byte[] rgb, int ledCount)
        {
            if (ledCount <= 0) return false;
            if (!EnsureOpen(wantedPort)) return false;

            int len = 6 + ledCount * 3;
            if (_buffer.Length != len) _buffer = new byte[len];
            int hi = ((ledCount - 1) >> 8) & 0xFF, lo = (ledCount - 1) & 0xFF;
            _buffer[0] = (byte)'A'; _buffer[1] = (byte)'d'; _buffer[2] = (byte)'a';
            _buffer[3] = (byte)hi; _buffer[4] = (byte)lo; _buffer[5] = (byte)(hi ^ lo ^ 0x55);
            Buffer.BlockCopy(rgb, 0, _buffer, 6, ledCount * 3);

            try
            {
                _port!.Write(_buffer, 0, len);
                return true;
            }
            catch (Exception ex)
            {
                LastError = PortName + ": " + ex.Message;
                Close();
                return false;
            }
        }

        public void Close()
        {
            Telemetry.Valid = false;
            _rx.Clear();
            try { _port?.Close(); } catch { }
            try { _port?.Dispose(); } catch { }
            _port = null;
        }

        public void Dispose() => Close();
    }
}
