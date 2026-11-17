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
            Dock = DockStyle.Bottom, Height = 130, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window
        };

        // Libraries tab
        private List<LibraryGroup> _libs = new List<LibraryGroup>();
        private int _libIdx = -1;
        private bool _loadingLib;
        private readonly ListBox _libList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly CheckBox _libEnabled = new CheckBox { Text = "Enabled", AutoSize = true };
        private readonly TextBox _libOutput = new TextBox();
        private readonly TextBox _libTag = new TextBox { Width = 140 };
        private readonly ListBox _srcList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
        private readonly TextBox _skipPrefix = new TextBox { Width = 70 };
        private readonly ComboBox _linkType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        private readonly CheckBox _buildOnLaunch = new CheckBox { Text = "Add links for new systems every time RetroBat is launched", AutoSize = true };
        private readonly List<Control> _libEditor = new List<Control>();

        private bool _dirty;

        public SettingsForm()
        {
            Text = "RetroBat Folder Switcher - Settings";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(800, 800);
            MinimumSize = new Size(680, 640);
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
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 6) };
            buttons.Controls.Add(Btn("Test (dry run)", TestDryRun));
            buttons.Controls.Add(Btn("Show log", ShowLog));
            buttons.Controls.Add(Btn("Desktop shortcuts", CreateShortcuts));
            buttons.Controls.Add(Btn("Save", () => Save(true)));
            buttons.Controls.Add(Btn("Save && launch RetroBat", SaveAndLaunch, bold: true));
            buttons.Controls.Add(Btn("Close", Close));

            var switchPage = new TabPage("Folder switching") { Padding = new Padding(8), AutoScroll = true };
            switchPage.Controls.Add(rulesGroup);   // Top-docked: added bottom-to-top
            switchPage.Controls.Add(generalGroup);

            var libPage = new TabPage("Libraries") { Padding = new Padding(8), AutoScroll = true };
            BuildLibrariesPage(libPage);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(switchPage);
            tabs.TabPages.Add(libPage);

            // Dock order: Fill first, then Bottom controls top-to-bottom.
            Controls.Add(tabs);
            Controls.Add(buttons);
            Controls.Add(_output);

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

            _libList.SelectedIndexChanged += (s, e) => { if (_loadingLib) return; CommitLib(); SelectLib(_libList.SelectedIndex); };
            _libOutput.TextChanged += (s, e) => LibEdited();
            _libTag.TextChanged += (s, e) => LibEdited();
            _libEnabled.CheckedChanged += (s, e) => LibEdited();
            _libOutput.Leave += (s, e) => RefreshFolderLists();
            _skipPrefix.TextChanged += (s, e) => _dirty = true;
            _linkType.SelectedIndexChanged += (s, e) => _dirty = true;
            _buildOnLaunch.CheckedChanged += (s, e) => _dirty = true;
            _srcList.DoubleClick += (s, e) => EditSource();

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

            _libs = (c.Libraries ?? new List<LibraryGroup>()).Select(l => l.Clone()).ToList();
            _skipPrefix.Text = c.SkipPrefix ?? "";
            _linkType.SelectedItem = new[] { "Auto", "Symlink", "Junction" }
                .FirstOrDefault(t => t.Equals(c.LinkType ?? "", StringComparison.OrdinalIgnoreCase)) ?? "Auto";
            _buildOnLaunch.Checked = c.BuildLibrariesOnLaunch;
            RefreshLibList(_libs.Count > 0 ? 0 : -1);
            RefreshFolderLists();
        }

        private Config FromUi()
        {
            CommitLib();
            return new Config
            {
            Libraries = _libs.Select(l => l.Clone()).ToList(),
            SkipPrefix = _skipPrefix.Text.Trim(),
            LinkType = (_linkType.SelectedItem as string) ?? "Auto",
            BuildLibrariesOnLaunch = _buildOnLaunch.Checked,
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
        }

        private bool CheckConfig(Config c)
        {
            var problems = new List<string>();
            string rb = RbDir(c);
            if (!Directory.Exists(rb)) problems.Add("RetroBat folder not found: " + rb);
            else if (!File.Exists(Path.Combine(rb, c.RetroBatExe))) problems.Add(c.RetroBatExe + " not found in " + rb);

            var names = new[] { c.WifiFolder, c.DockedFolder, c.DefaultFolder }
                .Concat(c.Libraries.Select(l => l.Output ?? "")).ToList();
            if (names.Any(n => n.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || n.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0))
                problems.Add("Folder names can't contain * ? \" < > |");
            else
            {
                string linkFull = Path.Combine(rb, c.LinkName);
                if (names.Where(n => n.Length > 0).Any(n => { try { return PathsEqual(Path.Combine(rb, n), linkFull); } catch { return false; } }))
                    problems.Add("No rule or library folder can be '" + linkFull + "' - that becomes the link.");
            }

            if (problems.Count == 0) return true;
            if (problems.Any(p => p.StartsWith("No rule") || p.StartsWith("Folder names")))
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

        // ---------- Libraries tab ----------

        private void BuildLibrariesPage(TabPage page)
        {
            var intro = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 38,
                Text = "Each library is a folder filled with links to the system folders (nes, snes, ...) found in its sources. " +
                       "Point the switching rules at these folders. Building never deletes real folders or files."
            };

            // Library list
            var libGrid = ListWithButtons(_libList,
                Btn("Add", AddLib), Btn("Remove", RemoveLib), Btn("Up", () => MoveLib(-1)), Btn("Down", () => MoveLib(1)));
            var libsGroup = Group("Libraries", libGrid);

            // Editor
            var ed = Grid();
            AddRow(ed, Lbl("Output folder"), _libOutput, Btn("Browse...", BrowseLibOutput));
            var tagRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            tagRow.Controls.Add(_libTag);
            tagRow.Controls.Add(new Label { Text = "creates an empty marker file \".tag\" in the output folder", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 7, 3, 3) });
            AddRow(ed, Lbl("Tag"), tagRow);
            AddRow(ed, Lbl(""), _libEnabled);
            AddRow(ed, Lbl("Sources"), new Label { Text = "Checked in order. If two sources have the same system, the first one wins.", AutoSize = true, ForeColor = SystemColors.GrayText });
            var srcGrid = ListWithButtons(_srcList,
                Btn("Add folder...", AddSourceFolder), Btn("Add path...", AddSourcePath), Btn("Edit...", EditSource),
                Btn("Remove", RemoveSource), Btn("Up", () => MoveSource(-1)), Btn("Down", () => MoveSource(1)));
            ed.Controls.Add(srcGrid, 0, ed.RowCount);
            ed.SetColumnSpan(srcGrid, 3);
            ed.RowCount++;
            ed.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var editGroup = Group("Selected library", ed);
            _libEditor.AddRange(new Control[] { _libOutput, _libTag, _libEnabled, srcGrid });

            // Options + actions
            _linkType.Items.AddRange(new object[] { "Auto", "Symlink", "Junction" });
            var opt = Grid();
            var skipRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            skipRow.Controls.Add(_skipPrefix);
            skipRow.Controls.Add(new Label { Text = "e.g. _ skips _media. Empty = link everything.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 7, 3, 3) });
            AddRow(opt, Lbl("Skip folders starting with"), skipRow);
            var typeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            typeRow.Controls.Add(_linkType);
            typeRow.Controls.Add(new Label { Text = "Auto = symlink if allowed, otherwise junction (local drives only)", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 7, 3, 3) });
            AddRow(opt, Lbl("Link type"), typeRow);
            AddRow(opt, Lbl(""), _buildOnLaunch);
            var actions = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0), WrapContents = true };
            actions.Controls.Add(Btn("Preview (dry run)", () => RunBuild(all: true, rebuild: false, dryRun: true)));
            actions.Controls.Add(Btn("Build selected", () => RunBuild(all: false, rebuild: false, dryRun: false)));
            actions.Controls.Add(Btn("Build all", () => RunBuild(all: true, rebuild: false, dryRun: false), bold: true));
            actions.Controls.Add(Btn("Rebuild all...", () => RunBuild(all: true, rebuild: true, dryRun: false)));
            opt.Controls.Add(actions, 0, opt.RowCount);
            opt.SetColumnSpan(actions, 3);
            opt.RowCount++;
            opt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var optGroup = Group("Build", opt);

            page.Controls.Add(optGroup);   // Top-docked: added bottom-to-top
            page.Controls.Add(editGroup);
            page.Controls.Add(libsGroup);
            page.Controls.Add(intro);

            var tips = new ToolTip();
            tips.SetToolTip(_libOutput, "This folder is filled with one link per system. Use it as a rule folder on the first tab.");
            tips.SetToolTip(_srcList, "Double-click to edit. Folders that don't exist (e.g. an unplugged drive) are skipped.");
            tips.SetToolTip(_linkType, "Symlinks need Windows Developer Mode or running as administrator. Junctions can't point to network drives.");
        }

        private void RefreshLibList(int select)
        {
            _loadingLib = true;
            _libList.Items.Clear();
            foreach (var l in _libs) _libList.Items.Add(l.ToString());
            if (select >= _libs.Count) select = _libs.Count - 1;
            _libList.SelectedIndex = select;
            _loadingLib = false;
            SelectLib(select);
        }

        private void SelectLib(int i)
        {
            _loadingLib = true;
            _libIdx = i;
            bool has = i >= 0 && i < _libs.Count;
            foreach (var c in _libEditor) c.Enabled = has;
            _libOutput.Text = has ? _libs[i].Output : "";
            _libTag.Text = has ? _libs[i].Tag : "";
            _libEnabled.Checked = has && _libs[i].Enabled;
            _srcList.Items.Clear();
            if (has) foreach (var src in _libs[i].Sources ?? new List<string>()) _srcList.Items.Add(src);
            _loadingLib = false;
        }

        private void CommitLib()
        {
            if (_loadingLib || _libIdx < 0 || _libIdx >= _libs.Count) return;
            var g = _libs[_libIdx];
            g.Output = _libOutput.Text.Trim();
            g.Tag = _libTag.Text.Trim().TrimStart('.');
            g.Enabled = _libEnabled.Checked;
            g.Sources = _srcList.Items.Cast<string>().ToList();
        }

        private void LibEdited()
        {
            if (_loadingLib) return;
            _dirty = true;
            CommitLib();
            if (_libIdx >= 0 && _libIdx < _libList.Items.Count)
            {
                _loadingLib = true;
                _libList.Items[_libIdx] = _libs[_libIdx].ToString();
                _loadingLib = false;
            }
        }

        private void AddLib()
        {
            CommitLib();
            _libs.Add(new LibraryGroup());
            _dirty = true;
            RefreshLibList(_libs.Count - 1);
            _libOutput.Focus();
        }

        private void RemoveLib()
        {
            if (_libIdx < 0) return;
            if (MessageBox.Show(this, "Remove this library from the list?\n\n" + _libs[_libIdx] +
                    "\n\nNothing on disk is deleted.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _libs.RemoveAt(_libIdx);
            _dirty = true;
            RefreshLibList(Math.Min(_libIdx, _libs.Count - 1));
        }

        private void MoveLib(int d)
        {
            CommitLib();
            int i = _libIdx, j = i + d;
            if (i < 0 || j < 0 || j >= _libs.Count) return;
            var t = _libs[i]; _libs[i] = _libs[j]; _libs[j] = t;
            _dirty = true;
            RefreshLibList(j);
        }

        private void BrowseLibOutput()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Select (or create) the folder this library is built in", ShowNewFolderButton = true })
            {
                if (Directory.Exists(_libOutput.Text)) dlg.SelectedPath = _libOutput.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) { _libOutput.Text = dlg.SelectedPath; RefreshFolderLists(); }
            }
        }

        private void AddSourceFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Select a source folder that contains system folders (nes, snes, ...)" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AddSource(dlg.SelectedPath);
            }
        }

        private void AddSourcePath()
        {
            string p = Prompt("Add source", "Folder path (can be a drive that isn't connected right now, e.g. X:\\roms):", "");
            if (!string.IsNullOrWhiteSpace(p)) AddSource(p.Trim());
        }

        private void AddSource(string path)
        {
            if (_srcList.Items.Cast<string>().Any(x => x.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            _srcList.Items.Add(path);
            _srcList.SelectedIndex = _srcList.Items.Count - 1;
            LibEdited();
        }

        private void EditSource()
        {
            int i = _srcList.SelectedIndex;
            if (i < 0) return;
            string p = Prompt("Edit source", "Folder path:", (string)_srcList.Items[i]);
            if (string.IsNullOrWhiteSpace(p)) return;
            _srcList.Items[i] = p.Trim();
            LibEdited();
        }

        private void RemoveSource()
        {
            int i = _srcList.SelectedIndex;
            if (i < 0) return;
            _srcList.Items.RemoveAt(i);
            if (_srcList.Items.Count > 0) _srcList.SelectedIndex = Math.Min(i, _srcList.Items.Count - 1);
            LibEdited();
        }

        private void MoveSource(int d)
        {
            int i = _srcList.SelectedIndex, j = i + d;
            if (i < 0 || j < 0 || j >= _srcList.Items.Count) return;
            object t = _srcList.Items[i];
            _srcList.Items.RemoveAt(i);
            _srcList.Items.Insert(j, t);
            _srcList.SelectedIndex = j;
            LibEdited();
        }

        private void RunBuild(bool all, bool rebuild, bool dryRun)
        {
            var c = FromUi();
            if (!all && (_libIdx < 0 || _libIdx >= c.Libraries.Count)) { Out("Select a library first."); return; }

            if (rebuild)
            {
                if (Switcher.IsRetroBatRunning()) { Out("Close RetroBat before rebuilding libraries."); return; }
                if (MessageBox.Show(this,
                        "Rebuild removes every link in each enabled library's output folder and creates them again " +
                        "from the sources, in the current order.\n\nReal folders and files are never touched.\n\nContinue?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            Out("---- " + (dryRun ? "preview" : rebuild ? "rebuild" : "build") + " ----");
            var log = dryRun ? (Action<string>)Out : s => { Out(s); Program.Log(s); };
            var b = new LibraryBuilder(log);
            UseWaitCursor = true;
            try
            {
                if (all) b.BuildAll(c, rebuild, dryRun);
                else
                {
                    b.Build(c, c.Libraries[_libIdx], rebuild, dryRun);
                }
            }
            catch (Exception ex) { Out("ERROR: " + ex.Message); }
            finally { UseWaitCursor = false; }

            if (b.Failed > 0 && !dryRun)
                Out("Some links failed. For network sources or Link type 'Symlink', turn on Developer Mode " +
                    "(Settings > System > For developers) or run this as administrator.");
            RefreshFolderLists();
        }

        private static Control ListWithButtons(ListBox list, params Button[] buttons)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var col = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
            for (int i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                b.AutoSize = false; b.Width = 92; b.Height = 26; b.Margin = new Padding(2);
                col.Controls.Add(b, i % 2, i / 2);
            }
            list.Height = Math.Max(72, ((buttons.Length + 1) / 2) * 30 - 4);
            t.Controls.Add(list, 0, 0);
            t.Controls.Add(col, 1, 0);
            return t;
        }

        private string Prompt(string title, string text, string value)
        {
            using (var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font, ClientSize = new Size(480, 110)
            })
            {
                var lbl = new Label { Text = text, Left = 10, Top = 10, Width = 460, AutoSize = false, Height = 20 };
                var tb = new TextBox { Text = value, Left = 10, Top = 34, Width = 460 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 314, Top = 72, Width = 75 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 395, Top = 72, Width = 75 };
                f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        // ---------- Helpers ----------

        private void RefreshFolderLists()
        {
            if (_loadingLib) return;
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
            foreach (var l in _libs)
                if (!string.IsNullOrWhiteSpace(l.Output) && !names.Contains(l.Output.Trim(), StringComparer.OrdinalIgnoreCase))
                    names.Add(l.Output.Trim());
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
            _output.Update();
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
