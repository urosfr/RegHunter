using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RegHunter
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(255, 10, 10, 12);
        public static readonly Color Panel = Color.FromArgb(255, 17, 17, 20);
        public static readonly Color PanelAlt = Color.FromArgb(255, 24, 22, 23);
        public static readonly Color Border = Color.FromArgb(255, 45, 12, 16);
        public static readonly Color Accent = Color.FromArgb(255, 153, 0, 18);
        public static readonly Color AccentHi = Color.FromArgb(255, 198, 26, 38);
        public static readonly Color Matrix = Color.FromArgb(255, 0, 200, 90);
        public static readonly Color Text = Color.FromArgb(255, 214, 210, 208);
        public static readonly Color TextDim = Color.FromArgb(255, 130, 120, 120);
        public static readonly Color Added = Color.FromArgb(255, 0, 200, 90);
        public static readonly Color Changed = Color.FromArgb(255, 230, 170, 30);
        public static readonly Color Removed = Color.FromArgb(255, 198, 26, 38);

        public static readonly Font FontMono = new Font("Consolas", 9.25f, FontStyle.Regular);
        public static readonly Font FontMonoB = new Font("Consolas", 9.5f, FontStyle.Bold);
        public static readonly Font FontTitle = new Font("Consolas", 11f, FontStyle.Bold);
    }

    // -------------------------------------------------------------------------
    // RoundedPanel
    //
    // Black-corner fix: the rectangular WinForms background is erased before
    // OnPaint fires. Corners outside the rounded clip path were being filled
    // with black (the default WM_ERASEBKGND colour when no brush is set).
    //
    // Fix: flat-fill the parent's BackColor behind us instead of recursively
    // invoking the parent's full paint chain (the old InvokePaint approach
    // caused a repaint storm once a timer-driven child control was added
    // elsewhere in the tree).
    // -------------------------------------------------------------------------
    internal class RoundedPanel : Panel
    {
        public int Radius { get; set; } = 10;
        public Color BorderColor { get; set; } = Theme.Border;

        public RoundedPanel()
        {
            SetStyle(
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint,
                true);
            SetStyle(ControlStyles.Opaque, false);
            BackColor = Theme.Panel;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Parent?.BackColor ?? Theme.Panel))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundedRect(rect, Radius))
            using (var fill = new SolidBrush(BackColor))
            using (var pen = new Pen(BorderColor, 1))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // -------------------------------------------------------------------------
    // RoundedButton
    //
    // Same black-corner fix as RoundedPanel, same flat-fill approach.
    // -------------------------------------------------------------------------
    internal class RoundedButton : Button
    {
        public int Radius { get; set; } = 8;
        private bool _hover, _down;

        public RoundedButton()
        {
            SetStyle(
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint,
                true);
            SetStyle(ControlStyles.Opaque, false);

            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;   // let parent show through corners
            ForeColor = Theme.Text;
            Font = Theme.FontMonoB;
            Cursor = Cursors.Hand;

            MouseEnter += (s, e) => { _hover = true; Invalidate(); };
            MouseLeave += (s, e) => { _hover = false; _down = false; Invalidate(); };
            MouseDown += (s, e) => { _down = true; Invalidate(); };
            MouseUp += (s, e) => { _down = false; Invalidate(); };
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Parent?.BackColor ?? Theme.Panel))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            Color back = _down ? Theme.Accent
                         : _hover ? Color.FromArgb(255, 40, 18, 20)
                                   : Theme.PanelAlt;
            Color border = _hover ? Theme.AccentHi : Theme.Border;

            using (var path = RoundedPanel.RoundedRect(rect, Radius))
            using (var fill = new SolidBrush(back))
            using (var pen = new Pen(border, 1))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            using (var brush = new SolidBrush(ForeColor))
                g.DrawString(Text, Font, brush, rect, sf);
        }
    }

    // -------------------------------------------------------------------------
    // PulseGlyph
    //
    // Small logo mark for the top bar: a scrolling pulse line in Theme.Matrix
    // green. Self-contained timer (80ms tick), flat-fill background (no
    // recursive parent repaint), cheap per-tick cost.
    // -------------------------------------------------------------------------
    internal class PulseGlyph : Control
    {
        private const int PointCount = 28;
        private readonly float[] _samples = new float[PointCount];
        private readonly Timer _timer;
        private int _phase;

        public PulseGlyph()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor,
                true);
            SetStyle(ControlStyles.Opaque, false);

            BackColor = Color.Transparent;
            Size = new Size(40, 20);
            Cursor = Cursors.Default;

            BuildWaveform();

            _timer = new Timer { Interval = 80 };
            _timer.Tick += (s, e) =>
            {
                _phase = (_phase + 1) % PointCount;
                Invalidate();
            };
            _timer.Start();

            Disposed += (s, e) => _timer.Dispose();
        }

        private void BuildWaveform()
        {
            for (int i = 0; i < PointCount; i++)
                _samples[i] = 0f;

            _samples[4] = 0.25f;
            _samples[5] = -0.85f;
            _samples[6] = 1f;
            _samples[7] = -0.4f;
            _samples[8] = 0.15f;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Parent?.BackColor ?? Theme.Panel))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float midY = Height / 2f;
            float amp = (Height / 2f) - 2f;
            float stepX = Width / (float)(PointCount - 1);

            using (var pen = new Pen(Theme.Matrix, 1.4f))
            {
                PointF prev = default;
                for (int i = 0; i < PointCount; i++)
                {
                    int sampleIndex = (i + _phase) % PointCount;
                    float x = i * stepX;
                    float y = midY - (_samples[sampleIndex] * amp);
                    var pt = new PointF(x, y);
                    if (i > 0) g.DrawLine(pen, prev, pt);
                    prev = pt;
                }
            }
        }
    }
}