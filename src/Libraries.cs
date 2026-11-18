using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RetroBatFolderSwitcher
{
    /// <summary>
    /// Builds each library's output folder out of links to the system folders found in its sources
    /// (same behaviour as create_symlinks_win_multiple.bat):
    ///  - sources are checked in order; the first source that has a system folder wins
    ///  - system folders starting with SkipPrefix (e.g. _media) are skipped
    ///  - anything already in the output folder is left alone
    /// Only links are ever removed (with rebuild), never real folders or files.
    /// </summary>
    internal class LibraryBuilder
    {
        private readonly Action<string> _log;
        public int Created, Skipped, Failed, Removed, NoMedia;

        /// <summary>Checked between items; return true to stop early.</summary>
        public Func<bool> Stop = () => false;

        public LibraryBuilder(Action<string> log) { _log = log ?? (_ => { }); }

        /// <summary>Libraries, then media links (if turned on). Used on launch and from the command line.</summary>
        public void BuildAll(Config cfg, bool rebuild, bool dryRun)
        {
            BuildLibraries(cfg, rebuild, dryRun, summary: false);
            if (cfg.MediaLinksEnabled) BuildMedia(cfg, rebuild, dryRun, summary: false);
            Summary("All links", rebuild, dryRun);
        }

        public void BuildLibraries(Config cfg, bool rebuild, bool dryRun, bool summary = true)
        {
            var groups = (cfg.Libraries ?? new List<LibraryGroup>()).Where(g => g.Enabled).ToList();
            if (groups.Count == 0) _log("No enabled libraries.");
            foreach (var g in groups) { if (Stop()) break; Build(cfg, g, rebuild, dryRun); }
            if (summary) Summary("Libraries", rebuild, dryRun);
        }

        private void Summary(string what, bool rebuild, bool dryRun)
        {
            _log(string.Format("{0} done: {1} created, {2} skipped, {3} failed{4}{5}.",
                what, Created, Skipped, Failed, rebuild ? ", " + Removed + " old links removed" : "", dryRun ? " (dry run)" : ""));
        }

        /// <summary>
        /// For every system folder in each media rom folder (e.g. E:\Roms\nes) adds a link
        /// E:\Roms\nes\media -> MediaFolder\nes\media, when that media folder exists.
        /// An existing real "media" folder is never touched.
        /// </summary>
        public void BuildMedia(Config cfg, bool rebuild, bool dryRun, bool summary = true)
        {
            string name = string.IsNullOrWhiteSpace(cfg.MediaLinkName) ? "media" : cfg.MediaLinkName.Trim();
            string mediaRoot = (cfg.MediaFolder ?? "").Trim();

            if (mediaRoot.Length == 0 || !Directory.Exists(mediaRoot))
            {
                _log("[media] Media folder not found: '" + mediaRoot + "' - skipping media links.");
                if (summary) Summary("Media links", rebuild, dryRun);
                return;
            }

            foreach (var raw in cfg.MediaRomFolders ?? new List<string>())
            {
                string romRoot = (raw ?? "").Trim();
                if (romRoot.Length == 0) continue;
                _log("[media] " + romRoot + "  (links to " + mediaRoot + "\\<system>\\" + name + ")" + (dryRun ? " (dry run)" : ""));
                if (!Directory.Exists(romRoot)) { _log("  WARNING: rom folder not found, skipping."); continue; }

                int c0 = Created, s0 = Skipped, f0 = Failed, m0 = NoMedia;
                string[] systems;
                try { systems = Directory.GetDirectories(romRoot); }
                catch (Exception ex) { _log("  Cannot read " + romRoot + ": " + ex.Message); Failed++; continue; }

                foreach (var sysDir in systems.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (Stop()) { _log("  Stopped."); return; }
                    string sys = Path.GetFileName(sysDir);
                    if (!string.IsNullOrEmpty(cfg.SkipPrefix) && sys.StartsWith(cfg.SkipPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string source = Path.Combine(Path.Combine(mediaRoot, sys), name);
                    if (!Directory.Exists(source)) { NoMedia++; continue; }

                    string link = Path.Combine(sysDir, name);
                    bool isLink = Links.IsLink(link);
                    if (rebuild && isLink)
                    {
                        try { if (!dryRun) Directory.Delete(link, false); Removed++; } // removes the link only
                        catch (Exception ex) { _log("    ! " + sys + ": could not remove old link: " + ex.Message); Failed++; continue; }
                    }
                    else if (isLink || Directory.Exists(link) || File.Exists(link))
                    {
                        Skipped++;
                        continue;
                    }

                    if (dryRun)
                    {
                        _log("    + " + sys + "\\" + name + "  ->  " + source);
                        Created++;
                        continue;
                    }
                    try
                    {
                        string kind = Links.Create(link, source, cfg.LinkType);
                        _log("    + " + sys + "\\" + name + "  ->  " + source + "  (" + kind + ")");
                        Created++;
                    }
                    catch (Exception ex)
                    {
                        _log("    ! " + sys + ": " + ex.Message);
                        Failed++;
                    }
                }
                _log(string.Format("  {0} created, {1} already there, {2} without media, {3} failed",
                    Created - c0, Skipped - s0, NoMedia - m0, Failed - f0));
            }
            if (summary) Summary("Media links", rebuild, dryRun);
        }

        public void Build(Config cfg, LibraryGroup g, bool rebuild, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(g.Output)) { _log("Library with no output folder - skipped."); return; }

            string output;
            try { output = Path.GetFullPath(g.Output.Trim()); }
            catch (Exception ex) { _log("Bad output folder '" + g.Output + "': " + ex.Message); Failed++; return; }

            _log("[" + output + "]" + (dryRun ? " (dry run)" : ""));
            int c0 = Created, s0 = Skipped, f0 = Failed;

            try
            {
                if (!Directory.Exists(output))
                {
                    _log("  Creating output folder");
                    if (!dryRun) Directory.CreateDirectory(output);
                }

                if (rebuild && Directory.Exists(output))
                {
                    foreach (var entry in Directory.GetDirectories(output))
                    {
                        if (!Links.IsLink(entry)) continue; // real folders are never touched
                        if (!dryRun) Directory.Delete(entry, false); // removes the link only
                        Removed++;
                    }
                    _log("  Removed existing links" + (dryRun ? " (would)" : ""));
                }

                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rawSource in g.Sources ?? new List<string>())
                {
                    string source = (rawSource ?? "").Trim();
                    if (source.Length == 0) continue;
                    if (!Directory.Exists(source))
                    {
                        _log("  WARNING: source not found, skipping: " + source);
                        continue;
                    }
                    _log("  Source: " + source);

                    string[] systems;
                    try { systems = Directory.GetDirectories(source); }
                    catch (Exception ex) { _log("  Cannot read " + source + ": " + ex.Message); Failed++; continue; }

                    foreach (var sys in systems.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        if (Stop()) { _log("  Stopped."); return; }
                        string name = Path.GetFileName(sys);
                        if (!string.IsNullOrEmpty(cfg.SkipPrefix) && name.StartsWith(cfg.SkipPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            Skipped++;
                            continue;
                        }
                        if (!claimed.Add(name)) { Skipped++; continue; } // an earlier source already provides it

                        string target = Path.Combine(output, name);
                        bool exists = Directory.Exists(target) || File.Exists(target) || Links.IsLink(target);
                        if (exists && !(rebuild && dryRun && Links.IsLink(target)))
                        {
                            Skipped++;
                            continue;
                        }

                        if (dryRun)
                        {
                            _log("    + " + name + "  ->  " + sys);
                            Created++;
                            continue;
                        }
                        try
                        {
                            string kind = Links.Create(target, sys, cfg.LinkType);
                            _log("    + " + name + "  ->  " + sys + "  (" + kind + ")");
                            Created++;
                        }
                        catch (Exception ex)
                        {
                            _log("    ! " + name + ": " + ex.Message);
                            Failed++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log("  ERROR: " + ex.Message);
                Failed++;
            }

            _log(string.Format("  {0} created, {1} skipped, {2} failed", Created - c0, Skipped - s0, Failed - f0));
        }
    }
}
