using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RetroBatFolderSwitcher
{
    /// <summary>
    /// Settings window. Tabs: Launch (this file), Libraries + Media links (SettingsForm.Links.cs), Sync (SettingsForm.Sync.cs).
    /// Below the tabs: the Activity log (with status + Stop) and Save / Save & launch / Close.
    /// </summary>
    internal partial class SettingsForm : Form
    {
        // ---- Launch tab ----
        private readonly CheckBox _dockOn = RuleHeader("1.  Docked");
        private readonly TextBox _drives = new TextBox();
        private readonly ComboBox _dockFolder = FolderCombo();
        private readonly CheckBox _wifiOn = RuleHeader("2.  Home Wi-Fi");
        private readonly TextBox _ssid = new TextBox();
        private readonly ComboBox _wifiFolder = FolderCombo();
        private readonly ComboBox _defaultFolder = FolderCombo();
        private readonly Label _now = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        private readonly CheckBox _libsOnLaunch = new CheckBox { Text = "Update library links", AutoSize = true, Margin = new Padding(3, 3, 24, 3) };
        private readonly CheckBox _mediaOnLaunch = new CheckBox { Text = "Update media links", AutoSize = true };
        private readonly TextBox _rbPath = new TextBox();
        private readonly TextBox _exe = new TextBox();
        private readonly TextBox _args = new TextBox();
        private readonly TextBox _link = new TextBox();

        // ---- Activity area + background tasks ----
        private readonly TextBox _output = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false,
            Dock = DockStyle.Bottom, Height = 140, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window
        };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Ready." };
        private Button _stopBtn;
        private readonly List<Button> _busyButtons = new List<Button>();
        private volatile bool _busy, _stopRequested;
        private readonly ConcurrentQueue<string> _taskLines = new ConcurrentQueue<string>();
        private readonly System.Windows.Forms.Timer _flushTimer = new System.Windows.Forms.Timer { Interval = 150 };
        private StreamWriter _taskLog;
        private readonly object _taskLogLock = new object();

        private readonly ToolTip _tips = new ToolTip { AutoPopDelay = 15000 };
        private bool _dirty, _loading;

        public SettingsForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);   // laid out at 100%; scaled up on high-DPI screens
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            Text = "RetroBat Folder Switcher";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(820, 860);
            MinimumSize = new Size(600, 520);
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            tabs.TabPages.Add(Page("Launch", BuildLaunchTab()));
            tabs.TabPages.Add(Page("Libraries", BuildLibrariesTab()));
            tabs.TabPages.Add(Page("Media links", BuildMediaTab()));
            tabs.TabPages.Add(Page("Sync", BuildSyncTab()));

            var activity = BuildActivityHeader();
            var bar = BuildButtonBar();

            // Dock order: Fill first, then Bottom controls from top to bottom.
            Controls.Add(tabs);
            Controls.Add(activity);
            Controls.Add(_output);
            Controls.Add(bar);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Copy all", null, (s, e) => { if (_output.TextLength > 0) Clipboard.SetText(_output.Text); });
            menu.Items.Add("Clear", null, (s, e) => _output.Clear());
            menu.Items.Add("Open log file", null, (s, e) => ShowLog());
            _output.ContextMenuStrip = menu;

            LoadConfig();
            TrackChanges(this);

            _dockOn.CheckedChanged += (s, e) => UpdateEnabled();
            _wifiOn.CheckedChanged += (s, e) => UpdateEnabled();
            _rbPath.Leave += (s, e) => RefreshFolderLists();
            _flushTimer.Tick += (s, e) => FlushTaskLines();
            _flushTimer.Start();

            KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.S) { Save(true); e.SuppressKeyPress = true; } };
            FormClosing += OnClosingAsk;
            Load += (s, e) => FitToScreen();
            Resize += (s, e) => SizeLogBox();
            Shown += (s, e) => RefreshNow();

            ResumeLayout(false);
            PerformLayout();
        }

        // =====================================================================
        // Launch tab
        // =====================================================================

        private Control BuildLaunchTab()
        {
            // Rules
            var r = Grid();
            AddFull(r, _dockOn);
            AddRow(r, Indent(Lbl("Drives connected")), _drives, Btn("Check", CheckDrives));
            AddRow(r, Indent(Lbl("Roms folder")), _dockFolder, Btn("Open", () => OpenFolder(_dockFolder.Text)));
            AddFull(r, Gap(4));
            AddFull(r, _wifiOn);
            AddRow(r, Indent(Lbl("Wi-Fi name contains")), _ssid, Btn("Detect", UseCurrentWifi));
            AddRow(r, Indent(Lbl("Roms folder")), _wifiFolder, Btn("Open", () => OpenFolder(_wifiFolder.Text)));
            AddFull(r, Gap(4));
            AddFull(r, RuleTitle("3.  Anywhere else"));
            AddRow(r, Indent(Lbl("Roms folder")), _defaultFolder, Btn("Open", () => OpenFolder(_defaultFolder.Text)));
            AddFull(r, Line());
            AddRow(r, Lbl("Right now"), _now, Btn("Refresh", RefreshNow));
            var rules = Group("Which roms folder RetroBat uses", r, "Checked from top to bottom. The first rule that matches wins.");

            // Before launching
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            flow.Controls.Add(_libsOnLaunch);
            flow.Controls.Add(_mediaOnLaunch);
            var before = Group("Before launching", flow);
            _tips.SetToolTip(_libsOnLaunch, "Adds library links for systems that are new since last time (Libraries tab).");
            _tips.SetToolTip(_mediaOnLaunch, "Adds media links for systems that are new since last time (Media links tab).");

            // RetroBat
            var g = Grid();
            AddRow(g, Lbl("RetroBat folder"), _rbPath, Btn("Browse...", BrowseRetroBat));
            var line = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, Margin = new Padding(0) };
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            line.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
            _exe.Dock = _args.Dock = _link.Dock = DockStyle.Fill;
            line.Controls.Add(_exe, 0, 0);
            line.Controls.Add(Lbl("Arguments"), 1, 0);
            line.Controls.Add(_args, 2, 0);
            line.Controls.Add(Lbl("Linked folder"), 3, 0);
            line.Controls.Add(_link, 4, 0);
            AddRow(g, Lbl("Program"), line);
            var acts = ButtonRow(Btn("Test (dry run)", TestDryRun), Btn("Create desktop shortcuts", CreateShortcuts));
            AddFull(g, acts);
            var retrobat = Group("RetroBat", g, "Leave the folder empty if this app is in the RetroBat folder.");

            _tips.SetToolTip(_drives, "All of these must be connected, e.g.  G:\\, H:\\");
            _tips.SetToolTip(_ssid, "Matches when the connected Wi-Fi name contains this text (not case-sensitive).");
            _tips.SetToolTip(_link, "The folder RetroBat reads (normally 'roms'). It becomes a link to the chosen roms folder.");
            _tips.SetToolTip(_defaultFolder, "On the first run your original roms folder is renamed to this.");
            foreach (var cb in new[] { _dockFolder, _wifiFolder, _defaultFolder })
                _tips.SetToolTip(cb, "A folder name inside the RetroBat folder, or a full path (e.g. a library).");

            var panel = new Panel { AutoScroll = true };
            panel.Controls.Add(retrobat);   // Top-docked: added bottom-to-top
            panel.Controls.Add(before);
            panel.Controls.Add(rules);
            return panel;
        }

        private void LoadConfig()
        {
            _loading = true;
            Config c;
            try { c = ConfigStore.Load(Program.ConfigPath, Out); }
            catch (Exception ex)
            {
                Out("Could not read " + Program.ConfigFileName + ": " + ex.Message + " - showing defaults.");
                c = new Config();
            }

            _dockOn.Checked = c.DockedRuleEnabled;
            _drives.Text = string.Join(", ", c.DockedDrives ?? new List<string>());
            _wifiOn.Checked = c.WifiRuleEnabled;
            _ssid.Text = c.WifiSsidContains;
            _libsOnLaunch.Checked = c.BuildLibrariesOnLaunch;
            _mediaOnLaunch.Checked = c.MediaLinksOnLaunch;
            _rbPath.Text = c.RetroBatPath;
            _exe.Text = c.RetroBatExe;
            _args.Text = c.LaunchArguments;
            _link.Text = c.LinkName;

            LoadLinks(c);
            LoadSync(c);

            RefreshFolderLists();
            _dockFolder.Text = c.DockedFolder;
            _wifiFolder.Text = c.WifiFolder;
            _defaultFolder.Text = c.DefaultFolder;
            UpdateEnabled();
            _loading = false;
            _dirty = false;
        }

        private Config FromUi()
        {
            CommitLib();
            var c = new Config
            {
                RetroBatPath = _rbPath.Text.Trim(),
                RetroBatExe = Or(_exe.Text, "retrobat.exe"),
                LaunchArguments = _args.Text.Trim(),
                LinkName = Or(_link.Text, "roms"),
                DockedRuleEnabled = _dockOn.Checked,
                DockedDrives = _drives.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(Switcher.NormalizeDrive).Distinct().ToList(),
                DockedFolder = Or(_dockFolder.Text, "roms-docked"),
                WifiRuleEnabled = _wifiOn.Checked,
                WifiSsidContains = _ssid.Text.Trim(),
                WifiFolder = Or(_wifiFolder.Text, "roms-local"),
                DefaultFolder = Or(_defaultFolder.Text, "roms-default"),
                BuildLibrariesOnLaunch = _libsOnLaunch.Checked,
                MediaLinksOnLaunch = _mediaOnLaunch.Checked,
            };
            ApplyLinks(c);
            ApplySync(c);
            return c;
        }

        private bool CheckConfig(Config c)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            string rb = RbDir(c);
            if (!Directory.Exists(rb)) warnings.Add("RetroBat folder not found: " + rb);
            else if (!File.Exists(Path.Combine(rb, c.RetroBatExe))) warnings.Add(c.RetroBatExe + " not found in " + rb);

            var names = new[] { c.WifiFolder, c.DockedFolder, c.DefaultFolder }
                .Concat(c.Libraries.Select(l => l.Output ?? "")).ToList();
            if (names.Any(n => n.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || n.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0))
                errors.Add("Folder names can't contain * ? \" < > |");
            else
            {
                string linkFull = Path.Combine(rb, c.LinkName);
                if (names.Where(n => n.Length > 0).Any(n => { try { return PathsEqual(Path.Combine(rb, n), linkFull); } catch { return false; } }))
                    errors.Add("No roms or library folder can be '" + linkFull + "' - that becomes the link.");
            }

            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", errors), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            if (warnings.Count == 0) return true;
            return MessageBox.Show(this, string.Join("\n", warnings) + "\n\nSave anyway?", Text,
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
                _status.Text = "Settings saved.";
                if (announce) Out("Saved " + Program.ConfigPath);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save settings:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void TestDryRun()
        {
            var c = FromUi();
            Out("---- launch test (dry run, unsaved settings are used) ----");
            try { new Switcher(Out).Apply(c, RbDir(c), dryRun: true); }
            catch (Exception ex) { Out("ERROR: " + ex.Message); }
        }

        private void SaveAndLaunch()
        {
            if (_busy) { Out("Wait for the current task to finish (or press Stop)."); return; }
            if (!Save(false)) return;
            var c = FromUi();
            string rb = RbDir(c);
            var sw = new Switcher(s => { Out(s); Program.Log(s); });
            Program.Log("---- start (from settings) " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            if (Switcher.IsRetroBatRunning())
            {
                Out("RetroBat is already running - close it first so the roms folder can be switched.");
                return;
            }
            try { sw.Apply(c, rb, dryRun: false); }
            catch (Exception ex)
            {
                Out("ERROR: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Couldn't switch the roms folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (sw.Launch(rb, c)) Close();
        }

        private void RefreshNow()
        {
            var c = FromUi();
            var lines = new List<string>();
            string folder = new Switcher(lines.Add).ChooseFolder(c, out string reason);
            _now.Text = reason + "   →   " + folder;
            _tips.SetToolTip(_now, string.Join("\n", lines));
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
            Out("Drives: " + string.Join(", ", c.DockedDrives.Select(d => d + (Switcher.DriveReady(d) ? " connected" : " missing"))));
            var ready = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } }).Select(d => d.Name);
            Out("All connected drives: " + string.Join(", ", ready));
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

        private void OpenFolder(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string p = Path.Combine(RbDir(FromUi()), name.Trim());
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
            else Out("No log file yet: " + Program.LogPath);
        }

        private void CreateShortcuts()
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var c = FromUi();
                string rbExe = Path.Combine(RbDir(c), c.RetroBatExe);
                string icon = File.Exists(rbExe) ? rbExe : exe;
                MakeShortcut(Path.Combine(desktop, "RetroBat (Folder Switcher).lnk"), exe, "", icon, "Pick the roms folder, then start RetroBat");
                MakeShortcut(Path.Combine(desktop, "RetroBat Folder Switcher Settings.lnk"), exe, "--settings", exe, "RetroBat Folder Switcher settings");
                Out("Created desktop shortcuts 'RetroBat (Folder Switcher)' and 'RetroBat Folder Switcher Settings'.");
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

        /// <summary>Fills the roms folder dropdowns with rom* folders in the RetroBat folder and the library folders.</summary>
        private void RefreshFolderLists()
        {
            var c = FromUi();
            string rb = RbDir(c);
            var names = new List<string>();
            try
            {
                if (Directory.Exists(rb))
                    names = Directory.GetDirectories(rb).Select(Path.GetFileName)
                        .Where(n => n.StartsWith("rom", StringComparison.OrdinalIgnoreCase) &&
                                    !n.Equals(c.LinkName, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(n => n).ToList();
            }
            catch { }
            foreach (var l in c.Libraries)
                if (!string.IsNullOrWhiteSpace(l.Output) && !names.Contains(l.Output.Trim(), StringComparer.OrdinalIgnoreCase))
                    names.Add(l.Output.Trim());

            bool was = _loading;
            _loading = true;
            foreach (var cb in new[] { _dockFolder, _wifiFolder, _defaultFolder })
            {
                string keep = cb.Text;
                cb.Items.Clear();
                cb.Items.AddRange(names.Cast<object>().ToArray());
                cb.Text = keep;
            }
            _loading = was;
        }

        private void UpdateEnabled()
        {
            _drives.Enabled = _dockFolder.Enabled = _dockOn.Checked;
            _ssid.Enabled = _wifiFolder.Enabled = _wifiOn.Checked;
        }

        private void OnClosingAsk(object sender, FormClosingEventArgs e)
        {
            if (_busy)
            {
                if (MessageBox.Show(this, "A task is still running. Stop it and close?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
                _stopRequested = true;
                for (int i = 0; i < 50 && _busy; i++) { Thread.Sleep(100); Application.DoEvents(); }
            }
            if (!_dirty) return;
            var r = MessageBox.Show(this, "Save changes before closing?", Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) e.Cancel = true;
            else if (r == DialogResult.Yes && !Save(false)) e.Cancel = true;
        }

        // =====================================================================
        // Activity area, button bar, background tasks
        // =====================================================================

        private Control BuildActivityHeader()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, Padding = new Padding(2, 6, 2, 2) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var title = new Label { Text = "Activity", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 10, 3) };
            _stopBtn = Btn("Stop", () => { _stopRequested = true; _status.Text = "Stopping..."; });
            _stopBtn.Visible = false;
            var openLog = new LinkLabel { Text = "Open log file", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(10, 6, 3, 3) };
            openLog.LinkClicked += (s, e) => ShowLog();
            t.Controls.Add(title, 0, 0);
            t.Controls.Add(_status, 1, 0);
            t.Controls.Add(_stopBtn, 2, 0);
            t.Controls.Add(openLog, 3, 0);
            _tips.SetToolTip(_output, "Right-click to copy or clear.");
            return t;
        }

        private Control BuildButtonBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 0, 2), WrapContents = false
            };
            var close = Btn("Close", Close);
            var launch = Busy(Btn("Save && launch RetroBat", SaveAndLaunch, bold: true));
            var save = Btn("Save", () => Save(true));
            bar.Controls.Add(close);
            bar.Controls.Add(launch);
            bar.Controls.Add(save);
            AcceptButton = null;
            CancelButton = null;
            _tips.SetToolTip(save, "Ctrl+S");
            return bar;
        }

        /// <summary>
        /// Runs work on a background thread. work(log, stopRequested) returns the final status text.
        /// Output goes to the Activity log (batched) and, when logToFile, to the log file.
        /// </summary>
        private void RunTask(string status, bool logToFile, Func<Action<string>, Func<bool>, string> work, Action after = null)
        {
            if (_busy) { Out("Busy - wait for the current task or press Stop."); return; }
            SetBusy(true);
            _stopRequested = false;
            _status.Text = status;
            if (logToFile)
            {
                Program.TrimLogPublic();
                try { _taskLog = new StreamWriter(Program.LogPath, append: true, encoding: new UTF8Encoding(false)); }
                catch { _taskLog = null; }
            }

            Func<bool> stop = () => _stopRequested;
            var t = new Thread(() =>
            {
                string result = "Finished.";
                try { result = work(TaskLine, stop) ?? result; }
                catch (Exception ex) { TaskLine("ERROR: " + ex.Message); result = "Failed - see the log."; }
                finally
                {
                    lock (_taskLogLock) { try { _taskLog?.Dispose(); } catch { } _taskLog = null; }
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            FlushTaskLines();
                            SetBusy(false);
                            _status.Text = result;
                            after?.Invoke();
                        }));
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "task" };
            t.Start();
        }

        private void TaskLine(string line)
        {
            _taskLines.Enqueue(line);
            lock (_taskLogLock) { try { _taskLog?.WriteLine(line); } catch { } }
        }

        private void SetStatusAsync(string text)
        {
            try { BeginInvoke((Action)(() => _status.Text = text)); } catch { }
        }

        private void FlushTaskLines()
        {
            if (_taskLines.IsEmpty) return;
            var sb = new StringBuilder();
            while (_taskLines.TryDequeue(out string l))
            {
                if (sb.Length > 0) sb.Append("\r\n");
                sb.Append(l);
            }
            // Keep the on-screen log manageable; the full output is in the log file.
            if (_output.TextLength > 2000000)
                _output.Text = "(earlier output trimmed - see the log file)\r\n" + _output.Text.Substring(_output.TextLength - 500000);
            Out(sb.ToString());
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (var b in _busyButtons) b.Enabled = !busy;
            _stopBtn.Visible = busy;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }

        private void Out(string msg)
        {
            if (_output.TextLength > 0) _output.AppendText("\r\n");
            _output.AppendText(msg);
            _output.Update();
        }

        // =====================================================================
        // Layout helpers
        // =====================================================================

        private static TabPage Page(string title, Control content)
        {
            var p = new TabPage(title) { Padding = new Padding(10), UseVisualStyleBackColor = true };
            content.Dock = DockStyle.Fill;
            p.Controls.Add(content);
            return p;
        }

        /// <summary>Auto-sized group (stacked from the top) with an optional one-line description.</summary>
        private static GroupBox Group(string title, Control content, string description = null)
        {
            var g = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 10) };
            content.Dock = DockStyle.Top;
            g.Controls.Add(content);
            if (description != null) g.Controls.Add(Hint(description, DockStyle.Top));
            return g;
        }

        /// <summary>Group that fills its cell (for lists that should grow with the window).</summary>
        private static GroupBox FillGroup(string title, Control content)
        {
            var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 10) };
            content.Dock = DockStyle.Fill;
            g.Controls.Add(content);
            return g;
        }

        /// <summary>Vertical stack: rows sized to content except those marked fill.</summary>
        private static TableLayoutPanel Stack(params Control[] rows) => StackWithFill(rows, new HashSet<Control>());

        private static TableLayoutPanel StackWithFill(Control[] rows, HashSet<Control> fill)
        {
            var t = new TableLayoutPanel { ColumnCount = 1, RowCount = rows.Length, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int fills = Math.Max(1, rows.Count(fill.Contains));
            for (int i = 0; i < rows.Length; i++)
            {
                bool f = fill.Contains(rows[i]);
                t.RowStyles.Add(f ? new RowStyle(SizeType.Percent, 100f / fills) : new RowStyle(SizeType.AutoSize));
                rows[i].Dock = DockStyle.Fill;
                t.Controls.Add(rows[i], 0, i);
            }
            return t;
        }

        private static TableLayoutPanel Grid()
        {
            var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        private static void AddRow(TableLayoutPanel t, Control label, Control field, Control button = null)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (field is TextBox || field is ComboBox) field.Dock = DockStyle.Fill;
            t.Controls.Add(label, 0, row);
            t.Controls.Add(field, 1, row);
            if (button != null) t.Controls.Add(button, 2, row);
        }

        private static void AddFull(TableLayoutPanel t, Control c)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, t.ColumnCount);
        }

        private static FlowLayoutPanel ButtonRow(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
            f.Controls.AddRange(items);
            return f;
        }

        private static Label Lbl(string t) => new Label { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 10, 3) };

        private static Control Indent(Label l) { l.Margin = new Padding(24, 7, 10, 3); return l; }

        private static Label Hint(string text, DockStyle dock = DockStyle.None) => new Label
        {
            Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Dock = dock, Margin = new Padding(3, 2, 3, 6), Padding = new Padding(0, 0, 0, 6)
        };

        private static CheckBox RuleHeader(string text) => new CheckBox
        {
            Text = text, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(3, 4, 3, 2)
        };

        private static Label RuleTitle(string text) => new Label
        {
            Text = text, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(20, 6, 3, 2)
        };

        private static Control Gap(int h) => new Panel { Height = h, Margin = new Padding(0) };

        private static Control Line() => new Label { Height = 1, Dock = DockStyle.Fill, BorderStyle = BorderStyle.Fixed3D, AutoSize = false, Margin = new Padding(3, 10, 3, 6) };

        private static ComboBox FolderCombo() => new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };

        private static Button Btn(string text, Action onClick, bool bold = false)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(76, 26), Padding = new Padding(6, 1, 6, 1), Margin = new Padding(3) };
            if (bold) b.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            b.Click += (s, e) => onClick();
            return b;
        }

        /// <summary>Narrow button (Up/Down).</summary>
        private static Button Small(Button b)
        {
            b.AutoSize = false;
            b.MinimumSize = Size.Empty;
            b.Padding = new Padding(0);
            b.Size = new Size(Math.Max(48, TextRenderer.MeasureText(b.Text, b.Font).Width + 16), 26);
            return b;
        }

        private Button Busy(Button b)
        {
            _busyButtons.Add(b);
            return b;
        }

        /// <summary>Marks settings as changed when the user edits any field.</summary>
        private void TrackChanges(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c == _output) continue;
                if (c is TextBox) c.TextChanged += (s, e) => { if (!_loading) _dirty = true; };
                else if (c is ComboBox cb) { cb.TextChanged += (s, e) => { if (!_loading) _dirty = true; }; }
                else if (c is CheckBox ch) ch.CheckedChanged += (s, e) => { if (!_loading) _dirty = true; };
                TrackChanges(c);
            }
        }

        /// <summary>Dialogs are laid out at 96 DPI; this scales them like the main window.</summary>
        private static void ScaleDialog(Form f)
        {
            f.AutoScaleDimensions = new SizeF(96F, 96F);
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.ResumeLayout(false);
            f.PerformLayout();
        }

        private float DpiScale()
        {
            try { using (var g = CreateGraphics()) return Math.Max(1f, g.DpiX / 96f); }
            catch { return 1f; }
        }

        /// <summary>Keeps the window on screen (high-DPI handhelds) and sizes the log box.</summary>
        private void FitToScreen()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
            int w = Math.Min(Width, wa.Width), h = Math.Min(Height, wa.Height);
            Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
            SizeLogBox();
        }

        private void SizeLogBox()
        {
            float k = DpiScale();
            _output.Height = Math.Max((int)(60 * k), Math.Min((int)(200 * k), ClientSize.Height / 7));
        }

        private string Prompt(string title, string text, string value)
        {
            using (var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font, ClientSize = new Size(480, 110)
            })
            {
                f.SuspendLayout();
                var lbl = new Label { Text = text, Left = 10, Top = 10, Width = 460, AutoSize = false, Height = 20 };
                var tb = new TextBox { Text = value, Left = 10, Top = 34, Width = 460 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 314, Top = 72, Width = 75, Height = 28 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 395, Top = 72, Width = 75, Height = 28 };
                f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                ScaleDialog(f);
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        private static string RbDir(Config c)
        {
            try { return Switcher.ResolveRetroBatDir(c, Program.ExeDir); }
            catch { return c.RetroBatPath ?? ""; }
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static string Or(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
    }
}
