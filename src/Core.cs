using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RetroBatFolderSwitcher
{
    public class Config
    {
        /// <summary>RetroBat install folder. Empty = the folder this exe is in.</summary>
        public string RetroBatPath { get; set; } = "";
        public string RetroBatExe { get; set; } = "retrobat.exe";
        public string LaunchArguments { get; set; } = "";

        /// <summary>Name of the folder RetroBat reads (becomes a junction).</summary>
        public string LinkName { get; set; } = "roms";

        /// <summary>Rule 1: connected Wi-Fi name contains this text (case-insensitive).</summary>
        public bool WifiRuleEnabled { get; set; } = true;
        public string WifiSsidContains { get; set; } = "sireteanu";
        public string WifiFolder { get; set; } = "roms-local";

        /// <summary>Rule 2: all of these drives are present and ready.</summary>
        public bool DockedRuleEnabled { get; set; } = true;
        public List<string> DockedDrives { get; set; } = new List<string> { "G:\\", "H:\\" };
        public string DockedFolder { get; set; } = "roms-docked";

        /// <summary>Rule 3: everything else. The original roms folder is moved here on first run.</summary>
        public string DefaultFolder { get; set; } = "roms-default";
    }

    internal static class ConfigStore
    {
        public static Config Load(string path, Action<string> log)
        {
            if (!File.Exists(path))
            {
                var def = new Config();
                Save(path, def);
                log?.Invoke("Wrote default config: " + path);
                return def;
            }
            return new JavaScriptSerializer().Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
        }

        public static void Save(string path, Config c)
        {
            var js = new JavaScriptSerializer();
            Func<object, string> v = o => js.Serialize(o);
            var drives = string.Join(", ", (c.DockedDrives ?? new List<string>()).Select(d => v(d)));
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"RetroBatPath\": ").Append(v(c.RetroBatPath ?? "")).Append(",\r\n");
            sb.Append("  \"RetroBatExe\": ").Append(v(c.RetroBatExe ?? "")).Append(",\r\n");
            sb.Append("  \"LaunchArguments\": ").Append(v(c.LaunchArguments ?? "")).Append(",\r\n");
            sb.Append("  \"LinkName\": ").Append(v(c.LinkName ?? "")).Append(",\r\n");
            sb.Append("  \"WifiRuleEnabled\": ").Append(v(c.WifiRuleEnabled)).Append(",\r\n");
            sb.Append("  \"WifiSsidContains\": ").Append(v(c.WifiSsidContains ?? "")).Append(",\r\n");
            sb.Append("  \"WifiFolder\": ").Append(v(c.WifiFolder ?? "")).Append(",\r\n");
            sb.Append("  \"DockedRuleEnabled\": ").Append(v(c.DockedRuleEnabled)).Append(",\r\n");
            sb.Append("  \"DockedDrives\": [ ").Append(drives).Append(" ],\r\n");
            sb.Append("  \"DockedFolder\": ").Append(v(c.DockedFolder ?? "")).Append(",\r\n");
            sb.Append("  \"DefaultFolder\": ").Append(v(c.DefaultFolder ?? "")).Append("\r\n");
            sb.Append("}\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    internal class Switcher
    {
        private readonly Action<string> _log;
        public Switcher(Action<string> log) { _log = log ?? (_ => { }); }

        public static string ResolveRetroBatDir(Config cfg, string exeDir) =>
            Path.GetFullPath(string.IsNullOrWhiteSpace(cfg.RetroBatPath) ? exeDir : cfg.RetroBatPath);

        // ---------- Rules ----------

        public string ChooseFolder(Config cfg, out string reason)
        {
            // 1) Wi-Fi
            if (cfg.WifiRuleEnabled && !string.IsNullOrWhiteSpace(cfg.WifiSsidContains))
            {
                var ssids = Wifi.GetConnectedSsids();
                _log("Connected Wi-Fi: " + (ssids.Count == 0 ? "(none detected)" : string.Join(", ", ssids)));
                var hit = ssids.FirstOrDefault(s => s.IndexOf(cfg.WifiSsidContains.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null)
                {
                    reason = "Wi-Fi '" + hit + "'";
                    return cfg.WifiFolder;
                }
            }

            // 2) Docked drives
            if (cfg.DockedRuleEnabled && cfg.DockedDrives != null && cfg.DockedDrives.Count > 0)
            {
                var states = cfg.DockedDrives.Select(d => new { Drive = d, Ready = DriveReady(d) }).ToList();
                _log("Drives: " + string.Join(", ", states.Select(s => s.Drive + (s.Ready ? " ready" : " missing"))));
                if (states.All(s => s.Ready))
                {
                    reason = "drives " + string.Join(" + ", cfg.DockedDrives) + " present";
                    return cfg.DockedFolder;
                }
            }

            // 3) Default
            reason = "default";
            return cfg.DefaultFolder;
        }

        public static bool DriveReady(string drive)
        {
            try
            {
                string root = NormalizeDrive(drive);
                var di = new DriveInfo(root);
                return di.IsReady && Directory.Exists(root);
            }
            catch { return false; }
        }

        public static string NormalizeDrive(string drive)
        {
            string root = (drive ?? "").Trim();
            if (root.Length == 1) root += ":\\";
            else if (root.Length == 2 && root[1] == ':') root += "\\";
            return root.ToUpperInvariant();
        }

        // ---------- Apply ----------

        /// <summary>Decides the folder and points the link at it. Returns the folder actually used (or null).</summary>
        public string Apply(Config cfg, string rbDir, bool dryRun)
        {
            string chosen = ChooseFolder(cfg, out string reason);
            _log("Rule matched: " + reason + " -> " + chosen);

            string linkPath = Path.Combine(rbDir, cfg.LinkName);
            string targetPath = Path.Combine(rbDir, chosen ?? "");
            string defaultPath = Path.Combine(rbDir, cfg.DefaultFolder);

            if (!dryRun) MigrateOriginalFolder(linkPath, defaultPath);
            else if (Directory.Exists(linkPath) && !IsReparsePoint(linkPath))
                _log("Dry run: '" + cfg.LinkName + "' is a real folder; first real run renames it to '" + cfg.DefaultFolder + "'.");

            bool defaultWillExist = Directory.Exists(defaultPath) ||
                                    (dryRun && Directory.Exists(linkPath) && !IsReparsePoint(linkPath));

            if (!Directory.Exists(targetPath) && !PathsEqual(targetPath, defaultPath))
            {
                _log("Target folder missing: " + targetPath + " - falling back to " + cfg.DefaultFolder);
                targetPath = defaultPath;
            }

            if (!Directory.Exists(targetPath) && !(PathsEqual(targetPath, defaultPath) && defaultWillExist))
            {
                _log("Folder missing: " + targetPath + " - leaving '" + cfg.LinkName + "' as is.");
                return null;
            }

            if (dryRun) _log("Dry run: would point " + linkPath + " -> " + targetPath);
            else PointJunction(linkPath, targetPath);
            return targetPath;
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        /// <summary>First run: the real roms folder becomes the default library and roms becomes a link.</summary>
        private void MigrateOriginalFolder(string linkPath, string defaultPath)
        {
            if (!Directory.Exists(linkPath) || IsReparsePoint(linkPath)) return;

            if (Directory.Exists(defaultPath))
            {
                // Never merge or delete real data.
                throw new InvalidOperationException(
                    "'" + linkPath + "' is a real folder and '" + defaultPath + "' already exists. " +
                    "Move or merge them yourself, then run again.");
            }

            _log("First run: moving real folder " + linkPath + " -> " + defaultPath);
            Directory.Move(linkPath, defaultPath);
        }

        private void PointJunction(string linkPath, string targetPath)
        {
            if (File.Exists(linkPath))
                throw new InvalidOperationException("'" + linkPath + "' is a file, not a folder.");

            if (IsReparsePoint(linkPath))
            {
                string current = Junction.GetTarget(linkPath);
                if (current != null && PathsEqual(current, targetPath))
                {
                    _log("Link already points to " + targetPath);
                    return;
                }
                // Removing a junction removes only the link, never the target's contents.
                Directory.Delete(linkPath, false);
            }
            else if (Directory.Exists(linkPath))
            {
                throw new InvalidOperationException("'" + linkPath + "' is a real folder; refusing to replace it.");
            }

            Junction.Create(linkPath, targetPath);
            _log("Link set: " + linkPath + " -> " + targetPath);
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // ---------- Launch ----------

        public static bool IsRetroBatRunning()
        {
            string[] names = { "emulationstation", "retrobat" };
            int self = Process.GetCurrentProcess().Id;
            return Process.GetProcesses().Any(p =>
            {
                try { return p.Id != self && names.Contains(p.ProcessName.ToLowerInvariant()); }
                catch { return false; }
            });
        }

        public bool Launch(string rbDir, Config cfg)
        {
            string exe = Path.Combine(rbDir, cfg.RetroBatExe);
            if (!File.Exists(exe))
            {
                _log("Cannot find " + exe + " - set the RetroBat folder in settings.");
                return false;
            }
            _log("Launching " + exe);
            Process.Start(new ProcessStartInfo(exe, cfg.LaunchArguments ?? "")
            {
                WorkingDirectory = rbDir,
                UseShellExecute = true
            });
            return true;
        }
    }
}
