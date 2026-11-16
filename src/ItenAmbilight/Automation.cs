using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ItenAmbilight
{
    /// <summary>Zeitfenster im Zeitplan: in dieser Zeit sind die LEDs aus oder laufen mit fester Helligkeit.</summary>
    public class ScheduleEntry
    {
        public string From { get; set; } = "22:00";             // Uhrzeit HH:mm
        public string To { get; set; } = "07:00";               // Uhrzeit HH:mm; kleiner als From = über Mitternacht
        public string Action { get; set; } = "off";             // off | brightness
        public int Brightness { get; set; } = 30;               // Prozent, nur bei Action = brightness
    }

    /// <summary>Was die LEDs zeigen, wenn der PC gesperrt ist bzw. schläft. Standard: aus.</summary>
    public class Scene
    {
        public string Mode { get; set; } = "off";               // off | color | effect
        public string Color { get; set; } = "#FF6A00";
        public string Effect { get; set; } = "rainbow";
        public int Brightness { get; set; } = 30;               // Prozent
    }

    /// <summary>Zeitplan und Szenen: reine Logik ohne Windows-Aufrufe.</summary>
    public static class Automation
    {
        /// <summary>Liest "22:00", "7:30", "22.15" oder "7" als Minuten seit Mitternacht.</summary>
        public static bool TryParseTime(string? text, out int minutes)
        {
            minutes = 0;
            string[] parts = (text ?? "").Trim().Split(':', '.');
            if (parts.Length < 1 || parts.Length > 2) return false;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int h)) return false;
            int m = 0;
            if (parts.Length == 2 && !int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out m)) return false;
            if (h == 24 && m == 0) h = 0;
            if (h < 0 || h > 23 || m < 0 || m > 59) return false;
            minutes = h * 60 + m;
            return true;
        }

        public static string FormatTime(int minutes) =>
            (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);

        /// <summary>true, wenn die Uhrzeit im Zeitfenster liegt. Der Anfang zählt dazu, das Ende nicht. Gleiche Zeiten = nie.</summary>
        public static bool Contains(ScheduleEntry entry, int minuteOfDay)
        {
            if (!TryParseTime(entry.From, out int from) || !TryParseTime(entry.To, out int to)) return false;
            if (from == to) return false;
            return from < to ? minuteOfDay >= from && minuteOfDay < to
                             : minuteOfDay >= from || minuteOfDay < to;      // über Mitternacht
        }

        /// <summary>Erstes passendes Zeitfenster (Reihenfolge der Liste), sonst null.</summary>
        public static ScheduleEntry? FindSchedule(IEnumerable<ScheduleEntry>? entries, DateTime now)
        {
            if (entries == null) return null;
            int minute = now.Hour * 60 + now.Minute;
            return entries.FirstOrDefault(e => e != null && Contains(e, minute));
        }

        public static string Describe(ScheduleEntry e) =>
            e.From + " – " + e.To + "  ·  " + (e.Action == "brightness" ? "Helligkeit " + Math.Clamp(e.Brightness, 0, 100) + " %" : "LEDs aus");
    }

    public enum CommandResult { Unknown, Changed, Show, Quit }

    /// <summary>
    /// Befehle, die von aussen kommen: Tastenkürzel, Kommandozeile (Stream Deck) und zweiter Programmstart.
    /// Ein Befehl ist eine Textzeile wie "toggle", "mode effect", "effect fire", "brightness +10", "color #FF8800".
    /// </summary>
    public static class AppCommands
    {
        /// <summary>Aktionen, die sich auf ein Tastenkürzel legen lassen.</summary>
        public static readonly (string Id, string Title)[] HotkeyActions =
        {
            ("toggle", "Ein / Aus umschalten"),
            ("mode ambilight", "Betriebsart Ambilight"),
            ("mode effect", "Betriebsart Effekt"),
            ("mode color", "Betriebsart Feste Farbe"),
            ("effect next", "Nächster Effekt"),
            ("effect prev", "Vorheriger Effekt"),
            ("brightness +10", "Heller (+10 %)"),
            ("brightness -10", "Dunkler (−10 %)"),
            ("show", "Fenster öffnen"),
        };

        public const string Help =
            "--toggle              Ein / Aus umschalten\n" +
            "--on  /  --off        Einschalten / Ausschalten\n" +
            "--mode ambilight      Betriebsart: ambilight, effect oder color\n" +
            "--effect fire         Effekt wählen (Kennung), oder next / prev\n" +
            "--brightness 60       Helligkeit in Prozent, oder +10 / -10\n" +
            "--color #FF8800       Feste Farbe\n" +
            "--show                Fenster öffnen\n" +
            "--quit                Programm beenden";

        /// <summary>
        /// Wandelt die Startargumente in Befehle um, z. B. "--mode effect --effect fire" -> "mode effect", "effect fire".
        /// tray = "--tray" war dabei (ohne Fenster starten). error = erstes unbekanntes Argument, sonst null.
        /// </summary>
        public static List<string> ParseArgs(string[] args, out bool tray, out string? error)
        {
            var list = new List<string>();
            tray = false;
            error = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim();
                if (!a.StartsWith("--", StringComparison.Ordinal)) { error ??= a; continue; }
                string name = a.Substring(2).ToLowerInvariant(), value = "";
                int eq = name.IndexOf('=');
                if (eq > 0) { value = a.Substring(2 + eq + 1).Trim(); name = name.Substring(0, eq); }

                switch (name)
                {
                    case "tray": tray = true; break;
                    case "toggle": case "on": case "off": case "show": case "quit":
                        list.Add(name); break;
                    case "mode": case "effect": case "brightness": case "color":
                        if (value.Length == 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            value = args[++i].Trim();
                        if (value.Length == 0) error ??= a + " (Wert fehlt)";
                        else list.Add(name + " " + value);
                        break;
                    default: error ??= a; break;
                }
            }
            return list;
        }

        /// <summary>Alle wählbaren Effekte in fester Reihenfolge: mitgelieferte, dann eigene.</summary>
        public static List<string> EffectIds(AppSettings s)
        {
            var ids = Effects.All.Select(e => e.Id).ToList();
            ids.AddRange(s.CustomEffects.Select(c => "custom:" + c.Id));
            return ids;
        }

        /// <summary>Führt einen Befehl auf den Einstellungen aus. text = kurze Beschreibung für den Verlauf.</summary>
        public static CommandResult Apply(AppSettings s, string? command, out string text)
        {
            text = "";
            string[] parts = (command ?? "").Trim().Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return CommandResult.Unknown;
            string verb = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (verb)
            {
                case "show": return CommandResult.Show;
                case "quit": return CommandResult.Quit;

                case "toggle":
                    s.Enabled = !s.Enabled;
                    text = s.Enabled ? "Eingeschaltet" : "Ausgeschaltet";
                    return CommandResult.Changed;
                case "on":
                    s.Enabled = true; text = "Eingeschaltet";
                    return CommandResult.Changed;
                case "off":
                    s.Enabled = false; text = "Ausgeschaltet";
                    return CommandResult.Changed;

                case "mode":
                    switch (arg.ToLowerInvariant())
                    {
                        case "ambilight": s.Mode = "ambilight"; text = "Betriebsart Ambilight"; break;
                        case "effect": s.Mode = "effect"; text = "Betriebsart Effekt"; break;
                        case "color": s.Mode = "color"; text = "Betriebsart Feste Farbe"; break;
                        case "off": s.Enabled = false; text = "Ausgeschaltet"; return CommandResult.Changed;
                        default: return CommandResult.Unknown;
                    }
                    s.Enabled = true;
                    return CommandResult.Changed;

                case "effect":
                {
                    List<string> ids = EffectIds(s);
                    if (ids.Count == 0) return CommandResult.Unknown;
                    string wanted = arg.ToLowerInvariant();
                    string id;
                    if (wanted == "next" || wanted == "prev")
                    {
                        int i = ids.IndexOf(s.Effect);
                        if (i < 0) i = 0;
                        else i = (i + (wanted == "next" ? 1 : ids.Count - 1)) % ids.Count;
                        id = ids[i];
                    }
                    else
                    {
                        // Kennung (z. B. "fire") oder angezeigter Name (z. B. "Feuer")
                        id = ids.FirstOrDefault(x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase))
                             ?? ids.FirstOrDefault(x => string.Equals(s.EffectTitle(x), arg, StringComparison.OrdinalIgnoreCase))
                             ?? "";
                        if (id.Length == 0) return CommandResult.Unknown;
                    }
                    s.Effect = id;
                    s.Mode = "effect";
                    s.Enabled = true;
                    text = "Effekt " + s.EffectTitle(id);
                    return CommandResult.Changed;
                }

                case "brightness":
                {
                    string v = arg.Replace("%", "").Replace(" ", "").Replace('−', '-');
                    bool relative = v.StartsWith("+", StringComparison.Ordinal) || v.StartsWith("-", StringComparison.Ordinal);
                    if (!int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)) return CommandResult.Unknown;
                    s.Brightness = Math.Clamp(relative ? s.Brightness + n : n, 0, 100);
                    text = "Helligkeit " + s.Brightness + " %";
                    return CommandResult.Changed;
                }

                case "color":
                {
                    string h = arg.TrimStart('#');
                    if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return CommandResult.Unknown;
                    s.StaticColor = "#" + h.ToUpperInvariant();
                    s.Mode = "color";
                    s.Enabled = true;
                    text = "Feste Farbe " + s.StaticColor;
                    return CommandResult.Changed;
                }
            }
            return CommandResult.Unknown;
        }
    }
}
