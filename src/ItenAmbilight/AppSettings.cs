using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ItenAmbilight
{
    public enum StartCorner { BottomLeft, TopLeft, TopRight, BottomRight }

    /// <summary>Regel für ein bestimmtes Programm: gilt, solange dessen Fenster im Vordergrund ist.</summary>
    public class AppRule
    {
        public string Process { get; set; } = "";               // Prozessname ohne .exe, z. B. "vlc"
        public string Path { get; set; } = "";                  // voller Pfad der .exe (nur für das Symbol)
        public string Mode { get; set; } = "off";               // ambilight | effect | color | off
        public string Effect { get; set; } = "rainbow";
        public string Color { get; set; } = "#FFFFFF";
    }

    /// <summary>Eigener Effekt als Lua-Skript. Als Effekt-Kennung gilt "custom:" + Id.</summary>
    public class CustomEffect
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "Eigener Effekt";
        public string Script { get; set; } = "";
    }

    /// <summary>
    /// Farbabgleich. Es gibt zwei davon: einen für normales Bild (SDR) und einen für HDR.
    /// Reihenfolge der sechs Farben: Rot, Gelb, Grün, Cyan, Blau, Magenta.
    /// </summary>
    public class ColorProfile
    {
        public double Saturation { get; set; } = 100;            // Prozent, 100 = unverändert
        public double Gamma { get; set; } = 2.0;                 // 1.0 = unverändert
        public double GainRed { get; set; } = 100;               // Weissabgleich, Prozent
        public double GainGreen { get; set; } = 100;
        public double GainBlue { get; set; } = 100;
        public double[] HueShift { get; set; } = new double[6];                              // Grad, -30..30
        public double[] SatScale { get; set; } = { 100, 100, 100, 100, 100, 100 };           // Prozent
        public double[] ValScale { get; set; } = { 100, 100, 100, 100, 100, 100 };           // Prozent
        public double BlackLevel { get; set; } = 0;              // 0..40: darunter bleiben die LEDs aus

        public void Normalize()
        {
            if (HueShift == null || HueShift.Length != 6) HueShift = new double[6];
            if (SatScale == null || SatScale.Length != 6) SatScale = new double[] { 100, 100, 100, 100, 100, 100 };
            if (ValScale == null || ValScale.Length != 6) ValScale = new double[] { 100, 100, 100, 100, 100, 100 };
        }
    }

    /// <summary>Alle Einstellungen. Werden als JSON in %AppData%\ItenAmbilight\settings.json gespeichert.</summary>
    public class AppSettings
    {
        // --- Monitor ---
        public string MonitorDeviceName { get; set; } = "";      // leer = Hauptmonitor
        public double DiagonalInch { get; set; } = 26.5;
        public double LedsPerMeter { get; set; } = 60;

        // --- LED-Layout ---
        public bool AutoLedCount { get; set; } = false;          // true = aus Bildschirmgrösse berechnen
        public int LedsTop { get; set; } = 36;
        public int LedsBottom { get; set; } = 36;
        public int LedsLeft { get; set; } = 20;
        public int LedsRight { get; set; } = 20;
        public StartCorner Start { get; set; } = StartCorner.BottomLeft;
        public bool Clockwise { get; set; } = true;              // von VORNE auf den Bildschirm gesehen

        // --- Bildbereich (Prozent) ---
        public double CropLeft { get; set; } = 0;
        public double CropRight { get; set; } = 0;
        public double CropTop { get; set; } = 0;
        public double CropBottom { get; set; } = 0;
        public double SampleDepth { get; set; } = 8;             // wie tief ins Bild gemessen wird

        // --- Modus ---
        public string Mode { get; set; } = "ambilight";         // ambilight | effect | color
        public string Effect { get; set; } = "rainbow";         // siehe Effects.Names
        public int EffectSpeed { get; set; } = 50;              // 1..100
        public string EffectColor { get; set; } = "#FF6A00";    // Farbe für Atmen / Lauflicht
        public string StaticColor { get; set; } = "#FFFFFF";    // feste Farbe
        public List<AppRule> Rules { get; set; } = new List<AppRule>();
        /// <summary>Regler pro Effekt (Geschwindigkeit, Stärke, Farben). Fehlt ein Eintrag, gelten die Standardwerte des Effekts.</summary>
        public Dictionary<string, EffectParams> EffectSettings { get; set; } = new Dictionary<string, EffectParams>();
        public List<CustomEffect> CustomEffects { get; set; } = new List<CustomEffect>();

        // --- Tastenkürzel: Aktion (siehe AppCommands.HotkeyActions) -> Tasten, z. B. "Ctrl+Alt+F9". Leer = nicht belegt. ---
        public Dictionary<string, string> Hotkeys { get; set; } = new Dictionary<string, string>();

        // --- Zeitplan (standardmässig aus) ---
        public bool ScheduleEnabled { get; set; } = false;
        public List<ScheduleEntry> Schedule { get; set; } = new List<ScheduleEntry>();

        // --- Szenen: PC gesperrt / Ruhezustand oder Bildschirm aus (Standard: LEDs aus) ---
        public Scene SceneLock { get; set; } = new Scene();
        public Scene SceneSleep { get; set; } = new Scene();

        // --- Farbabgleich: getrennt für SDR und HDR ---
        public ColorProfile? Sdr { get; set; }
        public ColorProfile? Hdr { get; set; }

        public EffectParams GetEffectParams(string id)
        {
            if (EffectSettings.TryGetValue(id ?? "", out EffectParams? p) && p != null) return p;
            return Effects.Find(id ?? "")?.Defaults() ?? new EffectParams();
        }

        public CustomEffect? FindCustom(string effectId)
        {
            if (effectId == null || !effectId.StartsWith("custom:", StringComparison.Ordinal)) return null;
            string id = effectId.Substring(7);
            return CustomEffects.Find(c => c.Id == id);
        }

        public string EffectTitle(string id) => FindCustom(id)?.Name ?? Effects.TitleOf(id);

        /// <summary>Ergänzt fehlende Teile (z. B. nach dem Laden einer älteren Einstellungsdatei).</summary>
        public void Normalize()
        {
            if (Sdr == null)
            {
                // ältere Datei: die bisherigen Werte als SDR-Abgleich übernehmen
                Sdr = new ColorProfile { Saturation = Saturation, Gamma = Gamma, GainRed = GainRed, GainGreen = GainGreen, GainBlue = GainBlue };
            }
            Hdr ??= JsonSerializer.Deserialize<ColorProfile>(JsonSerializer.Serialize(Sdr))!;
            Sdr.Normalize();
            Hdr.Normalize();
            Rules ??= new List<AppRule>();
            EffectSettings ??= new Dictionary<string, EffectParams>();
            CustomEffects ??= new List<CustomEffect>();
            Hotkeys ??= new Dictionary<string, string>();
            Schedule ??= new List<ScheduleEntry>();
            Schedule.RemoveAll(e => e == null);
            SceneLock ??= new Scene();
            SceneSleep ??= new Scene();
            if (Effects.Find(Effect) == null && FindCustom(Effect) == null) Effect = "rainbow";
        }

        // --- Schwarze Balken ---
        public bool BlackbarEnabled { get; set; } = true;        // Balken (Filme 21:9, 4:3 ...) automatisch überspringen
        public int BlackbarThreshold { get; set; } = 16;         // 0..60: bis zu diesem Wert gilt ein Pixel als schwarz

        // --- Farbkalibrierung (alt, nur noch zum Übernehmen älterer Einstellungsdateien; es gelten Sdr und Hdr) ---
        public double Saturation { get; set; } = 100;            // Prozent, 100 = unverändert
        public double Gamma { get; set; } = 2.0;                 // 1.0 = unverändert; LEDs brauchen ca. 1.8 bis 2.4
        public double GainRed { get; set; } = 100;               // Weissabgleich, Prozent
        public double GainGreen { get; set; } = 100;
        public double GainBlue { get; set; } = 100;

        // --- HDR ---
        public bool HdrCapture { get; set; } = true;             // bei eingeschaltetem HDR das echte HDR-Bild aufnehmen
        public double HdrPaperWhite { get; set; } = 200;         // nits, die als "normales Weiss" gelten (80..480)

        // --- Ausgabe ---
        public double Smoothing { get; set; } = 60;              // 0 = keine, 95 = sehr träge
        public int Brightness { get; set; } = 100;               // Prozent
        public int TargetFps { get; set; } = 60;
        public string Port { get; set; } = "auto";
        public string CaptureMethod { get; set; } = "auto";    // auto | dxgi | gdi
        public int CaptureWidth { get; set; } = 320;             // Breite des verkleinerten Bildes: 160 | 320 | 640
        public bool Enabled { get; set; } = true;
        public bool CheckUpdates { get; set; } = true;           // beim Start auf GitHub nach einer neuen Version suchen

        [JsonIgnore]
        public int TotalLeds => LedsTop + LedsBottom + LedsLeft + LedsRight;

        // ---------- Laden / Speichern ----------
        public static string FilePath =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "ItenAmbilight", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                    if (s != null) { s.Normalize(); return s; }
                }
            }
            catch { /* defekte Datei -> Standardwerte */ }
            var fresh = new AppSettings();
            fresh.Normalize();
            return fresh;
        }

        public void Save()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var opt = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, opt));
        }

        public AppSettings Clone()
        {
            var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
            copy.Normalize();
            return copy;
        }
    }
}
