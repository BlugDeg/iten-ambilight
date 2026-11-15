using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ItenAmbilight
{
    public partial class MainWindow : Window
    {
        private const int MaxLeds = 300;               // Obergrenze der Firmware v4.0
        private const double PreviewWidth = 440;       // Breite der ausgeklappten Vorschau

        private readonly App _app = (App)Application.Current;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private readonly UsageMonitor _usage = new UsageMonitor();
        private bool _loading;
        private readonly Image _previewImage = new Image { Stretch = Stretch.Fill };
        private WriteableBitmap? _previewBitmap;
        private readonly List<EffectItem> _allEffects = new List<EffectItem>();
        private string _category = "Alle";
        private readonly LuaEffect _editorLua = new LuaEffect();
        private readonly double[] _editorBuffer = new double[60 * 3];
        private string _logStamp = "";

        public MainWindow()
        {
            InitializeComponent();

            FillMonitors();
            FillPorts();
            BuildCategoryChips();
            FillEffects();
            FillRuleProcesses();
            FillCustomEffects("");
            LoadFromSettings();
            RuleEffectBox.SelectedIndex = 0;
            UpdateSwatch(RuleColorBox, RuleColorSwatch);
            UpdateRulePanels();
            VersionInfo.Text = "Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?")
                               + "  ·  Einstellungen: " + AppSettings.FilePath;

            // ---------- Navigation ----------
            NavStart.Checked += (_, _) => ShowPage(PageStart, "Start");
            NavEffects.Checked += (_, _) => ShowPage(PageEffects, "Effekte");
            NavEditor.Checked += (_, _) => ShowPage(PageEditor, "Eigene Effekte");
            NavRules.Checked += (_, _) => ShowPage(PageRules, "Regeln");
            NavDevice.Checked += (_, _) => ShowPage(PageDevice, "Gerät");
            NavHardware.Checked += (_, _) => ShowPage(PageHardware, "LED-Hardware");
            NavAmbilight.Checked += (_, _) => ShowPage(PageAmbilight, "Ambilight");
            NavGeneral.Checked += (_, _) => ShowPage(PageGeneral, "Allgemein");
            GotoEffectsButton.Click += (_, _) => NavEffects.IsChecked = true;

            PreviewToggle.Checked += (_, _) => SetPreview(true);
            PreviewToggle.Unchecked += (_, _) => SetPreview(false);

            // ---------- Jede Änderung wirkt sofort ----------
            ModeAmbilight.Checked += (_, _) => UiChanged();
            ModeEffect.Checked += (_, _) => UiChanged();
            ModeColor.Checked += (_, _) => UiChanged();
            ModeOff.Checked += (_, _) => UiChanged();
            EffectList.SelectionChanged += (_, _) =>
            {
                if (_loading) return;
                _loading = true;
                ModeEffect.IsChecked = true;               // Effekt angeklickt -> Betriebsart Effekt
                _loading = false;
                UiChanged();
                ShowFxParams();
            };
            StaticColorBox.TextChanged += (_, _) => UiChanged();
            StaticColorButton.Click += (_, _) => PickColor(StaticColorBox);

            // ---------- Regler des gewählten Effekts ----------
            FxSpeedSlider.ValueChanged += (_, _) => FxChanged();
            FxIntensitySlider.ValueChanged += (_, _) => FxChanged();
            FxColor1Box.TextChanged += (_, _) => FxChanged();
            FxColor2Box.TextChanged += (_, _) => FxChanged();
            FxColor3Box.TextChanged += (_, _) => FxChanged();
            FxColor1Button.Click += (_, _) => PickColor(FxColor1Box);
            FxColor2Button.Click += (_, _) => PickColor(FxColor2Box);
            FxColor3Button.Click += (_, _) => PickColor(FxColor3Box);
            FxResetButton.Click += (_, _) =>
            {
                _app.Settings.EffectSettings.Remove(SelectedEffectId);
                _app.ApplySettings(save: false);
                ShowFxParams();
            };

            // ---------- Eigene Effekte ----------
            CustomBox.SelectionChanged += (_, _) => ShowCustomEffect();
            CustomNewButton.Click += (_, _) => NewCustomEffect();
            CustomDeleteButton.Click += (_, _) => DeleteCustomEffect();
            CustomSaveButton.Click += (_, _) => SaveCustomEffect(run: false);
            CustomRunButton.Click += (_, _) => SaveCustomEffect(run: true);
            BuildButton.Click += (_, _) => BuildFromKit();

            // ---------- Gerät ----------
            DeviceToggleButton.Click += (_, _) => _app.Engine.SendDeviceCommand("OT", "Ein / Aus am Gerät umgeschaltet");
            DeviceDimButton.Click += (_, _) => _app.Engine.SendDeviceCommand("BN", "Dimmstufe am Gerät gewechselt");

            MonitorBox.SelectionChanged += (_, _) => UiChanged();
            DiagonalBox.TextChanged += (_, _) => UiChanged();
            DensityBox.TextChanged += (_, _) => UiChanged();
            AutoBox.Click += (_, _) => UiChanged();
            TopBox.TextChanged += (_, _) => UiChanged();
            BottomBox.TextChanged += (_, _) => UiChanged();
            LeftBox.TextChanged += (_, _) => UiChanged();
            RightBox.TextChanged += (_, _) => UiChanged();
            StartBox.SelectionChanged += (_, _) => UiChanged();
            ClockwiseBox.Click += (_, _) => UiChanged();
            CropLeftSlider.ValueChanged += (_, _) => UiChanged();
            CropRightSlider.ValueChanged += (_, _) => UiChanged();
            CropTopSlider.ValueChanged += (_, _) => UiChanged();
            CropBottomSlider.ValueChanged += (_, _) => UiChanged();
            DepthSlider.ValueChanged += (_, _) => UiChanged();
            SmoothSlider.ValueChanged += (_, _) => UiChanged();
            BrightSlider.ValueChanged += (_, _) => UiChanged();
            FpsSlider.ValueChanged += (_, _) => UiChanged();
            PortBox.SelectionChanged += (_, _) => UiChanged();
            CaptureBox.SelectionChanged += (_, _) => UiChanged();
            CaptureResBox.SelectionChanged += (_, _) => UiChanged();
            BlackbarBox.Click += (_, _) => UiChanged();
            BlackbarSlider.ValueChanged += (_, _) => UiChanged();
            SatSlider.ValueChanged += (_, _) => UiChanged();
            GammaSlider.ValueChanged += (_, _) => UiChanged();
            GainRSlider.ValueChanged += (_, _) => UiChanged();
            GainGSlider.ValueChanged += (_, _) => UiChanged();
            GainBSlider.ValueChanged += (_, _) => UiChanged();
            HdrBox.Click += (_, _) => UiChanged();
            PaperWhiteSlider.ValueChanged += (_, _) => UiChanged();
            BlackLevelSlider.ValueChanged += (_, _) => UiChanged();
            ProfileBox.SelectionChanged += (_, _) => LoadProfile();
            CalibrateButton.Click += (_, _) => StartCalibration();

            IdentifyButton.Click += (_, _) => MonitorIdentifier.Show();

            // Testfarbe wird nicht gespeichert und endet, sobald das Fenster geschlossen wird
            TestBox.SelectionChanged += (_, _) => _app.Engine.TestColor = Math.Max(0, TestBox.SelectedIndex);
            ResetColorsButton.Click += (_, _) =>
            {
                if (MessageBox.Show(this, "Den ganzen Farbabgleich für dieses Profil auf die Standardwerte zurücksetzen?",
                                    "Farbabgleich", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                if (EditingHdr) _app.Settings.Hdr = new ColorProfile(); else _app.Settings.Sdr = new ColorProfile();
                LoadProfile();
                _app.ApplySettings(save: false);
            };

            // ---------- Regeln ----------
            RuleColorButton.Click += (_, _) => PickColor(RuleColorBox);
            RuleColorBox.TextChanged += (_, _) => UpdateSwatch(RuleColorBox, RuleColorSwatch);
            RuleModeBox.SelectionChanged += (_, _) => UpdateRulePanels();
            RuleRefreshButton.Click += (_, _) => FillRuleProcesses();
            RuleBrowseButton.Click += (_, _) => BrowseRuleExe();
            RuleAddButton.Click += (_, _) => AddRule();
            RuleRemoveButton.Click += (_, _) => RemoveRule();
            RulesList.SelectionChanged += (_, _) => ShowSelectedRule();

            // ---------- Gerät und Allgemein ----------
            AutostartBox.Click += (_, _) =>
            {
                if (!Autostart.Set(AutostartBox.IsChecked == true))
                    SaveInfo.Text = "Autostart konnte nicht geändert werden.";
                AutostartBox.IsChecked = Autostart.IsEnabled();
            };
            UpdateBox.Click += (_, _) => { _app.Settings.CheckUpdates = UpdateBox.IsChecked == true; _app.ApplySettings(save: true); };
            UpdateNowButton.Click += async (_, _) =>
            {
                UpdateNowButton.IsEnabled = false;
                await _app.CheckForUpdateAsync(silent: false);
                UpdateNowButton.IsEnabled = true;
            };
            RefreshPortsButton.Click += (_, _) => { FillPorts(); SelectPort(_app.Settings.Port); };
            SaveButton.Click += (_, _) =>
            {
                _app.ApplySettings(save: true);
                SaveInfo.Text = "Gespeichert.";
            };

            _timer.Interval = TimeSpan.FromMilliseconds(100);
            _timer.Tick += (_, _) => { UpdateStatus(); UpdatePreview(); UpdateEditorPreview(); };
            _timer.Start();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            UiNative.UseDarkTitleBar(this);
        }

        // Fenster schliessen = nur verstecken (Programm läuft im Tray weiter)
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_app.IsQuitting) { _usage.Dispose(); return; }   // Programm wird beendet -> wirklich schliessen
            e.Cancel = true;
            TestBox.SelectedIndex = 0;            // Testfarbe beenden
            _app.ApplySettings(save: true);
            Hide();
        }

        // ---------------------------------------------------------------- Seiten und Vorschau
        private void ShowPage(FrameworkElement page, string title)
        {
            foreach (FrameworkElement p in new FrameworkElement[] { PageStart, PageEffects, PageEditor, PageRules, PageDevice, PageHardware, PageAmbilight, PageGeneral })
                p.Visibility = ReferenceEquals(p, page) ? Visibility.Visible : Visibility.Collapsed;
            PageTitle.Text = title;
        }

        /// <summary>Klappt die Vorschau rechts aus oder ein; das Fenster wird entsprechend breiter oder schmaler.</summary>
        private void SetPreview(bool open)
        {
            PreviewColumn.Width = new GridLength(open ? PreviewWidth : 0);
            if (WindowState == WindowState.Normal)
                Width = Math.Max(MinWidth, Width + (open ? PreviewWidth : -PreviewWidth));
        }

        // ---------------------------------------------------------------- Listen füllen
        private void FillMonitors()
        {
            MonitorBox.Items.Clear();
            int i = 1;
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                string text = "Monitor " + i + "  ·  " + s.Bounds.Width + " × " + s.Bounds.Height + (s.Primary ? "  ·  Hauptmonitor" : "");
                MonitorBox.Items.Add(new ComboBoxItem { Content = text, Tag = s.DeviceName });
                i++;
            }
        }

        private void FillPorts()
        {
            bool old = _loading;
            _loading = true;
            PortBox.Items.Clear();
            PortBox.Items.Add(new ComboBoxItem { Content = "automatisch finden", Tag = "auto" });
            List<string> esp = AdalightDevice.FindEspPorts();
            foreach (string p in SerialPort.GetPortNames().Distinct().OrderBy(x => x))
            {
                bool isEsp = esp.Contains(p, StringComparer.OrdinalIgnoreCase);
                PortBox.Items.Add(new ComboBoxItem { Content = p + (isEsp ? "  (ESP32)" : ""), Tag = p });
            }
            _loading = old;
        }

        /// <summary>Vorschau-Streifen: der Effekt wird kurz laufen gelassen und sein Bild als Farbverlauf gezeigt.</summary>
        private Brush EffectPreviewBrush(string id)
        {
            const int n = 40;
            var buffer = new double[n * 3];
            AppSettings s = _app.Settings;
            EffectParams p = s.GetEffectParams(id);
            CustomEffect? custom = s.FindCustom(id);
            if (custom != null)
            {
                var lua = new LuaEffect();
                for (int f = 0; f < 20; f++) lua.Render(custom.Script, buffer, n, 0.1, p);
            }
            else
            {
                var fx = new Effects();
                for (int f = 0; f < 90; f++) fx.Render(id, buffer, n, 1 / 30.0, p);
            }
            return StripBrush(buffer, n);
        }

        private static Brush StripBrush(double[] rgb, int n)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i < n; i++)
            {
                var c = Color.FromRgb((byte)Math.Clamp(rgb[i * 3], 0, 255), (byte)Math.Clamp(rgb[i * 3 + 1], 0, 255), (byte)Math.Clamp(rgb[i * 3 + 2], 0, 255));
                brush.GradientStops.Add(new GradientStop(c, n == 1 ? 0 : (double)i / (n - 1)));
            }
            brush.Freeze();
            return brush;
        }

        private void BuildCategoryChips()
        {
            CategoryPanel.Children.Clear();
            var names = new List<string> { "Alle" };
            names.AddRange(Effects.Categories);
            names.Add("Eigene");
            foreach (string name in names)
            {
                var chip = new RadioButton { Content = name, GroupName = "EffectCategory", Style = (Style)FindResource("Chip"), IsChecked = name == _category };
                string captured = name;
                chip.Checked += (_, _) => { _category = captured; ShowEffectList(); };
                CategoryPanel.Children.Add(chip);
            }
        }

        /// <summary>Liest alle Effekte neu ein (mitgelieferte und eigene) und füllt Liste und Regel-Auswahl.</summary>
        private void FillEffects()
        {
            _allEffects.Clear();
            foreach (EffectDef e in Effects.All)
                _allEffects.Add(new EffectItem { Id = e.Id, Title = e.Title, Category = e.Category, Brush = EffectPreviewBrush(e.Id) });
            foreach (CustomEffect c in _app.Settings.CustomEffects)
                _allEffects.Add(new EffectItem { Id = "custom:" + c.Id, Title = c.Name, Category = "Eigene", Brush = EffectPreviewBrush("custom:" + c.Id) });

            string ruleEffect = TagOf(RuleEffectBox, "rainbow");
            RuleEffectBox.Items.Clear();
            foreach (EffectItem e in _allEffects)
                RuleEffectBox.Items.Add(new ComboBoxItem { Content = e.Title + "  ·  " + e.Category, Tag = e.Id });
            SelectByTag(RuleEffectBox, ruleEffect);
            ShowEffectList();
        }

        /// <summary>Zeigt die Effekte der gewählten Kategorie.</summary>
        private void ShowEffectList()
        {
            bool old = _loading;
            _loading = true;
            EffectList.Items.Clear();
            foreach (EffectItem e in _allEffects)
                if (_category == "Alle" || e.Category == _category) EffectList.Items.Add(e);
            EffectList.SelectedItem = EffectList.Items.OfType<EffectItem>().FirstOrDefault(x => x.Id == _app.Settings.Effect);
            _loading = old;
            ShowFxParams();
        }

        private string SelectedEffectId => (EffectList.SelectedItem as EffectItem)?.Id ?? _app.Settings.Effect;

        /// <summary>Lädt die Regler des gewählten Effekts in die rechte Spalte.</summary>
        private void ShowFxParams()
        {
            AppSettings s = _app.Settings;
            string id = SelectedEffectId;
            EffectParams p = s.GetEffectParams(id);
            EffectItem? item = _allEffects.FirstOrDefault(x => x.Id == id);
            int colors = Effects.Find(id)?.ColorCount ?? 3;                 // eigene Effekte: alle drei Farben

            bool old = _loading;
            _loading = true;
            FxTitle.Text = item?.Title ?? s.EffectTitle(id);
            FxCategory.Text = item?.Category ?? "";
            FxPreview.Background = item?.Brush ?? Brushes.Transparent;
            FxSpeedSlider.Value = p.Speed;
            FxIntensitySlider.Value = p.Intensity;
            FxColor1Box.Text = p.Color1;
            FxColor2Box.Text = p.Color2;
            FxColor3Box.Text = p.Color3;
            FxColor1Panel.Visibility = colors >= 1 ? Visibility.Visible : Visibility.Collapsed;
            FxColor2Panel.Visibility = colors >= 2 ? Visibility.Visible : Visibility.Collapsed;
            FxColor3Panel.Visibility = colors >= 3 ? Visibility.Visible : Visibility.Collapsed;
            _loading = old;
            UpdateSwatch(FxColor1Box, FxColor1Swatch);
            UpdateSwatch(FxColor2Box, FxColor2Swatch);
            UpdateSwatch(FxColor3Box, FxColor3Swatch);
        }

        /// <summary>Speichert die Regler für den gewählten Effekt (jeder Effekt merkt sich seine eigenen).</summary>
        private void FxChanged()
        {
            if (_loading) return;
            AppSettings s = _app.Settings;
            string id = SelectedEffectId;
            EffectParams p = s.GetEffectParams(id).Clone();
            p.Speed = (int)Math.Round(FxSpeedSlider.Value);
            p.Intensity = (int)Math.Round(FxIntensitySlider.Value);
            if (TryParseHex(FxColor1Box.Text, out Color c1)) p.Color1 = ToHex(c1);
            if (TryParseHex(FxColor2Box.Text, out Color c2)) p.Color2 = ToHex(c2);
            if (TryParseHex(FxColor3Box.Text, out Color c3)) p.Color3 = ToHex(c3);
            s.EffectSettings[id] = p;
            _app.ApplySettings(save: false);
            UpdateSwatch(FxColor1Box, FxColor1Swatch);
            UpdateSwatch(FxColor2Box, FxColor2Swatch);
            UpdateSwatch(FxColor3Box, FxColor3Swatch);
        }

        // ---------------------------------------------------------------- Eigene Effekte
        private CustomEffect? EditingCustom()
        {
            string id = (CustomBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            return _app.Settings.CustomEffects.Find(c => c.Id == id);
        }

        private void FillCustomEffects(string selectId)
        {
            CustomBox.Items.Clear();
            foreach (CustomEffect c in _app.Settings.CustomEffects)
                CustomBox.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
            SelectByTag(CustomBox, selectId);
            ShowCustomEffect();
        }

        private void ShowCustomEffect()
        {
            CustomEffect? c = EditingCustom();
            bool has = c != null;
            CustomNameBox.IsEnabled = ScriptBox.IsEnabled = CustomSaveButton.IsEnabled = CustomRunButton.IsEnabled = has;
            CustomDeleteButton.IsEnabled = BuildButton.IsEnabled = has;
            CustomNameBox.Text = c?.Name ?? "";
            ScriptBox.Text = c?.Script ?? "";
            if (!has) ScriptStatus.Text = "Noch kein eigener Effekt. Mit «Neu» legst du einen an.";
        }

        private void NewCustomEffect()
        {
            var list = _app.Settings.CustomEffects;
            var c = new CustomEffect
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Eigener Effekt " + (list.Count + 1),
                Script = LuaEffect.Template,
            };
            list.Add(c);
            _app.ApplySettings(save: true);
            FillCustomEffects(c.Id);
            FillEffects();
        }

        private void DeleteCustomEffect()
        {
            CustomEffect? c = EditingCustom();
            if (c == null) return;
            if (MessageBox.Show(this, "«" + c.Name + "» wirklich löschen?", "Eigener Effekt", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            AppSettings s = _app.Settings;
            string effectId = "custom:" + c.Id;
            s.CustomEffects.Remove(c);
            s.EffectSettings.Remove(effectId);
            if (s.Effect == effectId) s.Effect = "rainbow";
            foreach (AppRule r in s.Rules) if (r.Effect == effectId) r.Effect = "rainbow";
            _app.ApplySettings(save: true);
            FillCustomEffects("");
            FillEffects();
            RefreshRules();
            UpdateInfo();
        }

        private void SaveCustomEffect(bool run)
        {
            CustomEffect? c = EditingCustom();
            if (c == null) return;
            c.Name = string.IsNullOrWhiteSpace(CustomNameBox.Text) ? "Eigener Effekt" : CustomNameBox.Text.Trim();
            c.Script = ScriptBox.Text;
            AppSettings s = _app.Settings;
            if (run)
            {
                s.Effect = "custom:" + c.Id;
                s.Mode = "effect";
                s.Enabled = true;
            }
            _app.ApplySettings(save: true);
            FillCustomEffects(c.Id);
            FillEffects();
            RefreshRules();
            if (run) LoadFromSettings(); else UpdateInfo();
        }

        private void BuildFromKit()
        {
            if (EditingCustom() == null) return;
            if (ScriptBox.Text.Trim().Length > 0 &&
                MessageBox.Show(this, "Das Skript rechts wird durch das Baukasten-Skript ersetzt. Fortfahren?",
                                "Baukasten", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            ScriptBox.Text = LuaEffect.BuildScript(TagOf(BuildBaseBox, "gradient2"), TagOf(BuildMotionBox, "wave"),
                                                   BuildSparkleBox.IsChecked == true, (int)Math.Round(BuildWavesSlider.Value));
        }

        /// <summary>Live-Vorschau des Skripts im Editor (läuft nur, solange die Seite offen ist).</summary>
        private void UpdateEditorPreview()
        {
            if (!IsVisible || PageEditor.Visibility != Visibility.Visible) return;
            CustomEffect? c = EditingCustom();
            if (c == null) { ScriptPreview.Background = (Brush)FindResource("InputBrush"); return; }
            _editorLua.Render(ScriptBox.Text, _editorBuffer, 60, 0.1, _app.Settings.GetEffectParams("custom:" + c.Id));
            ScriptPreview.Background = StripBrush(_editorBuffer, 60);
            if (_editorLua.Error.Length > 0)
            {
                ScriptStatus.Text = "Fehler: " + _editorLua.Error;
                ScriptStatus.Foreground = Brushes.OrangeRed;
            }
            else
            {
                ScriptStatus.Text = ScriptBox.Text == c.Script ? "Kein Fehler · gespeichert" : "Kein Fehler · noch nicht gespeichert";
                ScriptStatus.Foreground = (Brush)FindResource("MutedBrush");
            }
        }

        // ---------------------------------------------------------------- Farbabgleich (SDR / HDR) und Assistent
        private bool EditingHdr => TagOf(ProfileBox, "sdr") == "hdr";

        private ColorProfile CurrentProfile()
        {
            AppSettings s = _app.Settings;
            s.Normalize();
            return EditingHdr ? s.Hdr! : s.Sdr!;
        }

        private void LoadProfile()
        {
            ColorProfile p = CurrentProfile();
            bool old = _loading;
            _loading = true;
            SatSlider.Value = p.Saturation;
            GammaSlider.Value = p.Gamma;
            GainRSlider.Value = p.GainRed;
            GainGSlider.Value = p.GainGreen;
            GainBSlider.Value = p.GainBlue;
            BlackLevelSlider.Value = p.BlackLevel;
            _loading = old;
        }

        private void StartCalibration()
        {
            TestBox.SelectedIndex = 0;
            // Aufnahme starten und kurz warten: erst dann ist bekannt, ob das Bild als HDR ankommt (SDR- oder HDR-Abgleich)
            _app.Engine.Calibration = 1;
            System.Threading.Thread.Sleep(600);
            var window = new CalibrationWindow(SelectedScreen()) { Owner = this };
            window.ShowDialog();
            _app.Engine.Calibration = 0;
            _app.ApplySettings(save: true);
            if (ProfileBox.SelectedIndex < 0) ProfileBox.SelectedIndex = 0;
            SelectByTag(ProfileBox, window.EditedHdr ? "hdr" : "sdr");
            LoadFromSettings();
        }

        private void FillRuleProcesses()
        {
            string selected = (RuleProcessBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";
            RuleProcessBox.Items.Clear();
            foreach (var app in ForegroundApp.RunningWindowApps())
                RuleProcessBox.Items.Add(new ComboBoxItem { Content = app.Name, Tag = app.Path });
            SelectRuleProcess(selected, "");
        }

        /// <summary>Wählt das Programm in der Liste; steht es nicht drin, wird es ergänzt.</summary>
        private void SelectRuleProcess(string name, string path)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (ComboBoxItem item in RuleProcessBox.Items)
            {
                if (string.Equals(item.Content as string, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(item.Tag as string) && path.Length > 0) item.Tag = path;
                    RuleProcessBox.SelectedItem = item;
                    return;
                }
            }
            var added = new ComboBoxItem { Content = name, Tag = path };
            RuleProcessBox.Items.Add(added);
            RuleProcessBox.SelectedItem = added;
        }

        private void BrowseRuleExe()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programme (*.exe)|*.exe", Title = "Programm für die Regel wählen" };
            if (dlg.ShowDialog(this) == true)
                SelectRuleProcess(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName), dlg.FileName);
        }

        private static void SelectByTag(ComboBox box, string tag)
        {
            if (box.Items.Count > 0) box.SelectedIndex = 0;
            foreach (object o in box.Items)
                if (o is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                    box.SelectedItem = item;
        }

        private static string TagOf(ComboBox box, string fallback) =>
            (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

        private void SelectPort(string port)
        {
            bool old = _loading;
            _loading = true;
            SelectByTag(PortBox, port);
            _loading = old;
        }

        // ---------------------------------------------------------------- Farben
        private static bool TryParseHex(string text, out Color color)
        {
            color = Colors.White;
            string h = text.Trim().TrimStart('#');
            if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        private static string ToHex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        private static void UpdateSwatch(TextBox box, Border swatch)
        {
            swatch.Background = TryParseHex(box.Text, out Color c) ? new SolidColorBrush(c) : Brushes.Transparent;
        }

        private void PickColor(TextBox box)
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
            if (TryParseHex(box.Text, out Color c)) dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                box.Text = ToHex(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        }

        // ---------------------------------------------------------------- Regeln pro Programm
        private static string RuleAction(AppRule r)
        {
            return r.Mode switch
            {
                "effect" => "Effekt · " + ((App)Application.Current).Settings.EffectTitle(r.Effect),
                "color" => "Feste Farbe · " + r.Color,
                "ambilight" => "Ambilight",
                _ => "LEDs aus",
            };
        }

        private void RefreshRules()
        {
            RulesList.Items.Clear();
            foreach (AppRule r in _app.Settings.Rules)
                RulesList.Items.Add(new RuleRow { Icon = UiNative.IconFor(r.Path), Title = r.Process, Action = RuleAction(r) });
            RulesEmpty.Visibility = _app.Settings.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateRulePanels()
        {
            string mode = TagOf(RuleModeBox, "off");
            RuleEffectPanel.Visibility = mode == "effect" ? Visibility.Visible : Visibility.Collapsed;
            RuleColorPanel.Visibility = mode == "color" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddRule()
        {
            var item = RuleProcessBox.SelectedItem as ComboBoxItem;
            string name = ForegroundApp.Normalize(item?.Content as string ?? "");
            if (name.Length == 0) { MessageBox.Show(this, "Bitte zuerst ein Programm wählen.", "Regel"); return; }

            var rules = _app.Settings.Rules;
            int i = rules.FindIndex(r => string.Equals(r.Process, name, StringComparison.OrdinalIgnoreCase));
            string path = item?.Tag as string ?? "";
            if (path.Length == 0 && i >= 0) path = rules[i].Path;        // Symbol der bestehenden Regel behalten

            var rule = new AppRule
            {
                Process = name,
                Path = path,
                Mode = TagOf(RuleModeBox, "off"),
                Effect = TagOf(RuleEffectBox, "rainbow"),
                Color = TryParseHex(RuleColorBox.Text, out Color c) ? ToHex(c) : "#FFFFFF",
            };
            if (i >= 0) rules[i] = rule; else rules.Add(rule);
            RefreshRules();
            _app.ApplySettings(save: true);
        }

        private void RemoveRule()
        {
            int i = RulesList.SelectedIndex;
            if (i < 0 || i >= _app.Settings.Rules.Count) return;
            _app.Settings.Rules.RemoveAt(i);
            RefreshRules();
            _app.ApplySettings(save: true);
        }

        private void ShowSelectedRule()
        {
            int i = RulesList.SelectedIndex;
            if (i < 0 || i >= _app.Settings.Rules.Count) return;
            AppRule r = _app.Settings.Rules[i];
            SelectRuleProcess(r.Process, r.Path);
            SelectByTag(RuleModeBox, r.Mode);
            SelectByTag(RuleEffectBox, r.Effect);
            RuleColorBox.Text = r.Color;
            UpdateRulePanels();
        }

        // ---------------------------------------------------------------- Einstellungen <-> Oberfläche
        public void LoadFromSettings()
        {
            AppSettings s = _app.Settings;
            _loading = true;

            if (!s.Enabled) ModeOff.IsChecked = true;
            else if (s.Mode == "effect") ModeEffect.IsChecked = true;
            else if (s.Mode == "color") ModeColor.IsChecked = true;
            else ModeAmbilight.IsChecked = true;

            EffectList.SelectedItem = EffectList.Items.OfType<EffectItem>().FirstOrDefault(x => x.Id == s.Effect);
            StaticColorBox.Text = s.StaticColor;
            if (ProfileBox.SelectedIndex < 0) ProfileBox.SelectedIndex = 0;
            SelectByTag(CaptureResBox, s.CaptureWidth <= 160 ? "160" : s.CaptureWidth >= 640 ? "640" : "320");
            AutostartBox.IsChecked = Autostart.IsEnabled();
            UpdateBox.IsChecked = s.CheckUpdates;
            RefreshRules();

            MonitorBox.SelectedIndex = MonitorBox.Items.Count > 0 ? 0 : -1;
            foreach (ComboBoxItem item in MonitorBox.Items)
                if ((string)item.Tag == s.MonitorDeviceName) MonitorBox.SelectedItem = item;
            if (string.IsNullOrEmpty(s.MonitorDeviceName))
            {
                var primary = System.Windows.Forms.Screen.PrimaryScreen;
                foreach (ComboBoxItem item in MonitorBox.Items)
                    if (primary != null && (string)item.Tag == primary.DeviceName) MonitorBox.SelectedItem = item;
            }

            DiagonalBox.Text = s.DiagonalInch.ToString("0.##", CultureInfo.InvariantCulture);
            DensityBox.Text = s.LedsPerMeter.ToString("0.##", CultureInfo.InvariantCulture);
            AutoBox.IsChecked = s.AutoLedCount;
            TopBox.Text = s.LedsTop.ToString();
            BottomBox.Text = s.LedsBottom.ToString();
            LeftBox.Text = s.LedsLeft.ToString();
            RightBox.Text = s.LedsRight.ToString();
            StartBox.SelectedIndex = (int)s.Start;
            ClockwiseBox.IsChecked = s.Clockwise;

            CropLeftSlider.Value = s.CropLeft;
            CropRightSlider.Value = s.CropRight;
            CropTopSlider.Value = s.CropTop;
            CropBottomSlider.Value = s.CropBottom;
            DepthSlider.Value = s.SampleDepth;
            SmoothSlider.Value = s.Smoothing;
            BrightSlider.Value = s.Brightness;
            FpsSlider.Value = s.TargetFps;
            BlackbarBox.IsChecked = s.BlackbarEnabled;
            BlackbarSlider.Value = s.BlackbarThreshold;
            LoadProfile();
            _loading = true;
            HdrBox.IsChecked = s.HdrCapture;
            PaperWhiteSlider.Value = s.HdrPaperWhite;
            SelectByTag(PortBox, s.Port);
            SelectByTag(CaptureBox, s.CaptureMethod);

            _loading = false;
            UpdateInfo();
            ShowFxParams();
        }

        private static double ParseDouble(string text, double fallback)
        {
            return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        private static int ParseCount(string text, int fallback)
        {
            return int.TryParse(text.Trim(), out int v) ? Math.Clamp(v, 0, 500) : fallback;
        }

        private System.Windows.Forms.Screen SelectedScreen()
        {
            string name = (MonitorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            var all = System.Windows.Forms.Screen.AllScreens;
            return all.FirstOrDefault(x => x.DeviceName == name) ?? System.Windows.Forms.Screen.PrimaryScreen ?? all[0];
        }

        private void UiChanged()
        {
            if (_loading) return;
            AppSettings s = _app.Settings;

            // Kachel "Aus" schaltet nur aus; die zuletzt gewählte Betriebsart bleibt gespeichert
            if (ModeOff.IsChecked == true) s.Enabled = false;
            else
            {
                s.Enabled = true;
                s.Mode = ModeEffect.IsChecked == true ? "effect" : ModeColor.IsChecked == true ? "color" : "ambilight";
            }
            s.Effect = (EffectList.SelectedItem as EffectItem)?.Id ?? s.Effect;
            if (TryParseHex(StaticColorBox.Text, out Color sc)) s.StaticColor = ToHex(sc);
            s.CaptureWidth = int.Parse(TagOf(CaptureResBox, "320"), CultureInfo.InvariantCulture);

            s.MonitorDeviceName = (MonitorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            s.DiagonalInch = ParseDouble(DiagonalBox.Text, s.DiagonalInch);
            s.LedsPerMeter = ParseDouble(DensityBox.Text, s.LedsPerMeter);
            s.AutoLedCount = AutoBox.IsChecked == true;

            if (s.AutoLedCount)
            {
                var b = SelectedScreen().Bounds;
                var calc = LedLayout.Calculate(s.DiagonalInch, b.Width, b.Height, s.LedsPerMeter);
                if (calc.horizontal > 0 && calc.vertical > 0)
                {
                    s.LedsTop = s.LedsBottom = calc.horizontal;
                    s.LedsLeft = s.LedsRight = calc.vertical;
                    _loading = true;
                    TopBox.Text = BottomBox.Text = calc.horizontal.ToString();
                    LeftBox.Text = RightBox.Text = calc.vertical.ToString();
                    _loading = false;
                }
            }
            else
            {
                s.LedsTop = ParseCount(TopBox.Text, s.LedsTop);
                s.LedsBottom = ParseCount(BottomBox.Text, s.LedsBottom);
                s.LedsLeft = ParseCount(LeftBox.Text, s.LedsLeft);
                s.LedsRight = ParseCount(RightBox.Text, s.LedsRight);
            }

            s.Start = (StartCorner)Math.Max(0, StartBox.SelectedIndex);
            s.Clockwise = ClockwiseBox.IsChecked == true;
            s.CropLeft = CropLeftSlider.Value;
            s.CropRight = CropRightSlider.Value;
            s.CropTop = CropTopSlider.Value;
            s.CropBottom = CropBottomSlider.Value;
            s.SampleDepth = DepthSlider.Value;
            s.Smoothing = SmoothSlider.Value;
            s.Brightness = (int)Math.Round(BrightSlider.Value);
            s.TargetFps = (int)Math.Round(FpsSlider.Value);
            s.BlackbarEnabled = BlackbarBox.IsChecked == true;
            s.BlackbarThreshold = (int)Math.Round(BlackbarSlider.Value);
            ColorProfile prof = CurrentProfile();
            prof.Saturation = Math.Round(SatSlider.Value);
            prof.Gamma = Math.Round(GammaSlider.Value, 2);
            prof.GainRed = Math.Round(GainRSlider.Value);
            prof.GainGreen = Math.Round(GainGSlider.Value);
            prof.GainBlue = Math.Round(GainBSlider.Value);
            prof.BlackLevel = Math.Round(BlackLevelSlider.Value);
            s.HdrCapture = HdrBox.IsChecked == true;
            s.HdrPaperWhite = Math.Round(PaperWhiteSlider.Value);
            s.Port = TagOf(PortBox, "auto");
            s.CaptureMethod = TagOf(CaptureBox, "auto");

            _app.ApplySettings(save: false);
            SaveInfo.Text = "";
            UpdateInfo();
        }

        private void UpdateInfo()
        {
            AppSettings s = _app.Settings;
            bool auto = s.AutoLedCount;
            TopBox.IsEnabled = BottomBox.IsEnabled = LeftBox.IsEnabled = RightBox.IsEnabled = !auto;

            var b = SelectedScreen().Bounds;
            var calc = LedLayout.Calculate(s.DiagonalInch, b.Width, b.Height, s.LedsPerMeter);
            SizeInfo.Text = calc.widthMm > 0
                ? string.Format(CultureInfo.InvariantCulture,
                      "Bildfläche ca. {0:0} × {1:0} mm  →  Vorschlag: {2} LEDs oben und unten, {3} links und rechts",
                      calc.widthMm, calc.heightMm, calc.horizontal, calc.vertical)
                : "";

            // Regler nur zeigen, wenn sie zur Betriebsart passen
            bool effect = s.Enabled && s.Mode == "effect";
            bool color = s.Enabled && s.Mode == "color";
            PanelEffect.Visibility = effect ? Visibility.Visible : Visibility.Collapsed;
            PanelColor.Visibility = color ? Visibility.Visible : Visibility.Collapsed;
            EffectNameText.Text = s.EffectTitle(s.Effect);
            ModeEffectText.Text = "Aktuell: " + s.EffectTitle(s.Effect);
            UpdateSwatch(StaticColorBox, StaticColorSwatch);
            if (TryParseHex(s.StaticColor, out Color fixedColor)) ModeColorStrip.Background = new SolidColorBrush(fixedColor);

            int total = s.TotalLeds;
            if (total >= 1 && total <= MaxLeds)
            {
                TotalInfo.Text = total + " LEDs";
                TotalInfo.Foreground = (Brush)FindResource("TextBrush");
            }
            else
            {
                TotalInfo.Text = total + " LEDs – möglich sind 1 bis " + MaxLeds;
                TotalInfo.Foreground = Brushes.OrangeRed;
            }
        }

        // ---------------------------------------------------------------- Status (läuft immer)
        private void UpdateStatus()
        {
            if (!IsVisible) return;
            AmbilightEngine engine = _app.Engine;

            StatusText.Text = engine.Status;
            FpsText.Text = engine.Fps > 0 ? engine.Fps.ToString("0", CultureInfo.InvariantCulture) + " Bilder/s" : "–";
            CpuText.Text = _usage.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %";
            GpuText.Text = _usage.GpuPercent.HasValue ? _usage.GpuPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "nicht verfügbar";
            RamText.Text = _usage.RamMb.ToString("0", CultureInfo.InvariantCulture) + " MB";

            bool ok = engine.DeviceOk;
            ConnDot.Fill = ok ? (Brush)FindResource("GreenBrush") : (Brush)FindResource("MutedBrush");
            ConnText.Text = ok ? "Verbunden · " + engine.PortName : "Nicht verbunden";
            ConnSub.Text = _app.Settings.TotalLeds + " LEDs" + (engine.FirmwareId.Length > 0 ? "\n" + engine.FirmwareId : "");

            DeviceInfo.Text = (ok ? "Verbunden an " + engine.PortName : "Kein Gerät verbunden")
                              + (engine.FirmwareId.Length > 0 ? "\nFirmware: " + engine.FirmwareId : "\nFirmware: keine Kennung erhalten (ältere Firmware oder keine Antwort)");
            UsageInfo.Text = "Prozessor        " + CpuText.Text
                             + "\nGrafikkarte      " + GpuText.Text
                             + "\nArbeitsspeicher  " + RamText.Text
                             + "\nBildrate         " + FpsText.Text
                             + "\nAufnahme         " + (engine.CaptureName.Length > 0 ? engine.CaptureName : "–");

            // --- Meldungen der Firmware (ab v5.0) ---
            DeviceTelemetry tel = engine.Telemetry;
            if (tel.Valid)
            {
                TelOnText.Text = tel.On ? "Eingeschaltet" : "Ausgeschaltet";
                TelDimText.Text = (tel.DimIndex + 1) + " von 3 · " + (int)Math.Round(tel.DimValue / 2.55) + " %";
                TelTempText.Text = tel.TempC.ToString("0.0", CultureInfo.InvariantCulture) + " °C";
                TelPowerText.Text = (tel.PowerMw / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " W";
                TelHint.Text = tel.On ? "Die Leistung ist aus den Farbwerten hochgerechnet, nicht gemessen."
                                      : "Am Gerät ausgeschaltet: die LEDs bleiben dunkel, bis du hier oder am Taster wieder einschaltest.";
            }
            else
            {
                TelOnText.Text = TelDimText.Text = TelTempText.Text = TelPowerText.Text = "–";
                TelHint.Text = "Keine Meldungen vom Gerät. Dafür braucht das ESP32 die Firmware v5.0 (liegt im Ordner firmware).";
            }
            DeviceToggleButton.IsEnabled = DeviceDimButton.IsEnabled = tel.Valid;
            if (!tel.On && tel.Valid && ok) StatusText.Text = engine.Status + "  –  am Gerät ausgeschaltet (Taster)";
            FxError.Text = engine.ScriptError.Length > 0 ? "Skriptfehler: " + engine.ScriptError : "";

            if (PageDevice.Visibility == Visibility.Visible)
            {
                List<LogEntry> log = engine.GetLog();
                string stamp = log.Count + "|" + (log.Count > 0 ? log[0].Time.Ticks.ToString() : "");
                if (stamp != _logStamp)
                {
                    _logStamp = stamp;
                    LogList.ItemsSource = log.Take(40).ToList();
                }
            }
            ProfileInfo.Text = "Gerade in Gebrauch: " + (engine.IsHdr ? "HDR-Abgleich" : "Abgleich für normales Bild");

            HdrInfo.Text = engine.IsHdr
                ? "HDR ist aktiv: das Bild wird als HDR aufgenommen und umgerechnet."
                : "Zurzeit kein HDR-Bild (HDR in Windows aus, Aufnahme über GDI oder Option ausgeschaltet).";
        }

        // ---------------------------------------------------------------- Vorschau (nur ausgeklappt)
        private void UpdatePreview()
        {
            if (!IsVisible || PreviewToggle.IsChecked != true) return;
            AppSettings s = _app.Settings;
            AmbilightEngine engine = _app.Engine;

            double cw = PreviewCanvas.ActualWidth, ch = PreviewCanvas.ActualHeight;
            if (cw < 80 || ch < 80) return;

            var b = SelectedScreen().Bounds;
            double aspect = (double)b.Width / Math.Max(1, b.Height);

            // Bildschirm mittig einpassen, aussen bleibt Platz für die LED-Kästchen
            const double led = 12;                       // Dicke der LED-Kästchen
            double margin = 10 + led;
            double w = cw - 2 * margin, h = w / aspect;
            if (h > ch - 2 * margin) { h = ch - 2 * margin; w = h * aspect; }
            if (w < 20 || h < 20) return;
            double ox = (cw - w) / 2, oy = (ch - h) / 2;

            PreviewCanvas.Children.Clear();

            // --- aufgenommenes Bild ---
            byte[] frame = engine.GetLastFrame(out int fw, out int fh);
            if (fw > 0 && fh > 0 && frame.Length >= fw * fh * 4)
            {
                if (_previewBitmap == null || _previewBitmap.PixelWidth != fw || _previewBitmap.PixelHeight != fh)
                {
                    _previewBitmap = new WriteableBitmap(fw, fh, 96, 96, PixelFormats.Bgr32, null);
                    _previewImage.Source = _previewBitmap;
                }
                _previewBitmap.WritePixels(new Int32Rect(0, 0, fw, fh), frame, fw * 4, 0);
            }
            var back = new Rectangle { Width = w, Height = h, Fill = Brushes.Black };
            Canvas.SetLeft(back, ox);
            Canvas.SetTop(back, oy);
            PreviewCanvas.Children.Add(back);
            _previewImage.Width = w;
            _previewImage.Height = h;
            Canvas.SetLeft(_previewImage, ox);
            Canvas.SetTop(_previewImage, oy);
            PreviewCanvas.Children.Add(_previewImage);

            // --- gemessener Bereich (eingestellter Rand oder erkannter schwarzer Balken) ---
            engine.GetEffectiveCrop(out double cl, out double cr, out double ct, out double cb);
            if (!s.Enabled) { cl = s.CropLeft / 100.0; cr = s.CropRight / 100.0; ct = s.CropTop / 100.0; cb = s.CropBottom / 100.0; }
            if (cl + cr + ct + cb > 0.001)
            {
                var crop = new Rectangle
                {
                    Width = Math.Max(1, (1 - cl - cr) * w),
                    Height = Math.Max(1, (1 - ct - cb) * h),
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                };
                Canvas.SetLeft(crop, ox + cl * w);
                Canvas.SetTop(crop, oy + ct * h);
                PreviewCanvas.Children.Add(crop);
            }

            // --- LED-Kästchen aussen um den Bildschirm ---
            List<LedRegion> regions = LedLayout.Build(s, aspect);
            byte[] colors = engine.GetLastColors();
            for (int i = 0; i < regions.Count; i++)
            {
                LedRegion r = regions[i];
                Color c = Colors.Black;
                if (colors.Length >= (i + 1) * 3) c = Color.FromRgb(colors[i * 3], colors[i * 3 + 1], colors[i * 3 + 2]);

                double x, y, rw, rh;
                switch (r.Side)
                {
                    case 0: x = ox + r.X0 * w; y = oy - led - 2; rw = (r.X1 - r.X0) * w; rh = led; break;      // oben
                    case 2: x = ox + r.X0 * w; y = oy + h + 2; rw = (r.X1 - r.X0) * w; rh = led; break;        // unten
                    case 1: x = ox + w + 2; y = oy + r.Y0 * h; rw = led; rh = (r.Y1 - r.Y0) * h; break;        // rechts
                    default: x = ox - led - 2; y = oy + r.Y0 * h; rw = led; rh = (r.Y1 - r.Y0) * h; break;     // links
                }
                var rect = new Rectangle
                {
                    Width = Math.Max(1, rw),
                    Height = Math.Max(1, rh),
                    Fill = new SolidColorBrush(c),
                    Stroke = i == 0 ? Brushes.White : Brushes.Black,
                    StrokeThickness = i == 0 ? 2 : 0.5,
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                PreviewCanvas.Children.Add(rect);
            }
        }
    }
}
