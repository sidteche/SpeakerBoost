// Speaker Boost - portable GUI for a speaker-only loudness boost through Equalizer APO.
// Applies ONLY to the built-in Realtek "Speakers" output; headphones, Bluetooth and
// monitors are never touched. Runs in the tray and can start with Windows ("/background").
// Build with build.ps1 (uses the C# compiler built into Windows).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

[assembly: System.Reflection.AssemblyTitle("Speaker Boost")]
[assembly: System.Reflection.AssemblyProduct("Speaker Boost")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace SpeakerBoost
{
    static class Program
    {
        public const string ShowSignalName = "SpeakerBoost.Show";

        [STAThread]
        static void Main(string[] args)
        {
            bool background = Array.IndexOf(args, "/background") >= 0;
            bool firstInstance;
            using (Mutex mutex = new Mutex(true, "SpeakerBoost.SingleInstance", out firstInstance))
            {
                if (!firstInstance)
                {
                    // Already running: bring the existing window up instead of starting twice.
                    if (!background)
                        try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(background));
            }
        }
    }

    class Settings
    {
        public bool On = true;
        public int Gain = 7, Voice = 4, Treble = 2, BassCut = 150;
    }

    class Endpoint
    {
        public string Guid, Name, Iface;
        public bool Active, HasApo;
    }

    static class Audio
    {
        public const string ApoDir = @"C:\Program Files\EqualizerAPO";
        const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
        const string NameProp = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
        const string IfaceProp = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
        static readonly Regex GuidRx = new Regex(@"^\{[0-9A-Fa-f-]{36}\}$");

        public static string ConfigFile { get { return Path.Combine(ApoDir, @"config\config.txt"); } }
        public static bool ApoInstalled { get { return File.Exists(Path.Combine(ApoDir, "EqualizerAPO.dll")); } }

        // The built-in Realtek speakers, preferring the active endpoint.
        public static Endpoint FindSpeakers()
        {
            Endpoint best = null;
            using (RegistryKey render = Registry.LocalMachine.OpenSubKey(RenderKey))
            {
                if (render == null) return null;
                foreach (string id in render.GetSubKeyNames())
                {
                    using (RegistryKey ep = render.OpenSubKey(id))
                    using (RegistryKey props = render.OpenSubKey(id + @"\Properties"))
                    {
                        if (ep == null || props == null) continue;
                        string name = props.GetValue(NameProp) as string;
                        string iface = props.GetValue(IfaceProp) as string;
                        if (name != "Speakers" || iface == null || !iface.StartsWith("Realtek")) continue;

                        object state = ep.GetValue("DeviceState");
                        Endpoint e = new Endpoint();
                        e.Guid = id; e.Name = name; e.Iface = iface;
                        e.Active = state is int && (int)state == 1;
                        e.HasApo = HasEqualizerApo(render, id);
                        if (best == null || (e.Active && !best.Active)) best = e;
                    }
                }
            }
            return best;
        }

        // True when one of the endpoint's audio effects is Equalizer APO's DLL.
        static bool HasEqualizerApo(RegistryKey render, string id)
        {
            using (RegistryKey fx = render.OpenSubKey(id + @"\FxProperties"))
            {
                if (fx == null) return false;
                foreach (string v in fx.GetValueNames())
                {
                    string clsid = fx.GetValue(v) as string;
                    if (clsid == null || !GuidRx.IsMatch(clsid)) continue;
                    using (RegistryKey srv = Registry.ClassesRoot.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32"))
                    {
                        string dll = srv == null ? null : srv.GetValue("") as string;
                        if (dll != null && dll.IndexOf("EqualizerAPO", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                }
            }
            return false;
        }

        public static string FindLoudMax()
        {
            string[] paths = {
                @"C:\Program Files\VSTPlugins\LoudMax64.dll",
                @"C:\Program Files\Steinberg\VSTPlugins\LoudMax64.dll",
                @"C:\Program Files\Common Files\VST2\LoudMax64.dll",
                Path.Combine(ApoDir, @"VSTPlugins\LoudMax64.dll")
            };
            foreach (string p in paths) if (File.Exists(p)) return p;
            return null;
        }

        // Settings are stored in the config's first line, so the app needs no extra files.
        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                Match m = Regex.Match(File.ReadAllText(ConfigFile), @"^# SpeakerBoost (.+)$", RegexOptions.Multiline);
                if (!m.Success) return s;
                foreach (Match kv in Regex.Matches(m.Groups[1].Value, @"(\w+)=(\d+)"))
                {
                    int n = int.Parse(kv.Groups[2].Value);
                    switch (kv.Groups[1].Value)
                    {
                        case "on": s.On = n != 0; break;
                        case "gain": s.Gain = n; break;
                        case "voice": s.Voice = n; break;
                        case "treble": s.Treble = n; break;
                        case "bass": s.BassCut = n; break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return s;
        }

        // True when the config is ours and targets the current speakers.
        public static bool ConfigIsCurrent(Settings s, string speakerGuid)
        {
            try
            {
                string text = File.ReadAllText(ConfigFile);
                if (!text.StartsWith("# SpeakerBoost ")) return false;
                return !s.On || text.IndexOf(speakerGuid, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool StartupEnabled
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("SpeakerBoost") != null;
            }
        }

        public static void SetStartup(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("SpeakerBoost", "\"" + Application.ExecutablePath + "\" /background");
                else k.DeleteValue("SpeakerBoost", false);
            }
        }

        public static void Save(Settings s, string speakerGuid, string loudMax)
        {
            // Keep whatever config existed before Speaker Boost took over.
            string backup = ConfigFile + ".before-speakerboost";
            if (File.Exists(ConfigFile) && !File.Exists(backup) &&
                File.ReadAllText(ConfigFile).IndexOf("SpeakerBoost", StringComparison.Ordinal) < 0)
                File.Copy(ConfigFile, backup);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("# SpeakerBoost on={0} gain={1} voice={2} treble={3} bass={4}",
                s.On ? 1 : 0, s.Gain, s.Voice, s.Treble, s.BassCut));
            sb.AppendLine("# Managed by SpeakerBoost.exe - applies to the built-in speakers only");
            if (s.On)
            {
                sb.AppendLine("Device: " + speakerGuid);
                sb.AppendLine("Preamp: " + s.Gain + " dB");
                if (s.BassCut > 0) sb.AppendLine("Filter: ON HPQ Fc " + s.BassCut + " Hz Q 0.707");
                sb.AppendLine("Filter: ON PK Fc 300 Hz Gain -2 dB Q 1.0");
                if (s.Voice > 0) sb.AppendLine("Filter: ON PK Fc 2500 Hz Gain " + s.Voice + " dB Q 0.9");
                if (s.Treble > 0) sb.AppendLine("Filter: ON HS Fc 8000 Hz Gain " + s.Treble + " dB");
                if (loudMax != null) sb.AppendLine("VSTPlugin: Library \"" + loudMax + "\"");
            }
            File.WriteAllText(ConfigFile, sb.ToString(), new UTF8Encoding(false));
        }
    }

    class MainForm : Form
    {
        static readonly Color Accent = Color.FromArgb(14, 124, 102);
        static readonly Color Good = Color.FromArgb(27, 127, 59);
        static readonly Color Warn = Color.FromArgb(179, 107, 0);
        static readonly Color Bad = Color.FromArgb(192, 57, 43);
        static readonly int[] BassOptions = { 0, 100, 150, 200 };

        Settings settings;
        Endpoint speakers;
        string loudMax;
        bool loading;

        Label lblSpeakers, lblApo, lblLimiter, lblApplied;
        Button btnFix;
        LinkLabel lnkLoudMax;
        CheckBox toggle;
        TrackBar tbGain, tbVoice, tbTreble;
        Label valGain, valVoice, valTreble;
        ComboBox cbBass;
        CheckBox chkStartup;
        TableLayoutPanel tuning;
        FlowLayoutPanel presets;
        Timer debounce, watchdog;
        NotifyIcon tray;
        ToolStripMenuItem miToggle, miStartup;
        EventWaitHandle showSignal;
        DateTime lastWrite;
        bool allowShow, exiting, hintShown;

        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr process);

        public MainForm(bool background)
        {
            allowShow = !background;
            Text = "Speaker Boost";
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            debounce = new Timer();
            debounce.Interval = 250;
            debounce.Tick += delegate { debounce.Stop(); Apply(); };

            BuildUi();
            BuildTray();
            RefreshStatus();
            LoadSettings();

            // A moved exe would leave a stale startup entry; point it at this copy.
            if (Audio.StartupEnabled) Audio.SetStartup(true);
            chkStartup.Checked = Audio.StartupEnabled;

            // Re-apply if a driver update changes the speakers' ID or the config gets overwritten.
            watchdog = new Timer();
            watchdog.Interval = 20000;
            watchdog.Tick += delegate { CheckHealth(); };
            watchdog.Start();
            CheckHealth();

            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSignalName);
            Thread listener = new Thread(delegate()
            {
                while (showSignal.WaitOne())
                {
                    if (exiting) return;
                    try { BeginInvoke(new MethodInvoker(ShowWindow)); } catch { }
                }
            });
            listener.IsBackground = true;
            listener.Start();

            if (background) TrimMemory();
        }

        // Keeps the window hidden when started with /background (tray only).
        protected override void SetVisibleCore(bool value)
        {
            if (!allowShow)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem open = new ToolStripMenuItem("Open Speaker Boost");
            open.Font = new Font(menu.Font, FontStyle.Bold);
            open.Click += delegate { ShowWindow(); };
            miToggle = new ToolStripMenuItem("Boost on");
            miToggle.Click += delegate { toggle.Checked = !toggle.Checked; };
            ToolStripMenuItem presetMenu = new ToolStripMenuItem("Preset");
            presetMenu.DropDownItems.Add("Low", null, delegate { ApplyPreset(4, 3, 1, 150); });
            presetMenu.DropDownItems.Add("Medium", null, delegate { ApplyPreset(7, 4, 2, 150); });
            presetMenu.DropDownItems.Add("High", null, delegate { ApplyPreset(10, 5, 2, 200); });
            miStartup = new ToolStripMenuItem("Start with Windows");
            miStartup.Click += delegate { chkStartup.Checked = !chkStartup.Checked; };
            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };
            menu.Items.AddRange(new ToolStripItem[] {
                open, new ToolStripSeparator(), miToggle, presetMenu, miStartup, new ToolStripSeparator(), exit });

            tray = new NotifyIcon();
            tray.Icon = Icon ?? SystemIcons.Application;
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.Visible = true;
        }

        void UpdateTray()
        {
            if (tray == null) return;
            miToggle.Checked = toggle.Checked;
            miStartup.Checked = chkStartup.Checked;
            tray.Text = toggle.Checked ? "Speaker Boost: ON (+" + tbGain.Value + " dB)" : "Speaker Boost: OFF";
        }

        void ShowWindow()
        {
            allowShow = true;
            RefreshStatus();
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            exiting = true;
            tray.Visible = false;
            showSignal.Set();
            Close();
        }

        static void TrimMemory()
        {
            try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
        }

        void CheckHealth()
        {
            if (!Audio.ApoInstalled) return;
            Endpoint now = Audio.FindSpeakers();
            bool deviceChanged = (now == null) != (speakers == null) ||
                (now != null && speakers != null && now.Guid != speakers.Guid);
            if (deviceChanged) RefreshStatus();
            if (speakers == null) return;

            DateTime stamp = File.Exists(Audio.ConfigFile) ? File.GetLastWriteTimeUtc(Audio.ConfigFile) : DateTime.MinValue;
            if (deviceChanged || stamp != lastWrite)
            {
                if (!Audio.ConfigIsCurrent(settings, speakers.Guid)) Apply(false);
                else lastWrite = stamp;
            }
        }

        void BuildUi()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.AutoSize = true;
            root.Padding = new Padding(18, 14, 18, 14);
            root.MinimumSize = new Size(440, 0);
            Controls.Add(root);

            Label title = new Label();
            title.Text = "Speaker Boost";
            title.Font = new Font("Segoe UI Semibold", 15f);
            title.AutoSize = true;
            root.Controls.Add(title);

            Label sub = new Label();
            sub.Text = "Louder, clearer laptop speakers. Headphones and Bluetooth stay untouched.";
            sub.ForeColor = Color.DimGray;
            sub.AutoSize = true;
            sub.MaximumSize = new Size(400, 0);
            sub.Margin = new Padding(3, 0, 3, 10);
            root.Controls.Add(sub);

            root.Controls.Add(Header("Status"));
            lblSpeakers = StatusLabel(root);
            lblApo = StatusLabel(root);
            lblLimiter = StatusLabel(root);

            FlowLayoutPanel fixRow = new FlowLayoutPanel();
            fixRow.AutoSize = true;
            fixRow.Margin = new Padding(0, 2, 0, 0);
            btnFix = new Button();
            btnFix.AutoSize = true;
            btnFix.Click += FixClicked;
            lnkLoudMax = new LinkLabel();
            lnkLoudMax.Text = "Get LoudMax (free limiter)";
            lnkLoudMax.AutoSize = true;
            lnkLoudMax.Margin = new Padding(6, 8, 3, 3);
            lnkLoudMax.LinkClicked += delegate { OpenUrl("https://loudmax.blogspot.com/"); };
            fixRow.Controls.Add(btnFix);
            fixRow.Controls.Add(lnkLoudMax);
            root.Controls.Add(fixRow);

            toggle = new CheckBox();
            toggle.Appearance = Appearance.Button;
            toggle.FlatStyle = FlatStyle.Flat;
            toggle.FlatAppearance.BorderSize = 0;
            toggle.TextAlign = ContentAlignment.MiddleCenter;
            toggle.Font = new Font("Segoe UI Semibold", 11f);
            toggle.Size = new Size(400, 46);
            toggle.Dock = DockStyle.Fill;
            toggle.Margin = new Padding(3, 14, 3, 6);
            toggle.Cursor = Cursors.Hand;
            toggle.CheckedChanged += delegate { UpdateToggle(); if (!loading) Apply(); };
            root.Controls.Add(toggle);

            root.Controls.Add(Header("Presets"));
            presets = new FlowLayoutPanel();
            presets.AutoSize = true;
            presets.Controls.Add(PresetButton("Low", 4, 3, 1, 150));
            presets.Controls.Add(PresetButton("Medium", 7, 4, 2, 150));
            presets.Controls.Add(PresetButton("High", 10, 5, 2, 200));
            root.Controls.Add(presets);

            root.Controls.Add(Header("Fine-tune"));
            tuning = new TableLayoutPanel();
            tuning.ColumnCount = 3;
            tuning.AutoSize = true;
            tuning.Dock = DockStyle.Fill;
            tuning.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tuning.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tuning.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60f));
            tbGain = Slider("Volume boost", 12, out valGain);
            tbVoice = Slider("Voice clarity", 8, out valVoice);
            tbTreble = Slider("Treble", 6, out valTreble);

            Label bassLbl = new Label();
            bassLbl.Text = "Bass cut below";
            bassLbl.AutoSize = true;
            bassLbl.Anchor = AnchorStyles.Left;
            cbBass = new ComboBox();
            cbBass.DropDownStyle = ComboBoxStyle.DropDownList;
            cbBass.Items.AddRange(new object[] { "Off", "100 Hz", "150 Hz (recommended)", "200 Hz (louder)" });
            cbBass.Width = 200;
            cbBass.Anchor = AnchorStyles.Left;
            cbBass.SelectedIndexChanged += delegate { Changed(); };
            tuning.Controls.Add(bassLbl);
            tuning.Controls.Add(cbBass);
            tuning.Controls.Add(new Label());
            root.Controls.Add(tuning);

            chkStartup = new CheckBox();
            chkStartup.Text = "Start with Windows (runs quietly in the tray)";
            chkStartup.AutoSize = true;
            chkStartup.Margin = new Padding(3, 12, 3, 0);
            chkStartup.CheckedChanged += delegate
            {
                try { Audio.SetStartup(chkStartup.Checked); } catch { }
                UpdateTray();
            };
            root.Controls.Add(chkStartup);

            FlowLayoutPanel footer = new FlowLayoutPanel();
            footer.AutoSize = true;
            footer.Margin = new Padding(0, 8, 0, 0);
            Button refresh = new Button();
            refresh.Text = "Refresh";
            refresh.AutoSize = true;
            refresh.Click += delegate { RefreshStatus(); Apply(); };
            lblApplied = new Label();
            lblApplied.AutoSize = true;
            lblApplied.ForeColor = Color.DimGray;
            lblApplied.Margin = new Padding(8, 8, 3, 3);
            footer.Controls.Add(refresh);
            footer.Controls.Add(lblApplied);
            root.Controls.Add(footer);
        }

        Label Header(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI Semibold", 10f);
            l.AutoSize = true;
            l.Margin = new Padding(3, 10, 3, 4);
            return l;
        }

        Label StatusLabel(TableLayoutPanel root)
        {
            Label l = new Label();
            l.AutoSize = true;
            l.MaximumSize = new Size(410, 0);
            l.Margin = new Padding(3, 1, 3, 1);
            root.Controls.Add(l);
            return l;
        }

        TrackBar Slider(string name, int max, out Label value)
        {
            Label l = new Label();
            l.Text = name;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            TrackBar tb = new TrackBar();
            tb.Minimum = 0;
            tb.Maximum = max;
            tb.TickFrequency = 1;
            tb.Dock = DockStyle.Fill;
            value = new Label();
            value.AutoSize = true;
            value.Anchor = AnchorStyles.Left;
            Label v = value;
            tb.ValueChanged += delegate { v.Text = "+" + tb.Value + " dB"; Changed(); };
            tuning.Controls.Add(l);
            tuning.Controls.Add(tb);
            tuning.Controls.Add(value);
            return tb;
        }

        Button PresetButton(string name, int gain, int voice, int treble, int bass)
        {
            Button b = new Button();
            b.Text = name;
            b.Size = new Size(96, 32);
            b.Click += delegate { ApplyPreset(gain, voice, treble, bass); };
            return b;
        }

        void ApplyPreset(int gain, int voice, int treble, int bass)
        {
            loading = true;
            toggle.Checked = true;
            tbGain.Value = Math.Min(gain, tbGain.Maximum);
            tbVoice.Value = voice;
            tbTreble.Value = treble;
            cbBass.SelectedIndex = Array.IndexOf(BassOptions, bass);
            loading = false;
            Apply();
        }

        void RefreshStatus()
        {
            speakers = Audio.FindSpeakers();
            loudMax = Audio.FindLoudMax();
            bool apo = Audio.ApoInstalled;

            if (speakers == null) SetStatus(lblSpeakers, Bad, "Built-in Realtek speakers not found");
            else SetStatus(lblSpeakers, Good, "Speakers: " + speakers.Iface + (speakers.Active ? "" : " (disabled)"));

            if (!apo) SetStatus(lblApo, Bad, "Equalizer APO is not installed");
            else if (speakers != null && !speakers.HasApo) SetStatus(lblApo, Bad, "Equalizer APO is not attached to the speakers");
            else SetStatus(lblApo, Good, "Equalizer APO attached to speakers only");

            if (loudMax != null) SetStatus(lblLimiter, Good, "LoudMax limiter found - full boost available, no crackling");
            else SetStatus(lblLimiter, Warn, "No LoudMax limiter - boost capped at +3 dB to avoid crackling");

            if (!apo) { btnFix.Text = "Download Equalizer APO"; btnFix.Visible = true; }
            else if (speakers != null && !speakers.HasApo) { btnFix.Text = "Attach to speakers..."; btnFix.Visible = true; }
            else btnFix.Visible = false;
            lnkLoudMax.Visible = loudMax == null;

            int max = loudMax != null ? 12 : 3;
            if (tbGain.Value > max) tbGain.Value = max;
            tbGain.Maximum = max;

            bool usable = apo && speakers != null;
            toggle.Enabled = usable;
            presets.Enabled = usable && toggle.Checked;
            tuning.Enabled = usable && toggle.Checked;
        }

        void SetStatus(Label l, Color c, string text)
        {
            l.ForeColor = c;
            l.Text = (c == Good ? "\u2714  " : c == Warn ? "\u26A0  " : "\u2716  ") + text;
        }

        void LoadSettings()
        {
            loading = true;
            settings = Audio.Load();
            toggle.Checked = settings.On;
            tbGain.Value = Math.Max(0, Math.Min(settings.Gain, tbGain.Maximum));
            tbVoice.Value = Math.Max(0, Math.Min(settings.Voice, tbVoice.Maximum));
            tbTreble.Value = Math.Max(0, Math.Min(settings.Treble, tbTreble.Maximum));
            int i = Array.IndexOf(BassOptions, settings.BassCut);
            cbBass.SelectedIndex = i < 0 ? 2 : i;
            valGain.Text = "+" + tbGain.Value + " dB";
            valVoice.Text = "+" + tbVoice.Value + " dB";
            valTreble.Text = "+" + tbTreble.Value + " dB";
            UpdateToggle();
            loading = false;
            UpdateTray();
            lblApplied.Text = "Changes apply instantly.";
        }

        void UpdateToggle()
        {
            toggle.Text = toggle.Checked ? "Boost is ON  -  click to turn off" : "Boost is OFF  -  click to turn on";
            toggle.BackColor = toggle.Checked ? Accent : Color.Gainsboro;
            toggle.ForeColor = toggle.Checked ? Color.White : Color.Black;
            toggle.FlatAppearance.CheckedBackColor = Accent;
            bool usable = Audio.ApoInstalled && speakers != null;
            presets.Enabled = usable && toggle.Checked;
            tuning.Enabled = usable && toggle.Checked;
        }

        void Changed()
        {
            if (loading) return;
            debounce.Stop();
            debounce.Start();
        }

        void Apply()
        {
            Apply(true);
        }

        // interactive=false is used by the watchdog, which must never pop up dialogs.
        void Apply(bool interactive)
        {
            UpdateTray();
            if (loading || speakers == null || !Audio.ApoInstalled) return;
            settings.On = toggle.Checked;
            settings.Gain = tbGain.Value;
            settings.Voice = tbVoice.Value;
            settings.Treble = tbTreble.Value;
            settings.BassCut = BassOptions[Math.Max(0, cbBass.SelectedIndex)];
            try
            {
                Audio.Save(settings, speakers.Guid, loudMax);
                lastWrite = File.GetLastWriteTimeUtc(Audio.ConfigFile);
                lblApplied.Text = "\u2714 Applied at " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (UnauthorizedAccessException)
            {
                lblApplied.Text = "Windows blocked saving the settings.";
                if (interactive && MessageBox.Show(this, "Windows blocked saving the settings.\n\nRestart Speaker Boost as administrator?",
                        "Speaker Boost", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    RelaunchAsAdmin();
            }
            catch (Exception ex)
            {
                lblApplied.Text = "Could not save: " + ex.Message;
            }
        }

        void FixClicked(object sender, EventArgs e)
        {
            if (!Audio.ApoInstalled)
            {
                OpenUrl("https://sourceforge.net/projects/equalizerapo/");
                return;
            }
            MessageBox.Show(this, "In the window that opens, tick ONLY \"Speakers\" (Realtek), click OK, then restart Windows.",
                "Speaker Boost", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start(Path.Combine(Audio.ApoDir, "DeviceSelector.exe")); } catch { }
        }

        void RelaunchAsAdmin()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);
                Close();
            }
            catch { }
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(url); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (debounce.Enabled) { debounce.Stop(); Apply(); }

            // The X button hides to the tray; only "Exit" or Windows shutdown really quits.
            if (!exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!hintShown)
                {
                    hintShown = true;
                    tray.ShowBalloonTip(3000, "Speaker Boost is still running",
                        "It keeps your speaker boost applied. Right-click the tray icon for options.", ToolTipIcon.Info);
                }
                TrimMemory();
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }
    }
}
