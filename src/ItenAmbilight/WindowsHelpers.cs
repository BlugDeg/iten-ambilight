using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ItenAmbilight
{
    /// <summary>Welches Programm ist gerade im Vordergrund? (für die Regeln pro Programm)</summary>
    public static class ForegroundApp
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>Prozessname ohne ".exe" (z. B. "vlc"), leer wenn nicht ermittelbar.</summary>
        public static string ProcessName()
        {
            try
            {
                IntPtr w = GetForegroundWindow();
                if (w == IntPtr.Zero) return "";
                GetWindowThreadProcessId(w, out uint pid);
                if (pid == 0) return "";
                using Process p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch { return ""; }
        }

        /// <summary>Alle Programme, die gerade ein Fenster offen haben: Name und (wenn lesbar) Pfad der .exe.</summary>
        public static List<(string Name, string Path)> RunningWindowApps()
        {
            var list = new List<(string Name, string Path)>();
            try
            {
                foreach (Process p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(p.MainWindowTitle)) continue;
                        if (list.Any(x => string.Equals(x.Name, p.ProcessName, StringComparison.OrdinalIgnoreCase))) continue;
                        string path = "";
                        try { path = p.MainModule?.FileName ?? ""; } catch { /* kein Zugriff (z. B. Programm mit Adminrechten) */ }
                        list.Add((p.ProcessName, path));
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Vergleichsform: ohne ".exe", ohne Leerzeichen am Rand.</summary>
        public static string Normalize(string name)
        {
            name = (name ?? "").Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name;
        }
    }

    /// <summary>Start mit Windows (Eintrag nur für den angemeldeten Benutzer, keine Adminrechte nötig).</summary>
    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ItenAmbilight";

        public static bool IsEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }

        public static bool Set(bool enabled)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key == null) return false;
                if (enabled)
                {
                    string? exe = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exe)) return false;
                    key.SetValue(ValueName, "\"" + exe + "\" --tray");
                }
                else key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }
            catch { return false; }
        }
    }
}
