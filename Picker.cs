using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RegHunter
{
    internal sealed class Picker : Form
    {
        private readonly ListView _list;
        private readonly RoundedButton _btnAttach;
        private readonly RoundedButton _btnRefresh;

        public int SelectedPid { get; private set; } = -1;
        public string SelectedName { get; private set; } = "";

        public Picker()
        {
            Text = "ATTACH TO PROCESS";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            Size = new Size(440, 480);
            DoubleBuffered = true;

            var shell = new RoundedPanel { Dock = DockStyle.Fill, Radius = 12, Padding = new Padding(14) };
            Controls.Add(shell);

            var title = new Label
            {
                Text = "SELECT TARGET PROCESS",
                ForeColor = Theme.Accent,
                Font = Theme.FontTitle,
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft
            };
            shell.Controls.Add(title);

            _list = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                Font = Theme.FontMono,
                Dock = DockStyle.Fill
            };
            _list.Columns.Add("PID", 70);
            _list.Columns.Add("PROCESS", 170);
            _list.Columns.Add("PATH", 600);
            _list.DoubleClick += (s, e) => AttachSelected();
            shell.Controls.Add(_list);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
            _btnRefresh = new RoundedButton { Text = "REFRESH", Size = new Size(100, 32), Location = new Point(0, 6) };
            _btnAttach = new RoundedButton { Text = "ATTACH", Size = new Size(110, 32) };
            _btnRefresh.Click += (s, e) => LoadProcesses();
            _btnAttach.Click += (s, e) => AttachSelected();
            bottom.Controls.Add(_btnRefresh);
            bottom.Controls.Add(_btnAttach);
            bottom.Resize += (s, e) => _btnAttach.Location = new Point(bottom.Width - _btnAttach.Width, 6);
            shell.Controls.Add(bottom);

            title.BringToFront();
            bottom.BringToFront();

            LoadProcesses();
        }

        private void LoadProcesses()
        {
            _list.BeginUpdate();
            _list.Items.Clear();

            Process[] procs = Process.GetProcesses();
            try
            {
                var groups = new Dictionary<string, List<(int Pid, string Path)>>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var p in procs)
                {
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; }
                    catch { }

                    if (string.IsNullOrEmpty(path)) continue;

                    string name = p.ProcessName;
                    if (!groups.TryGetValue(name, out var list))
                    {
                        list = new List<(int, string)>();
                        groups[name] = list;
                    }
                    list.Add((p.Id, path));
                }

                foreach (var kvp in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var instances = kvp.Value;
                    instances.Sort((a, b) => a.Pid.CompareTo(b.Pid));
                    int repPid = instances[0].Pid;
                    string repPath = instances[0].Path;
                    string exeName = kvp.Key + ".exe";

                    string pidText = instances.Count == 1
                        ? repPid.ToString()
                        : $"{instances.Count}× {repPid}";

                    var item = new ListViewItem(pidText);
                    item.SubItems.Add(exeName);
                    item.SubItems.Add(repPath);
                    item.Tag = instances.Select(i => i.Pid).ToArray();

                    if (instances.Count > 1)
                        item.ForeColor = Theme.TextDim;

                    _list.Items.Add(item);
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
                _list.EndUpdate();
            }
        }

        private void AttachSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var item = _list.SelectedItems[0];

            int[] pids = (int[])item.Tag;
            SelectedPid = pids[0];
            SelectedName = item.SubItems[1].Text;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}