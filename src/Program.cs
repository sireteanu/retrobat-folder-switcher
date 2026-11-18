using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RetroBatFolderSwitcher
{
    internal static class Program
    {
        public const string ConfigFileName = "FolderSwitcher.json";
        public const string LogFileName = "FolderSwitcher.log";

        public static string ExeDir => AppDomain.CurrentDomain.BaseDirectory;
        public static string ConfigPath => Path.Combine(ExeDir, ConfigFileName);
        public static string LogPath => Path.Combine(ExeDir, LogFileName);

        [STAThread]
        private static int Main(string[] args)
        {
            Func<string, bool> Has = flag => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

            bool settings = Has("--settings") || Has("/settings") || Has("--config");
            try { settings |= (Control.ModifierKeys & Keys.Shift) == Keys.Shift; } catch { }

            if (settings)
            {
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SettingsForm());
                return 0;
            }

            if (Has("--build-libraries") || Has("--rebuild-libraries"))
                return BuildLibrariesOnly(rebuild: Has("--rebuild-libraries"), dryRun: Has("--dry-run"));

            return RunHeadless(dryRun: Has("--dry-run"), noLaunch: Has("--dry-run") || Has("--no-launch"));
        }

        private static int RunHeadless(bool dryRun, bool noLaunch)
        {
            TrimLog();
            Log("---- start " + (dryRun ? "(dry run) " : "") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            Config cfg;
            try { cfg = ConfigStore.Load(ConfigPath, Log); }
            catch (Exception ex) { Log("Config error: " + ex.Message); return 1; }

            string rbDir = Switcher.ResolveRetroBatDir(cfg, ExeDir);
            Log("RetroBat folder: " + rbDir);
            var sw = new Switcher(Log);

            if (Switcher.IsRetroBatRunning())
            {
                Log("RetroBat/EmulationStation already running - not touching the roms link.");
                if (!noLaunch) sw.Launch(rbDir, cfg);
                return 0;
            }

            if (cfg.BuildLibrariesOnLaunch)
            {
                try { new LibraryBuilder(Log).BuildAll(cfg, rebuild: false, dryRun: dryRun); }
                catch (Exception ex) { Log("ERROR building libraries: " + ex.Message); }
            }

            try { sw.Apply(cfg, rbDir, dryRun); }
            catch (Exception ex) { Log("ERROR switching folder: " + ex.Message); }

            if (!noLaunch) sw.Launch(rbDir, cfg);
            return 0;
        }

        private static int BuildLibrariesOnly(bool rebuild, bool dryRun)
        {
            TrimLog();
            Log("---- " + (rebuild ? "rebuild" : "build") + " libraries " + (dryRun ? "(dry run) " : "") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Config cfg;
            try { cfg = ConfigStore.Load(ConfigPath, Log); }
            catch (Exception ex) { Log("Config error: " + ex.Message); return 1; }
            if (rebuild && !dryRun && Switcher.IsRetroBatRunning())
            {
                Log("RetroBat is running - close it before rebuilding links.");
                return 1;
            }
            var b = new LibraryBuilder(Log);
            b.BuildAll(cfg, rebuild, dryRun);
            return b.Failed > 0 ? 2 : 0;
        }

        public static void Log(string msg)
        {
            try { File.AppendAllText(LogPath, msg + "\r\n"); } catch { }
            try { Console.WriteLine(msg); } catch { }
        }

        internal static void TrimLogPublic() => TrimLog();

        private static void TrimLog()
        {
            try
            {
                // Robocopy output can be long, so keep a generous tail.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 8 * 1024 * 1024)
                {
                    var lines = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, lines.Skip(Math.Max(0, lines.Length - 20000)));
                }
            }
            catch { }
        }

        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    }
}
