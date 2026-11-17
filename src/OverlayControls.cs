using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SmartBuffer
{
    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(24, 24, 27);
        public static readonly Color Panel = Color.FromArgb(39, 39, 42);
        public static readonly Color Bar = Color.FromArgb(31, 31, 35);
        public static readonly Color Hover = Color.FromArgb(48, 48, 52);
        public static readonly Color Border = Color.FromArgb(63, 63, 70);
        public static readonly Color Slash = Color.FromArgb(113, 113, 122);
        public static readonly Color Text = Color.FromArgb(244, 244, 245);
        public static readonly Color Muted = Color.FromArgb(161, 161, 170);
        public static readonly Color Accent = Color.FromArgb(59, 130, 246);
        public static readonly Color AccentHover = Color.FromArgb(96, 165, 250);
        public static readonly Color Knob = Color.FromArgb(250, 250, 250);
        public static readonly Color Danger = Color.FromArgb(239, 68, 68);
        public static readonly Color ErrorText = Color.FromArgb(252, 165, 165);
        public static readonly Color FolderBack = Color.FromArgb(29, 78, 216);
        public static readonly Color FolderEdge = Color.FromArgb(147, 197, 253);
        public static readonly Color Pink = Color.FromArgb(236, 72, 153);
        public static readonly Color Highlight = Color.FromArgb(251, 191, 36);
        public static readonly Color Mint = Color.FromArgb(52, 211, 153);

        public static Color Mix(Color a, Color b, float amount)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
        }
    }

    public sealed class Choice
    {
        public string Label { get; }
        public object Value { get; }

        public Choice(string label, object value)
        {
            Label = label;
            Value = value;
        }

        public override string ToString()
        {
            return Label;
        }
    }

    // everything i draw myself starts here, solid background and hover tracking
    public abstract class PaintedControl : Control
    {
        protected bool Hovered { get; private set; }
        protected bool Pressed { get; private set; }

        protected PaintedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = Theme.Background;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Hovered = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Pressed = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Pressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected void Change<T>(ref T field, T value)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                Invalidate();
            }
        }
    }

    public sealed class ToggleSwitch : PaintedControl
    {
        private bool isOn;

        public event EventHandler Toggled;
        public Color OnColor { get; set; } = Theme.Accent;

        public bool Checked
        {
            get => isOn;
            set => Change(ref isOn, value);
        }

        public ToggleSwitch()
        {
            Size = new Size(Ui.S(44), Ui.S(24));
            Cursor = Cursors.Hand;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Enabled)
            {
                Checked = !Checked;
                Toggled?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(BackColor);

            int height = Height - 1;
            int width = Width - 1;
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, height, height, 90, 180);
                path.AddArc(width - height, 0, height, height, 270, 180);
                path.CloseFigure();
                Color track = isOn ? OnColor : Theme.Border;
                if (!Enabled)
                {
                    track = Color.FromArgb(120, track);
                }
                using (var brush = new SolidBrush(track))
                {
                    graphics.FillPath(brush, path);
                }
            }

            int gap = Math.Max(3, height / 8);
            int knob = height - gap * 2;
            int x = isOn ? width - knob - gap : gap;
            using (var brush = new SolidBrush(Theme.Knob))
            {
                graphics.FillEllipse(brush, x, gap, knob, knob);
            }
        }
    }

    public sealed class FlatCombo : ComboBox
    {
        public FlatCombo()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Ui.Text(13);
            ItemHeight = Ui.S(22);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Rectangle box = ClientRectangle;
            using (var fill = new SolidBrush(BackColor))
            using (var border = new Pen(Focused || DroppedDown ? Theme.Accent : Theme.Border))
            using (var arrow = new Pen(Enabled ? Theme.Text : Theme.Muted, 1.5F))
            {
                graphics.FillRectangle(fill, box);
                graphics.DrawRectangle(border, box.Left, box.Top, box.Width - 1, box.Height - 1);
                int arrowWidth = SystemInformation.VerticalScrollBarWidth + 2;
                string text = SelectedIndex >= 0 ? Items[SelectedIndex].ToString() : "";
                TextRenderer.DrawText(graphics, text, Font, new Rectangle(Ui.S(6), 0, box.Width - arrowWidth - Ui.S(6), box.Height), Enabled ? Theme.Text : Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int centerX = box.Right - arrowWidth / 2 - 1;
                int centerY = box.Height / 2;
                graphics.DrawLines(arrow, new[]
                {
                    new Point(centerX - 4, centerY - 2),
                    new Point(centerX, centerY + 2),
                    new Point(centerX + 4, centerY - 2),
                });
            }
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }

        protected override void OnDropDownClosed(EventArgs e)
        {
            base.OnDropDownClosed(e);
            Invalidate();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0)
            {
                return;
            }
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var brush = new SolidBrush(selected ? Theme.Accent : Theme.Panel))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }
            TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, e.Bounds, Enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // click it and press the combo you want
    public sealed class HotkeyBox : TextBox
    {
        public const string NoneText = "None";

        private const string Prompt = "Press keys...";
        private const string ClearablePrompt = "Press keys, or Delete for none";

        private readonly bool clearable;
        private string saved;

        public event Action<Hotkey> Captured;
        public event Action<bool> CaptureChanged;

        public HotkeyBox(bool clearable)
        {
            this.clearable = clearable;
            ReadOnly = true;
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Ui.Text(13);
            Cursor = Cursors.Hand;
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            saved = Text;
            Text = clearable ? ClearablePrompt : Prompt;
            CaptureChanged?.Invoke(true);
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            if (Text == Prompt || Text == ClearablePrompt)
            {
                Text = saved;
            }
            CaptureChanged?.Invoke(false);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Escape)
            {
                Parent?.Focus();
                return true;
            }
            if (code == Keys.Tab)
            {
                return base.ProcessCmdKey(ref message, keyData);
            }
            if (clearable && keyData == Keys.Delete)
            {
                Text = NoneText;
                saved = Text;
                Captured?.Invoke(null);
                Parent?.Focus();
                return true;
            }

            Hotkey hotkey = Hotkey.FromKeyData(keyData);
            if (hotkey != null)
            {
                Text = hotkey.ToString();
                saved = Text;
                Captured?.Invoke(hotkey);
                Parent?.Focus();
            }
            return true;
        }
    }

    public sealed class IconButton : PaintedControl
    {
        private string icon;

        public string Icon
        {
            get => icon;
            set => Change(ref icon, value);
        }

        public float IconSize { get; set; } = 14;
        public Color IconColor { get; set; } = Theme.Text;
        public Color HoverColor { get; set; } = Theme.Border;
        public bool Boxed { get; set; }

        public IconButton(string icon)
        {
            this.icon = icon;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            if (Boxed)
            {
                Ui.FillRound(graphics, Hovered ? Theme.Hover : Theme.Panel, box, Ui.S(8));
                Ui.DrawRound(graphics, Theme.Border, box, Ui.S(8));
            }
            else if (Hovered && Enabled)
            {
                Ui.FillRound(graphics, HoverColor, box, Ui.S(8));
            }
            Ui.DrawIcon(graphics, icon, ClientRectangle, Enabled ? IconColor : Theme.Border, IconSize);
        }
    }

    public sealed class ActionButton : PaintedControl
    {
        private string label;
        private string hint;
        private string icon;

        public bool Primary { get; set; }

        public string Label
        {
            get => label;
            set => Change(ref label, value);
        }

        public string Hint
        {
            get => hint;
            set => Change(ref hint, value);
        }

        public void SetIcon(string value)
        {
            Change(ref icon, value);
        }

        public ActionButton(string label, string icon = null, bool primary = false, string hint = null)
        {
            this.label = label;
            this.icon = icon;
            this.hint = hint;
            Primary = primary;
            // apply has to pull focus off a hotkey box so its capture ends first
            SetStyle(ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            Font font = Ui.Strong(14);
            Font small = Ui.Text(12);
            Color textColor = Enabled ? (Primary ? Color.White : Theme.Text) : Theme.Muted;

            if (Primary && Enabled)
            {
                Ui.FillRound(graphics, Hovered ? Theme.AccentHover : Theme.Accent, box, Ui.S(8));
            }
            else
            {
                Ui.FillRound(graphics, Hovered && Enabled ? Theme.Hover : Theme.Panel, box, Ui.S(8));
                Ui.DrawRound(graphics, Theme.Border, box, Ui.S(8));
            }

            int iconWidth = icon == null ? 0 : Ui.S(26);
            int textWidth = Ui.Measure(label, font);

            if (Primary)
            {
                int hintWidth = string.IsNullOrEmpty(hint) ? 0 : Ui.S(8) + Ui.Measure(hint, small);
                int x = (Width - iconWidth - textWidth - hintWidth) / 2;
                if (icon != null)
                {
                    Ui.DrawIcon(graphics, icon, new Rectangle(x, 0, Ui.S(18), Height), textColor, 14);
                    x += iconWidth;
                }
                Ui.DrawLine(graphics, label, font, new Rectangle(x, 0, textWidth + 2, Height), textColor);
                if (hintWidth > 0)
                {
                    Ui.DrawLine(graphics, hint, small, new Rectangle(x + textWidth + Ui.S(8), 0, hintWidth, Height), textColor);
                }
                return;
            }

            if (icon == null && string.IsNullOrEmpty(hint))
            {
                Ui.DrawLine(graphics, label, font, ClientRectangle, textColor, TextFormatFlags.HorizontalCenter);
                return;
            }

            int left = Ui.S(14);
            if (icon != null)
            {
                Ui.DrawIcon(graphics, icon, new Rectangle(left, 0, Ui.S(18), Height), textColor, 14);
                left += iconWidth;
            }
            Ui.DrawLine(graphics, label, font, new Rectangle(left, 0, textWidth + 2, Height), textColor);
            if (!string.IsNullOrEmpty(hint))
            {
                Ui.DrawLine(graphics, hint, small, new Rectangle(0, 0, Width - Ui.S(14), Height), Theme.Muted, TextFormatFlags.Right);
            }
        }
    }

    public sealed class LibraryTab : PaintedControl
    {
        private readonly string label;
        private readonly string icon;
        private bool selected;

        public bool Selected
        {
            get => selected;
            set => Change(ref selected, value);
        }

        public LibraryTab(string label, string icon = null)
        {
            this.label = label;
            this.icon = icon;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            if (selected || Hovered)
            {
                Ui.FillRound(graphics, selected ? Theme.Border : Theme.Hover, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(8));
            }
            Color color = selected || Hovered ? Theme.Text : Theme.Muted;
            Font font = Ui.Strong(15);
            int textWidth = Ui.Measure(label, font);
            int iconWidth = icon == null ? 0 : Ui.S(26);
            int x = (Width - iconWidth - textWidth) / 2;
            if (icon != null)
            {
                Ui.DrawIcon(graphics, icon, new Rectangle(x, 0, Ui.S(18), Height), color, 15);
            }
            Ui.DrawLine(graphics, label, font, new Rectangle(x + iconWidth, 0, textWidth + 2, Height), color);
        }
    }

    // one flat line in the sidebar, icon then title and hotkey, with something on the right
    public sealed class SidebarRow : PaintedControl
    {
        private string title;
        private string subtitle;
        private string trail;
        private Color trailColor = Theme.Text;

        public string Icon { get; set; }
        public Action<Graphics, Rectangle> IconPainter { get; set; }
        public bool Clickable { get; set; }
        public bool Card { get; set; }
        // the title part on top, anything below it (like the save button) is the row's own child
        public int HeadHeight { get; set; }
        // color of the icon badge, and of the outline while Active
        public Color Tint { get; set; } = Theme.Accent;
        private bool active;

        public bool Active
        {
            get => active;
            set
            {
                Change(ref active, value);
                foreach (Control child in Controls)
                {
                    child.BackColor = Fill;
                }
            }
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            e.Control.BackColor = Fill;
        }

        // child controls sit on the card, so they need its fill as their background
        public Color Fill => Card ? (active ? Theme.Mix(Theme.Panel, Tint, 0.08F) : Theme.Panel) : BackColor;

        public string Subtitle
        {
            get => subtitle;
            set => Change(ref subtitle, value);
        }

        public string Trail
        {
            get => trail;
            set => Change(ref trail, value);
        }

        public Color TrailColor
        {
            get => trailColor;
            set => Change(ref trailColor, value);
        }

        public SidebarRow(string title)
        {
            this.title = title;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            int head = HeadHeight > 0 ? HeadHeight : Height;
            int left = Ui.S(54);
            if (Card)
            {
                graphics.Clear(BackColor);
                var box = new Rectangle(0, 0, Width - 1, Height - 1);
                bool hot = Clickable && Hovered;
                Ui.FillRound(graphics, hot ? Theme.Mix(Theme.Hover, Tint, 0.06F) : Fill, box, Ui.S(12));
                Color edge = active ? Theme.Mix(Theme.Border, Tint, 0.75F) : hot ? Theme.Mix(Theme.Border, Tint, 0.45F) : Theme.Border;
                Ui.DrawRound(graphics, edge, box, Ui.S(12), active ? Ui.Scale * 1.5F : 1F);

                // a round tinted badge behind the icon
                int size = Ui.S(38);
                var badge = new Rectangle(Ui.S(12), (head - size) / 2, size, size);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(Color.FromArgb(active ? 70 : 40, Tint)))
                {
                    graphics.FillEllipse(brush, badge);
                }
                if (IconPainter != null)
                {
                    IconPainter(graphics, badge);
                }
                else if (Icon != null)
                {
                    Ui.DrawIcon(graphics, Icon, badge, Tint, 17);
                }
                left = Ui.S(62);
            }
            else
            {
                graphics.Clear(Clickable && Hovered ? Theme.Panel : BackColor);
                var iconBox = new Rectangle(Ui.S(16), 0, Ui.S(24), head);
                if (IconPainter != null)
                {
                    IconPainter(graphics, iconBox);
                }
                else if (Icon != null)
                {
                    Ui.DrawIcon(graphics, Icon, iconBox, Theme.Text, 18);
                }
            }

            int width = Width - left - Ui.S(60);
            int middle = head / 2;
            Ui.DrawLine(graphics, title, Ui.Strong(16), new Rectangle(left, middle - Ui.S(22), width, Ui.S(23)), Theme.Text);
            Ui.DrawLine(graphics, subtitle ?? "", Ui.Text(13), new Rectangle(left, middle + Ui.S(1), Width - left - Ui.S(16), Ui.S(19)), Theme.Muted);

            if (trail != null)
            {
                Ui.DrawIcon(graphics, trail, new Rectangle(Width - Ui.S(38), 0, Ui.S(22), head), trailColor, 15);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            if (Clickable)
            {
                base.OnClick(e);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            Cursor = Clickable ? Cursors.Hand : Cursors.Default;
            base.OnMouseEnter(e);
        }
    }

    public sealed class SegmentToggle : PaintedControl
    {
        private readonly string[] options;
        private int selected;
        private int hover = -1;

        public event Action Changed;

        public int Selected
        {
            get => selected;
            set => Change(ref selected, value);
        }

        public SegmentToggle(params string[] options)
        {
            this.options = options;
            Cursor = Cursors.Hand;
            int width = Ui.S(8);
            foreach (string option in options)
            {
                width += Ui.Measure(option, Ui.Text(13)) + Ui.S(24) + Ui.S(2);
            }
            Size = new Size(width, Ui.S(46));
        }

        private Rectangle Segment(int index)
        {
            int x = Ui.S(4);
            for (int i = 0; i < index; i++)
            {
                x += Ui.Measure(options[i], Ui.Text(13)) + Ui.S(26);
            }
            return new Rectangle(x, Ui.S(4), Ui.Measure(options[index], Ui.Text(13)) + Ui.S(24), Height - Ui.S(8) - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            var box = new Rectangle(0, 0, Width - 1, Height - 1);
            Ui.FillRound(graphics, Theme.Background, box, Ui.S(9));
            Ui.DrawRound(graphics, Theme.Border, box, Ui.S(9));
            for (int i = 0; i < options.Length; i++)
            {
                Rectangle segment = Segment(i);
                if (i == selected)
                {
                    Ui.FillRound(graphics, Theme.Border, segment, Ui.S(6));
                }
                Color color = i == selected || i == hover ? Theme.Text : Theme.Muted;
                Ui.DrawLine(graphics, options[i], Ui.Text(13), segment, color, TextFormatFlags.HorizontalCenter);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int found = -1;
            for (int i = 0; i < options.Length; i++)
            {
                if (Segment(i).Contains(e.Location))
                {
                    found = i;
                }
            }
            Change(ref hover, found);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < options.Length; i++)
            {
                if (Segment(i).Contains(e.Location) && i != selected)
                {
                    Selected = i;
                    Changed?.Invoke();
                }
            }
        }
    }

    // shows how far along something is, and seeks when it's interactive
    public sealed class ProgressLine : PaintedControl
    {
        private double value;
        private bool dragging;

        public event Action<double> Seeking;

        public bool Interactive { get; set; }
        public bool Dragging => dragging;

        public double Value
        {
            get => value;
            set => Change(ref this.value, Math.Max(0, Math.Min(1, value)));
        }

        private double rangeStart;
        private double rangeEnd = 1;
        private List<KeyValuePair<double, double>> marks = new List<KeyValuePair<double, double>>();

        // highlight moments, drawn as amber bars over the track
        public void SetMarks(List<KeyValuePair<double, double>> value)
        {
            marks = value ?? new List<KeyValuePair<double, double>>();
            Invalidate();
        }

        // the part outside start..end is dimmed, for trimming
        public void SetRange(double start, double end)
        {
            start = Math.Max(0, Math.Min(1, start));
            end = Math.Max(start, Math.Min(1, end));
            if (start != rangeStart || end != rangeEnd)
            {
                rangeStart = start;
                rangeEnd = end;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            int thickness = Ui.S(6);
            var track = new Rectangle(0, (Height - thickness) / 2, Width - 1, thickness);
            Ui.FillRound(graphics, Theme.Border, track, thickness / 2);
            int filled = (int)Math.Round(track.Width * value);
            if (filled > 0)
            {
                Ui.FillRound(graphics, Theme.Accent, new Rectangle(track.X, track.Y, Math.Max(filled, thickness), thickness), thickness / 2);
            }
            using (var amber = new SolidBrush(Theme.Highlight))
            {
                foreach (KeyValuePair<double, double> mark in marks)
                {
                    int from = track.X + (int)Math.Round(track.Width * Math.Max(0, mark.Key));
                    int to = track.X + (int)Math.Round(track.Width * Math.Min(1, mark.Value));
                    graphics.FillRectangle(amber, from, track.Y - Ui.S(4), Math.Max(Ui.S(3), to - from), Ui.S(3));
                }
            }
            if (rangeStart > 0 || rangeEnd < 1)
            {
                int left = track.X + (int)Math.Round(track.Width * rangeStart);
                int right = track.X + (int)Math.Round(track.Width * rangeEnd);
                using (var dim = new SolidBrush(Color.FromArgb(170, BackColor)))
                {
                    graphics.FillRectangle(dim, 0, 0, left, Height);
                    graphics.FillRectangle(dim, right, 0, Width - right, Height);
                }
                using (var mark = new SolidBrush(Theme.Text))
                {
                    graphics.FillRectangle(mark, left, track.Y - Ui.S(5), Ui.S(2), thickness + Ui.S(10));
                    graphics.FillRectangle(mark, Math.Min(right, Width - Ui.S(2)), track.Y - Ui.S(5), Ui.S(2), thickness + Ui.S(10));
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Interactive)
            {
                dragging = true;
                Seek(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Seek(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            Cursor = Interactive ? Cursors.Hand : Cursors.Default;
            base.OnMouseEnter(e);
        }

        private void Seek(int x)
        {
            Value = (double)x / Math.Max(1, Width);
            Seeking?.Invoke(value);
        }
    }

    // clips / game / when, everything but the last part is a link back
    public sealed class Breadcrumb : PaintedControl
    {
        private readonly List<KeyValuePair<string, Action>> parts = new List<KeyValuePair<string, Action>>();
        private readonly List<Rectangle> boxes = new List<Rectangle>();
        private string count;
        private int hover = -1;

        public void Set(string count, params KeyValuePair<string, Action>[] parts)
        {
            this.count = count;
            this.parts.Clear();
            this.parts.AddRange(parts);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            boxes.Clear();
            Font font = Ui.Strong(16);
            int x = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                bool last = i == parts.Count - 1;
                int width = Math.Min(Ui.Measure(parts[i].Key, font) + 2, Math.Max(0, Width - x));
                var box = new Rectangle(x, 0, width, Height);
                boxes.Add(box);
                Ui.DrawLine(graphics, parts[i].Key, font, box, last || i == hover ? Theme.Text : Theme.Muted);
                x += width;
                if (!last)
                {
                    Ui.DrawLine(graphics, "/", font, new Rectangle(x + Ui.S(8), 0, Ui.S(12), Height), Theme.Slash);
                    x += Ui.S(24);
                }
            }
            if (!string.IsNullOrEmpty(count) && x < Width)
            {
                Ui.DrawLine(graphics, count, Ui.Text(13), new Rectangle(x + Ui.S(14), 0, Width - x - Ui.S(14), Height), Theme.Muted);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int found = -1;
            for (int i = 0; i < boxes.Count - 1; i++)
            {
                if (boxes[i].Contains(e.Location))
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
            for (int i = 0; i < boxes.Count - 1; i++)
            {
                if (boxes[i].Contains(e.Location))
                {
                    parts[i].Value?.Invoke();
                    return;
                }
            }
        }
    }

    // the newest clips as thumbnails, two across, click one to play it
    public sealed class RecentStrip : PaintedControl
    {
        private const int Columns = 2;
        private const int Gap = 8;

        private readonly MediaCache cache;
        private List<KeyValuePair<LibraryFolder, LibraryFile>> clips = new List<KeyValuePair<LibraryFolder, LibraryFile>>();
        private int hot = -1;

        public event Action<LibraryFolder, LibraryFile> Picked;
        public string EmptyText { get; set; } = "";

        public RecentStrip(MediaCache cache)
        {
            this.cache = cache;
        }

        public int Rows => Math.Max(1, (clips.Count + Columns - 1) / Columns);

        public void Show(List<KeyValuePair<LibraryFolder, LibraryFile>> value)
        {
            clips = value;
            Invalidate();
        }

        private Rectangle Cell(int index)
        {
            int gap = Ui.S(Gap);
            int width = (Width - 1 - gap * (Columns - 1)) / Columns;
            int height = width * 9 / 16;
            return new Rectangle((index % Columns) * (width + gap), (index / Columns) * (height + gap), width, height);
        }

        private int HitTest(Point point)
        {
            for (int i = 0; i < clips.Count; i++)
            {
                if (Cell(i).Contains(point) && Cell(i).Bottom <= Height)
                {
                    return i;
                }
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Change(ref hot, index);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            int index = HitTest(PointToClient(Cursor.Position));
            if (index >= 0)
            {
                Picked?.Invoke(clips[index].Key, clips[index].Value);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            if (clips.Count == 0)
            {
                var box = new Rectangle(0, 0, Width - 1, Math.Min(Height - 1, Ui.S(88)));
                using (var pen = new Pen(Theme.Border) { DashStyle = DashStyle.Dash })
                using (GraphicsPath path = Ui.Round(box, Ui.S(12)))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.DrawPath(pen, path);
                }
                Ui.DrawLine(graphics, EmptyText, Ui.Text(13), box, Theme.Muted, TextFormatFlags.HorizontalCenter);
                return;
            }
            for (int i = 0; i < clips.Count; i++)
            {
                Rectangle cell = Cell(i);
                if (cell.Bottom > Height)
                {
                    break;
                }
                VideoThumb.Draw(graphics, cell, cache.Get(clips[i].Value.Path), i == hot ? Theme.Accent : Theme.Border, i == hot ? Ui.Scale * 2F : 1F, 10, 6, 11);
            }
        }
    }

    // found highlight moments: tick box to keep, click the row to watch it
    public sealed class MomentList : PaintedControl
    {
        public const int RowHeight = 34;
        private List<Moment> moments = new List<Moment>();
        private int hot = -1;

        public event Action<Moment> Watch;
        public event Action Changed;

        public void Show(List<Moment> value)
        {
            moments = value;
            Invalidate();
        }

        private int RowAt(int y)
        {
            int row = y / Ui.S(RowHeight);
            return row >= 0 && row < moments.Count ? row : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row = RowAt(e.Y);
            Cursor = row >= 0 ? Cursors.Hand : Cursors.Default;
            Change(ref hot, row);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = -1;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int row = RowAt(e.Y);
            if (row < 0)
            {
                return;
            }
            if (e.X < Ui.S(34))
            {
                moments[row].Keep = !moments[row].Keep;
                Invalidate();
                Changed?.Invoke();
            }
            else
            {
                Watch?.Invoke(moments[row]);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            int height = Ui.S(RowHeight);
            for (int i = 0; i < moments.Count; i++)
            {
                Moment moment = moments[i];
                var row = new Rectangle(0, i * height, Width - 1, height - Ui.S(4));
                Ui.FillRound(graphics, i == hot ? Theme.Hover : Theme.Panel, row, Ui.S(7));

                int box = Ui.S(16);
                var check = new Rectangle(Ui.S(10), row.Y + (row.Height - box) / 2, box, box);
                if (moment.Keep)
                {
                    Ui.FillRound(graphics, Theme.Accent, check, Ui.S(4));
                    Ui.DrawIcon(graphics, "", check, Color.White, 10);
                }
                else
                {
                    Ui.DrawRound(graphics, Theme.Slash, check, Ui.S(4), Ui.Scale * 1.5F);
                }

                string when = Library.LengthText(TimeSpan.FromSeconds(moment.Start)) + " - " + Library.LengthText(TimeSpan.FromSeconds(moment.End));
                Font font = Ui.Strong(13);
                int whenWidth = Ui.Measure(when, font);
                Ui.DrawLine(graphics, when, font, new Rectangle(Ui.S(36), row.Y, whenWidth + 2, row.Height), moment.Keep ? Theme.Text : Theme.Muted);
                Color tag = moment.Kills > 0 ? Theme.Highlight : Theme.Muted;
                Ui.DrawLine(graphics, moment.Why(), Ui.Text(12), new Rectangle(Ui.S(44) + whenWidth, row.Y, row.Width - whenWidth - Ui.S(54), row.Height), tag, TextFormatFlags.Right);
            }
        }
    }

    // a slider that snaps to fixed steps, with the current value written at its right end
    public sealed class StepSlider : PaintedControl
    {
        private const int ValueWidth = 70;
        private readonly int[] steps;
        private readonly Func<int, string> format;
        private int index;
        private bool dragging;

        public event Action Changed;

        public StepSlider(int[] steps, Func<int, string> format)
        {
            this.steps = steps;
            this.format = format;
            Height = Ui.S(28);
            Cursor = Cursors.Hand;
        }

        public int Value
        {
            get => steps[index];
            // the nearest step, for a saved value that isn't one of them
            set => Change(ref index, Enumerable.Range(0, steps.Length).OrderBy(i => Math.Abs(steps[i] - value)).First());
        }

        private Rectangle Track => new Rectangle(Ui.S(8), Height / 2 - Ui.S(2), Width - Ui.S(ValueWidth) - Ui.S(16), Ui.S(4));

        private int KnobX(int i) => Track.X + (int)Math.Round((double)Track.Width * i / (steps.Length - 1));

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.Clear(BackColor);
            Rectangle track = Track;
            Color fill = Enabled ? Theme.Accent : Theme.Border;
            Ui.FillRound(graphics, Theme.Border, track, track.Height / 2);
            int x = KnobX(index);
            Ui.FillRound(graphics, fill, new Rectangle(track.X, track.Y, Math.Max(track.Height, x - track.X), track.Height), track.Height / 2);
            int knob = Ui.S(16);
            using (var brush = new SolidBrush(Enabled ? Theme.Knob : Theme.Slash))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(brush, x - knob / 2, Height / 2 - knob / 2, knob, knob);
            }
            Ui.DrawLine(graphics, format(Value), Ui.Strong(13), new Rectangle(Width - Ui.S(ValueWidth), 0, Ui.S(ValueWidth), Height), Enabled ? Theme.Text : Theme.Muted, TextFormatFlags.Right);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Enabled && e.Button == MouseButtons.Left)
            {
                dragging = true;
                Pick(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Pick(e.X);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Enabled)
            {
                SetIndex(Math.Max(0, Math.Min(steps.Length - 1, index + Math.Sign(e.Delta))));
            }
        }

        private void Pick(int x)
        {
            double at = (double)(x - Track.X) / Math.Max(1, Track.Width) * (steps.Length - 1);
            SetIndex((int)Math.Round(Math.Max(0, Math.Min(steps.Length - 1, at))));
        }

        private void SetIndex(int value)
        {
            if (value != index)
            {
                Change(ref index, value);
                Changed?.Invoke();
            }
        }
    }
}
