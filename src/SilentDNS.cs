// SilentDNS for Windows — tiny system-tray DNS changer.
// Target: .NET Framework 4.8 (built into Windows 10/11). No installer, no dependencies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("SilentDNS")]
[assembly: System.Reflection.AssemblyProduct("SilentDNS for Windows")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace SilentDNS
{
    class Preset
    {
        public string Name, P4, S4, P6, S6, DohP, DohS;
        public Preset(string n, string p4, string s4, string p6, string s6, string dohP, string dohS)
        { Name = n; P4 = p4; S4 = s4; P6 = p6; S6 = s6; DohP = dohP; DohS = dohS; }
    }

    static class Presets
    {
        const string CF = "https://cloudflare-dns.com/dns-query";
        const string GG = "https://dns.google/dns-query";
        const string Q9 = "https://dns.quad9.net/dns-query";
        const string AG = "https://dns.adguard-dns.com/dns-query";

        public static readonly Preset[] All = {
            new Preset("Cloudflare + Google", "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111", "2001:4860:4860::8888", CF, GG),
            new Preset("Cloudflare",          "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001", CF, CF),
            new Preset("Google",              "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844", GG, GG),
            new Preset("Quad9",               "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9", Q9, Q9),
            new Preset("AdGuard (blocks ads)","94.140.14.14", "94.140.15.15", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff", AG, AG),
        };

        // DoH template Windows should use for a given server IP (known providers only)
        public static string TemplateFor(string ip)
        {
            foreach (var p in All)
            {
                if (ip == p.P4 || ip == p.P6) return p.DohP;
                if (ip == p.S4 || ip == p.S6) return p.DohS;
            }
            return null;
        }
    }

    class Config
    {
        public bool Enabled = true;
        public string Preset = "Cloudflare + Google";
        public string P4 = "1.1.1.1", S4 = "8.8.8.8";
        public string P6 = "2606:4700:4700::1111", S6 = "2001:4860:4860::8888";
        public bool Encrypted = true;
        public bool StartWithWindows = true;
        public bool FirstRun = true;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SilentDNS"); } }
        static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

        public static Config Load()
        {
            var c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                c.FirstRun = false;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "Enabled": c.Enabled = v == "1"; break;
                        case "Preset": c.Preset = v; break;
                        case "Primary": c.P4 = v; break;
                        case "Secondary": c.S4 = v; break;
                        case "PrimaryV6": c.P6 = v; break;
                        case "SecondaryV6": c.S6 = v; break;
                        case "Encrypted": c.Encrypted = v == "1"; break;
                        case "StartWithWindows": c.StartWithWindows = v == "1"; break;
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[] {
                    "Enabled=" + (Enabled ? "1" : "0"),
                    "Preset=" + Preset,
                    "Primary=" + P4, "Secondary=" + S4,
                    "PrimaryV6=" + P6, "SecondaryV6=" + S6,
                    "Encrypted=" + (Encrypted ? "1" : "0"),
                    "StartWithWindows=" + (StartWithWindows ? "1" : "0"),
                });
            }
            catch { }
        }
    }

    static class Dns
    {
        public static string Run(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    return o;
                }
            }
            catch (Exception e) { return e.Message; }
        }

        static string Netsh { get { return Path.Combine(Environment.SystemDirectory, "netsh.exe"); } }

        // Real, connected network adapters (Wi-Fi, Ethernet, USB tethering) — skips loopback, VPN tunnels, hotspot host adapters
        public static List<NetworkInterface> ActiveAdapters()
        {
            var list = new List<NetworkInterface>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback || n.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var ip = n.GetIPProperties();
                    if (ip.GatewayAddresses.Count == 0) continue; // no internet route (e.g. hotspot host side)
                    list.Add(n);
                }
                catch { }
            }
            return list;
        }

        static bool Valid(string s, AddressFamily fam)
        {
            IPAddress a;
            return !string.IsNullOrEmpty(s) && IPAddress.TryParse(s, out a) && a.AddressFamily == fam;
        }

        public static int Apply(Config c, bool force)
        {
            int changed = 0;
            var want4 = new List<string>();
            if (Valid(c.P4, AddressFamily.InterNetwork)) want4.Add(c.P4);
            if (Valid(c.S4, AddressFamily.InterNetwork)) want4.Add(c.S4);
            var want6 = new List<string>();
            if (Valid(c.P6, AddressFamily.InterNetworkV6)) want6.Add(c.P6);
            if (Valid(c.S6, AddressFamily.InterNetworkV6)) want6.Add(c.S6);

            foreach (var n in ActiveAdapters())
            {
                IPInterfaceProperties ip;
                try { ip = n.GetIPProperties(); } catch { continue; }
                var cur = ip.DnsAddresses.Select(a => a.ToString().Split('%')[0]).ToList();

                // IPv4
                try
                {
                    int idx = ip.GetIPv4Properties().Index;
                    var cur4 = cur.Where(x => !x.Contains(":")).ToList();
                    if (want4.Count > 0 && (force || !cur4.SequenceEqual(want4)))
                    {
                        Run(Netsh, "interface ipv4 set dnsservers name=" + idx + " source=static address=" + want4[0] + " register=primary validate=no");
                        for (int i = 1; i < want4.Count; i++)
                            Run(Netsh, "interface ipv4 add dnsservers name=" + idx + " address=" + want4[i] + " index=" + (i + 1) + " validate=no");
                        changed++;
                    }
                }
                catch { }

                // IPv6 — so Windows can't quietly fall back to the ISP's IPv6 DNS
                try
                {
                    var v6 = ip.GetIPv6Properties();
                    if (v6 == null) continue;
                    int idx6 = v6.Index;
                    var cur6 = cur.Where(x => x.Contains(":") && !x.StartsWith("fec0", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (want6.Count > 0 && (force || !cur6.SequenceEqual(want6)))
                    {
                        Run(Netsh, "interface ipv6 set dnsservers name=" + idx6 + " source=static address=" + want6[0] + " register=primary validate=no");
                        for (int i = 1; i < want6.Count; i++)
                            Run(Netsh, "interface ipv6 add dnsservers name=" + idx6 + " address=" + want6[i] + " index=" + (i + 1) + " validate=no");
                        changed++;
                    }
                }
                catch { }
            }

            if (c.Encrypted) EnableDoh(want4.Concat(want6));
            if (changed > 0 || force) Run("ipconfig.exe", "/flushdns");
            return changed;
        }

        public static void Reset()
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var ip = n.GetIPProperties();
                    try { Run(Netsh, "interface ipv4 set dnsservers name=" + ip.GetIPv4Properties().Index + " source=dhcp"); } catch { }
                    try { var v6 = ip.GetIPv6Properties(); if (v6 != null) Run(Netsh, "interface ipv6 set dnsservers name=" + v6.Index + " source=dhcp"); } catch { }
                }
                catch { }
            }
            Run("ipconfig.exe", "/flushdns");
        }

        // Windows 11 built-in DNS-over-HTTPS: register the template and turn on auto-upgrade
        static void EnableDoh(IEnumerable<string> servers)
        {
            foreach (var s in servers)
            {
                string t = Presets.TemplateFor(s);
                if (t == null) continue;
                string args = " encryption server=" + s + " dohtemplate=" + t + " autoupgrade=yes udpfallback=no";
                string o = Run(Netsh, "dns add" + args);
                if (o.IndexOf("exist", StringComparison.OrdinalIgnoreCase) >= 0 || o.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0)
                    Run(Netsh, "dns set" + args);
            }
            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters"))
                    k.SetValue("EnableAutoDoh", 2, RegistryValueKind.DWord);
            }
            catch { }
        }

        // Live DNS on the first connected adapter: [0] = adapter + IPv4, [1] = IPv6
        public static string[] CurrentSummary()
        {
            var a = ActiveAdapters();
            if (a.Count == 0) return new[] { "No network connected", "" };
            try
            {
                var all = a[0].GetIPProperties().DnsAddresses;
                var d4 = all.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()).ToList();
                var d6 = all.Where(x => x.AddressFamily == AddressFamily.InterNetworkV6 && !x.IsIPv6SiteLocal)
                            .Select(x => x.ToString().Split('%')[0]).ToList();
                return new[] {
                    a[0].Name + "  IPv4: " + (d4.Count == 0 ? "—" : string.Join(", ", d4)),
                    "IPv6: " + (d6.Count == 0 ? "— (IPv6 not in use on this network)" : string.Join(", ", d6))
                };
            }
            catch { return new[] { a[0].Name, "" }; }
        }
    }

    static class Startup
    {
        const string TaskName = "SilentDNS";

        // Task Scheduler (not the Run key) so it starts elevated at logon with no UAC prompt,
        // and never gets stopped on battery or after 72 hours.
        public static void Enable()
        {
            string exe = Application.ExecutablePath;
            string user = WindowsIdentity.GetCurrent().Name;
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>SilentDNS — keeps your DNS set</Description></RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>7</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + Esc(exe) + "</Command><Arguments>--tray</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string tmp = Path.Combine(Path.GetTempPath(), "silentdns_task.xml");
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            Dns.Run("schtasks.exe", "/Create /TN " + TaskName + " /XML \"" + tmp + "\" /F");
            try { File.Delete(tmp); } catch { }
        }

        public static void Disable() { Dns.Run("schtasks.exe", "/Delete /TN " + TaskName + " /F"); }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }
    }

    class SettingsForm : Form
    {
        ComboBox preset = new ComboBox();
        TextBox p4 = new TextBox(), s4 = new TextBox(), p6 = new TextBox(), s6 = new TextBox();
        CheckBox doh = new CheckBox(), startup = new CheckBox();
        Config cfg;

        public SettingsForm(Config c)
        {
            cfg = c;
            Text = "SilentDNS";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(340, 344);
            Font = new Font("Segoe UI", 9f);
            Icon = TrayApp.MakeIcon(true, 32);

            int y = 14;
            Add(new Label { Text = "Preset", Left = 14, Top = y + 3, Width = 90 });
            preset.DropDownStyle = ComboBoxStyle.DropDownList;
            preset.SetBounds(110, y, 214, 24);
            foreach (var p in Presets.All) preset.Items.Add(p.Name);
            preset.Items.Add("Custom");
            preset.SelectedItem = preset.Items.Contains(c.Preset) ? c.Preset : "Custom";
            preset.SelectedIndexChanged += delegate { FillFromPreset(); };
            Add(preset);

            y += 34; Header("IPv4", ref y);
            Row("Primary DNS", p4, c.P4, ref y);
            Row("Secondary DNS", s4, c.S4, ref y);
            Header("IPv6", ref y);
            Row("Primary DNS", p6, c.P6, ref y);
            Row("Secondary DNS", s6, c.S6, ref y);

            doh.Text = "Encrypted DNS (DoH, Windows 11)"; doh.Checked = c.Encrypted; doh.SetBounds(14, y + 4, 300, 22); Add(doh); y += 26;
            startup.Text = "Start with Windows (system tray)"; startup.Checked = c.StartWithWindows; startup.SetBounds(14, y + 4, 300, 22); Add(startup); y += 36;

            var save = new Button { Text = "Save && Apply", Width = 110, Height = 28, Left = 214, Top = y };
            save.Click += delegate { OnSave(); };
            var cancel = new Button { Text = "Cancel", Width = 80, Height = 28, Left = 126, Top = y };
            cancel.Click += delegate { Close(); };
            Add(save); Add(cancel);
            AcceptButton = save; CancelButton = cancel;
            foreach (var t in new[] { p4, s4, p6, s6 }) t.TextChanged += delegate { if (t.Focused) preset.SelectedItem = "Custom"; };
        }

        void Add(Control c) { Controls.Add(c); }
        void Header(string text, ref int y)
        {
            Add(new Label { Text = text, Left = 14, Top = y, Width = 310, Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Color.FromArgb(22, 140, 80) });
            y += 22;
        }
        void Row(string label, TextBox box, string val, ref int y)
        {
            Add(new Label { Text = label, Left = 14, Top = y + 3, Width = 95 });
            box.SetBounds(110, y, 214, 24); box.Text = val; Add(box); y += 32;
        }

        void FillFromPreset()
        {
            var p = Presets.All.FirstOrDefault(x => x.Name == (string)preset.SelectedItem);
            if (p == null) return;
            p4.Text = p.P4; s4.Text = p.S4; p6.Text = p.P6; s6.Text = p.S6;
        }

        void OnSave()
        {
            IPAddress a;
            if (!IPAddress.TryParse(p4.Text.Trim(), out a) || a.AddressFamily != AddressFamily.InterNetwork)
            { MessageBox.Show("Primary DNS must be a valid IPv4 address, e.g. 1.1.1.1", "SilentDNS"); return; }
            if (s4.Text.Trim().Length > 0 && (!IPAddress.TryParse(s4.Text.Trim(), out a) || a.AddressFamily != AddressFamily.InterNetwork))
            { MessageBox.Show("Secondary DNS must be an IPv4 address, e.g. 8.8.8.8", "SilentDNS"); return; }
            foreach (var t in new[] { p6, s6 })
                if (t.Text.Trim().Length > 0 && (!IPAddress.TryParse(t.Text.Trim(), out a) || a.AddressFamily != AddressFamily.InterNetworkV6))
                { MessageBox.Show("\"" + t.Text + "\" is not a valid IPv6 address.\nExample: 2606:4700:4700::1111", "SilentDNS"); return; }

            cfg.Preset = (string)preset.SelectedItem;
            cfg.P4 = p4.Text.Trim(); cfg.S4 = s4.Text.Trim(); cfg.P6 = p6.Text.Trim(); cfg.S6 = s6.Text.Trim();
            cfg.Encrypted = doh.Checked;
            cfg.StartWithWindows = startup.Checked;
            cfg.Enabled = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    class TrayApp : ApplicationContext
    {
        NotifyIcon tray = new NotifyIcon();
        Config cfg = Config.Load();
        System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = 4000 };
        System.Windows.Forms.Timer guard = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        Icon onIcon = MakeIcon(true, SystemInformation.SmallIconSize.Width);
        Icon offIcon = MakeIcon(false, SystemInformation.SmallIconSize.Width);
        ToolStripMenuItem status, status6, toggle, startupItem, presetsMenu;
        Control ui = new Control();
        int busy;

        public TrayApp()
        {
            ui.CreateControl();
            BuildMenu();
            tray.Visible = true;
            tray.DoubleClick += delegate { OpenSettings(); };

            if (cfg.FirstRun) { cfg.Save(); }
            SyncStartupTask();

            // Re-apply when Wi-Fi/Ethernet changes or the PC wakes from sleep
            NetworkChange.NetworkAddressChanged += delegate { RestartDebounce(); };
            NetworkChange.NetworkAvailabilityChanged += delegate { RestartDebounce(); };
            SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Resume) RestartDebounce(); };
            debounce.Tick += delegate { debounce.Stop(); ApplyAsync(false); };
            guard.Tick += delegate { ApplyAsync(false); }; // quiet check in case something else changed DNS
            guard.Start();

            Refresh();
            ApplyAsync(true);

            if (cfg.FirstRun)
                tray.ShowBalloonTip(4000, "SilentDNS is running", "DNS set to " + cfg.P4 + " / " + cfg.S4 + ". It lives here in the tray and starts with Windows.", ToolTipIcon.Info);
        }

        void RestartDebounce()
        {
            if (ui.InvokeRequired) { ui.BeginInvoke((MethodInvoker)RestartDebounce); return; }
            debounce.Stop(); debounce.Start();
        }

        void BuildMenu()
        {
            var m = new ContextMenuStrip();
            status = new ToolStripMenuItem("…") { Enabled = false };
            status6 = new ToolStripMenuItem("…") { Enabled = false };
            toggle = new ToolStripMenuItem("Turn off (use automatic DNS)", null, delegate { Toggle(); });
            presetsMenu = new ToolStripMenuItem("Quick switch");
            foreach (var p in Presets.All)
            {
                var pp = p;
                presetsMenu.DropDownItems.Add(new ToolStripMenuItem(p.Name + "   (" + p.P4 + ", " + p.S4 + "  +IPv6)", null, delegate { UsePreset(pp); })
                {
                    Tag = p.Name,
                    ToolTipText = "IPv4: " + p.P4 + ", " + p.S4 + "\nIPv6: " + p.P6 + ", " + p.S6
                });
            }
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate
            {
                cfg.StartWithWindows = !cfg.StartWithWindows; cfg.Save(); SyncStartupTask(); Refresh();
            });

            m.Items.Add(status);
            m.Items.Add(status6);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(toggle);
            m.Items.Add(presetsMenu);
            m.Items.Add("Settings…", null, delegate { OpenSettings(); });
            m.Items.Add("Flush DNS cache", null, delegate { ThreadPool.QueueUserWorkItem(_ => Dns.Run("ipconfig.exe", "/flushdns")); });
            m.Items.Add(startupItem);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Exit (DNS stays set)", null, delegate { ExitApp(); });
            presetsMenu.DropDown.ShowItemToolTips = true;
            m.Opening += delegate { var s = Dns.CurrentSummary(); status.Text = s[0]; status6.Text = s[1]; status6.Visible = s[1].Length > 0; };
            tray.ContextMenuStrip = m;
        }

        void Refresh()
        {
            tray.Icon = cfg.Enabled ? onIcon : offIcon;
            string t = cfg.Enabled
                ? "SilentDNS — ON" + (cfg.Encrypted ? " (encrypted)" : "") + "\n" + cfg.P4 + " / " + cfg.S4 + (cfg.P6.Length > 0 ? "\n+ IPv6 DNS" : "")
                : "SilentDNS — OFF (automatic DNS)";
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            toggle.Text = cfg.Enabled ? "Turn off (use automatic DNS)" : "Turn on";
            startupItem.Checked = cfg.StartWithWindows;
            foreach (ToolStripMenuItem i in presetsMenu.DropDownItems)
                i.Checked = cfg.Enabled && (string)i.Tag == cfg.Preset;
        }

        void ApplyAsync(bool force)
        {
            if (!cfg.Enabled) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            var snapshot = cfg;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Dns.Apply(snapshot, force); } finally { Interlocked.Exchange(ref busy, 0); }
            });
        }

        void Toggle()
        {
            cfg.Enabled = !cfg.Enabled; cfg.Save(); Refresh();
            if (cfg.Enabled) ApplyAsync(true);
            else ThreadPool.QueueUserWorkItem(_ => Dns.Reset());
        }

        void UsePreset(Preset p)
        {
            cfg.Preset = p.Name; cfg.P4 = p.P4; cfg.S4 = p.S4; cfg.P6 = p.P6; cfg.S6 = p.S6;
            cfg.Enabled = true; cfg.Save(); Refresh(); ApplyAsync(true);
        }

        SettingsForm open;
        void OpenSettings()
        {
            if (open != null) { open.Activate(); return; }
            open = new SettingsForm(cfg);
            if (open.ShowDialog() == DialogResult.OK)
            {
                cfg.Save(); SyncStartupTask(); Refresh(); ApplyAsync(true);
            }
            open.Dispose(); open = null;
        }

        void SyncStartupTask()
        {
            bool on = cfg.StartWithWindows;
            ThreadPool.QueueUserWorkItem(_ => { if (on) Startup.Enable(); else Startup.Disable(); });
        }

        void ExitApp()
        {
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }

        // Drawn at runtime — a small shield. Green = on, grey = off.
        public static Icon MakeIcon(bool on, int size)
        {
            if (size < 16) size = 16;
            using (var bmp = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 16f;
                var pts = new[] {
                    new PointF(8*s, 0.8f*s), new PointF(14.4f*s, 3*s), new PointF(13.6f*s, 9.5f*s),
                    new PointF(8*s, 15.2f*s), new PointF(2.4f*s, 9.5f*s), new PointF(1.6f*s, 3*s) };
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(pts);
                    using (var b = new LinearGradientBrush(new RectangleF(0, 0, size, size),
                        on ? Color.FromArgb(46, 204, 113) : Color.FromArgb(150, 150, 150),
                        on ? Color.FromArgb(22, 140, 80) : Color.FromArgb(95, 95, 95), 90f))
                        g.FillPath(b, path);
                }
                using (var pen = new Pen(Color.White, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (on) g.DrawLines(pen, new[] { new PointF(5*s, 8*s), new PointF(7.2f*s, 10.2f*s), new PointF(11*s, 5.8f*s) });
                    else g.DrawLine(pen, 5.2f*s, 8*s, 10.8f*s, 8*s);
                }
                IntPtr h = bmp.GetHicon();
                return (Icon)Icon.FromHandle(h).Clone();
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\SilentDNS_SingleInstance", out created))
            {
                if (!created) return; // already running in the tray
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
