using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RetroBatFolderSwitcher
{
    // The "Sync" tab: individually runnable robocopy mirror jobs.
    internal partial class SettingsForm
    {
        private List<SyncJob> _romJobs = new List<SyncJob>();
        private List<SyncJob> _mediaJobs = new List<SyncJob>();
        private readonly ListView _romView = JobView(media: false);
        private readonly ListView _mediaView = JobView(media: true);
        private readonly TextBox _roboOpts = new TextBox();

        // Background tasks (robocopy, library and media builds) share one runner, status line and Stop button.
        private readonly List<Button> _busyButtons = new List<Button>();
        private Button _stopBtn;
        private readonly Label _status = new Label { Dock = DockStyle.Bottom, Height = 22, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Ready." };
        private volatile bool _busy, _stopRequested;
        private readonly ConcurrentQueue<string> _taskLines = new ConcurrentQueue<string>();
        private System.Windows.Forms.Timer _flushTimer;
        private StreamWriter _taskLog;
        private readonly object _taskLogLock = new object();

        private void BuildSyncPage(TabPage page)
        {
            var intro = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 38,
                Text = "Mirror folders with robocopy /MIR. Select one or more jobs and run just those. " +
                       "Output files that aren't in the input are deleted, except in excluded folders. Use Preview first."
            };

            // Rom sync
            var romGrid = Grid();
            var romList = ListWithButtons(_romView,
                Btn("Add...", () => AddJob(false)), Btn("Edit...", () => EditSelected(false)), Btn("Remove", () => RemoveJob(false)),
                Btn("Up", () => MoveJob(false, -1)), Btn("Down", () => MoveJob(false, 1)));
            romGrid.Controls.Add(romList, 0, romGrid.RowCount);
            romGrid.SetColumnSpan(romList, 3);
            romGrid.RowCount++;
            romGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var romActions = ActionRow(false);
            romGrid.Controls.Add(romActions, 0, romGrid.RowCount);
            romGrid.SetColumnSpan(romActions, 3);
            romGrid.RowCount++;
            romGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var romGroup = Group("Rom sync   (robocopy IN OUT /MIR /XD media)", romGrid);

            // Media sync
            var medGrid = Grid();
            var medList = ListWithButtons(_mediaView,
                Btn("Add...", () => AddJob(true)), Btn("Edit...", () => EditSelected(true)), Btn("Remove", () => RemoveJob(true)),
                Btn("Up", () => MoveJob(true, -1)), Btn("Down", () => MoveJob(true, 1)));
            medGrid.Controls.Add(medList, 0, medGrid.RowCount);
            medGrid.SetColumnSpan(medList, 3);
            medGrid.RowCount++;
            medGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var medActions = ActionRow(true);
            medGrid.Controls.Add(medActions, 0, medGrid.RowCount);
            medGrid.SetColumnSpan(medActions, 3);
            medGrid.RowCount++;
            medGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var medGroup = Group("Media sync   (robocopy IN OUT /MIR /XD <excluded folders>)", medGrid);

            // Options
            var opt = Grid();
            AddRow(opt, Lbl("Extra robocopy options"), _roboOpts);
            var optGroup = Group("Options", opt);

            page.Controls.Add(optGroup);   // Top-docked: added bottom-to-top
            page.Controls.Add(medGroup);
            page.Controls.Add(romGroup);
            page.Controls.Add(intro);

            _romView.DoubleClick += (s, e) => EditSelected(false);
            _mediaView.DoubleClick += (s, e) => EditSelected(true);
            _roboOpts.TextChanged += (s, e) => _dirty = true;

            var tips = new ToolTip();
            tips.SetToolTip(_roboOpts, "Added to every sync. /R:2 /W:5 = retry failed files twice, 5 s apart (robocopy's default waits for ages). /NP = no % progress in the log.");
            tips.SetToolTip(_romView, "Double-click to edit. Ctrl/Shift-click to select several.");
            tips.SetToolTip(_mediaView, "Double-click to edit. Ctrl/Shift-click to select several.");

            _flushTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _flushTimer.Tick += (s, e) => FlushTaskLines();
            _flushTimer.Start();

            FormClosing += (s, e) =>
            {
                if (e.Cancel || !_busy) return;
                if (MessageBox.Show(this, "A task is still running. Stop it and close?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
                _stopRequested = true;
                for (int i = 0; i < 50 && _busy; i++) { Thread.Sleep(100); Application.DoEvents(); }
            };
        }

        private Control ActionRow(bool media)
        {
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0), WrapContents = true };
            var a = Btn("Preview selected", () => RunJobs(media, selectedOnly: true, preview: true));
            var b = Btn("Run selected", () => RunJobs(media, selectedOnly: true, preview: false), bold: true);
            var c = Btn("Run all...", () => RunJobs(media, selectedOnly: false, preview: false));
            row.Controls.AddRange(new Control[] { a, b, c });
            _busyButtons.AddRange(new[] { a, b, c });
            return row;
        }

        private static ListView JobView(bool media)
        {
            float k;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = Math.Max(1f, g.DpiX / 96f);
            var v = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false,
                Dock = DockStyle.Fill, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            v.Columns.Add("Input folder", (int)((media ? 230 : 300) * k));
            v.Columns.Add("Output folder", (int)((media ? 190 : 250) * k));
            if (media) v.Columns.Add("Excluded folders", (int)(200 * k));
            return v;
        }

        // ---------- config <-> UI ----------

        private void LoadSync(Config c)
        {
            _romJobs = (c.RomSyncs ?? new List<SyncJob>()).Select(j => j.Clone()).ToList();
            _mediaJobs = (c.MediaSyncs ?? new List<SyncJob>()).Select(j => j.Clone()).ToList();
            _roboOpts.Text = c.RobocopyOptions ?? "";
            RefreshJobs(false, 0);
            RefreshJobs(true, 0);
        }

        private void ApplySync(Config c)
        {
            c.RomSyncs = _romJobs.Select(j => j.Clone()).ToList();
            c.MediaSyncs = _mediaJobs.Select(j => j.Clone()).ToList();
            c.RobocopyOptions = _roboOpts.Text.Trim();
        }

        private List<SyncJob> Jobs(bool media) => media ? _mediaJobs : _romJobs;
        private ListView JobList(bool media) => media ? _mediaView : _romView;

        private void RefreshJobs(bool media, int select)
        {
            var v = JobList(media);
            v.BeginUpdate();
            v.Items.Clear();
            foreach (var j in Jobs(media))
            {
                var it = new ListViewItem(j.Source ?? "");
                it.SubItems.Add(j.Destination ?? "");
                if (media) it.SubItems.Add(JoinExcludes(j.Exclude));
                v.Items.Add(it);
            }
            if (select >= 0 && select < v.Items.Count) { v.Items[select].Selected = true; v.Items[select].Focused = true; }
            // Fit columns to their contents, but never narrower than the header.
            for (int c = 0; c < v.Columns.Count; c++)
            {
                v.AutoResizeColumn(c, ColumnHeaderAutoResizeStyle.HeaderSize);
                int header = v.Columns[c].Width;
                if (v.Items.Count > 0)
                {
                    v.AutoResizeColumn(c, ColumnHeaderAutoResizeStyle.ColumnContent);
                    if (v.Columns[c].Width < header) v.Columns[c].Width = header;
                }
            }
            v.EndUpdate();
        }

        private int FirstSelected(bool media) => JobList(media).SelectedIndices.Count > 0 ? JobList(media).SelectedIndices[0] : -1;

        private void AddJob(bool media)
        {
            var j = new SyncJob();
            if (!EditJobDialog(j, media, "Add " + (media ? "media" : "rom") + " sync")) return;
            Jobs(media).Add(j);
            _dirty = true;
            RefreshJobs(media, Jobs(media).Count - 1);
        }

        private void EditSelected(bool media)
        {
            int i = FirstSelected(media);
            if (i < 0) return;
            var j = Jobs(media)[i].Clone();
            if (!EditJobDialog(j, media, "Edit " + (media ? "media" : "rom") + " sync")) return;
            Jobs(media)[i] = j;
            _dirty = true;
            RefreshJobs(media, i);
        }

        private void RemoveJob(bool media)
        {
            int i = FirstSelected(media);
            if (i < 0) return;
            Jobs(media).RemoveAt(i);
            _dirty = true;
            RefreshJobs(media, Math.Min(i, Jobs(media).Count - 1));
        }

        private void MoveJob(bool media, int d)
        {
            int i = FirstSelected(media), j = i + d;
            var list = Jobs(media);
            if (i < 0 || j < 0 || j >= list.Count) return;
            var t = list[i]; list[i] = list[j]; list[j] = t;
            _dirty = true;
            RefreshJobs(media, j);
        }

        private bool EditJobDialog(SyncJob job, bool media, string title)
        {
            using (var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font,
                ClientSize = new Size(560, media ? 210 : 150)
            })
            {
                f.SuspendLayout();
                var src = new TextBox { Text = job.Source, Left = 120, Top = 14, Width = 330 };
                var dst = new TextBox { Text = job.Destination, Left = 120, Top = 48, Width = 330 };
                var ex = new TextBox { Text = JoinExcludes(job.Exclude), Left = 120, Top = 82, Width = 420, Visible = media };
                var exHint = new Label
                {
                    Text = "Folder names separated by spaces or commas. Quote names with spaces: \"my folder\"", Left = 120, Top = 108,
                    AutoSize = true, ForeColor = SystemColors.GrayText, Visible = media
                };
                Func<TextBox, Button> browse = tb =>
                {
                    var b = new Button { Text = "Browse...", Left = 458, Top = tb.Top - 2, Width = 82, Height = 26 };
                    b.Click += (s, e) =>
                    {
                        using (var d = new FolderBrowserDialog { ShowNewFolderButton = true })
                        {
                            if (Directory.Exists(tb.Text)) d.SelectedPath = tb.Text;
                            if (d.ShowDialog(f) == DialogResult.OK) tb.Text = d.SelectedPath;
                        }
                    };
                    return b;
                };
                var note = new Label
                {
                    Text = media ? "" : "media folders are always excluded.",
                    Left = 120, Top = 80, AutoSize = true, ForeColor = SystemColors.GrayText, Visible = !media
                };
                int by = media ? 166 : 110;
                var ok = new Button { Text = "OK", Left = 384, Top = by, Width = 75, Height = 28 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 465, Top = by, Width = 75, Height = 28 };
                ok.Click += (s, e) =>
                {
                    if (string.IsNullOrWhiteSpace(src.Text) || string.IsNullOrWhiteSpace(dst.Text))
                    {
                        MessageBox.Show(f, "Enter both an input and an output folder.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    f.DialogResult = DialogResult.OK;
                };
                f.Controls.AddRange(new Control[]
                {
                    new Label { Text = "Input folder", Left = 12, Top = 17, AutoSize = true }, src, browse(src),
                    new Label { Text = "Output folder", Left = 12, Top = 51, AutoSize = true }, dst, browse(dst),
                    new Label { Text = "Excluded folders", Left = 12, Top = 85, AutoSize = true, Visible = media }, ex, exHint,
                    note, ok, cancel
                });
                f.AcceptButton = ok; f.CancelButton = cancel;
                ScaleDialog(f);
                if (f.ShowDialog(this) != DialogResult.OK) return false;

                job.Source = src.Text.Trim().Trim('"');
                job.Destination = dst.Text.Trim().Trim('"');
                job.Exclude = media ? Robocopy.ParseExcludes(ex.Text) : new List<string>();
                return true;
            }
        }

        private static string JoinExcludes(List<string> ex) =>
            string.Join(" ", (ex ?? new List<string>()).Select(x => x.IndexOf(' ') >= 0 ? "\"" + x + "\"" : x));

        // ---------- running ----------

        private void RunJobs(bool media, bool selectedOnly, bool preview)
        {
            if (_busy) { Out("Busy - wait for the current task or press Stop."); return; }

            var list = Jobs(media);
            var idx = selectedOnly
                ? JobList(media).SelectedIndices.Cast<int>().OrderBy(i => i).ToList()
                : Enumerable.Range(0, list.Count).ToList();
            if (idx.Count == 0) { Out(selectedOnly ? "Select a job first (Ctrl/Shift-click for several)." : "No jobs."); return; }

            string opts = _roboOpts.Text.Trim();
            var runs = new List<Tuple<string, string>>(); // (label, args)
            var problems = new List<string>();
            foreach (int i in idx)
            {
                var j = list[i];
                string label = (media ? "Media" : "Rom") + " sync " + (i + 1) + ": " + j.Source + "  ->  " + j.Destination;
                string why = Robocopy.Check(j);
                if (why != null) { problems.Add(label + "\n    " + why); continue; }
                runs.Add(Tuple.Create(label, Robocopy.BuildArgs(j, romSync: !media, options: opts, preview: preview)));
            }

            if (problems.Count > 0)
            {
                string msg = "Can't run:\n\n" + string.Join("\n\n", problems);
                foreach (var p in problems) Out("Skipped - " + p.Replace("\n    ", ": "));
                if (runs.Count == 0) { MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                if (MessageBox.Show(this, msg + "\n\nRun the other " + runs.Count + " job(s)?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            }

            if (!preview)
            {
                var sb = new StringBuilder("Mirror these folders?\n\n");
                foreach (var r in runs) sb.Append("robocopy ").Append(r.Item2).Append("\n\n");
                sb.Append("Files in the output folder that aren't in the input will be DELETED (excluded folders are kept).");
                if (MessageBox.Show(this, sb.ToString(), Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            }

            RunTask("Syncing...", logToFile: !preview, work: (log, stop) =>
            {
                int failed = 0;
                log("==== " + (preview ? "PREVIEW (nothing is changed)" : "SYNC") + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                for (int n = 0; n < runs.Count; n++)
                {
                    var r = runs[n];
                    SetStatusAsync("Running " + (n + 1) + " of " + runs.Count + ": " + r.Item1);
                    log("");
                    log("==== " + r.Item1);
                    log("==== robocopy " + r.Item2);
                    int code;
                    try { code = Robocopy.Run(r.Item2, log, stop); }
                    catch (Exception ex) { log("ERROR starting robocopy: " + ex.Message); code = 16; }
                    log("==== exit code " + code + ": " + Robocopy.DescribeExit(code));
                    if (code >= 8) failed++;
                    if (code < 0) { log("==== stopped - remaining jobs not run."); break; }
                }
                log("==== done" + (failed > 0 ? " - " + failed + " job(s) failed" : ""));
                return stop() ? "Stopped." : failed > 0 ? failed + " sync job(s) failed - see the log."
                     : (preview ? "Preview finished." : "Sync finished.");
            });
        }

        // ---------- shared background task runner ----------

        /// <summary>
        /// Runs work on a background thread. work(log, stopRequested) returns the final status text.
        /// Output goes to the log box (batched) and, when logToFile, to the log file.
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
                _output.Text = "(earlier output trimmed - see Show log)\r\n" + _output.Text.Substring(_output.TextLength - 500000);
            Out(sb.ToString());
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (var b in _busyButtons) b.Enabled = !busy;
            if (_stopBtn != null) _stopBtn.Enabled = busy;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }
    }
}
