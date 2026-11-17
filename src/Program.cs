using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RetroBatRomSwitcher
{
    public class Config
    {
        /// <summary>RetroBat install folder. Empty = the folder this exe is in.</summary>
        public string RetroBatPath { get; set; } = "";
        public string RetroBatExe { get; set; } = "retrobat.exe";
        public string LaunchArguments { get; set; } = "";

        /// <summary>Name of the folder RetroBat reads (becomes a junction).</summary>
        public string LinkName { get; set; } = "roms";

        /// <summary>Rule 1: connected Wi-Fi SSID contains this text (case-insensitive).</summary>
        public string WifiSsidContains { get; set; } = "sireteanu";
        public string WifiFolder { get; set; } = "roms-local";

        /// <summary>Rule 2: all of these drives are present and ready.</summary>
        public List<string> DockedDrives { get; set; } = new List<string> { "G:\\", "H:\\" };
        public string DockedFolder { get; set; } = "roms-docked";

        /// <summary>Rule 3: everything else. Your original roms folder is moved here on first run.</summary>
        public string DefaultFolder { get; set; } = "roms-default";
    }

    internal static class Program
    {
        private const string ConfigFileName = "RomSwitcher.json";
        private const string LogFileName = "RomSwitcher.log";
        private static string _logPath;

        [STAThread]
        private static int Main(string[] args)
        {
            bool dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
            bool noLaunch = dryRun || args.Any(a => a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase));

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            _logPath = Path.Combine(exeDir, LogFileName);
            TrimLog();
            Log("---- start " + (dryRun ? "(dry run) " : "") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            Config cfg;
            try { cfg = LoadConfig(Path.Combine(exeDir, ConfigFileName)); }
            catch (Exception ex) { Log("Config error: " + ex.Message); return 1; }

            string rbDir = string.IsNullOrWhiteSpace(cfg.RetroBatPath) ? exeDir : cfg.RetroBatPath;
            rbDir = Path.GetFullPath(rbDir);
            Log("RetroBat folder: " + rbDir);

            if (IsRetroBatRunning())
            {
                Log("RetroBat/EmulationStation already running - not touching the roms link.");
                if (!noLaunch) Launch(rbDir, cfg);
                return 0;
            }

            string chosen = ChooseFolder(cfg, out string reason);
            Log("Rule matched: " + reason + " -> " + chosen);

            string linkPath = Path.Combine(rbDir, cfg.LinkName);
            string targetPath = Path.Combine(rbDir, chosen);
            string defaultPath = Path.Combine(rbDir, cfg.DefaultFolder);

            try
            {
                if (!dryRun) MigrateOriginalFolder(linkPath, defaultPath);

                if (!Directory.Exists(targetPath) && !PathsEqual(targetPath, defaultPath))
                {
                    Log("Target folder missing: " + targetPath + " - falling back to " + cfg.DefaultFolder);
                    targetPath = defaultPath;
                }

                if (!Directory.Exists(targetPath))
                    Log("Default folder missing too: " + targetPath + " - leaving roms as is.");
                else if (dryRun)
                    Log("Dry run: would point " + linkPath + " -> " + targetPath);
                else
                    PointJunction(linkPath, targetPath);
            }
            catch (Exception ex)
            {
                Log("ERROR switching folder: " + ex.Message);
            }

            if (!noLaunch) Launch(rbDir, cfg);
            return 0;
        }

        // ---------- Rules ----------

        private static string ChooseFolder(Config cfg, out string reason)
        {
            // 1) Wi-Fi
            if (!string.IsNullOrWhiteSpace(cfg.WifiSsidContains))
            {
                var ssids = Wifi.GetConnectedSsids();
                Log("Connected Wi-Fi: " + (ssids.Count == 0 ? "(none detected)" : string.Join(", ", ssids)));
                var hit = ssids.FirstOrDefault(s => s.IndexOf(cfg.WifiSsidContains, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null)
                {
                    reason = "Wi-Fi '" + hit + "'";
                    return cfg.WifiFolder;
                }
            }

            // 2) Docked drives
            if (cfg.DockedDrives != null && cfg.DockedDrives.Count > 0)
            {
                var states = cfg.DockedDrives.Select(d => new { Drive = d, Ready = DriveReady(d) }).ToList();
                Log("Drives: " + string.Join(", ", states.Select(s => s.Drive + (s.Ready ? " ready" : " missing"))));
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

        private static bool DriveReady(string drive)
        {
            try
            {
                string root = drive.Trim();
                if (root.Length == 1) root += ":\\";
                else if (root.Length == 2 && root[1] == ':') root += "\\";
                var di = new DriveInfo(root);
                return di.IsReady && Directory.Exists(root);
            }
            catch { return false; }
        }

        // ---------- Junction handling ----------

        private static bool IsReparsePoint(string path)
        {
            var attr = File.GetAttributes(path);
            return (attr & FileAttributes.ReparsePoint) != 0;
        }

        /// <summary>First run: the real roms folder becomes the default library and roms becomes a link.</summary>
        private static void MigrateOriginalFolder(string linkPath, string defaultPath)
        {
            if (!Directory.Exists(linkPath) || IsReparsePoint(linkPath)) return;

            if (Directory.Exists(defaultPath))
            {
                // Never merge or delete real data.
                throw new InvalidOperationException(
                    "'" + linkPath + "' is a real folder and '" + defaultPath + "' already exists. " +
                    "Move or merge them yourself, then run again.");
            }

            Log("First run: moving real folder " + linkPath + " -> " + defaultPath);
            Directory.Move(linkPath, defaultPath);
        }

        private static void PointJunction(string linkPath, string targetPath)
        {
            if (File.Exists(linkPath))
                throw new InvalidOperationException("'" + linkPath + "' is a file, not a folder.");

            if (Directory.Exists(linkPath) || IsBrokenLink(linkPath))
            {
                if (!IsReparsePoint(linkPath))
                    throw new InvalidOperationException("'" + linkPath + "' is a real folder; refusing to replace it.");

                string current = Junction.GetTarget(linkPath);
                if (current != null && PathsEqual(current, targetPath))
                {
                    Log("Link already points to " + targetPath);
                    return;
                }
                // Removing a junction removes only the link, never the target's contents.
                Directory.Delete(linkPath, false);
            }

            Junction.Create(linkPath, targetPath);
            Log("Link set: " + linkPath + " -> " + targetPath);
        }

        private static bool IsBrokenLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // ---------- Launch ----------

        private static bool IsRetroBatRunning()
        {
            string[] names = { "emulationstation", "retrobat" };
            int self = Process.GetCurrentProcess().Id;
            return Process.GetProcesses().Any(p =>
            {
                try { return p.Id != self && names.Contains(p.ProcessName.ToLowerInvariant()); }
                catch { return false; }
            });
        }

        private static void Launch(string rbDir, Config cfg)
        {
            string exe = Path.Combine(rbDir, cfg.RetroBatExe);
            if (!File.Exists(exe))
            {
                Log("Cannot find " + exe + " - set RetroBatPath in " + ConfigFileName + ".");
                return;
            }
            Log("Launching " + exe);
            Process.Start(new ProcessStartInfo(exe, cfg.LaunchArguments ?? "")
            {
                WorkingDirectory = rbDir,
                UseShellExecute = true
            });
        }

        // ---------- Config / log ----------

        private const string DefaultConfigJson =
@"{
  ""RetroBatPath"": """",
  ""RetroBatExe"": ""retrobat.exe"",
  ""LaunchArguments"": """",
  ""LinkName"": ""roms"",
  ""WifiSsidContains"": ""sireteanu"",
  ""WifiFolder"": ""roms-local"",
  ""DockedDrives"": [ ""G:\\"", ""H:\\"" ],
  ""DockedFolder"": ""roms-docked"",
  ""DefaultFolder"": ""roms-default""
}
";

        private static Config LoadConfig(string path)
        {
            var json = new JavaScriptSerializer();
            if (!File.Exists(path))
            {
                File.WriteAllText(path, DefaultConfigJson, new UTF8Encoding(false));
                Log("Wrote default config: " + path);
                return new Config();
            }
            return json.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
        }

        private static void Log(string msg)
        {
            try { File.AppendAllText(_logPath, msg + "\r\n"); } catch { }
            try { Console.WriteLine(msg); } catch { }
        }

        private static void TrimLog()
        {
            try
            {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 256 * 1024)
                {
                    var lines = File.ReadAllLines(_logPath);
                    File.WriteAllLines(_logPath, lines.Skip(Math.Max(0, lines.Length - 500)));
                }
            }
            catch { }
        }
    }

    // ================= Wi-Fi =================

    internal static class Wifi
    {
        public static List<string> GetConnectedSsids()
        {
            var result = new List<string>();
            try { result.AddRange(FromWlanApi()); } catch { }
            if (result.Count == 0)
            {
                try { result.AddRange(FromNetsh()); } catch { }
            }
            return result.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint ver, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr mem);

        private const int OpcodeCurrentConnection = 7;
        private const int InterfaceInfoSize = 16 + 512 + 4;   // GUID + WCHAR[256] + state
        private const int SsidLengthOffset = 4 + 4 + 512;     // isState + mode + profileName
        private const int SsidOffset = SsidLengthOffset + 4;

        private static IEnumerable<string> FromWlanApi()
        {
            var found = new List<string>();
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr h) != 0) return found;
            try
            {
                if (WlanEnumInterfaces(h, IntPtr.Zero, out IntPtr list) != 0) return found;
                try
                {
                    int count = Marshal.ReadInt32(list);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = IntPtr.Add(list, 8 + i * InterfaceInfoSize);
                        var guid = (Guid)Marshal.PtrToStructure(item, typeof(Guid));
                        if (WlanQueryInterface(h, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out _, out IntPtr data, IntPtr.Zero) != 0)
                            continue;
                        try
                        {
                            int len = Math.Min(32, Marshal.ReadInt32(data, SsidLengthOffset));
                            var bytes = new byte[len];
                            Marshal.Copy(IntPtr.Add(data, SsidOffset), bytes, 0, len);
                            found.Add(Encoding.UTF8.GetString(bytes));
                            // Profile name is usually the SSID too; helps if SSID is hidden from us.
                            found.Add(Marshal.PtrToStringUni(IntPtr.Add(data, 8)));
                        }
                        finally { WlanFreeMemory(data); }
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
            return found;
        }

        private static IEnumerable<string> FromNetsh()
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return Regex.Matches(output, @"^\s*(SSID|Profile)\s*:\s*(.+?)\s*$", RegexOptions.Multiline)
                            .Cast<Match>().Select(m => m.Groups[2].Value).ToList();
            }
        }
    }

    // ================= Junctions (no admin needed) =================

    internal static class Junction
    {
        public static void Create(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                if (p.ExitCode != 0 || !Directory.Exists(link))
                    throw new IOException("mklink failed: " + err.Trim());
            }
        }

        // Reads a junction's target via FSCTL_GET_REPARSE_POINT. Returns null if unreadable.
        public static string GetTarget(string link)
        {
            const uint GENERIC_READ = 0x80000000;
            const uint FILE_SHARE_ALL = 7;
            const uint OPEN_EXISTING = 3;
            const uint FLAGS = 0x02000000 /*BACKUP_SEMANTICS*/ | 0x00200000 /*OPEN_REPARSE_POINT*/;
            const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
            const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

            IntPtr h = CreateFile(link, GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FLAGS, IntPtr.Zero);
            if (h == new IntPtr(-1)) return null;
            IntPtr buf = Marshal.AllocHGlobal(16 * 1024);
            try
            {
                if (!DeviceIoControl(h, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buf, 16 * 1024, out _, IntPtr.Zero))
                    return null;
                if ((uint)Marshal.ReadInt32(buf, 0) != IO_REPARSE_TAG_MOUNT_POINT) return null;
                int subOff = Marshal.ReadInt16(buf, 8);
                int subLen = Marshal.ReadInt16(buf, 10);
                string s = Marshal.PtrToStringUni(IntPtr.Add(buf, 16 + subOff), subLen / 2);
                return s.StartsWith(@"\??\") ? s.Substring(4) : s;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                CloseHandle(h);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    }
}
