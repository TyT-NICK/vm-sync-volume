// Voicemeeter Volume Sync
// Keeps the gain of selected Voicemeeter strips/buses in sync with a Windows output device volume.
//
// Single-file C# 5 source. Build with build.cmd, which uses the C# compiler that ships with
// Windows (.NET Framework 4.x) - no SDK, runtime or third-party packages required.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Voicemeeter Volume Sync")]
[assembly: AssemblyProduct("VMVolumeSync")]
[assembly: AssemblyDescription("Syncs Windows volume to Voicemeeter strips and buses")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace VMVolumeSync
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool isFirstInstance;
            using (new Mutex(true, @"Local\VMVolumeSync.SingleInstance", out isFirstInstance))
            {
                if (!isFirstInstance) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using (var app = new TrayApp()) Application.Run(app);
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Tray application
    // ------------------------------------------------------------------------------------------

    sealed class TrayApp : ApplicationContext
    {
        const string AppName = "Voicemeeter Volume Sync";
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "VMVolumeSync";

        static readonly float[] MinGainChoices = { -60f, -50f, -40f, -30f, -20f };
        static readonly float[] MaxGainChoices = { 0f, 6f, 12f };

        readonly Settings settings;
        readonly VoicemeeterRemote vm;
        readonly AudioWatcher audio;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly System.Windows.Forms.Timer timer;
        readonly TrayIcons icons = new TrayIcons();
        readonly Font boldFont;

        const float GainTolerance = 0.06f;  // dB; absorbs 0.1 dB rounding when values round-trip
        const int EchoWindowMs = 400;       // ignore Voicemeeter read-backs of values we just wrote

        readonly System.Windows.Forms.Timer syncTimer;
        // Last gain/mute known for each target, either sent by us or read back from Voicemeeter.
        readonly Dictionary<string, TargetState> targetStates = new Dictionary<string, TargetState>();

        VmLayout layout;
        int forcePushTicks;     // re-send for a few ticks after (re)connecting, while Voicemeeter settles
        bool hintShown;
        bool keepMenuOpen;
        bool disposed;

        public TrayApp()
        {
            settings = Settings.Load();

            menu = new ContextMenuStrip();
            boldFont = new Font(menu.Font, FontStyle.Bold);
            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            menu.ItemClicked += (s, e) => keepMenuOpen = false;
            menu.Closing += OnMenuClosing;

            tray = new NotifyIcon { ContextMenuStrip = menu, Text = AppName };
            tray.Icon = icons.Get(IconState.Disconnected);
            tray.MouseUp += OnTrayMouseUp;
            tray.Visible = true;

            audio = new AudioWatcher(SynchronizationContext.Current);
            audio.VolumeChanged += () => Push();
            audio.DevicesChanged += () => { if (audio.Bind(settings.DeviceId)) Push(); else UpdateTray(); };
            audio.Bind(settings.DeviceId);

            vm = new VoicemeeterRemote();
            if (!vm.Open(settings.VoicemeeterDir))
            {
                tray.ShowBalloonTip(8000, AppName,
                    "VoicemeeterRemote DLL was not found. Is Voicemeeter installed? " +
                    "You can set VoicemeeterDir in " + Path.GetFileName(settings.FilePath) + ".",
                    ToolTipIcon.Warning);
            }

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (s, e) => Tick();
            timer.Start();

            syncTimer = new System.Windows.Forms.Timer { Interval = 100 };
            syncTimer.Tick += (s, e) => Pull();
            syncTimer.Start();

            Tick();
        }

        // Called every second: tracks the Voicemeeter connection/edition and retries a missing audio device.
        void Tick()
        {
            if (disposed) return;

            int kind = vm.PollKind();
            int currentKind = layout == null ? 0 : layout.Kind;
            if (kind != currentKind)
            {
                layout = VmLayout.For(kind);
                targetStates.Clear();
                forcePushTicks = layout != null ? 3 : 0;
                if (layout != null && settings.Targets.Count == 0 && !hintShown)
                {
                    hintShown = true;
                    tray.ShowBalloonTip(8000, AppName,
                        "Connected to " + layout.Name + ". Click the tray icon to choose which " +
                        "Voicemeeter inputs/outputs should follow the Windows volume.",
                        ToolTipIcon.Info);
                }
            }

            if (!audio.IsBound) audio.Bind(settings.DeviceId);

            if (forcePushTicks > 0)
            {
                forcePushTicks--;
                targetStates.Clear();
            }
            Push();
        }

        // Windows -> Voicemeeter: sends the current Windows volume/mute to every target that differs.
        void Push()
        {
            if (disposed) return;

            float level;
            bool muted;
            if (layout != null && audio.TryRead(out level, out muted))
            {
                float gain = MapGain(level);
                int mute = muted ? 1 : 0;
                string gainText = gain.ToString("0.0", CultureInfo.InvariantCulture);
                var script = new StringBuilder();
                foreach (string target in settings.Targets)
                {
                    if (!layout.IsValidTarget(target)) continue;
                    TargetState state = GetState(target);
                    bool sendGain = float.IsNaN(state.Gain) || Math.Abs(state.Gain - gain) > GainTolerance;
                    bool sendMute = settings.SyncMute && state.Mute != mute;
                    if (!sendGain && !sendMute) continue;

                    if (sendGain)
                    {
                        script.Append(target).Append(".Gain=").Append(gainText).Append(';');
                        state.Gain = gain;
                    }
                    if (sendMute)
                    {
                        script.Append(target).Append(".Mute=").Append(mute).Append(';');
                        state.Mute = mute;
                    }
                    state.SentAt = Environment.TickCount;
                }

                if (script.Length > 0 && !vm.SetParameters(script.ToString())) targetStates.Clear(); // retry next time
            }
            UpdateTray();
        }

        // Voicemeeter -> Windows (two-way mode): a target moved in Voicemeeter drives the Windows volume,
        // and the resulting Windows volume event fans the change out to the other targets via Push().
        void Pull()
        {
            if (disposed || !settings.TwoWay || layout == null || forcePushTicks > 0 || !audio.IsBound) return;
            if (!vm.TakeDirty()) return;

            int now = Environment.TickCount;
            float? newGain = null;
            int? newMute = null;
            foreach (string target in settings.Targets)
            {
                if (!layout.IsValidTarget(target)) continue;
                TargetState state = GetState(target);
                if (now - state.SentAt < EchoWindowMs)
                {
                    vm.MarkDirty(); // look at this target again once the echo window has passed
                    continue;
                }

                float gain;
                if (vm.GetFloat(target + ".Gain", out gain))
                {
                    if (!float.IsNaN(state.Gain) && Math.Abs(gain - state.Gain) > GainTolerance) newGain = gain;
                    state.Gain = gain;
                }

                float muteValue;
                if (settings.SyncMute && vm.GetFloat(target + ".Mute", out muteValue))
                {
                    int mute = muteValue >= 0.5f ? 1 : 0;
                    if (state.Mute >= 0 && mute != state.Mute) newMute = mute;
                    state.Mute = mute;
                }
            }

            if (newGain.HasValue) audio.TrySetVolume(InverseGain(newGain.Value));
            if (newMute.HasValue) audio.TrySetMute(newMute.Value == 1);
        }

        TargetState GetState(string target)
        {
            TargetState state;
            if (!targetStates.TryGetValue(target, out state))
            {
                state = new TargetState();
                targetStates[target] = state;
            }
            return state;
        }

        float InverseGain(float gain)
        {
            float min = Math.Min(settings.GainMin, settings.GainMax);
            float max = Math.Max(settings.GainMin, settings.GainMax);
            if (gain <= min) return 0f;
            if (gain >= max) return 1f;
            if (settings.LogCurve) return (float)Math.Pow(10.0, (gain - max) / 20.0);
            return (gain - min) / (max - min);
        }

        float MapGain(float level)
        {
            float min = Math.Min(settings.GainMin, settings.GainMax);
            float max = Math.Max(settings.GainMin, settings.GainMax);
            double gain;
            if (level <= 0f) gain = min;
            else if (settings.LogCurve) gain = Math.Max(min, max + 20.0 * Math.Log10(level));
            else gain = min + (max - min) * level;
            return (float)Math.Round(gain, 1);
        }

        void UpdateTray()
        {
            float level;
            bool muted;
            bool haveAudio = audio.TryRead(out level, out muted);

            IconState state = layout == null ? IconState.Disconnected
                : haveAudio && muted ? IconState.Muted : IconState.Normal;
            tray.Icon = icons.Get(state);

            string status;
            if (layout == null) status = vm.IsLoaded ? "Voicemeeter is not running" : "Voicemeeter not installed";
            else if (!haveAudio) status = layout.Name + ": no Windows device";
            else if (settings.Targets.Count == 0) status = layout.Name + ": nothing selected";
            else status = string.Format("{0}: {1:0}% → {2} dB{3}", layout.Name, level * 100,
                FormatDb(MapGain(level)), muted ? " (muted)" : "");

            string text = "VM Volume Sync\n" + status;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        // ---- menu --------------------------------------------------------------------------

        void BuildMenu()
        {
            menu.SuspendLayout();
            while (menu.Items.Count > 0) menu.Items[0].Dispose();

            string vmStatus = layout != null ? layout.Name + " connected"
                : vm.IsLoaded ? "Voicemeeter is not running" : "Voicemeeter Remote API not found";
            menu.Items.Add(new ToolStripMenuItem(vmStatus) { Enabled = false, Font = boldFont });
            menu.Items.Add(new ToolStripMenuItem("Following: " + (audio.DeviceName ?? "(device unavailable)")) { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            if (layout != null)
            {
                vm.RefreshParameters();

                var strips = Submenu("Inputs (strips)", true);
                for (int i = 0; i < layout.Strips; i++)
                {
                    if (i == layout.HwStrips) strips.DropDownItems.Add(new ToolStripSeparator());
                    string key = "Strip[" + i + "]";
                    string label = vm.GetString(key + ".Label");
                    strips.DropDownItems.Add(TargetItem(key,
                        string.IsNullOrEmpty(label) ? layout.DefaultStripName(i) : label,
                        (i < layout.HwStrips ? "HW " : "Virtual ") + (i + 1)));
                }
                menu.Items.Add(strips);

                var buses = Submenu("Outputs (buses)", true);
                for (int i = 0; i < layout.Buses; i++)
                {
                    if (i == layout.HwBuses) buses.DropDownItems.Add(new ToolStripSeparator());
                    string key = "Bus[" + i + "]";
                    string label = vm.GetString(key + ".Label");
                    buses.DropDownItems.Add(TargetItem(key,
                        string.IsNullOrEmpty(label) ? "Bus " + layout.BusName(i) : label,
                        layout.BusName(i)));
                }
                menu.Items.Add(buses);
            }
            else
            {
                menu.Items.Add(new ToolStripMenuItem("Start Voicemeeter to choose controls") { Enabled = false });
            }

            menu.Items.Add(new ToolStripSeparator());

            var device = Submenu("Windows output device", false);
            string defaultName = audio.DefaultDeviceName();
            device.DropDownItems.Add(Choice("Default device" + (defaultName != null ? " (" + defaultName + ")" : ""),
                settings.DeviceId.Length == 0, () => SetDevice("")));
            device.DropDownItems.Add(new ToolStripSeparator());
            foreach (AudioDevice d in audio.ListDevices())
            {
                string id = d.Id;
                device.DropDownItems.Add(Choice(d.Name, settings.DeviceId == id, () => SetDevice(id)));
            }
            menu.Items.Add(device);

            var range = Submenu("Gain range", false);
            range.DropDownItems.Add(new ToolStripMenuItem("At Windows 0%") { Enabled = false });
            foreach (float v in MinGainChoices)
            {
                float value = v;
                range.DropDownItems.Add(Choice(FormatDb(value) + " dB", settings.GainMin == value,
                    () => { settings.GainMin = value; SettingsChanged(); }));
            }
            range.DropDownItems.Add(new ToolStripSeparator());
            range.DropDownItems.Add(new ToolStripMenuItem("At Windows 100%") { Enabled = false });
            foreach (float v in MaxGainChoices)
            {
                float value = v;
                range.DropDownItems.Add(Choice(FormatDb(value) + " dB", settings.GainMax == value,
                    () => { settings.GainMax = value; SettingsChanged(); }));
            }
            menu.Items.Add(range);

            var curve = Submenu("Volume curve", false);
            curve.DropDownItems.Add(Choice("Linear in dB", !settings.LogCurve,
                () => { settings.LogCurve = false; SettingsChanged(); }));
            curve.DropDownItems.Add(Choice("Logarithmic (amplitude)", settings.LogCurve,
                () => { settings.LogCurve = true; SettingsChanged(); }));
            menu.Items.Add(curve);

            menu.Items.Add(Choice("Sync mute", settings.SyncMute,
                () => { settings.SyncMute = !settings.SyncMute; SettingsChanged(); }));
            var twoWay = Choice("Two-way sync", settings.TwoWay,
                () => { settings.TwoWay = !settings.TwoWay; SettingsChanged(); });
            twoWay.ToolTipText = "Moving a bound control in Voicemeeter also changes the Windows volume";
            menu.Items.Add(twoWay);

            if (layout != null)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Action("Show Voicemeeter", () => vm.SetParameters("Command.Show=1;")));
                menu.Items.Add(Action("Restart audio engine", () => vm.SetParameters("Command.Restart=1;")));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Choice("Start with Windows", IsStartupEnabled(), () => SetStartup(!IsStartupEnabled())));
            menu.Items.Add(Action("Exit", ExitThread));

            menu.ResumeLayout();
        }

        ToolStripMenuItem Submenu(string text, bool staysOpen)
        {
            var item = new ToolStripMenuItem(text);
            item.DropDown.ItemClicked += (s, e) => keepMenuOpen = staysOpen;
            item.DropDown.Closing += OnMenuClosing;
            return item;
        }

        ToolStripMenuItem TargetItem(string key, string text, string hint)
        {
            var item = new ToolStripMenuItem(text) { Checked = settings.Targets.Contains(key), ShortcutKeyDisplayString = hint };
            item.Click += (s, e) =>
            {
                if (!settings.Targets.Remove(key)) settings.Targets.Add(key);
                item.Checked = settings.Targets.Contains(key);
                SettingsChanged();
            };
            return item;
        }

        static ToolStripMenuItem Choice(string text, bool isChecked, Action onClick)
        {
            var item = new ToolStripMenuItem(text) { Checked = isChecked };
            item.Click += (s, e) => onClick();
            return item;
        }

        static ToolStripMenuItem Action(string text, Action onClick)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => onClick();
            return item;
        }

        // Keeps the menu open while ticking strips/buses so several can be selected at once.
        void OnMenuClosing(object sender, ToolStripDropDownClosingEventArgs e)
        {
            if (keepMenuOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            else keepMenuOpen = false;
        }

        void OnTrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // NotifyIcon only opens its menu on right-click; reuse its private helper so the menu is positioned natively.
            MethodInfo show = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (show != null) show.Invoke(tray, null);
        }

        void SetDevice(string id)
        {
            settings.DeviceId = id;
            audio.Bind(id);
            SettingsChanged();
        }

        void SettingsChanged()
        {
            settings.Save();
            targetStates.Clear();
            Push();
        }

        static string FormatDb(float db)
        {
            return db.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);
        }

        // ---- start with Windows (per-user Run key, no admin rights needed) -------------------

        static bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                string value = key == null ? null : key.GetValue(RunValueName) as string;
                return value != null &&
                    string.Equals(value.Trim().Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        static void SetStartup(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enable) key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunValueName, false);
            }
        }

        // ---- system events -------------------------------------------------------------------

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // Light/dark taskbar switch: redraw the icon in the matching colour.
            if (e.Category != UserPreferenceCategory.General) return;
            icons.Reset();
            UpdateTray();
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            audio.Bind(settings.DeviceId);
            forcePushTicks = 3;
        }

        protected override void ExitThreadCore()
        {
            tray.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                timer.Dispose();
                syncTimer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                boldFont.Dispose();
                icons.Dispose();
                audio.Dispose();
                vm.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    sealed class TargetState
    {
        public float Gain = float.NaN;  // NaN = unknown
        public int Mute = -1;           // -1 = unknown
        public int SentAt = Environment.TickCount - 60000;
    }

    // ------------------------------------------------------------------------------------------
    // Settings (INI next to the exe when that folder is writable, otherwise %APPDATA%)
    // ------------------------------------------------------------------------------------------

    sealed class Settings
    {
        const string FileName = "VMVolumeSync.ini";

        public string DeviceId = "";
        public float GainMin = -60f;
        public float GainMax = 12f;
        public bool LogCurve;
        public bool SyncMute = true;
        public bool TwoWay;
        public string VoicemeeterDir = "";
        public readonly SortedSet<string> Targets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        public string FilePath { get; private set; }

        static readonly Regex TargetPattern = new Regex(@"^(strip|bus)\[(\d+)\]$", RegexOptions.IgnoreCase);

        public static Settings Load()
        {
            var s = new Settings { FilePath = ResolvePath() };
            if (!File.Exists(s.FilePath)) return s;
            try
            {
                foreach (string raw in File.ReadAllLines(s.FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string value = line.Substring(eq + 1).Trim();
                    switch (line.Substring(0, eq).Trim().ToLowerInvariant())
                    {
                        case "targets":
                            foreach (string t in value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                string normalized = NormalizeTarget(t);
                                if (normalized != null) s.Targets.Add(normalized);
                            }
                            break;
                        case "device": s.DeviceId = value; break;
                        case "gainmin": s.GainMin = ParseGain(value, s.GainMin); break;
                        case "gainmax": s.GainMax = ParseGain(value, s.GainMax); break;
                        case "curve": s.LogCurve = value.StartsWith("log", StringComparison.OrdinalIgnoreCase); break;
                        case "syncmute": s.SyncMute = ParseBool(value); break;
                        case "twoway": s.TwoWay = ParseBool(value); break;
                        case "voicemeeterdir": s.VoicemeeterDir = value.Trim('"'); break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return s;
        }

        public void Save()
        {
            string[] lines =
            {
                "; Voicemeeter Volume Sync settings",
                "; Targets: Strip[n] / Bus[n] (zero-based), separated by commas",
                "; GainMin/GainMax: dB applied at Windows 0% / 100% (Voicemeeter accepts -60..12)",
                "; Curve: linear | log    Device: empty = Windows default output device",
                "; TwoWay: 1 = changes made in Voicemeeter also drive the Windows volume",
                "; VoicemeeterDir: optional folder containing VoicemeeterRemote64.dll",
                "[VMVolumeSync]",
                "Targets=" + string.Join(",", Targets),
                "Device=" + DeviceId,
                "GainMin=" + GainMin.ToString(CultureInfo.InvariantCulture),
                "GainMax=" + GainMax.ToString(CultureInfo.InvariantCulture),
                "Curve=" + (LogCurve ? "log" : "linear"),
                "SyncMute=" + (SyncMute ? "1" : "0"),
                "TwoWay=" + (TwoWay ? "1" : "0"),
                "VoicemeeterDir=" + VoicemeeterDir,
            };
            try
            {
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                string fallback = AppDataPath();
                if (string.Equals(fallback, FilePath, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fallback));
                    File.WriteAllLines(fallback, lines);
                    FilePath = fallback;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        static string NormalizeTarget(string text)
        {
            Match m = TargetPattern.Match(text.Trim());
            if (!m.Success) return null;
            bool isStrip = m.Groups[1].Value.Equals("strip", StringComparison.OrdinalIgnoreCase);
            return (isStrip ? "Strip[" : "Bus[") + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) + "]";
        }

        static bool ParseBool(string text)
        {
            return text != "0" && !text.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        static float ParseGain(string text, float fallback)
        {
            float v;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return fallback;
            return Math.Max(-60f, Math.Min(12f, v));
        }

        static string ResolvePath()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            string portable = Path.Combine(exeDir, FileName);
            string roaming = AppDataPath();
            if (File.Exists(portable)) return portable;
            if (File.Exists(roaming)) return roaming;
            return CanWrite(exeDir) ? portable : roaming;
        }

        static string AppDataPath()
        {
            return Path.Combine(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMVolumeSync"), FileName);
        }

        static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".vmvolumesync-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // Voicemeeter editions
    // ------------------------------------------------------------------------------------------

    sealed class VmLayout
    {
        public int Kind;
        public string Name;
        public int Strips, HwStrips, Buses, HwBuses;
        public string[] VirtualStripNames;

        static readonly Regex TargetPattern = new Regex(@"^(Strip|Bus)\[(\d+)\]$");

        // Kind values come from VBVMR_GetVoicemeeterType: 1 = Voicemeeter, 2 = Banana, 3 = Potato.
        public static VmLayout For(int kind)
        {
            switch (kind)
            {
                case 0: return null;
                case 1: return new VmLayout { Kind = 1, Name = "Voicemeeter", Strips = 3, HwStrips = 2, Buses = 2, HwBuses = 1,
                    VirtualStripNames = new[] { "Voicemeeter VAIO" } };
                case 2: return new VmLayout { Kind = 2, Name = "Voicemeeter Banana", Strips = 5, HwStrips = 3, Buses = 5, HwBuses = 3,
                    VirtualStripNames = new[] { "Voicemeeter VAIO", "Voicemeeter AUX" } };
                default: return new VmLayout { Kind = kind, Name = "Voicemeeter Potato", Strips = 8, HwStrips = 5, Buses = 8, HwBuses = 5,
                    VirtualStripNames = new[] { "Voicemeeter VAIO", "Voicemeeter AUX", "Voicemeeter VAIO3" } };
            }
        }

        public string DefaultStripName(int index)
        {
            if (index < HwStrips) return "Hardware Input " + (index + 1);
            int v = index - HwStrips;
            return v < VirtualStripNames.Length ? VirtualStripNames[v] : "Virtual Input " + (v + 1);
        }

        public string BusName(int index)
        {
            if (Kind == 1) return index == 0 ? "A" : "B";
            return index < HwBuses ? "A" + (index + 1) : "B" + (index - HwBuses + 1);
        }

        public bool IsValidTarget(string target)
        {
            Match m = TargetPattern.Match(target);
            if (!m.Success) return false;
            int index = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return index < (m.Groups[1].Value == "Strip" ? Strips : Buses);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Voicemeeter Remote API (VoicemeeterRemote64.dll, installed with every Voicemeeter edition)
    // ------------------------------------------------------------------------------------------

    sealed class VoicemeeterRemote : IDisposable
    {
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int NoArgFn();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int GetTypeFn(out int type);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        delegate int SetParametersFn(string script);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        delegate int GetStringWFn(string name, IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        delegate int GetFloatFn(string name, out float value);

        IntPtr module;
        NoArgFn login, logout, isParametersDirty;
        GetTypeFn getType;
        SetParametersFn setParameters;
        GetStringWFn getStringW;
        GetFloatFn getFloat;
        bool loggedIn;
        bool dirty;     // sticky copy of VBVMR_IsParametersDirty, which resets on every call
        int failedPolls;

        public bool IsLoaded { get { return module != IntPtr.Zero; } }

        public bool Open(string overrideDir)
        {
            string dllName = IntPtr.Size == 8 ? "VoicemeeterRemote64.dll" : "VoicemeeterRemote.dll";
            foreach (string dir in CandidateDirs(overrideDir))
            {
                string path = Path.Combine(dir, dllName);
                if (!File.Exists(path)) continue;
                module = LoadLibrary(path);
                if (module != IntPtr.Zero) break;
            }
            if (module == IntPtr.Zero) return false;

            try
            {
                login = Bind<NoArgFn>("VBVMR_Login");
                logout = Bind<NoArgFn>("VBVMR_Logout");
                isParametersDirty = Bind<NoArgFn>("VBVMR_IsParametersDirty");
                getType = Bind<GetTypeFn>("VBVMR_GetVoicemeeterType");
                setParameters = Bind<SetParametersFn>("VBVMR_SetParameters");
                getStringW = Bind<GetStringWFn>("VBVMR_GetParameterStringW");
                getFloat = Bind<GetFloatFn>("VBVMR_GetParameterFloat");
            }
            catch (EntryPointNotFoundException)
            {
                FreeLibrary(module);
                module = IntPtr.Zero;
                return false;
            }

            Login();
            return true;
        }

        // Returns the running edition (1/2/3) or 0 when Voicemeeter is not reachable.
        public int PollKind()
        {
            if (!IsLoaded) return 0;
            if (!loggedIn) Login();

            int kind;
            if (loggedIn && getType(out kind) == 0)
            {
                failedPolls = 0;
                RefreshParameters();
                return kind;
            }

            // Voicemeeter was started/restarted after we logged in: re-open the connection now and then.
            if (++failedPolls % 5 == 0)
            {
                Logout();
                Login();
            }
            return 0;
        }

        // Updates the client-side parameter cache; remembers whether anything changed for TakeDirty().
        public void RefreshParameters()
        {
            if (loggedIn && isParametersDirty() == 1) dirty = true;
        }

        public bool TakeDirty()
        {
            RefreshParameters();
            bool result = dirty;
            dirty = false;
            return result;
        }

        public void MarkDirty()
        {
            dirty = true;
        }

        public bool GetFloat(string name, out float value)
        {
            value = 0f;
            return loggedIn && getFloat(name, out value) == 0;
        }

        public bool SetParameters(string script)
        {
            return loggedIn && setParameters(script) == 0;
        }

        public string GetString(string name)
        {
            if (!loggedIn) return null;
            IntPtr buffer = Marshal.AllocHGlobal(1024); // API contract: 512 WCHARs
            try
            {
                Marshal.WriteInt16(buffer, 0);
                return getStringW(name, buffer) == 0 ? Marshal.PtrToStringUni(buffer).Trim() : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        void Login()
        {
            // 0 = connected, 1 = OK but Voicemeeter not running yet; negative values are errors.
            loggedIn = login() >= 0;
        }

        void Logout()
        {
            if (loggedIn) logout();
            loggedIn = false;
        }

        T Bind<T>(string name) where T : class
        {
            IntPtr fn = GetProcAddress(module, name);
            if (fn == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        static IEnumerable<string> CandidateDirs(string overrideDir)
        {
            if (!string.IsNullOrEmpty(overrideDir)) yield return overrideDir;
            yield return Path.GetDirectoryName(Application.ExecutablePath);

            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                string dir = null;
                try
                {
                    using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey key = hklm.OpenSubKey(UninstallKey))
                    {
                        string uninstaller = key == null ? null : key.GetValue("UninstallString") as string;
                        if (!string.IsNullOrEmpty(uninstaller)) dir = Path.GetDirectoryName(uninstaller.Trim().Trim('"'));
                    }
                }
                catch (Exception) { }
                if (dir != null) yield return dir;
            }

            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"VB\Voicemeeter");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"VB\Voicemeeter");
        }

        public void Dispose()
        {
            if (!IsLoaded) return;
            Logout();
            FreeLibrary(module);
            module = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Windows Core Audio: watches one output device's master volume/mute
    // ------------------------------------------------------------------------------------------

    sealed class AudioDevice
    {
        public string Id;
        public string Name;
    }

    sealed class AudioWatcher : IMMNotificationClient, IAudioEndpointVolumeCallback, IDisposable
    {
        const int ClsCtxAll = 23;
        const int DeviceStateActive = 1;
        static readonly PropertyKey FriendlyNameKey = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant value);

        readonly SynchronizationContext ui;
        IMMDeviceEnumerator enumerator;
        IAudioEndpointVolume endpoint;
        int volumePending, devicesPending;

        public event Action VolumeChanged;
        public event Action DevicesChanged;

        public string DeviceId { get; private set; }
        public string DeviceName { get; private set; }
        public bool IsBound { get { return endpoint != null; } }

        public AudioWatcher(SynchronizationContext ui)
        {
            this.ui = ui;
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.RegisterEndpointNotificationCallback(this);
        }

        // Binds to the given device id (empty = default output). Returns true when the binding changed.
        public bool Bind(string wantedId)
        {
            IMMDevice device = null;
            try
            {
                if (string.IsNullOrEmpty(wantedId))
                {
                    enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
                }
                else
                {
                    enumerator.GetDevice(wantedId, out device);
                    int state;
                    device.GetState(out state);
                    if (state != DeviceStateActive)
                    {
                        Release(device);
                        device = null;
                    }
                }
            }
            catch (COMException)
            {
                device = null; // no such device / no output devices at all
            }

            try
            {
                string id = device == null ? null : GetId(device);
                if (endpoint != null && id != null && id == DeviceId) return false;

                bool wasBound = endpoint != null;
                Unbind();
                if (device == null) return wasBound;

                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object instance;
                device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out instance);
                endpoint = (IAudioEndpointVolume)instance;
                endpoint.RegisterControlChangeNotify(this);
                DeviceId = id;
                DeviceName = GetFriendlyName(device);
                return true;
            }
            catch (COMException)
            {
                Unbind();
                return true;
            }
            finally
            {
                Release(device);
            }
        }

        public bool TryRead(out float level, out bool muted)
        {
            level = 0f;
            muted = false;
            if (endpoint == null) return false;
            try
            {
                endpoint.GetMasterVolumeLevelScalar(out level);
                endpoint.GetMute(out muted);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
        }

        public bool TrySetVolume(float level)
        {
            if (endpoint == null) return false;
            try
            {
                Guid context = Guid.Empty;
                endpoint.SetMasterVolumeLevelScalar(Math.Max(0f, Math.Min(1f, level)), ref context);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
        }

        public bool TrySetMute(bool mute)
        {
            if (endpoint == null) return false;
            try
            {
                Guid context = Guid.Empty;
                endpoint.SetMute(mute, ref context);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
        }

        public string DefaultDeviceName()
        {
            IMMDevice device = null;
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
                return GetFriendlyName(device);
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                Release(device);
            }
        }

        public List<AudioDevice> ListDevices()
        {
            var result = new List<AudioDevice>();
            IMMDeviceCollection collection = null;
            try
            {
                enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out collection);
                int count;
                collection.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        collection.Item(i, out device);
                        result.Add(new AudioDevice { Id = GetId(device), Name = GetFriendlyName(device) ?? "Unknown device" });
                    }
                    catch (COMException) { }
                    finally { Release(device); }
                }
            }
            catch (COMException) { }
            finally { Release(collection); }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        void Unbind()
        {
            if (endpoint != null)
            {
                try { endpoint.UnregisterControlChangeNotify(this); }
                catch (COMException) { }
                Release(endpoint);
            }
            endpoint = null;
            DeviceId = null;
            DeviceName = null;
        }

        static string GetId(IMMDevice device)
        {
            string id;
            device.GetId(out id);
            return id;
        }

        static string GetFriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                device.OpenPropertyStore(0 /* STGM_READ */, out store);
                PropertyKey key = FriendlyNameKey;
                PropVariant value;
                store.GetValue(ref key, out value);
                try
                {
                    return value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.p1) : null;
                }
                finally
                {
                    PropVariantClear(ref value);
                }
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                Release(store);
            }
        }

        static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }

        // Core Audio calls these on its own worker threads: coalesce and hop to the UI thread.

        void PostVolume()
        {
            if (Interlocked.Exchange(ref volumePending, 1) != 0) return;
            ui.Post(_ =>
            {
                Interlocked.Exchange(ref volumePending, 0);
                Action handler = VolumeChanged;
                if (handler != null && enumerator != null) handler();
            }, null);
        }

        void PostDevices()
        {
            if (Interlocked.Exchange(ref devicesPending, 1) != 0) return;
            ui.Post(_ =>
            {
                Interlocked.Exchange(ref devicesPending, 0);
                Action handler = DevicesChanged;
                if (handler != null && enumerator != null) handler();
            }, null);
        }

        int IAudioEndpointVolumeCallback.OnNotify(IntPtr data) { PostVolume(); return 0; }
        int IMMNotificationClient.OnDeviceStateChanged(string deviceId, int newState) { PostDevices(); return 0; }
        int IMMNotificationClient.OnDeviceAdded(string deviceId) { PostDevices(); return 0; }
        int IMMNotificationClient.OnDeviceRemoved(string deviceId) { PostDevices(); return 0; }
        int IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) { return 0; }

        int IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string defaultDeviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Console) PostDevices();
            return 0;
        }

        public void Dispose()
        {
            if (enumerator == null) return;
            Unbind();
            try { enumerator.UnregisterEndpointNotificationCallback(this); }
            catch (COMException) { }
            Release(enumerator);
            enumerator = null;
        }
    }

    // ---- Core Audio COM interop ------------------------------------------------------------

    enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
        public PropertyKey(Guid formatId, int propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public IntPtr p1;
        public IntPtr p2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IMMNotificationClient client);
        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    // ------------------------------------------------------------------------------------------
    // Tray icon, drawn at runtime so the app needs no asset files
    // ------------------------------------------------------------------------------------------

    enum IconState { Normal, Muted, Disconnected }

    sealed class TrayIcons : IDisposable
    {
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        readonly Dictionary<IconState, Icon> cache = new Dictionary<IconState, Icon>();

        public Icon Get(IconState state)
        {
            Icon icon;
            if (!cache.TryGetValue(state, out icon))
            {
                icon = Draw(state);
                cache[state] = icon;
            }
            return icon;
        }

        public void Reset()
        {
            foreach (Icon icon in cache.Values) icon.Dispose();
            cache.Clear();
        }

        public void Dispose()
        {
            Reset();
        }

        static bool TaskbarIsLight()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    return value is int && (int)value == 1;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        static Icon Draw(IconState state)
        {
            int size = SystemInformation.SmallIconSize.Width;
            float u = size / 16f;
            Color fg = TaskbarIsLight() ? Color.FromArgb(28, 28, 28) : Color.White;
            if (state == IconState.Disconnected) fg = Color.FromArgb(150, fg);

            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (var brush = new SolidBrush(fg))
                using (var pen = new Pen(fg, 1.4f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    g.FillPolygon(brush, new[]
                    {
                        new PointF(1.5f * u, 5.5f * u), new PointF(4.5f * u, 5.5f * u), new PointF(8.5f * u, 2f * u),
                        new PointF(8.5f * u, 14f * u), new PointF(4.5f * u, 10.5f * u), new PointF(1.5f * u, 10.5f * u),
                    });

                    if (state == IconState.Normal)
                    {
                        g.DrawArc(pen, 7f * u, 5f * u, 6f * u, 6f * u, -50, 100);
                        g.DrawArc(pen, 4.5f * u, 2.5f * u, 11f * u, 11f * u, -50, 100);
                    }
                    else if (state == IconState.Muted)
                    {
                        g.DrawLine(pen, 10.5f * u, 5.5f * u, 14.5f * u, 10.5f * u);
                        g.DrawLine(pen, 14.5f * u, 5.5f * u, 10.5f * u, 10.5f * u);
                    }
                    else
                    {
                        using (var dot = new SolidBrush(Color.FromArgb(232, 72, 52)))
                            g.FillEllipse(dot, 9.5f * u, 8.5f * u, 6.5f * u, 6.5f * u);
                    }
                }

                IntPtr handle = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(handle)) return (Icon)temp.Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }
    }
}
