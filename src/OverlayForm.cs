using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmartBuffer
{
    // a normal solid window that only exists while it's open, it never touches the game
    public sealed class OverlayForm : Form
    {
        private const int DockWidth = 378;
        private const int HeaderHeight = 72;
        private const int TabsTop = 84;
        private const int TabsHeight = 46;
        private const int CaptureLabelTop = 140;
        private const int RowsTop = 166;
        private const int SectionLabelHeight = 22;
        private const int RecentCount = 8;
        private const int RowHeight = 62;
        private const int SaveBarHeight = 44;
        private const int Inset = 12;
        private const int CardGap = 8;
        private const int MicHeight = 64;
        private const int MessageHeight = 72;
        private const int CornerPreference = 33;
        private const int CornerNone = 1;
        private const int StatusGraceSeconds = 15;
        private const int MessageSeconds = 5;

        private readonly Context ctx;
        private readonly Settings draft;
        private readonly Companion owner;
        private readonly IntPtr previousWindow;
        private readonly Rectangle screen;
        private readonly Timer statusTimer;
        private readonly LibraryTab clipsTab = new LibraryTab("Clips", Icons.Film);
        private readonly LibraryTab recordingsTab = new LibraryTab("Recordings", Icons.Video);
        private readonly SidebarRow recordRow = new SidebarRow("Record") { Clickable = true, Card = true, Tint = Theme.Danger };
        private readonly SidebarRow replayRow = new SidebarRow("Instant Replay") { Icon = Icons.History, Card = true, Tint = Theme.Accent };
        private readonly SidebarRow shotRow = new SidebarRow("Screenshot") { Icon = Icons.Photo, Clickable = true, Trail = Icons.Camera, Card = true, Tint = Theme.Pink };
        private readonly SidebarRow micRow = new SidebarRow("Microphone") { Icon = Icons.Mic, Card = true, Tint = Theme.Mint };
        private readonly ToggleSwitch replaySwitch = new ToggleSwitch { BackColor = Theme.Panel };
        private readonly ToggleSwitch micSwitch = new ToggleSwitch { OnColor = Theme.Mint };
        private readonly ActionButton saveButton = new ActionButton("Save clip", null, true) { BackColor = Theme.Panel };
        private readonly MediaCache cache = new MediaCache();
        private readonly RecentStrip recent;
        private readonly HeaderBar header = new HeaderBar();
        private readonly Label messageLabel = new Label { ForeColor = Theme.Muted, BackColor = Theme.Background, TextAlign = ContentAlignment.BottomLeft };

        private Settings saved;
        private int recentLabelTop;
        // a card like the others, only there once an update has downloaded
        private readonly SidebarRow updateRow = new SidebarRow("Update ready") { Icon = Icons.Download, Card = true, Clickable = true, Tint = Theme.Accent, Visible = false };
        private readonly ActionButton updateButton = new ActionButton("Update", null, true);
        private const long LowDiskBytes = 10L * 1000 * 1000 * 1000;
        private const int DiskCheckSeconds = 30;
        private DateTime diskCheckedAt = DateTime.MinValue;
        private string diskWarning = "";
        private GalleryForm gallery;
        private bool recording;
        private bool micOn;
        private bool switching;
        private bool restoreFocus = true;
        private DateTime ignoreReplayUntil = DateTime.MinValue;
        private DateTime ignoreMicUntil = DateTime.MinValue;
        private DateTime messageUntil = DateTime.MinValue;

        public bool Busy { get; set; }
        public bool DialogOpen { get; set; }

        public OverlayForm(Context ctx, Settings draft, Companion owner)
        {
            this.ctx = ctx;
            this.draft = draft;
            this.owner = owner;
            saved = Settings.Load(ctx);
            previousWindow = GetForegroundWindow();

            // always the main monitor, wherever the mouse or the game is
            Ui.UseMainMonitor();
            screen = Screen.PrimaryScreen.Bounds;

            Text = Context.AppName;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = Ui.Text(13);
            Bounds = new Rectangle(screen.Left, screen.Top, Ui.S(DockWidth), screen.Height);
            MaximumSize = Size;

            recent = new RecentStrip(cache);
            BuildHeader();
            BuildTabs();
            BuildRows();
            BuildMic();
            Controls.Add(messageLabel);
            messageLabel.Font = Ui.Text(12.5F);
            messageLabel.SetBounds(Ui.S(16), ClientSize.Height - Ui.S(MicHeight + Inset + MessageHeight), ClientSize.Width - Ui.S(32), Ui.S(MessageHeight - 8));
            int cardWidth = ClientSize.Width - 1 - Ui.S(Inset) * 2;
            updateRow.SetBounds(Ui.S(Inset), messageLabel.Top - Ui.S(RowHeight + CardGap), cardWidth, Ui.S(RowHeight));
            updateButton.SetBounds(cardWidth - Ui.S(16 + 92), (Ui.S(RowHeight) - Ui.S(36)) / 2, Ui.S(92), Ui.S(36));
            updateRow.Controls.Add(updateButton);
            updateRow.Click += (_, __) => owner.InstallUpdate();
            updateButton.Click += (_, __) => owner.InstallUpdate();
            Controls.Add(updateRow);
            BuildRecent();
            CheckForUpdate();

            micOn = saved.RecordMic;
            replaySwitch.Checked = saved.ReplayEnabled;
            UpdateHotkeyText();

            statusTimer = new Timer { Interval = 1000 };
            statusTimer.Tick += (_, __) => UpdateStatus();
            statusTimer.Start();
            UpdateStatus();

            Deactivate += (_, __) => CheckFocusSoon();
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CloseIfIdle();
                }
            };
        }

        public void CloseIfIdle()
        {
            if (!Busy && !DialogOpen)
            {
                Close();
            }
        }

        // show in folder and the like, the new window should stay on top of the game
        public void CloseForOtherApp()
        {
            restoreFocus = false;
            CloseIfIdle();
        }

        // focus moving to the gallery counts as a deactivate too, so this waits to see where it landed
        public void CheckFocusSoon()
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && !Busy && !DialogOpen && ActiveForm == null)
                {
                    Close();
                }
            }));
        }

        public void UpdateTabs()
        {
            bool open = gallery != null && gallery.Visible && !gallery.ShowingSettings;
            clipsTab.Selected = open && gallery.Kind == LibraryKind.Clips;
            recordingsTab.Selected = open && gallery.Kind == LibraryKind.Recordings;
        }

        public void OnApplied(Settings applied)
        {
            saved = applied;
            owner.OnApplied(applied);
            UpdateHotkeyText();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            int corner = CornerNone;
            DwmSetWindowAttribute(Handle, CornerPreference, ref corner, sizeof(int));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
            {
                int right = ClientSize.Width - 1;
                e.Graphics.DrawLine(pen, right, 0, right, ClientSize.Height);
            }
            // the tabs sit in one rounded track, like a segmented control
            Ui.FillRound(e.Graphics, Theme.Panel, new Rectangle(Ui.S(Inset), Ui.S(TabsTop), ClientSize.Width - 1 - Ui.S(Inset * 2), Ui.S(TabsHeight)), Ui.S(10));
            PaintSectionLabel(e.Graphics, "CAPTURE", Ui.S(CaptureLabelTop));
            PaintSectionLabel(e.Graphics, "RECENT CLIPS", recentLabelTop);
        }

        private void PaintSectionLabel(Graphics graphics, string text, int top)
        {
            Ui.DrawLine(graphics, text, Ui.Strong(11.5F), new Rectangle(Ui.S(Inset + 4), top, Ui.S(200), Ui.S(SectionLabelHeight)), Theme.Muted);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            gallery?.Close();
        }

        // name on top, live status under it, on a faint accent glow
        private sealed class HeaderBar : Panel
        {
            public string Status = "";
            public Color StatusColor = Theme.Muted;

            public HeaderBar()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                BackColor = Theme.Panel;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                // the glow fades out before the buttons on the right, they sit on plain panel color
                graphics.Clear(Theme.Panel);
                var glowBox = new Rectangle(0, 0, Width * 3 / 5, Height);
                using (var glow = new LinearGradientBrush(glowBox, Theme.Mix(Theme.Panel, Theme.Accent, 0.28F), Theme.Panel, LinearGradientMode.Horizontal))
                {
                    graphics.FillRectangle(glow, new Rectangle(0, 0, glowBox.Width - 1, Height));
                }
                using (var pen = new Pen(Theme.Border))
                {
                    graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
                }

                int size = Ui.S(38);
                var logo = new Rectangle(Ui.S(14), (Height - size) / 2, size, size);
                using (var fill = new LinearGradientBrush(logo, Theme.AccentHover, Theme.FolderBack, LinearGradientMode.ForwardDiagonal))
                using (GraphicsPath path = Ui.Round(logo, Ui.S(10)))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.FillPath(fill, path);
                }
                Ui.DrawIcon(graphics, Icons.History, logo, Color.White, 18);

                int left = logo.Right + Ui.S(12);
                int middle = Height / 2;
                Ui.DrawLine(graphics, Context.AppName, Ui.Text(17, FontStyle.Bold), new Rectangle(left, middle - Ui.S(23), Ui.S(200), Ui.S(24)), Theme.Text);
                int dot = Ui.S(8);
                using (var brush = new SolidBrush(StatusColor))
                {
                    graphics.FillEllipse(brush, left + 1, middle + Ui.S(7), dot, dot);
                }
                Ui.DrawLine(graphics, Status, Ui.Text(12.5F), new Rectangle(left + dot + Ui.S(7), middle + Ui.S(1), Ui.S(200), Ui.S(20)), Theme.Mix(Theme.Muted, StatusColor, 0.5F));
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            statusTimer.Dispose();
            cache.Dispose();
            if (restoreFocus && previousWindow != IntPtr.Zero)
            {
                SetForegroundWindow(previousWindow);
            }
            base.OnFormClosed(e);
        }

        private void BuildHeader()
        {
            header.Bounds = new Rectangle(0, 0, ClientSize.Width - 1, Ui.S(HeaderHeight));

            int square = Ui.S(44);
            int top = (header.Height - square) / 2;
            var close = new IconButton(Icons.Close) { BackColor = Theme.Panel, IconSize = 12, Bounds = new Rectangle(header.Width - square - Ui.S(6), top, square, square) };
            var gear = new IconButton(Icons.Gear) { BackColor = Theme.Panel, IconSize = 16, Bounds = new Rectangle(close.Left - square - Ui.S(2), top, square, square) };
            close.Click += (_, __) => CloseIfIdle();
            gear.Click += (_, __) => ToggleSettings();
            header.Controls.Add(gear);
            header.Controls.Add(close);
            Controls.Add(header);
        }

        private void BuildTabs()
        {
            int pad = Ui.S(4);
            int half = (ClientSize.Width - 1 - Ui.S(Inset * 2) - pad * 2) / 2;
            clipsTab.BackColor = Theme.Panel;
            recordingsTab.BackColor = Theme.Panel;
            clipsTab.SetBounds(Ui.S(Inset) + pad, Ui.S(TabsTop) + pad, half, Ui.S(TabsHeight) - pad * 2);
            recordingsTab.SetBounds(Ui.S(Inset) + pad + half, Ui.S(TabsTop) + pad, half, Ui.S(TabsHeight) - pad * 2);
            clipsTab.Click += (_, __) => OpenTab(LibraryKind.Clips);
            recordingsTab.Click += (_, __) => OpenTab(LibraryKind.Recordings);
            Controls.Add(clipsTab);
            Controls.Add(recordingsTab);
        }

        private void BuildRows()
        {
            int left = Ui.S(Inset);
            int width = ClientSize.Width - 1 - left * 2;
            int y = Ui.S(RowsTop);

            recordRow.IconPainter = PaintRecordIcon;
            recordRow.SetBounds(left, y, width, Ui.S(RowHeight));
            recordRow.Click += (_, __) => owner.ToggleRecording();
            y += Ui.S(RowHeight + CardGap);

            // save clip lives inside the instant replay card, it only works while replay is on
            replayRow.HeadHeight = Ui.S(RowHeight);
            replayRow.SetBounds(left, y, width, Ui.S(RowHeight + SaveBarHeight + 14));
            replaySwitch.Location = new Point(width - Ui.S(16) - replaySwitch.Width, (Ui.S(RowHeight) - replaySwitch.Height) / 2);
            replaySwitch.Toggled += (_, __) => ToggleReplay();
            saveButton.SetBounds(Ui.S(14), Ui.S(RowHeight), width - Ui.S(28), Ui.S(SaveBarHeight));
            saveButton.Click += (_, __) => SaveClip();
            replayRow.Controls.Add(replaySwitch);
            replayRow.Controls.Add(saveButton);
            y += replayRow.Height + Ui.S(CardGap);

            shotRow.SetBounds(left, y, width, Ui.S(RowHeight));
            shotRow.Click += (_, __) => owner.TakeScreenshot();

            Controls.Add(recordRow);
            Controls.Add(replayRow);
            Controls.Add(shotRow);
        }

        private void BuildMic()
        {
            int left = Ui.S(Inset);
            int width = ClientSize.Width - 1 - left * 2;
            micRow.SetBounds(left, ClientSize.Height - Ui.S(MicHeight + Inset), width, Ui.S(MicHeight));
            micSwitch.Location = new Point(width - Ui.S(16) - micSwitch.Width, (micRow.Height - micSwitch.Height) / 2);
            micSwitch.Toggled += (_, __) => ToggleMic();
            micRow.Controls.Add(micSwitch);
            Controls.Add(micRow);
        }

        private void BuildRecent()
        {
            int left = Ui.S(Inset);
            recentLabelTop = shotRow.Bottom + Ui.S(14);
            int top = recentLabelTop + Ui.S(SectionLabelHeight + 6);
            recent.SetBounds(left, top, ClientSize.Width - 1 - left * 2, Math.Max(0, (owner.Update != null ? updateRow.Top : messageLabel.Top) - Ui.S(8) - top));
            recent.EmptyText = "No clips yet. Press " + saved.SaveHotkey + " with replay on.";
            recent.Picked += (folder, file) =>
            {
                EnsureGallery();
                gallery.ShowClip(folder, file, LibraryKind.Clips);
                UpdateTabs();
            };
            cache.Updated += recent.Invalidate;
            Controls.Add(recent);
            LoadRecent();
        }

        /// Straight to a saved file, from a click on the "saved" card.
        public void OpenSaved(string path)
        {
            Settings current = Settings.Load(ctx);
            LibraryKind kind = path.StartsWith(current.RecordingsDir, StringComparison.OrdinalIgnoreCase) ? LibraryKind.Recordings : LibraryKind.Clips;
            string root = kind == LibraryKind.Recordings ? current.RecordingsDir : current.ClipsDir;
            foreach (LibraryFolder folder in Library.Scan(root, null))
            {
                LibraryFile file = folder.Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
                if (file != null)
                {
                    EnsureGallery();
                    gallery.ShowClip(folder, file, kind);
                    UpdateTabs();
                    return;
                }
            }
            OpenTab(kind);
        }

        // after a delete or rename in the player, so the grid never points at a file that's gone
        public void RefreshRecent()
        {
            LoadRecent();
        }

        private async void LoadRecent()
        {
            string root = saved.ClipsDir;
            List<KeyValuePair<LibraryFolder, LibraryFile>> newest = await Task.Run(() =>
            {
                try
                {
                    return Library.Scan(root, null)
                        .SelectMany(folder => folder.Files.Select(file => new KeyValuePair<LibraryFolder, LibraryFile>(folder, file)))
                        .OrderByDescending(pair => pair.Value.Modified)
                        .Take(RecentCount)
                        .ToList();
                }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
                {
                    return new List<KeyValuePair<LibraryFolder, LibraryFile>>();
                }
            });
            if (!IsDisposed)
            {
                recent.Show(newest);
            }
        }

        private void PaintRecordIcon(Graphics graphics, Rectangle box)
        {
            Color color = Theme.Danger;
            int outer = Ui.S(16);
            int inner = Ui.S(6);
            int centerX = box.X + box.Width / 2;
            int centerY = box.Y + box.Height / 2;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(color, Ui.Scale * 1.6F))
            using (var brush = new SolidBrush(color))
            {
                graphics.DrawEllipse(pen, centerX - outer / 2, centerY - outer / 2, outer, outer);
                graphics.FillEllipse(brush, centerX - inner / 2, centerY - inner / 2, inner, inner);
            }
        }

        private void OpenTab(LibraryKind kind)
        {
            EnsureGallery();
            if (gallery.Visible && !gallery.ShowingSettings && gallery.Kind == kind && gallery.AtLibraryRoot)
            {
                gallery.CloseGallery();
                return;
            }
            gallery.ShowLibrary(kind);
            UpdateTabs();
        }

        private void ToggleSettings()
        {
            EnsureGallery();
            if (gallery.Visible && gallery.ShowingSettings)
            {
                gallery.CloseGallery();
                return;
            }
            gallery.ShowSettings();
            UpdateTabs();
        }

        private void EnsureGallery()
        {
            if (gallery == null)
            {
                gallery = new GalleryForm(ctx, owner, this, draft, screen);
            }
        }

        private void SaveClip()
        {
            if (!replaySwitch.Checked)
            {
                ShowMessage("Turn on Instant Replay to save a clip.");
                return;
            }
            owner.SaveClip();
        }

        private async void ToggleReplay()
        {
            bool on = replaySwitch.Checked;
            switching = true;
            replaySwitch.Enabled = false;
            ignoreReplayUntil = DateTime.UtcNow.AddSeconds(StatusGraceSeconds);
            string failure = await Ui.RunSafely(ctx, () => ObsControl.SetReplay(ctx, on));
            switching = false;
            draft.ReplayEnabled = on;
            saved.ReplayEnabled = on;
            if (IsDisposed)
            {
                return;
            }
            if (failure != null)
            {
                ignoreReplayUntil = DateTime.MinValue;
                ShowMessage(failure);
            }
            UpdateStatus();
        }

        private async void ToggleMic()
        {
            bool on = micSwitch.Checked;
            micOn = on;
            ignoreMicUntil = DateTime.UtcNow.AddSeconds(StatusGraceSeconds);
            string failure = await Ui.RunSafely(ctx, () => ObsControl.SetMic(ctx, on));
            draft.RecordMic = on;
            saved.RecordMic = on;
            if (IsDisposed)
            {
                return;
            }
            if (failure != null)
            {
                ignoreMicUntil = DateTime.MinValue;
                ShowMessage(failure);
            }
            UpdateStatus();
        }

        private void UpdateHotkeyText()
        {
            replayRow.Subtitle = HotkeyLine(saved.ReplayHotkey, "Toggle on/off");
            shotRow.Subtitle = HotkeyLine(saved.ScreenshotHotkey, "Save a screenshot");
            micRow.Subtitle = HotkeyLine(saved.MicHotkey, "Toggle on/off");
            saveButton.Hint = saved.SaveHotkey;
            UpdateRecordRow(null);
        }

        private string HotkeyLine(string hotkey, string action)
        {
            if (string.IsNullOrEmpty(hotkey))
            {
                return action;
            }
            return owner.HotkeyIsTaken(hotkey) ? hotkey + " is in use" : hotkey + " - " + action;
        }

        private void UpdateRecordRow(ObsStatus status)
        {
            bool wasRecording = recording;
            recording = status?.Recording ?? recording;
            string action = "Start";
            if (recording)
            {
                TimeSpan elapsed = DateTime.UtcNow - (status?.RecordingSinceUtc ?? DateTime.UtcNow);
                action = "Stop, recording " + Library.LengthText(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
            }
            recordRow.Subtitle = HotkeyLine(saved.RecordHotkey, action);
            recordRow.Trail = recording ? Icons.Stop : Icons.Play;
            recordRow.TrailColor = recording ? Theme.Danger : Theme.Text;
            recordRow.Active = recording;
            if (recording)
            {
                TimeSpan elapsed = DateTime.UtcNow - (status?.RecordingSinceUtc ?? DateTime.UtcNow);
                SetHeaderStatus("Recording " + Library.LengthText(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed), Theme.Danger);
            }
            if (wasRecording != recording)
            {
                recordRow.Invalidate();
            }
        }

        private void SetHeaderStatus(string text, Color color)
        {
            if (header.Status != text || header.StatusColor != color)
            {
                header.Status = text;
                header.StatusColor = color;
                header.Invalidate();
            }
        }

        private void ShowMessage(string text)
        {
            messageLabel.Text = text;
            messageUntil = DateTime.UtcNow.AddSeconds(MessageSeconds);
        }

        private void UpdateStatus()
        {
            ObsStatus status = ObsControl.ReadStatus(ctx);
            UpdateRecordRow(status);

            // obs takes a few seconds to catch up after a click, so the switch doesn't flick back
            if (DateTime.UtcNow >= ignoreReplayUntil || status.Replay == replaySwitch.Checked)
            {
                replaySwitch.Checked = status.ObsRunning ? status.Replay : saved.ReplayEnabled;
            }
            replaySwitch.Enabled = !switching && (!status.ObsRunning || status.ScriptRunning);

            if (status.Mic != null && (DateTime.UtcNow >= ignoreMicUntil || status.Mic == micOn))
            {
                micOn = status.Mic.Value;
            }
            micSwitch.Checked = micOn;
            micRow.Active = micOn;
            replayRow.Active = replaySwitch.Checked;
            if (!recording)
            {
                if (!status.ObsRunning)
                {
                    SetHeaderStatus("OBS is closed", Theme.Slash);
                }
                else
                {
                    SetHeaderStatus(replaySwitch.Checked ? "Instant Replay is on" : "Ready", replaySwitch.Checked ? Theme.FolderEdge : Theme.Mint);
                }
            }

            if (DateTime.UtcNow >= messageUntil)
            {
                messageLabel.Text = ProblemText(status);
            }
        }

        private string ProblemText(ObsStatus status)
        {
            if (!status.ObsRunning)
            {
                return "OBS isn't running. Record and Instant Replay will start it.";
            }
            if (!status.ScriptRunning)
            {
                return Obs.VisiblePopup(ctx) != null ? "OBS is waiting on a popup." : "OBS is on another profile, so Smart Buffer can't control it.";
            }
            return DiskWarning();
        }

        // checked now and then, not every second, the drive won't fill that fast
        private string DiskWarning()
        {
            if (DateTime.UtcNow - diskCheckedAt < TimeSpan.FromSeconds(DiskCheckSeconds))
            {
                return diskWarning;
            }
            diskCheckedAt = DateTime.UtcNow;
            diskWarning = "";
            try
            {
                var drive = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(saved.ClipsDir));
                if (drive.IsReady && drive.AvailableFreeSpace < LowDiskBytes)
                {
                    diskWarning = $"Only {Library.SizeText(drive.AvailableFreeSpace)} free on {drive.Name.TrimEnd('\\')}. Clips stop saving when it's full.";
                }
            }
            catch (Exception e) when (e is ArgumentException || e is System.IO.IOException || e is UnauthorizedAccessException)
            {
            }
            return diskWarning;
        }

        // the tray app already downloaded and checked it, so this only offers it
        private void CheckForUpdate()
        {
            ReadyUpdate update = owner.Update;
            if (update != null)
            {
                updateRow.Subtitle = $"Version {update.Version}";
                updateRow.Visible = true;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
