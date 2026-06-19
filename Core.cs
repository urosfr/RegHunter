using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RegHunter
{
    // -------------------------------------------------------------------------
    // FlickerFreeListView
    //
    // Fixes WinForms ListView flicker WITHOUT suppressing the full NM_CUSTOMDRAW
    // pipeline. Suppressing ALL of NM_CUSTOMDRAW (returning 0 unconditionally)
    // kills per-item ForeColor because WinForms applies those colors inside its
    // own custom-draw handler. Instead we:
    //
    //   • Enable LVS_EX_DOUBLEBUFFER (native double-buffer, no flicker)
    //   • Restore an opaque LVM_SETTEXTBKCOLOR (re-enables Win32 fast-path)
    //   • Enable OptimizedDoubleBuffer via SetStyle
    //   • Do NOT intercept NM_CUSTOMDRAW — let WinForms paint per-item colors
    // -------------------------------------------------------------------------
    internal class FlickerFreeListView : ListView
    {
        private const int LVM_SETEXTENDEDLISTVIEWSTYLE = 0x1036; // LVM_FIRST + 54
        private const int LVM_SETTEXTBKCOLOR = 0x1026; // LVM_FIRST + 38
        private const uint LVS_EX_DOUBLEBUFFER = 0x00010000;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public FlickerFreeListView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Native double-buffering — eliminates flicker on scroll and insert.
            SendMessage(Handle,
                LVM_SETEXTENDEDLISTVIEWSTYLE,
                (IntPtr)LVS_EX_DOUBLEBUFFER,
                (IntPtr)LVS_EX_DOUBLEBUFFER);

            // Opaque background: restores the Win32 fast-path incremental repaint
            // (WinForms sets CLR_NONE which forces full-erase every frame).
            SendMessage(Handle,
                LVM_SETTEXTBKCOLOR,
                IntPtr.Zero,
                (IntPtr)ColorToColorRef(Theme.PanelAlt));
        }

        // GDI COLORREF = 0x00BBGGRR
        private static int ColorToColorRef(Color c) =>
            c.R | (c.G << 8) | (c.B << 16);
    }

    // -------------------------------------------------------------------------
    // DarkMenuRenderer + DarkMenuColors
    // -------------------------------------------------------------------------
    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? (e.Item.ForeColor == Color.Empty ? Theme.Text : e.Item.ForeColor)
                : Theme.TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var pen = new System.Drawing.Pen(Theme.Border))
            {
                int y = e.Item.Height / 2;
                e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
            }
        }
    }

    internal class DarkMenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(255, 60, 18, 22);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(255, 60, 18, 22);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(255, 60, 18, 22);
        public override Color MenuItemPressedGradientBegin => Theme.Accent;
        public override Color MenuItemPressedGradientEnd => Theme.Accent;
        public override Color MenuBorder => Theme.Border;
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}