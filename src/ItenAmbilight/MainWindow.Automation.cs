using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ItenAmbilight
{
    /// <summary>Seiten «Automatik» (Zeitplan, Szenen) und «Steuerung» (Tastenkürzel, Stream Deck).</summary>
    public partial class MainWindow
    {
        private sealed class HotkeyRow
        {
            public string Action = "";
            public TextBox Box = null!;
            public Button Clear = null!;
            public TextBlock Note = null!;
        }

        private readonly List<HotkeyRow> _hotkeyRows = new List<HotkeyRow>();

        private void InitAutomation()
        {
            // ---------- Zeitplan ----------
            ScheduleBox.Click += (_, _) =>
            {
                _app.Settings.ScheduleEnabled = ScheduleBox.IsChecked == true;
                _app.ApplySettings(save: true);
            };
            ScheduleActionBox.SelectionChanged += (_, _) => UpdateSchedulePanels();
            ScheduleAddButton.Click += (_, _) => AddSchedule(replaceSelected: false);
            ScheduleChangeButton.Click += (_, _) => AddSchedule(replaceSelected: true);
            ScheduleRemoveButton.Click += (_, _) => RemoveSchedule();
            ScheduleList.SelectionChanged += (_, _) => ShowSelectedSchedule();

            // ---------- Szenen ----------
            LockModeBox.SelectionChanged += (_, _) => SceneChanged();
            LockEffectBox.SelectionChanged += (_, _) => SceneChanged();
            LockColorBox.TextChanged += (_, _) => { UpdateSwatch(LockColorBox, LockColorSwatch); SceneChanged(); };
            LockColorButton.Click += (_, _) => PickColor(LockColorBox);
            LockBrightSlider.ValueChanged += (_, _) => SceneChanged();
            SleepModeBox.SelectionChanged += (_, _) => SceneChanged();
            SleepEffectBox.SelectionChanged += (_, _) => SceneChanged();
            SleepColorBox.TextChanged += (_, _) => { UpdateSwatch(SleepColorBox, SleepColorSwatch); SceneChanged(); };
            SleepColorButton.Click += (_, _) => PickColor(SleepColorBox);
            SleepBrightSlider.ValueChanged += (_, _) => SceneChanged();

            // ---------- Tastenkürzel ----------
            BuildHotkeyRows();

            // ---------- Stream Deck / Kommandozeile ----------
            string exe = "\"" + (Environment.ProcessPath ?? "ItenAmbilight.exe") + "\"";
            CommandBox.Text = string.Join("\n", new[]
            {
                exe + " --toggle",
                exe + " --on",
                exe + " --off",
                exe + " --mode ambilight",
                exe + " --mode effect",
                exe + " --mode color",
                exe + " --effect next",
                exe + " --effect prev",
                exe + " --effect fire",
                exe + " --brightness +10",
                exe + " --brightness -10",
                exe + " --brightness 60",
                exe + " --color #FF8800",
                exe + " --show",
                exe + " --quit",
            });
            CommandCopyButton.Click += (_, _) => CopyText(CommandBox.Text, "Zeilen kopiert.");
            CommandEffectsButton.Click += (_, _) =>
            {
                AppSettings s = _app.Settings;
                CopyText(string.Join("\n", AppCommands.EffectIds(s).Select(id => id + "    " + s.EffectTitle(id))),
                         "Kennungen kopiert (links die Kennung für --effect, rechts der Name).");
            };
        }

        private void CopyText(string text, string info)
        {
            try { Clipboard.SetText(text); CommandInfo.Text = info; }
            catch { CommandInfo.Text = "Die Zwischenablage ist gerade belegt – bitte nochmals versuchen."; }
        }

        /// <summary>Lädt Zeitplan und Szenen aus den Einstellungen. Wird von LoadFromSettings aufgerufen (_loading ist gesetzt).</summary>
        private void LoadAutomation()
        {
            AppSettings s = _app.Settings;
            ScheduleBox.IsChecked = s.ScheduleEnabled;
            RefreshSchedule();
            UpdateSchedulePanels();

            SelectByTag(LockModeBox, s.SceneLock.Mode);
            SelectByTag(LockEffectBox, s.SceneLock.Effect);
            LockColorBox.Text = s.SceneLock.Color;
            LockBrightSlider.Value = s.SceneLock.Brightness;
            SelectByTag(SleepModeBox, s.SceneSleep.Mode);
            SelectByTag(SleepEffectBox, s.SceneSleep.Effect);
            SleepColorBox.Text = s.SceneSleep.Color;
            SleepBrightSlider.Value = s.SceneSleep.Brightness;
            UpdateSwatch(LockColorBox, LockColorSwatch);
            UpdateSwatch(SleepColorBox, SleepColorSwatch);
            UpdateScenePanels();
            RefreshHotkeyRows();
        }

        /// <summary>Füllt die Effekt-Auswahl der Szenen (nach jeder Änderung an den eigenen Effekten).</summary>
        private void FillSceneEffects()
        {
            bool old = _loading;
            _loading = true;
            foreach (ComboBox box in new[] { LockEffectBox, SleepEffectBox })
            {
                string current = ReferenceEquals(box, LockEffectBox) ? _app.Settings.SceneLock.Effect : _app.Settings.SceneSleep.Effect;
                box.Items.Clear();
                foreach (EffectItem e in _allEffects)
                    box.Items.Add(new ComboBoxItem { Content = e.Title + "  ·  " + e.Category, Tag = e.Id });
                SelectByTag(box, current);
            }
            _loading = old;
        }

        // ---------------------------------------------------------------- Szenen
        private void UpdateScenePanels()
        {
            SetSceneRows(TagOf(LockModeBox, "off"), LockColorLabel, LockColorPanel, LockEffectLabel, LockEffectBox,
                         LockBrightLabel, LockBrightSlider, LockBrightText);
            SetSceneRows(TagOf(SleepModeBox, "off"), SleepColorLabel, SleepColorPanel, SleepEffectLabel, SleepEffectBox,
                         SleepBrightLabel, SleepBrightSlider, SleepBrightText);
        }

        private static void SetSceneRows(string mode, UIElement colorLabel, UIElement colorPanel, UIElement effectLabel, UIElement effectBox,
                                         UIElement brightLabel, UIElement brightSlider, UIElement brightText)
        {
            Visibility color = mode == "color" ? Visibility.Visible : Visibility.Collapsed;
            Visibility effect = mode == "effect" ? Visibility.Visible : Visibility.Collapsed;
            Visibility bright = mode == "off" ? Visibility.Collapsed : Visibility.Visible;
            colorLabel.Visibility = colorPanel.Visibility = color;
            effectLabel.Visibility = effectBox.Visibility = effect;
            brightLabel.Visibility = brightSlider.Visibility = brightText.Visibility = bright;
        }

        private void SceneChanged()
        {
            if (_loading) return;
            AppSettings s = _app.Settings;
            ReadScene(s.SceneLock, LockModeBox, LockEffectBox, LockColorBox, LockBrightSlider);
            ReadScene(s.SceneSleep, SleepModeBox, SleepEffectBox, SleepColorBox, SleepBrightSlider);
            UpdateScenePanels();
            _app.ApplySettings(save: false);          // gespeichert wird beim Schliessen des Fensters
        }

        private static void ReadScene(Scene scene, ComboBox modeBox, ComboBox effectBox, TextBox colorBox, Slider bright)
        {
            scene.Mode = TagOf(modeBox, "off");
            scene.Effect = TagOf(effectBox, scene.Effect);
            if (TryParseHex(colorBox.Text, out Color c)) scene.Color = ToHex(c);
            scene.Brightness = (int)Math.Round(bright.Value);
        }

        // ---------------------------------------------------------------- Zeitplan
        private void RefreshSchedule()
        {
            ScheduleList.Items.Clear();
            foreach (ScheduleEntry e in _app.Settings.Schedule) ScheduleList.Items.Add(Automation.Describe(e));
            ScheduleEmpty.Visibility = _app.Settings.Schedule.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateSchedulePanels()
        {
            Visibility v = TagOf(ScheduleActionBox, "off") == "brightness" ? Visibility.Visible : Visibility.Hidden;
            ScheduleBrightSlider.Visibility = ScheduleBrightText.Visibility = v;
        }

        private void AddSchedule(bool replaceSelected)
        {
            List<ScheduleEntry> list = _app.Settings.Schedule;
            int i = ScheduleList.SelectedIndex;
            if (replaceSelected && (i < 0 || i >= list.Count))
            {
                ScheduleInfo.Text = "Bitte zuerst in der Liste eine Zeile anklicken.";
                return;
            }
            if (!Automation.TryParseTime(ScheduleFromBox.Text, out int from) || !Automation.TryParseTime(ScheduleToBox.Text, out int to))
            {
                ScheduleInfo.Text = "Bitte die Zeiten als Stunde:Minute eingeben, z. B. 22:00 oder 7:30.";
                return;
            }
            if (from == to)
            {
                ScheduleInfo.Text = "Anfang und Ende sind gleich – so würde das Zeitfenster nie gelten.";
                return;
            }
            var entry = new ScheduleEntry
            {
                From = Automation.FormatTime(from),
                To = Automation.FormatTime(to),
                Action = TagOf(ScheduleActionBox, "off"),
                Brightness = (int)Math.Round(ScheduleBrightSlider.Value),
            };
            if (replaceSelected) list[i] = entry; else list.Add(entry);
            RefreshSchedule();
            ScheduleList.SelectedIndex = replaceSelected ? i : list.Count - 1;
            ScheduleInfo.Text = _app.Settings.ScheduleEnabled ? "" : "Gespeichert. Der Zeitplan ist noch ausgeschaltet (Schalter oben).";
            _app.ApplySettings(save: true);
        }

        private void RemoveSchedule()
        {
            int i = ScheduleList.SelectedIndex;
            if (i < 0 || i >= _app.Settings.Schedule.Count) return;
            _app.Settings.Schedule.RemoveAt(i);
            RefreshSchedule();
            ScheduleInfo.Text = "";
            _app.ApplySettings(save: true);
        }

        private void ShowSelectedSchedule()
        {
            int i = ScheduleList.SelectedIndex;
            if (i < 0 || i >= _app.Settings.Schedule.Count) return;
            ScheduleEntry e = _app.Settings.Schedule[i];
            ScheduleFromBox.Text = e.From;
            ScheduleToBox.Text = e.To;
            SelectByTag(ScheduleActionBox, e.Action);
            ScheduleBrightSlider.Value = e.Brightness;
            UpdateSchedulePanels();
        }

        // ---------------------------------------------------------------- Tastenkürzel
        private void BuildHotkeyRows()
        {
            HotkeyPanel.Children.Clear();
            _hotkeyRows.Clear();
            foreach ((string id, string title) in AppCommands.HotkeyActions)
            {
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock { Text = title, Style = (Style)FindResource("Label") };
                var box = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand, ToolTip = "Anklicken und Tasten drücken" };
                var clear = new Button { Content = "Löschen", Margin = new Thickness(8, 0, 0, 0) };
                var note = new TextBlock { Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                Grid.SetColumn(box, 1); Grid.SetColumn(clear, 2); Grid.SetColumn(note, 3);
                grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(clear); grid.Children.Add(note);
                HotkeyPanel.Children.Add(grid);

                var row = new HotkeyRow { Action = id, Box = box, Clear = clear, Note = note };
                _hotkeyRows.Add(row);

                box.GotKeyboardFocus += (_, _) =>
                {
                    _app.Hooks?.Suspend(true);                 // bestehende Kürzel dürfen jetzt nicht auslösen
                    box.Text = "Tasten drücken …";
                    HotkeyInfo.Text = "";
                };
                box.LostKeyboardFocus += (_, _) =>
                {
                    _app.Hooks?.Suspend(false);
                    Dispatcher.BeginInvoke(new Action(RefreshHotkeyRows));    // erst wenn der Fokus wirklich gewechselt hat
                };
                box.PreviewKeyDown += (_, e) => HotkeyKeyDown(row, e);
                clear.Click += (_, _) =>
                {
                    _app.Settings.Hotkeys.Remove(row.Action);
                    _app.ApplySettings(save: true);
                    _app.ApplyHotkeys();
                    RefreshHotkeyRows();
                };
            }
        }

        private void HotkeyKeyDown(HotkeyRow row, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            ModifierKeys mods = Keyboard.Modifiers;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;

            if (key == Key.Tab && (mods & ~ModifierKeys.Shift) == 0) return;      // mit Tab weiter zum nächsten Feld
            e.Handled = true;
            if (key == Key.Escape && mods == ModifierKeys.None) { row.Clear.Focus(); return; }

            string text = SystemHooks.Format(mods, key);
            if (text.Length == 0)
            {
                // nur Strg/Alt/Shift gedrückt: weiter warten. Normale Taste ohne Strg/Alt/Win: Hinweis.
                bool onlyModifier = key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt
                                    || key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;
                if (!onlyModifier) HotkeyInfo.Text = "Bitte mit Strg, Alt oder Win kombinieren. Die Tasten F1 bis F24 gehen auch allein.";
                return;
            }

            // Dieselbe Kombination kann nur eine Aktion auslösen: bei der anderen entfernen
            Dictionary<string, string> hotkeys = _app.Settings.Hotkeys;
            foreach (string other in hotkeys.Where(p => p.Key != row.Action && string.Equals(p.Value, text, StringComparison.OrdinalIgnoreCase))
                                            .Select(p => p.Key).ToList())
                hotkeys.Remove(other);
            hotkeys[row.Action] = text;
            _app.ApplySettings(save: true);
            _app.ApplyHotkeys();
            row.Clear.Focus();                    // Feld verlassen -> Kürzel werden angemeldet und die Anzeige aktualisiert
        }

        private void RefreshHotkeyRows()
        {
            List<string> failed = _app.Hooks?.Failed ?? new List<string>();
            bool any = false;
            foreach (HotkeyRow row in _hotkeyRows)
            {
                if (row.Box.IsKeyboardFocused) continue;                          // wird gerade eingegeben
                _app.Settings.Hotkeys.TryGetValue(row.Action, out string? stored);
                row.Box.Text = string.IsNullOrWhiteSpace(stored) ? "nicht belegt" : SystemHooks.Display(stored);
                row.Box.Foreground = (Brush)FindResource(string.IsNullOrWhiteSpace(stored) ? "MutedBrush" : "TextBrush");
                bool bad = failed.Contains(row.Action);
                row.Note.Text = bad ? "nicht möglich – von Windows oder einem anderen Programm belegt" : "";
                row.Note.Foreground = Brushes.OrangeRed;
                any |= bad;
            }
            if (any) HotkeyInfo.Text = "Mindestens ein Tastenkürzel ist schon vergeben. Bitte eine andere Kombination wählen.";
        }
    }
}
