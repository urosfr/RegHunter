using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RegHunter
{
    // -------------------------------------------------------------------------
    // FlickerFreeListView
    //
    //  • LVS_EX_DOUBLEBUFFER  — native flicker-free rendering
    //  • LVM_SETTEXTBKCOLOR   — opaque bg restores Win32 fast-path repaint
    //  • SetWindowTheme("","")— strips Aero visual styles from the control so
    //                           group headers render with our colors, not the
    //                           system light-gray themed band
    //  • LVM_SETGROUPINFO     — sets crHeader / crHeaderHot per group so each
    //                           hive label is painted in its accent color.
    //                           No WndProc / NM_CUSTOMDRAW interception needed.
    // -------------------------------------------------------------------------
    internal class FlickerFreeListView : ListView
    {
        // ── Win32 message constants ───────────────────────────────────────────
        private const int LVM_FIRST = 0x1000;
        private const int LVM_SETEXTENDEDLISTVIEWSTYLE = LVM_FIRST + 54;
        private const int LVM_SETTEXTBKCOLOR = LVM_FIRST + 38;
        private const int LVM_SETTEXTCOLOR = LVM_FIRST + 36;
        private const int LVM_SETBKCOLOR = LVM_FIRST + 1;
        private const int LVM_SETGROUPINFO = LVM_FIRST + 147;  // 0x1093

        private const uint LVS_EX_DOUBLEBUFFER = 0x00010000;

        // ── LVGROUP mask flags ────────────────────────────────────────────────
        private const uint LVGF_NONE = 0x00000000;
        private const uint LVGF_HEADER = 0x00000001;
        private const uint LVGF_GROUPID = 0x00000010;
        private const uint LVGF_STATE = 0x00000004;
        private const uint LVGF_TITLEIMAGE = 0x00001000;
        private const uint LVGF_EXTENDEDIMAGE = 0x00002000;
        private const uint LVGF_ITEMS = 0x00004000;
        private const uint LVGF_SUBSET = 0x00008000;
        private const uint LVGF_SUBSETITEMS = 0x00010000;
        // Color flags (Vista+)
        private const uint LVGF_GROUPID2 = 0x00000010;
        private const uint LVGF_ALIGN = 0x00000008;
        private const uint LVGF_TASK = 0x00000800;
        private const uint LVGF_DESCRIPTIONBOTTOM = 0x00000100;
        private const uint LVGF_DESCRIPTIONTOP = 0x00000200;
        private const uint LVGF_LINKBOTTOM = 0x00000400;
        private const uint LVGF_HEADERCENTERED = 0x00000002;
        // The actual color mask bits (Vista+, confirmed in commctrl.h)
        private const uint LVGF_TITLEIMAGE2 = 0x00001000;
        // crHeader / crHeaderHot / crFooter / crFooterHot are always sent
        // when mask includes LVGF_GROUPID — Windows reads them if themes are off.
        // Simplest: send mask = LVGF_GROUPID | LVGF_HEADER and fill color fields.
        private const uint COLOR_MASK = LVGF_GROUPID | LVGF_HEADER;

        // ── LVGROUP struct (Unicode, matches commctrl.h exactly) ──────────────
        // Size must be Marshal.SizeOf<LVGROUP>() — we set it in the constructor.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LVGROUP
        {
            public int cbSize;
            public uint mask;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pszHeader;
            public int cchHeader;
            public IntPtr pszFooter;      // unused, keep as IntPtr
            public int cchFooter;
            public int iGroupId;
            public uint stateMask;
            public uint state;
            public uint uAlign;
            // Vista+ fields below — present in the struct on all modern Windows
            public IntPtr pszSubtitle;
            public uint cchSubtitle;
            public IntPtr pszTask;
            public uint cchTask;
            public IntPtr pszDescriptionTop;
            public uint cchDescriptionTop;
            public IntPtr pszDescriptionBottom;
            public uint cchDescriptionBottom;
            public int iTitleImage;
            public int iExtendedImage;
            public int iFirstItem;
            public uint cItems;
            public IntPtr pszSubsetTitle;
            public uint cchSubsetTitle;
            // Color fields (Vista+, only used when visual styles are disabled)
            public uint crHeader;
            public uint crHeaderHot;
            public uint crFooter;
            public uint crFooterHot;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // Overload that takes a ref struct — used for LVM_SETGROUPINFO
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVGROUP lParam);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList);

        // Pending color registrations: groups may be added before the handle
        // exists, so we store them and apply once the handle is created.
        private readonly System.Collections.Generic.Dictionary<string, Color> _groupColors
            = new System.Collections.Generic.Dictionary<string, Color>(
                StringComparer.OrdinalIgnoreCase);

        // Called by Main to register a hive accent color.
        public void SetGroupHeaderColor(string groupName, Color color)
        {
            _groupColors[groupName] = color;
        }

        // Call this after adding a group to apply its registered color.
        // groupId  = the Win32 group ID (ListViewGroup.ID via reflection, or
        //            we use the Groups index as a stand-in since WinForms assigns
        //            IDs sequentially starting at 0).
        // We get the real ID by reading the internal ListViewGroup._id field via
        // reflection — more reliable than assuming sequential assignment.
        public void ApplyGroupHeaderColor(ListViewGroup group)
        {
            if (!IsHandleCreated) return;

            Color color = Theme.TextDim;
            if (!_groupColors.TryGetValue(group.Name, out color))
                color = Theme.TextDim;

            int groupId = GetGroupId(group);
            if (groupId < 0) return;

            ApplyGroupColor(groupId, group.Header, color);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Strip Aero visual styles — this is the key step that makes group
            // headers respect crHeader instead of the system theme colors.
            SetWindowTheme(Handle, "", "");

            // Native double-buffering — no flicker on scroll/insert.
            SendMessage(Handle,
                LVM_SETEXTENDEDLISTVIEWSTYLE,
                (IntPtr)LVS_EX_DOUBLEBUFFER,
                (IntPtr)LVS_EX_DOUBLEBUFFER);

            // Opaque background — restores Win32 fast-path incremental repaint.
            SendMessage(Handle, LVM_SETTEXTBKCOLOR, IntPtr.Zero,
                (IntPtr)ColorToColorRef(Theme.PanelAlt));
            SendMessage(Handle, LVM_SETBKCOLOR, IntPtr.Zero,
                (IntPtr)ColorToColorRef(Theme.PanelAlt));
            SendMessage(Handle, LVM_SETTEXTCOLOR, IntPtr.Zero,
                (IntPtr)ColorToColorRef(Theme.Text));

            // Re-apply colors to any groups that were added before the handle existed.
            foreach (ListViewGroup g in Groups)
                ApplyGroupHeaderColor(g);
        }

        private void ApplyGroupColor(int groupId, string headerText, Color color)
        {
            uint colorRef = (uint)ColorToColorRef(color);

            var lvg = new LVGROUP();
            lvg.cbSize = Marshal.SizeOf(typeof(LVGROUP));
            lvg.mask = COLOR_MASK;
            lvg.iGroupId = groupId;
            lvg.pszHeader = headerText;
            lvg.crHeader = colorRef;
            lvg.crHeaderHot = colorRef;

            SendMessage(Handle, LVM_SETGROUPINFO, (IntPtr)groupId, ref lvg);
        }

        // Read the internal Win32 group ID from the ListViewGroup via reflection.
        // WinForms stores it in a private field named "ID" (or "_id" depending on
        // the .NET version). Falls back to the Groups index if not found.
        private int GetGroupId(ListViewGroup group)
        {
            // Try the known field names used across .NET Framework versions.
            var t = typeof(ListViewGroup);
            foreach (string name in new[] { "ID", "_id", "id" })
            {
                var fi = t.GetField(name,
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (fi != null)
                {
                    object val = fi.GetValue(group);
                    if (val is int i) return i;
                }
            }
            // Fallback: use index in Groups collection.
            return Groups.IndexOf(group);
        }

        // GDI COLORREF = 0x00BBGGRR
        private static int ColorToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
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