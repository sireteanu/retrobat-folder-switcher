using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RetroBatFolderSwitcher
{
    /// <summary>One robocopy mirror job.</summary>
    public class SyncJob
    {
        public string Source { get; set; } = "";
        public string Destination { get; set; } = "";
        /// <summary>Folder names to skip (/XD). Rom syncs always skip "media" on top of these.</summary>
        public List<string> Exclude { get; set; } = new List<string>();

        public SyncJob Clone() => new SyncJob
        {
            Source = Source, Destination = Destination, Exclude = new List<string>(Exclude ?? new List<string>())
        };

        public static List<SyncJob> DefaultRomSyncs() => new List<SyncJob>
        {
            new SyncJob { Source = @"X:\sets\mapping\retrobat\DRIVE",  Destination = @"C:\Games\Roms" },
            new SyncJob { Source = @"X:\sets\mapping\retrobat\SDCARD", Destination = @"E:\Roms" },
        };

        public static List<SyncJob> DefaultMediaSyncs() => new List<SyncJob>
        {
            new SyncJob
            {
                Source = @"X:\sets\mapping\retrobat\MEDIA_FULL", Destination = @"C:\Games\RetroBat\media",
                Exclude = new List<string> { "bezel", "boxback", "fanarts", "manuals", "mix", "videos", "screenshots", "box2d", "titles" }
            },
        };
    }

    internal static class Robocopy
    {
        public const string RomExclude = "media";

        /// <summary>Folder names separated by spaces, commas or semicolons. Quote names that contain spaces.</summary>
        public static List<string> ParseExcludes(string text) =>
            System.Text.RegularExpressions.Regex.Matches(text ?? "", "\"([^\"]+)\"|[^\\s,;\"]+")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => (m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public static List<string> ExcludesFor(SyncJob job, bool romSync)
        {
            var list = new List<string>();
            if (romSync) list.Add(RomExclude);
            foreach (var x in job.Exclude ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(x) && !list.Contains(x.Trim(), StringComparer.OrdinalIgnoreCase))
                    list.Add(x.Trim());
            return list;
        }

        public static string BuildArgs(SyncJob job, bool romSync, string options, bool preview)
        {
            var sb = new StringBuilder();
            sb.Append(QuotePath(job.Source)).Append(' ').Append(QuotePath(job.Destination)).Append(" /MIR");
            var ex = ExcludesFor(job, romSync);
            if (ex.Count > 0) sb.Append(" /XD ").Append(string.Join(" ", ex.Select(QuoteArg)));
            if (!string.IsNullOrWhiteSpace(options)) sb.Append(' ').Append(options.Trim());
            if (preview) sb.Append(" /L");
            return sb.ToString();
        }

        /// <summary>Quotes a path for robocopy. A trailing backslash would escape the closing quote, so it is removed.</summary>
        internal static string QuotePath(string p)
        {
            p = (p ?? "").Trim().Trim('"');
            if (p.Length > 3) p = p.TrimEnd('\\');
            if (p.Length == 3 && p.EndsWith(":\\")) p = p + "."; // "C:\" -> "C:\."
            return "\"" + p + "\"";
        }

        private static string QuoteArg(string a) => a.IndexOfAny(new[] { ' ', '\t' }) >= 0 ? "\"" + a + "\"" : a;

        /// <summary>Returns a reason not to run this mirror, or null if it looks safe.</summary>
        public static string Check(SyncJob job)
        {
            string src = (job.Source ?? "").Trim(), dst = (job.Destination ?? "").Trim();
            if (src.Length == 0) return "no input folder set";
            if (dst.Length == 0) return "no output folder set";

            string fs, fd;
            try { fs = Path.GetFullPath(src).TrimEnd('\\'); fd = Path.GetFullPath(dst).TrimEnd('\\'); }
            catch (Exception ex) { return "bad path: " + ex.Message; }

            if (fd.Length <= 2 || string.Equals(Path.GetPathRoot(fd)?.TrimEnd('\\'), fd, StringComparison.OrdinalIgnoreCase))
                return "the output can't be a whole drive (" + dst + ") - /MIR would delete everything else on it";
            if (Inside(fs, fd) || Inside(fd, fs))
                return "input and output can't be the same folder or inside each other";
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var bad in new[] { win, home })
                if (!string.IsNullOrEmpty(bad) && (string.Equals(fd, bad.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || Inside(bad.TrimEnd('\\'), fd)))
                    return "refusing to mirror into " + fd;

            if (!Directory.Exists(src)) return "input folder not found: " + src + " (is the drive connected?)";
            try
            {
                if (!Directory.EnumerateFileSystemEntries(src).Any())
                    return "input folder is empty - mirroring it would empty " + dst;
            }
            catch (Exception ex) { return "cannot read input folder: " + ex.Message; }
            return null;
        }

        /// <summary>True when 'inner' is 'outer' or inside it.</summary>
        private static bool Inside(string outer, string inner) =>
            string.Equals(outer, inner, StringComparison.OrdinalIgnoreCase) ||
            inner.StartsWith(outer + "\\", StringComparison.OrdinalIgnoreCase);

        public static string DescribeExit(int code)
        {
            if (code < 0) return "stopped";
            if (code >= 16) return "FAILED - serious error, nothing was copied (see messages above)";
            if (code >= 8) return "FAILED - some files or folders could not be copied (see messages above)";
            var parts = new List<string>();
            if ((code & 1) != 0) parts.Add("files copied");
            if ((code & 2) != 0) parts.Add("extra files in output removed");
            if ((code & 4) != 0) parts.Add("some mismatched files");
            return "OK - " + (parts.Count == 0 ? "already in sync, nothing to do" : string.Join(", ", parts));
        }

        /// <summary>Runs robocopy, streaming every output line. Returns the exit code, or -1 if stopped.</summary>
        public static int Run(string args, Action<string> onLine, Func<bool> stopRequested)
        {
            Encoding enc;
            try { enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { enc = Encoding.Default; }

            var psi = new ProcessStartInfo("robocopy.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) onLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(200))
                {
                    if (stopRequested())
                    {
                        try { p.Kill(); } catch { }
                        p.WaitForExit(5000);
                        return -1;
                    }
                }
                p.WaitForExit(); // flush remaining async output
                return p.ExitCode;
            }
        }
    }
}
