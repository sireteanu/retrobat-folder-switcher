using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace RetroBatFolderSwitcher
{
    // ================= Local network check =================

    internal class LocalCheckResult
    {
        public bool Local;
        public string Server;
        public string Adapter;
        public string Mac;
        /// <summary>Set when Windows would route traffic to the server through another adapter (e.g. the VPN).</summary>
        public string Warning;
        public readonly List<string> Details = new List<string>();

        public string Summary => Local
            ? Server + " found on " + Adapter + " (" + Mac + ")"
            : Server + " is not on the local network";
    }

    /// <summary>
    /// Tells whether a server is on the same physical network, i.e. reachable without a VPN.
    /// It asks for the server's hardware (MAC) address with ARP from each wired / Wi-Fi adapter on the
    /// server's subnet. ARP never crosses a VPN tunnel such as WireGuard, so an answer means "local".
    /// Optionally the answer must come from the expected MAC (many networks reuse 192.168.1.x).
    /// </summary>
    internal static class LocalNetwork
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

        [DllImport("iphlpapi.dll")]
        private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        public static LocalCheckResult Check(string server, string expectedMac)
        {
            var r = new LocalCheckResult { Server = (server ?? "").Trim() };
            IPAddress ip = Resolve(r.Server, r);
            if (ip == null) return r;
            r.Server = ip.ToString();
            uint dest = ToUInt(ip);

            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception ex) { r.Details.Add("Cannot list network adapters: " + ex.Message); return r; }

            bool anyOnSubnet = false;
            foreach (var ni in all)
            {
                if (ni.OperationalStatus != OperationalStatus.Up || !IsPhysical(ni)) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    if (!SameSubnet(ua.Address, ip, ua.IPv4Mask)) continue;
                    anyOnSubnet = true;

                    var mac = new byte[6];
                    uint len = 6;
                    int rc;
                    try { rc = SendARP(dest, ToUInt(ua.Address), mac, ref len); }
                    catch (Exception ex) { r.Details.Add(ni.Name + ": ARP not available (" + ex.Message + ")"); continue; }
                    if (rc != 0 || len == 0)
                    {
                        r.Details.Add(ni.Name + " (" + ua.Address + "): no answer from " + ip);
                        continue;
                    }

                    string found = FormatMac(mac, len);
                    if (!string.IsNullOrWhiteSpace(expectedMac) && NormalizeMac(expectedMac) != NormalizeMac(found))
                    {
                        r.Details.Add(ni.Name + ": " + ip + " answered as " + found + ", not your server (" + expectedMac.Trim() + ")");
                        continue;
                    }

                    r.Local = true;
                    r.Adapter = ni.Name;
                    r.Mac = found;
                    r.Details.Add(ni.Name + " (" + ua.Address + "): found " + ip + " at " + found);
                    r.Warning = RouteWarning(dest, ip, ni, all);
                    return r;
                }
            }
            if (!anyOnSubnet)
                r.Details.Add("No connected wired or Wi-Fi adapter is on " + ip + "'s network.");
            return r;
        }

        /// <summary>Warns if Windows would send traffic to the server through a different adapter (the VPN).</summary>
        private static string RouteWarning(uint dest, IPAddress ip, NetworkInterface local, NetworkInterface[] all)
        {
            try
            {
                int localIndex = local.GetIPProperties().GetIPv4Properties().Index;
                if (GetBestInterface(dest, out uint best) != 0 || best == (uint)localIndex) return null;
                string bestName = all.Where(n =>
                {
                    try { return n.GetIPProperties().GetIPv4Properties()?.Index == (int)best; } catch { return false; }
                }).Select(n => n.Name).FirstOrDefault() ?? ("interface " + best);
                return "Windows sends traffic for " + ip + " through '" + bestName + "', not '" + local.Name +
                       "', so copies may still go through the VPN. Remove your home subnet from WireGuard's AllowedIPs to fix this.";
            }
            catch { return null; }
        }

        private static bool IsPhysical(NetworkInterface ni)
        {
            switch (ni.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                case NetworkInterfaceType.Ethernet3Megabit:
                case NetworkInterfaceType.Wireless80211:
                    break;
                default:
                    return false; // loopback, tunnels, WireGuard (Wintun) etc.
            }
            string d = ((ni.Description ?? "") + " " + (ni.Name ?? "")).ToLowerInvariant();
            foreach (var vpn in new[] { "wireguard", "wintun", "tap-windows", "openvpn", "tailscale", "zerotier", "vpn" })
                if (d.Contains(vpn)) return false;
            return true;
        }

        private static IPAddress Resolve(string server, LocalCheckResult r)
        {
            if (server.Length == 0) { r.Details.Add("No server address set."); return null; }
            if (IPAddress.TryParse(server, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetwork) return ip;
            try
            {
                ip = Dns.GetHostAddresses(server).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) r.Details.Add("'" + server + "' has no IPv4 address.");
                return ip;
            }
            catch (Exception ex) { r.Details.Add("Cannot find '" + server + "': " + ex.Message); return null; }
        }

        private static uint ToUInt(IPAddress ip) => BitConverter.ToUInt32(ip.GetAddressBytes(), 0); // network byte order

        private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
        {
            byte[] x = a.GetAddressBytes(), y = b.GetAddressBytes(), m = mask.GetAddressBytes();
            for (int i = 0; i < 4; i++) if ((x[i] & m[i]) != (y[i] & m[i])) return false;
            return true;
        }

        private static string FormatMac(byte[] mac, uint len) =>
            string.Join("-", mac.Take((int)Math.Min(len, (uint)mac.Length)).Select(b => b.ToString("X2")));

        public static string NormalizeMac(string mac) =>
            new string((mac ?? "").ToUpperInvariant().Where(Uri.IsHexDigit).ToArray());
    }

    // ================= Junctions (no admin needed) =================

    internal static class Junction
    {
        /// <summary>Creates a directory junction natively (no admin, no cmd window). Local targets only.</summary>
        public static void Create(string link, string target)
        {
            string full = Path.GetFullPath(target);
            if (full.Length > 3) full = full.TrimEnd('\\');
            string subst = @"\??\" + full;
            byte[] substBytes = Encoding.Unicode.GetBytes(subst);
            byte[] printBytes = Encoding.Unicode.GetBytes(full);

            // REPARSE_DATA_BUFFER (MountPointReparseBuffer)
            int pathBufLen = substBytes.Length + 2 + printBytes.Length + 2;
            int dataLen = 8 + pathBufLen;
            var buf = new byte[8 + dataLen];
            BitConverter.GetBytes(0xA0000003u).CopyTo(buf, 0);                      // IO_REPARSE_TAG_MOUNT_POINT
            BitConverter.GetBytes((ushort)dataLen).CopyTo(buf, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buf, 8);                          // SubstituteNameOffset
            BitConverter.GetBytes((ushort)substBytes.Length).CopyTo(buf, 10);         // SubstituteNameLength
            BitConverter.GetBytes((ushort)(substBytes.Length + 2)).CopyTo(buf, 12);   // PrintNameOffset
            BitConverter.GetBytes((ushort)printBytes.Length).CopyTo(buf, 14);         // PrintNameLength
            substBytes.CopyTo(buf, 16);
            printBytes.CopyTo(buf, 16 + substBytes.Length + 2);

            Directory.CreateDirectory(link);
            const uint GENERIC_WRITE = 0x40000000;
            const uint OPEN_EXISTING = 3;
            const uint FLAGS = 0x02000000 /*BACKUP_SEMANTICS*/ | 0x00200000 /*OPEN_REPARSE_POINT*/;
            const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;

            IntPtr h = CreateFile(link, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, FLAGS, IntPtr.Zero);
            if (h == new IntPtr(-1))
            {
                int e = Marshal.GetLastWin32Error();
                try { Directory.Delete(link, false); } catch { }
                throw new IOException("Could not open '" + link + "' (error " + e + ").");
            }
            IntPtr mem = Marshal.AllocHGlobal(buf.Length);
            try
            {
                Marshal.Copy(buf, 0, mem, buf.Length);
                if (!DeviceIoControl(h, FSCTL_SET_REPARSE_POINT, mem, buf.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    int e = Marshal.GetLastWin32Error();
                    CloseHandle(h); h = IntPtr.Zero;
                    try { Directory.Delete(link, false); } catch { }
                    throw new IOException("Could not create junction '" + link + "' (error " + e + ").");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
                if (h != IntPtr.Zero) CloseHandle(h);
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

    // ================= Links (symlink / junction) =================

    internal static class Links
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CreateSymbolicLink(string link, string target, int flags);

        private const int SYMBOLIC_LINK_FLAG_DIRECTORY = 1;
        private const int SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 2;

        /// <summary>Directory symlink. Works without admin when Windows Developer Mode is on.</summary>
        public static bool TryCreateSymlink(string link, string target, out int error)
        {
            bool ok = CreateSymbolicLink(link, target, SYMBOLIC_LINK_FLAG_DIRECTORY | SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error == 87) // older Windows doesn't know the unprivileged flag
            {
                ok = CreateSymbolicLink(link, target, SYMBOLIC_LINK_FLAG_DIRECTORY);
                error = ok ? 0 : Marshal.GetLastWin32Error();
            }
            return ok;
        }

        public static bool IsNetworkPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\")) return true;
                return new DriveInfo(Path.GetPathRoot(full)).DriveType == DriveType.Network;
            }
            catch { return false; }
        }

        /// <summary>True for symlinks/junctions, including broken ones whose target is offline.</summary>
        public static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        /// <summary>Creates a directory link. linkType: Auto, Symlink or Junction. Returns the kind created.</summary>
        public static string Create(string link, string target, string linkType)
        {
            string type = (linkType ?? "Auto").Trim().ToLowerInvariant();
            bool network = IsNetworkPath(target);

            if (type == "junction")
            {
                if (network) throw new IOException("junctions can't point to network locations - use Symlink or Auto");
                Junction.Create(link, target);
                return "junction";
            }

            if (TryCreateSymlink(link, target, out int err)) return "symlink";

            if (type == "symlink" || network)
                throw new IOException(err == 1314
                    ? "symlinks need Developer Mode or Run as administrator" + (network ? " (network source)" : "")
                    : "symlink failed (error " + err + ")");

            Junction.Create(link, target);
            return "junction";
        }
    }
}
