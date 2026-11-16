using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace ItenAmbilight
{
    public partial class App : Application
    {
        private System.Windows.Forms.NotifyIcon? _tray;
        private System.Windows.Forms.ToolStripItem? _toggleItem;
        private MainWindow? _window;

        public AppSettings Settings { get; private set; } = new AppSettings();
        public AmbilightEngine Engine { get; private set; } = null!;
        public bool IsQuitting { get; private set; }

        private SingleInstance? _single;
        private SystemHooks? _hooks;
        private volatile bool _locked, _displayOff, _suspended;

        /// <summary>Tastenkürzel und Systemmeldungen; null, solange das Programm startet.</summary>
        public SystemHooks? Hooks => _hooks;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---------- Befehle von der Kommandozeile (Stream Deck, Verknüpfungen) ----------
            List<string> commands = AppCommands.ParseArgs(e.Args, out bool trayOnly, out string? argError);
            if (argError != null)
            {
                MessageBox.Show("Unbekanntes Startargument: " + argError + "\n\nMöglich sind:\n\n" + AppCommands.Help,
                                "IT-EN Ambilight");
                Shutdown();
                return;
            }

            // ---------- Nur einmal laufen ----------
            _single = new SingleInstance();
            if (!_single.TryAcquire(0))
            {
                // Läuft schon: Befehle weitergeben; ohne Befehl das Fenster des laufenden Programms zeigen.
                if (commands.Count == 0 && !trayOnly) commands.Add("show");
                if (commands.Count == 0 || SingleInstance.Send(commands, 2000)) { Shutdown(); return; }
                // Keine Antwort (das andere Programm wird z. B. gerade beendet): kurz warten, dann selbst starten.
                if (!_single.TryAcquire(5000)) { Shutdown(); return; }
            }
            if (commands.Count == 1 && commands[0] == "quit") { _single.Dispose(); Shutdown(); return; }   // es lief gar nicht

            Settings = AppSettings.Load();
            Engine = new AmbilightEngine(Settings);
            Engine.Start();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Einstellungen öffnen", null, (_, _) => ShowWindow());
            _toggleItem = menu.Items.Add("", null, (_, _) => ToggleEnabled());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Beenden", null, (_, _) => Quit());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "IT-EN Ambilight",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (_, _) => ShowWindow();
            UpdateTray();

            // ---------- Tastenkürzel, Bildschirm ein/aus, Sperren, Ruhezustand ----------
            _hooks = new SystemHooks();
            _hooks.Pressed += RunCommand;
            _hooks.DisplayChanged += on => { _displayOff = !on; UpdateSystemState(); };
            _hooks.SetHotkeys(Settings.Hotkeys);
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // Weitere Programmstarts geben ihre Befehle hierher
            _single.Listen(line => Dispatcher.BeginInvoke(new Action(() => RunCommand(line))));

            // "--tray" = ohne Fenster starten (für Autostart). Ein Start mit Befehl (z. B. --toggle) öffnet auch kein Fenster.
            bool wantsWindow = commands.Remove("show");
            foreach (string command in commands) RunCommand(command);
            if (wantsWindow || (!trayOnly && commands.Count == 0)) ShowWindow();

            if (Settings.CheckUpdates) _ = CheckForUpdateAsync(silent: true);
        }

        // ---------------------------------------------------------------- Befehle, Tastenkürzel, Szenen
        /// <summary>Führt einen Befehl aus (Tastenkürzel, Kommandozeile, zweiter Programmstart). Siehe AppCommands.</summary>
        public void RunCommand(string command)
        {
            if (IsQuitting) return;
            switch (AppCommands.Apply(Settings, command, out string text))
            {
                case CommandResult.Show: ShowWindow(); break;
                case CommandResult.Quit: Quit(); break;
                case CommandResult.Changed:
                    ApplySettings(save: true);
                    _window?.LoadFromSettings();
                    Engine.AddLog("Befehl", text);
                    break;
                default:
                    Engine.AddLog("Befehl", "Unbekannter Befehl: " + command);
                    break;
            }
        }

        /// <summary>Tastenkürzel aus den Einstellungen neu anmelden. Liefert die Aktionen, die Windows abgelehnt hat.</summary>
        public List<string> ApplyHotkeys()
        {
            if (_hooks == null) return new List<string>();
            _hooks.SetHotkeys(Settings.Hotkeys);
            return new List<string>(_hooks.Failed);
        }

        private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.SessionLogon) _locked = false;
            UpdateSystemState();
        }

        private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _suspended = true;
                UpdateSystemState();
                try { Engine.PrepareSleep(); } catch { }      // Szene «Ruhezustand» noch senden, bevor der PC schläft
            }
            else if (e.Mode == PowerModes.Resume)
            {
                _suspended = false;
                Engine.Resume();
                UpdateSystemState();
            }
        }

        private void UpdateSystemState()
        {
            Engine.SystemState = _suspended || _displayOff ? 2 : _locked ? 1 : 0;
        }

        /// <summary>Symbol der eigenen .exe (app.ico); gibt es keines, das Standardsymbol von Windows.</summary>
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                    if (icon != null) return icon;
                }
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        private bool _updateBusy;

        /// <summary>
        /// Sucht auf dem offiziellen GitHub nach einer neuen Version und fragt, ob sie installiert werden soll.
        /// silent = true: nichts anzeigen, wenn es kein Update gibt oder das Netz fehlt (Suche beim Start).
        /// </summary>
        public async Task CheckForUpdateAsync(bool silent)
        {
            if (_updateBusy) return;
            _updateBusy = true;
            try
            {
                if (silent) await Task.Delay(4000);              // erst starten lassen
                Updater.UpdateInfo? info = await Updater.CheckAsync();
                if (info == null)
                {
                    if (!silent) MessageBox.Show("Du hast die neueste Version (" + Updater.CurrentVersion + ").", "IT-EN Ambilight");
                    return;
                }

                MessageBoxResult answer = MessageBox.Show(
                    "Neues Update auf dem offiziellen GitHub gefunden.\n\n" +
                    "Installiert: Version " + Updater.CurrentVersion + "\n" +
                    "Neu: Version " + info.Version + "\n\n" +
                    "Möchtest du es jetzt aktualisieren? Das Programm lädt die neue Version herunter und startet neu.",
                    "IT-EN Ambilight – Update", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;

                if (_tray != null) _tray.Text = "IT-EN Ambilight – Update wird geladen …";
                string file = await Updater.DownloadAsync(info);
                Updater.InstallAfterExit(file);
                Quit();
            }
            catch (Exception ex)
            {
                if (_tray != null) _tray.Text = "IT-EN Ambilight";
                if (!silent)
                    MessageBox.Show("Die Update-Suche hat nicht geklappt:\n" + ex.Message, "IT-EN Ambilight – Update");
            }
            finally { _updateBusy = false; }
        }

        public void ShowWindow()
        {
            if (_window == null) _window = new MainWindow();
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        public void ToggleEnabled()
        {
            Settings.Enabled = !Settings.Enabled;
            ApplySettings(save: true);
            _window?.LoadFromSettings();
        }

        /// <summary>Einstellungen an die Engine geben und optional speichern.</summary>
        public void ApplySettings(bool save)
        {
            Engine.Apply(Settings);
            if (save)
            {
                try { Settings.Save(); } catch { }
            }
            UpdateTray();
        }

        private void UpdateTray()
        {
            if (_toggleItem != null)
                _toggleItem.Text = Settings.Enabled ? "Ambilight ausschalten" : "Ambilight einschalten";
        }

        public void Quit()
        {
            IsQuitting = true;
            try { Settings.Save(); } catch { }
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { _hooks?.Dispose(); } catch { }
            Engine.Dispose();                      // schaltet die LEDs aus
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _single?.Dispose();
            Shutdown();
        }
    }
}
