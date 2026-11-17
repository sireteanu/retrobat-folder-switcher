using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RetroBatRomSwitcher
{
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
