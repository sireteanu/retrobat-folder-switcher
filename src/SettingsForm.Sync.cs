using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace RetroBatFolderSwitcher
{
    // "Sync" tab: robocopy mirror jobs, run one or several at a time.
    internal partial class SettingsForm
    {
        private List<SyncJob> _romJobs = new List<SyncJob>();
        private List<SyncJob> _mediaJobs = new List<SyncJob>();
        private readonly ListView _romView = JobView(media: false);
        private readonly ListView _mediaView = JobView(media: true);
        private readonly TextBox _roboOpts = new TextBox();

        private Control BuildSyncTab()
        {
            var intro = Hint("Mirror folders with robocopy. Select one or more jobs and run only those. " +
                             "Files in the output that aren't in the input are deleted.");

            var romBox = FillGroup("Rom sync   -   robocopy IN OUT /MIR /XD media", JobPanel(false));
            var mediaBox = FillGroup("Media sync   -   robocopy IN OUT /MIR /XD <excluded folders>", JobPanel(true));

            var opts = Grid();
            AddRow(opts, Lbl("Extra robocopy options"), _roboOpts);

            _romView.DoubleClick += (s, e) => EditSelected(false);
            _mediaView.DoubleClick += (s, e) => EditSelected(true);
            foreach (var v in new[] { _romView, _mediaView })
            {
                var view = v;
                view.KeyDown += (s, e) =>
                {
                    if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem it in view.Items) it.Selected = true; e.SuppressKeyPress = true; }
                };
                _tips.SetToolTip(view, "Ctrl/Shift-click to select several jobs, Ctrl+A for all. Double-click to edit.");
            }
            _tips.SetToolTip(_roboOpts, "Added to every job. /R:2 /W:5 = retry a failed file twice, 5 s apart " +
                                        "(robocopy's default can wait for days). /NP = no % progress in the log.");

            return StackWithFill(new Control[] { intro, romBox, mediaBox, opts }, new HashSet<Control> { romBox, mediaBox });
        }

        private Control JobPanel(bool media)
        {
            var view = JobList(media);
            var preview = Busy(Btn("Preview", () => RunJobs(media, preview: true)));
            var run = Busy(Btn("Run", () => RunJobs(media, preview: false), bold: true));
            var row = ButtonRow(
                Btn("Add...", () => AddJob(media)), Btn("Edit...", () => EditSelected(media)), Btn("Remove", () => RemoveJob(media)),
                new Label { Width = 24 },
                preview, run);
            _tips.SetToolTip(preview, "Runs robocopy with /L: lists what would be copied and deleted, changes nothing.");
            _tips.SetToolTip(run, "Mirrors the selected job(s). You'll be asked to confirm first.");
            return StackWithFill(new Control[] { view, row }, new HashSet<Control> { view });
        }

        private static ListView JobView(bool media)
        {
            var v = new ListView
            {
                View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false,
                GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            v.Columns.Add("Input folder", 260);
            v.Columns.Add("Output folder", 200);
            if (media) v.Columns.Add("Excluded folders", 200);
            return v;
        }

        // ---------- config <-> UI ----------

        private void LoadSync(Config c)
        {
            _romJobs = (c.RomSyncs ?? new List<SyncJob>()).Select(j => j.Clone()).ToList();
            _mediaJobs = (c.MediaSyncs ?? new List<SyncJob>()).Select(j => j.Clone()).ToList();
            _roboOpts.Text = c.RobocopyOptions ?? "";
            RefreshJobs(false, -1);
            RefreshJobs(true, -1);
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
            if (!EditJobDialog(j, media, media ? "Add media sync" : "Add rom sync")) return;
            Jobs(media).Add(j);
            _dirty = true;
            RefreshJobs(media, Jobs(media).Count - 1);
        }

        private void EditSelected(bool media)
        {
            int i = FirstSelected(media);
            if (i < 0) return;
            var j = Jobs(media)[i].Clone();
            if (!EditJobDialog(j, media, media ? "Edit media sync" : "Edit rom sync")) return;
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

        private bool EditJobDialog(SyncJob job, bool media, string title)
        {
            using (var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font,
                ClientSize = new Size(560, media ? 200 : 150)
            })
            {
                f.SuspendLayout();
                var src = new TextBox { Text = job.Source, Left = 120, Top = 14, Width = 330 };
                var dst = new TextBox { Text = job.Destination, Left = 120, Top = 48, Width = 330 };
                var ex = new TextBox { Text = JoinExcludes(job.Exclude), Left = 120, Top = 82, Width = 420, Visible = media };
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
                    Text = media ? "Folder names, separated by spaces or commas. Quote names with spaces: \"my folder\""
                                 : "media folders are always excluded.",
                    Left = 120, Top = media ? 108 : 80, AutoSize = true, ForeColor = SystemColors.GrayText
                };
                int by = media ? 156 : 108;
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
                    new Label { Text = "Excluded folders", Left = 12, Top = 85, AutoSize = true, Visible = media }, ex,
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

        private void RunJobs(bool media, bool preview)
        {
            if (_busy) { Out("Busy - wait for the current task or press Stop."); return; }

            var list = Jobs(media);
            var idx = JobList(media).SelectedIndices.Cast<int>().OrderBy(i => i).ToList();
            if (idx.Count == 0) { Out("Select a job first (Ctrl/Shift-click for several, Ctrl+A for all)."); return; }

            string opts = _roboOpts.Text.Trim();
            var runs = new List<Tuple<string, string>>(); // (label, args)
            var problems = new List<string>();
            foreach (int i in idx)
            {
                var j = list[i];
                string label = (media ? "Media" : "Rom") + " sync: " + j.Source + "  ->  " + j.Destination;
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

            RunTask(preview ? "Previewing sync..." : "Syncing...", logToFile: !preview, work: (log, stop) =>
            {
                int failed = 0;
                log("==== " + (preview ? "SYNC PREVIEW (nothing is changed)" : "SYNC") + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                for (int n = 0; n < runs.Count; n++)
                {
                    var r = runs[n];
                    SetStatusAsync((preview ? "Previewing " : "Syncing ") + (n + 1) + " of " + runs.Count + ": " + r.Item1);
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
                     : (preview ? "Preview finished - nothing was changed." : "Sync finished.");
            });
        }
    }
}
