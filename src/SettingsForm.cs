using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace RetroBatFolderSwitcher
{
    internal class SettingsForm : Form
    {
        private readonly TextBox _rbPath = new TextBox();
        private readonly TextBox _exe = new TextBox();
        private readonly TextBox _args = new TextBox();
        private readonly TextBox _link = new TextBox();

        private readonly CheckBox _wifiOn = new CheckBox { Text = "1. If Wi-Fi name contains", AutoSize = true };
        private readonly TextBox _ssid = new TextBox();
        private readonly ComboBox _wifiFolder = FolderCombo();

        private readonly CheckBox _dockOn = new CheckBox { Text = "2. Else if all these drives exist", AutoSize = true };
        private readonly TextBox _drives = new TextBox();
        private readonly ComboBox _dockFolder = FolderCombo();

        private readonly ComboBox _defaultFolder = FolderCombo();

        private readonly Label _now = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        private readonly TextBox _output = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window
        };

        private bool _dirty;

        public SettingsForm()
        {
            Text = "RetroBat Folder Switcher - Settings";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 680);
            MinimumSize = new Size(640, 620);
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

            Padding = new Padding(10);

            // ---- General ----
            var g = Grid();
            AddRow(g, Lbl("RetroBat folder"), _rbPath, Btn("Browse...", BrowseRetroBat));
            AddRow(g, Lbl("RetroBat executable"), _exe);
            AddRow(g, Lbl("Launch arguments"), _args);
            AddRow(g, Lbl("Folder RetroBat reads"), _link);
            var generalGroup = Group("General", g);

            // ---- Rules ----
            var r = Grid();
            AddRow(r, _wifiOn, _ssid, Btn("Use current", UseCurrentWifi));
            AddRow(r, Lbl("      use folder"), _wifiFolder, Btn("Open", () => OpenSub(_wifiFolder.Text)));
            AddRow(r, Spacer(), Spacer());
            AddRow(r, _dockOn, _drives, Btn("Check", CheckDrives));
            AddRow(r, Lbl("      use folder"), _dockFolder, Btn("Open", () => OpenSub(_dockFolder.Text)));
            AddRow(r, Spacer(), Spacer());
            AddRow(r, Lbl("3. Otherwise use folder"), _defaultFolder, Btn("Open", () => OpenSub(_defaultFolder.Text)));
            AddRow(r, Spacer(), Spacer());
            AddRow(r, Lbl("Right now"), _now, Btn("Refresh", RefreshNow));
            var rulesGroup = Group("Rules (checked top to bottom, first match wins)", r);

            // ---- Buttons ----
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 6) };
            buttons.Controls.Add(Btn("Test (dry run)", TestDryRun));
            buttons.Controls.Add(Btn("Show log", ShowLog));
            buttons.Controls.Add(Btn("Desktop shortcuts", CreateShortcuts));
            buttons.Controls.Add(Btn("Save", () => Save(true)));
            buttons.Controls.Add(Btn("Save && launch RetroBat", SaveAndLaunch, bold: true));
            buttons.Controls.Add(Btn("Close", Close));

            // Dock order: Fill first, then Top controls bottom-to-top.
            Controls.Add(_output);
            Controls.Add(buttons);
            Controls.Add(rulesGroup);
            Controls.Add(generalGroup);

            var tips = new ToolTip();
            tips.SetToolTip(_rbPath, "Leave empty if this exe is in the RetroBat folder.");
            tips.SetToolTip(_ssid, "Case-insensitive. Matches if the connected Wi-Fi name contains this text.");
            tips.SetToolTip(_drives, "Comma-separated, e.g.  G:\\, H:\\");
            tips.SetToolTip(_link, "RetroBat reads 'roms'. It becomes a link to the chosen folder.");
            tips.SetToolTip(_defaultFolder, "Your original roms folder is renamed to this on the first real run.");

            LoadConfig();

            foreach (var c in new Control[] { _rbPath, _exe, _args, _link, _ssid, _drives, _wifiFolder, _dockFolder, _defaultFolder })
                c.TextChanged += (s, e) => _dirty = true;
            _wifiOn.CheckedChanged += (s, e) => { _dirty = true; UpdateEnabled(); };
            _dockOn.CheckedChanged += (s, e) => { _dirty = true; UpdateEnabled(); };
            _rbPath.Leave += (s, e) => RefreshFolderLists();

            FormClosing += OnClosingAsk;
            Shown += (s, e) => { _dirty = false; RefreshNow(); };
        }

        // ---------- Config <-> UI ----------

        private void LoadConfig()
        {
            Config c;
            try { c = ConfigStore.Load(Program.ConfigPath, Out); }
            catch (Exception ex)
            {
                Out("Could not read " + Program.ConfigFileName + ": " + ex.Message + " - showing defaults.");
                c = new Config();
            }
            _rbPath.Text = c.RetroBatPath;
            _exe.Text = c.RetroBatExe;
            _args.Text = c.LaunchArguments;
            _link.Text = c.LinkName;
            _wifiOn.Checked = c.WifiRuleEnabled;
            _ssid.Text = c.WifiSsidContains;
            _dockOn.Checked = c.DockedRuleEnabled;
            _drives.Text = string.Join(", ", c.DockedDrives ?? new List<string>());
            RefreshFolderLists();
            _wifiFolder.Text = c.WifiFolder;
            _dockFolder.Text = c.DockedFolder;
            _defaultFolder.Text = c.DefaultFolder;
            UpdateEnabled();
        }

        private Config FromUi() => new Config
        {
            RetroBatPath = _rbPath.Text.Trim(),
            RetroBatExe = Or(_exe.Text, "retrobat.exe"),
            LaunchArguments = _args.Text.Trim(),
            LinkName = Or(_link.Text, "roms"),
            WifiRuleEnabled = _wifiOn.Checked,
            WifiSsidContains = _ssid.Text.Trim(),
            WifiFolder = Or(_wifiFolder.Text, "roms-local"),
            DockedRuleEnabled = _dockOn.Checked,
            DockedDrives = _drives.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(Switcher.NormalizeDrive).Distinct().ToList(),
            DockedFolder = Or(_dockFolder.Text, "roms-docked"),
            DefaultFolder = Or(_defaultFolder.Text, "roms-default"),
        };

        private bool CheckConfig(Config c)
        {
            var problems = new List<string>();
            string rb = RbDir(c);
            if (!Directory.Exists(rb)) problems.Add("RetroBat folder not found: " + rb);
            else if (!File.Exists(Path.Combine(rb, c.RetroBatExe))) problems.Add(c.RetroBatExe + " not found in " + rb);

            var names = new[] { c.WifiFolder, c.DockedFolder, c.DefaultFolder };
            if (names.Any(n => n.Equals(c.LinkName, StringComparison.OrdinalIgnoreCase)))
                problems.Add("Rule folders can't be named '" + c.LinkName + "' - that name becomes the link.");
            if (names.Any(n => n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                problems.Add("Folder names can't contain \\ / : * ? \" < > |");

            if (problems.Count == 0) return true;
            if (problems.Any(p => p.StartsWith("Rule folders") || p.StartsWith("Folder names")))
            {
                MessageBox.Show(this, string.Join("\n", problems), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            return MessageBox.Show(this, string.Join("\n", problems) + "\n\nSave anyway?", Text,
                       MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        private bool Save(bool announce)
        {
            var c = FromUi();
            if (!CheckConfig(c)) return false;
            try
            {
                ConfigStore.Save(Program.ConfigPath, c);
                _dirty = false;
                if (announce) Out("Saved " + Program.ConfigPath);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save settings:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // ---------- Actions ----------

        private void TestDryRun()
        {
            var c = FromUi();
            Out("---- dry run (unsaved settings) ----");
            try { new Switcher(Out).Apply(c, RbDir(c), dryRun: true); }
            catch (Exception ex) { Out("ERROR: " + ex.Message); }
        }

        private void SaveAndLaunch()
        {
            if (!Save(false)) return;
            var c = FromUi();
            string rb = RbDir(c);
            var sw = new Switcher(s => { Out(s); Program.Log(s); });
            Program.Log("---- start (from settings) " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            if (Switcher.IsRetroBatRunning())
            {
                Out("RetroBat is already running - close it first so the folder can be switched.");
                return;
            }
            try { sw.Apply(c, rb, dryRun: false); }
            catch (Exception ex)
            {
                Out("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Couldn't switch folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (sw.Launch(rb, c)) Close();
        }

        private void RefreshNow()
        {
            var c = FromUi();
            var quiet = new List<string>();
            string folder = new Switcher(quiet.Add).ChooseFolder(c, out string reason);
            _now.Text = reason + "  →  " + folder;
            _now.ForeColor = SystemColors.ControlText;
            foreach (var l in quiet) Out(l);
        }

        private void UseCurrentWifi()
        {
            var ssids = Wifi.GetConnectedSsids();
            if (ssids.Count == 0)
            {
                MessageBox.Show(this,
                    "No Wi-Fi name detected.\n\nIf you are on Wi-Fi, Windows may be hiding the name: turn on Location " +
                    "services and 'Let desktop apps access your location' (Settings > Privacy & security > Location).",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _ssid.Text = ssids[0];
            _wifiOn.Checked = true;
            Out("Connected Wi-Fi: " + string.Join(", ", ssids));
            RefreshNow();
        }

        private void CheckDrives()
        {
            var c = FromUi();
            if (c.DockedDrives.Count == 0) { Out("No drives listed."); return; }
            Out("Drives: " + string.Join(", ", c.DockedDrives.Select(d => d + (Switcher.DriveReady(d) ? " ready" : " missing"))));
            var ready = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } })
                                 .Select(d => d.Name);
            Out("All ready drives on this PC: " + string.Join(", ", ready));
            RefreshNow();
        }

        private void BrowseRetroBat()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Select your RetroBat folder (the one with retrobat.exe)" })
            {
                string cur = RbDir(FromUi());
                if (Directory.Exists(cur)) dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _rbPath.Text = PathsEqual(dlg.SelectedPath, Program.ExeDir) ? "" : dlg.SelectedPath;
                RefreshFolderLists();
            }
        }

        private void OpenSub(string name)
        {
            string p = Path.Combine(RbDir(FromUi()), name ?? "");
            if (!Directory.Exists(p))
            {
                if (MessageBox.Show(this, "'" + p + "' doesn't exist. Create it?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try { Directory.CreateDirectory(p); RefreshFolderLists(); }
                catch (Exception ex) { Out("Could not create folder: " + ex.Message); return; }
            }
            Process.Start("explorer.exe", "\"" + p + "\"");
        }

        private void ShowLog()
        {
            if (File.Exists(Program.LogPath)) Process.Start("notepad.exe", "\"" + Program.LogPath + "\"");
            else Out("No log yet: " + Program.LogPath);
        }

        private void CreateShortcuts()
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string rbExe = Path.Combine(RbDir(FromUi()), FromUi().RetroBatExe);
                string icon = File.Exists(rbExe) ? rbExe : exe;
                MakeShortcut(Path.Combine(desktop, "RetroBat (Folder Switcher).lnk"), exe, "", icon, "Pick ROM folder, then start RetroBat");
                MakeShortcut(Path.Combine(desktop, "RetroBat Folder Switcher Settings.lnk"), exe, "--settings", exe, "RetroBat Folder Switcher settings");
                Out("Created desktop shortcuts: 'RetroBat (Folder Switcher)' and 'RetroBat Folder Switcher Settings'.");
            }
            catch (Exception ex) { Out("Could not create shortcuts: " + ex.Message); }
        }

        private static void MakeShortcut(string lnk, string target, string args, string icon, string desc)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { icon + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { desc });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        private void OnClosingAsk(object sender, FormClosingEventArgs e)
        {
            if (!_dirty) return;
            var r = MessageBox.Show(this, "Save changes before closing?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) e.Cancel = true;
            else if (r == DialogResult.Yes && !Save(false)) e.Cancel = true;
        }

        // ---------- Helpers ----------

        private void RefreshFolderLists()
        {
            var c = FromUi();
            string rb = RbDir(c);
            var names = new List<string>();
            try
            {
                if (Directory.Exists(rb))
                    names = Directory.GetDirectories(rb)
                        .Select(Path.GetFileName)
                        .Where(n => n.StartsWith("rom", StringComparison.OrdinalIgnoreCase) &&
                                    !n.Equals(c.LinkName, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(n => n).ToList();
            }
            catch { }
            foreach (var cb in new[] { _wifiFolder, _dockFolder, _defaultFolder })
            {
                string keep = cb.Text;
                cb.Items.Clear();
                cb.Items.AddRange(names.Cast<object>().ToArray());
                cb.Text = keep;
            }
        }

        private void UpdateEnabled()
        {
            _ssid.Enabled = _wifiFolder.Enabled = _wifiOn.Checked;
            _drives.Enabled = _dockFolder.Enabled = _dockOn.Checked;
        }

        private void Out(string msg)
        {
            if (_output.TextLength > 0) _output.AppendText("\r\n");
            _output.AppendText(msg);
        }

        private static string RbDir(Config c)
        {
            try { return Switcher.ResolveRetroBatDir(c, Program.ExeDir); }
            catch { return c.RetroBatPath ?? ""; }
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

        private static ComboBox FolderCombo() => new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };

        private static Label Lbl(string t) => new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) };

        private static Control Spacer() => new Panel { Height = 4, Margin = new Padding(0) };

        private static Button Btn(string text, Action onClick, bool bold = false)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(3) };
            if (bold) b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            b.Click += (s, e) => onClick();
            return b;
        }

        private static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(4) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        private static void AddRow(TableLayoutPanel t, Control a, Control b, Control c = null)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (a is CheckBox) a.Margin = new Padding(3, 6, 8, 3);
            if (b is TextBox || b is ComboBox) b.Dock = DockStyle.Fill;
            if (b is Label) b.Margin = new Padding(3, 7, 3, 3);
            t.Controls.Add(a, 0, row);
            t.Controls.Add(b, 1, row);
            if (c != null) t.Controls.Add(c, 2, row);
        }

        private static GroupBox Group(string title, Control content)
        {
            var g = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 8) };
            g.Controls.Add(content);
            return g;
        }
    }
}
