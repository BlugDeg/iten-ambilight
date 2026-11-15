using System;
using System.Text;
using MoonSharp.Interpreter;

namespace ItenAmbilight
{
    /// <summary>
    /// Eigener Effekt als Lua-Skript. Das Skript definiert
    ///     function led(i, pos, t, p)  ->  Farbe
    /// i = Nummer der LED (1..n), pos = Lage rund um den Monitor (0..1), t = Zeit in Sekunden,
    /// p = Regler: p.intensity (0..1), p.color1, p.color2, p.color3, p.n.
    /// Zurückgegeben wird rgb(r, g, b), hsv(h, s, v) oder drei Zahlen 0..255.
    /// Optional: function frame(t, dt, p) wird einmal pro Bild vor den LEDs aufgerufen.
    /// </summary>
    public sealed class LuaEffect
    {
        private const string Prelude = @"
function clamp(x, lo, hi) if x < lo then return lo elseif x > hi then return hi else return x end end
function rgb(r, g, b) return {r, g, b} end
function hsv(h, s, v)
  h = (h % 1) * 6
  local i = math.floor(h)
  local f = h - i
  local p, q, t = v * (1 - s), v * (1 - s * f), v * (1 - s * (1 - f))
  local r, g, b
  if i == 0 then r, g, b = v, t, p
  elseif i == 1 then r, g, b = q, v, p
  elseif i == 2 then r, g, b = p, v, t
  elseif i == 3 then r, g, b = p, q, v
  elseif i == 4 then r, g, b = t, p, v
  else r, g, b = v, p, q end
  return {r * 255, g * 255, b * 255}
end
function mix(a, b, x)
  x = clamp(x, 0, 1)
  return {a[1] + (b[1] - a[1]) * x, a[2] + (b[2] - a[2]) * x, a[3] + (b[3] - a[3]) * x}
end
function scale(c, x) return {c[1] * x, c[2] * x, c[3] * x} end
function wave(x) return 0.5 + 0.5 * math.sin(x * 6.2831853) end
__out = {}
function __render(n, t, dt, p)
  if frame then frame(t, dt, p) end
  for i = 1, n do
    local a, b, c = led(i, (i - 1) / n, t, p)
    if type(a) == 'table' then a, b, c = a[1], a[2], a[3] end
    local k = (i - 1) * 3
    __out[k + 1] = a or 0
    __out[k + 2] = b or 0
    __out[k + 3] = c or 0
  end
end
";
        /// <summary>Vorlage für einen neuen eigenen Effekt.</summary>
        public const string Template =
@"-- Eigener Effekt
-- i   = Nummer der LED (1 bis n)
-- pos = Lage rund um den Monitor (0 bis 1)
-- t   = Zeit in Sekunden (der Regler Geschwindigkeit ist schon eingerechnet)
-- p   = Regler: p.intensity (0 bis 1), p.color1, p.color2, p.color3
-- Befehle: rgb(r,g,b)  hsv(h,s,v)  mix(a,b,x)  scale(farbe,x)  wave(x)  noise(x,t)  clamp(x,lo,hi)

function led(i, pos, t, p)
  local hell = wave(pos * 3 - t * 0.25)
  return scale(mix(p.color1, p.color2, pos), 0.1 + 0.9 * hell)
end
";

        private Script? _script;
        private DynValue? _render;
        private Table? _out;
        private Table? _params;
        private string _source = "\0";
        private string _paramKey = "";
        private double _t;
        private bool _broken;

        /// <summary>Fehlermeldung des Skripts; leer = alles in Ordnung.</summary>
        public string Error { get; private set; } = "";

        private void Compile(string source)
        {
            _source = source;
            _broken = true;
            _t = 0;
            _paramKey = "";
            Error = "";
            try
            {
                // Sandbox: kein Dateizugriff, kein Betriebssystem, kein Nachladen von Code
                var script = new Script(CoreModules.Preset_HardSandbox);
                script.Globals["noise"] = (Func<double, double, double>)((x, t) => Effects.Noise3(x, t, 0));
                script.DoString(Prelude);
                script.DoString(source);
                if (script.Globals.Get("led").Type != DataType.Function)
                {
                    Error = "Im Skript fehlt die Funktion led(i, pos, t, p).";
                    return;
                }
                _script = script;
                _render = script.Globals.Get("__render");
                _out = script.Globals.Get("__out").Table;
                _broken = false;
            }
            catch (InterpreterException ex) { Error = ex.DecoratedMessage ?? ex.Message; }
            catch (Exception ex) { Error = ex.Message; }
        }

        private static DynValue ColorTable(Script script, string hex)
        {
            Effects.ParseColor(hex, out double r, out double g, out double b);
            var t = new Table(script);
            t[1] = r; t[2] = g; t[3] = b;
            return DynValue.NewTable(t);
        }

        /// <summary>Schreibt die Zielfarben (3 Werte pro LED, 0..255). Bei einem Fehler bleiben die LEDs dunkel.</summary>
        public void Render(string source, double[] target, int n, double dt, EffectParams p)
        {
            if (!ReferenceEquals(source, _source) && source != _source) Compile(source ?? "");
            if (_broken || _script == null || _render == null || _out == null)
            {
                Array.Clear(target, 0, Math.Min(target.Length, n * 3));
                return;
            }

            double step = Math.Min(dt, 0.2) * Math.Clamp(p.Speed, 1, 100) / 50.0;
            _t += step;

            string key = p.Intensity + p.Color1 + p.Color2 + p.Color3 + n;
            if (key != _paramKey || _params == null)
            {
                _paramKey = key;
                _params = new Table(_script);
                _params["intensity"] = Math.Clamp(p.Intensity, 1, 100) / 100.0;
                _params["n"] = n;
                _params["color1"] = ColorTable(_script, p.Color1);
                _params["color2"] = ColorTable(_script, p.Color2);
                _params["color3"] = ColorTable(_script, p.Color3);
            }

            try
            {
                // Als Coroutine mit Befehlszähler: eine Endlosschleife wird abgebrochen statt das Programm zu blockieren
                Coroutine co = _script.CreateCoroutine(_render).Coroutine;
                co.AutoYieldCounter = 50000 + 4000L * n;
                co.Resume(DynValue.NewNumber(n), DynValue.NewNumber(_t), DynValue.NewNumber(step), DynValue.NewTable(_params));
                if (co.State == CoroutineState.ForceSuspended)
                {
                    Error = "Das Skript rechnet zu lange (Endlosschleife?). Es wurde angehalten.";
                    _broken = true;
                    Array.Clear(target, 0, Math.Min(target.Length, n * 3));
                    return;
                }
                for (int i = 0; i < n * 3; i++)
                {
                    DynValue v = _out.Get(i + 1);
                    double x = v.Type == DataType.Number ? v.Number : 0;
                    target[i] = double.IsNaN(x) ? 0 : Math.Clamp(x, 0, 255);
                }
            }
            catch (InterpreterException ex)
            {
                Error = ex.DecoratedMessage ?? ex.Message;
                _broken = true;
                Array.Clear(target, 0, Math.Min(target.Length, n * 3));
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _broken = true;
                Array.Clear(target, 0, Math.Min(target.Length, n * 3));
            }
        }

        // ------------------------------------------------------------------ Baukasten
        /// <summary>Erzeugt aus den Baukasten-Angaben ein fertiges Skript.</summary>
        /// <param name="baseKind">solid | gradient2 | gradient3 | rainbow</param>
        /// <param name="motion">none | rotate | wave | breathe | comet</param>
        public static string BuildScript(string baseKind, string motion, bool sparkle, int waves)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- Erzeugt vom Baukasten. Du kannst das Skript von Hand weiter anpassen.");
            sb.AppendLine("-- Farben und Geschwindigkeit stellst du mit den Reglern des Effekts ein.");
            sb.AppendLine();
            sb.AppendLine("function led(i, pos, t, p)");
            sb.AppendLine(motion == "rotate"
                ? "  local x = (pos + t * 0.1) % 1          -- Bewegung: Farben drehen sich um den Monitor"
                : "  local x = pos");
            switch (baseKind)
            {
                case "gradient2":
                    sb.AppendLine("  local c = mix(p.color1, p.color2, wave(x))           -- Verlauf aus zwei Farben");
                    break;
                case "gradient3":
                    sb.AppendLine("  local y = x * 3                                      -- Verlauf aus drei Farben");
                    sb.AppendLine("  local c");
                    sb.AppendLine("  if y < 1 then c = mix(p.color1, p.color2, y)");
                    sb.AppendLine("  elseif y < 2 then c = mix(p.color2, p.color3, y - 1)");
                    sb.AppendLine("  else c = mix(p.color3, p.color1, y - 2) end");
                    break;
                case "rainbow":
                    sb.AppendLine("  local c = hsv(x, 1, 1)                               -- Regenbogen");
                    break;
                default:
                    sb.AppendLine("  local c = p.color1                                   -- eine Farbe");
                    break;
            }
            switch (motion)
            {
                case "wave":
                    sb.AppendLine("  c = scale(c, 0.1 + 0.9 * wave(pos * " + Math.Clamp(waves, 1, 8) + " - t * 0.25))   -- Bewegung: Helligkeitswellen");
                    break;
                case "breathe":
                    sb.AppendLine("  c = scale(c, 0.05 + 0.95 * wave(t * 0.25))           -- Bewegung: alle LEDs atmen");
                    break;
                case "comet":
                    sb.AppendLine("  local hinter = (t * 0.25 - pos) % 1                  -- Bewegung: Lauflicht mit Schweif");
                    sb.AppendLine("  c = scale(c, math.exp(-hinter / (0.03 + 0.2 * p.intensity)))");
                    break;
            }
            if (sparkle)
            {
                sb.AppendLine("  if noise(i * 7.3, math.floor(t * 6) * 3.1) > 0.9 then  -- Überlagerung: Funkeln");
                sb.AppendLine("    c = rgb(255, 255, 255)");
                sb.AppendLine("  end");
            }
            sb.AppendLine("  return c");
            sb.AppendLine("end");
            return sb.ToString();
        }
    }
}
