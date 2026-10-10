// JobTray: a taskbar (notification-area) indicator for long-running jobs.
//
// Any program or chat session reports a job by writing one small JSON file to
// the status folder (%USERPROFILE%\.job-status, or JOB_STATUS_DIR); see
// skills/job-steward/jobstatus.py for the fields. The icon shows the main job's
// progress as a ring; a click opens the list of all jobs. The tray also checks
// each job's process, so a job that dies silently shows up red instead of
// looking stuck.
//
// Built with the C# compiler that ships with Windows (.NET Framework 4), so it
// needs nothing installed and stays around 15 MB of RAM: see build.cmd.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("JobTray - background job status")]
[assembly: AssemblyProduct("JobTray")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace JobTray
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--autostart")
                Autostart.Set(args[1] == "on");
            bool fresh;
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\JobTray-show"))
            using (var mutex = new Mutex(true, "Local\\JobTray-single-instance", out fresh))
            {
                if (!fresh)
                {
                    // already in the tray: `JobTray.exe --show` opens its list
                    if (Array.IndexOf(args, "--show") >= 0) show.Set();
                    return;
                }
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string dir = Environment.GetEnvironmentVariable("JOB_STATUS_DIR");
                if (string.IsNullOrEmpty(dir))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".job-status");
                var app = new TrayApp(dir);
                ThreadPool.RegisterWaitForSingleObject(show, (state, timedOut) => app.ShowFromOtherThread(), null, -1, false);
                Application.Run(app);
            }
        }
    }

    static class Autostart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
                return k != null && k.GetValue("JobTray") != null;
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (on) k.SetValue("JobTray", "\"" + Application.ExecutablePath + "\"");
                else if (k.GetValue("JobTray") != null) k.DeleteValue("JobTray");
            }
        }
    }

    class Job
    {
        public string File, Id, Title, Project, Session, State, Message, Unit, Log, Output, Tail, Count, Success;
        public string Parent;     // the main job this is a step (sub-job) of
        public string Retry, RetryCwd;   // command line that runs the job again, and where
        public int Depth, Children;
        public double? Done, Total, Progress, EtaSeconds;
        public double Started, Updated, PidStarted;
        public int Pid;
        // worked out by the tray
        public string Shown;      // starting, running, waiting, done, failed, lost
        public string ShownMessage;
        public double? Eta;

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static Job Load(string file)
        {
            Dictionary<string, object> d;
            try
            {
                string text;
                // share everything, so a writer can replace the file while it is read
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8))
                    text = r.ReadToEnd();
                d = Json.Deserialize<Dictionary<string, object>>(text);
            }
            catch (Exception) { return null; }
            if (d == null) return null;
            var j = new Job();
            j.File = file;
            j.Id = Str(d, "id") ?? Path.GetFileNameWithoutExtension(file);
            j.Title = Str(d, "title") ?? j.Id;
            j.Project = Str(d, "project");
            j.Session = Str(d, "session");
            j.State = (Str(d, "state") ?? "running").ToLowerInvariant();
            j.Message = Str(d, "message");
            j.Unit = Str(d, "unit");
            j.Log = Str(d, "log");
            j.Output = Str(d, "output");
            j.Tail = Str(d, "tail");
            j.Count = Str(d, "count");
            j.Success = Str(d, "success");
            j.Parent = Str(d, "parent");
            j.Retry = Str(d, "retry");
            j.RetryCwd = Str(d, "retry_cwd");
            j.Done = Num(d, "done");
            j.Total = Num(d, "total");
            j.Progress = Num(d, "progress");
            j.EtaSeconds = Num(d, "eta_seconds");
            j.Started = Num(d, "started") ?? 0;
            j.Updated = Num(d, "updated") ?? Util.Epoch(System.IO.File.GetLastWriteTimeUtc(file));
            j.Pid = (int)(Num(d, "pid") ?? 0);
            j.PidStarted = Num(d, "pid_started") ?? 0;
            return j;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object o;
            if (!d.TryGetValue(key, out o) || o == null) return null;
            var s = Convert.ToString(o, CultureInfo.InvariantCulture);
            return s.Length == 0 ? null : s;
        }

        static double? Num(Dictionary<string, object> d, string key)
        {
            object o;
            if (!d.TryGetValue(key, out o) || o == null) return null;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        public bool Active { get { return Shown == "starting" || Shown == "running" || Shown == "waiting"; } }
        public bool Problem { get { return Shown == "failed" || Shown == "lost"; } }

        public double? Fraction
        {
            get
            {
                if (Done.HasValue && Total.HasValue && Total.Value > 0)
                    return Math.Max(0, Math.Min(1, Done.Value / Total.Value));
                if (Progress.HasValue) return Math.Max(0, Math.Min(1, Progress.Value));
                return null;
            }
        }
    }

    static class Util
    {
        static readonly DateTime Unix = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double Epoch(DateTime utc) { return (utc - Unix).TotalSeconds; }
        public static double Now() { return Epoch(DateTime.UtcNow); }

        public static bool Alive(int pid, double started)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    try
                    {
                        if (p.HasExited) return false;
                        // a reused pid belongs to some other, later process
                        if (started > 0 && Math.Abs(Epoch(p.StartTime.ToUniversalTime()) - started) > 3) return false;
                    }
                    catch (Exception) { }   // no right to look closer: it exists, so count it alive
                    return true;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public static string TailText(string path, int bytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - bytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start];
                    int n = 0;
                    while (n < buf.Length)
                    {
                        int r = fs.Read(buf, n, buf.Length - n);
                        if (r <= 0) break;
                        n += r;
                    }
                    return Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch (Exception) { return null; }
        }

        public static string LastLine(string path)
        {
            var text = TailText(path, 4096);
            if (text == null) return null;
            // \r too: progress bars redraw one line with carriage returns
            var lines = text.Split('\n', '\r');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i].Trim();
                if (l.Length > 0) return l.Length > 300 ? l.Substring(0, 300) : l;
            }
            return null;
        }

        public static int? CountFiles(string pattern)
        {
            try
            {
                string dir = Path.GetDirectoryName(pattern), name = Path.GetFileName(pattern);
                if (!Directory.Exists(dir)) return null;
                return Directory.EnumerateFiles(dir, name).Count();
            }
            catch (Exception) { return null; }
        }

        public static string Duration(double s)
        {
            if (s < 60) return "under a minute";
            int m = (int)Math.Round(s / 60);
            if (m < 60) return m + " min";
            return (m / 60) + " h " + (m % 60).ToString("00") + " min";
        }

        public static string Ago(double s)
        {
            if (s < 10) return "just now";
            if (s < 60) return (int)s + " s ago";
            return Duration(s) + " ago";
        }

        public static string Num(double v) { return v.ToString(v == Math.Floor(v) ? "0" : "0.#", CultureInfo.InvariantCulture); }

        public static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show("Could not open " + path + "\n\n" + e.Message, "JobTray"); }
        }

        public static bool LightTheme(string value)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue(value);
                    return v is int && (int)v == 1;
                }
            }
            catch (Exception) { return false; }
        }
    }

    class Theme
    {
        public Color Back, Card, Text, Sub, Track, Accent, Amber, Red, Green;

        public static Theme Get(bool light)
        {
            var t = new Theme();
            if (light)
            {
                t.Back = Color.FromArgb(243, 243, 243); t.Card = Color.White; t.Text = Color.FromArgb(26, 26, 26);
                t.Sub = Color.FromArgb(96, 96, 96); t.Track = Color.FromArgb(220, 220, 220);
                t.Accent = Color.FromArgb(0, 103, 192); t.Amber = Color.FromArgb(157, 93, 0);
                t.Red = Color.FromArgb(196, 43, 28); t.Green = Color.FromArgb(15, 123, 15);
            }
            else
            {
                t.Back = Color.FromArgb(32, 32, 32); t.Card = Color.FromArgb(45, 45, 45); t.Text = Color.FromArgb(243, 243, 243);
                t.Sub = Color.FromArgb(170, 170, 170); t.Track = Color.FromArgb(70, 70, 70);
                t.Accent = Color.FromArgb(76, 194, 255); t.Amber = Color.FromArgb(252, 185, 0);
                t.Red = Color.FromArgb(255, 110, 100); t.Green = Color.FromArgb(108, 203, 95);
            }
            return t;
        }

        public Color For(string shown)
        {
            switch (shown)
            {
                case "waiting": return Amber;
                case "done": return Green;
                case "failed": case "lost": return Red;
                case "skipped": return Sub;
                default: return Accent;
            }
        }

        public static string Label(string shown)
        {
            switch (shown)
            {
                case "starting": return "Starting";
                case "waiting": return "Waiting";
                case "done": return "Done";
                case "failed": return "Failed";
                case "lost": return "Stopped unexpectedly";
                case "skipped": return "Not run";
                default: return "Running";
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        readonly string dir;
        readonly NotifyIcon icon = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly FileSystemWatcher watcher;
        readonly MenuItem autostartItem;
        readonly Dictionary<string, string> lastShown = new Dictionary<string, string>();
        readonly Dictionary<string, double[]> firstSample = new Dictionary<string, double[]>();   // id -> {time, done}
        List<Job> jobs = new List<Job>();
        volatile bool dirty = true;
        bool firstLoad = true;
        int tick;
        Popup popup;
        IntPtr iconHandle = IntPtr.Zero;
        string iconSig;
        readonly Control ui = new Control();   // a handle on the UI thread, for calls from other threads

        public TrayApp(string dir)
        {
            this.dir = dir;
            Directory.CreateDirectory(dir);
            GC.KeepAlive(ui.Handle);   // creates the handle now, on this thread

            var menu = new ContextMenu();
            menu.MenuItems.Add("Show jobs", delegate { ShowPopup(); }).DefaultItem = true;
            menu.MenuItems.Add("Clear finished", delegate { ClearFinished(); });
            menu.MenuItems.Add("Open status folder", delegate { Util.Open(this.dir); });
            autostartItem = menu.MenuItems.Add("Start with Windows", delegate
            {
                Autostart.Set(!Autostart.IsOn());
                autostartItem.Checked = Autostart.IsOn();
            });
            autostartItem.Checked = Autostart.IsOn();
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Exit", delegate { ExitThread(); });
            icon.ContextMenu = menu;
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
            icon.BalloonTipClicked += delegate { ShowPopup(); };

            watcher = new FileSystemWatcher(dir, "*.json");
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;
            FileSystemEventHandler changed = (s, e) => dirty = true;
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (s, e) => dirty = true;
            watcher.EnableRaisingEvents = true;

            Refresh();
            icon.Visible = true;
            // every second: react to a changed file; every 5 s: check processes, tails and counts
            timer.Interval = 1000;
            timer.Tick += delegate { tick++; if (dirty || tick % 5 == 0) Refresh(); };
            timer.Start();
        }

        void Refresh()
        {
            dirty = false;
            double now = Util.Now();
            var list = new List<Job>();
            string[] files;
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch (Exception) { files = new string[0]; }
            foreach (var f in files)
            {
                var j = Job.Load(f);
                if (j == null)
                {
                    // half-written or unreadable for a moment: keep what we had
                    var prev = jobs.FirstOrDefault(p => p.File == f);
                    if (prev != null) list.Add(prev);
                    continue;
                }
                Evaluate(j, now);
                double age = now - j.Updated;
                // finished jobs leave the list after a day, unexplained stops after three
                if ((j.Shown == "done" || j.Shown == "failed") && age > 86400 || j.Shown == "lost" && age > 3 * 86400)
                {
                    try { System.IO.File.Delete(f); } catch (Exception) { }
                    continue;
                }
                list.Add(j);
            }
            list.Sort((a, b) =>
            {
                int ra = Rank(a), rb = Rank(b);
                return ra != rb ? ra.CompareTo(rb) : b.Updated.CompareTo(a.Updated);
            });
            list = Nest(list);
            // a step still waiting when its main job has ended will never run
            foreach (var j in list)
            {
                if (!j.Active || j.Parent == null || j.Pid > 0) continue;
                var p = list.FirstOrDefault(k => k.Id == j.Parent && k != j);
                if (p != null && !p.Active) { j.Shown = "skipped"; j.Eta = null; }
            }
            Notify(list);
            jobs = list;
            UpdateIcon();
            if (popup != null && popup.Visible) popup.SetJobs(jobs);
        }

        // Main jobs in order, each followed by its sub-jobs (steps whose "parent"
        // is its id). A sub-job whose main job isn't listed stands on its own.
        static List<Job> Nest(List<Job> list)
        {
            var ids = new HashSet<string>(list.Select(j => j.Id));
            var ordered = new List<Job>();
            var placed = new HashSet<Job>();
            Action<Job, int> add = null;
            add = (j, depth) =>
            {
                if (!placed.Add(j)) return;
                j.Depth = depth;
                ordered.Add(j);
                var kids = list.Where(k => k.Parent == j.Id && k != j).ToList();
                j.Children = kids.Count;
                foreach (var k in kids) if (depth < 3) add(k, depth + 1);
            };
            foreach (var j in list.Where(j => j.Parent == null || j.Parent == j.Id || !ids.Contains(j.Parent)))
                add(j, 0);
            foreach (var j in list) add(j, 0);   // anything left over (a loop of parents)
            return ordered;
        }

        static int Rank(Job j)
        {
            switch (j.Shown)
            {
                case "lost": case "failed": return 0;
                case "running": case "starting": return 1;
                case "waiting": return 2;
                default: return 3;
            }
        }

        void Evaluate(Job j, double now)
        {
            if (j.Count != null)
            {
                int? c = Util.CountFiles(j.Count);
                if (c.HasValue) j.Done = c.Value;
            }
            string tail = j.Tail != null ? Util.LastLine(j.Tail) : null;
            j.ShownMessage = tail ?? j.Message;

            string st = j.State;
            if (st == "queued" || st == "paused" || st == "hold" || st == "on hold") st = "waiting";
            else if (st == "error") st = "failed";
            else if (st == "finished" || st == "complete" || st == "completed" || st == "success") st = "done";
            else if (st != "starting" && st != "waiting" && st != "done" && st != "failed") st = "running";
            bool active = st != "done" && st != "failed";

            if (active && j.Pid > 0 && !Util.Alive(j.Pid, j.PidStarted))
            {
                // the job's process is gone without saying it finished
                st = "lost";
                if (j.Success != null)
                {
                    string end = Util.TailText(j.Log ?? j.Tail, 16384);
                    try
                    {
                        if (end != null && Regex.IsMatch(end, j.Success, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                            st = "done";
                    }
                    catch (ArgumentException) { }
                }
                active = false;
            }
            if (active && tail != null && Regex.IsMatch(tail, @"\b(on hold|paused|waiting|queued)\b", RegexOptions.IgnoreCase))
                st = "waiting";
            j.Shown = st;

            j.Eta = null;
            if (active && j.EtaSeconds.HasValue)
                j.Eta = Math.Max(0, j.EtaSeconds.Value - (now - j.Updated));
            else if (active && j.Done.HasValue && j.Total.HasValue && j.Total.Value > j.Done.Value)
            {
                // the rate since this tray first saw the job (waits included, so it stays honest)
                double[] s;
                if (!firstSample.TryGetValue(j.Id, out s) || j.Done.Value < s[1])
                    firstSample[j.Id] = s = new[] { now, j.Done.Value };
                double gained = j.Done.Value - s[1];
                if (gained >= 1 && now - s[0] > 30)
                    j.Eta = (j.Total.Value - j.Done.Value) * (now - s[0]) / gained;
            }
        }

        void Notify(List<Job> list)
        {
            var seen = new Dictionary<string, string>();
            foreach (var j in list)
            {
                seen[j.Id] = j.Shown;
                string before;
                lastShown.TryGetValue(j.Id, out before);
                if (firstLoad || before == j.Shown) continue;
                if (before == null && j.Active) continue;
                string text = null;
                if (j.Shown == "done") text = "Finished." + (j.ShownMessage != null ? " " + j.ShownMessage : "");
                else if (j.Shown == "failed") text = "Failed. " + (j.ShownMessage ?? "See its log.");
                else if (j.Shown == "lost") text = "Its process stopped without finishing. Last: " + (j.ShownMessage ?? "(no message)");
                if (text != null && j.Problem && j.Retry != null)
                    text = Trim(text, 200) + " Click here for Retry.";
                if (text != null)
                    icon.ShowBalloonTip(8000, j.Title, Trim(text, 250), j.Shown == "done" ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            lastShown.Clear();
            foreach (var kv in seen) lastShown[kv.Key] = kv.Value;
            firstLoad = false;
        }

        static string Trim(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 1) + "…"; }

        void UpdateIcon()
        {
            var problem = jobs.FirstOrDefault(j => j.Problem);
            // the ring follows a job with a count (usually a sub-job: the step under way)
            var main = jobs.FirstOrDefault(j => j.Active && j.Fraction.HasValue)
                       ?? jobs.FirstOrDefault(j => j.Shown == "running" || j.Shown == "starting")
                       ?? jobs.FirstOrDefault(j => j.Shown == "waiting");
            var done = jobs.FirstOrDefault(j => j.Shown == "done");
            string kind;
            double? frac = null;
            if (problem != null) kind = "problem";
            else if (main != null) { kind = main.Shown == "waiting" ? "waiting" : "running"; frac = main.Fraction; }
            else if (done != null) kind = "done";
            else kind = "idle";

            bool light = Util.LightTheme("SystemUsesLightTheme");
            // no known progress: a short arc that moves on every refresh, to show it's alive
            int phase = frac.HasValue ? 0 : (tick / 5) % 8;
            string sig = kind + "|" + (frac.HasValue ? Math.Round(frac.Value * 50).ToString() : "-") + "|" + phase + "|" + light;
            if (sig != iconSig)
            {
                iconSig = sig;
                var t = Theme.Get(light);
                int size = SystemInformation.SmallIconSize.Width;
                using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp)) DrawIcon(g, size, kind, frac, phase, t, light);
                    IntPtr h = bmp.GetHicon();
                    var old = icon.Icon;
                    icon.Icon = Icon.FromHandle(h);
                    if (old != null) old.Dispose();
                    if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                    iconHandle = h;
                }
            }

            string tip;
            if (jobs.Count == 0) tip = "JobTray: nothing running";
            else
            {
                var first = problem ?? main ?? done ?? jobs[0];
                tip = first.Title + ": " + Theme.Label(first.Shown).ToLowerInvariant();
                if (first.Active && first.Fraction.HasValue) tip += ", " + (int)Math.Round(first.Fraction.Value * 100) + "%";
                if (first.Eta.HasValue) tip += ", ~" + Util.Duration(first.Eta.Value) + " left";
                if (jobs.Count > 1) tip += " (+" + (jobs.Count - 1) + " more)";
            }
            icon.Text = Trim(tip, 63);   // the tooltip limit in .NET Framework
        }

        static void DrawIcon(Graphics g, int size, string kind, double? frac, int phase, Theme t, bool light)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float w = Math.Max(2f, size / 6f);
            var r = new RectangleF(w / 2 + 0.5f, w / 2 + 0.5f, size - w - 1, size - w - 1);
            Color track = light ? Color.FromArgb(90, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255);
            if (kind == "problem" || kind == "done")
            {
                var full = new RectangleF(0.5f, 0.5f, size - 1, size - 1);
                using (var b = new SolidBrush(kind == "done" ? t.Green : t.Red)) g.FillEllipse(b, full);
                using (var p = new Pen(light ? Color.White : Color.Black, Math.Max(1.5f, size / 8f)))
                {
                    p.StartCap = p.EndCap = LineCap.Round;
                    float s = size;
                    if (kind == "done")
                        g.DrawLines(p, new[] { new PointF(s * .28f, s * .52f), new PointF(s * .44f, s * .68f), new PointF(s * .72f, s * .34f) });
                    else
                    {
                        g.DrawLine(p, s / 2, s * .25f, s / 2, s * .55f);
                        g.DrawLine(p, s / 2, s * .74f, s / 2, s * .75f);
                    }
                }
                return;
            }
            using (var p = new Pen(track, w)) g.DrawEllipse(p, r);
            if (kind == "idle") return;
            using (var p = new Pen(kind == "waiting" ? t.Amber : t.Accent, w))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                if (frac.HasValue) g.DrawArc(p, r, -90, (float)Math.Max(12, 360 * frac.Value));
                else g.DrawArc(p, r, -90 + phase * 45, 90);
            }
        }

        public void ShowFromOtherThread()
        {
            ui.BeginInvoke((Action)ShowPopup);
        }

        void TogglePopup()
        {
            // a click on the icon first takes focus from the open list, which hides it
            if (popup != null && (popup.Visible || (DateTime.Now - popup.HiddenAt).TotalMilliseconds < 300))
            {
                popup.Hide();
                return;
            }
            ShowPopup();
        }

        void ShowPopup()
        {
            if (popup == null)
            {
                popup = new Popup();
                popup.Dismiss += job =>
                {
                    // a main job takes its finished sub-jobs with it
                    foreach (var j in jobs.Where(j => j == job || j.Parent == job.Id && !j.Active))
                        try { System.IO.File.Delete(j.File); } catch (Exception) { }
                    dirty = true;
                };
                popup.Retry += RetryJob;
            }
            popup.SetJobs(jobs);
            popup.ShowNear();
        }

        void RetryJob(Job job)
        {
            popup.Hide();
            var answer = MessageBox.Show("Run \"" + job.Title + "\" again?\n\n" + job.Retry, "JobTray",
                                         MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;
            // clear the failed rows; the new run writes fresh ones
            foreach (var j in jobs.Where(j => (j == job || j.Parent == job.Id) && !j.Active))
                try { System.IO.File.Delete(j.File); } catch (Exception) { }
            try
            {
                // through cmd so any command line works; no window, and not tied to
                // this tray: it keeps running if the tray is closed
                var psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + job.Retry + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                if (job.RetryCwd != null && Directory.Exists(job.RetryCwd)) psi.WorkingDirectory = job.RetryCwd;
                Process.Start(psi).Dispose();
                icon.ShowBalloonTip(5000, job.Title, "Started again.", ToolTipIcon.Info);
            }
            catch (Exception e)
            {
                MessageBox.Show("Could not start it again:\n\n" + e.Message, "JobTray");
            }
            dirty = true;
        }

        void ClearFinished()
        {
            foreach (var j in jobs.Where(j => !j.Active))
                try { System.IO.File.Delete(j.File); } catch (Exception) { }
            dirty = true;
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            watcher.Dispose();
            icon.Visible = false;
            icon.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            base.ExitThreadCore();
        }
    }

    class Popup : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public event Action<Job> Dismiss;
        public event Action<Job> Retry;
        public DateTime HiddenAt = DateTime.MinValue;
        List<Job> jobs = new List<Job>();
        readonly List<KeyValuePair<Rectangle, Action>> links = new List<KeyValuePair<Rectangle, Action>>();
        readonly float s;
        readonly Font titleFont, bodyFont, smallFont, headFont;
        Theme t;
        const int MaxRows = 6;

        public Popup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            using (var g = CreateGraphics()) s = g.DpiX / 96f;
            headFont = new Font("Segoe UI Semibold", 10.5f);
            titleFont = new Font("Segoe UI Semibold", 9.5f);
            bodyFont = new Font("Segoe UI", 9f);
            smallFont = new Font("Segoe UI", 8f);
            Deactivate += delegate { Hide(); };
            VisibleChanged += delegate { if (!Visible) HiddenAt = DateTime.Now; };
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
            MouseMove += (o, e) => Cursor = links.Any(l => l.Key.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
            MouseClick += (o, e) =>
            {
                foreach (var l in links.ToList())
                    if (l.Key.Contains(e.Location)) { l.Value(); return; }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                cp.ExStyle |= 0x80;         // WS_EX_TOOLWINDOW: no taskbar button, no Alt+Tab
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2;   // Windows 11 rounded corners
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch (Exception) { }
        }

        int P(float v) { return (int)Math.Round(v * s); }

        int RowHeight(Job j)
        {
            int h = 22 + 18 + 18;                       // title, detail, footer
            if (j.Fraction.HasValue) h += 12;   // bar
            if (j.ShownMessage != null) h += 18;
            return P(h + 20);
        }

        public void SetJobs(List<Job> list)
        {
            jobs = list;
            t = Theme.Get(Util.LightTheme("AppsUseLightTheme"));
            BackColor = t.Back;
            var cards = Cards();
            int h = P(52) + cards.Take(MaxRows).Sum(c => c.Sum(j => RowHeight(j)) + P(8)) + P(cards.Count > MaxRows ? 28 : 6);
            if (jobs.Count == 0) h = P(52 + 70);
            int w = P(400);
            if (Width != w || Height != h)
            {
                Size = new Size(w, h);
                if (Visible) Place();
            }
            Invalidate();
        }

        public void ShowNear()
        {
            Place();
            Show();
            Activate();
        }

        void Place()
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.Right - Width - P(12), wa.Bottom - Height - P(12));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            links.Clear();
            int pad = P(14), x = pad, width = Width - 2 * pad;
            double now = Util.Now();
            Draw(g, "Background jobs", headFont, t.Text, x, P(14), width);
            Draw(g, "live", smallFont, t.Sub, x, P(17), width, TextFormatFlags.Right);
            int y = P(48);
            if (jobs.Count == 0)
            {
                Draw(g, "Nothing running.", bodyFont, t.Text, x, y + P(6), width);
                Draw(g, "Long jobs started from any Claude session or script show up here.", smallFont, t.Sub, x, y + P(28), width);
                return;
            }
            var cards = Cards();
            foreach (var card in cards.Take(MaxRows))
            {
                // one card per main job; its sub-jobs (steps) sit indented inside it
                int h = card.Sum(j => RowHeight(j));
                using (var path = Round(new Rectangle(x - P(4), y, width + P(8), h), P(8)))
                using (var b = new SolidBrush(t.Card)) g.FillPath(b, path);
                int top = y;
                foreach (var j in card)
                {
                    int indent = P(18) * j.Depth;
                    if (j.Depth > 0)
                    {
                        using (var p = new Pen(t.Track, P(2)))
                        {
                            g.DrawLine(p, x + indent - P(8), y + P(2), x + indent - P(8), y + RowHeight(j) - P(10));
                            g.DrawLine(p, x + P(8), y - P(1), x + width - P(4), y - P(1));   // divider from the row above
                        }
                    }
                    DrawJob(g, j, x + P(8) + indent, y + P(10), width - P(8) - indent, now);
                    y += RowHeight(j);
                }
                y = top + h + P(8);
            }
            if (cards.Count > MaxRows)
                Draw(g, "+" + (cards.Count - MaxRows) + " more (Open status folder from the right-click menu)", smallFont, t.Sub, x, y, width);
        }

        // the flat, nested list cut into cards: a main job and the sub-jobs after it
        List<List<Job>> Cards()
        {
            var cards = new List<List<Job>>();
            foreach (var j in jobs)
            {
                if (j.Depth == 0 || cards.Count == 0) cards.Add(new List<Job>());
                cards[cards.Count - 1].Add(j);
            }
            return cards;
        }

        void DrawJob(Graphics g, Job j, int x, int y, int width, double now)
        {
            Color c = t.For(j.Shown);
            // state chip on the right, title on the left
            string label = Theme.Label(j.Shown);
            var chip = TextRenderer.MeasureText(label, smallFont);
            var chipRect = new Rectangle(x + width - chip.Width - P(12), y, chip.Width + P(10), P(19));
            using (var path = Round(chipRect, P(9)))
            using (var b = new SolidBrush(Color.FromArgb(40, c))) g.FillPath(b, path);
            TextRenderer.DrawText(g, label, smallFont, chipRect, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            Draw(g, j.Title, titleFont, t.Text, x, y, width - chipRect.Width - P(20));
            y += P(22);

            double? f = j.Fraction;
            if (f.HasValue)
            {
                var bar = new Rectangle(x, y + P(2), width - P(4), P(5));
                using (var path = Round(bar, P(2)))
                using (var b = new SolidBrush(t.Track)) g.FillPath(b, path);
                if (f.HasValue && f.Value > 0)
                {
                    var fill = new Rectangle(bar.X, bar.Y, Math.Max(P(5), (int)(bar.Width * f.Value)), bar.Height);
                    using (var path = Round(fill, P(2)))
                    using (var b = new SolidBrush(c)) g.FillPath(b, path);
                }
                y += P(12);
            }

            var detail = new List<string>();
            if (j.Done.HasValue && j.Total.HasValue)
                detail.Add(Util.Num(Math.Min(j.Done.Value, j.Total.Value)) + " of " + Util.Num(j.Total.Value) + (j.Unit != null ? " " + j.Unit : ""));
            else if (j.Done.HasValue)
                detail.Add(Util.Num(j.Done.Value) + (j.Unit != null ? " " + j.Unit : "") + " done");
            if (f.HasValue) detail.Add((int)Math.Round(f.Value * 100) + "%");
            if (j.Eta.HasValue) detail.Add("about " + Util.Duration(j.Eta.Value) + " left");
            if (j.Active && j.Started > 0) detail.Add((j.Shown == "waiting" ? "started " : "running ") + Util.Duration(now - j.Started) + (j.Shown == "waiting" ? " ago" : ""));
            if (j.Shown == "lost") detail.Add("its process is gone; check the log");
            if (detail.Count == 0) detail.Add(Theme.Label(j.Shown));
            Draw(g, string.Join("  ·  ", detail), bodyFont, t.Text, x, y, width);
            y += P(18);

            if (j.ShownMessage != null)
            {
                Draw(g, j.ShownMessage, smallFont, t.Sub, x, y + P(1), width);
                y += P(18);
            }

            // footer: where it came from and how fresh, then links
            var who = new List<string>();
            if (j.Depth > 0) who.Add("sub-job");
            else if (j.Children > 0) who.Add("main job, " + j.Children + (j.Children == 1 ? " sub-job" : " sub-jobs"));
            if (j.Project != null && j.Depth == 0) who.Add(j.Project);
            if (j.Session != null) who.Add(j.Session);
            who.Add("updated " + Util.Ago(now - j.Updated));
            int right = x + width;
            var actions = new List<KeyValuePair<string, Action>>();
            // a failed job that knows its command can run it again. A failed step
            // (sub-job) usually fails its main job too, and that row has the button.
            if (j.Problem && j.Retry != null)
                actions.Add(new KeyValuePair<string, Action>("Retry", () => { if (Retry != null) Retry(j); }));
            if (!j.Active || j.Shown == "lost")
                actions.Add(new KeyValuePair<string, Action>("Dismiss", () => { if (Dismiss != null) Dismiss(j); }));
            if (j.Output != null && j.Shown == "done") actions.Add(new KeyValuePair<string, Action>("Open result", () => Util.Open(j.Output)));
            if (j.Log != null) actions.Add(new KeyValuePair<string, Action>("Log", () => Util.Open(j.Log)));
            foreach (var a in actions)
            {
                var sz = TextRenderer.MeasureText(a.Key, smallFont);
                var rect = new Rectangle(right - sz.Width, y + P(2), sz.Width, sz.Height);
                if (a.Key.StartsWith("Retry"))
                {
                    // a real button, not a link: it's the thing to do on a failed row
                    rect = new Rectangle(right - sz.Width - P(16), y - P(2), sz.Width + P(16), sz.Height + P(6));
                    using (var path = Round(rect, P(5)))
                    using (var b = new SolidBrush(t.Accent)) g.FillPath(b, path);
                    TextRenderer.DrawText(g, a.Key, smallFont, rect, t.Card,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                else
                    TextRenderer.DrawText(g, a.Key, smallFont, rect.Location, t.Accent, TextFormatFlags.NoPrefix);
                links.Add(new KeyValuePair<Rectangle, Action>(rect, a.Value));
                right -= rect.Width + P(12);
            }
            Draw(g, string.Join("  ·  ", who), smallFont, t.Sub, x, y + P(2), right - x - P(4));
        }

        static void Draw(Graphics g, string text, Font font, Color color, int x, int y, int width, TextFormatFlags extra = 0)
        {
            if (width <= 0) return;
            TextRenderer.DrawText(g, text, font, new Rectangle(x, y, width, font.Height + 4), color,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | extra);
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
