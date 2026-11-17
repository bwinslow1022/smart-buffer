using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace SmartBuffer
{
    // Uses the Windows 10+ media player, which picks up Store codecs (AV1, HEVC) that wpf's player ignores.
    // The video is drawn by windows composition into its own child window, so ordinary controls can sit on top.
    public sealed class VideoHost : Control
    {
        private const int BadgeSize = 72;

        [ThreadStatic] private static Compositor compositor;
        [ThreadStatic] private static IntPtr queueController; // keeps the thread's dispatcher queue alive

        private readonly Control screen = new Control { Dock = DockStyle.Fill, BackColor = Color.Black };
        private readonly Control badge = new Badge();
        private readonly Label problem = new Label
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            ForeColor = Color.FromArgb(161, 161, 170),
            Font = Ui.Text(14),
            TextAlign = ContentAlignment.MiddleCenter,
            Padding = new Padding(32),
            Text = "This video can't play here, Windows may be missing its codec.\nClick to open it in your default player.",
            Visible = false,
        };

        private SpriteVisual visual;
        private DesktopWindowTarget target;
        private MediaPlayer player;
        private MediaPlayerSurface surface;
        private bool previewing;

        /// Muted, looping, from a third of the way in, no play badge. For hovering a clip in the library.
        public void LoadPreview(string path)
        {
            Load(path);
            previewing = true;
            badge.Visible = false;
            player.Volume = 0;
            player.IsLoopingEnabled = true;
        }

        public event Action Clicked;
        public event Action Finished;
        public event Action Opened;
        public event Action Failed;

        public bool Playing { get; private set; }
        public bool HasFailed { get; private set; }

        public TimeSpan Length => player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;

        public TimeSpan Position
        {
            get => player?.PlaybackSession.Position ?? TimeSpan.Zero;
            set
            {
                if (player != null)
                {
                    player.PlaybackSession.Position = value;
                }
            }
        }

        public VideoHost()
        {
            BackColor = Color.Black;
            Controls.Add(screen);
            Controls.Add(badge);
            Controls.Add(problem);
            badge.BringToFront();
            problem.BringToFront();
            screen.MouseUp += OnMouseUp;
            badge.MouseUp += OnMouseUp;
            problem.MouseUp += OnMouseUp;
            screen.Resize += (_, __) => FitSurface();
            Resize += (_, __) => badge.Location = new Point((Width - badge.Width) / 2, (Height - badge.Height) / 2);
        }

        public void Load(string path)
        {
            Unload();
            previewing = false;
            HasFailed = false;
            problem.Visible = false;
            badge.Visible = true;
            EnsureTarget();

            player = new MediaPlayer { AutoPlay = false, Volume = 1 };
            MediaPlayer current = player;
            player.MediaOpened += (_, __) => OnUi(current, () =>
            {
                if (previewing)
                {
                    current.PlaybackSession.Position = TimeSpan.FromTicks(current.PlaybackSession.NaturalDuration.Ticks / 3);
                    current.Play();
                    Playing = true;
                    return;
                }
                current.PlaybackSession.Position = TimeSpan.Zero; // a seek draws the first frame instead of black
                Opened?.Invoke();
            });
            player.MediaEnded += (_, __) => OnUi(current, () =>
            {
                Pause();
                current.PlaybackSession.Position = TimeSpan.Zero;
                Finished?.Invoke();
            });
            player.MediaFailed += (_, __) => OnUi(current, () =>
            {
                HasFailed = true;
                Playing = false;
                badge.Visible = false;
                problem.Visible = !previewing;
                Failed?.Invoke();
            });
            player.Source = MediaSource.CreateFromUri(new Uri(path));
            surface = player.GetSurface(compositor);
            CompositionSurfaceBrush brush = compositor.CreateSurfaceBrush(surface.CompositionSurface);
            brush.Stretch = CompositionStretch.Uniform;
            visual.Brush = brush;
            FitSurface();
        }

        public void Toggle()
        {
            if (Playing)
            {
                Pause();
            }
            else if (!HasFailed && player != null)
            {
                player.Play();
                Playing = true;
                badge.Visible = false;
            }
        }

        // pauses on the next or previous frame, the play badge stays hidden so the frame can be seen
        public void StepFrame(bool forward)
        {
            if (player == null || HasFailed)
            {
                return;
            }
            if (forward)
            {
                player.StepForwardOneFrame();
            }
            else
            {
                player.StepBackwardOneFrame();
            }
            Playing = false;
            badge.Visible = false;
        }

        public void Pause()
        {
            player?.Pause();
            Playing = false;
            if (!HasFailed)
            {
                badge.Visible = true;
            }
        }

        // closes the file too, otherwise windows can't move or delete it afterwards
        public void Unload()
        {
            Playing = false;
            if (visual != null)
            {
                visual.Brush = null;
            }
            surface?.Dispose();
            surface = null;
            if (player != null)
            {
                (player.Source as IDisposable)?.Dispose();
                player.Dispose();
                player = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Unload();
                target?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Clicked?.Invoke();
            }
        }

        // media events arrive on a worker thread; a stale player's events are dropped
        private void OnUi(MediaPlayer from, Action work)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            BeginInvoke(new Action(() =>
            {
                if (from == player)
                {
                    work();
                }
            }));
        }

        private void FitSurface()
        {
            if (player != null && screen.Width > 0 && screen.Height > 0)
            {
                player.SetSurfaceSize(new Windows.Foundation.Size(screen.Width, screen.Height));
            }
        }

        private void EnsureTarget()
        {
            if (target != null)
            {
                return;
            }
            if (compositor == null)
            {
                if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
                {
                    CreateDispatcherQueueController(new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 }, out queueController);
                }
                compositor = new Compositor();
            }
            ((ICompositorDesktopInterop)(object)compositor).CreateDesktopWindowTarget(screen.Handle, true, out IntPtr raw);
            target = (DesktopWindowTarget)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);
            visual = compositor.CreateSpriteVisual();
            visual.RelativeSizeAdjustment = System.Numerics.Vector2.One;
            target.Root = visual;
        }

        private sealed class Badge : Control
        {
            public Badge()
            {
                int size = Ui.S(BadgeSize);
                Size = new Size(size, size);
                BackColor = Color.FromArgb(24, 24, 27);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                var round = new GraphicsPath();
                round.AddEllipse(0, 0, size, size);
                Region = new Region(round); // its own window over the video, so the circle is cut by a region
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int x = Width / 2 - Ui.S(7), y = Height / 2 - Ui.S(14);
                using (var brush = new SolidBrush(Color.FromArgb(244, 244, 245)))
                {
                    e.Graphics.FillPolygon(brush, new[] { new Point(x, y), new Point(x, y + Ui.S(28)), new Point(x + Ui.S(24), y + Ui.S(14)) });
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct QueueOptions
        {
            public int Size, ThreadType, ApartmentType; // DQTYPE_THREAD_CURRENT, DQTAT_COM_STA
        }

        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
        }

        [DllImport("CoreMessaging.dll", PreserveSig = false)]
        private static extern void CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
    }
}
