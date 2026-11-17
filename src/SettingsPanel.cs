using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SmartBuffer
{
    // the settings tabs, opened from the gear
    public sealed class SettingsPanel : Panel
    {
        public const int PageWidth = 600;
        public const int PageHeight = PanelTop + PanelHeight + FooterHeight;

        private const int Inset = 24;
        private const int TabTop = 14;
        private const int TabHeight = 36;
        private const int PanelTop = 64;
        private const int PanelHeight = 548;
        private const int FooterHeight = 64;
        private const int CardPad = 16;
        private const int ControlWidth = 230;
        private const int SectionGap = 14;
        private const int HeaderHeight = 26;

        // bitrates the quality slider stops at, finer where the differences are easiest to see
        private static readonly int[] QualitySteps = Enumerable.Range(5, 26)
            .Concat(Enumerable.Range(0, 10).Select(i => 32 + i * 2))
            .Concat(Enumerable.Range(0, 10).Select(i => 55 + i * 5))
            .ToArray();

        private readonly Context ctx;
        private readonly Settings draft;
        private readonly Companion owner;
        private readonly Action<bool> busyChanged;
        private readonly Action<bool> dialogChanged;
        private readonly Action<Settings> applied;
        private readonly Label messageLabel;
        private readonly ActionButton applyButton;
        private readonly List<LibraryTab> tabs = new List<LibraryTab>();
        private readonly List<Panel> pages = new List<Panel>();

        private Settings saved;
        private bool busy;
        private Label captureHelp;
        private FlatCombo monitorCombo;

        public SettingsPanel(Context ctx, Settings draft, Companion owner, Action<bool> busyChanged, Action<bool> dialogChanged, Action<Settings> applied)
        {
            this.ctx = ctx;
            this.draft = draft;
            this.owner = owner;
            this.busyChanged = busyChanged;
            this.dialogChanged = dialogChanged;
            this.applied = applied;
            saved = Settings.Load(ctx);
            BackColor = Theme.Background;
            Font = Ui.Text(13);
            DoubleBuffered = true;

            AddTab("Clips", BuildClipsTab());
            AddTab("Video", BuildVideoTab());
            AddTab("Audio", BuildAudioTab());
            AddTab("Hotkeys", BuildHotkeysTab());
            AddTab("Startup", BuildStartupTab());
            SelectTab(0);

            int footer = Ui.S(PanelTop + PanelHeight + 12);
            messageLabel = NewLabel("", new Point(Ui.S(Inset), footer), new Size(Ui.S(PageWidth - Inset * 2 - 130), Ui.S(40)), Theme.Muted, 12);
            messageLabel.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(messageLabel);

            applyButton = new ActionButton("Apply", null, true)
            {
                Location = new Point(Ui.S(PageWidth - Inset - 110), footer),
                Size = new Size(Ui.S(110), Ui.S(38)),
            };
            applyButton.Click += (_, __) => ApplyChanges();
            Controls.Add(applyButton);
            UpdateDirty();
        }

        // the tabs sit in one rounded track, like the clips/recordings switch on the dock
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Ui.FillRound(e.Graphics, Theme.Panel, new Rectangle(Ui.S(Inset), Ui.S(TabTop) - Ui.S(4), Ui.S(PageWidth - Inset * 2), Ui.S(TabHeight + 8)), Ui.S(10));
            using (var pen = new Pen(Theme.Border))
            {
                int line = Ui.S(PanelTop + PanelHeight + 2);
                e.Graphics.DrawLine(pen, 0, line, Width, line);
            }
        }

        private Panel BuildClipsTab()
        {
            Panel panel = NewPage();
            int y = 0;

            Card saving = AddCard(panel, ref y, "Saving");
            AddRow(saving, "Clip length", "How far back a saved clip goes.", NewCombo(new[]
            {
                new Choice("30 seconds", 30),
                new Choice("1 minute", 60),
                new Choice("2 minutes", 120),
                new Choice("3 minutes", 180),
                new Choice("5 minutes", 300),
                new Choice("10 minutes", 600),
            }, draft.ClipSeconds, value => draft.ClipSeconds = (int)value));
            AddFolderRow(saving, "Clips folder", "Where should clips be saved?", draft.ClipsDir, value => draft.ClipsDir = value);
            AddFolderRow(saving, "Recordings folder", "Where should recordings be saved?", draft.RecordingsDir, value => draft.RecordingsDir = value);
            EndCard(saving, ref y);

            Card after = AddCard(panel, ref y, "When something saves");
            AddToggleRow(after, "Play a sound", "Handy in fullscreen games, where pop-ups can't show.", draft.SaveSound, value => draft.SaveSound = value);
            AddToggleRow(after, "Show a pop-up", "Click it to open what was saved.", draft.SaveNotification, value => draft.SaveNotification = value);
            AddToggleRow(after, "Sort into game folders", "Anything without a game goes in Desktop.", draft.SortByGame, value => draft.SortByGame = value);
            EndCard(after, ref y);

            Card storage = AddCard(panel, ref y, "Storage");
            AddLimitRow(storage);
            EndCard(storage, ref y);
            return panel;
        }

        private Panel BuildVideoTab()
        {
            Panel panel = NewPage();
            int y = 0;

            Card capture = AddCard(panel, ref y, "Capture");
            captureHelp = AddRow(capture, "Capture", " \n ", NewCombo(new[]
            {
                new Choice("Game capture", Settings.CaptureGame),
                new Choice("Desktop capture", Settings.CaptureDesktop),
            }, draft.Capture, value =>
            {
                draft.Capture = (string)value;
                UpdateCaptureHelp();
            }));

            List<DisplayInfo> displays = SystemInfo.AllDisplays();
            DisplayInfo primary = displays.FirstOrDefault(d => d.IsPrimary);
            Choice[] monitors = displays
                .Select(d => new Choice($"Display {d.Number} ({d.Width}x{d.Height}){(d.IsPrimary ? ", main" : "")}", d.MonitorId))
                .ToArray();
            string currentMonitor = string.IsNullOrEmpty(draft.MonitorId) ? primary?.MonitorId : draft.MonitorId;
            monitorCombo = NewCombo(monitors, currentMonitor, value => draft.MonitorId = (string)value);
            AddRow(capture, "Monitor", "Only used by desktop capture.", monitorCombo);
            EndCard(capture, ref y);

            Card quality = AddCard(panel, ref y, "Quality");
            var slider = new StepSlider(QualitySteps, mbps => mbps + " Mbps") { Value = draft.Quality / 1000 };
            Label qualityHelp = AddRow(quality, "Bitrate", QualityText(slider.Value), slider);
            slider.Changed += () =>
            {
                draft.Quality = slider.Value * 1000;
                qualityHelp.Text = QualityText(slider.Value);
                UpdateDirty();
            };

            AddRow(quality, "Frame rate", null, NewCombo(new[]
            {
                new Choice("30 fps", 30),
                new Choice("60 fps", 60),
                new Choice("120 fps", 120),
                new Choice("144 fps", 144),
            }, draft.Fps, value => draft.Fps = (int)value));

            var resolutions = new List<Choice> { new Choice("Same as monitor", 0) };
            foreach (int height in new[] { 1440, 1080, 720 })
            {
                if (primary == null || height < primary.Height)
                {
                    resolutions.Add(new Choice(height + "p", height));
                }
            }
            AddRow(quality, "Resolution", null, NewCombo(resolutions.ToArray(), draft.OutputHeight, value => draft.OutputHeight = (int)value));

            State state = State.Load(ctx);
            var codecs = new List<Choice>();
            if (state.EncoderBrand != Encoders.BrandCpu)
            {
                codecs.Add(new Choice("H.264, plays everywhere", Encoders.CodecH264));
                if (state.SupportsHevc)
                {
                    codecs.Add(new Choice("HEVC, smaller files", Encoders.CodecHevc));
                }
                if (state.SupportsAv1)
                {
                    codecs.Add(new Choice("AV1, smallest files", Encoders.CodecAv1));
                }
            }
            codecs.Add(new Choice("CPU x264, slowest", Encoders.CodecCpu));
            AddRow(quality, "Encoder", "H.264 previews right in Discord.", NewCombo(codecs.ToArray(), draft.Codec, value => draft.Codec = (string)value));
            EndCard(quality, ref y);

            UpdateCaptureHelp();
            return panel;
        }

        // a minute of video at this bitrate, and what it compares to
        private static string QualityText(int mbps)
        {
            string tier = mbps < 10 ? "Small, blurry in fast scenes."
                : mbps < 18 ? "Fine for 1080p."
                : mbps <= 25 ? "About what ShadowPlay uses."
                : mbps <= 45 ? "Sharper than ShadowPlay."
                : "Near lossless, very big files.";
            return $"About {mbps * 60 / 8} MB per minute. {tier}";
        }

        private Panel BuildAudioTab()
        {
            Panel panel = NewPage();
            int y = 0;

            Card mic = AddCard(panel, ref y, "Microphone");
            var mics = new List<Choice> { new Choice("Windows default", Settings.DeviceDefault) };
            mics.AddRange(AudioDevices.Microphones().Select(d => new Choice(d.Name, d.Id)));
            AddRow(mic, "Microphone", "Turn it on and off from the overlay.", NewCombo(mics.ToArray(), draft.MicDevice, value => draft.MicDevice = (string)value));
            AddRow(mic, "Noise suppression", "Removes fan and keyboard noise.", NewCombo(new[]
            {
                new Choice("Off", Settings.NoiseOff),
                new Choice("RNNoise, sounds best", Settings.NoiseRnnoise),
                new Choice("Speex, lightest on CPU", Settings.NoiseSpeex),
            }, draft.NoiseSuppression, value => draft.NoiseSuppression = (string)value));
            EndCard(mic, ref y);

            Card game = AddCard(panel, ref y, "Game audio");
            var outputs = new List<Choice> { new Choice("Windows default", Settings.DeviceDefault) };
            outputs.AddRange(AudioDevices.Outputs().Select(d => new Choice(d.Name, d.Id)));
            outputs.Add(new Choice("Off", Settings.DeviceOff));
            AddRow(game, "Game and desktop audio", null, NewCombo(outputs.ToArray(), draft.DesktopDevice, value => draft.DesktopDevice = (string)value));
            AddToggleRow(game, "Separate audio tracks", "Game and mic on their own tracks too, so Find highlights can tell your reactions apart. Recordings lost in a crash can't be recovered with this on.", draft.SeparateTracks, value => draft.SeparateTracks = value);
            EndCard(game, ref y);
            return panel;
        }

        private Panel BuildHotkeysTab()
        {
            Panel panel = NewPage();
            int y = 0;

            Card keys = AddCard(panel, ref y, "Hotkeys");
            AddHotkeyRow(keys, "Save clip", draft.SaveHotkey, false, false, value => draft.SaveHotkey = value);
            AddHotkeyRow(keys, "Open overlay", draft.OverlayHotkey, false, true, value => draft.OverlayHotkey = value);
            AddHotkeyRow(keys, "Record", draft.RecordHotkey, true, true, value => draft.RecordHotkey = value);
            AddHotkeyRow(keys, "Screenshot", draft.ScreenshotHotkey, true, true, value => draft.ScreenshotHotkey = value);
            AddHotkeyRow(keys, "Instant Replay on/off", draft.ReplayHotkey, true, true, value => draft.ReplayHotkey = value);
            AddHotkeyRow(keys, "Microphone on/off", draft.MicHotkey, true, true, value => draft.MicHotkey = value);
            EndCard(keys, ref y);
            panel.Controls.Add(NewLabel("While Smart Buffer runs these combos belong to it, so a game that also uses one won't see the key. Click a box and press Delete to turn one off.",
                new Point(Ui.S(4), y), new Size(panel.Width - Ui.S(8), Ui.S(40)), Theme.Muted, 11.75F));
            return panel;
        }

        private Panel BuildStartupTab()
        {
            Panel panel = NewPage();
            int y = 0;

            Card startup = AddCard(panel, ref y, "Startup");
            AddToggleRow(startup, "Start with Windows", "OBS starts too if Instant Replay was on.", draft.StartAtLogin, value => draft.StartAtLogin = value);
            AddToggleRow(startup, "Run OBS as admin", "Only needed for games that run as admin. Changing it asks for approval.", draft.RunAsAdmin, value => draft.RunAsAdmin = value);
            AddToggleRow(startup, "Turn off Game Bar capture", "Stops Windows recording the same game in the background.", draft.GameBarOff, value => draft.GameBarOff = value);
            EndCard(startup, ref y);
            return panel;
        }

        private void UpdateCaptureHelp()
        {
            bool desktop = draft.Capture == Settings.CaptureDesktop;
            captureHelp.Text = desktop
                ? "Records the whole monitor, pop-ups and other windows included."
                : "Just the game, cleanest footage. Some anti-cheats may object.";
            monitorCombo.Enabled = desktop;
        }

        private void AddTab(string title, Panel page)
        {
            int index = tabs.Count;
            int pad = Ui.S(4);
            int width = (Ui.S(PageWidth - Inset * 2) - pad * 2) / 5;
            var tab = new LibraryTab(title)
            {
                BackColor = Theme.Panel,
                Location = new Point(Ui.S(Inset) + pad + index * width, Ui.S(TabTop)),
                Size = new Size(width, Ui.S(TabHeight)),
            };
            tab.Click += (_, __) => SelectTab(index);
            tabs.Add(tab);
            pages.Add(page);
            Controls.Add(tab);
            Controls.Add(page);
        }

        private void SelectTab(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Visible = i == index;
                tabs[i].Selected = i == index;
            }
        }

        private Panel NewPage()
        {
            return new Panel
            {
                Location = new Point(Ui.S(Inset), Ui.S(PanelTop)),
                Size = new Size(Ui.S(PageWidth - Inset * 2), Ui.S(PanelHeight)),
                BackColor = Theme.Background,
            };
        }

        // a rounded group of rows with thin lines between them
        private sealed class Card : Panel
        {
            public readonly List<int> Dividers = new List<int>();
            public int Next;

            public Card()
            {
                DoubleBuffered = true;
                BackColor = Theme.Background;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var box = new Rectangle(0, 0, Width - 1, Height - 1);
                Ui.FillRound(e.Graphics, Theme.Panel, box, Ui.S(12));
                Ui.DrawRound(e.Graphics, Theme.Border, box, Ui.S(12));
                using (var pen = new Pen(Theme.Border))
                {
                    foreach (int y in Dividers)
                    {
                        e.Graphics.DrawLine(pen, Ui.S(CardPad), y, Width - Ui.S(CardPad), y);
                    }
                }
            }
        }

        private Card AddCard(Panel panel, ref int y, string title)
        {
            var header = NewLabel(title.ToUpperInvariant(), new Point(Ui.S(4), y), new Size(panel.Width, Ui.S(HeaderHeight - 6)), Theme.Muted, 11.5F);
            header.Font = Ui.Strong(11.5F);
            panel.Controls.Add(header);
            y += Ui.S(HeaderHeight);
            var card = new Card { Location = new Point(0, y), Width = panel.Width };
            panel.Controls.Add(card);
            return card;
        }

        private static void EndCard(Card card, ref int y)
        {
            card.Height = card.Next + 1;
            y += card.Height + Ui.S(SectionGap);
        }

        /// One setting: title and optional description on the left, the control on the right. Returns the description label.
        private Label AddRow(Card card, string title, string description, Control control, int width = ControlWidth)
        {
            int pad = Ui.S(CardPad);
            int textWidth = card.Width - pad * 3 - Ui.S(width);
            Font small = Ui.Text(11.75F);
            int descHeight = string.IsNullOrEmpty(description) ? 0
                : TextRenderer.MeasureText(description, small, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height;
            int height = descHeight == 0 ? Ui.S(50) : Ui.S(34) + descHeight + Ui.S(12);
            int top = card.Next;
            if (top > 0)
            {
                card.Dividers.Add(top);
            }

            Label name = NewLabel(title, new Point(pad, top + (descHeight == 0 ? (height - Ui.S(22)) / 2 : Ui.S(12))), new Size(textWidth, Ui.S(22)), Theme.Text, 13.5F);
            card.Controls.Add(name);
            Label help = null;
            if (descHeight > 0)
            {
                help = NewLabel(description, new Point(pad, top + Ui.S(34)), new Size(textWidth, descHeight + 2), Theme.Muted, 11.75F);
                card.Controls.Add(help);
            }

            control.BackColor = control is FlatCombo || control is TextBox ? Theme.Background : Theme.Panel;
            control.Width = control is ToggleSwitch ? control.Width : Ui.S(width);
            control.Location = new Point(card.Width - pad - (control is ToggleSwitch ? control.Width : Ui.S(width)), top + (height - control.Height) / 2);
            card.Controls.Add(control);
            card.Next = top + height;
            return help;
        }

        private void AddToggleRow(Card card, string title, string description, bool value, Action<bool> changed)
        {
            var toggle = new ToggleSwitch { Checked = value };
            toggle.Toggled += (_, __) =>
            {
                changed(toggle.Checked);
                UpdateDirty();
            };
            AddRow(card, title, description, toggle);
        }

        // on/off, then how much the clips folder may hold, from 10 GB to 5 TB
        private static readonly int[] LimitSteps = { 10, 25, 50, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000, 3000, 4000, 5000 };
        private const int DefaultLimitGb = 500;

        private static string GbText(int gb)
        {
            return gb < 1000 ? gb + " GB" : (gb / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " TB";
        }

        private void AddLimitRow(Card card)
        {
            bool on = draft.StorageLimitGb > 0;
            var holder = new Panel { BackColor = Theme.Panel, Height = Ui.S(28) };
            var toggle = new ToggleSwitch { Checked = on, BackColor = Theme.Panel };
            var slider = new StepSlider(LimitSteps, GbText)
            {
                Value = on ? draft.StorageLimitGb : DefaultLimitGb,
                Enabled = on,
                BackColor = Theme.Panel,
            };
            AddRow(card, "Clips folder limit", "Over the limit, the oldest clips are deleted for good. Favorites are always kept.", holder, ControlWidth + 40);
            toggle.Location = new Point(0, (holder.Height - toggle.Height) / 2);
            slider.SetBounds(toggle.Right + Ui.S(10), 0, holder.Width - toggle.Right - Ui.S(10), holder.Height);
            holder.Controls.Add(toggle);
            holder.Controls.Add(slider);
            toggle.Toggled += (_, __) =>
            {
                slider.Enabled = toggle.Checked;
                draft.StorageLimitGb = toggle.Checked ? slider.Value : 0;
                UpdateDirty();
            };
            slider.Changed += () =>
            {
                draft.StorageLimitGb = slider.Value;
                UpdateDirty();
            };
        }

        private void AddFolderRow(Card card, string title, string question, string current, Action<string> changed)
        {
            var holder = new Panel { BackColor = Theme.Panel, Height = Ui.S(30) };
            var folder = new TextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Background,
                ForeColor = Theme.Text,
                Font = Ui.Text(12.5F),
                Text = current,
            };
            var browse = new ActionButton("Change") { BackColor = Theme.Panel };
            AddRow(card, title, null, holder);
            browse.SetBounds(holder.Width - Ui.S(76), 0, Ui.S(76), holder.Height);
            folder.SetBounds(0, (holder.Height - folder.Height) / 2, browse.Left - Ui.S(8), folder.Height);
            browse.Click += (_, __) => PickFolder(folder, question, changed);
            holder.Controls.Add(folder);
            holder.Controls.Add(browse);
        }

        private void AddHotkeyRow(Card card, string title, string current, bool clearable, bool mustBeFree, Action<string> changed)
        {
            string value = current;
            var box = new HotkeyBox(clearable) { Text = string.IsNullOrEmpty(current) ? HotkeyBox.NoneText : current, TextAlign = HorizontalAlignment.Center };
            box.CaptureChanged += capturing => owner.PauseHotkeys(capturing);
            box.Captured += hotkey =>
            {
                ShowMessage("");
                if (hotkey != null && !owner.IsHotkeyFree(hotkey))
                {
                    if (mustBeFree)
                    {
                        ShowMessage("Another app already uses that combo, so pick a different one.");
                        box.Text = string.IsNullOrEmpty(value) ? HotkeyBox.NoneText : value;
                        return;
                    }
                    ShowMessage($"Another app also uses {hotkey}. It should still work, but that app may react to it too.");
                }
                value = hotkey?.ToString() ?? "";
                changed(value);
                UpdateDirty();
            };
            AddRow(card, title, null, box, 200);
        }

        private FlatCombo NewCombo(Choice[] choices, object current, Action<object> changed)
        {
            var combo = new FlatCombo();
            combo.Items.AddRange(choices.Cast<object>().ToArray());
            int index = Array.FindIndex(choices, choice => Equals(choice.Value, current));
            combo.SelectedIndex = index >= 0 ? index : 0;
            combo.SelectedIndexChanged += (_, __) =>
            {
                changed(((Choice)combo.SelectedItem).Value);
                UpdateDirty();
            };
            return combo;
        }

        private static Label NewLabel(string text, Point location, Size size, Color color, float fontSize)
        {
            return new Label
            {
                Text = text,
                Location = location,
                Size = size,
                ForeColor = color,
                Font = Ui.Text(fontSize),
                BackColor = Color.Transparent,
            };
        }

        private void PickFolder(TextBox folder, string question, Action<string> changed)
        {
            dialogChanged(true);
            string picked = FolderPicker.Pick(FindForm(), question, folder.Text);
            if (picked != null)
            {
                changed(picked);
                folder.Text = picked;
                UpdateDirty();
            }
            dialogChanged(false);
            FindForm()?.Activate();
        }

        private string RepeatedHotkey()
        {
            string[] hotkeys = { draft.SaveHotkey, draft.OverlayHotkey, draft.RecordHotkey, draft.ScreenshotHotkey, draft.ReplayHotkey, draft.MicHotkey };
            return hotkeys
                .Where(hotkey => !string.IsNullOrEmpty(hotkey))
                .GroupBy(hotkey => hotkey)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .FirstOrDefault();
        }

        private async void ApplyChanges()
        {
            if (!applyButton.Enabled)
            {
                return;
            }
            string repeated = RepeatedHotkey();
            if (repeated != null)
            {
                ShowMessage($"{repeated} is set for two things. Pick a different combo for one of them.");
                return;
            }

            SetBusy(true);
            ShowMessage(saved.NeedsObsRestart(draft) ? "Applying. Recording restarts for a few seconds..." : "Applying...");

            Settings next = draft.Clone();
            string failure = await Ui.RunSafely(ctx, () => ObsControl.Apply(ctx, next));

            SetBusy(false);
            if (IsDisposed)
            {
                return;
            }
            if (failure == null)
            {
                saved = Settings.Load(ctx);
                applied(saved);
                ShowMessage("Saved.");
            }
            else
            {
                ShowMessage(failure);
            }
            UpdateDirty();
        }

        private void SetBusy(bool value)
        {
            busy = value;
            busyChanged(value);
            UpdateDirty();
        }

        private void UpdateDirty()
        {
            if (applyButton == null)
            {
                return;
            }
            bool dirty = !draft.SameAs(saved);
            applyButton.Enabled = dirty && !busy;
        }

        private void ShowMessage(string text)
        {
            messageLabel.Text = text;
        }
    }
}
