// RamInfo: what is using the PC's memory right now, for JobTray's list.
//
// Working sets are summed per program name (all of a browser's processes are
// one row), the biggest few are listed, and "Others" is everything else that
// is in use: the remaining programs plus the system's own memory. Working sets
// can count a page shared between processes more than once, so the rows are a
// close guide, not an exact ledger.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace JobTray
{
    class RamApp
    {
        public string Name;     // as shown
        public HashSet<string> Processes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // process names, checked before ending
        public long Bytes;
        public int Count;
        public List<int> Pids = new List<int>();
        public bool CanEnd;
    }

    class RamSnapshot
    {
        public long Total, Free;
        public List<RamApp> Top = new List<RamApp>();
        public long OthersBytes;
        public int OthersCount;
        public Dictionary<int, long> WorkingSet = new Dictionary<int, long>();
        public Dictionary<int, int> Parent = new Dictionary<int, int>();

        public long Used { get { return Math.Max(0, Total - Free); } }

        // a job's process plus everything it started
        public long TreeBytes(int pid)
        {
            if (pid <= 0 || !WorkingSet.ContainsKey(pid)) return 0;
            var tree = new HashSet<int> { pid };
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var kv in Parent)
                    if (tree.Contains(kv.Value) && tree.Add(kv.Key)) grew = true;
            }
            long sum = 0;
            foreach (int p in tree) { long b; if (WorkingSet.TryGetValue(p, out b)) sum += b; }
            return sum;
        }
    }

    static class RamInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx s);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // never offered an End button: ending these breaks Windows, or this very list
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "Memory Compression", "Secure System", "smss", "csrss", "wininit",
            "winlogon", "services", "lsass", "svchost", "dwm", "fontdrvhost", "explorer", "sihost", "taskhostw",
            "MsMpEng", "ctfmon", "audiodg", "spoolsv", "JobTray", "RuntimeBroker", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "TextInputHost", "WmiPrvSE", "conhost"
        };

        static readonly Dictionary<string, string> Friendly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", "Chrome" }, { "brave", "Brave" }, { "msedge", "Edge" }, { "firefox", "Firefox" },
            { "msedgewebview2", "Edge WebView2 (no owner)" }, { "claude", "Claude" },
            { "utorrentie", "uTorrent" }, { "WhatsApp.Root", "WhatsApp" }, { "SearchHost", "Windows Search" }, { "code", "VS Code" },
            { "svchost", "Windows services" }, { "MsMpEng", "Windows Defender" }, { "dwm", "Desktop (dwm)" },
            { "explorer", "File Explorer" }, { "Memory Compression", "Memory compression" },
            { "OverwolfBrowser", "Overwolf" }, { "RiotClientServices", "Riot Client" },
            { "python", "Python" }, { "node", "Node.js" }, { "ollama", "Ollama" }, { "ollama app", "Ollama" },
            { "ollama_llama_server", "Ollama model" }
        };

        public static RamSnapshot Take(int top)
        {
            var snap = new RamSnapshot();
            var ms = new MemoryStatusEx();
            ms.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (GlobalMemoryStatusEx(ref ms)) { snap.Total = (long)ms.TotalPhys; snap.Free = (long)ms.AvailPhys; }

            // parent links, for "this job and everything it started"
            IntPtr h = CreateToolhelp32Snapshot(0x2, 0);   // TH32CS_SNAPPROCESS
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                try
                {
                    var e = new ProcessEntry();
                    e.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                    for (bool ok = Process32FirstW(h, ref e); ok; ok = Process32NextW(h, ref e))
                        snap.Parent[(int)e.ProcessId] = (int)e.ParentProcessId;
                }
                finally { CloseHandle(h); }
            }

            // WebView2 is a web engine other programs embed: its processes are counted
            // under the program that started them, not under their own name
            var names = new Dictionary<int, string>();
            var sizes = new List<KeyValuePair<int, long>>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == 0) continue;   // the idle "process"
                    names[p.Id] = p.ProcessName;
                    sizes.Add(new KeyValuePair<int, long>(p.Id, p.WorkingSet64));
                }
                catch (Exception) { }   // gone while we looked, or not ours to read
                finally { p.Dispose(); }
            }

            var groups = new Dictionary<string, RamApp>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in sizes)
            {
                int id = kv.Key;
                string own = names[id], key = own;
                if (own.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                {
                    int up = id;
                    for (int depth = 0; depth < 32; depth++)
                    {
                        int par;
                        string parName;
                        if (!snap.Parent.TryGetValue(up, out par) || !names.TryGetValue(par, out parName)) break;
                        if (!parName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) { key = parName; break; }
                        up = par;
                    }
                }
                snap.WorkingSet[id] = kv.Value;
                RamApp app;
                if (!groups.TryGetValue(key, out app))
                {
                    string shown;
                    if (!Friendly.TryGetValue(key, out shown)) shown = key.Length > 0 ? char.ToUpper(key[0]) + key.Substring(1) : key;
                    groups[key] = app = new RamApp { Name = shown, CanEnd = !Protected.Contains(key) };
                }
                app.Bytes += kv.Value;
                app.Count++;
                app.Pids.Add(id);
                app.Processes.Add(own);
            }

            var sorted = groups.Values.OrderByDescending(a => a.Bytes).ToList();
            snap.Top = sorted.Take(top).ToList();
            long shownBytes = snap.Top.Sum(a => a.Bytes);
            // everything in use that the rows above don't name: smaller programs and the system itself
            snap.OthersBytes = Math.Max(0, snap.Used - shownBytes);
            snap.OthersCount = sorted.Skip(top).Sum(a => a.Count);
            return snap;
        }
    }
}
