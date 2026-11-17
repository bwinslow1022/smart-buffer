using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartBuffer
{
    // the panel that floats beside the sidebar, holds the library and the settings
    public sealed class GalleryForm : Form
    {
        private const int Gap = 16;
        private const int BarHeight = 64;
        private const int CornerPreference = 33;
        private const int CornerRound = 2;
        private const int BorderColorAttribute = 34;

        private readonly Context ctx;
        private readonly Companion owner;
        private readonly OverlayForm dock;
        private readonly Settings draft;
        private readonly Rectangle area;
        private readonly MediaCache cache = new MediaCache();

        private readonly Panel bar = new Panel { BackColor = Theme.Bar };
        private readonly Panel barLine = new Panel { BackColor = Theme.Border };
        private readonly IconButton backButton = new IconButton(Icons.Left) { Boxed = true };
        private readonly IconButton closeButton = new IconButton(Icons.Close) { BackColor = Theme.Bar, IconColor = Theme.Muted, HoverColor = Theme.Panel, IconSize = 12 };
        private readonly Breadcrumb crumb = new Breadcrumb { BackColor = Theme.Bar };
        private readonly TextBox search = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = Ui.Text(13),
        };
        private const int SearchWidth = 220;
        private string Query => search.Text.Trim();
        private readonly SegmentToggle sort = new SegmentToggle("Recently modified", "Biggest first") { BackColor = Theme.Bar };
        private readonly LibraryGrid grid;

        private PlayerPanel player;
        private SettingsPanel settings;
        private List<LibraryFolder> folders = new List<LibraryFolder>();
        private LibraryFolder openFolder;
        private LibraryFile openFile;
        private int scanNumber;

        public LibraryKind Kind { get; private set; }
        public bool ShowingSettings { get; private set; }
        public bool AtLibraryRoot => !ShowingSettings && openFolder == null && openFile == null;

        public GalleryForm(Context ctx, Companion owner, OverlayForm dock, Settings draft, Rectangle screen)
        {
            this.ctx = ctx;
            this.owner = owner;
            this.dock = dock;
            this.draft = draft;

            Owner = dock;
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

            int left = screen.Left + dock.Width + Ui.S(Gap);
            area = new Rectangle(left, screen.Top + Ui.S(Gap), screen.Right - left - Ui.S(Gap), screen.Height - Ui.S(Gap) * 2);
            Bounds = area;

            backButton.BackColor = Theme.Bar;
            backButton.Click += (_, __) => GoBack();
            closeButton.Click += (_, __) => CloseGallery();
            sort.Changed += OnSortChanged;
            bar.Controls.AddRange(new Control[] { backButton, crumb, search, sort, closeButton });
            search.TextChanged += (_, __) =>
            {
                ShowCurrentGrid();
                Render();
            };
            search.HandleCreated += (_, __) => SendMessage(search.Handle, SetCueBanner, (IntPtr)1, "Search all " + (Kind == LibraryKind.Clips ? "clips" : "recordings"));
            Controls.Add(bar);
            Controls.Add(barLine);

            grid = new LibraryGrid(cache);
            grid.FolderOpened += OpenFolder;
            grid.FileOpened += OpenFile;
            Controls.Add(grid);

            KeyDown += OnKey;
            Deactivate += (_, __) => dock.CheckFocusSoon();
        }

        public void ShowLibrary(LibraryKind kind)
        {
            search.Text = "";
            if (search.IsHandleCreated)
            {
                SendMessage(search.Handle, SetCueBanner, (IntPtr)1, "Search all " + (kind == LibraryKind.Clips ? "clips" : "recordings"));
            }
            ShowingSettings = false;
            player?.Unload();
            Kind = kind;
            openFolder = null;
            openFile = null;
            Bounds = area;
            ShowPanel();
            Render();
            Rescan();
        }

        // straight into the player, from the recent clips on the dock
        public void ShowClip(LibraryFolder folder, LibraryFile file, LibraryKind kind)
        {
            ShowingSettings = false;
            Kind = kind;
            Bounds = area;
            ShowPanel();
            if (!System.IO.File.Exists(file.Path))
            {
                // deleted or renamed outside the player, so the folders show instead
                dock.RefreshRecent();
                openFolder = null;
                openFile = null;
                Render();
                Rescan();
                return;
            }
            openFolder = folder;
            OpenFile(file);
        }

        public void ShowSettings()
        {
            ShowingSettings = true;
            player?.Unload();
            openFile = null;
            openFolder = null;
            if (settings == null)
            {
                settings = new SettingsPanel(ctx, draft, owner, value => dock.Busy = value, value => dock.DialogOpen = value, dock.OnApplied);
                Controls.Add(settings);
            }
            int height = Math.Min(area.Height, Ui.S(BarHeight + SettingsPanel.PageHeight));
            Bounds = new Rectangle(area.X, area.Y, Ui.S(SettingsPanel.PageWidth), height);
            ShowPanel();
            Render();
        }

        public void CloseGallery()
        {
            // focus has to land before this hides, or the focus check closes the whole overlay
            player?.Unload();
            dock.Activate();
            Hide();
            dock.UpdateTabs();
        }

        // only the notice talks to the user here, and it's a normal modal window
        public bool ConfirmUpload()
        {
            dock.DialogOpen = true;
            bool confirmed;
            using (var notice = new UploadNoticeForm())
            {
                Rectangle screen = Screen.FromControl(this).Bounds;
                notice.Location = new Point(screen.Left + (screen.Width - notice.Width) / 2, screen.Top + (screen.Height - notice.Height) / 2);
                confirmed = notice.ShowDialog(this) == DialogResult.OK;
            }
            dock.DialogOpen = false;
            dock.CheckFocusSoon();
            return confirmed;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            int corner = CornerRound;
            DwmSetWindowAttribute(Handle, CornerPreference, ref corner, sizeof(int));
            int border = ColorTranslator.ToWin32(Theme.Border);
            DwmSetWindowAttribute(Handle, BorderColorAttribute, ref border, sizeof(int));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutPage();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            player?.Unload();
            cache.Dispose();
            base.OnFormClosed(e);
        }

        private void ShowPanel()
        {
            if (!Visible)
            {
                Show();
            }
            Activate();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            // typing a new name: escape and space belong to the text box
            if (FocusedTextBox())
            {
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                dock.CloseIfIdle();
            }
            else if (e.KeyCode == Keys.Space && openFile != null)
            {
                player.Toggle();
                e.Handled = true;
            }
        }

        // arrows would otherwise move focus between buttons before the player saw them
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (openFile != null && !ShowingSettings && player != null && !FocusedTextBox() && !(ActiveControl is ComboBox box && box.DroppedDown))
            {
                switch (keyData)
                {
                    case Keys.Left:
                        player.Skip(-PlayerPanel.SkipSeconds);
                        return true;
                    case Keys.Right:
                        player.Skip(PlayerPanel.SkipSeconds);
                        return true;
                    case Keys.Oemcomma:
                        player.StepFrame(false);
                        return true;
                    case Keys.OemPeriod:
                        player.StepFrame(true);
                        return true;
                }
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private static bool FocusedTextBox()
        {
            return Control.FromHandle(GetFocus()) is TextBox box && !box.ReadOnly;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        private const int SetCueBanner = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        private async void Rescan()
        {
            int number = ++scanNumber;
            Settings saved = Settings.Load(ctx);
            string root = Kind == LibraryKind.Clips ? saved.ClipsDir : saved.RecordingsDir;
            ObsStatus status = ObsControl.ReadStatus(ctx);
            DateTime? writing = Kind == LibraryKind.Recordings && status.Recording ? status.RecordingSinceUtc ?? DateTime.UtcNow : (DateTime?)null;

            List<LibraryFolder> found = await Task.Run(() => Library.Scan(root, writing));
            if (IsDisposed || number != scanNumber)
            {
                return;
            }
            folders = found;
            if (openFolder != null)
            {
                openFolder = folders.FirstOrDefault(folder => folder.Name == openFolder.Name);
            }
            ShowCurrentGrid();
            Render();
        }

        private bool BySize => sort.Selected == 1;
        private string Noun => Kind == LibraryKind.Clips ? "clip" : "recording";

        private string EmptyText()
        {
            Settings saved = Settings.Load(ctx);
            if (Kind == LibraryKind.Clips)
            {
                return "No clips yet. Press " + saved.SaveHotkey + " with Instant Replay on to save one.";
            }
            return string.IsNullOrEmpty(saved.RecordHotkey)
                ? "No recordings yet. Press Record to start one."
                : "No recordings yet. Press " + saved.RecordHotkey + " to start one.";
        }

        // matches the file name or its game folder, across every folder
        private List<LibraryFile> SearchResults()
        {
            string query = Query;
            return folders
                .SelectMany(folder => folder.Files.Where(file => file.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || folder.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(file => file.Modified)
                .ToList();
        }

        private void ShowCurrentGrid()
        {
            if (Query != "" && openFolder == null)
            {
                grid.ShowFiles(SearchResults());
                return;
            }
            if (openFolder != null)
            {
                grid.ShowFiles(Library.Sorted(openFolder, BySize));
            }
            else
            {
                grid.ShowFolders(Library.Sorted(folders, BySize), Noun, EmptyText());
            }
        }

        private void OnSortChanged()
        {
            if (openFolder != null)
            {
                grid.UpdateFiles(Library.Sorted(openFolder, BySize));
            }
            else
            {
                grid.UpdateFolders(Library.Sorted(folders, BySize));
            }
        }

        private void OpenFolder(LibraryFolder folder)
        {
            openFolder = folder;
            grid.ShowFiles(Library.Sorted(folder, BySize));
            Render();
        }

        private void OpenFile(LibraryFile file)
        {
            // from search results, the folder is whichever one holds the file
            if (openFolder == null || !openFolder.Files.Contains(file))
            {
                openFolder = folders.FirstOrDefault(folder => folder.Files.Contains(file)) ?? openFolder;
            }
            openFile = file;
            if (player == null)
            {
                player = new PlayerPanel(ctx, owner.Uploads, cache, ConfirmUpload, dock.CloseForOtherApp);
                player.FilePicked += OpenFile;
                player.FileGone += OnFileGone;
                player.FileRenamed += () =>
                {
                    Render();
                    dock.RefreshRecent();
                };
                Controls.Add(player);
                LayoutPage();
            }
            Render();
            player.Open(Library.Sorted(openFolder, BySize), file, openFolder.Name);
        }

        // the next clip in the folder opens in its place, or back to the folders when it was the last
        private void OnFileGone(LibraryFile gone)
        {
            List<LibraryFile> sorted = Library.Sorted(openFolder, BySize);
            int index = sorted.FindIndex(f => f.Path == gone.Path);
            openFolder.Files.RemoveAll(f => f.Path == gone.Path);
            sorted.RemoveAll(f => f.Path == gone.Path);
            dock.RefreshRecent();
            if (sorted.Count == 0)
            {
                GoToRoot();
                return;
            }
            OpenFile(sorted[Math.Min(Math.Max(index, 0), sorted.Count - 1)]);
        }

        private void GoBack()
        {
            if (openFile != null)
            {
                player.Unload();
                openFile = null;
            }
            else
            {
                openFolder = null;
            }
            Render();
            Rescan();
        }

        private void GoToRoot()
        {
            player?.Unload();
            openFile = null;
            openFolder = null;
            Render();
            Rescan();
        }

        private void GoToFolder()
        {
            player?.Unload();
            openFile = null;
            Render();
            Rescan();
        }

        private void Render()
        {
            bool inPlayer = !ShowingSettings && openFile != null;
            grid.Visible = !ShowingSettings && openFile == null;
            if (player != null)
            {
                player.Visible = inPlayer;
            }
            if (settings != null)
            {
                settings.Visible = ShowingSettings;
            }
            backButton.Visible = !ShowingSettings && openFolder != null;
            sort.Visible = !ShowingSettings && openFile == null;

            if (ShowingSettings)
            {
                crumb.Set(null, Part("Settings", null));
            }
            else
            {
                string root = Kind == LibraryKind.Clips ? "Clips" : "Recordings";
                if (openFile != null)
                {
                    crumb.Set(null, Part(root, GoToRoot), Part(openFolder.Name, GoToFolder), Part(Library.When(openFile.Modified), null));
                }
                else if (openFolder != null)
                {
                    crumb.Set(Library.Plural(openFolder.Files.Count, Noun) + ", " + Library.SizeText(openFolder.Size), Part(root, GoToRoot), Part(openFolder.Name, null));
                }
                else
                {
                    if (Query != "")
                    {
                        int matches = SearchResults().Count;
                        crumb.Set(matches == 1 ? "1 match" : matches + " matches", Part(root, null));
                    }
                    else
                    {
                        crumb.Set(Library.Plural(folders.Count, "folder") + ", " + UsedSpace(), Part(root, null));
                    }
                }
            }
            LayoutPage();
            dock.UpdateTabs();
        }

        private string UsedSpace()
        {
            string used = Library.SizeText(folders.Sum(folder => folder.Size));
            long limit = Kind == LibraryKind.Clips ? ClipCare.LimitBytes(Settings.Load(ctx)) : 0;
            return limit > 0 ? used + " of " + Library.SizeText(limit) : used;
        }

        private static KeyValuePair<string, Action> Part(string text, Action click)
        {
            return new KeyValuePair<string, Action>(text, click);
        }

        private void LayoutPage()
        {
            if (bar == null || grid == null)
            {
                return;
            }
            int width = ClientSize.Width;
            int height = ClientSize.Height;
            int barHeight = Ui.S(BarHeight);
            bar.SetBounds(1, 1, width - 2, barHeight - 1);
            barLine.SetBounds(1, barHeight, width - 2, 1);

            int square = Ui.S(44);
            int top = (bar.Height - square) / 2;
            int x = Ui.S(16);
            if (!ShowingSettings && openFolder != null)
            {
                backButton.SetBounds(x, top, square, square);
                x += square + Ui.S(14);
            }
            closeButton.SetBounds(bar.Width - square - Ui.S(12), top, square, square);
            int right = closeButton.Left - Ui.S(12);
            if (!ShowingSettings && openFile == null)
            {
                sort.Location = new Point(right - sort.Width, (bar.Height - sort.Height) / 2);
                right = sort.Left - Ui.S(12);
            }
            search.Visible = !ShowingSettings && openFile == null && openFolder == null;
            if (search.Visible)
            {
                search.SetBounds(right - Ui.S(SearchWidth), (bar.Height - search.Height) / 2, Ui.S(SearchWidth), search.Height);
                right = search.Left - Ui.S(12);
            }
            crumb.SetBounds(x, top, Math.Max(0, right - x), square);

            var body = new Rectangle(1, barHeight + 1, width - 2, height - barHeight - 2);
            grid.Bounds = body;
            if (player != null)
            {
                player.Bounds = body;
            }
            if (settings != null)
            {
                settings.Bounds = body;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    public sealed class UploadNoticeForm : Form
    {
        private const int NoticeWidth = 460;

        public UploadNoticeForm()
        {
            Text = Context.AppName;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Theme.Background;
            ClientSize = new Size(Ui.S(NoticeWidth), Ui.S(244));

            Controls.Add(NewLabel("Before you upload", 24, 22, Ui.Text(18, FontStyle.Bold), Theme.Text));
            Controls.Add(NewLabel("\u2022  Anyone with the link can watch this video.", 24, 66, Ui.Text(14), Theme.Text));
            Controls.Add(NewLabel("\u2022  kappa.lol stores your IP address and may review uploads.", 24, 92, Ui.Text(14), Theme.Text));
            Controls.Add(NewLabel("\u2022  There's no stated expiry, so delete it when you're done.", 24, 118, Ui.Text(14), Theme.Text));
            Controls.Add(NewLabel("\u2022  kappa.lol's terms say you must be 18 or over.", 24, 144, Ui.Text(14), Theme.Text));

            var cancel = new ActionButton("Cancel")
            {
                Location = new Point(Ui.S(NoticeWidth - 24 - 96 - 8 - 96), Ui.S(180)),
                Size = new Size(Ui.S(96), Ui.S(44)),
            };
            cancel.Click += (_, __) => DialogResult = DialogResult.Cancel;
            var upload = new ActionButton("Upload", null, true)
            {
                Location = new Point(Ui.S(NoticeWidth - 24 - 96), Ui.S(180)),
                Size = new Size(Ui.S(96), Ui.S(44)),
            };
            upload.Click += (_, __) => DialogResult = DialogResult.OK;
            Controls.Add(cancel);
            Controls.Add(upload);

            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                }
            };
            // closing deactivates it too, and that mustn't turn an upload click into a cancel
            Deactivate += (_, __) =>
            {
                if (DialogResult == DialogResult.None)
                {
                    DialogResult = DialogResult.Cancel;
                }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        private static Label NewLabel(string text, int x, int y, Font font, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(Ui.S(x), Ui.S(y)),
                Size = new Size(Ui.S(NoticeWidth - x * 2), font.Height + Ui.S(6)),
                Font = font,
                ForeColor = color,
                BackColor = Color.Transparent,
            };
        }
    }
}
