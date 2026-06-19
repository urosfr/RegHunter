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

        // Proportional column widths (sum = 1.0). TIME/STATUS stay narrow;
        // KEY PATH absorbs most extra width since paths vary most in length.
        private const float ColRatioTime = 0.10f;
        private const float ColRatioStatus = 0.10f;
        private const float ColRatioDetail = 0.22f;
        private const int MinListWidthForAutoResize = 300;

        public Main()
        {
            Text = "REGHUNTER";
            BackColor = Theme.Bg;
            Size = new Size(900, 560);
            MinimumSize = new Size(640, 400);
            DoubleBuffered = true;
            Font = Theme.FontMono;

            var outer = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(8),
                BackColor = Theme.Bg
            };
            var shell = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Radius = 14,
                Padding = new Padding(16)
            };
            outer.Controls.Add(shell);
            Controls.Add(outer);

            // --- Top bar ---
            var top = new Panel { Dock = DockStyle.Top, Height = 50 };

            _pulse = new PulseGlyph
            {
                Size = new Size(40, 20),
                Location = new Point(0, 15)
            };

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

            // --- Status bar ---
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

            // --- List ---
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
                Dock = DockStyle.Fill
            };
            _list.Columns.Add("TIME", 90);
            _list.Columns.Add("STATUS", 80);
            _list.Columns.Add("KEY PATH", 560);
            _list.Columns.Add("DETAIL", 200);
            shell.Controls.Add(_list);

            top.BringToFront();
            _lblStatus.BringToFront();

            // Keep columns proportional as the window/list resizes. User can still
            // drag column borders manually at any time; we only redistribute on
            // an actual size change of the list itself.
            _list.Resize += (s, e) => ResizeColumnsProportionally();

            // --- Context menu ---
            BuildContextMenu();
            _list.ContextMenuStrip = _ctxMenu;
            _list.MouseClick += List_MouseClick;

            FormClosing += (s, e) =>
            {
                _monitor?.Stop();
                _monitor?.Dispose();
            };

            Shown += (s, e) => ResizeColumnsProportionally();
        }

        // Redistribute the four column widths proportionally to the list's
        // current client width. Guards against the list being too narrow
        // (e.g. mid-drag) to avoid pointless thrash.
        private void ResizeColumnsProportionally()
        {
            if (_list == null || _list.Columns.Count < 4) return;

            int available = _list.ClientSize.Width;
            if (available < MinListWidthForAutoResize) return;

            _list.Columns[0].Width = (int)(available * ColRatioTime);
            _list.Columns[1].Width = (int)(available * ColRatioStatus);
            _list.Columns[3].Width = (int)(available * ColRatioDetail);

            // KEY PATH takes whatever remains so the four widths always sum
            // exactly to the available width (no dead gap, no clipped scrollbar).
            int used = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[3].Width;
            _list.Columns[2].Width = Math.Max(120, available - used);
        }

        // -------------------------------------------------------------------------
        // Context menu
        // -------------------------------------------------------------------------

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

            // Only show when an item is actually selected.
            // Also relabel Delete based on whether the row is a value-change or a key event.
            _ctxMenu.Opening += (s, e) =>
            {
                e.Cancel = _list.SelectedItems.Count == 0;
                if (!e.Cancel)
                {
                    _deleteItem.Text = SelectedValueName() != null ? "Delete Value" : "Delete Key";
                }
            };
        }

        // Right-click: select the item under the cursor before the menu opens.
        private void List_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _list.HitTest(e.Location);
            if (hit.Item != null)
            {
                hit.Item.Selected = true;
                hit.Item.Focused = true;
            }
        }

        // -------------------------------------------------------------------------
        // "Open in Regedit" — writes LastKey then launches regedit.exe
        // -------------------------------------------------------------------------

        private void CtxOpen_Click(object sender, EventArgs e)
        {
            string path = SelectedKeyPath();
            if (path == null) return;

            string fullPath = FriendlyToFullHiveName(path);
            if (fullPath == null)
            {
                MessageBox.Show(
                    $"Cannot open an unresolved path in Regedit:\n{path}",
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // Write the target key into Regedit's LastKey so it navigates there on start.
                // This is the canonical, documented technique — Regedit reads this value at launch.
                using (var rk = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                {
                    rk.SetValue("LastKey", fullPath, RegistryValueKind.String);
                }

                // Launch a new regedit instance (the -m flag allows multiple instances).
                Process.Start(new ProcessStartInfo("regedit.exe", "-m")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to open Regedit:\n" + ex.Message,
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // -------------------------------------------------------------------------
        // "Delete Key" / "Delete Value" — branches based on row type.
        //
        // Rows produced from RegistrySetValue / RegistryDeleteValue events show the
        // PARENT KEY in the KEY PATH column and the value name in DETAIL
        // ("value: <name>" / "deleted value: <name>"). The old code always treated
        // KEY PATH as a key to delete, so selecting a value-change row tried to
        // delete the parent key itself (wrong target -> bogus "already deleted"
        // errors). We now detect value rows via SelectedValueName() and delete the
        // value instead.
        // -------------------------------------------------------------------------

        private void CtxDelete_Click(object sender, EventArgs e)
        {
            string path = SelectedKeyPath();
            if (path == null) return;

            string cleanPath = StripAnnotation(path);
            string valueName = SelectedValueName();

            if (valueName != null)
            {
                DeleteRegistryValue(cleanPath, valueName);
                return;
            }

            var confirm = MessageBox.Show(
                $"Delete exactly this registry key?\n\n{cleanPath}\n\n" +
                "Only this key will be removed. If it has subkeys you will be asked separately.",
                "REGHUNTER — DELETE KEY",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes) return;

            try
            {
                // Attempt exact-key delete first (no children).
                if (!TryDeleteRegistryKey(cleanPath, allowTree: false, out string error))
                {
                    if (error == "HAS_CHILDREN")
                    {
                        // Key has subkeys — ask whether to delete the whole tree.
                        var treeConfirm = MessageBox.Show(
                            $"This key has subkeys.\n\n{cleanPath}\n\n" +
                            "Delete this key AND all its subkeys recursively?",
                            "REGHUNTER — KEY HAS SUBKEYS",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2);

                        if (treeConfirm != DialogResult.Yes) return;

                        if (!TryDeleteRegistryKey(cleanPath, allowTree: true, out string treeError))
                        {
                            MessageBox.Show("Delete failed:\n" + treeError,
                                "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }
                    else
                    {
                        MessageBox.Show("Delete failed:\n" + error,
                            "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                if (_list.SelectedItems.Count > 0)
                    _list.Items.Remove(_list.SelectedItems[0]);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Delete failed:\n" + ex.Message,
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Deletes a single registry VALUE (not a key). Uses the same 64-bit/32-bit
        // view probing as TryDeleteRegistryKey so WOW6432Node-redirected values
        // are found correctly.
        private void DeleteRegistryValue(string friendlyKeyPath, string valueName)
        {
            var confirm = MessageBox.Show(
                $"Delete this registry value?\n\nKey:   {friendlyKeyPath}\nValue: {valueName}",
                "REGHUNTER — DELETE VALUE",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes) return;

            if (!TryParseHive(friendlyKeyPath, out RegistryHive hiveEnum, out string subPath))
            {
                MessageBox.Show($"Cannot determine registry hive for path:\n{friendlyKeyPath}",
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                RegistryView foundView = RegistryView.Registry64;
                bool found = false;

                using (RegistryKey root64 = RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry64))
                using (RegistryKey k64 = root64.OpenSubKey(subPath, writable: false))
                {
                    if (k64 != null)
                    {
                        found = true;
                    }
                    else
                    {
                        using (RegistryKey root32 = RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry32))
                        using (RegistryKey k32 = root32.OpenSubKey(subPath, writable: false))
                        {
                            if (k32 != null)
                            {
                                found = true;
                                foundView = RegistryView.Registry32;
                            }
                        }
                    }
                }

                if (!found)
                {
                    MessageBox.Show(
                        "Key not found in either the 64-bit or 32-bit registry view:\n" + friendlyKeyPath,
                        "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                using (RegistryKey root = RegistryKey.OpenBaseKey(hiveEnum, foundView))
                using (RegistryKey key = root.OpenSubKey(subPath, writable: true))
                {
                    if (key == null)
                    {
                        MessageBox.Show(
                            "Key could not be opened for writing (access denied):\n" + friendlyKeyPath,
                            "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    key.DeleteValue(valueName, throwOnMissingValue: true);
                }

                if (_list.SelectedItems.Count > 0)
                    _list.Items.Remove(_list.SelectedItems[0]);
            }
            catch (ArgumentException)
            {
                MessageBox.Show(
                    "Value no longer exists — it may already have been deleted or renamed.",
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show(
                    "Access denied. RegHunter must be run as Administrator to delete this value.",
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Delete failed:\n" + ex.Message,
                    "REGHUNTER", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // -------------------------------------------------------------------------
        // "Copy Path" — strips annotation so the clipboard always has a clean,
        // pasteable path (no trailing "  [S-1-5-...]" suffixes).
        // -------------------------------------------------------------------------

        private void CtxCopy_Click(object sender, EventArgs e)
        {
            string path = SelectedKeyPath();
            if (path != null)
                Clipboard.SetText(StripAnnotation(path));
        }

        // -------------------------------------------------------------------------
        // Registry path helpers
        // -------------------------------------------------------------------------

        // Delete exactly the named key — no children.
        // Returns true on success, false + error on failure.
        // allowTree: if true, falls back to DeleteSubKeyTree when children exist.
        private static bool TryDeleteRegistryKey(string friendlyPath, bool allowTree, out string error)
        {
            error = null;

            if (!TryParseHive(friendlyPath, out RegistryHive hiveEnum, out string subPath))
            {
                error = $"Cannot determine registry hive for path:\n{friendlyPath}";
                return false;
            }

            if (string.IsNullOrEmpty(subPath))
            {
                error = "Cannot delete a root hive key.";
                return false;
            }

            int lastSlash = subPath.LastIndexOf('\\');
            string parentSubPath = lastSlash < 0 ? null : subPath.Substring(0, lastSlash);
            string leafName = lastSlash < 0 ? subPath : subPath.Substring(lastSlash + 1);

            // ETW reports kernel paths before WOW64 redirection, so a 32-bit game's key
            // under HKLM\SOFTWARE may appear at that path in ETW but actually live in the
            // 32-bit registry view. We probe 64-bit first; if that returns null, we try
            // the 32-bit view. Whichever view finds the key is used for the delete too.
            RegistryView foundView = RegistryView.Registry64;
            try
            {
                using (RegistryKey root64 = RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry64))
                using (RegistryKey probe = root64.OpenSubKey(subPath, writable: false))
                {
                    if (probe == null)
                    {
                        // Not in 64-bit view — try 32-bit view.
                        using (RegistryKey root32 = RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry32))
                        using (RegistryKey probe32 = root32.OpenSubKey(subPath, writable: false))
                        {
                            if (probe32 == null)
                            {
                                error = "Key not found in either the 64-bit or 32-bit registry view.\n" +
                                        "It may have already been deleted, or this account lacks read access.\n\n" +
                                        friendlyPath;
                                return false;
                            }
                        }
                        foundView = RegistryView.Registry32;
                    }
                }

                // Open root in the view where we found the key.
                using (RegistryKey root = RegistryKey.OpenBaseKey(hiveEnum, foundView))
                {
                    RegistryKey parentKey = parentSubPath == null
                        ? root
                        : root.OpenSubKey(parentSubPath, writable: true);

                    if (parentKey == null)
                    {
                        string viewLabel = foundView == RegistryView.Registry32 ? "32-bit" : "64-bit";
                        error = $"Parent key could not be opened for writing ({viewLabel} view, access denied):\n" +
                                $"{(parentSubPath ?? "(root hive)")}";
                        return false;
                    }

                    using (parentKey)
                    {
                        if (allowTree)
                        {
                            // throwOnMissingSubKey:true — already probed existence above;
                            // an ArgumentException here means a genuine race.
                            parentKey.DeleteSubKeyTree(leafName, throwOnMissingSubKey: true);
                        }
                        else
                        {
                            // InvalidOperationException = has subkeys → caller offers tree-delete.
                            parentKey.DeleteSubKey(leafName, throwOnMissingSubKey: true);
                        }
                    }
                }
                return true;
            }
            catch (InvalidOperationException)
            {
                error = "HAS_CHILDREN";
                return false;
            }
            catch (ArgumentException)
            {
                error = "Key no longer exists — it may have been deleted by another process " +
                        "between the confirmation and the delete attempt.";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                error = "Access denied. RegHunter must be run as Administrator to delete this key.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Resolve "HKLM\..." -> (RegistryHive.LocalMachine, "...")
        // Returns the RegistryHive enum so callers can open explicit 32/64-bit views.
        private static bool TryParseHive(string path, out RegistryHive hive, out string subPath)
        {
            hive = RegistryHive.LocalMachine; // default, overwritten below
            subPath = null;
            if (string.IsNullOrEmpty(path)) return false;

            var parts = path.Split(new[] { '\\' }, 2);
            string hiveToken = parts[0].ToUpperInvariant().TrimStart('\\');
            subPath = parts.Length > 1 ? parts[1] : "";

            switch (hiveToken)
            {
                case "HKLM":
                case "HKEY_LOCAL_MACHINE":
                    hive = RegistryHive.LocalMachine; return true;
                case "HKCU":
                case "HKEY_CURRENT_USER":
                    hive = RegistryHive.CurrentUser; return true;
                case "HKU":
                case "HKEY_USERS":
                    hive = RegistryHive.Users; return true;
                case "HKCR":
                case "HKEY_CLASSES_ROOT":
                    hive = RegistryHive.ClassesRoot; return true;
                case "HKCC":
                case "HKEY_CURRENT_CONFIG":
                    hive = RegistryHive.CurrentConfig; return true;
                default:
                    return false;
            }
        }

        // Expand short hive names to full names required by Regedit's LastKey value.
        // "HKLM\Software\Foo" -> "HKEY_LOCAL_MACHINE\Software\Foo"
        // "HKU\S-1-5-21-...\Software" -> "HKEY_USERS\S-1-5-21-...\Software"
        private static string FriendlyToFullHiveName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("(")) return null; // unresolved / annotation-only

            // Strip any annotation before expanding, so "HKCR\Foo  [SID]" works cleanly.
            path = StripAnnotation(path);

            var parts = path.Split(new[] { '\\' }, 2);
            string hiveToken = parts[0].ToUpperInvariant();
            string rest = parts.Length > 1 ? @"\" + parts[1] : "";

            switch (hiveToken)
            {
                case "HKLM": return "HKEY_LOCAL_MACHINE" + rest;
                case "HKCU": return "HKEY_CURRENT_USER" + rest;
                case "HKU": return "HKEY_USERS" + rest;
                case "HKCR": return "HKEY_CLASSES_ROOT" + rest;
                case "HKCC": return "HKEY_CURRENT_CONFIG" + rest;
                // Already full names (fallback)
                case "HKEY_LOCAL_MACHINE":
                case "HKEY_CURRENT_USER":
                case "HKEY_USERS":
                case "HKEY_CLASSES_ROOT":
                case "HKEY_CURRENT_CONFIG":
                    return path;
                default:
                    return null;
            }
        }

        // Strip trailing "  [annotation]" from paths like "HKCR\Foo  [S-1-5-...]"
        private static string StripAnnotation(string path)
        {
            if (path == null) return null;
            int idx = path.IndexOf("  [", StringComparison.Ordinal);
            return idx >= 0 ? path.Substring(0, idx).TrimEnd() : path;
        }

        // Get the KEY PATH column text for the currently selected list item, or null.
        private string SelectedKeyPath()
        {
            if (_list.SelectedItems.Count == 0) return null;
            var item = _list.SelectedItems[0];
            if (item.SubItems.Count < 3) return null;
            return item.SubItems[2].Text;
        }

        // Returns the value name if the selected row represents a registry VALUE
        // event (DETAIL starts with "value: " or "deleted value: "), or null if
        // the row is a key event (created/removed/set info). Rows produced from
        // RegistrySetValue/RegistryDeleteValue put the PARENT KEY in KEY PATH and
        // the value name in DETAIL — see RegMonitor.Start().
        private string SelectedValueName()
        {
            if (_list.SelectedItems.Count == 0) return null;
            var item = _list.SelectedItems[0];
            if (item.SubItems.Count < 4) return null;
            string detail = item.SubItems[3].Text;

            const string prefix1 = "value: ";
            const string prefix2 = "deleted value: ";

            if (detail.StartsWith(prefix1, StringComparison.Ordinal))
                return detail.Substring(prefix1.Length);
            if (detail.StartsWith(prefix2, StringComparison.Ordinal))
                return detail.Substring(prefix2.Length);

            return null;
        }

        // -------------------------------------------------------------------------
        // Monitor attachment
        // -------------------------------------------------------------------------

        private void BtnAttach_Click(object sender, EventArgs e)
        {
            using (var picker = new Picker())
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;

                _monitor?.Stop();
                _monitor?.Dispose();
                _monitor = null;
                _list.Items.Clear();

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

        // Fired on threadpool — marshal to UI.
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
                // Iterate forward and always insert at 0.
                // Each insert pushes the previous one down, so the last item
                // processed (newest in the batch) ends up at index 0 — top of list.
                for (int i = 0; i < batch.Count; i++)
                {
                    var ev = batch[i];
                    var item = new ListViewItem(ev.Time.ToString("HH:mm:ss.fff"));
                    item.SubItems.Add(StatusText(ev.Op));
                    item.SubItems.Add(ev.KeyPath);
                    item.SubItems.Add(ev.Detail);
                    item.ForeColor = StatusColor(ev.Op);
                    _list.Items.Insert(0, item);
                }
                while (_list.Items.Count > MaxItems)
                    _list.Items.RemoveAt(_list.Items.Count - 1);
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