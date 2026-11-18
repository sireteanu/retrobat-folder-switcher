using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RetroBatFolderSwitcher
{
    // "Libraries" and "Media links" tabs.
    internal partial class SettingsForm
    {
        // ---- Libraries ----
        private List<LibraryGroup> _libs = new List<LibraryGroup>();
        private int _libIdx = -1;
        private bool _loadingLib;
        private readonly ListBox _libList = new ListBox { IntegralHeight = false };
        private readonly TextBox _libOutput = new TextBox();
        private readonly CheckBox _libEnabled = new CheckBox { Text = "Include when building", AutoSize = true, Margin = new Padding(3, 4, 3, 6) };
        private readonly ListBox _srcList = new ListBox { IntegralHeight = false, HorizontalScrollbar = true };
        private readonly ComboBox _linkType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private Control _libEditor;

        // ---- Media links ----
        private readonly TextBox _mediaFolder = new TextBox();
        private readonly TextBox _mediaName = new TextBox { Width = 110 };
        private readonly ListBox _mediaRoms = new ListBox { IntegralHeight = false, HorizontalScrollbar = true };
        private readonly Label _mediaExample = new Label { AutoSize = true, Margin = new Padding(3, 7, 3, 3), Font = new Font("Consolas", 9f) };

        // =====================================================================
        // Libraries tab
        // =====================================================================

        private Control BuildLibrariesTab()
        {
            var intro = Hint("A library is a folder of links to system folders (nes, snes, ...) found on other drives. " +
                             "Use a library as a roms folder on the Launch tab.");

            // Left: the list of libraries
            var left = StackWithFill(new Control[]
            {
                _libList,
                ButtonRow(Small(Btn("Add", AddLib)), Small(Btn("Remove", RemoveLib)), Small(Btn("Up", () => MoveLib(-1))), Small(Btn("Down", () => MoveLib(1))))
            }, new HashSet<Control> { _libList });

            // Right: the selected library
            var top = Grid();
            AddRow(top, Lbl("Folder"), _libOutput, Btn("Browse...", BrowseLibOutput));
            AddRow(top, new Label(), _libEnabled);
            var sourcesTitle = new Label { Text = "Sources  (checked in order; the first one that has a system wins)", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            var right = StackWithFill(new Control[]
            {
                top,
                sourcesTitle,
                _srcList,
                ButtonRow(Btn("Add folder...", AddSourceFolder), Btn("Type path...", AddSourcePath), Small(Btn("Edit...", EditSource)),
                          Small(Btn("Remove", RemoveSource)), Small(Btn("Up", () => MoveSource(-1))), Small(Btn("Down", () => MoveSource(1))))
            }, new HashSet<Control> { _srcList });
            _libEditor = right;

            var split = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            split.Controls.Add(FillGroup("Libraries", left), 0, 0);
            split.Controls.Add(FillGroup("Selected library", right), 1, 0);

            _linkType.Items.AddRange(new object[] { "Auto", "Symlink", "Junction" });
            var actions = ButtonRow(
                Busy(Btn("Preview", () => RunBuild(rebuild: false, dryRun: true))),
                Busy(Btn("Build", () => RunBuild(rebuild: false, dryRun: false), bold: true)),
                Busy(Btn("Rebuild...", () => RunBuild(rebuild: true, dryRun: false))),
                new Label { Text = "Link type", AutoSize = true, Margin = new Padding(24, 9, 6, 3) },
                _linkType);

            _libList.SelectedIndexChanged += (s, e) => { if (_loadingLib) return; CommitLib(); SelectLib(_libList.SelectedIndex); };
            _libOutput.TextChanged += (s, e) => LibEdited();
            _libEnabled.CheckedChanged += (s, e) => LibEdited();
            _libOutput.Leave += (s, e) => RefreshFolderLists();
            _srcList.DoubleClick += (s, e) => EditSource();

            _tips.SetToolTip(_libOutput, "This folder is filled with one link per system.");
            _tips.SetToolTip(_srcList, "Double-click to edit. Sources that don't exist right now (an unplugged drive) are skipped.");
            _tips.SetToolTip(_linkType, "Auto: symlink if Windows allows it, otherwise a junction.\n" +
                                        "Symlink: needs Developer Mode or admin; required for network drives.\n" +
                                        "Junction: no admin needed; local drives only.");
            _tips.SetToolTip(actions.Controls[0], "Show what Build would do, without changing anything.");
            _tips.SetToolTip(actions.Controls[1], "Add links for systems that aren't linked yet, in all included libraries.");
            _tips.SetToolTip(actions.Controls[2], "Remove all links in the included libraries and create them again (after changing the source order).");

            var root = StackWithFill(new Control[] { intro, split, actions }, new HashSet<Control> { split });
            return root;
        }

        private void LoadLinks(Config c)
        {
            _libs = (c.Libraries ?? new List<LibraryGroup>()).Select(l => l.Clone()).ToList();
            _linkType.SelectedItem = new[] { "Auto", "Symlink", "Junction" }
                .FirstOrDefault(t => t.Equals(c.LinkType ?? "", StringComparison.OrdinalIgnoreCase)) ?? "Auto";
            RefreshLibList(_libs.Count > 0 ? 0 : -1);

            _mediaFolder.Text = c.MediaFolder ?? "";
            _mediaName.Text = c.MediaLinkName ?? "media";
            _mediaRoms.Items.Clear();
            foreach (var r in c.MediaRomFolders ?? new List<string>()) _mediaRoms.Items.Add(r);
            UpdateMediaExample();
        }

        private void ApplyLinks(Config c)
        {
            c.Libraries = _libs.Select(l => l.Clone()).ToList();
            c.LinkType = (_linkType.SelectedItem as string) ?? "Auto";
            c.MediaFolder = _mediaFolder.Text.Trim();
            c.MediaLinkName = Or(_mediaName.Text, "media");
            c.MediaRomFolders = _mediaRoms.Items.Cast<string>().ToList();
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
            if (_libEditor != null) _libEditor.Enabled = has;
            _libOutput.Text = has ? _libs[i].Output : "";
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
            g.Enabled = _libEnabled.Checked;
            g.Sources = _srcList.Items.Cast<string>().ToList();
        }

        private void LibEdited()
        {
            if (_loadingLib) return;
            if (!_loading) _dirty = true;
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
                if (dlg.ShowDialog(this) == DialogResult.OK) AddSource(dlg.SelectedPath);
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

        private void RunBuild(bool rebuild, bool dryRun)
        {
            var c = FromUi();
            if (rebuild)
            {
                if (Switcher.IsRetroBatRunning()) { Out("Close RetroBat before rebuilding libraries."); return; }
                if (MessageBox.Show(this,
                        "Rebuild removes every link in the included libraries and creates them again from the sources, " +
                        "in the current order.\n\nReal folders and files are never touched.\n\nContinue?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            RunTask(dryRun ? "Previewing libraries..." : rebuild ? "Rebuilding libraries..." : "Building libraries...",
                logToFile: !dryRun, work: (log, stop) =>
            {
                log("---- libraries " + (dryRun ? "preview" : rebuild ? "rebuild" : "build") + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                var b = new LibraryBuilder(log) { Stop = stop };
                b.BuildLibraries(c, rebuild, dryRun);
                if (b.Failed > 0 && !dryRun)
                    log("Some links failed. For network sources or Link type 'Symlink', turn on Developer Mode " +
                        "(Settings > System > For developers) or run this as administrator.");
                return stop() ? "Stopped." : b.Failed > 0 ? b.Failed + " link(s) failed - see the log."
                     : (dryRun ? "Preview: " + b.Created + " link(s) would be created." : "Libraries built: " + b.Created + " new link(s).");
            }, after: RefreshFolderLists);
        }

        // =====================================================================
        // Media links tab
        // =====================================================================

        private Control BuildMediaTab()
        {
            var intro = Hint("For rom folders copied without media (robocopy ... /XD media): adds a 'media' link in every system folder, " +
                             "pointing at that system's media.");

            var top = Grid();
            AddRow(top, Lbl("Media folder"), _mediaFolder, Btn("Browse...", BrowseMediaFolder));
            AddRow(top, Lbl("Link name"), _mediaName);
            AddRow(top, Lbl("Example"), _mediaExample);
            var romsTitle = new Label { Text = "Rom folders  (every system folder inside gets a media link)", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
            var group = StackWithFill(new Control[]
            {
                top,
                romsTitle,
                _mediaRoms,
                ButtonRow(Btn("Add folder...", AddMediaRomFolder), Btn("Type path...", AddMediaRomPath), Btn("Remove", RemoveMediaRom))
            }, new HashSet<Control> { _mediaRoms });

            var actions = ButtonRow(
                Busy(Btn("Preview", () => RunMedia(rebuild: false, dryRun: true))),
                Busy(Btn("Create links", () => RunMedia(rebuild: false, dryRun: false), bold: true)),
                Busy(Btn("Recreate...", () => RunMedia(rebuild: true, dryRun: false))));

            _mediaFolder.TextChanged += (s, e) => UpdateMediaExample();
            _mediaName.TextChanged += (s, e) => UpdateMediaExample();
            _tips.SetToolTip(_mediaFolder, "One folder per system, each with a media folder inside (e.g. where MEDIA_FULL is synced to).");
            _tips.SetToolTip(_mediaName, "Name of the link, and of the folder inside each system's media folder.");
            _tips.SetToolTip(actions.Controls[1], "Add missing media links. Systems without media and existing real media folders are skipped.");
            _tips.SetToolTip(actions.Controls[2], "Remove the media links and create them again. Real folders are never touched.");

            var box = FillGroup("Media links", group);
            return StackWithFill(new Control[] { intro, box, actions }, new HashSet<Control> { box });
        }

        private void UpdateMediaExample()
        {
            string name = Or(_mediaName.Text, "media");
            string rom = _mediaRoms.Items.Count > 0 ? ((string)_mediaRoms.Items[0]).TrimEnd('\\') : "<rom folder>";
            string media = string.IsNullOrWhiteSpace(_mediaFolder.Text) ? "<media folder>" : _mediaFolder.Text.Trim().TrimEnd('\\');
            _mediaExample.Text = rom + "\\nes\\" + name + "  ->  " + media + "\\nes\\" + name;
        }

        private void BrowseMediaFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Select the media folder (one folder per system, each with a media folder inside)" })
            {
                if (Directory.Exists(_mediaFolder.Text)) dlg.SelectedPath = _mediaFolder.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) _mediaFolder.Text = dlg.SelectedPath;
            }
        }

        private void AddMediaRomFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Select a rom folder (contains system folders such as nes, snes, ...)" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK) AddMediaRom(dlg.SelectedPath);
            }
        }

        private void AddMediaRomPath()
        {
            string p = Prompt("Add rom folder", "Rom folder path (e.g. E:\\Roms):", "");
            if (!string.IsNullOrWhiteSpace(p)) AddMediaRom(p.Trim());
        }

        private void AddMediaRom(string path)
        {
            if (_mediaRoms.Items.Cast<string>().Any(x => x.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            _mediaRoms.Items.Add(path);
            _dirty = true;
            UpdateMediaExample();
        }

        private void RemoveMediaRom()
        {
            int i = _mediaRoms.SelectedIndex;
            if (i < 0) return;
            _mediaRoms.Items.RemoveAt(i);
            _dirty = true;
            UpdateMediaExample();
        }

        private void RunMedia(bool rebuild, bool dryRun)
        {
            var c = FromUi();
            if (rebuild)
            {
                if (Switcher.IsRetroBatRunning()) { Out("Close RetroBat before recreating media links."); return; }
                if (MessageBox.Show(this,
                        "This removes the media links in the rom folders and creates them again.\n\n" +
                        "Real media folders and files are never touched.\n\nContinue?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            RunTask(dryRun ? "Previewing media links..." : "Creating media links...", logToFile: !dryRun, work: (log, stop) =>
            {
                log("---- media links " + (dryRun ? "preview" : rebuild ? "recreate" : "create") + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                var b = new LibraryBuilder(log) { Stop = stop };
                b.BuildMedia(c, rebuild, dryRun);
                return stop() ? "Stopped." : b.Failed > 0 ? b.Failed + " media link(s) failed - see the log."
                     : (dryRun ? "Preview: " + b.Created + " media link(s) would be created." : "Media links: " + b.Created + " new link(s).");
            });
        }
    }
}
