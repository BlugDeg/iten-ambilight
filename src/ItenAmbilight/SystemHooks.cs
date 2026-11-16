using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ItenAmbilight
{
    /// <summary>
    /// Unsichtbares Fenster für alles, was Windows per Nachricht meldet:
    /// globale Tastenkürzel und "Bildschirm ein / aus".
    /// </summary>
    public sealed class SystemHooks : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
        [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        private const int WM_HOTKEY = 0x0312, WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
        private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        private static Guid ConsoleDisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");

        private readonly HwndSource _source;
        private readonly Dictionary<int, string> _actions = new Dictionary<int, string>();
        private Dictionary<string, string> _wanted = new Dictionary<string, string>();
        private IntPtr _powerHandle;
        private int _nextId = 1;
        private bool _suspended;

        /// <summary>Ein Tastenkürzel wurde gedrückt; Argument = Befehl (siehe AppCommands).</summary>
        public event Action<string>? Pressed;
        /// <summary>Windows hat den Bildschirm ein- (true) oder ausgeschaltet (false).</summary>
        public event Action<bool>? DisplayChanged;
        /// <summary>Aktionen, deren Tastenkürzel Windows abgelehnt hat (meist: schon von einem anderen Programm belegt).</summary>
        public List<string> Failed { get; } = new List<string>();

        public SystemHooks()
        {
            var p = new HwndSourceParameters("ItenAmbilightMessages")
            {
                Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
                WindowStyle = unchecked((int)0x80000000),        // WS_POPUP, nie sichtbar
                ExtendedWindowStyle = 0x00000080,                // WS_EX_TOOLWINDOW: nicht in der Taskleiste
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
            try { _powerHandle = RegisterPowerSettingNotification(_source.Handle, ref ConsoleDisplayState, 0); } catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                if (_actions.TryGetValue(wParam.ToInt32(), out string? action)) Pressed?.Invoke(action);
                handled = true;
            }
            else if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
            {
                // POWERBROADCAST_SETTING: GUID (16 Byte), Länge (4 Byte), Daten
                try
                {
                    Guid setting = Marshal.PtrToStructure<Guid>(lParam);
                    if (setting == ConsoleDisplayState && Marshal.ReadInt32(lParam, 16) >= 4)
                    {
                        int state = Marshal.ReadInt32(lParam, 20);           // 0 = aus, 1 = ein, 2 = gedimmt
                        DisplayChanged?.Invoke(state != 0);
                    }
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        /// <summary>Legt die Tastenkürzel neu fest: Aktion -> Tasten ("Ctrl+Alt+F9"). Leere Einträge werden übersprungen.</summary>
        public void SetHotkeys(Dictionary<string, string> hotkeys)
        {
            _wanted = new Dictionary<string, string>(hotkeys);
            if (!_suspended) RegisterAll();
        }

        /// <summary>Während ein Tastenkürzel eingegeben wird, dürfen die bestehenden nicht auslösen.</summary>
        public void Suspend(bool on)
        {
            _suspended = on;
            if (on) UnregisterAll(); else RegisterAll();
        }

        private void UnregisterAll()
        {
            foreach (int id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
        }

        private void RegisterAll()
        {
            UnregisterAll();
            Failed.Clear();
            foreach (KeyValuePair<string, string> pair in _wanted)
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                if (!TryParse(pair.Value, out uint mods, out uint vk)) { Failed.Add(pair.Key); continue; }
                // Helligkeit darf beim Halten der Taste wiederholen, alles andere nicht
                if (!pair.Key.StartsWith("brightness", StringComparison.Ordinal)) mods |= MOD_NOREPEAT;
                int id = _nextId++;
                if (RegisterHotKey(_source.Handle, id, mods, vk)) _actions[id] = pair.Key;
                else Failed.Add(pair.Key);
            }
        }

        // ---------- Text <-> Tasten ----------
        private static bool IsModifier(Key k) =>
            k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt ||
            k == Key.LeftShift || k == Key.RightShift || k == Key.LWin || k == Key.RWin || k == Key.System;

        /// <summary>
        /// Macht aus gedrückten Tasten den gespeicherten Text, z. B. "Ctrl+Alt+F9".
        /// Leer, wenn die Kombination nicht taugt: nur Umschalttasten, oder eine normale Taste ohne Strg/Alt/Win
        /// (das würde die Taste in allen Programmen blockieren). Funktionstasten F1 bis F24 gehen auch allein.
        /// </summary>
        public static string Format(ModifierKeys modifiers, Key key)
        {
            if (key == Key.None || IsModifier(key)) return "";
            bool function = key >= Key.F1 && key <= Key.F24;
            bool strong = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
            if (!function && !strong) return "";

            string text = "";
            if ((modifiers & ModifierKeys.Control) != 0) text += "Ctrl+";
            if ((modifiers & ModifierKeys.Alt) != 0) text += "Alt+";
            if ((modifiers & ModifierKeys.Shift) != 0) text += "Shift+";
            if ((modifiers & ModifierKeys.Windows) != 0) text += "Win+";
            string name = key.ToString();
            if (key >= Key.D0 && key <= Key.D9) name = name.Substring(1);        // "D5" -> "5"
            return text + name;
        }

        public static bool TryParse(string text, out uint modifiers, out uint vk)
        {
            modifiers = 0; vk = 0;
            string[] parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "strg": modifiers |= MOD_CONTROL; break;
                    case "alt": modifiers |= MOD_ALT; break;
                    case "shift": modifiers |= MOD_SHIFT; break;
                    case "win": modifiers |= MOD_WIN; break;
                    default: return false;
                }
            }
            string name = parts[parts.Length - 1];
            if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
            if (!Enum.TryParse(name, ignoreCase: true, out Key key) || key == Key.None || IsModifier(key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        /// <summary>Anzeige für die Oberfläche: "Ctrl" heisst auf der Schweizer Tastatur auch Ctrl, also unverändert.</summary>
        public static string Display(string? stored) => string.IsNullOrWhiteSpace(stored) ? "" : stored!.Replace("+", " + ");

        public void Dispose()
        {
            UnregisterAll();
            try { if (_powerHandle != IntPtr.Zero) UnregisterPowerSettingNotification(_powerHandle); } catch { }
            _powerHandle = IntPtr.Zero;
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }
}
