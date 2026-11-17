using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmartBuffer
{
    // the preview page: video and scrub bar on the left, file details and actions on the right
    public sealed class PlayerPanel : Panel
    {
        private const int Inset = 24;
        private const int SideWidth = 280;
        private const int SideGap = 24;
        private const int RowGap = 12;
        private const int ControlHeight = 44;
        private const int StripCount = 5;
        private const int StripGap = 10;
        private const int ConfirmMs = 2500;
        private const string FileHint = "the video file";
        private const string HostLine = "Hosted by kappa.lol. There's no stated expiry, so delete it when you're done.";
        private const string IdleLine = "Public link, up to 100 MB. " + HostLine;
        private const string UploadLabel = "Upload to kappa.lol";
        private const string DeleteLabel = "Delete from kappa.lol";

        private readonly Context ctx;
        private readonly Uploader uploads;
        private readonly MediaCache cache;
        private readonly Func<bool> confirmUpload;
        private readonly Action closeOverlay;

        private readonly Panel frame = new Panel { BackColor = Color.Black };
        private readonly IconButton playButton = new IconButton(Icons.Play) { Boxed = true };
        private readonly ProgressLine scrub = new ProgressLine { Interactive = true };
        private readonly Label clock = NewLabel(13, Theme.Muted);
        private readonly ThumbStrip strip;
        private readonly Label keysHint = NewLabel(12, Theme.Muted);
        private const int HintHeight = 22;
        public const int SkipSeconds = 5;
        private readonly Panel side = new Panel { BackColor = Theme.Background };

        private readonly Label nameLabel = new Label { Font = Ui.Text(18, FontStyle.Bold), ForeColor = Theme.Text, AutoEllipsis = true, UseMnemonic = false };
        private readonly Label fileLabel = NewLabel(13, Theme.Muted);
        private readonly List<Label> keys = new List<Label>();
        private readonly List<Label> values = new List<Label>();
        private readonly ActionButton folderButton = new ActionButton("Show in folder", Icons.Folder);
        private readonly ActionButton copyButton = new ActionButton("Copy", Icons.Copy, false, FileHint);
        private readonly ActionButton favoriteButton = new ActionButton("Favorite");
        private readonly ActionButton removeButton = new ActionButton("Delete");
        private readonly ActionButton renameButton = new ActionButton("Rename");
        private readonly TextBox renameBox = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = Ui.Text(13),
            Visible = false,
        };
        private readonly ActionButton sortButton = new ActionButton("Sort as game");
        private string desktopSource;
        private readonly Panel rule = new Panel { BackColor = Theme.Border };

        private readonly Label shareTitle = NewLabel(14, Theme.Text);
        private readonly ActionButton startButton = new ActionButton("Start 0:00");
        private readonly ActionButton endButton = new ActionButton("End");
        private readonly Label trimHint = NewLabel(12.5F, Theme.Muted);
        private readonly FlatCombo sizeCombo = new FlatCombo();
        private readonly ActionButton shareButton = new ActionButton("Make share copy", Icons.Film, true);
        private readonly Label shareStatus = NewLabel(12.5F, Theme.Muted);
        private readonly ActionButton highlightButton = new ActionButton("Find highlights");
        private readonly ActionButton learnButton = new ActionButton("Learn kill sound");
        private readonly MomentList momentList = new MomentList();
        private readonly ActionButton keepButton = new ActionButton("Save highlights", Icons.Film, true);
        private List<Moment> moments = new List<Moment>();
        private string game;
        private readonly Panel shareRule = new Panel { BackColor = Theme.Border };
        private static readonly Choice[] SizeChoices =
        {
            new Choice("Under 10 MB", 10),
            new Choice("Under 20 MB", 20),
            new Choice("Under 25 MB", 25),
            new Choice("Under 50 MB", 50),
            new Choice("Under 100 MB", 100),
            new Choice("Full quality (trim only)", 0),
        };
        private TimeSpan trimStart;
        private TimeSpan? trimEnd;
        private bool sharing;

        private readonly Label uploadTitle = NewLabel(14, Theme.Text);
        private readonly Label uploadRight = NewLabel(13, Theme.Text);
        private readonly ProgressLine uploadBar = new ProgressLine();
        private readonly Label uploadDetail = NewLabel(12.5F, Theme.Muted);
        private readonly Label uploadError = NewLabel(13, Theme.ErrorText);
        private readonly TextBox linkBox = new TextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            Font = Ui.Text(13),
        };
        private readonly ActionButton uploadButton = new ActionButton(UploadLabel, Icons.Upload, true);
        private readonly ActionButton deleteButton = new ActionButton(DeleteLabel, Icons.Delete);
        private readonly ActionButton cancelButton = new ActionButton("Cancel");
        private readonly Label uploadFine = NewLabel(12.5F, Theme.Muted);

        private readonly Timer tick = new Timer { Interval = 200 };
        private readonly Timer copyReset = new Timer { Interval = ConfirmMs };

        private VideoHost video;
        private List<LibraryFile> files = new List<LibraryFile>();
        private LibraryFile file;
        private UploadState shownState;
        // big clips upload as a smaller copy, this remembers which copy belongs to which clip
        private readonly Dictionary<string, string> uploadCopies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private const int UploadCopyMb = 95;

        private string UploadPath => file != null && uploadCopies.TryGetValue(file.Path, out string copy) && File.Exists(copy) ? copy : file?.Path;
        private long UploadSize => UploadPath == file?.Path ? file?.Size ?? 0 : new FileInfo(UploadPath).Length;
        private bool showError;
        private bool showDelete;

        public event Action<LibraryFile> FilePicked;
        // deleted, or moved into a game folder, either way it's gone from this folder
        public event Action<LibraryFile> FileGone;
        public event Action FileRenamed;

        public PlayerPanel(Context ctx, Uploader uploads, MediaCache cache, Func<bool> confirmUpload, Action closeOverlay)
        {
            this.ctx = ctx;
            this.uploads = uploads;
            this.cache = cache;
            this.confirmUpload = confirmUpload;
            this.closeOverlay = closeOverlay;
            BackColor = Theme.Background;

            strip = new ThumbStrip(cache, StripCount);
            strip.Picked += picked => FilePicked?.Invoke(picked);

            playButton.Click += (_, __) => Toggle();
            scrub.Seeking += value =>
            {
                if (video != null)
                {
                    video.Position = TimeSpan.FromTicks((long)(video.Length.Ticks * value));
                    UpdateClock();
                }
            };

            Controls.Add(frame);
            Controls.Add(playButton);
            Controls.Add(scrub);
            Controls.Add(clock);
            Controls.Add(keysHint);
            keysHint.Text = "Space  play / pause        \u2190 \u2192  5 seconds back / ahead        ,  .  one frame back / ahead";
            Controls.Add(strip);
            Controls.Add(side);

            clock.TextAlign = ContentAlignment.MiddleRight;
            uploadRight.TextAlign = ContentAlignment.MiddleRight;
            side.Controls.Add(nameLabel);
            side.Controls.Add(fileLabel);
            foreach (string key in new[] { "Modified", "Size", "Length", "Video" })
            {
                Label left = NewLabel(13, Theme.Muted);
                left.Text = key;
                Label right = NewLabel(13, Theme.Text);
                right.TextAlign = ContentAlignment.TopRight;
                keys.Add(left);
                values.Add(right);
                side.Controls.Add(left);
                side.Controls.Add(right);
            }
            uploadTitle.Font = Ui.Strong(14);
            shareTitle.Font = Ui.Strong(14);
            shareTitle.Text = "Share a smaller copy";
            trimHint.Text = "Pause where it should start or end, then click.";
            sizeCombo.Items.AddRange(SizeChoices);
            int limit = Settings.Load(ctx).ShareLimitMb;
            sizeCombo.SelectedIndex = Math.Max(0, Array.FindIndex(SizeChoices, c => Equals(c.Value, limit)));
            sizeCombo.SelectedIndexChanged += (_, __) =>
            {
                Settings saved = Settings.Load(ctx);
                saved.ShareLimitMb = (int)((Choice)sizeCombo.SelectedItem).Value;
                saved.Save(ctx);
            };
            startButton.Click += (_, __) => SetTrim(true);
            endButton.Click += (_, __) => SetTrim(false);
            shareButton.Click += (_, __) => MakeShareCopy();
            highlightButton.Click += (_, __) => FindHighlights();
            learnButton.Click += (_, __) => LearnKillSound();
            keepButton.Click += (_, __) => SaveHighlights();
            momentList.Watch += WatchMoment;
            momentList.Changed += UpdateKeepButton;
            side.Controls.AddRange(new Control[] { shareTitle, startButton, endButton, trimHint, sizeCombo, shareButton, highlightButton, learnButton, momentList, keepButton, shareStatus, shareRule });
            side.Controls.AddRange(new Control[]
            {
                folderButton, copyButton, favoriteButton, renameButton, removeButton, renameBox, sortButton, rule, uploadTitle, uploadRight, uploadBar, uploadDetail, uploadError, linkBox, uploadButton, deleteButton, cancelButton, uploadFine,
            });

            folderButton.Click += (_, __) => ShowInFolder();
            copyButton.Click += (_, __) => CopyFile();
            favoriteButton.Click += (_, __) =>
            {
                ClipCare.ToggleFavorite(ctx, file.Path);
                UpdateFileButtons();
            };
            removeButton.Click += (_, __) => RemoveFile();
            renameButton.Click += (_, __) => StartRename();
            renameBox.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    FinishRename(true);
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    FinishRename(false);
                }
                else
                {
                    return;
                }
                e.Handled = e.SuppressKeyPress = true;
            };
            renameBox.Leave += (_, __) => FinishRename(true);
            sortButton.Click += (_, __) => SortAsGame();
            uploadButton.Click += (_, __) => UploadClicked();
            cancelButton.Click += (_, __) => uploads.Cancel(UploadPath);
            deleteButton.Click += (_, __) =>
            {
                uploads.Delete(UploadPath);
                UpdateUpload();
            };

            tick.Tick += (_, __) => OnTick();
            copyReset.Tick += (_, __) =>
            {
                copyReset.Stop();
                copyButton.Hint = FileHint;
                UpdateUpload();
            };
            cache.Updated += UpdateDetails;
            uploads.Changed += OnUploadChanged;
        }

        public void Open(List<LibraryFile> files, LibraryFile file, string title)
        {
            this.files = files;
            this.file = file;
            game = title;
            ShowMoments(new List<Moment>());
            learnButton.Label = Highlights.KnowsKillSound(ctx, game) ? "Relearn kill sound" : "Learn kill sound";

            if (video == null)
            {
                video = new VideoHost { Dock = DockStyle.Fill };
                video.Clicked += OnVideoClicked;
                video.Finished += UpdatePlayState;
                video.Opened += () => { UpdateClock(); UpdateTrim(); };
                frame.Padding = new Padding(1);
                frame.Controls.Add(video);
            }

            video.Load(file.Path);
            scrub.Value = 0;
            trimStart = TimeSpan.Zero;
            trimEnd = null;
            if (!sharing)
            {
                shareStatus.Text = "";
            }
            UpdateTrim();
            copyReset.Stop();
            copyButton.Hint = FileHint;
            strip.Set(files, file);
            nameLabel.Text = title;
            fileLabel.Text = file.Name;
            UpdateFileButtons();
            UpdateDetails();
            UpdatePlayState();
            UpdateClock();
            UpdateUpload();
            LayoutAll();
            tick.Start();
        }

        public void Toggle()
        {
            if (video == null)
            {
                return;
            }
            video.Toggle();
            UpdatePlayState();
        }

        public void Skip(int seconds)
        {
            if (video == null || video.Length <= TimeSpan.Zero)
            {
                return;
            }
            TimeSpan to = video.Position + TimeSpan.FromSeconds(seconds);
            video.Position = to < TimeSpan.Zero ? TimeSpan.Zero : to > video.Length ? video.Length : to;
            OnTick();
        }

        public void StepFrame(bool forward)
        {
            video?.StepFrame(forward);
            UpdatePlayState();
            BeginInvoke((Action)OnTick);
        }

        public void Unload()
        {
            tick.Stop();
            video?.Unload();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tick.Dispose();
                copyReset.Dispose();
                cache.Updated -= UpdateDetails;
                uploads.Changed -= OnUploadChanged;
                video?.Unload();
            }
            base.Dispose(disposing);
        }

        private static Label NewLabel(float size, Color color)
        {
            return new Label { Font = Ui.Text(size), ForeColor = color, BackColor = Theme.Background, TextAlign = ContentAlignment.TopLeft, UseMnemonic = false };
        }

        private void LayoutAll()
        {
            int margin = Ui.S(Inset);
            int gap = Ui.S(RowGap);
            int controls = Ui.S(ControlHeight);
            int stripGap = Ui.S(StripGap);
            int width = Math.Max(Ui.S(200), Width - margin * 2 - Ui.S(SideWidth) - Ui.S(SideGap));

            // ultrawide monitors would make the video taller than the window, so width gives way
            int available = Height - margin * 2 - controls - gap * 2 - Ui.S(HintHeight);
            int StripHeight(int w) => (w - stripGap * (StripCount - 1)) / StripCount * 9 / 16;
            if (width * 9 / 16 + StripHeight(width) > available)
            {
                width = (int)((available + stripGap * 0.45) * 16 / 9 / 1.2);
            }
            int videoHeight = width * 9 / 16;

            frame.SetBounds(margin, margin, width, videoHeight);
            int y = frame.Bottom + gap;
            playButton.SetBounds(margin, y, controls, controls);
            int clockWidth = Ui.S(110);
            clock.SetBounds(margin + width - clockWidth, y, clockWidth, controls);
            scrub.SetBounds(playButton.Right + gap, y, width - controls - clockWidth - gap * 2, controls);
            y += controls + Ui.S(4);
            keysHint.SetBounds(margin, y, width, Ui.S(HintHeight - 4));
            y += Ui.S(HintHeight) + gap - Ui.S(4);
            strip.SetBounds(margin, y, width, StripHeight(width));
            strip.Gap = stripGap;

            side.SetBounds(margin + width + Ui.S(SideGap), margin, Ui.S(SideWidth), Height - margin * 2);
            LayoutSide();
        }

        private void LayoutSide()
        {
            int width = side.Width;
            int y = 0;
            nameLabel.SetBounds(0, y, width, Ui.S(28));
            y += Ui.S(30);
            int fileHeight = TextRenderer.MeasureText(fileLabel.Text, fileLabel.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
            fileLabel.SetBounds(0, y, width, fileHeight + 2);
            renameBox.SetBounds(0, y - Ui.S(3), width, Ui.S(28));
            y += Math.Max(fileHeight, renameBox.Visible ? Ui.S(26) : 0) + Ui.S(14);

            for (int i = 0; i < keys.Count; i++)
            {
                keys[i].SetBounds(0, y, Ui.S(90), Ui.S(20));
                values[i].SetBounds(Ui.S(90), y, width - Ui.S(90), Ui.S(20));
                y += Ui.S(25);
            }
            y += Ui.S(8);

            folderButton.SetBounds(0, y, width, Ui.S(44));
            y += Ui.S(52);
            copyButton.SetBounds(0, y, width, Ui.S(44));
            y += Ui.S(52);
            int third = (width - Ui.S(16)) / 3;
            favoriteButton.SetBounds(0, y, third, Ui.S(40));
            renameButton.SetBounds(third + Ui.S(8), y, third, Ui.S(40));
            removeButton.SetBounds(third * 2 + Ui.S(16), y, width - third * 2 - Ui.S(16), Ui.S(40));
            y += Ui.S(48);
            if (desktopSource != null)
            {
                sortButton.SetBounds(0, y, width, Ui.S(40));
                y += Ui.S(48);
            }
            y += Ui.S(8);

            shareRule.SetBounds(0, y, width, 1);
            y += Ui.S(14);
            shareTitle.SetBounds(0, y, width, Ui.S(22));
            y += Ui.S(28);
            int half = (width - Ui.S(8)) / 2;
            startButton.SetBounds(0, y, half, Ui.S(36));
            endButton.SetBounds(half + Ui.S(8), y, width - half - Ui.S(8), Ui.S(36));
            y += Ui.S(40);
            trimHint.SetBounds(0, y, width, Ui.S(18));
            y += Ui.S(24);
            sizeCombo.SetBounds(0, y, width, Ui.S(30));
            y += Ui.S(38);
            shareButton.SetBounds(0, y, width, Ui.S(44));
            y += Ui.S(52);
            highlightButton.SetBounds(0, y, half, Ui.S(40));
            learnButton.SetBounds(half + Ui.S(8), y, width - half - Ui.S(8), Ui.S(40));
            y += Ui.S(46);
            if (moments.Count > 0)
            {
                momentList.SetBounds(0, y, width, Ui.S(MomentList.RowHeight) * moments.Count);
                y += momentList.Height + Ui.S(4);
                keepButton.SetBounds(0, y, width, Ui.S(40));
                y += Ui.S(46);
            }
            int statusHeight = string.IsNullOrEmpty(shareStatus.Text) ? 0 : TextRenderer.MeasureText(shareStatus.Text, shareStatus.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
            shareStatus.SetBounds(0, y, width, statusHeight + 2);
            y += statusHeight + Ui.S(14);
            rule.SetBounds(0, y, width, 1);
            y += Ui.S(14);

            // visible reads false while the page is hidden, so this goes off the state instead
            bool uploading = shownState == UploadState.Uploading;
            bool hasLink = shownState == UploadState.Done || shownState == UploadState.Deleting;
            if (uploading || hasLink)
            {
                uploadTitle.SetBounds(0, y, Ui.S(170), Ui.S(22));
                uploadRight.SetBounds(Ui.S(170), y, width - Ui.S(170), Ui.S(22));
                y += Ui.S(28);
            }
            if (uploading)
            {
                uploadBar.SetBounds(0, y, width, Ui.S(12));
                y += Ui.S(18);
                uploadDetail.SetBounds(0, y, width, Ui.S(20));
                y += Ui.S(24);
            }
            if (hasLink)
            {
                linkBox.SetBounds(0, y, width, Ui.S(30));
                y += linkBox.Height + Ui.S(8);
            }
            if (showError)
            {
                int height = TextRenderer.MeasureText(uploadError.Text, uploadError.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
                uploadError.SetBounds(0, y, width, height + 2);
                y += height + Ui.S(8);
            }
            if (uploading)
            {
                cancelButton.SetBounds(0, y, width, Ui.S(36));
                y += Ui.S(44);
            }
            else
            {
                uploadButton.SetBounds(0, y, width, Ui.S(44));
                y += Ui.S(52);
            }
            if (showDelete)
            {
                deleteButton.SetBounds(0, y, width, Ui.S(36));
                y += Ui.S(44);
            }
            int fineHeight = TextRenderer.MeasureText(uploadFine.Text, uploadFine.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height;
            uploadFine.SetBounds(0, y, width, fineHeight + 2);
        }

        // the preview can fail (a codec windows doesn't have), the file's own length still works
        private TimeSpan ClipLength => video != null && video.Length > TimeSpan.Zero ? video.Length : (file != null ? cache.Get(file.Path)?.Length : null) ?? TimeSpan.Zero;
        private TimeSpan TrimEnd => trimEnd ?? ClipLength;

        private void SetTrim(bool start)
        {
            if (video == null || video.Length <= TimeSpan.Zero)
            {
                shareStatus.Text = "The preview can't play this clip, so trimming isn't available. Making a copy still works for the whole clip.";
                LayoutSide();
                return;
            }
            TimeSpan at = video.Position;
            if (start)
            {
                trimStart = at < TrimEnd ? at : TimeSpan.Zero;
            }
            else
            {
                trimEnd = at > trimStart ? at : (TimeSpan?)null;
            }
            UpdateTrim();
        }

        private void UpdateTrim()
        {
            TimeSpan length = ClipLength;
            startButton.Label = "Start " + Library.LengthText(trimStart);
            endButton.Label = "End " + (length > TimeSpan.Zero ? Library.LengthText(TrimEnd) : "-");
            scrub.SetRange(length.Ticks > 0 ? (double)trimStart.Ticks / length.Ticks : 0, length.Ticks > 0 ? (double)TrimEnd.Ticks / length.Ticks : 1);
        }

        private void MakeShareCopy()
        {
            if (sharing || file == null || video == null)
            {
                return;
            }
            if (ClipLength <= TimeSpan.Zero)
            {
                shareStatus.Text = "Still reading this clip's length, try again in a second.";
                LayoutSide();
                return;
            }
            video.Pause();
            UpdatePlayState();
            sharing = true;
            shareButton.Enabled = false;
            string source = file.Path;
            TimeSpan start = trimStart, end = TrimEnd;
            int limit = (int)((Choice)sizeCombo.SelectedItem).Value;
            Action<string> status = text => BeginInvoke((Action)(() => SetHighlightStatus(text, Theme.Muted)));
            Task.Run(() =>
            {
                ShareEncoder.EnsureFfmpeg(ctx, p => status($"Getting ffmpeg (one time, ~115 MB)... {p:P0}"));
                return ShareEncoder.Make(ctx, source, start, end, limit, p => status($"Making the copy... {p:P0}"));
            }).ContinueWith(task => BeginInvoke((Action)(() =>
            {
                sharing = false;
                shareButton.Enabled = true;
                if (task.IsFaulted)
                {
                    Exception error = task.Exception.GetBaseException();
                    shareStatus.Text = error is SetupException ? error.Message : "Something went wrong: " + error.Message;
                    shareStatus.ForeColor = Theme.ErrorText;
                }
                else
                {
                    string made = task.Result;
                    bool copied = TryCopy(() => Clipboard.SetFileDropList(new StringCollection { made }));
                    shareStatus.Text = $"{Path.GetFileName(made)}, {Library.SizeText(new FileInfo(made).Length)}. "
                        + (copied ? "Copied, paste it in Discord." : "Saved next to the clip.");
                    shareStatus.ForeColor = Theme.Text;
                }
                LayoutSide();
            })));
        }

        // finds the moments and shows them for review, nothing is saved until "Save highlights"
        private void FindHighlights()
        {
            string source = file?.Path;
            double seconds = ClipLength.TotalSeconds;
            string forGame = game;
            RunHighlightTask(status =>
            {
                ShareEncoder.EnsureFfmpeg(ctx, p => status($"Getting ffmpeg (one time, ~115 MB)... {p:P0}"));
                return Highlights.Find(ctx, source, seconds, forGame, status);
            }, found =>
            {
                ShowMoments(found);
                string tip = Highlights.KnowsKillSound(ctx, game) ? "" : " Tip: pause on a kill sound and click Learn kill sound, then kills get found too.";
                return found.Count == 0
                    ? "Nothing stood out." + tip
                    : $"Found {found.Count}. Click one to watch it, untick the ones you don't want, then save." + tip;
            });
        }

        // the game's kill sound, taken from where the video is paused
        private void LearnKillSound()
        {
            if (video == null || video.Length <= TimeSpan.Zero)
            {
                SetHighlightStatus("The preview can't play this clip, so there's no spot to learn from.", Theme.ErrorText);
                return;
            }
            string source = file.Path;
            double at = video.Position.TotalSeconds;
            double seconds = ClipLength.TotalSeconds;
            string forGame = game;
            RunHighlightTask(status =>
            {
                ShareEncoder.EnsureFfmpeg(ctx, p => status($"Getting ffmpeg (one time, ~115 MB)... {p:P0}"));
                return Highlights.LearnKillSound(ctx, source, at, seconds, forGame, status);
            }, count =>
            {
                learnButton.Label = "Relearn kill sound";
                return $"Learned the {forGame} kill sound, it hears {count} kill{(count == 1 ? "" : "s")} in this clip. If that's way off, pause exactly as a kill sound starts and learn it again.";
            });
        }

        private void SaveHighlights()
        {
            List<Moment> kept = moments.Where(m => m.Keep).ToList();
            if (kept.Count == 0)
            {
                return;
            }
            string source = file.Path;
            RunHighlightTask(status => Highlights.Save(ctx, source, kept, status), made =>
            {
                ShowMoments(new List<Moment>());
                return $"Saved {made.Count} highlight{(made.Count == 1 ? "" : "s")} next to this file, named \"(highlight 1)\" and up.";
            });
        }

        private void WatchMoment(Moment moment)
        {
            if (video == null || video.Length <= TimeSpan.Zero)
            {
                return;
            }
            video.Position = TimeSpan.FromSeconds(moment.Start);
            if (!video.Playing)
            {
                video.Toggle();
            }
            UpdatePlayState();
            UpdateClock();
        }

        private void ShowMoments(List<Moment> found)
        {
            moments = found;
            momentList.Show(found);
            momentList.Visible = keepButton.Visible = found.Count > 0;
            double length = Math.Max(1, ClipLength.TotalSeconds);
            scrub.SetMarks(found.Select(m => new KeyValuePair<double, double>(m.Start / length, m.End / length)).ToList());
            UpdateKeepButton();
            LayoutSide();
        }

        private void UpdateKeepButton()
        {
            int count = moments.Count(m => m.Keep);
            keepButton.Label = count == 1 ? "Save 1 highlight" : $"Save {count} highlights";
            keepButton.Enabled = count > 0 && !sharing;
        }

        // progress arrives many times a second, so only a change in height moves the controls below
        private void SetHighlightStatus(string text, Color color)
        {
            if (shareStatus.Text == text && shareStatus.ForeColor == color)
            {
                return;
            }
            int before = StatusHeight();
            shareStatus.Text = text;
            shareStatus.ForeColor = color;
            if (StatusHeight() != before)
            {
                LayoutSide();
            }
        }

        private int StatusHeight()
        {
            return string.IsNullOrEmpty(shareStatus.Text) ? 0 : TextRenderer.MeasureText(shareStatus.Text, shareStatus.Font, new Size(side.Width, 0), TextFormatFlags.WordBreak).Height;
        }

        // ffmpeg work off the ui thread, one at a time, with progress in the status line
        private void RunHighlightTask<T>(Func<Action<string>, T> work, Func<T, string> done)
        {
            if (sharing || file == null)
            {
                return;
            }
            video?.Pause();
            UpdatePlayState();
            sharing = true;
            shareButton.Enabled = highlightButton.Enabled = learnButton.Enabled = keepButton.Enabled = uploadButton.Enabled = false;
            Action<string> status = text => BeginInvoke((Action)(() => SetHighlightStatus(text, Theme.Muted)));
            Task.Run(() => work(status)).ContinueWith(task => BeginInvoke((Action)(() =>
            {
                sharing = false;
                shareButton.Enabled = highlightButton.Enabled = learnButton.Enabled = true;
                UpdateKeepButton();
                UpdateUpload();
                if (task.IsFaulted)
                {
                    Exception error = task.Exception.GetBaseException();
                    SetHighlightStatus(error is SetupException ? error.Message : "Something went wrong: " + error.Message, Theme.ErrorText);
                }
                else
                {
                    SetHighlightStatus(done(task.Result), Theme.Text);
                }
            })));
        }

        private void UpdateDetails()
        {
            if (file == null)
            {
                return;
            }
            MediaInfo info = cache.Get(file.Path);
            values[0].Text = Library.When(file.Modified);
            values[1].Text = Library.SizeText(file.Size);
            values[2].Text = info?.Length != null ? Library.LengthText(info.Length.Value) : "-";
            values[3].Text = info?.VideoText() ?? "-";
            UpdateTrim();
            strip.Invalidate();
        }

        private void OnTick()
        {
            if (video == null)
            {
                return;
            }
            if (!scrub.Dragging && video.Length > TimeSpan.Zero)
            {
                scrub.Value = (double)video.Position.Ticks / video.Length.Ticks;
            }
            UpdateClock();
        }

        private void UpdateClock()
        {
            if (video == null)
            {
                return;
            }
            string text = Library.LengthText(video.Position) + " / " + Library.LengthText(video.Length);
            if (clock.Text != text)
            {
                clock.Text = text;
                UpdateTrim(); // the length can arrive after the video reports it opened
            }
        }

        private void UpdatePlayState()
        {
            playButton.Icon = video != null && video.Playing ? Icons.Pause : Icons.Play;
        }

        private void OnVideoClicked()
        {
            if (video.HasFailed)
            {
                // it can fail because the file got deleted, and then there's nothing to open either
                if (File.Exists(file.Path))
                {
                    Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true })?.Dispose();
                    closeOverlay();
                }
                return;
            }
            Toggle();
        }

        private void UpdateFileButtons()
        {
            bool favorite = ClipCare.IsFavorite(ctx, file.Path);
            favoriteButton.Label = favorite ? "Favorited" : "Favorite";
            favoriteButton.Primary = favorite;
            favoriteButton.Invalidate();
            desktopSource = ClipCare.DesktopSource(ctx, file.Path);
            sortButton.Visible = desktopSource != null;
            sortButton.Label = desktopSource == null ? "" : "It was " + desktopSource + ", sort as a game";
            LayoutSide();
        }

        private void StartRename()
        {
            if (sharing || file == null)
            {
                return;
            }
            renameBox.Text = Path.GetFileNameWithoutExtension(file.Path);
            renameBox.Visible = true;
            fileLabel.Visible = false;
            LayoutSide();
            renameBox.Focus();
            renameBox.SelectAll();
        }

        // the extension stays, only the name before it changes
        private void FinishRename(bool keep)
        {
            if (!renameBox.Visible)
            {
                return;
            }
            string name = renameBox.Text.Trim();
            string current = Path.GetFileNameWithoutExtension(file.Path);
            renameBox.Visible = false;
            fileLabel.Visible = true;
            LayoutSide();
            if (!keep || name == "" || name == current)
            {
                return;
            }
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith("."))
            {
                SetHighlightStatus("A name can't have \\ / : * ? \" < > | in it.", Theme.ErrorText);
                return;
            }
            string target = Path.Combine(Path.GetDirectoryName(file.Path), name + Path.GetExtension(file.Path));
            if (File.Exists(target))
            {
                SetHighlightStatus("There's already a file called that here.", Theme.ErrorText);
                return;
            }
            string old = file.Path;
            video?.Unload();
            try
            {
                File.Move(old, target);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                SetHighlightStatus("Couldn't rename it: " + e.Message, Theme.ErrorText);
                video?.Load(old);
                return;
            }
            if (ClipCare.IsFavorite(ctx, old))
            {
                ClipCare.ToggleFavorite(ctx, old);
                ClipCare.ToggleFavorite(ctx, target);
            }
            // the same object is in the folder list, so the gallery sees the new name too
            file.Path = target;
            file.Name = Path.GetFileName(target);
            Open(files, file, nameLabel.Text);
            SetHighlightStatus("Renamed.", Theme.Text);
            FileRenamed?.Invoke();
        }

        // to the recycle bin, so a wrong click can be undone there
        private void RemoveFile()
        {
            if (sharing)
            {
                return;
            }
            LibraryFile gone = file;
            video?.Unload();
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(gone.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is OperationCanceledException)
            {
                SetHighlightStatus("Couldn't delete it: " + e.Message, Theme.ErrorText);
                video?.Load(gone.Path);
                return;
            }
            if (ClipCare.IsFavorite(ctx, gone.Path))
            {
                ClipCare.ToggleFavorite(ctx, gone.Path);
            }
            FileGone?.Invoke(gone);
        }

        private void SortAsGame()
        {
            if (sharing || desktopSource == null)
            {
                return;
            }
            LibraryFile moved = file;
            string game = desktopSource;
            video?.Unload();
            try
            {
                ClipCare.SortAsGame(ctx, Settings.Load(ctx).ClipsDir, game);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                SetHighlightStatus("Couldn't move it: " + e.Message, Theme.ErrorText);
                video?.Load(moved.Path);
                return;
            }
            FileGone?.Invoke(moved);
        }

        private void ShowInFolder()
        {
            Process.Start("explorer.exe", "/select,\"" + file.Path + "\"")?.Dispose();
            closeOverlay();
        }

        private void CopyFile()
        {
            if (TryCopy(() => Clipboard.SetFileDropList(new StringCollection { file.Path })))
            {
                copyButton.Hint = "copied, paste it anywhere";
                copyReset.Stop();
                copyReset.Start();
            }
        }

        private static bool TryCopy(Action copy)
        {
            try
            {
                copy();
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        private void UploadClicked()
        {
            UploadJob job = uploads.Get(UploadPath);
            if (job != null && job.State == UploadState.Deleting)
            {
                return;
            }
            if (job != null && job.State == UploadState.Done)
            {
                if (TryCopy(() => Clipboard.SetText(job.Link)))
                {
                    uploadButton.Label = "Link copied";
                    copyReset.Stop();
                    copyReset.Start();
                }
                return;
            }
            if (!Settings.Load(ctx).KappaNoticeSeen)
            {
                if (!confirmUpload())
                {
                    return;
                }
                Settings settings = Settings.Load(ctx);
                settings.KappaNoticeSeen = true;
                settings.Save(ctx);
            }
            if (UploadSize > Uploader.MaxBytes)
            {
                ShrinkThenUpload();
                return;
            }
            uploads.Start(UploadPath, UploadSize);
            UpdateUpload();
        }

        // over kappa.lol's 100 MB: a share copy first (trim and size from above, never over the cap), then that uploads
        private void ShrinkThenUpload()
        {
            string source = file.Path;
            TimeSpan start = trimStart, end = TrimEnd;
            int picked = (int)((Choice)sizeCombo.SelectedItem).Value;
            int limit = picked <= 0 || picked > UploadCopyMb ? UploadCopyMb : picked;
            RunHighlightTask(status =>
            {
                ShareEncoder.EnsureFfmpeg(ctx, p => status($"Getting ffmpeg (one time, ~115 MB)... {p:P0}"));
                return ShareEncoder.Make(ctx, source, start, end, limit, p => status($"Making a smaller copy to upload... {p:P0}"));
            }, made =>
            {
                uploadCopies[source] = made;
                if (file != null && file.Path == source)
                {
                    uploads.Start(made, new FileInfo(made).Length);
                    UpdateUpload();
                }
                return $"Uploading {Path.GetFileName(made)}, {Library.SizeText(new FileInfo(made).Length)}.";
            });
        }

        private void OnUploadChanged(UploadJob job)
        {
            if (file != null && string.Equals(job.Path, UploadPath, StringComparison.OrdinalIgnoreCase))
            {
                UpdateUpload();
            }
        }


        private void UpdateUpload()
        {
            UploadJob job = uploads.Get(UploadPath);
            UploadState state = job?.State ?? UploadState.Idle;
            bool tooBig = UploadSize > Uploader.MaxBytes;
            bool hasLink = state == UploadState.Done || state == UploadState.Deleting;
            shownState = state;
            showError = !string.IsNullOrEmpty(job?.Error) && (hasLink || state == UploadState.Failed);
            showDelete = hasLink && !string.IsNullOrEmpty(job.Key);

            uploadTitle.Visible = state == UploadState.Uploading || hasLink;
            uploadRight.Visible = uploadTitle.Visible;
            uploadBar.Visible = state == UploadState.Uploading;
            uploadDetail.Visible = state == UploadState.Uploading;
            uploadError.Visible = showError;
            linkBox.Visible = hasLink;
            uploadButton.Visible = state != UploadState.Uploading;
            deleteButton.Visible = showDelete;
            deleteButton.Enabled = state == UploadState.Done;
            deleteButton.Label = state == UploadState.Deleting ? "Deleting..." : DeleteLabel;
            cancelButton.Visible = state == UploadState.Uploading;
            uploadButton.Enabled = state != UploadState.Deleting && !sharing;
            uploadFine.Text = tooBig && state == UploadState.Idle
                ? "Over kappa.lol's 100 MB limit, so a smaller copy is made first, using the trim and size above. " + HostLine
                : hasLink ? HostLine : IdleLine;
            uploadError.Text = job?.Error ?? "";

            switch (state)
            {
                case UploadState.Uploading:
                    int percent = job.Total > 0 ? (int)(job.Sent * 100 / job.Total) : 0;
                    uploadTitle.Text = "Uploading to kappa.lol";
                    uploadRight.Text = percent + "%";
                    uploadBar.Value = percent / 100.0;
                    uploadDetail.Text = Library.SizeText(job.Sent) + " of " + Library.SizeText(job.Total);
                    break;
                case UploadState.Done:
                case UploadState.Deleting:
                    uploadTitle.Text = "Link ready";
                    uploadRight.Text = "";
                    linkBox.Text = job.Link;
                    SetUploadButton("Copy link", Icons.Link);
                    break;
                case UploadState.Failed:
                    SetUploadButton("Retry", Icons.Retry);
                    break;
                default:
                    SetUploadButton(tooBig ? "Shrink and upload to kappa.lol" : UploadLabel, Icons.Upload);
                    break;
            }
            LayoutSide();
        }

        private void SetUploadButton(string label, string icon)
        {
            uploadButton.Label = label;
            uploadButton.SetIcon(icon);
        }
    }

    // the row of neighbouring clips under the video
    public sealed class ThumbStrip : PaintedControl
    {
        private readonly MediaCache cache;
        private readonly int slots;
        private List<LibraryFile> files = new List<LibraryFile>();
        private LibraryFile current;
        private int first;
        private int hover = -1;

        public event Action<LibraryFile> Picked;

        public int Gap { get; set; }

        public ThumbStrip(MediaCache cache, int slots)
        {
            this.cache = cache;
            this.slots = slots;
        }

        public void Set(List<LibraryFile> files, LibraryFile current)
        {
            this.files = files;
            this.current = current;
            int index = files.FindIndex(file => file.Path == current.Path);
            first = Math.Max(0, Math.Min(index - slots / 2, files.Count - slots));
            Invalidate();
        }

        private Rectangle Slot(int position)
        {
            int width = (Width - Gap * (slots - 1)) / slots;
            return new Rectangle(position * (width + Gap), 0, width, Height - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            for (int i = 0; i < slots && first + i < files.Count; i++)
            {
                LibraryFile file = files[first + i];
                bool selected = current != null && file.Path == current.Path;
                Color border = selected || i == hover ? Theme.Accent : Theme.Border;
                VideoThumb.Draw(graphics, Slot(i), cache.Get(file.Path), border, selected ? Ui.S(2) : 1F, 8, 5, 11);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int found = -1;
            for (int i = 0; i < slots && first + i < files.Count; i++)
            {
                if (Slot(i).Contains(e.Location))
                {
                    found = i;
                }
            }
            Change(ref hover, found);
            Cursor = found >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (hover >= 0 && first + hover < files.Count)
            {
                Picked?.Invoke(files[first + hover]);
            }
        }
    }
}
