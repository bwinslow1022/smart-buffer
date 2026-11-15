using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SmartBuffer
{
    // ShadowPlay-style "saved" card in the top-right corner of the monitor you're playing on.
    // It never takes focus and clicks pass through it, so it can't interrupt a game.
    public sealed class SaveToast : Form
    {
        private const int ShowMs = 2600;
        private const int FadeMs = 220;
        private const int FrameMs = 15;
        private const int EdgeGap = 24;
        private const int ExTopmost = 0x8, ExTransparent = 0x20, ExToolWindow = 0x80, ExLayered = 0x80000, ExNoActivate = 0x08000000;
        private const int CornerPreference = 33, RoundCorners = 2;
        private static readonly IntPtr TopmostHandle = new IntPtr(-1);
        private const uint NoSize = 0x1, NoMove = 0x2, NoActivate = 0x10, ShowWindowFlag = 0x40;
        private static readonly Color Good = Color.FromArgb(52, 211, 153);

        private static SaveToast current;

        private readonly string title;
        private readonly string detail;
        private readonly string icon;
        private readonly Timer timer = new Timer { Interval = FrameMs };
        private DateTime shownAt;

        private SaveToast(string title, string detail, string icon)
        {
            this.title = title;
            this.detail = detail;
            this.icon = icon;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Background;
            DoubleBuffered = true;
            Opacity = 0;
            timer.Tick += (_, __) => Animate();
        }

        /// Shows the card, replacing one that's still on screen.
        public static void Show(string title, string detail, string icon)
        {
            current?.Close();
            IntPtr game = GetForegroundWindow();
            Ui.UseMonitorOf(game);
            Rectangle area = (game != IntPtr.Zero ? Screen.FromHandle(game) : Screen.PrimaryScreen).Bounds;
            var toast = new SaveToast(title, detail, icon);
            toast.Size = new Size(Ui.S(300), Ui.S(64));
            toast.Location = new Point(area.Right - toast.Width - Ui.S(EdgeGap), area.Top + Ui.S(EdgeGap));
            current = toast;
            toast.shownAt = DateTime.UtcNow;
            ShowWindow(toast.Handle, 4); // SW_SHOWNOACTIVATE
            SetWindowPos(toast.Handle, TopmostHandle, 0, 0, 0, 0, NoSize | NoMove | NoActivate | ShowWindowFlag);
            toast.timer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= ExTopmost | ExTransparent | ExToolWindow | ExLayered | ExNoActivate;
                return p;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int corner = RoundCorners;
            DwmSetWindowAttribute(Handle, CornerPreference, ref corner, sizeof(int));
        }

        private void Animate()
        {
            double ms = (DateTime.UtcNow - shownAt).TotalMilliseconds;
            if (ms < FadeMs)
            {
                Opacity = ms / FadeMs;
            }
            else if (ms < ShowMs)
            {
                Opacity = 1;
            }
            else if (ms < ShowMs + FadeMs)
            {
                Opacity = 1 - (ms - ShowMs) / FadeMs;
            }
            else
            {
                Close();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Ui.DrawRound(g, Theme.Border, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(8));
            var badge = new Rectangle(Ui.S(14), (Height - Ui.S(34)) / 2, Ui.S(34), Ui.S(34));
            Ui.FillRound(g, Color.FromArgb(40, Good), badge, Ui.S(17));
            Ui.DrawIcon(g, icon, badge, Good, Ui.S(15));
            int left = badge.Right + Ui.S(12);
            Ui.DrawLine(g, title, Ui.Strong(14), new Rectangle(left, Ui.S(12), Width - left - Ui.S(14), Ui.S(20)), Theme.Text);
            Ui.DrawLine(g, detail, Ui.Text(12.5F), new Rectangle(left, Ui.S(33), Width - left - Ui.S(14), Ui.S(18)), Theme.Muted);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            if (current == this)
            {
                current = null;
            }
            base.OnFormClosed(e);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
