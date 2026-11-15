using System;
using System.Threading.Tasks;
using System.Windows;

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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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

            // "--tray" = ohne Fenster starten (für Autostart)
            bool trayOnly = Array.Exists(e.Args, a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            if (!trayOnly) ShowWindow();

            if (Settings.CheckUpdates) _ = CheckForUpdateAsync(silent: true);
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
            Engine.Dispose();                      // schaltet die LEDs aus
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Shutdown();
        }
    }
}
