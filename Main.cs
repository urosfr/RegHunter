using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RegHunter
{
    public sealed class Main : Form
    {
        private RoundedButton _btnAttach;
        private PulseGlyph _pulse;
        private Label _lblStatus;
        private FlickerFreeListView _list;
        private ContextMenuStrip _ctxMenu;
        private ToolStripMenuItem _deleteItem;
        private RegMonitor _monitor;
        private int _attachedPid = -1;
        private const int MaxItems = 5000;

        private const float ColRatioTime = 0.10f;
        private const float ColRatioStatus = 0.10f;
        private const float ColRatioDetail = 0.22f;
        private const int MinListWidthForAutoResize = 300;

        // One ListViewGroup per hive, keyed by short hive prefix.
        private readonly Dictionary<string, ListViewGroup> _hiveGroups
            = new Dictionary<string, ListViewGroup>(StringComparer.OrdinalIgnoreCase);

        // Accent colours shown in the group header for each hive.
        private static readonly Dictionary<string, Color> HiveHeaderColors
            = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            { "HKLM",  Color.FromArgb(0,   200,  90) },   // green  — matches Theme.Matrix
            { "HKCU",  Color.FromArgb(102, 136, 255) },   // blue
            { "HKU",   Color.FromArgb(204, 136,  68) },   // amber
            { "HKCR",  Color.FromArgb(180,  68, 204) },   // purple
            { "HKCC",  Color.FromArgb( 68, 204, 204) },   // teal
            { "OTHER", Color.FromArgb(130, 120, 120) },   // dim — matches Theme.TextDim
        };

        public Main()
        {
            Text = "REGHUNTER";
            BackColor = Theme.Bg;
            Size = new Size(900, 560);
            MinimumSize = new Size(640, 400);
            DoubleBuffered = true;
            Font = Theme.FontMono;

            var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8), BackColor = Theme.Bg };
            var shell = new RoundedPanel { Dock = DockStyle.Fill, Radius = 14, Padding = new Padding(16) };
            outer.Controls.Add(shell);
            Controls.Add(outer);

            // ── Top bar ──────────────────────────────────────────────────────
            var top = new Panel { Dock = DockStyle.Top, Height = 50 };
            _pulse = new PulseGlyph { Size = new Size(40, 20), Location = new Point(0, 15) };
            var title = new Label
            {
                Text = "REGHUNTER // REGISTRY ACTIVITY MONITOR",
                ForeColor = Theme.Matrix,
                Font = Theme.FontTitle,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(48, 0, 0, 0)
            };
            _btnAttach = new RoundedButton { Text = "ATTACH", Size = new Size(110, 34), Dock = DockStyle.Right };
            _btnAttach.Click += BtnAttach_Click;
            top.Controls.Add(title);
            top.Controls.Add(_btnAttach);
            top.Controls.Add(_pulse);
            _pulse.BringToFront();
            shell.Controls.Add(top);

            // ── Status bar ───────────────────────────────────────────────────
            _lblStatus = new Label
            {
                Text = "Idle — no process attached",
                ForeColor = Theme.TextDim,
                Dock = DockStyle.Top,
                Height = 26,
                Font = Theme.FontMono,
                TextAlign = ContentAlignment.MiddleLeft
            };
            shell.Controls.Add(_lblStatus);

            // ── List ─────────────────────────────────────────────────────────
            _list = new FlickerFreeListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                Font = Theme.FontMono,
                Dock = DockStyle.Fill,
                ShowGroups = true,
            };
            _list.Columns.Add("TIME", 90);
            _list.Columns.Add("STATUS", 80);
            _list.Columns.Add("KEY PATH", 560);
            _list.Columns.Add("DETAIL", 200);
            shell.Controls.Add(_list);

            // Register hive accent colours with the listview so its WndProc
            // can paint group headers in the right colour per hive.
            foreach (var kv in HiveHeaderColors)
                _list.SetGroupHeaderColor(kv.Key, kv.Value);

            top.BringToFront();
            _lblStatus.BringToFront();

            _list.Resize += (s, e) => ResizeColumnsProportionally();

            BuildContextMenu();
            _list.ContextMenuStrip = _ctxMenu;
            _list.MouseClick += List_MouseClick;

            FormClosing += (s, e) => { _monitor?.Stop(); _monitor?.Dispose(); };

            // Defer until layout is complete so ClientSize is valid.
            Shown += (s, e) => BeginInvoke(new Action(ResizeColumnsProportionally));
        }

        // ── Column resize ─────────────────────────────────────────────────────

        private void ResizeColumnsProportionally()
        {
            if (_list == null || _list.Columns.Count < 4) return;
            int available = _list.ClientSize.Width;
            if (available < MinListWidthForAutoResize) return;

            _list.Columns[0].Width = (int)(available * ColRatioTime);
            _list.Columns[1].Width = (int)(available * ColRatioStatus);
            _list.Columns[3].Width = (int)(available * ColRatioDetail);
            int used = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[3].Width;
            _list.Columns[2].Width = Math.Max(120, available - used);
        }

        // ── Hive group helpers ────────────────────────────────────────────────

        private static string HivePrefixOf(string keyPath)
        {
            if (string.IsNullOrEmpty(keyPath)) return "OTHER";
            int slash = keyPath.IndexOf('\\');
            string prefix = (slash >= 0 ? keyPath.Substring(0, slash) : keyPath)
                            .ToUpperInvariant().TrimStart('\\');
            switch (prefix)
            {
                case "HKLM": case "HKEY_LOCAL_MACHINE": return "HKLM";
                case "HKCU": case "HKEY_CURRENT_USER": return "HKCU";
                case "HKU": case "HKEY_USERS": return "HKU";
                case "HKCR": case "HKEY_CLASSES_ROOT": return "HKCR";
                case "HKCC": case "HKEY_CURRENT_CONFIG": return "HKCC";
                default: return "OTHER";
            }
        }

        // Returns (and lazily creates) the ListViewGroup for a hive prefix.
        // The group Name is the prefix key (e.g. "HKLM"); Header starts the
        // same and is updated with a count after each batch.
        private ListViewGroup GetOrCreateGroup(string hivePrefix)
        {
            if (_hiveGroups.TryGetValue(hivePrefix, out ListViewGroup existing))
                return existing;

            var g = new ListViewGroup(hivePrefix, hivePrefix);
            _list.Groups.Add(g);
            _hiveGroups[hivePrefix] = g;

            // Apply the hive accent color to the header immediately after adding.
            _list.ApplyGroupHeaderColor(g);
            return g;
        }

        private void RefreshGroupHeaders()
        {
            foreach (var kv in _hiveGroups)
            {
                kv.Value.Header = $"{kv.Key}  ({kv.Value.Items.Count})";
                // Changing Header text via WinForms resets the native color —
                // reapply after every text update.
                _list.ApplyGroupHeaderColor(kv.Value);
            }
        }

        // ── Context menu ──────────────────────────────────────────────────────

        private void BuildContextMenu()
        {
            _ctxMenu = new ContextMenuStrip();
            _ctxMenu.BackColor = Theme.Panel;
            _ctxMenu.ForeColor = Theme.Text;
            _ctxMenu.Font = Theme.FontMono;
            _ctxMenu.Renderer = new DarkMenuRenderer();

            var openItem = new ToolStripMenuItem("Open in Regedit");
            _deleteItem = new ToolStripMenuItem("Delete Key");
            var copyItem = new ToolStripMenuItem("Copy Path");

            openItem.ForeColor = Theme.Matrix;
            _deleteItem.ForeColor = Theme.Removed;
            copyItem.ForeColor = Theme.Text;

            openItem.Click += CtxOpen_Click;
            _deleteItem.Click += CtxDelete_Click;
            copyItem.Click += CtxCopy_Click;

            _ctxMenu.Items.Add(openItem);
            _ctxMenu.Items.Add(new ToolStripSeparator());
            _ctxMenu.Items.Add(_deleteItem);
            _ctxMenu.Items.Add(new ToolStripSeparator());
            _ctxMenu.Items.Add(copyItem);

            _ctxMenu.Opening += (s, e) =>
            {
                e.Cancel = _list.SelectedItems.Count == 0;
                if (!e.Cancel)
                    _deleteItem.Text = SelectedValueName() != null ? "Delete Value" : "Delete Key";
            };
        }

        private void List_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _list.HitTest(e.Location);
            if (hit.Item != null) { hit.Item.Selected = true; hit.Item.Focused = true; }
        }

        // ── "Open in Regedit" ─────────────────────────────────────────────────

        private void CtxOpen_Click(object sender, EventArgs e)
        {
            string path = SelectedKeyPath();
            if (path == null) return;
            string fullPath = FriendlyToFullHiveName(path);
            if (fullPath == null)
            {
                MessageBox.Show($"Cannot open an unresolved path in Regedit:\n{path}",
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                using (var rk = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    rk.SetValue("LastKey", fullPath, RegistryValueKind.String);

                Process.Start(new ProcessStartInfo("regedit.exe", "-m") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to open Regedit:\n" + ex.Message,
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── "Delete Key" / "Delete Value" ─────────────────────────────────────

        private void CtxDelete_Click(object sender, EventArgs e)
        {
            string path = SelectedKeyPath();
            if (path == null) return;
            string cleanPath = StripAnnotation(path);
            string valueName = SelectedValueName();

            if (valueName != null) { DeleteRegistryValue(cleanPath, valueName); return; }

            var confirm = MessageBox.Show(
                $"Delete exactly this registry key?\n\n{cleanPath}\n\n" +
                "Only this key will be removed. If it has subkeys you will be asked separately.",
                "REGHUNTER — DELETE KEY", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            try
            {
                if (!TryDeleteRegistryKey(cleanPath, allowTree: false, out string error))
                {
                    if (error == "HAS_CHILDREN")
                    {
                        var tc = MessageBox.Show(
                            $"This key has subkeys.\n\n{cleanPath}\n\nDelete this key AND all its subkeys recursively?",
                            "REGHUNTER — KEY HAS SUBKEYS", MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        if (tc != DialogResult.Yes) return;
                        if (!TryDeleteRegistryKey(cleanPath, allowTree: true, out string te))
                        { MessageBox.Show("Delete failed:\n" + te, "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                    }
                    else
                    { MessageBox.Show("Delete failed:\n" + error, "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                }
                if (_list.SelectedItems.Count > 0) _list.Items.Remove(_list.SelectedItems[0]);
            }
            catch (Exception ex)
            { MessageBox.Show("Delete failed:\n" + ex.Message, "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private void DeleteRegistryValue(string friendlyKeyPath, string valueName)
        {
            var confirm = MessageBox.Show(
                $"Delete this registry value?\n\nKey:   {friendlyKeyPath}\nValue: {valueName}",
                "REGHUNTER — DELETE VALUE", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            if (!TryParseHive(friendlyKeyPath, out RegistryHive hiveEnum, out string subPath))
            { MessageBox.Show($"Cannot determine registry hive for path:\n{friendlyKeyPath}", "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            try
            {
                RegistryView foundView = RegistryView.Registry64;
                bool found = false;

                using (var root64 = RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry64))
                using (var k64 = root64.OpenSubKey(subPath, writable: false))
                {
                    if (k64 != null) { found = true; }
                    else
                    {
                        using (var root32 = RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry32))
                        using (var k32 = root32.OpenSubKey(subPath, writable: false))
                            if (k32 != null) { found = true; foundView = RegistryView.Registry32; }
                    }
                }

                if (!found)
                { MessageBox.Show("Key not found in either registry view:\n" + friendlyKeyPath, "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

                using (var root = RegistryKey.OpenBaseKey(hiveEnum, foundView))
                using (var key = root.OpenSubKey(subPath, writable: true))
                {
                    if (key == null)
                    { MessageBox.Show("Key could not be opened for writing (access denied):\n" + friendlyKeyPath, "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                    key.DeleteValue(valueName, throwOnMissingValue: true);
                }

                if (_list.SelectedItems.Count > 0) _list.Items.Remove(_list.SelectedItems[0]);
            }
            catch (ArgumentException)
            { MessageBox.Show("Value no longer exists — it may already have been deleted or renamed.", "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (UnauthorizedAccessException)
            { MessageBox.Show("Access denied. RegHunter must be run as Administrator to delete this value.", "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            catch (Exception ex)
            { MessageBox.Show("Delete failed:\n" + ex.Message, "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // ── "Copy Path" ───────────────────────────────────────────────────────

        private void CtxCopy_Click(object sender, EventArgs e)
        {
            string path = SelectedKeyPath();
            if (path != null) Clipboard.SetText(StripAnnotation(path));
        }

        // ── Registry helpers ──────────────────────────────────────────────────

        // BUG FIX: parentKey aliased to root caused a double-dispose.
        // Now only disposed when it is a distinct opened subkey.
        private static bool TryDeleteRegistryKey(string friendlyPath, bool allowTree, out string error)
        {
            error = null;
            if (!TryParseHive(friendlyPath, out RegistryHive hiveEnum, out string subPath))
            { error = $"Cannot determine registry hive for path:\n{friendlyPath}"; return false; }
            if (string.IsNullOrEmpty(subPath))
            { error = "Cannot delete a root hive key."; return false; }

            int lastSlash = subPath.LastIndexOf('\\');
            string parentSubPath = lastSlash < 0 ? null : subPath.Substring(0, lastSlash);
            string leafName = lastSlash < 0 ? subPath : subPath.Substring(lastSlash + 1);
            RegistryView foundView = RegistryView.Registry64;

            try
            {
                using (var root64 = RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry64))
                using (var probe = root64.OpenSubKey(subPath, writable: false))
                {
                    if (probe == null)
                    {
                        using (var root32 = RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry32))
                        using (var probe32 = root32.OpenSubKey(subPath, writable: false))
                        {
                            if (probe32 == null)
                            { error = "Key not found in either registry view.\nIt may have already been deleted.\n\n" + friendlyPath; return false; }
                        }
                        foundView = RegistryView.Registry32;
                    }
                }

                using (var root = RegistryKey.OpenBaseKey(hiveEnum, foundView))
                {
                    bool parentIsRoot = parentSubPath == null;
                    RegistryKey parentKey = parentIsRoot ? root : root.OpenSubKey(parentSubPath, writable: true);
                    if (parentKey == null)
                    { error = $"Parent key could not be opened for writing:\n{parentSubPath ?? "(root hive)"}"; return false; }
                    try
                    {
                        if (allowTree) parentKey.DeleteSubKeyTree(leafName, throwOnMissingSubKey: true);
                        else parentKey.DeleteSubKey(leafName, throwOnMissingSubKey: true);
                    }
                    finally { if (!parentIsRoot) parentKey.Dispose(); }
                }
                return true;
            }
            catch (InvalidOperationException) { error = "HAS_CHILDREN"; return false; }
            catch (ArgumentException) { error = "Key no longer exists — it may have been deleted by another process."; return false; }
            catch (UnauthorizedAccessException) { error = "Access denied. RegHunter must be run as Administrator to delete this key."; return false; }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static bool TryParseHive(string path, out RegistryHive hive, out string subPath)
        {
            hive = RegistryHive.LocalMachine; subPath = null;
            if (string.IsNullOrEmpty(path)) return false;
            var parts = path.Split(new[] { '\\' }, 2);
            string tok = parts[0].ToUpperInvariant().TrimStart('\\');
            subPath = parts.Length > 1 ? parts[1] : "";
            switch (tok)
            {
                case "HKLM": case "HKEY_LOCAL_MACHINE": hive = RegistryHive.LocalMachine; return true;
                case "HKCU": case "HKEY_CURRENT_USER": hive = RegistryHive.CurrentUser; return true;
                case "HKU": case "HKEY_USERS": hive = RegistryHive.Users; return true;
                case "HKCR": case "HKEY_CLASSES_ROOT": hive = RegistryHive.ClassesRoot; return true;
                case "HKCC": case "HKEY_CURRENT_CONFIG": hive = RegistryHive.CurrentConfig; return true;
                default: return false;
            }
        }

        private static string FriendlyToFullHiveName(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("(")) return null;
            path = StripAnnotation(path);
            var parts = path.Split(new[] { '\\' }, 2);
            string tok = parts[0].ToUpperInvariant();
            string rest = parts.Length > 1 ? @"\" + parts[1] : "";
            switch (tok)
            {
                case "HKLM": return "HKEY_LOCAL_MACHINE" + rest;
                case "HKCU": return "HKEY_CURRENT_USER" + rest;
                case "HKU": return "HKEY_USERS" + rest;
                case "HKCR": return "HKEY_CLASSES_ROOT" + rest;
                case "HKCC": return "HKEY_CURRENT_CONFIG" + rest;
                case "HKEY_LOCAL_MACHINE":
                case "HKEY_CURRENT_USER":
                case "HKEY_USERS":
                case "HKEY_CLASSES_ROOT":
                case "HKEY_CURRENT_CONFIG":
                    return path;
                default: return null;
            }
        }

        private static string StripAnnotation(string path)
        {
            if (path == null) return null;
            int idx = path.IndexOf("  [", StringComparison.Ordinal);
            return idx >= 0 ? path.Substring(0, idx).TrimEnd() : path;
        }

        private string SelectedKeyPath()
        {
            if (_list.SelectedItems.Count == 0) return null;
            var item = _list.SelectedItems[0];
            return item.SubItems.Count < 3 ? null : item.SubItems[2].Text;
        }

        private string SelectedValueName()
        {
            if (_list.SelectedItems.Count == 0) return null;
            var item = _list.SelectedItems[0];
            if (item.SubItems.Count < 4) return null;
            string d = item.SubItems[3].Text;
            if (d.StartsWith("value: ", StringComparison.Ordinal)) return d.Substring(7);
            if (d.StartsWith("deleted value: ", StringComparison.Ordinal)) return d.Substring(15);
            return null;
        }

        // ── Monitor attachment ────────────────────────────────────────────────

        private void BtnAttach_Click(object sender, EventArgs e)
        {
            using (var picker = new Picker())
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;

                _monitor?.Stop();
                _monitor?.Dispose();
                _monitor = null;
                _list.Items.Clear();
                _list.Groups.Clear();
                _hiveGroups.Clear();

                _attachedPid = picker.SelectedPid;
                _lblStatus.Text = $"Attaching to {picker.SelectedName} (PID {_attachedPid})…";
                _lblStatus.ForeColor = Theme.Changed;

                _monitor = new RegMonitor(_attachedPid);
                _monitor.BatchReady += Monitor_BatchReady;

                try
                {
                    _monitor.Start();
                    _lblStatus.Text = $"Watching {picker.SelectedName} (PID {_attachedPid})";
                    _lblStatus.ForeColor = Theme.Matrix;
                }
                catch (Exception ex)
                {
                    _lblStatus.Text = $"Couldn't attach: {ex.Message} — try running as Administrator";
                    _lblStatus.ForeColor = Theme.Removed;
                    _monitor.Dispose();
                    _monitor = null;
                }
            }
        }

        private void Monitor_BatchReady(object sender, IList<RegEventArgs> batch)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(() => AppendBatch(batch)));
        }

        private void AppendBatch(IList<RegEventArgs> batch)
        {
            if (batch.Count == 0) return;
            _list.BeginUpdate();
            try
            {
                // With ShowGroups=true the ListView ignores Insert() index and
                // always appends items to their group. To get newest-first within
                // each group we walk the batch in reverse so that, as each item
                // gets appended to its group, the earlier (older) items in the
                // batch follow the newer ones.
                for (int i = batch.Count - 1; i >= 0; i--)
                {
                    var ev = batch[i];
                    var item = new ListViewItem(ev.Time.ToString("HH:mm:ss.fff"));
                    item.SubItems.Add(StatusText(ev.Op));
                    item.SubItems.Add(ev.KeyPath);
                    item.SubItems.Add(ev.Detail);
                    item.ForeColor = StatusColor(ev.Op);
                    item.Group = GetOrCreateGroup(HivePrefixOf(ev.KeyPath));
                    _list.Items.Add(item);
                }

                // Trim oldest items when over the cap.
                while (_list.Items.Count > MaxItems)
                    _list.Items.RemoveAt(_list.Items.Count - 1);

                RefreshGroupHeaders();
            }
            finally
            {
                _list.EndUpdate();
            }
        }

        private static string StatusText(RegOp op)
        {
            switch (op)
            {
                case RegOp.Added: return "ADDED";
                case RegOp.Changed: return "CHANGED";
                case RegOp.Removed: return "REMOVED";
                default: return "?";
            }
        }

        private static Color StatusColor(RegOp op)
        {
            switch (op)
            {
                case RegOp.Added: return Theme.Added;
                case RegOp.Changed: return Theme.Changed;
                case RegOp.Removed: return Theme.Removed;
                default: return Theme.Text;
            }
        }
    }
}