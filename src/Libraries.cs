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
        public int Created, Skipped, Failed, Removed;

        public LibraryBuilder(Action<string> log) { _log = log ?? (_ => { }); }

        public void BuildAll(Config cfg, bool rebuild, bool dryRun)
        {
            var groups = (cfg.Libraries ?? new List<LibraryGroup>()).Where(g => g.Enabled).ToList();
            if (groups.Count == 0) { _log("No enabled libraries."); return; }
            foreach (var g in groups) Build(cfg, g, rebuild, dryRun);
            _log(string.Format("Libraries done: {0} created, {1} skipped, {2} failed{3}{4}.",
                Created, Skipped, Failed, rebuild ? ", " + Removed + " old links removed" : "", dryRun ? " (dry run)" : ""));
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
