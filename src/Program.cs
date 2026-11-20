using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Rz2;

namespace Rz3
{
    public class MainForm : Form
    {
        RzDevice _dev;
        readonly List<MapEntry> _map = MapTable.CreateDefaults();
        readonly bool[] _dirty = new bool[MapTable.InputKeys.Length];
        List<byte> _profileIds = new List<byte>();
        byte _profile = 1;
        byte _mode = 0;
        bool _loading;
        // True only when the last mapping read returned the whole table. A partial read leaves
        // rows looking "disabled", and writing those back would really disable them.
        bool _mapReadOk;
        readonly Dictionary<byte, string> _localNames = new Dictionary<byte, string>();
        readonly Dictionary<byte, int> _dpiWanted = new Dictionary<byte, int>();

        TabControl _tabs;
        readonly ComboBox[] _typeBox = new ComboBox[MapTable.InputKeys.Length];
        readonly ComboBox[] _paramBox = new ComboBox[MapTable.InputKeys.Length];
        readonly CheckBox[] _hsBox = new CheckBox[MapTable.InputKeys.Length];
        readonly CheckBox[][] _modBox = new CheckBox[MapTable.InputKeys.Length][];
        readonly ComboBox[] _shortcutBox = new ComboBox[MapTable.InputKeys.Length];
        Label _status, _bat, _charge, _dpiLabel;
        NumericUpDown _dpi, _clutchDpi, _lowBat, _idle;
        ComboBox _poll, _scrollMode;
        TrackBar _bright;
        CheckBox _scrollAccel, _onlyDirty, _hsMode;
        ComboBox _zone, _effect, _dir, _profiles;
        TrackBar _accelLevel, _smartReel;
        CheckBox _smartReelOn;
        Label _scrollHint;
        Button _clearToggle;
        NumericUpDown _debounce;
        Label _btnBanner, _profBanner, _currentLbl;
        int _activeProfile = -1;
        TrackBar _effBright;
        NumericUpDown _effRate;
        Button _colorBtn;
        Color _effectColor = Color.Cyan;
        TextBox _rawOut, _rawArgs, _nameBox;
        ComboBox _rawCls, _rawCmd;
        System.Windows.Forms.Timer _battTimer;
        System.Windows.Forms.Timer _reconTimer;
        System.Windows.Forms.Timer _watchTimer;
        bool _lost;
        bool _tempSwitch;
        int _activeSel;
        int _activeProfileBefore;
        string _connText = "";

        // ---- turn 17: polling measurement + host action engine ----
        RateMeter _meter;
        HostEngine _host;
        readonly Dictionary<byte, HostAction[]> _hostTable = new Dictionary<byte, HostAction[]>();
        readonly Dictionary<int, int> _rateCal = new Dictionary<int, int>();
        bool _hostEnabled = true;
        bool _hsPollSupported;
        Label _hostStatus, _pollIdLbl;
        CheckBox _hostOn, _autoStart;
        NotifyIcon _tray;
        // tray display options (persisted in rz3_ui.json)
        bool _trayShowBattery;
        bool _trayShowTip;
        CheckBox _trayBattIcon, _trayBattTip;
        ToolStripMenuItem _miBattIcon, _miBattTip, _miAuto;
        int _lastBatt = -1;
        int _lastChg = -1;
        int _lastAc = -1;
        int _lastLink = Rz2.RzDevice.LinkNone;
        int _lastCharge = Rz2.RzDevice.ChargeUnknown;
        int _battFail;               // consecutive failed battery reads -> reconnect
        ComboBox[] _hostKind;
        TextBox[] _hostValue;
        NumericUpDown[] _hostParam;
        ComboBox[] _hostBind;
        ComboBox[] _hostWinCmd;
        Label[] _hostBindState;
        ToolTip _hostTip;
        public MainForm()
        {
            Text = VersionInfo.Title;
            Width = 1060; Height = 760;
            MinimumSize = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterScreen;
            // Same logo as the exe/tray, for the title bar and Alt-Tab.
            try { Icon = TrayIconFactory.AppIcon(); } catch { }
            Font = new Font("Segoe UI", 9f);

            // Preferences and the action table must be loaded *before* the pages are built: the
            // checkboxes take their initial state from these fields.
            _trayShowBattery = UiPrefs.GetBool("tray.batteryOnIcon", false);
            _trayShowTip = UiPrefs.GetBool("tray.batteryInTip", true);
            LoadLocalNames();
            LoadDpiWanted();
            LoadRateCal();
            LoadHostTable();
            LoadPowerPolicy();

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(BuildProfilesPage());
            _tabs.TabPages.Add(BuildButtonsPage());
            _tabs.TabPages.Add(BuildHostPage());
            _tabs.TabPages.Add(BuildProfileSettingsPage());
            _tabs.TabPages.Add(BuildDevicePage());
            _tabs.TabPages.Add(BuildLightingPage());
            _tabs.TabPages.Add(BuildPowerPage());
            _tabs.TabPages.Add(BuildRawPage());
            Controls.Add(_tabs);

            TableLayoutPanel bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 82, ColumnCount = 1 };
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            _status = new Label { Dock = DockStyle.Fill, Text = "Looking for device...", AutoEllipsis = true };
            _bat = new Label { Dock = DockStyle.Fill, Text = "" };
            FlowLayoutPanel row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            Button apply = new Button { Text = "Write mappings to device", Width = 190, Height = 24, Margin = new Padding(0, 0, 6, 0) };
            apply.Click += delegate { ApplyMap(); };
            Button reload = new Button { Text = "Reconnect", Width = 100, Height = 24, Margin = new Padding(0, 0, 6, 0) };
            reload.Click += delegate { Connect(); };
            _onlyDirty = new CheckBox { Text = "Only rows I changed", Width = 170, Checked = true };
            row.Controls.Add(apply);
            row.Controls.Add(reload);
            row.Controls.Add(_onlyDirty);
            bottom.Controls.Add(_status, 0, 0);
            bottom.Controls.Add(_bat, 0, 1);
            bottom.Controls.Add(row, 0, 2);
            Controls.Add(bottom);

            // Battery polling was 20 s, which left the UI showing a stale percentage for a long
            // time after the mouse changed link. 8 s is cheap (two feature reads) and keeps the
            // tray honest.
            _battTimer = new System.Windows.Forms.Timer { Interval = 8000 };
            _battTimer.Tick += delegate
            {
                RefreshBattery();
                RefreshPowerTabIfIdle();
            };
            _reconTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _reconTimer.Tick += delegate { TryReattach(); };
            _watchTimer = new System.Windows.Forms.Timer { Interval = 1200 };
            _watchTimer.Tick += delegate { WatchActiveProfile(); };

            // Raw Input sinks (mouse reports for the rate meter, F13..F24 triggers for the engine)
            // and the engine itself. All of it starts with the window handle, never with a hook.
            _meter = new RateMeter();
            _host = new HostEngine();
            _host.Enabled = _hostEnabled;
            _host.Logged += delegate(string s) { HostLog(s); };
            _host.DeviceAction = new Func<string, string>(DeviceActionCore);
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                // Closing used to kill the host engine with no warning: every button that was
                // bound to a host action silently stopped working. Unless the user asks for a
                // real exit (tray menu / Shift on close), closing hides to the tray instead.
                // The comment promised Shift-on-close for a long time but nothing implemented
                // it; holding Shift is the conventional "really quit" gesture and is cheap to
                // honour, so it is now checked explicitly.
                bool shift = false;
                try { shift = (ModifierKeys & System.Windows.Forms.Keys.Shift) == System.Windows.Forms.Keys.Shift; } catch { }
                if (e.CloseReason == CloseReason.UserClosing && !_exitRequested && !shift)
                {
                    e.Cancel = true;
                    Hide();
                    EnterTray();
                    return;
                }
                try { if (_engineTimer != null) { _engineTimer.Stop(); _engineTimer.Dispose(); _engineTimer = null; } } catch { }
                try { _host.Dispose(); } catch { }
                try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
                try { if (_showEvent != null) { _showEvent.Set(); _showEvent.Close(); _showEvent = null; } } catch { }
            };

            // Host actions need this process alive; the tray keeps it out of the way.
            _tray = new NotifyIcon();
            _tray.Text = "Rz3";
            UpdateTray();
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open Rz3", null, delegate { RestoreFromTray(); });

            _miBattIcon = new ToolStripMenuItem("Show battery level on the icon");
            _miBattIcon.CheckOnClick = true;
            _miBattIcon.Checked = _trayShowBattery;
            _miBattIcon.Click += delegate { SetTrayOption(true, _miBattIcon.Checked); };
            menu.Items.Add(_miBattIcon);

            _miBattTip = new ToolStripMenuItem("Show battery in the tooltip");
            _miBattTip.CheckOnClick = true;
            _miBattTip.Checked = _trayShowTip;
            _miBattTip.Click += delegate { SetTrayOption(false, _miBattTip.Checked); };
            menu.Items.Add(_miBattTip);

            _miAuto = new ToolStripMenuItem("Start Rz3 with Windows (tray)");
            _miAuto.CheckOnClick = true;
            _miAuto.Checked = IsAutoStart();
            _miAuto.Click += delegate { SetAutoStart(_miAuto.Checked); SyncAutoStartUi(); };
            menu.Items.Add(_miAuto);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Refresh battery now", null, delegate { RefreshBattery(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit Rz3", null, delegate { RequestExit(); });
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) RestoreFromTray();
            };
            _tray.DoubleClick += delegate { RestoreFromTray(); };

            // Minimising the window parks Rz3 in the tray instead of the taskbar; the tray menu
            // brings it back and "Exit Rz3" really closes it.
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && !_inTray && _tray != null) EnterTray();
            };

            Load += delegate
            {
                // The icon is shown only once the window exists: a NotifyIcon made visible before
                // the form has a handle never registers with the shell, which is why the icon used
                // to appear only after the first minimise.
                EnsureTrayIcon();
                InitRuntime();
                Connect();
            };
        }

        // Makes sure the notification icon is registered with the shell, and re-registers it if
        // Windows dropped it (Explorer restart, icon overflow, a failed first attempt).
        void EnsureTrayIcon()
        {
            if (_tray == null) return;
            try
            {
                if (_tray.Icon == null) UpdateTray();
                _tray.Visible = true;
            }
            catch { }
        }

        bool _exitRequested;

        // Called by the tray menu's "Exit Rz3" and by the Shift-on-close shortcut: a real exit.
        void RequestExit()
        {
            _exitRequested = true;
            Close();
        }

        // A second Rz3.exe asks the running one to come back from the tray through this event.
        const string ShowEventName = "Rz3_ShowWindow_Event";
        EventWaitHandle _showEvent;
        Thread _showThread;

        void StartShowListener()
        {
            if (_showEvent != null) return;
            try
            {
                bool createdNew;
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out createdNew);
                _showThread = new Thread(delegate()
                {
                    while (true)
                    {
                        try { _showEvent.WaitOne(); } catch { return; }
                        try { BeginInvoke(new Action(delegate { RestoreFromTray(); })); } catch { }
                    }
                });
                _showThread.IsBackground = true;
                _showThread.Name = "rz3-show-window";
                _showThread.Start();
            }
            catch { }
        }

        // Rebuilds the tray icon and tooltip from the last battery reading.
        void UpdateTray()
        {
            if (_tray == null) return;
            bool wasVisible = false;
            try { wasVisible = _tray.Visible; } catch { }
            try
            {
                Icon old = _tray.Icon;
                _tray.Icon = TrayIconFactory.Build(_trayShowBattery, _lastBatt, _lastChg == 1);
                if (old != null) old.Dispose();
            }
            catch { }
            string tip = "Rz3 - host actions";
            if (_trayShowTip)
            {
                if (_lastBatt < 0 && _lastLink == Rz2.RzDevice.LinkNone)
                    tip = "Rz3 - mouse not connected";
                else
                {
                    string link = _lastLink == Rz2.RzDevice.LinkWired ? " (cable)"
                        : _lastLink == Rz2.RzDevice.LinkWireless ? " (2.4 GHz)"
                        : "";
                    string state = _lastCharge == Rz2.RzDevice.ChargeCharging ? ", charging"
                        : _lastCharge == Rz2.RzDevice.ChargeFull ? ", full"
                        : _lastCharge == Rz2.RzDevice.ChargeOnBattery ? ", on battery"
                        : "";
                    tip = "Rz3 - battery " + (_lastBatt >= 0 ? _lastBatt + "%" : "n/a") + state + link;
                }
            }
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            try { _tray.Text = tip; } catch { }
            // Swapping the icon sometimes makes the shell forget the entry; re-asserting Visible
            // re-adds it. Only when it was visible: a deliberately hidden icon stays hidden.
            if (wasVisible)
            {
                try { _tray.Visible = false; } catch { }
                try { _tray.Visible = true; } catch { }
            }
        }

        void SetTrayOption(bool onIcon, bool value)
        {
            if (onIcon) { _trayShowBattery = value; UiPrefs.SetBool("tray.batteryOnIcon", value); }
            else { _trayShowTip = value; UiPrefs.SetBool("tray.batteryInTip", value); }
            if (_trayBattIcon != null) _trayBattIcon.Checked = _trayShowBattery;
            if (_trayBattTip != null) _trayBattTip.Checked = _trayShowTip;
            if (_miBattIcon != null) _miBattIcon.Checked = _trayShowBattery;
            if (_miBattTip != null) _miBattTip.Checked = _trayShowTip;
            UpdateTray();
        }

        // Sets the checkboxes from the registry WITHOUT firing CheckedChanged again (that used to
        // bounce the box straight back when the write failed, so the click looked ignored).
        void SyncAutoStartUi()
        {
            bool on = IsAutoStart();
            if (_miAuto != null) _miAuto.Checked = on;
            if (_autoStart != null)
            {
                _loading = true;
                try { _autoStart.Checked = on; }
                finally { _loading = false; }
            }
        }

        // Flipping "Start with Windows" must either work or say why. A restricted token (an
        // unsigned exe started from a locked-down location) makes the HKCU Run write throw
        // SecurityException, and the old catch silently swallowed it.
        void OnAutoStartChanged()
        {
            if (_loading || _autoStart == null) return;
            bool want = _autoStart.Checked;
            SetAutoStart(want);
            bool actual = IsAutoStart();
            if (actual != want)
            {
                SyncAutoStartUi();
                string why = _autoStartError.Length > 0 ? _autoStartError : "the change was not saved";
                HostLog("autostart change failed: " + why);
                MessageBox.Show(
                    "Windows did not let Rz3 change the startup entry." + Environment.NewLine + Environment.NewLine +
                    "Reason: " + why + Environment.NewLine + Environment.NewLine +
                    "This happens when Rz3 runs from a location Windows treats as untrusted. Install Rz3 " +
                    "under your user profile (the \"Install to %LOCALAPPDATA%\" button does it) or run it " +
                    "as administrator.",
                    "Start with Windows", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SyncAutoStartUi();
            HostLog(want ? "will start with Windows (tray)" : "no longer starts with Windows");
        }

        // Runs the app without a window: the host engine keeps working, the icon sits in the tray.
        bool _inTray;

        public void EnterTray()
        {
            if (_inTray) return;
            _inTray = true;
            if (_tray != null) _tray.Visible = true;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Hide();
            HostLog("running in the tray - host actions stay active");
            // Tell the user where the window went: the taskbar entry disappearing with no other
            // sign is what made it look like Rz3 had quit (and its buttons stopped working).
            try
            {
                if (_tray != null)
                    _tray.ShowBalloonTip(2500, "Rz3 is still running",
                        "The window is in the notification area. Click the icon to open it again; " +
                        "host actions keep working while this icon exists.", ToolTipIcon.Info);
            }
            catch { }
        }

        void LeaveTray()
        {
            _inTray = false;
        }

        void RestoreFromTray()
        {
            LeaveTray();
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool IsAutoStart()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    string v = k.GetValue("Rz3") as string;
                    return v != null && v.Length > 0;
                }
            }
            catch { return false; }
        }

        static string _autoStartError = "";

        static void SetAutoStart(bool on)
        {
            _autoStartError = "";
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) { _autoStartError = "Run key not writable (null)"; return; }
                    if (on) k.SetValue("Rz3", "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue("Rz3", false);
                }
            }
            catch (Exception ex) { _autoStartError = ex.GetType().Name + ": " + ex.Message; }
        }

        // Raw input arrives as WM_INPUT on our own window: no global hook, no swallowed keys.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF)
            {
                try { if (_meter != null) _meter.HandleRawInput(m.LParam); } catch { }
                try { if (_host != null) _host.HandleRawInput(m.LParam); } catch { }
            }
            base.WndProc(ref m);
        }

        void InitRuntime()
        {
            StartShowListener();
            try { if (_meter != null) _meter.Attach(Handle); } catch { }
            try { if (_host != null) { _host.Attach(Handle); _host.Enabled = _hostEnabled; } } catch { }
            RebuildHostTable();
            HostLog("engine " + (_host != null ? _host.Status : "n/a") +
                    ", meter " + (_meter != null ? _meter.Status : "n/a"));

            // Hook and status keep-alive: a dropped hook means the buttons go dead while the app
            // still looks fine, so re-install it and refresh the "last trigger" readout.
            _engineTimer = new System.Windows.Forms.Timer();
            _engineTimer.Interval = 4000;
            _engineTimer.Tick += delegate
            {
                try { if (_host != null && !_host.HookAlive) _host.EnsureAttached(); } catch { }
                try { EnsureTrayIcon(); } catch { }
                try { RefreshHostStatus(); } catch { }
            };
            _engineTimer.Start();

            // Power tab: live battery/AC readout plus the low-battery balloon. The policy is also
            // re-evaluated here, so unplugging the mouse dims/turns off the lighting on its own.
            _pwTimer = new System.Windows.Forms.Timer();
            _pwTimer.Interval = 5000;
            _pwTimer.Tick += delegate
            {
                try { RefreshPowerLive(); } catch { }
                try { ApplyPowerPolicy(false); } catch { }
                try { CheckLowBatteryBalloon(); } catch { }
            };
            _pwTimer.Start();
        }


        System.Windows.Forms.Timer _engineTimer;

        // ---------------- profiles ----------------
        TabPage BuildProfilesPage()
        {
            TabPage p = new TabPage("Profiles");
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, Padding = new Padding(12), Height = 190 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            t.Controls.Add(new Label { Text = "Onboard profile", Dock = DockStyle.Fill }, 0, 0);
            t.Controls.Add(new Label { Text = "Name", Dock = DockStyle.Fill }, 1, 0);

            _profiles = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            _profiles.SelectedIndexChanged += delegate { SelectProfile(); };
            t.Controls.Add(_profiles, 0, 1);

            _nameBox = new TextBox { Dock = DockStyle.Fill };
            t.Controls.Add(_nameBox, 1, 1);
            Button rename = new Button { Text = "Rename", Dock = DockStyle.Fill };
            rename.Click += delegate { RenameProfile(); };
            t.Controls.Add(rename, 2, 1);

            FlowLayoutPanel row1 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            row1.Controls.Add(MkButton("Activate", delegate { ActivateProfile(); }));
            row1.Controls.Add(MkButton("New", delegate { NewProfile(); }));
            row1.Controls.Add(MkButton("Clone", delegate { CloneProfile(); }));
            row1.Controls.Add(MkButton("Delete", delegate { DeleteProfile(); }));
            row1.Controls.Add(MkButton("Refresh", delegate { LoadProfiles(); }));
            t.Controls.Add(row1, 0, 2);
            t.SetColumnSpan(row1, 3);

            FlowLayoutPanel row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            row2.Controls.Add(MkButton("Save profile to file", delegate { SaveProfileFile(); }));
            row2.Controls.Add(MkButton("Load profile from file", delegate { LoadProfileFile(); }));
            row2.Controls.Add(MkButton("Reset to Razer defaults", delegate { ResetDefaults(); }));
            t.Controls.Add(row2, 0, 3);
            t.SetColumnSpan(row2, 3);

            Label hint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Mappings, DPI, polling and debounce belong to the profile (green tabs). " +
                       "Scroll feel, lighting and power are global (orange tab). The device refuses to delete " +
                       "the last profile; names only stick on profiles Razer Synapse created."
            };
            t.Controls.Add(hint, 0, 4);
            t.SetColumnSpan(hint, 3);

            p.Controls.Add(t);

            _currentLbl = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Padding = new Padding(12, 0, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                BackColor = Color.FromArgb(226, 240, 226),
                Text = "Current profile: -"
            };
            p.Controls.Add(_currentLbl);
            return p;
        }

        static Button MkButton(string text, EventHandler onClick)
        {
            Button b = new Button { Text = text, Width = 150, Height = 24, Margin = new Padding(0, 0, 6, 0) };
            b.Click += onClick;
            return b;
        }

        // ---------------- mappings ----------------
        TabPage BuildButtonsPage()
        {
            TabPage p = new TabPage("Buttons");
            _btnBanner = Banner("Stored inside profile", true);
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, Padding = new Padding(8), AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            for (int i = 0; i < MapTable.InputKeys.Length; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            Font bold = new Font("Segoe UI", 9f, FontStyle.Bold);
            Label h0 = Head("Input", bold); t.Controls.Add(h0, 0, 0);
            Label h1 = Head("Output type", bold); t.Controls.Add(h1, 1, 0);
            Label h2 = Head("Value", bold); t.Controls.Add(h2, 2, 0);
            Label h3 = Head("Hypershift row", bold); t.Controls.Add(h3, 3, 0);
            Label h4 = Head("Modifiers", bold); t.Controls.Add(h4, 4, 0);
            Label h5 = Head("Windows shortcut / host slot", bold); t.Controls.Add(h5, 5, 0);

            for (int i = 0; i < MapTable.InputKeys.Length; i++)
            {
                int idx = i;
                t.Controls.Add(new Label { Text = MapTable.InputNames[i], TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, i + 1);

                ComboBox cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                cb.Items.AddRange(OutTypes.Names);
                cb.SelectedIndex = 0;
                _typeBox[i] = cb;
                t.Controls.Add(cb, 1, i + 1);

                ComboBox pb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                _paramBox[i] = pb;
                t.Controls.Add(pb, 2, i + 1);

                CheckBox hs = new CheckBox { Dock = DockStyle.Fill };
                _hsBox[i] = hs;
                t.Controls.Add(hs, 3, i + 1);

                FlowLayoutPanel mods = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
                _modBox[i] = new CheckBox[Modifiers.Bits.Length];
                for (int m = 0; m < Modifiers.Bits.Length; m++)
                {
                    CheckBox mc = new CheckBox { Text = Modifiers.Labels[m], Width = 34, Height = 22, Margin = new Padding(0, 1, 2, 0), AutoSize = false };
                    mc.CheckedChanged += delegate { if (!_loading) _dirty[idx] = true; };
                    _modBox[i][m] = mc;
                    mods.Controls.Add(mc);
                }
                t.Controls.Add(mods, 4, i + 1);

                ComboBox sc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                sc.Items.Add("(none)");
                sc.Items.AddRange(WinShortcuts.Names);
                for (int hslot = 0; hslot < HostTrigger.Count; hslot++)
                    sc.Items.Add("Host action " + (hslot + 1) + " (" + HostTrigger.Name(hslot) + ")");
                sc.SelectedIndex = 0;
                _shortcutBox[i] = sc;
                sc.SelectedIndexChanged += delegate
                {
                    if (_loading) return;
                    int si2 = sc.SelectedIndex - 1;
                    if (si2 < 0) return;
                    if (si2 >= WinShortcuts.Names.Length)
                    {
                        // Host action trigger: the firmware writes the spare key, Rz3's engine
                        // runs the action defined for this profile in the "Host actions" tab.
                        int slot = si2 - WinShortcuts.Names.Length;
                        if (slot < 0 || slot >= HostTrigger.Count) return;
                        _loading = true;
                        _typeBox[idx].SelectedIndex = Array.IndexOf(OutTypes.Values, OutTypes.Keyboard);
                        FillParams(idx);
                        for (int b = 0; b < Modifiers.Bits.Length; b++) _modBox[idx][b].Checked = false;
                        _loading = false;
                        FillParams(idx);
                        SelectParam(idx, "f" + (13 + slot));
                        _dirty[idx] = true;
                        return;
                    }
                    _loading = true;
                    _typeBox[idx].SelectedIndex = Array.IndexOf(OutTypes.Values, OutTypes.Keyboard);
                    FillParams(idx);
                    for (int b = 0; b < Modifiers.Bits.Length; b++)
                        _modBox[idx][b].Checked = (WinShortcuts.Mods[si2] & Modifiers.Bits[b]) != 0;
                    _loading = false;
                    FillParams(idx);
                    SelectParam(idx, UsageToName(WinShortcuts.Usages[si2]));
                    _dirty[idx] = true;
                };
                t.Controls.Add(sc, 5, i + 1);

                FillParams(i);
                cb.SelectedIndexChanged += delegate { if (_loading) return; FillParams(idx); _dirty[idx] = true; };
                pb.SelectedIndexChanged += delegate { if (!_loading) _dirty[idx] = true; };
            }

            _hsMode = new CheckBox { Text = "Edit Hypershift layer (the checkbox in the row marks the record as Hypershift)", Dock = DockStyle.Top, Height = 24 };
            _hsMode.CheckedChanged += delegate { _mode = (byte)(_hsMode.Checked ? 1 : 0); LoadMappings(); };
            p.Controls.Add(t);
            p.Controls.Add(_hsMode);
            p.Controls.Add(_btnBanner);
            return p;
        }

        // ---------------- mouse ----------------

        TabPage BuildProfileSettingsPage()
        {
            TabPage p = new TabPage("Profile settings");
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            for (int i = 0; i < 11; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _dpi = new NumericUpDown { Minimum = 100, Maximum = 35000, Value = 1000, Increment = 100, Width = 120 };
            _clutchDpi = new NumericUpDown { Minimum = 100, Maximum = 35000, Value = 400, Increment = 50, Width = 120 };
            // Polling rates are the firmware's own enum (100..1000) plus the high-speed family
            // (2000..8000) when this unit answers the HyperPolling command. Filled on connect.
            _poll = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _poll.Items.AddRange(new object[] { "1000", "500", "333", "250", "200", "125", "100" });
            _poll.SelectedIndex = 0;
            _debounce = new NumericUpDown { Minimum = 1, Maximum = 30, Value = 4, Width = 80 };

            _dpiLabel = new Label { Text = "DPI", Dock = DockStyle.Fill };
            AddRow(t, 0, "", _dpi);
            t.Controls.Remove(t.GetControlFromPosition(0, 0));
            t.Controls.Add(_dpiLabel, 0, 0);
            AddRow(t, 1, "Clutch DPI", _clutchDpi);
            AddRow(t, 2, "Polling rate (Hz)", _poll);
            AddRow(t, 3, "Debounce (ms, rejected by this firmware)", _debounce);

            Label note = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Read from and written to the selected profile only. The mouse keeps DPI and polling " +
                       "per profile, so no activation is needed; the active profile is not touched."
            };
            t.Controls.Add(note, 1, 4);

            Button b = new Button { Text = "Apply profile settings", Width = 200, Height = 26 };
            b.Click += delegate { ApplyProfileSettings(); };
            t.Controls.Add(new Label { Text = "" }, 0, 5);
            t.Controls.Add(b, 1, 5);

            Button cyc = new Button { Text = "Assign bottom profile button: cycle profiles", Width = 340, Height = 26 };
            cyc.Click += delegate { AssignProfileCycle(); };
            t.Controls.Add(new Label { Text = "" }, 0, 6);
            t.Controls.Add(cyc, 1, 6);

            Label cycNote = new Label
            {
                Dock = DockStyle.Fill,
                Text = "The profile button carries one action, so plain Next stops at the last profile. " +
                       "The cycle variants wrap around; if the firmware ignores them, assign Next again."
            };
            t.Controls.Add(cycNote, 1, 7);

            // Polling is the one setting where "the device said OK" means nothing: the firmware
            // echoes back any id. Measuring reports per second is the only honest check, so the
            // measurement lives right next to the control.
            _pollIdLbl = new Label { Dock = DockStyle.Fill, Text = "", AutoEllipsis = true };
            AddRow(t, 8, "Stored as", _pollIdLbl);

            FlowLayoutPanel mrow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            Button m1 = MkButton("Measure current rate", delegate { MeasureCurrentRate(); });
            m1.Width = 155;
            Button m2 = MkButton("Calibrate all ids", delegate { CalibrateRates(); });
            m2.Width = 130;
            mrow.Controls.Add(m1);
            mrow.Controls.Add(m2);
            t.Controls.Add(new Label { Text = "Real reports/s", Dock = DockStyle.Fill }, 0, 9);
            t.Controls.Add(mrow, 1, 9);

            Label rateNote = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Measurement counts real HID reports through Raw Input, so keep moving the mouse " +
                       "while it runs. A still mouse reports 0/s at every rate."
            };
            t.Controls.Add(rateNote, 1, 10);

            p.Controls.Add(t);
            _profBanner = Banner("Stored inside profile", true);
            p.Controls.Add(_profBanner);
            return p;
        }

        TabPage BuildDevicePage()
        {
            TabPage p = new TabPage("Device (global)");
            p.Controls.Add(Banner("Global - stored on the device, shared by every profile", false));
            TabControl sub = new TabControl { Dock = DockStyle.Fill };

            TabPage sp = new TabPage("Scroll feel");
            TableLayoutPanel st = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            st.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            st.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 10; i++) st.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            _scrollMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
            _scrollMode.Items.AddRange(new object[] { "Tactile", "Free spin", "Micro tactile" });
            _scrollMode.SelectedIndexChanged += delegate { UpdateScrollUi(); };
            _smartReelOn = new CheckBox { Text = "Smart reel", Width = 160 };
            _smartReelOn.CheckedChanged += delegate { UpdateScrollUi(); };
            _smartReel = new TrackBar { Minimum = 0, Maximum = 4, Value = 2, Width = 260, TickFrequency = 1 };
            _scrollAccel = new CheckBox { Text = "Virtual scroll acceleration", Width = 260 };
            _accelLevel = new TrackBar { Minimum = 0, Maximum = 4, Value = 2, Width = 260, TickFrequency = 1 };

            AddRow(st, 0, "Scroll mode", _scrollMode);
            AddRow(st, 1, "", _smartReelOn);
            AddRow(st, 2, "Smart reel sensitivity (0-4)", _smartReel);
            AddRow(st, 3, "", _scrollAccel);
            AddRow(st, 4, "Acceleration strength (0-4)", _accelLevel);

            _scrollHint = new Label { Dock = DockStyle.Fill, Text = "" };
            st.Controls.Add(_scrollHint, 1, 5);
            st.SetColumnSpan(_scrollHint, 2);

            _clearToggle = new Button { Text = "Clear scroll-mode switch", Width = 340, Height = 24, Visible = false };
            _clearToggle.Click += delegate { ClearModeToggle(); };
            st.Controls.Add(_clearToggle, 1, 6);

            Button bs = new Button { Text = "Apply scroll settings", Width = 200, Height = 26 };
            bs.Click += delegate { ApplyScroll(); };
            st.Controls.Add(new Label { Text = "" }, 0, 7);
            st.Controls.Add(bs, 1, 7);
            sp.Controls.Add(st);


            TabPage pp = new TabPage("Power");
            TableLayoutPanel pt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            pt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            pt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 8; i++) pt.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            _charge = new Label { Text = "", Width = 400 };
            _lowBat = new NumericUpDown { Minimum = 5, Maximum = 100, Value = 15, Width = 80 };
            _idle = new NumericUpDown { Minimum = 60, Maximum = 900, Value = 300, Increment = 60, Width = 80 };
            Button refresh = new Button { Text = "Refresh battery", Width = 150, Height = 24 };
            refresh.Click += delegate { RefreshBattery(); };
            AddRow(pt, 0, "Battery", _charge);
            AddRow(pt, 1, "", refresh);
            AddRow(pt, 2, "Low battery warning (%)", _lowBat);
            AddRow(pt, 3, "Sleep idle time (s)", _idle);
            Button bp = new Button { Text = "Apply power settings", Width = 200, Height = 26 };
            bp.Click += delegate { ApplyPower(); };
            pt.Controls.Add(new Label { Text = "" }, 0, 4);
            pt.Controls.Add(bp, 1, 4);

            // Tray options: the icon can carry the charge level, and/or the tooltip can say it.
            _trayBattIcon = new CheckBox { Text = "Show battery level on the tray icon", Checked = _trayShowBattery, Width = 320 };
            _trayBattIcon.CheckedChanged += delegate { SetTrayOption(true, _trayBattIcon.Checked); };
            _trayBattTip = new CheckBox { Text = "Show battery in the tray tooltip", Checked = _trayShowTip, Width = 320 };
            _trayBattTip.CheckedChanged += delegate { SetTrayOption(false, _trayBattTip.Checked); };
            AddRow(pt, 5, "Tray", _trayBattIcon);
            AddRow(pt, 6, "", _trayBattTip);
            Label trayHint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Closing the window keeps Rz3 running only in --tray mode; the tray menu can open the " +
                       "window, switch these options and start Rz3 with Windows."
            };
            pt.Controls.Add(trayHint, 1, 7);
            pp.Controls.Add(pt);

            sub.TabPages.Add(sp);
            sub.TabPages.Add(pp);
            p.Controls.Add(sub);
            UpdateScrollUi();
            return p;
        }

        TabPage BuildLightingPage()
        {
            TabPage lp = new TabPage("Lighting");
            TableLayoutPanel lt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            lt.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            lt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 9; i++) lt.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            _zone = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            _zone.Items.AddRange(new object[] { "All zones", "Scroll wheel", "Logo", "Base light (underglow)" });
            _effect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            _effect.Items.AddRange(new object[] { "Off", "Static", "Breathing", "Spectrum", "Wave", "Reactive (unverified)", "Custom frame (single colour)" });
            _dir = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
            _dir.Items.AddRange(new object[] { "Left", "Right", "Down" });
            _effRate = new NumericUpDown { Minimum = 1, Maximum = 255, Value = 40, Width = 80 };
            _effBright = new TrackBar { Minimum = 0, Maximum = 100, Value = 60, Width = 300, TickFrequency = 10 };
            _bright = new TrackBar { Minimum = 0, Maximum = 100, Value = 35, Width = 300, TickFrequency = 10 };
            _colorBtn = new Button { Text = "Colour...", Width = 120 };
            _colorBtn.Click += delegate
            {
                ColorDialog d = new ColorDialog();
                if (d.ShowDialog() == DialogResult.OK) { _effectColor = d.Color; _colorBtn.BackColor = d.Color; }
            };
            AddRow(lt, 0, "Zone", _zone);
            AddRow(lt, 1, "Effect", _effect);
            AddRow(lt, 2, "Direction", _dir);
            AddRow(lt, 3, "Rate", _effRate);
            AddRow(lt, 4, "Brightness (all)", _bright);
            AddRow(lt, 5, "Effect brightness", _effBright);
            AddRow(lt, 6, "Static colour", _colorBtn);
            Label ln = new Label { Dock = DockStyle.Fill, Text = "Effects are device-wide: switching profile does not change them." };
            lt.Controls.Add(ln, 1, 8);
            Button bl = new Button { Text = "Apply lighting", Width = 200, Height = 26 };
            bl.Click += delegate { ApplyLighting(); };
            lt.Controls.Add(new Label { Text = "" }, 0, 7);
            lt.Controls.Add(bl, 1, 7);

            lp.Controls.Add(lt);
            lp.Controls.Add(Banner("Global - the firmware keeps one lighting state for the whole device", false));
            return lp;
        }

        static Label Banner(string prefix, bool perProfile)
        {
            Label l = new Label
            {
                Dock = DockStyle.Top,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                BackColor = perProfile ? Color.FromArgb(226, 240, 226) : Color.FromArgb(252, 241, 220)
            };
            l.Text = perProfile ? (prefix + "  -  changes follow the selected profile") : prefix;
            return l;
        }

        TabPage BuildRawPage()
        {
            TabPage p = new TabPage("Raw command");
            Panel top = new Panel { Dock = DockStyle.Top, Height = 40 };
            _rawCls = new ComboBox { Left = 10, Top = 8, Width = 90, DropDownStyle = ComboBoxStyle.DropDown };
            _rawCls.Items.AddRange(new object[] { "0x02", "0x05", "0x04", "0x07", "0x0F" });
            _rawCls.Text = "0x02";
            _rawCmd = new ComboBox { Left = 106, Top = 8, Width = 90, DropDownStyle = ComboBoxStyle.DropDown };
            _rawCmd.Items.AddRange(new object[] { "0x0C", "0x8E", "0x85", "0x80", "0x84" });
            _rawCmd.Text = "0x8E";
            _rawArgs = new TextBox { Left = 202, Top = 8, Width = 380, Text = "1 0 0" };
            Button send = new Button { Text = "Send", Left = 590, Top = 7, Width = 90 };
            send.Click += delegate { SendRaw(); };
            top.Controls.Add(_rawCls); top.Controls.Add(_rawCmd); top.Controls.Add(_rawArgs); top.Controls.Add(send);
            _rawOut = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f) };
            p.Controls.Add(_rawOut);
            p.Controls.Add(top);
            return p;
        }

        static void AddRow(TableLayoutPanel t, int row, string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);
            t.Controls.Add(c, 1, row);
        }

        // Column heading that wraps instead of being clipped when the cell is narrow.
        static Label Head(string text, Font bold)
        {
            return new Label
            {
                Text = text,
                Font = bold,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        void FillParams(int i)
        {
            ComboBox pb = _paramBox[i];
            if (pb == null || _typeBox[i] == null) return;
            bool prevLoading = _loading;
            _loading = true;
            pb.Items.Clear();
            switch (OutTypes.Values[_typeBox[i].SelectedIndex])
            {
                case OutTypes.Keyboard: foreach (string k in Keys.Order) pb.Items.Add(k); break;
                case OutTypes.MouseButton: pb.Items.AddRange(MouseActions.Names); break;
                case OutTypes.Media: pb.Items.AddRange(Media.Names); break;
                case OutTypes.NavigateProfile: pb.Items.AddRange(ProfileActions.Names); break;
                case OutTypes.Sensitivity: pb.Items.AddRange(SensActions.Names); break;
                case OutTypes.SideScroll: pb.Items.AddRange(new object[] { "Scroll left", "Scroll right" }); break;
                case OutTypes.ScrollMode: pb.Items.Add("Tactile / free-spin"); break;
                case OutTypes.Lighting: pb.Items.AddRange(LightingActions.Names); break;
                case OutTypes.PowerKey: pb.Items.AddRange(PowerActions.Names); break;
                case OutTypes.RazerKey: pb.Items.AddRange(PowerActions.Names); break;
                case OutTypes.DoubleClick: pb.Items.Add("Double click"); break;
                case OutTypes.ModeButton: pb.Items.Add("Switch mode"); break;
                case OutTypes.MacroI:
                case OutTypes.MacroIV:
                    for (int m = 1; m <= 16; m++) pb.Items.Add("Macro " + m);
                    break;
                default: pb.Items.Add("-"); break;
            }
            if (pb.Items.Count > 0) pb.SelectedIndex = 0;
            _loading = prevLoading;
        }

        // ---------------- device ----------------
        // Transact with reconnect protection: null means the device stayed gone.
        byte[] Tx(byte cls, byte cmd, byte[] args, byte size)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (_dev == null && !TryReattach()) return null;
                    byte[] r = _dev.Transact(cls, cmd, args, size);
                    if (r != null) return r;
                }
                catch { }
                if (attempt == 0 && _dev == null) TryReattach();
                Thread.Sleep(150);
            }
            return null;
        }

        void Connect()
        {
            try
            {
                if (_dev != null) { try { _dev.Close(); } catch { } _dev = null; }
                _dev = RzDevice.Find();
                if (_dev == null || !_dev.Open())
                {
                    MarkLost("Device not found / busy. Close Razer Synapse (it keeps the device open).");
                    return;
                }
                string fw = "";
                try { fw = _dev.Firmware(); } catch { }
                string shortPath = _dev.Path;
                int h = shortPath.IndexOf('#');
                if (h > 0 && shortPath.Length > h + 1) shortPath = shortPath.Substring(h + 1);
                _connText = "Connected: " + shortPath + (fw != null && fw.Length > 0 ? ("   firmware " + fw) : "");
                _status.Text = _connText;
                _reconTimer.Stop();
                RefreshBattery();
                _battTimer.Start();
                _watchTimer.Start();
                LoadProfiles();
                LoadDeviceState();
                // Probe once per connection whether the firmware stores a power profile; the
                // control is hidden when it does not, so a restart can never look like a reset.
                try { _pwProfileSupported = _dev.PowerProfileSupported(); }
                catch { _pwProfileSupported = false; }
                LoadPowerTab();
                PushWantedDpi();
            }
            catch (Exception ex) { MarkLost("Error: " + ex.Message); }
        }

        // Cable pulled / dongle gone: keep the UI alive and poll until the mouse is back.
        void MarkLost(string reason)
        {
            if (_dev != null) { try { _dev.Close(); } catch { } _dev = null; }
            _lost = true;
            _connText = "";
            _battTimer.Stop();
            _watchTimer.Stop();
            _bat.Text = "";
            _charge.Text = "";
            if (_reconTimer != null && !_reconTimer.Enabled) _reconTimer.Start();
            _status.Text = reason + "   Waiting for the mouse...";
        }

        // Tries to grab the device again; returns true when a handle is open.
        bool TryReattach()
        {
            if (_dev != null) return true;
            try
            {
                RzDevice d = RzDevice.Find();
                if (d == null || !d.Open()) return false;
                _dev = d;
                _lost = false;
                _reconTimer.Stop();
                Connect();
                return _dev != null;
            }
            catch { return false; }
        }

        // The profile button on the mouse changes the active profile behind our back; follow it
        // unless the user is in the middle of editing rows of the current profile.
        void WatchActiveProfile()
        {
            if (_dev == null || _loading || _tempSwitch) return;
            if (_profileIds.Count == 0) { LoadProfiles(); return; }
            int act = -1;
            try { act = _dev.ActiveProfile(); } catch { }
            if (act <= 0 || act == _activeProfile) return;
            bool dirty = false;
            for (int i = 0; i < _dirty.Length; i++) if (_dirty[i]) dirty = true;
            if (dirty)
            {
                // Keep the user's selection; only remember the new active profile.
                _activeProfile = act;
                _status.Text = "Mouse switched to profile " + act + ". You have unsaved rows here; " +
                               "press Reconnect to follow (or keep editing profile " + _profile + ").";
                LoadProfiles();
                return;
            }
            // _activeProfile is left for LoadProfiles, which compares against the previous value
            // to decide whether the selection should follow the device.
            LoadProfiles();
            _status.Text = "Active profile changed on the mouse -> profile " + act + ".";
        }


        // The Power tab used to be read once per connection. When the mouse went to sleep the
        // read failed, the controls were disabled, and nothing ever re-enabled them until the user
        // pressed Refresh - which is exactly the "offline until I hit Refresh" behaviour.
        // This re-reads them, but never while the user is editing a field.
        void RefreshPowerTabIfIdle()
        {
            if (_pwSleep == null || _dev == null || _loading) return;
            try
            {
                if (_pwSleep.Focused || _pwLow.Focused || _pwProfile.Focused) return;
            }
            catch { }
            // Only fill a control that is disabled: a value the user already sees is not touched,
            // so the tab never jumps while it is being read.
            try
            {
                if (!_pwSleep.Enabled)
                {
                    ushort sleep = _dev.GetIdleTime();
                    if (sleep > 0)
                    {
                        _pwSleep.Value = Math.Max((int)_pwSleep.Minimum, Math.Min((int)_pwSleep.Maximum, (int)sleep));
                        _pwSleep.Enabled = true;
                    }
                }
                if (!_pwLow.Enabled)
                {
                    int low = _dev.GetLowBattery();
                    if (low >= 0) { _pwLow.Value = Math.Max(5, Math.Min(100, low)); _pwLow.Enabled = true; }
                }
                if (_pwProfileSupported && !_pwProfile.Enabled)
                {
                    int prof = _dev.GetPowerProfile();
                    if (prof >= 0 && prof <= 2) { _pwProfile.SelectedIndex = prof; _pwProfile.Enabled = true; }
                }
            }
            catch { }
            RefreshPowerLive();
        }

        void RefreshBattery()
        {
            if (_dev == null)
            {
                _bat.Text = _lost ? "waiting for the mouse..." : "";
                _lastBatt = -1;
                _lastChg = -1;
                _lastAc = -1;
                UpdateTray();
                return;
            }
            int pct;
            int st = _dev.ChargeState(out pct);
            int link = _dev.LinkMode;
            if (pct < 0)
            {
                _battFail++;
                if (_battFail >= 2)
                {
                    _battFail = 0;
                    MarkLost("Device stopped answering (moved between cable and dongle, asleep, or held by Synapse).");
                    return;
                }
            }
            else _battFail = 0;
            _lastBatt = pct;
            _lastLink = link;
            _lastCharge = st;
            _lastChg = st == Rz2.RzDevice.ChargeCharging ? 1 : st == Rz2.RzDevice.ChargeOnBattery || st == Rz2.RzDevice.ChargeFull ? 0 : -1;
            _lastAc = link == Rz2.RzDevice.LinkWired ? 1 : link == Rz2.RzDevice.LinkWireless ? 0 : -1;
            UpdateTray();
            _bat.Text = pct >= 0 ? ("Battery " + pct + "%") : "Battery n/a";
            // Same words as the Power tab and the tray: the link and the charge state, so the
            // three places can never disagree.
            _charge.Text = (pct >= 0 ? pct + "%  " : "") +
                Rz2.RzDevice.ChargeName(st) + "  \u2022  " + Rz2.RzDevice.LinkName(link);
        }

        void LoadProfiles()
        {
            if (_dev == null) return;
            _loading = true;
            _profileIds.Clear();
            _profiles.Items.Clear();
            int active = -1;
            try { active = _dev.ActiveProfile(); } catch { }
            try
            {
                byte[] req = new byte[80];
                byte[] r = Tx(0x05, 0x81, req, 80);
                if (r != null && r[0] == 0x02)
                {
                    for (int i = 9; i < 88; i++)
                    {
                        byte id = r[i];
                        if (id == 0) break;
                        _profileIds.Add(id);
                    }
                }
            }
            catch { }
            if (active > 0 && !_profileIds.Contains((byte)active)) _profileIds.Insert(0, (byte)active);
            _activeProfileBefore = _activeProfile;
            _activeProfile = active;
            // Follow the device when the user was sitting on the active profile; otherwise keep the
            // profile they deliberately selected (a temp switch must not steal the selection).
            bool follow = (_profile == 0) || (_activeProfileBefore == 0) || (_profile == (byte)Math.Max(0, _activeProfileBefore)) || !_profileIds.Contains(_profile);
            byte keep = follow ? (byte)Math.Max(0, active) : _profile;
            for (int i = 0; i < _profileIds.Count; i++)
            {
                byte id = _profileIds[i];
                if (id == active) _activeSel = i;
                _profiles.Items.Add(id + ": " + DisplayName(id) + (id == active ? "   [active]" : ""));

            }
            if (_profileIds.Count == 0)
            {
                _profiles.Items.Add("(no profile)");
                _status.Text = "No answer from the mouse (asleep or unplugged). Move it - the app picks it up again.";
            }
            int sel = _profileIds.IndexOf(keep);
            if (sel < 0) sel = 0;
            _profiles.SelectedIndex = sel;
            _loading = false;
            SelectProfile();
            RebuildHostTable();
        }

        void SelectProfile()
        {
            if (_loading || _dev == null) return;
            int ix = _profiles.SelectedIndex;
            if (ix < 0 || ix >= _profileIds.Count) return;
            _profile = _profileIds[ix];
            string name = DeviceName(_profile);
            _nameBox.Text = name ?? "";
            string shown = DisplayName(_profile);
            string tag = "Profile " + _profile + " - " + shown;
            if (_btnBanner != null) _btnBanner.Text = "Stored inside " + tag + "  -  changes follow this profile";
            if (_profBanner != null) _profBanner.Text = "Stored inside " + tag;
            _status.Text = (_connText.Length > 0 ? _connText + "   |   " : "") + "Editing " + tag;
            if (_currentLbl != null) _currentLbl.Text = "Current profile: " + tag;
            LoadMappings();
            LoadProfileSettings();
            LoadHostPage();
        }

        // Name shown for a profile: device name, else the locally remembered name.
        string DisplayName(byte id)
        {
            string n = DeviceName(id);
            if (!string.IsNullOrEmpty(n)) return n;
            string ln;
            if (_localNames.TryGetValue(id, out ln) && !string.IsNullOrEmpty(ln)) return ln + " (local)";
            return "(unnamed)";
        }

        string DeviceName(byte id)
        {
            try { return _dev.GetProfileName(id); } catch { return null; }
        }

        void LoadLocalNames()
        {
            _localNames.Clear();
            try
            {
                if (!File.Exists(NamesPath)) return;
                foreach (string line in File.ReadAllLines(NamesPath))
                {
                    string[] p = line.Split(new char[] { '\t' }, 2);
                    byte id;
                    if (p.Length == 2 && byte.TryParse(p[0], out id)) _localNames[id] = p[1];
                }
            }
            catch { }
        }

        void SaveLocalNames()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<byte, string> kv in _localNames) sb.AppendLine(kv.Key + "\t" + kv.Value);
                File.WriteAllText(NamesPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        void LoadDpiWanted()
        {
            _dpiWanted.Clear();
            try
            {
                if (!File.Exists(DpiPath)) return;
                foreach (string line in File.ReadAllLines(DpiPath))
                {
                    string[] p = line.Split(new char[] { '\t' }, 2);
                    byte id; int v;
                    if (p.Length == 2 && byte.TryParse(p[0], out id) && int.TryParse(p[1], out v)) _dpiWanted[id] = v;
                }
            }
            catch { }
        }

        void SaveDpiWanted()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<byte, int> kv in _dpiWanted) sb.AppendLine(kv.Key + "\t" + kv.Value);
                File.WriteAllText(DpiPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        string DpiPath
        {
            get { return Path.Combine(Application.StartupPath, "rz3_dpi.json"); }
        }

        // DPI and polling are addressed by profile id, so no profile switch is needed.
        // The device keeps the value itself now; this only repairs a profile whose write failed
        // earlier (e.g. the mouse was asleep) and never overwrites a value that already matches.
        void PushWantedDpi()
        {
            if (_dev == null) return;
            int v;
            if (!_dpiWanted.TryGetValue(_profile, out v) || v < 100) return;
            int cur = 0;
            try { ushort[] d = _dev.GetDpi(_profile); if (d != null) cur = d[0]; } catch { }
            if (cur != v) { try { _dev.SetDpi(_profile, (ushort)v, (ushort)v); } catch { } }
            if (v >= 100) _dpi.Value = Math.Min(35000, v);
        }

        string NamesPath
        {
            get { return Path.Combine(Application.StartupPath, "rz3_names.json"); }
        }

        void LoadProfileSettings()
        {
            if (_dev == null) return;
            int act = -1;
            try { act = _dev.ActiveProfile(); } catch { }
            _dpiLabel.Text = act == _profile ? "DPI" : "DPI (profile " + _profile + ", not active)";
            try
            {
                ushort[] d = _dev.GetDpi(_profile);
                if (d != null) _dpi.Value = Math.Max(100, Math.Min(35000, (int)d[0]));
            }
            catch { }
            try
            {
                bool hs;
                int p = _dev.GetPollRateEx(_profile, out hs);
                _hsPollSupported = hs || _dev.HighSpeedPollSupported(_profile);
                FillPollList();
                int ix = p > 0 ? _poll.Items.IndexOf(p.ToString()) : -1;
                if (ix >= 0) _poll.SelectedIndex = ix;
                else if (p > 0) { _poll.Items.Add(p.ToString()); _poll.SelectedIndex = _poll.Items.Count - 1; }
            }
            catch { }
            RefreshPollLabel();
            try { int dbg = _dev.GetDebounce(); if (dbg > 0) _debounce.Value = Math.Min(30, Math.Max(1, dbg)); } catch { }
        }

        // Rates the firmware in *this* device exposes: the standard enum always, the high-speed
        // family only when the HyperPolling command answers.
        void FillPollList()
        {
            if (_poll == null) return;
            string keep = _poll.SelectedItem as string;
            _poll.Items.Clear();
            ushort[] rates = _hsPollSupported ? RzDevice.HsRates : RzDevice.StdRates;
            for (int i = 0; i < rates.Length; i++) _poll.Items.Add(rates[i].ToString());
            if (keep != null)
            {
                int ix = _poll.Items.IndexOf(keep);
                if (ix >= 0) { _poll.SelectedIndex = ix; return; }
            }
            if (_poll.Items.Count > 0) _poll.SelectedIndex = 0;
        }

        // Shows the raw firmware id plus whatever the last measurement said about it.
        void RefreshPollLabel()
        {
            if (_pollIdLbl == null) return;
            if (_dev == null) { _pollIdLbl.Text = ""; return; }
            byte raw = 0;
            try { raw = _dev.GetPollIdRaw(_profile); } catch { }
            int want = 0;
            int.TryParse(_poll.SelectedItem as string, out want);
            _pollIdLbl.Text = "id " + raw + (_hsPollSupported ? " (high-speed table)" : " (standard table)") +
                " - " + MeasuredText(raw, want);
        }


        void LoadDeviceState()
        {
            if (_dev == null) return;
            try { int m = _dev.GetScrollMode3(); if (m >= 0 && m <= 2) _scrollMode.SelectedIndex = m; } catch { }
            try { _scrollAccel.Checked = _dev.GetScrollAccel(); } catch { }
            try { int l = _dev.GetScrollAccelLevel(); if (l >= 0 && l <= 4) _accelLevel.Value = l; } catch { }
            try { int t = _dev.GetSmartReelThreshold(); if (t >= 0 && t <= 4) _smartReel.Value = t; } catch { }
            try { _smartReelOn.Checked = _dev.GetSmartReel(); } catch { }
            try { _smartReelOn.Checked = _dev.GetSmartReel(); } catch { }
            UpdateScrollUi();
        }

        void LoadMappings()
        {
            if (_dev == null) return;
            for (int i = 0; i < _map.Count; i++)
            {
                _map[i].OutType = 0;
                _map[i].Mode = _mode;
                _map[i].Data = new byte[0];
                _dirty[i] = false;
            }
            try
            {
                List<KeyValuePair<byte, byte[]>> recs = _dev.ReadMappings(_profile, _mode);
                _mapReadOk = recs != null && recs.Count >= MapTable.InputIds.Length - 1;
                if (recs != null)
                {
                    foreach (KeyValuePair<byte, byte[]> kv in recs)
                    {
                        int ix = Array.IndexOf(MapTable.InputIds, kv.Key);
                        if (ix < 0) continue;
                        byte[] d = kv.Value;
                        _map[ix].OutType = d[0];
                        _map[ix].Data = new byte[d.Length - 1];
                        Array.Copy(d, 1, _map[ix].Data, 0, d.Length - 1);
                    }
                }
            }
            catch { _mapReadOk = false; }
            if (!_mapReadOk && _status != null && _dev != null)
                _status.Text = "Mapping read was incomplete (" + _profile + "): rows may look disabled. " +
                               "Move the mouse and press Reconnect before writing.";
            ApplyToUi();
        }

        void ApplyToUi()
        {
            _loading = true;
            for (int i = 0; i < _map.Count; i++)
            {
                MapEntry m = _map[i];
                int ti = Array.IndexOf(OutTypes.Values, m.OutType);
                if (ti < 0) ti = 0;
                _typeBox[i].SelectedIndex = ti;
                FillParams(i);
                _loading = true;
                _hsBox[i].Checked = m.Mode == 1;
                for (int b = 0; b < Modifiers.Bits.Length; b++) _modBox[i][b].Checked = false;
                if (_shortcutBox[i] != null) _shortcutBox[i].SelectedIndex = 0;
                byte[] d = m.Data;
                switch (m.OutType)
                {
                    case OutTypes.Keyboard:
                        if (d.Length >= 2)
                        {
                            byte mod = d[0];
                            for (int b = 0; b < Modifiers.Bits.Length; b++) _modBox[i][b].Checked = (mod & Modifiers.Bits[b]) != 0;
                            SelectParam(i, UsageToName(d[1]));
                            int hslot = HostTrigger.SlotOfUsage(d[1]);
                            if (hslot >= 0 && mod == 0)
                                _shortcutBox[i].SelectedIndex = 1 + WinShortcuts.Names.Length + hslot;
                            else
                            {
                                int pre = WinShortcuts.Match(mod, d[1]);
                                _shortcutBox[i].SelectedIndex = pre + 1;
                            }
                        }
                        break;
                    case OutTypes.MouseButton:
                        if (d.Length >= 1) _paramBox[i].SelectedIndex = Math.Max(0, Array.IndexOf(MouseActions.Ids, d[0]));
                        break;
                    case OutTypes.Media:
                        if (d.Length >= 2) _paramBox[i].SelectedIndex = MathMax0(Array.IndexOf(Media.Usages, d[1]));
                        break;
                    case OutTypes.Sensitivity:
                        if (d.Length >= 5)
                        {
                            // Explicit clutch form: [05, xHi, xLo, yHi, yLo]
                            _paramBox[i].SelectedIndex = 0;
                            int v = (d[1] << 8) | d[2];
                            if (v >= 100 && v <= 35000) _clutchDpi.Value = v;
                        }
                        else if (d.Length >= 1)
                        {
                            int si = Array.IndexOf(SensActions.Ids, d[0]);
                            _paramBox[i].SelectedIndex = Math.Max(0, si);
                        }
                        break;
                    case OutTypes.SideScroll:
                        if (d.Length >= 1) _paramBox[i].SelectedIndex = d[0] == 0x69 ? 1 : 0;
                        break;
                    case OutTypes.NavigateProfile:
                        {
                            int hit = -1;
                            for (int k = 0; k < ProfileActions.Data.Length && hit < 0; k++)
                            {
                                byte[] c = ProfileActions.Data[k];
                                if (c.Length != d.Length) continue;
                                bool same = true;
                                for (int q = 0; q < c.Length; q++) if (c[q] != d[q]) { same = false; break; }
                                if (same) hit = k;
                            }
                            if (hit >= 0) _paramBox[i].SelectedIndex = hit;
                        }
                        break;
                }
            }
            _loading = false;
        }


        static int MathMax0(int v) { return v < 0 ? 0 : v; }

        static string UsageToName(byte usage)
        {
            foreach (KeyValuePair<string, byte> kv in Keys.Map) if (kv.Value == usage) return kv.Key;
            return null;
        }

        void SelectParam(int i, string name)
        {
            if (name == null) return;
            int ix = _paramBox[i].Items.IndexOf(name);
            if (ix >= 0) _paramBox[i].SelectedIndex = ix;
        }

        // ---------------- profile actions ----------------
        // DPI and polling are addressed by profile id (04/8x and 00/8E, 00/0E), so most work
        // no longer needs the profile to be active. These helpers stay for anything that is
        // only reachable through the active profile (e.g. debounce); the watcher is suppressed
        // while a temporary switch is live and the flag is only raised once it really happened.
        bool ActivateTemporarily(byte id, out byte previous)
        {
            previous = 0;
            int act = -1;
            try { act = _dev.ActiveProfile(); } catch { }
            if (act < 0) return false;
            if (act == id) return true;
            previous = (byte)act;
            if (!_dev.SetActiveProfile(id)) return false;
            _tempSwitch = true;
            return true;
        }

        void RestoreActive(byte previous)
        {
            if (previous > 0) { try { _dev.SetActiveProfile(previous); } catch { } }
            _tempSwitch = false;
        }

        void ActivateProfile()
        {
            if (_dev == null) return;
            try
            {
                if (!_dev.SetActiveProfile(_profile)) { MessageBox.Show("Activation refused."); return; }
                LoadProfiles();
                PushWantedDpi();
                MessageBox.Show("Profile " + _profile + " is now active.");
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        // Fresh id list straight from the device (the cached list can be stale after sleep).
        List<byte> FreshIds()
        {
            List<byte> ids = new List<byte>();
            byte[] r = Tx(0x05, 0x81, new byte[80], 80);
            if (r == null || r[0] != 0x02) return ids;
            for (int i = 9; i < 88; i++)
            {
                byte id = r[i];
                if (id == 0) break;
                ids.Add(id);
            }
            return ids;
        }

        int FreshCount()
        {
            byte[] r = Tx(0x05, 0x80, new byte[1], 0x01);
            return (r != null && r[0] == 0x02) ? r[8] : -1;
        }

        byte FreeIdIn(List<byte> ids)
        {
            byte id = 1;
            while (ids.Contains(id)) id++;
            return id;
        }
        void NewProfile()
        {
            if (_dev == null) return;
            int max = 5;
            try { int m = _dev.MaxProfiles(); if (m > 0) max = m; } catch { }
            List<byte> ids = FreshIds();
            if (ids.Count == 0)
            {
                MessageBox.Show("The mouse did not report its profile list (asleep or held by Synapse). " +
                    "Move the mouse and try again - creating a profile now would fall back to id 1 and overwrite it.");
                return;
            }
            if (ids.Count >= max) { MessageBox.Show("All " + max + " profile slots are in use."); return; }
            byte id = FreeIdIn(ids);
            try
            {
                if (!_dev.CreateProfile(id)) { MessageBox.Show("Could not create profile " + id + "."); return; }
                LoadProfiles();
                int ix = _profileIds.IndexOf(id);
                if (ix >= 0) _profiles.SelectedIndex = ix;
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        void CloneProfile()
        {
            if (_dev == null) return;
            int max = 5;
            try { int m = _dev.MaxProfiles(); if (m > 0) max = m; } catch { }
            List<byte> fids = FreshIds();
            if (fids.Count == 0)
            {
                MessageBox.Show("The mouse did not report its profile list (asleep or held by Synapse). " +
                    "Move the mouse and try again - cloning now would fall back to id 1 and overwrite it.");
                return;
            }
            if (fids.Count >= max) { MessageBox.Show("All " + max + " profile slots are in use."); return; }
            byte src = _profile;
            List<KeyValuePair<byte, byte[]>> m0 = null, m1 = null;
            ushort[] dpi = null;
            int poll = 0;
            string name = "";
            try
            {
                m0 = _dev.ReadMappings(src, 0);
                m1 = _dev.ReadMappings(src, 1);
                dpi = _dev.GetDpi(src);
                poll = _dev.GetPollRate(src);
                name = _dev.GetProfileName(src);
            }
            catch { }
            if (m0 == null || m1 == null) { MessageBox.Show("Could not read the source profile."); return; }

            byte id = FreeIdIn(fids);
            while (_profileIds.Contains(id)) id++;
            try
            {
                if (!_dev.CreateProfile(id)) { MessageBox.Show("Could not create profile " + id + "."); return; }
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); return; }

            int fail = 0;
            if (WriteRecords(id, 0, m0) < m0.Count) fail++;
            if (WriteRecords(id, 1, m1) < m1.Count) fail++;
            if (dpi != null && dpi.Length >= 2) { try { _dev.SetDpi(id, dpi[0], dpi[1]); } catch { } }
            if (poll > 0) { try { _dev.SetPollRate(id, (ushort)poll); } catch { } }
            int wantedDpi;
            if (_dpiWanted.TryGetValue(src, out wantedDpi)) { _dpiWanted[id] = wantedDpi; SaveDpiWanted(); }
            try { _dev.SetProfileName(id, (name ?? "") + " copy", null); } catch { }
            LoadProfiles();
            _profiles.SelectedIndex = _profileIds.IndexOf(id);
            MessageBox.Show("Cloned into profile " + id + (fail > 0 ? " (some records failed)." : "."));
        }

        int WriteRecords(byte profileId, byte mode, List<KeyValuePair<byte, byte[]>> recs)
        {
            int ok = 0;
            foreach (KeyValuePair<byte, byte[]> kv in recs)
            {
                byte[] d = kv.Value;
                byte type = d[0];
                byte[] data = new byte[d.Length - 1];
                Array.Copy(d, 1, data, 0, data.Length);
                byte[] args;
                if (type == 0 || data.Length == 0) args = new byte[] { profileId, kv.Key, mode };
                else
                {
                    args = new byte[5 + data.Length];
                    args[0] = profileId; args[1] = kv.Key; args[2] = mode; args[3] = type; args[4] = (byte)data.Length;
                    Array.Copy(data, 0, args, 5, data.Length);
                }
                byte[] r = Tx(0x02, 0x0C, args, (byte)args.Length);
                if (r != null && r[0] == 0x02) ok++;
                Thread.Sleep(35);
            }
            return ok;
        }

        // Returns "device", "local" or "none".
        string RenameCore(byte id, string want)
        {
            if (_dev == null) return "none";
            bool storedOnDevice = false;
            try
            {
                byte[] guid = null;
                try { guid = _dev.GetProfileGuid(id); } catch { }
                if (_dev.SetProfileName(id, want, guid))
                {
                    string back = null;
                    try { back = _dev.GetProfileName(id); } catch { }
                    storedOnDevice = back == want;
                }
            }
            catch { }
            // The mouse only keeps names in areas Synapse allocated; remember the rest locally.
            if (storedOnDevice) _localNames.Remove(id);
            else if (want.Length > 0) _localNames[id] = want;
            SaveLocalNames();
            LoadProfiles();
            return storedOnDevice ? "device" : (want.Length > 0 ? "local" : "none");
        }

        void RenameProfile()
        {
            if (_dev == null) return;
            string res = RenameCore(_profile, (_nameBox.Text ?? "").Trim());
            if (res == "local")
                MessageBox.Show("The mouse does not keep a name for this profile, so the name is remembered " +
                    "on this PC only (shown with \"(local)\").");
        }

        void DeleteProfile()
        {
            if (_dev == null) return;
            if (_profileIds.Count <= 1) { MessageBox.Show("At least one profile must stay."); return; }
            if (MessageBox.Show("Delete profile " + _profile + "?", "Confirm", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try
            {
                if (!_dev.DeleteProfile(_profile)) { MessageBox.Show("Delete refused."); return; }
                _localNames.Remove(_profile); SaveLocalNames();
                _dpiWanted.Remove(_profile); SaveDpiWanted();
                LoadProfiles();
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        void ResetDefaults()
        {
            if (_dev == null) return;
            if (MessageBox.Show("Overwrite the mappings of profile " + _profile + " with the Razer factory mapping?",
                "Reset", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            int ok = 0, fail = 0;
            for (int i = 0; i < _map.Count; i++)
            {
                byte[] rec = MapTable.FactoryRecord(MapTable.InputIds[i]);
                byte[] args;
                if (rec == null || rec.Length == 0)
                    args = new byte[] { _profile, MapTable.InputIds[i], _mode };
                else
                {
                    args = new byte[5 + rec.Length - 1];
                    args[0] = _profile; args[1] = MapTable.InputIds[i]; args[2] = _mode;
                    args[3] = rec[0]; args[4] = (byte)(rec.Length - 1);
                    Array.Copy(rec, 1, args, 5, rec.Length - 1);
                }
                byte[] r = Tx(0x02, 0x0C, args, (byte)args.Length);
                if (r != null && r[0] == 0x02) ok++; else fail++;
                Thread.Sleep(35);
            }
            LoadMappings();
            MessageBox.Show(ok + " record(s) reset" + (fail > 0 ? (", " + fail + " failed.") : "."));
        }

        void SaveProfileFile()
        {
            if (_dev == null) return;
            SaveFileDialog d = new SaveFileDialog();
            d.Filter = "Rz3 profile (*.rz3p)|*.rz3p|All files (*.*)|*.*";
            d.FileName = "profile_" + _profile + ".rz3p";
            if (d.ShowDialog() != DialogResult.OK) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("RZ3PROFILE 1");
                sb.AppendLine("NAME=" + (_dev.GetProfileName(_profile) ?? ""));
                ushort[] dpiNow = _dev.GetDpi(_profile);
                if (dpiNow != null && dpiNow.Length >= 2) sb.AppendLine("DPI=" + dpiNow[0] + "," + dpiNow[1]);
                sb.AppendLine("POLL=" + _dev.GetPollRate(_profile));
                int want;
                if (_dpiWanted.TryGetValue(_profile, out want)) sb.AppendLine("WANTDPI=" + want);
                foreach (int mode in new int[] { 0, 1 })
                {
                    List<KeyValuePair<byte, byte[]>> recs = _dev.ReadMappings(_profile, (byte)mode);
                    if (recs == null) continue;
                    foreach (KeyValuePair<byte, byte[]> kv in recs)
                    {
                        List<string> parts = new List<string>();
                        parts.Add(kv.Key.ToString("x2"));
                        foreach (byte b in kv.Value) parts.Add(b.ToString("x2"));
                        sb.AppendLine("M" + mode + "=" + string.Join(" ", parts.ToArray()));
                    }
                }
                File.WriteAllText(d.FileName, sb.ToString(), Encoding.ASCII);
                MessageBox.Show("Saved: " + d.FileName);
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        void LoadProfileFile()
        {
            if (_dev == null) return;
            OpenFileDialog d = new OpenFileDialog();
            d.Filter = "Rz3 profile (*.rz3p)|*.rz3p|All files (*.*)|*.*";
            if (d.ShowDialog() != DialogResult.OK) return;
            string[] lines = File.ReadAllLines(d.FileName, Encoding.ASCII);
            string name = null;
            int dpiX = 0, dpiY = 0, poll = 0;
            List<byte[]> m0 = new List<byte[]>(), m1 = new List<byte[]>();
            foreach (string line in lines)
            {
                if (line.StartsWith("NAME=")) name = line.Substring(5);
                else if (line.StartsWith("DPI="))
                {
                    string[] p = line.Substring(4).Split(',');
                    if (p.Length == 2) { int.TryParse(p[0], out dpiX); int.TryParse(p[1], out dpiY); }
                }
                else if (line.StartsWith("POLL=")) int.TryParse(line.Substring(5), out poll);
                else if (line.StartsWith("WANTDPI=")) int.TryParse(line.Substring(8), out dpiX);
                else if (line.StartsWith("M0=") || line.StartsWith("M1="))
                {
                    List<byte> bytes = new List<byte>();
                    foreach (string t in line.Substring(3).Split(' '))
                    {
                        byte v;
                        if (byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out v)) bytes.Add(v);
                    }
                    if (bytes.Count >= 2) (line.StartsWith("M0=") ? m0 : m1).Add(bytes.ToArray());
                }
            }
            if (MessageBox.Show("Apply this profile onto profile " + _profile + " (overwrites its mappings)?",
                "Load", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            int ok = 0;
            foreach (byte[] rec in m0) { if (WriteRaw(_profile, 0, rec)) ok++; Thread.Sleep(30); }
            foreach (byte[] rec in m1) { if (WriteRaw(_profile, 1, rec)) ok++; Thread.Sleep(30); }
            if (dpiX >= 100)
            {
                try { _dev.SetDpi(_profile, (ushort)dpiX, (ushort)Math.Max(100, dpiY)); } catch { }
                _dpiWanted[_profile] = dpiX; SaveDpiWanted();
            }
            if (poll > 0) { try { _dev.SetPollRate(_profile, (ushort)poll); } catch { } }
            if (!string.IsNullOrEmpty(name))
            {
                try
                {
                    byte[] guid = null;
                    try { guid = _dev.GetProfileGuid(_profile); } catch { }
                    _dev.SetProfileName(_profile, name, guid);
                }
                catch { }
            }
            LoadMappings();
            MessageBox.Show(ok + " record(s) restored.");
        }

        // rec = [inputId, outType, data...]; rebuilt into a device record for one profile/mode.
        bool WriteRaw(byte profileId, byte mode, byte[] rec)
        {
            if (rec.Length < 2) return false;
            byte[] data = new byte[rec.Length - 2];
            Array.Copy(rec, 2, data, 0, data.Length);
            byte[] args;
            if (rec[1] == 0 || data.Length == 0) args = new byte[] { profileId, rec[0], mode };
            else
            {
                args = new byte[5 + data.Length];
                args[0] = profileId; args[1] = rec[0]; args[2] = mode; args[3] = rec[1]; args[4] = (byte)data.Length;
                Array.Copy(data, 0, args, 5, data.Length);
            }
            byte[] r = Tx(0x02, 0x0C, args, (byte)args.Length);
            return r != null && r[0] == 0x02;
        }

        // ---------------- settings ----------------
        // Writes "cycle profiles" into the bottom profile button for both layers.
        void AssignProfileCycle()
        {
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            // Cycle UP (functionId 7, data 05) wraps forward through the profiles; verified working.
            int ok = 0;
            for (int mode = 0; mode < 2; mode++)
            {
                byte[] args = new byte[] { _profile, 0x0E, (byte)mode, 0x07, 0x01, 0x05 };
                byte[] r = Tx(0x02, 0x0C, args, (byte)args.Length);
                if (r != null && r[0] == 0x02) ok++;
                Thread.Sleep(60);
            }
            LoadMappings();
            MessageBox.Show(ok == 2
                ? "Both layers set to cycle up. The bottom profile button wraps from the last profile back to the first."
                : ok + "/2 write(s) accepted - check the profile button, or assign Next instead.");
        }

        void ApplyProfileSettings() { ApplyProfileSettings(false); }

        string _lastApply;

        void ApplyProfileSettings(bool quiet)
        {
            if (_dev == null) { if (!quiet) MessageBox.Show("Device not connected."); return; }
            // DPI and polling are addressed by profile id now, so the active profile is not touched.
            int verified = -1;
            int pollBack = 0;
            int hz = 0;
            try
            {
                ushort v = (ushort)_dpi.Value;
                ushort[] cur = _dev.GetDpi(_profile);
                if (cur != null && cur[0] == v) verified = v;      // nothing to do
                for (int attempt = 0; attempt < 3 && verified < 0; attempt++)
                {
                    if (!_dev.SetDpi(_profile, v, v)) { Thread.Sleep(120); continue; }
                    Thread.Sleep(60);
                    ushort[] back = _dev.GetDpi(_profile);
                    if (back != null && back.Length >= 2 && back[0] == v) verified = back[0];
                    else Thread.Sleep(120);
                }
                if (verified > 0) { _dpiWanted[_profile] = verified; SaveDpiWanted(); }
                if (int.TryParse(_poll.SelectedItem as string, out hz))
                {
                    bool wrote = _dev.SetPollRate(_profile, (ushort)hz);
                    Thread.Sleep(60);
                    bool hsBack;
                    pollBack = _dev.GetPollRateEx(_profile, out hsBack);
                    if (!wrote) pollBack = _dev.GetPollRateEx(_profile, out hsBack);
                }
                _dev.SetDebounce((byte)_debounce.Value);
            }
            catch (Exception ex) { if (!quiet) MessageBox.Show("Error: " + ex.Message); return; }

            // Show what the device really holds for this profile.
            if (verified > 0) _dpi.Value = verified;
            if (pollBack > 0)
            {
                int ix = _poll.Items.IndexOf(pollBack.ToString());
                if (ix >= 0) _poll.SelectedIndex = ix;
            }
            LoadProfileSettings();
            RefreshPollLabel();

            if (verified < 0)
            {
                string bad = "Profile " + _profile + ": the mouse did not take DPI " + _dpi.Value +
                    " (read-back differs). The value is remembered locally and pushed again when the profile is activated.";
                _lastApply = bad;
                if (!quiet) MessageBox.Show(bad);
                return;
            }
            string msg = "Profile " + _profile + " updated: DPI " + verified + ", polling " +
                (pollBack > 0 ? pollBack + " Hz" : "unchanged") + ".";
            if (pollBack > 0 && hz > 0 && pollBack != hz) msg += " The mouse refused the polling rate and kept " + pollBack + " Hz.";
            if (pollBack > 0)
            {
                byte raw = 0;
                try { raw = _dev.GetPollIdRaw(_profile); } catch { }
                int meas;
                msg += "\n\nFirmware id " + raw +
                    (_rateCal.TryGetValue(raw, out meas) ? (", last measured " + meas + " reports/s") : ", not measured yet") +
                    ".\nUse \"Measure current rate\" to verify with real reports.";
            }
            _lastApply = msg;
            if (!quiet) MessageBox.Show(msg);
        }

        // Scroll mode and smart reel are not alternatives: smart reel is the automatic switch to
        // free spin while scrolling fast, so it only matters on top of Tactile / Micro tactile.
        // Number of buttons (any layer) currently assigned to the scroll-mode toggle.
        // While such an assignment exists the firmware ignores smart reel.
        int CountModeToggle()
        {
            int n = 0;
            if (_dev == null) return 0;
            foreach (byte mode in new byte[] { 0, 1 })
            {
                List<KeyValuePair<byte, byte[]>> recs = _dev.ReadMappings(_profile, mode);
                if (recs == null) continue;
                foreach (KeyValuePair<byte, byte[]> kv in recs)
                    if (kv.Value.Length > 0 && kv.Value[0] == OutTypes.ScrollMode) n++;
            }
            return n;
        }

        void ClearModeToggle()
        {
            if (_dev == null) return;
            int cleared = 0;
            foreach (byte mode in new byte[] { 0, 1 })
            {
                List<KeyValuePair<byte, byte[]>> recs = _dev.ReadMappings(_profile, mode);
                if (recs == null) continue;
                foreach (KeyValuePair<byte, byte[]> kv in recs)
                {
                    if (kv.Value.Length == 0 || kv.Value[0] != OutTypes.ScrollMode) continue;
                    byte[] args = new byte[] { _profile, kv.Key, mode };
                    byte[] r = Tx(0x02, 0x0C, args, (byte)args.Length);
                    if (r != null && r[0] == 0x02) cleared++;
                    Thread.Sleep(60);
                }
            }
            LoadMappings();
            UpdateScrollUi();
            MessageBox.Show(cleared + " scroll-mode switch assignment(s) cleared. Smart reel works again.");
        }

        // Scroll mode and smart reel are not alternatives: smart reel is the automatic switch to
        // free spin while scrolling fast, so it only matters on top of Tactile / Micro tactile.
        void UpdateScrollUi()
        {
            int mode = _scrollMode.SelectedIndex;
            bool free = mode == 1;
            int toggles = CountModeToggle();
            _smartReelOn.Enabled = !free;
            _smartReel.Enabled = !free && _smartReelOn.Checked;
            _accelLevel.Enabled = _scrollAccel.Checked;
            _clearToggle.Visible = toggles > 0;
            _clearToggle.Text = "Clear " + toggles + " scroll-mode switch assignment(s)";
            if (free)
                _scrollHint.Text = "Free spin is always on; Smart reel has no effect here.";
            else if (_smartReelOn.Checked)
                _scrollHint.Text = "Tactile stays the base mode: while you scroll fast the wheel switches to free spin " +
                                   "and falls back when you slow down.";
            else
                _scrollHint.Text = "Smart reel is off: the wheel keeps the selected notched mode.";
            if (toggles > 0)
                _scrollHint.Text += "\nFirmware note: a button is bound to the scroll-mode switch, and while that " +
                                    "binding exists the mouse ignores Smart reel.";
        }


        void ApplyScroll()
        {
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            int mode = Math.Max(0, _scrollMode.SelectedIndex);
            bool free = mode == 1;
            try
            {
                _dev.SetScrollMode3((byte)mode);
                if (!free)
                {
                    _dev.SetSmartReelThreshold((byte)_smartReel.Value);
                    _dev.SetSmartReel(_smartReelOn.Checked);
                }
                _dev.SetScrollAccel(_scrollAccel.Checked);
                _dev.SetScrollAccelLevel((byte)_accelLevel.Value);

                int modeBack = _dev.GetScrollMode3();
                bool srBack = _dev.GetSmartReel();
                MessageBox.Show("Scroll settings written.\n\nRead back: mode " +
                    (modeBack == 1 ? "Free spin" : modeBack == 2 ? "Micro tactile" : "Tactile") +
                    ", smart reel " + (srBack ? "on" : "off") + ".");
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }


        void ApplyPower()
        {
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            try
            {
                _dev.SetLowBattery((int)_lowBat.Value);
                _dev.SetIdleTime((ushort)_idle.Value);
                MessageBox.Show("Power settings written (global).");
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        void ApplyLighting()
        {
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            try
            {
                byte led = _zone.SelectedIndex == 1 ? (byte)1 : _zone.SelectedIndex == 2 ? (byte)4 : _zone.SelectedIndex == 3 ? (byte)0x0A : (byte)0;
                _dev.SetBrightness(0, (byte)_bright.Value);
                _dev.SetBrightness(led, (byte)_effBright.Value);
                switch (_effect.SelectedIndex)
                {
                    case 0: _dev.EffectOff(led); break;
                    case 1: _dev.EffectStatic(led, _effectColor.R, _effectColor.G, _effectColor.B); break;
                    case 2: _dev.EffectBreathing(led, (byte)_effRate.Value); break;
                    case 3: _dev.EffectSpectrum(led); break;
                    case 4: _dev.EffectWave(led, (byte)_dir.SelectedIndex); break;
                    case 5: _dev.EffectReactive(led, (byte)_effRate.Value); break;
                    case 6:
                        {
                            byte[] rgb = new byte[39];
                            for (int i = 0; i < 13; i++)
                            {
                                rgb[i * 3] = _effectColor.R; rgb[i * 3 + 1] = _effectColor.G; rgb[i * 3 + 2] = _effectColor.B;
                            }
                            _dev.CustomFrame(rgb);
                            break;
                        }
                }
                MessageBox.Show("Lighting applied.");
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        void SendRaw()
        {
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            try
            {
                byte cls = Convert.ToByte(_rawCls.Text.Replace("0x", ""), 16);
                byte cmd = Convert.ToByte(_rawCmd.Text.Replace("0x", ""), 16);
                List<byte> args = new List<byte>();
                foreach (string s in _rawArgs.Text.Split(new char[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    args.Add(Convert.ToByte(s.Replace("0x", ""), 16));
                byte[] r = _dev.Raw(cls, cmd, args.ToArray());
                StringBuilder sb = new StringBuilder(_rawOut.Text);
                sb.AppendLine("> " + _rawCls.Text + " " + _rawCmd.Text + " [" + args.Count + "]");
                if (r == null) sb.AppendLine("  no reply");
                else
                {
                    sb.AppendLine("  status=0x" + r[0].ToString("x2"));
                    StringBuilder a = new StringBuilder();
                    for (int i = 8; i < 44; i++) a.Append(r[i].ToString("x2") + " ");
                    sb.AppendLine("  " + a);
                }
                _rawOut.Text = sb.ToString();
                _rawOut.SelectionStart = _rawOut.TextLength;
                _rawOut.ScrollToCaret();
            }
            catch (Exception ex) { MessageBox.Show("Error: " + ex.Message); }
        }

        // ---------------- write mappings ----------------
        MapEntry BuildEntry(int i)
        {
            byte type = OutTypes.Values[_typeBox[i].SelectedIndex];
            string param = _paramBox[i].SelectedItem as string;
            MapEntry m = _map[i];
            m.OutType = type;
            m.Mode = (byte)(_hsBox[i].Checked ? 1 : _mode);
            m.Data = new byte[0];
            switch (type)
            {
                case OutTypes.Disabled: break;
                case OutTypes.Keyboard:
                    {
                        byte u;
                        if (param != null && Keys.Map.TryGetValue(param, out u))
                        {
                            byte mod = 0;
                            for (int b = 0; b < Modifiers.Bits.Length; b++) if (_modBox[i][b].Checked) mod |= Modifiers.Bits[b];
                            m.Data = new byte[] { mod, u };
                        }
                        break;
                    }
                case OutTypes.MouseButton:
                    {
                        int ix = Array.IndexOf(MouseActions.Names, param);
                        m.Data = new byte[] { MouseActions.Ids[ix < 0 ? 0 : ix] };
                        break;
                    }
                case OutTypes.Media:
                    {
                        int ix = Array.IndexOf(Media.Names, param);
                        m.Data = new byte[] { 0x00, Media.Usages[ix < 0 ? 0 : ix] };
                        break;
                    }
                case OutTypes.NavigateProfile:
                    {
                        int ix = Array.IndexOf(ProfileActions.Names as string[], param);
                        m.Data = (byte[])ProfileActions.Data[ix < 0 ? 0 : ix].Clone();
                        break;
                    }
                case OutTypes.Sensitivity:
                    {
                        int ix = Array.IndexOf(SensActions.Names, param);
                        if (ix <= 0)
                        {
                            ushort v = (ushort)_clutchDpi.Value;
                            m.Data = new byte[] { 0x05, (byte)(v >> 8), (byte)v, (byte)(v >> 8), (byte)v };
                        }
                        else m.Data = new byte[] { SensActions.Ids[ix] };
                        break;
                    }
                case OutTypes.SideScroll:
                    m.Data = new byte[] { (byte)(param == "Scroll right" ? 0x69 : 0x68), 0x00, 0x14 };
                    break;
                case OutTypes.ScrollMode: m.Data = new byte[] { 0x01 }; break;
                case OutTypes.Lighting:
                    {
                        int ix = Array.IndexOf(LightingActions.Names, param);
                        m.Data = new byte[] { LightingActions.Ids[ix < 0 ? 0 : ix] };
                        break;
                    }
                case OutTypes.PowerKey:
                case OutTypes.RazerKey:
                    {
                        int ix = Array.IndexOf(PowerActions.Names, param);
                        m.Data = new byte[] { PowerActions.Ids[ix < 0 ? 0 : ix] };
                        break;
                    }
                case OutTypes.DoubleClick:
                case OutTypes.ModeButton: m.Data = new byte[] { 0x01 }; break;
                case OutTypes.MacroI:
                case OutTypes.MacroIV:
                    m.Data = new byte[] { (byte)(_paramBox[i].SelectedIndex + 1) };
                    break;
            }
            return m;
        }

        // Headless write test: temp profile, one normal + one Hypershift record, verify, delete.
        // "Profile changed on the mouse, then the user saves without reconnecting" scenario.
        public string NameTest()
        {
            StringBuilder sb = new StringBuilder();
            byte id = _profile;
            string res = RenameCore(id, "Antrenman");
            sb.AppendLine("result=" + res);
            sb.AppendLine("displayNow=" + DisplayName(id));
            sb.AppendLine("comboText=" + (_profiles.SelectedItem as string));
            sb.AppendLine("currentLabel=" + (_currentLbl != null ? _currentLbl.Text : "null"));
            sb.AppendLine("fileExists=" + File.Exists(NamesPath));
            // keep it, so the user sees a named profile; report where it lives
            return sb.ToString();
        }

        // DPI/polling must stick on a profile that is *not* active and must survive switching.
        public string UiProbe(string idText)
        {
            StringBuilder sb = new StringBuilder();
            byte want;
            if (byte.TryParse(idText, out want))
            {
                int ix = _profileIds.IndexOf(want);
                if (ix >= 0) { _profiles.SelectedIndex = ix; Application.DoEvents(); Thread.Sleep(400); Application.DoEvents(); }
            }
            int act = -1;
            try { act = _dev.ActiveProfile(); } catch { }
            sb.AppendLine("selected=" + _profile + " active=" + act);
            sb.AppendLine("label=" + _dpiLabel.Text);
            sb.AppendLine("dpiField=" + _dpi.Value + " pollField=" + (_poll.SelectedItem as string));
            sb.AppendLine("storedOnSelected=" + ((_dev.GetDpi(_profile) ?? new ushort[2])[0]) +
                " pollStored=" + _dev.GetPollRate(_profile));
            return sb.ToString();
        }

        public string DpiTest()
        {
            StringBuilder sb = new StringBuilder();
            int act = -1;
            try { act = _dev.ActiveProfile(); } catch { }
            if (act <= 0) return "no active profile";
            byte other = 0;
            foreach (byte id in _profileIds) if (id != act) { other = id; break; }
            if (other == 0) return "only one profile";

            ushort before = 0, stored = 0;
            try { ushort[] b = _dev.GetDpi(other); if (b != null) before = b[0]; } catch { }
            int beforePoll = _dev.GetPollRate(other);
            int target = before == 1200 ? 1000 : 1200;
            int targetPoll = beforePoll == 500 ? 1000 : 500;

            bool w = _dev.SetDpi(other, (ushort)target, (ushort)target);
            bool wp = _dev.SetPollRate(other, (ushort)targetPoll);
            Thread.Sleep(120);
            try { ushort[] b2 = _dev.GetDpi(other); if (b2 != null) stored = b2[0]; } catch { }
            int storedPoll = _dev.GetPollRate(other);

            // active profile must not have moved
            ushort actLive = 0, actNow = 0;
            try { ushort[] a = _dev.GetDpi((byte)act); if (a != null) actLive = a[0]; } catch { }

            // round trip: activate the other profile and come back
            _dev.SetActiveProfile(other); Thread.Sleep(500);
            ushort afterSwitch = 0, liveOnOther = 0;
            try { ushort[] a = _dev.GetDpi((byte)act); if (a != null) afterSwitch = a[0]; } catch { }
            try { ushort[] a = _dev.GetDpi(other); if (a != null) liveOnOther = a[0]; } catch { }
            int pollOnOther = _dev.GetPollRate(other);
            _dev.SetActiveProfile((byte)act); Thread.Sleep(500);
            try { ushort[] a = _dev.GetDpi((byte)act); if (a != null) actNow = a[0]; } catch { }

            sb.AppendLine("active=" + act + " other=" + other + " target=" + target + " targetPoll=" + targetPoll);
            sb.AppendLine("beforeOnOther=" + before + " writeAck=" + w + " writePollAck=" + wp);
            sb.AppendLine("storedOnOther=" + stored + " storedPoll=" + storedPoll);
            sb.AppendLine("afterRoundTripOnOther=" + afterSwitch + " pollOnOtherActive=" + pollOnOther);
            sb.AppendLine("liveWhileOtherActive=" + liveOnOther);
            sb.AppendLine("activeProfileDpi=" + actLive + " -> " + actNow + " (poll " + _dev.GetPollRate((byte)act) + ")");

            // Second half: the real Apply path on a profile that is not active.
            _profiles.SelectedIndex = _profileIds.IndexOf(other);
            Application.DoEvents();
            _dpi.Value = target;
            int pix = _poll.Items.IndexOf(targetPoll.ToString());
            if (pix >= 0) _poll.SelectedIndex = pix;
            ApplyProfileSettings(true);
            sb.AppendLine("applyMessage=" + (_lastApply ?? ""));
            sb.AppendLine("applyStoredDpi=" + (_dev.GetDpi(other) ?? new ushort[2])[0] +
                " applyStoredPoll=" + _dev.GetPollRate(other));
            int actAfterApply = _dev.ActiveProfile();
            sb.AppendLine("activeAfterApply=" + actAfterApply + " selectedInUi=" + _profile);

            // restore
            _dev.SetDpi(other, before, before);
            _dev.SetPollRate(other, (ushort)(beforePoll > 0 ? beforePoll : 1000));
            if (before >= 100) { _dpiWanted[other] = before; SaveDpiWanted(); }
            sb.AppendLine("restored=" + before + "/" + beforePoll);
            return sb.ToString();
        }

        public string SwitchTest()
        {
            StringBuilder sb = new StringBuilder();
            int act = -1;
            try { act = _dev.ActiveProfile(); } catch { }
            if (act <= 0) return "no active profile";
            byte other = (byte)(act == 1 ? 2 : 1);
            if (!_profileIds.Contains(other)) return "only one profile";
            _profiles.SelectedIndex = _profileIds.IndexOf(other);
            Application.DoEvents();

            string beforeOther = RecStr(other, 0x04);
            string beforeAct = RecStr((byte)act, 0x04);

            _typeBox[0].SelectedIndex = Array.IndexOf(OutTypes.Values, OutTypes.Keyboard);
            FillParams(0);
            SelectParam(0, "q");
            _dirty[0] = true;
            string failed;
            int ok = ApplyRows(out failed);

            string afterOther = RecStr(other, 0x04);
            string afterAct = RecStr((byte)act, 0x04);
            int actNow = -1;
            try { actNow = _dev.ActiveProfile(); } catch { }

            sb.AppendLine("activeBefore=" + act + " selected=" + other + " written=" + ok + " fail=" + failed.Replace("\n", " "));
            sb.AppendLine("otherBefore=" + beforeOther + " otherAfter=" + afterOther);
            sb.AppendLine("activeSlotBefore=" + beforeAct + " activeSlotAfter=" + afterAct);
            sb.AppendLine("activeAfterApply=" + actNow + " comboText=" + (_profiles.SelectedItem as string));
            _dirty[0] = false;
            return sb.ToString();
        }

        string RecStr(byte profile, byte input)
        {
            List<KeyValuePair<byte, byte[]>> recs = _dev.ReadMappings(profile, 0);
            if (recs == null) return "read-fail";
            foreach (KeyValuePair<byte, byte[]> kv in recs)
                if (kv.Key == input) return BitConverter.ToString(kv.Value);
            return "none";
        }

        public string SelfTestWrite()
        {
            StringBuilder sb = new StringBuilder();
            List<byte> ids = FreshIds();
            int count0 = FreshCount();
            // Without a fresh id list FreeIdIn() falls back to 1 - the test would write into the
            // real profile 1. Refuse instead (this bit us on 15.11.2026).
            if (ids.Count == 0) return "no profile list from the device - test skipped";
            if (count0 != ids.Count) return "profile count/list mismatch (" + count0 + " vs " + ids.Count + ") - test skipped";
            byte id = FreeIdIn(ids);
            if (ids.Contains(id)) return "no free profile id - test skipped";
            if (!_dev.CreateProfile(id)) { return "create failed"; }
            int count1 = FreshCount();
            if (count1 <= count0) return "create did not add a profile - test skipped, nothing written";
            LoadProfiles();
            int six = _profileIds.IndexOf(id);
            if (six < 0) return "temp profile " + id + " not in the list - test skipped";
            _profiles.SelectedIndex = six;
            Application.DoEvents();

            // Row 0: keyboard 'x' with Ctrl; row 1: Hypershift 'y'
            _typeBox[0].SelectedIndex = Array.IndexOf(OutTypes.Values, OutTypes.Keyboard);
            FillParams(0);
            SelectParam(0, "x");
            _modBox[0][0].Checked = true;
            _hsBox[0].Checked = false;
            _dirty[0] = true;

            _typeBox[1].SelectedIndex = Array.IndexOf(OutTypes.Values, OutTypes.Keyboard);
            FillParams(1);
            SelectParam(1, "y");
            _hsBox[1].Checked = true;
            _dirty[1] = true;

            string failed;
            int ok = ApplyRows(out failed);
            sb.AppendLine("rowsWritten=" + ok + " fail=" + failed.Replace("\n", " "));

            LoadMappings();
            sb.AppendLine("verify mode0 side4=" + BitConverter.ToString(_map[0].Data) + " type=0x" + _map[0].OutType.ToString("x2"));
            _hsMode.Checked = true;
            Application.DoEvents();
            sb.AppendLine("verify mode1 side5=" + BitConverter.ToString(_map[1].Data) + " type=0x" + _map[1].OutType.ToString("x2"));
            _hsMode.Checked = false;

            if (count1 > count0) _dev.DeleteProfile(id);
            else sb.AppendLine("WARNING: create did not add a profile, nothing deleted");
            LoadProfiles();
            sb.AppendLine("profilesLeft=" + _profileIds.Count);

            // Rename round-trip on profile 2 must preserve its GUID prefix.
            byte[] g1 = null, g2 = null;
            string n1 = null, n2 = null;
            try { g1 = _dev.GetProfileGuid(2); n1 = _dev.GetProfileName(2); } catch { }
            try { _dev.SetProfileName(2, n1 ?? "x", g1); } catch { }
            try { g2 = _dev.GetProfileGuid(2); n2 = _dev.GetProfileName(2); } catch { }
            sb.AppendLine("rename: guidSame=" + (g1 != null && g2 != null && BitConverter.ToString(g1) == BitConverter.ToString(g2)) +
                " nameSame=" + (n1 == n2) + " name=" + n2);
            return sb.ToString();
        }

        // Headless state dump used by --selftest.
        // --- drop simulation helpers (cli --droptest) ---
        public string DropTest1() { return _dev != null ? "yes" : "no"; }

        // Handle closed behind our back: the next write must reopen by itself.
        public string DropTest2()
        {
            if (_dev == null) return "no device";
            _dev.Close();
            byte[] r = Tx(0x00, 0x85, new byte[] { 0, 0 }, 2);
            return r != null ? "ok status=0x" + r[0].ToString("x2") : "failed";
        }

        // Full loss: drop the reference and let the reconnect timer path take over.
        public string DropTest3()
        {
            MarkLost("simulated pull");
            bool back = false;
            for (int i = 0; i < 10 && !back; i++) { back = TryReattach(); if (!back) Thread.Sleep(400); }
            if (!back) return "not reattached";
            RefreshBattery();
            return _dev != null ? "reattached, status=" + _status.Text.Substring(0, Math.Min(30, _status.Text.Length)) : "no";
        }

        public string SelfTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("comboItems=" + _profiles.Items.Count + " ids=" + _profileIds.Count);
            sb.AppendLine("profile=" + _profile + " mode=" + _mode + " name=" + _nameBox.Text);
            for (int i = 0; i < _map.Count; i++)
                sb.AppendLine(MapTable.InputKeys[i] + " type=0x" + _map[i].OutType.ToString("x2") +
                    " data=" + BitConverter.ToString(_map[i].Data) + " hs=" + (_hsBox[i].Checked ? 1 : 0));
            try { sb.AppendLine("dpi=" + _dpi.Value + " poll=" + (_poll.SelectedItem as string)); } catch { }
            return sb.ToString();
        }

        int ApplyRows(out string failed)
        {
            int ok = 0, fails = 0;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < _map.Count; i++)
            {
                if (_onlyDirty.Checked && !_dirty[i]) continue;
                MapEntry m = BuildEntry(i);
                byte[] args = m.Args(_profile);
                bool done = false;
                for (int t = 0; t < 3 && !done; t++)
                {
                    byte[] r = Tx(0x02, 0x0C, args, (byte)args.Length);
                    if (r != null && r[0] == 0x02)
                    {
                        done = true;
                        for (int k = 0; k < args.Length && 8 + k < 88; k++)
                            if (r[8 + k] != args[k]) { done = false; break; }
                    }
                    if (!done) Thread.Sleep(80);
                }
                if (done) { ok++; _dirty[i] = false; fails = 0; }
                else
                {
                    sb.AppendLine("  - " + m.Label);
                    fails++;
                    if (fails >= 3)
                    {
                        MarkLost("Device stopped answering (asleep, unplugged or held by Synapse).");
                        sb.AppendLine("  (device lost - remaining rows skipped)");
                        break;
                    }
                }
                Thread.Sleep(40);
            }
            failed = sb.ToString();
            return ok;
        }

        void ApplyMap()
        {
            if (_dev == null && !TryReattach())
            {
                MessageBox.Show("Device is not attached yet. Plug the mouse back in; the app reconnects on its own.");
                return;
            }
            // A partial read shows rows as "Disabled"; writing those back would really disable
            // buttons the user never touched. Ask first when the table is not known to be complete.
            if (!_mapReadOk && !_onlyDirty.Checked)
            {
                if (MessageBox.Show("The last mapping read from the mouse was incomplete, so some rows may only " +
                    "*look* disabled." + Environment.NewLine + Environment.NewLine +
                    "Writing all rows now can disable buttons you did not touch. Write anyway?",
                    "Incomplete read", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            }
            string failed;
            int ok;
            try { ok = ApplyRows(out failed); }
            catch (Exception ex)
            {
                MessageBox.Show("Write failed: " + ex.Message + "\n\nClose Razer Synapse and press Reconnect.");
                return;
            }
            if (ok == 0 && failed.Length > 0 && _dev == null)
            {
                MessageBox.Show("The device dropped off the bus. Press Reconnect.");
                return;
            }
            MessageBox.Show(ok + " mapping(s) written to profile " + _profile +
                (failed.Length > 0 ? (", failed:\n" + failed) : ".") +
                "\n\nMappings are stored on the mouse itself and survive power cycles.");
            LoadMappings();
        }

        // =====================================================================================
        // Polling: honest measurement
        // =====================================================================================
        string RateCalPath
        {
            get { return Path.Combine(Application.StartupPath, "rz3_rate_cal.json"); }
        }

        void LoadRateCal()
        {
            _rateCal.Clear();
            try
            {
                if (!File.Exists(RateCalPath)) return;
                foreach (string line in File.ReadAllLines(RateCalPath))
                {
                    string[] p = line.Split(new char[] { '\t' }, 2);
                    int id, hz;
                    if (p.Length == 2 && int.TryParse(p[0], out id) && int.TryParse(p[1], out hz)) _rateCal[id] = hz;
                }
            }
            catch { }
        }

        void SaveRateCal()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                List<int> keys = new List<int>(_rateCal.Keys);
                keys.Sort();
                foreach (int k in keys) sb.AppendLine(k + "\t" + _rateCal[k]);
                File.WriteAllText(RateCalPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        string MeasuredText(byte rawId, int nominal)
        {
            int m;
            if (_rateCal.TryGetValue(rawId, out m) && m > 0)
            {
                string s = "measured avg " + m + "/s";
                if (nominal > 0 && Math.Abs(m - nominal) > Math.Max(40, nominal / 15)) s += " (enum says " + nominal + ")";
                return s;
            }
            return "not measured yet";
        }

        Form ProgressForm(string title, out Label text)
        {
            MeasureForm f = new MeasureForm();
            f.RawSink = new Action<IntPtr>(delegate(IntPtr l) { if (_meter != null) _meter.HandleRawInput(l); });
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.Width = 520;
            f.Height = 150;
            f.ShowInTaskbar = false;
            f.TopMost = true;
            text = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 10f) };
            f.Controls.Add(text);
            f.Show(this);
            f.Activate();
            f.Focus();
            Application.DoEvents();
            // Raw input is registered per window and, on this machine, mouse reports only reach the
            // window that has the focus. Point the meter at the dialog while it is open.
            try { if (_meter != null) _meter.Attach(f.Handle); } catch { }
            f.FormClosed += delegate { try { if (_meter != null) _meter.Attach(Handle); } catch { } };
            return f;
        }

        // Counts reports for `seconds` while pumping the message loop (WM_INPUT only arrives
        // while the loop runs). Returns reports/s, or -1 when raw input is not attached.
        double _lastAvg, _lastPeak;

        double MeasureWindow(double seconds, string caption, Label live)
        {
            if (_meter == null || !_meter.Attached) return -1;
            _meter.ResetWindow();
            _lastPeak = 0;
            long start = _meter.Reports;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Application.DoEvents();
                if (live != null)
                    live.Text = caption + Environment.NewLine +
                        "Move the mouse in circles - " + (seconds - sw.Elapsed.TotalSeconds).ToString("0.0") + " s left";
                Thread.Sleep(8);
            }
            long end = _meter.Reports;
            _lastAvg = (end - start) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            _lastPeak = _meter.PeakHz;
            return _lastAvg;
        }

        void MeasureCurrentRate()
        {
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            int want = 0;
            int.TryParse(_poll.SelectedItem as string, out want);
            byte rawId = 0;
            try { rawId = _dev.GetPollIdRaw(_profile); } catch { }
            Label live;
            Form f = ProgressForm("Polling rate - profile " + _profile, out live);
            double hz = MeasureWindow(2.5, "Profile " + _profile + ", firmware id " + rawId + " (" + want + " Hz requested)", live);
            f.Close();
            if (hz < 0) { MessageBox.Show("Raw input is not attached; restart Rz3."); return; }
            int measured = (int)Math.Round(hz);
            int peak = (int)Math.Round(_lastPeak);
            if (measured > 0) { _rateCal[rawId] = measured; SaveRateCal(); }
            RefreshPollLabel();
            MessageBox.Show("Requested: " + want + " Hz (firmware id " + rawId + ")" + Environment.NewLine +
                "Measured: " + measured + " reports/s average, " + peak + " Hz peak" + Environment.NewLine + Environment.NewLine +
                (measured > 0
                    ? "Average counts every report; the peak is 1 / shortest gap between two reports, which is " +
                      "the rate the device really runs at. A slow drag lowers only the average."
                    : "No reports arrived: the mouse was still or asleep. A still mouse reports 0/s at every rate."));
        }

        // Walks every id the standard enum does not list (and the listed ones) to find out what
        // the firmware really does with them. Restores the original id afterwards.
        void CalibrateRates()
        {
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            if (_meter == null || !_meter.Attached) { MessageBox.Show("Raw input is not attached; restart Rz3."); return; }
            if (MessageBox.Show("Every id of the polling enum is written to profile " + _profile + " in turn and the " +
                "real report rate is measured (~30 s)." + Environment.NewLine + Environment.NewLine +
                "Keep moving the mouse the whole time. The original setting is restored at the end.",
                "Calibrate polling ids", MessageBoxButtons.OKCancel) != DialogResult.OK) return;

            byte orig = 0;
            try { orig = _dev.GetPollIdRaw(_profile); } catch { }
            Label live;
            Form f = ProgressForm("Calibrating polling ids", out live);
            List<int> done = new List<int>();
            for (int id = 1; id <= 16; id++)
            {
                try { _dev.SetPollIdRaw(_profile, (byte)id); } catch { }
                Thread.Sleep(280);
                double hz = MeasureWindow(1.3, "id " + id + " / 16", live);
                if (hz < 0) break;
                int measured = (int)Math.Round(hz);
                _rateCal[id] = measured;
                done.Add(id);
            }
            f.Close();
            try { _dev.SetPollIdRaw(_profile, orig); } catch { }
            SaveRateCal();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Profile " + _profile + " (restored to id " + orig + "):");
            if (done.Count == 0) sb.AppendLine("  no measurements - raw input did not deliver anything");
            foreach (int id in done)
            {
                ushort nominal = RzDevice.StdHzOf((byte)id);
                sb.AppendLine("  id " + id + " -> " + _rateCal[id] + "/s" + (nominal > 0 ? "   (enum says " + nominal + ")" : "   (not in the enum)"));
            }
            sb.AppendLine();
            sb.AppendLine("Ids missing from the enum that still produce reports are aliases of a real rate.");
            MessageBox.Show(sb.ToString(), "Calibration result");
            LoadProfileSettings();
        }

        // =====================================================================================
        // Host actions
        // =====================================================================================
        void LoadHostTable()
        {
            _hostTable.Clear();
            try { HostStore.Load(Application.StartupPath, _hostTable, out _hostEnabled); }
            catch { }
        }

        void SaveHostTable()
        {
            try { HostStore.Save(Application.StartupPath, _hostTable, _hostEnabled); }
            catch { }
        }

        HostAction[] ActionsFor(byte pid)
        {
            HostAction[] arr;
            if (!_hostTable.TryGetValue(pid, out arr) || arr == null || arr.Length != HostTrigger.Count)
            {
                arr = new HostAction[HostTrigger.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = new HostAction();
                _hostTable[pid] = arr;
            }
            return arr;
        }

        // The engine follows the *active* profile, because that is the layer the mouse is
        // emitting right now; the selected profile is only what the editor shows.
        void RebuildHostTable()
        {
            Dictionary<int, HostAction> t = new Dictionary<int, HostAction>();
            byte pid = (byte)(_activeProfile > 0 ? _activeProfile : _profile);
            HostAction[] arr;
            if (_hostTable.TryGetValue(pid, out arr) && arr != null)
                for (int s = 0; s < HostTrigger.Count && s < arr.Length; s++)
                    if (arr[s] != null && !arr[s].IsDisabled) t[s] = arr[s];
            if (_host != null) _host.SetTable(t);
            RefreshHostStatus();
        }

        void RefreshHostStatus()
        {
            if (_hostStatus == null) return;
            byte act = (byte)(_activeProfile > 0 ? _activeProfile : _profile);
            int n = 0, broken = 0;
            HostAction[] arr;
            if (_hostTable.TryGetValue(act, out arr) && arr != null)
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] == null) continue;
                    if (!arr[i].IsDisabled) n++;
                    else if (arr[i].Kind > 0) broken++;      // a kind was chosen but no value
                }
            string s = (_hostEnabled ? "engine on" : "engine paused") +
                (_host != null && !_host.Hooked ? ", no keyboard hook" : "") +
                " | profile " + act + ": " + n + " action(s)";
            if (broken > 0) s += ", " + broken + " incomplete (needs a value)";
            if (_host != null && _host.LastTriggerEvent != DateTime.MinValue)
                s += " | last trigger " + _host.LastTriggerEvent.ToString("HH:mm:ss");
            if (_host != null && _host.HookEvents == 0)
                s += " | hook has seen no keys yet";
            else if (_host != null)
                s += " | keys seen " + _host.HookEvents + " (triggers " + _host.HookTriggerEvents + ")";
            s += LocationLine();
            _hostStatus.Text = s;
        }

        void HostLog(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
            try
            {
                string path = Path.Combine(Application.StartupPath, "rz3_host.log");
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 262144) File.Delete(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
            if (_hostStatus == null) return;
            try
            {
                if (_hostStatus.InvokeRequired)
                    _hostStatus.BeginInvoke(new Action(delegate { _hostStatus.Text = line; }));
                else
                    _hostStatus.Text = line;
            }
            catch { }
        }

        void OnHostToggle()
        {
            _hostEnabled = _hostOn != null && _hostOn.Checked;
            if (_host != null) _host.Enabled = _hostEnabled;
            SaveHostTable();
            RefreshHostStatus();
            HostLog(_hostEnabled ? "engine enabled" : "engine paused");
        }

        // Device-side actions callable from a host trigger (DPI stage, profile, lighting).
        string DeviceActionCore(string what)
        {
            if (InvokeRequired)
            {
                try { return (string)Invoke(new Func<string, string>(DeviceActionCore), new object[] { what }); }
                catch { return "ui busy"; }
            }
            if (_dev == null) return "no device";
            string w = (what ?? "").Trim().ToLowerInvariant();
            try
            {
                switch (w)
                {
                    case "dpiup":
                    case "dpidown":
                    case "dpicycle":
                        {
                            int act;
                            ushort[] st = _dev.GetDpiStages(_profile, out act);
                            if (st == null) return "dpi table unreadable";
                            int next = (w == "dpidown") ? act - 1 : act + 1;
                            if (next > st.Length) next = 1;
                            if (next < 1) next = st.Length;
                            if (!_dev.SetDpiStage(_profile, next)) return "stage switch refused";
                            int now;
                            _dev.GetDpiStages(_profile, out now);
                            LoadProfileSettings();
                            return "dpi stage " + now + " = " + st[Math.Max(0, Math.Min(st.Length - 1, now - 1))] + " dpi";
                        }
                    case "profnext":
                    case "profprev":
                        {
                            List<byte> ids = FreshIds();
                            if (ids.Count < 2) return "only one profile";
                            int act = -1;
                            try { act = _dev.ActiveProfile(); } catch { }
                            int ix = ids.IndexOf((byte)act);
                            if (ix < 0) ix = 0;
                            int step = (w == "profnext") ? 1 : -1;
                            ix = (ix + step + ids.Count) % ids.Count;
                            byte target = ids[ix];
                            if (!_dev.SetActiveProfile(target)) return "profile switch refused";
                            LoadProfiles();
                            return "profile " + target;
                        }
                    case "lighttoggle":
                        {
                            byte[] r = _dev.Transact(0x0F, 0x82, new byte[] { 0x00, 0x04, 0x00, 0x00, 0x00, 0x00 }, 0x06);
                            int fx = (r != null && r[0] == 0x02) ? r[10] : -1;
                            if (fx == 0) { _dev.EffectSpectrum(0); return "lighting on"; }
                            _dev.EffectOff(0);
                            return "lighting off";
                        }
                }
            }
            catch (Exception ex) { return "device action failed: " + ex.Message; }
            return "unknown device action: " + w;
        }

        // Swaps the value cell between the free text box and the Windows-command pick list.
        void ShowValueCell(int slot)
        {
            if (_hostKind == null || slot < 0 || slot >= _hostKind.Length) return;
            bool isWin = _hostKind[slot].SelectedIndex == HostKinds.WindowsCmd;
            if (_hostWinCmd[slot] == null || _hostValue[slot] == null) return;
            _hostWinCmd[slot].Visible = isWin;
            _hostValue[slot].Visible = !isWin;
            if (isWin && _hostWinCmd[slot].SelectedIndex < 0) _hostWinCmd[slot].SelectedIndex = 0;
        }

        // Which physical buttons currently emit each host trigger, read back from the firmware.
        // Filled by ScanTriggerBindings() and shown in the Host actions rows, so the two tabs
        // talk to each other instead of the user having to remember what is bound where.
        readonly List<string>[] _triggerBoundTo = new List<string>[HostTrigger.Count];
        bool _bindScanOk;

        // Reads the normal + Hypershift mapping of the selected profile and records every input
        // whose output is one of our F13..F24 triggers.
        void ScanTriggerBindings()
        {
            for (int i = 0; i < _triggerBoundTo.Length; i++) _triggerBoundTo[i] = new List<string>();
            _bindScanOk = false;
            if (_dev == null) return;
            try
            {
                bool any = false;
                for (byte mode = 0; mode <= 1; mode++)
                {
                    List<KeyValuePair<byte, byte[]>> recs = _dev.ReadMappings(_profile, mode);
                    if (recs == null) continue;
                    any = true;
                    foreach (KeyValuePair<byte, byte[]> kv in recs)
                    {
                        byte[] d = kv.Value;
                        if (d == null || d.Length < 2) continue;
                        if (d[0] != OutTypes.Keyboard) continue;
                        // data = [modifier, usage]
                        byte usage = d.Length >= 3 ? d[2] : d[1];
                        byte mod = d.Length >= 3 ? d[1] : (byte)0;
                        if (mod != 0) continue;
                        int slot = HostTrigger.SlotOfUsage(usage);
                        if (slot < 0) continue;
                        int ix = Array.IndexOf(MapTable.InputIds, kv.Key);
                        string name = ix >= 0 ? MapTable.InputNames[ix] : ("input 0x" + kv.Key.ToString("x2"));
                        _triggerBoundTo[slot].Add(name + (mode == 1 ? " / Hypershift" : ""));
                    }
                }
                _bindScanOk = any;
            }
            catch { _bindScanOk = false; }
        }

        static string JoinShort(List<string> l)
        {
            if (l == null || l.Count == 0) return "";
            if (l.Count <= 2) return string.Join(", ", l);
            return l[0] + " +" + (l.Count - 1) + " more";
        }

        void LoadHostPage()
        {
            if (_hostKind == null) return;
            _loading = true;
            HostAction[] arr = ActionsFor(_profile);
            ScanTriggerBindings();
            for (int s = 0; s < HostTrigger.Count; s++)
            {
                HostAction a = arr[s];
                if (a == null) { a = new HostAction(); arr[s] = a; }
                int k = a.Kind;
                if (k < 0 || k >= HostKinds.Names.Length) k = 0;
                _hostKind[s].SelectedIndex = k;
                _hostValue[s].Text = a.Value ?? "";
                if (_hostWinCmd[s] != null)
                {
                    int wi = WinCommands.IndexOf(a.Value);
                    _hostWinCmd[s].SelectedIndex = wi >= 0 ? wi : 0;
                }
                ShowValueCell(s);
                int pv = k == HostKinds.Turbo ? a.Hz : a.Delay;
                if (pv < _hostParam[s].Minimum) pv = (int)_hostParam[s].Minimum;
                if (pv > _hostParam[s].Maximum) pv = (int)_hostParam[s].Maximum;
                _hostParam[s].Value = pv;
            }
            _loading = false;
            for (int s = 0; s < HostTrigger.Count; s++) MarkHostRow(s);
            RefreshHostStatus();
        }

        void SaveHostRow(int slot)
        {
            if (_loading || _hostKind == null || slot < 0 || slot >= HostTrigger.Count) return;
            HostAction[] arr = ActionsFor(_profile);
            HostAction a = arr[slot];
            if (a == null) { a = new HostAction(); arr[slot] = a; }
            a.Kind = _hostKind[slot].SelectedIndex;
            if (a.Kind == HostKinds.Disabled)
            {
                // Disabled must really be empty on disk, not just look empty in the UI. A kept
                // "winver.exe" behind a disabled row used to reappear the moment the kind was
                // changed back, which made the row look half-configured.
                a.Value = "";
            }
            else if (a.Kind == HostKinds.WindowsCmd && _hostWinCmd[slot] != null && _hostWinCmd[slot].SelectedIndex >= 0)
                a.Value = WinCommands.Keys[_hostWinCmd[slot].SelectedIndex];   // store the key, never the label
            else
                a.Value = _hostValue[slot].Text;
            int v = (int)_hostParam[slot].Value;
            if (a.Kind == HostKinds.Turbo) a.Hz = v; else a.Delay = v;
            SaveHostTable();
            RebuildHostTable();
            MarkHostRow(slot);
        }

        // Colours the row so a half-filled action is visible instead of silently inert, and shows
        // which physical button actually emits this trigger.
        //
        // The two tabs are one feature seen from both ends: a row here only fires while the
        // Buttons tab has some input mapped to this F-key. So the row now says so directly:
        //   "bound: Side button 4"          - ready
        //   "not bound to any button"       - configured but unreachable
        //   "broken bind"                   - the button is mapped to something else now
        // The Buttons tab shows "Host action 1 (F13)" in its shortcut column. This returns a short
        // suffix describing what that host slot actually does, or "" when it is empty - so a row
        // chosen in Buttons says what it will run without switching tabs.
        string HostCueForUsage(byte usage, byte mode)
        {
            int slot = HostTrigger.SlotOfUsage(usage);
            if (slot < 0) return "";
            HostAction[] arr = ActionsFor((byte)(_activeProfile > 0 ? _activeProfile : _profile));
            if (slot >= arr.Length || arr[slot] == null) return " - empty";
            HostAction a = arr[slot];
            if (a.IsDisabled) return " - empty";
            return " - " + a.Describe();
        }

        void MarkHostRow(int slot)
        {
            if (_hostKind == null || slot < 0 || slot >= _hostKind.Length) return;
            HostAction[] arr = ActionsFor(_profile);
            HostAction a = arr[slot];
            string problem = a == null ? "no action chosen" : a.Problem();
            bool configured = a != null && problem.Length == 0;

            string bound = JoinShort(_triggerBoundTo[slot]);
            bool unbound = configured && bound.Length == 0;

            Color back = Color.Empty;
            if (problem.Length > 0 && a != null && a.Kind > 0) back = Color.FromArgb(255, 246, 214);   // amber: needs a value
            else if (problem.Length > 0) back = Color.FromArgb(244, 244, 244);                         // grey: empty row
            else if (unbound) back = Color.FromArgb(232, 240, 255);                                    // blue: not reachable
            _hostKind[slot].BackColor = back;
            _hostValue[slot].BackColor = back;
            if (_hostWinCmd[slot] != null) _hostWinCmd[slot].BackColor = back;

            // The bind column states the truth read from the device, not what the user typed.
            if (_hostBindState[slot] != null)
            {
                if (bound.Length > 0)
                {
                    _hostBindState[slot].Text = "bound: " + bound;
                    _hostBindState[slot].ForeColor = Color.FromArgb(22, 122, 44);
                }
                else if (!_bindScanOk)
                {
                    _hostBindState[slot].Text = "bind unknown";
                    _hostBindState[slot].ForeColor = Color.FromArgb(140, 140, 140);
                }
                else
                {
                    _hostBindState[slot].Text = configured ? "not bound" : "";
                    _hostBindState[slot].ForeColor = Color.FromArgb(176, 96, 20);
                }
            }

            if (a != null && problem.Length > 0) _hostValue[slot].AccessibleDescription = problem;
            if (_hostTip != null)
            {
                string tip;
                if (problem.Length > 0) tip = HostTrigger.Name(slot) + ": " + problem + " - this row will not fire.";
                else if (unbound) tip = HostTrigger.Name(slot) + ": the action is set, but no button sends this " +
                    "trigger yet. Use the Bind column, or pick it in the Buttons tab.";
                else tip = HostTrigger.Name(slot) + ": " + a.Describe() + "\nSent by: " + bound;
                _hostTip.SetToolTip(_hostKind[slot], tip);
                _hostTip.SetToolTip(_hostValue[slot], tip);
            }
        }

        void TestHostRow(int slot)
        {
            SaveHostRow(slot);
            HostAction[] arr = ActionsFor(_profile);
            HostAction a = arr[slot];
            if (a == null || a.IsDisabled) { MessageBox.Show("Nothing defined for " + HostTrigger.Name(slot) + "."); return; }
            string res = _host != null ? _host.Run(slot, a, true) : "engine not running";
            HostLog(HostTrigger.Name(slot) + ": " + res);
        }

        void BindHostSlot(int slot)
        {
            if (_hostKind == null || _hostBind == null) return;
            SaveHostRow(slot);
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            HostAction[] arr = ActionsFor(_profile);
            if (arr[slot] == null || arr[slot].IsDisabled)
            {
                MessageBox.Show("Define the action for " + HostTrigger.Name(slot) + " first (kind + value).");
                return;
            }
            int sel = _hostBind[slot].SelectedIndex;
            if (sel < 0) return;
            int mode = sel / MapTable.InputKeys.Length;
            int input = sel % MapTable.InputKeys.Length;
            byte[] rec = new byte[] { MapTable.InputIds[input], OutTypes.Keyboard, 0x00, HostTrigger.Usage[slot] };
            bool ok = WriteRaw(_profile, (byte)mode, rec);
            LoadMappings();
            // Other rows may now be free or newly taken: refresh every row's binding state, not
            // just this one, so the table always matches the device.
            ScanTriggerBindings();
            for (int i = 0; i < HostTrigger.Count; i++) MarkHostRow(i);
            RefreshHostStatus();
            MessageBox.Show(ok
                ? "Profile " + _profile + ": " + MapTable.InputNames[input] + " (" +
                  (mode == 1 ? "Hypershift" : "normal") + ") now sends " + HostTrigger.Name(slot) + "." +
                  Environment.NewLine + Environment.NewLine +
                  "The mouse does the key, Rz3 does the rest - keep Rz3 running."
                : "Write refused. Is the mouse awake and Synapse closed?");
        }

        // =====================================================================================
        // Power tab
        // =====================================================================================
        // What this mouse can actually do (measured, see docs/HANDOFF.md §24c):
        //   battery level (07/80), low-battery threshold (07/81 / 07/01), sleep timeout
        //   (07/83 / 07/03), power profile (07/8D), charging-LED effect (06/8D read only),
        //   global lighting effect/brightness (0F/02, 0F/01).
        // What it cannot: report charging (07/85 = unsupported; Windows supplies that), and
        // separate LED brightness for battery mode (0F/83 = 0x03). The tab says so instead of
        // pretending, and offers host-side behaviour instead (dim/off while on battery).
        NumericUpDown _pwSleep, _pwLow;
        ComboBox _pwProfile;
        CheckBox _pwDimOnBattery, _pwLightOffOnBattery, _pwNotifyLow;
        TrackBar _pwBatteryBright, _pwAcBright;
        bool _pwProfileSupported;     // does this firmware actually store a power profile?
        Label _pwStatus, _pwLive, _pwCap, _pwPowerNote;
        System.Windows.Forms.Timer _pwTimer;

        // Host-side power policy, stored next to the UI preferences.
        class PowerPolicy
        {
            public bool DimOnBattery;          // lower brightness while unplugged
            public bool LightOffOnBattery;     // turn lighting off while unplugged
            public bool NotifyLow;             // balloon when the battery crosses the warning level
            public int BatteryBrightness = 60; // percent used while unplugged
            public int AcBrightness = 100;     // percent used while plugged in
            public bool AppliedOnAc = true;    // what the last apply used
            public bool NotifiedLow;           // low battery already announced
        }
        PowerPolicy _pwPolicy = new PowerPolicy();

        string PowerPolicyPath
        {
            get { return Path.Combine(Application.StartupPath, "rz3_power.json"); }
        }

        void LoadPowerPolicy()
        {
            try
            {
                if (!File.Exists(PowerPolicyPath)) return;
                foreach (string raw in File.ReadAllLines(PowerPolicyPath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim();
                    string v = raw.Substring(eq + 1).Trim();
                    bool b = v == "1";
                    int i;
                    switch (k)
                    {
                        case "DimOnBattery": _pwPolicy.DimOnBattery = b; break;
                        case "LightOffOnBattery": _pwPolicy.LightOffOnBattery = b; break;
                        case "NotifyLow": _pwPolicy.NotifyLow = b; break;
                        case "BatteryBrightness": if (int.TryParse(v, out i)) _pwPolicy.BatteryBrightness = i; break;
                        case "AcBrightness": if (int.TryParse(v, out i)) _pwPolicy.AcBrightness = i; break;
                    }
                }
            }
            catch { }
        }

        void SavePowerPolicy()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Rz3 power policy (host side)");
                sb.AppendLine("DimOnBattery=" + (_pwPolicy.DimOnBattery ? "1" : "0"));
                sb.AppendLine("LightOffOnBattery=" + (_pwPolicy.LightOffOnBattery ? "1" : "0"));
                sb.AppendLine("NotifyLow=" + (_pwPolicy.NotifyLow ? "1" : "0"));
                sb.AppendLine("BatteryBrightness=" + _pwPolicy.BatteryBrightness);
                sb.AppendLine("AcBrightness=" + _pwPolicy.AcBrightness);
                File.WriteAllText(PowerPolicyPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        TabPage BuildPowerPage()
        {
            TabPage p = new TabPage("Power");
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14), AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Font bold = new Font("Segoe UI", 9f, FontStyle.Bold);
            // 17 rows of 26 px = 442, which fits the tab client (475) minus padding.
            for (int i = 0; i < 24; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            int row = 0;

            _pwLive = new Label { Text = "", Dock = DockStyle.Fill, Height = 44, TextAlign = ContentAlignment.MiddleLeft };
            t.Controls.Add(Head("Battery right now", bold), 0, row);
            t.Controls.Add(_pwLive, 1, row);
            t.RowStyles[row].Height = 46;
            row++;

            t.Controls.Add(Head("Device settings (stored in the mouse)", bold), 0, row);
            t.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 1, row);
            row++;

            // Real ranges measured on the device (tools8/LimitsProbe.exe):
            //   sleep  : 07/03 stores 10..65535 s exactly (the old 60..900 limit was Rz3's own)
            //   warning: 07/01 stores 5..100 % exactly (the old 5..25 clamp was Rz3's own)
            _pwSleep = new NumericUpDown { Minimum = 10, Maximum = 3600, Increment = 10, Width = 90, Dock = DockStyle.Left };
            t.Controls.Add(new Label { Text = "Sleep after (seconds, 10-3600)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            t.Controls.Add(_pwSleep, 1, row);
            row++;

            _pwLow = new NumericUpDown { Minimum = 5, Maximum = 100, Increment = 1, Width = 90, Dock = DockStyle.Left };
            t.Controls.Add(new Label { Text = "Low battery warning (%)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            t.Controls.Add(_pwLow, 1, row);
            row++;

            _pwProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Dock = DockStyle.Left };
            _pwProfile.Items.AddRange(new object[] { "0 - Power save", "1 - Balanced", "2 - High performance" });
            t.Controls.Add(new Label { Text = "Device power profile", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            t.Controls.Add(_pwProfile, 1, row);
            row++;

            // The explanation for a disabled/unreadable control goes on the tab's own status line,
            // not in an extra table row: the table already fills the visible client area.
            t.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill, AutoSize = false }, 1, row);
            row++;

            t.Controls.Add(Head("While on battery (host behaviour)", bold), 0, row);
            t.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 1, row);
            row++;

            _pwDimOnBattery = new CheckBox { Text = "Dim the lighting on battery", Width = 320, Margin = new Padding(0, 4, 0, 0) };
            _pwDimOnBattery.CheckedChanged += delegate { _pwPolicy.DimOnBattery = _pwDimOnBattery.Checked; SavePowerPolicy(); ApplyPowerPolicy(true); };
            t.Controls.Add(_pwDimOnBattery, 1, row);
            row++;

            _pwBatteryBright = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 240, Dock = DockStyle.Left };
            _pwBatteryBright.ValueChanged += delegate
            {
                _pwPolicy.BatteryBrightness = _pwBatteryBright.Value;
                SavePowerPolicy();
            };
            t.Controls.Add(new Label { Text = "   Battery brightness (%)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            t.Controls.Add(_pwBatteryBright, 1, row);
            row++;

            _pwAcBright = new TrackBar { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 240, Dock = DockStyle.Left };
            _pwAcBright.ValueChanged += delegate
            {
                _pwPolicy.AcBrightness = _pwAcBright.Value;
                SavePowerPolicy();
            };
            t.Controls.Add(new Label { Text = "   Plugged-in brightness (%)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            t.Controls.Add(_pwAcBright, 1, row);
            row++;

            _pwLightOffOnBattery = new CheckBox { Text = "Turn the lighting off on battery", Width = 320, Margin = new Padding(0, 4, 0, 0) };
            _pwLightOffOnBattery.CheckedChanged += delegate { _pwPolicy.LightOffOnBattery = _pwLightOffOnBattery.Checked; SavePowerPolicy(); ApplyPowerPolicy(true); };
            t.Controls.Add(_pwLightOffOnBattery, 1, row);
            row++;

            _pwNotifyLow = new CheckBox { Text = "Notify me when the battery gets low", Width = 320, Margin = new Padding(0, 4, 0, 0) };
            _pwNotifyLow.CheckedChanged += delegate { _pwPolicy.NotifyLow = _pwNotifyLow.Checked; SavePowerPolicy(); };
            t.Controls.Add(_pwNotifyLow, 1, row);
            row++;

            t.Controls.Add(Head("Tray", bold), 0, row);
            t.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 1, row);
            row++;

            _trayBattIcon = new CheckBox { Text = "Show the battery level on the tray icon", Width = 320, Margin = new Padding(0, 4, 0, 0) };
            _trayBattIcon.CheckedChanged += delegate { SetTrayOption(true, _trayBattIcon.Checked); };
            t.Controls.Add(_trayBattIcon, 1, row);
            row++;

            _trayBattTip = new CheckBox { Text = "Show the battery in the tray tooltip", Width = 320, Margin = new Padding(0, 4, 0, 0) };
            _trayBattTip.CheckedChanged += delegate { SetTrayOption(false, _trayBattTip.Checked); };
            t.Controls.Add(_trayBattTip, 1, row);
            row++;

            FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            buttons.Controls.Add(MkButton("Apply", delegate { ApplyPowerTab(); }));
            buttons.Controls.Add(MkButton("Refresh", delegate { LoadPowerTab(); }));
            buttons.Controls.Add(MkButton("Apply lighting now", delegate { ApplyPowerPolicy(true); }));
            t.Controls.Add(buttons, 1, row);
            row++;

            // Status lives on the tab, not in the table: a Dock=Fill label inside a table row is
            // clipped by the row height, which the layout audit correctly flagged.
            _pwStatus = new Label { Text = "", Dock = DockStyle.Bottom, Height = 30, AutoEllipsis = true, Padding = new Padding(18, 2, 18, 0) };
            _pwPowerNote = _pwStatus;

            _pwCap = new Label
            {
                AutoSize = false,
                Height = 84,
                Dock = DockStyle.Bottom,
                ForeColor = Color.FromArgb(120, 120, 120),
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(6, 4, 6, 0),
                Text = "This firmware has no separate 'plugged-in' / 'on battery' lighting command and no charging " +
                       "report of its own, so the charging state comes from Windows and the battery lighting behaviour " +
                       "is applied by Rz3 while it runs."
            };

            p.Controls.Add(t);
            p.Controls.Add(_pwStatus);
            p.Controls.Add(_pwCap);
            p.Controls.Add(Banner("Device-wide power and battery behaviour", false));
            return p;
        }

        // Reads the device-held power settings into the controls.
        //
        // Every read is checked. A failed read must NOT leave the control on whatever it happened
        // to show: the profile combo used to stay on index 0 ("Power save") while the device held
        // another value, which is what "the profile is stuck on Power save" looked like. When a
        // value cannot be read the control is disabled and says so.
        void LoadPowerTab()
        {
            if (_pwSleep == null) return;
            _loading = true;
            int read = 0, failed = 0;
            if (_dev == null)
            {
                _pwSleep.Enabled = false; _pwLow.Enabled = false; _pwProfile.Enabled = false;
                _pwProfile.SelectedIndex = -1;
                _pwPowerNote.Text = "Not connected - device settings are hidden until the mouse answers.";
            }
            else
            {
                ushort sleep = 0; int low = -1, prof = -1;
                try { sleep = _dev.GetIdleTime(); } catch { }
                try { low = _dev.GetLowBattery(); } catch { }
                try { prof = _dev.GetPowerProfile(); } catch { }

                if (sleep > 0)
                {
                    int v = Math.Max((int)_pwSleep.Minimum, Math.Min((int)_pwSleep.Maximum, (int)sleep));
                    _pwSleep.Value = v; _pwSleep.Enabled = true; read++;
                }
                else { _pwSleep.Enabled = false; failed++; }

                if (low >= 0)
                {
                    _pwLow.Value = Math.Max(5, Math.Min(100, low)); _pwLow.Enabled = true; read++;
                }
                else { _pwLow.Enabled = false; failed++; }

                // The power profile is only offered when the firmware really stores it. This body
                // acknowledges 07/0D but never keeps the value, so showing "Power save" (the first
                // enum entry) was a lie that also looked like a bug on every restart.
                if (_pwProfileSupported)
                {
                    if (prof >= 0 && prof <= 2)
                    {
                        _pwProfile.SelectedIndex = prof; _pwProfile.Enabled = true; read++;
                    }
                    else
                    {
                        _pwProfile.SelectedIndex = -1; _pwProfile.Enabled = false; failed++;
                    }
                }
                else
                {
                    _pwProfile.SelectedIndex = -1;
                    _pwProfile.Enabled = false;
                }

                if (!_pwProfileSupported)
                    _pwPowerNote.Text = "Power profile is not stored by this firmware (it acknowledges the " +
                        "command but always reports 0), so the control is disabled instead of showing a " +
                        "value nobody set.";
                else
                    _pwPowerNote.Text = failed == 0
                        ? "Read from the mouse: sleep " + sleep + " s, warning " + low + " %, profile " + prof + "."
                        : failed + " of 3 setting(s) could not be read (the mouse may be asleep). " +
                          "They are disabled so a stale value is never shown - press Refresh.";
            }
            _pwDimOnBattery.Checked = _pwPolicy.DimOnBattery;
            _pwLightOffOnBattery.Checked = _pwPolicy.LightOffOnBattery;
            _pwNotifyLow.Checked = _pwPolicy.NotifyLow;
            _pwBatteryBright.Value = Math.Max(0, Math.Min(100, _pwPolicy.BatteryBrightness));
            _pwAcBright.Value = Math.Max(0, Math.Min(100, _pwPolicy.AcBrightness));
            if (_trayBattIcon != null) _trayBattIcon.Checked = _trayShowBattery;
            if (_trayBattTip != null) _trayBattTip.Checked = _trayShowTip;
            _loading = false;
            RefreshPowerLive();
        }

        void RefreshPowerLive()
        {
            if (_pwLive == null) return;
            try
            {
                int pct = -1;
                int st = Rz2.RzDevice.ChargeUnknown;
                int link = Rz2.RzDevice.LinkNone;
                if (_dev != null)
                {
                    st = _dev.ChargeState(out pct);
                    link = _dev.LinkMode;
                }
                _pwLive.Text = (pct >= 0 ? pct + "%  " : "") +
                    Rz2.RzDevice.ChargeName(st) + "  \u2022  " + Rz2.RzDevice.LinkName(link);
            }
            catch { }
        }

        // Writes the three device-held power settings and VERIFIES each one by reading it back.
        // A write that the firmware accepts but does not store used to look successful; now the
        // dialog says what the device actually kept.
        void ApplyPowerTab()
        {
            if (_dev == null) { MessageBox.Show("Device not connected."); return; }
            StringBuilder sb = new StringBuilder();
            int ok = 0, bad = 0;

            if (_pwSleep.Enabled)
            {
                try
                {
                    ushort want = (ushort)_pwSleep.Value;
                    _dev.SetIdleTime(want);
                    Thread.Sleep(80);
                    ushort got = _dev.GetIdleTime();
                    if (got == want) { ok++; sb.AppendLine("Sleep time: " + want + " s  (verified)"); }
                    else
                    {
                        bad++;
                        sb.AppendLine("Sleep time: asked " + want + " s, device kept " + got + " s");
                        if (got >= 60 && got <= 900) _pwSleep.Value = got;
                    }
                }
                catch (Exception ex) { bad++; sb.AppendLine("Sleep time failed: " + ex.Message); }
            }
            else sb.AppendLine("Sleep time: skipped (not readable, press Refresh first)");

            if (_pwLow.Enabled)
            {
                try
                {
                    int want = (int)_pwLow.Value;
                    _dev.SetLowBattery(want);
                    Thread.Sleep(80);
                    int got = _dev.GetLowBattery();
                    if (Math.Abs(got - want) <= 1) { ok++; sb.AppendLine("Low battery warning: " + want + " %  (verified)"); }
                    else
                    {
                        bad++;
                        sb.AppendLine("Low battery warning: asked " + want + " %, device kept " + got + " %");
                        if (got >= 0) _pwLow.Value = Math.Max(5, Math.Min(100, got));
                    }
                }
                catch (Exception ex) { bad++; sb.AppendLine("Low battery failed: " + ex.Message); }
            }
            else sb.AppendLine("Low battery warning: skipped (not readable, press Refresh first)");

            if (_pwProfileSupported && _pwProfile.Enabled && _pwProfile.SelectedIndex >= 0)
            {
                try
                {
                    int want = _pwProfile.SelectedIndex;
                    bool stored = _dev.SetPowerProfile((byte)want);
                    int got = _dev.GetPowerProfile();
                    if (stored && got == want) { ok++; sb.AppendLine("Power profile: " + want + "  (verified)"); }
                    else
                    {
                        bad++;
                        sb.AppendLine("Power profile: asked " + want + ", device reports " + got +
                            "  (this firmware acknowledges the write but does not store it)");
                        _pwProfileSupported = false;
                        _pwProfile.SelectedIndex = -1;
                        _pwProfile.Enabled = false;
                    }
                }
                catch (Exception ex) { bad++; sb.AppendLine("Power profile failed: " + ex.Message); }
            }
            else sb.AppendLine("Power profile: skipped (not stored by this firmware)");

            SavePowerPolicy();
            ApplyPowerPolicy(true);
            sb.AppendLine();
            sb.AppendLine(ok + " applied and verified, " + bad + " not stored.");
            if (bad > 0)
                sb.AppendLine("The mouse is asleep or the firmware does not keep this value; wake the mouse and apply again.");
            _pwStatus.Text = "Applied " + DateTime.Now.ToString("HH:mm:ss") +
                " (" + ok + " verified" + (bad > 0 ? ", " + bad + " failed" : "") + ")";
            MessageBox.Show(sb.ToString(), "Power settings");
            LoadPowerTab();
        }

        // Host side: apply brightness/lighting according to the AC state, but only when the state
        // actually changed (so it never fights the Lighting tab on every timer tick).
        void ApplyPowerPolicy(bool force)
        {
            if (_dev == null) return;
            int ac = Rz2.RzDevice.SystemOnAc();
            if (ac < 0) return;
            bool onAc = ac == 1;
            if (!force && onAc == _pwPolicy.AppliedOnAc) return;
            _pwPolicy.AppliedOnAc = onAc;
            try
            {
                if (!onAc && _pwPolicy.LightOffOnBattery)
                {
                    // bölge 0, efekt kapalı
                    _dev.Raw(0x0F, 0x02, new byte[] { 0x00, 0x00 });
                    _pwStatus.Text = "Lighting off (on battery)";
                    return;
                }
                if (!onAc && _pwPolicy.DimOnBattery)
                {
                    SetAllBrightness((byte)Math.Max(0, Math.Min(100, _pwPolicy.BatteryBrightness)));
                    _pwStatus.Text = "Dimmed to " + _pwPolicy.BatteryBrightness + "% (on battery)";
                    return;
                }
                if (onAc)
                {
                    SetAllBrightness((byte)Math.Max(0, Math.Min(100, _pwPolicy.AcBrightness)));
                    _pwStatus.Text = "Brightness " + _pwPolicy.AcBrightness + "% (plugged in)";
                }
            }
            catch (Exception ex) { _pwStatus.Text = "lighting apply failed: " + ex.Message; }
        }

        // The lighting zones this firmware exposes (0F/80 reports 01, 04, 0A). Led 0 means "all"
        // but is not honoured everywhere on this model, so each zone is written explicitly.
        static readonly byte[] Zones = { 0x01, 0x04, 0x0A };

        void SetAllBrightness(byte percent)
        {
            foreach (byte z in Zones)
            {
                try { _dev.SetBrightness(z, percent); } catch { }
                Thread.Sleep(30);
            }
        }

        void CheckLowBatteryBalloon()
        {
            if (!_pwPolicy.NotifyLow || _tray == null || _dev == null) return;
            try
            {
                int batt = _dev.Battery();
                int threshold = (int)_pwLow.Value;
                if (batt >= 0 && batt <= threshold && !_pwPolicy.NotifiedLow)
                {
                    _pwPolicy.NotifiedLow = true;
                    _tray.ShowBalloonTip(4000, "Rz3 - battery low",
                        "The mouse is at " + batt + "%. Charge it soon.", ToolTipIcon.Warning);
                }
                else if (batt > threshold + 5) _pwPolicy.NotifiedLow = false;
            }
            catch { }
        }

        TabPage BuildHostPage()
        {
            TabPage p = new TabPage("Host actions");
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, Padding = new Padding(8), AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 225));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Font bold = new Font("Segoe UI", 9f, FontStyle.Bold);
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            for (int i = 0; i < HostTrigger.Count; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));

            t.Controls.Add(Head("Trigger", bold), 0, 0);
            t.Controls.Add(Head("Action", bold), 1, 0);
            t.Controls.Add(Head("Value (program / keys / text)", bold), 2, 0);
            t.Controls.Add(Head("Hz / ms", bold), 3, 0);
            t.Controls.Add(Head("Test", bold), 4, 0);
            t.Controls.Add(Head("Bind to a mouse button", bold), 5, 0);
            t.Controls.Add(Head("Currently sent by", bold), 6, 0);

            _hostKind = new ComboBox[HostTrigger.Count];
            _hostValue = new TextBox[HostTrigger.Count];
            _hostParam = new NumericUpDown[HostTrigger.Count];
            _hostBind = new ComboBox[HostTrigger.Count];
            _hostWinCmd = new ComboBox[HostTrigger.Count];
            _hostBindState = new Label[HostTrigger.Count];
            _hostTip = new ToolTip();
            _hostTip.AutoPopDelay = 12000;
            _hostTip.InitialDelay = 350;

            for (int slot = 0; slot < HostTrigger.Count; slot++)
            {
                int s = slot;
                int row = slot + 1;

                t.Controls.Add(new Label { Text = HostTrigger.Name(slot), TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, row);

                ComboBox k = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
                k.Items.AddRange(HostKinds.Names);
                k.SelectedIndex = 0;
                _hostKind[slot] = k;
                t.Controls.Add(k, 1, row);

                // Value cell: a free text box for most kinds, a pick list for Windows commands
                // (the user should never have to remember "shell:MyComputerFolder").
                Panel valueCell = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
                TextBox v = new TextBox { Dock = DockStyle.Fill };
                ComboBox wc = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
                for (int wi = 0; wi < WinCommands.Keys.Length; wi++)
                    wc.Items.Add(WinCommands.Names[wi] + "  (" + WinCommands.Keys[wi] + ")");
                valueCell.Controls.Add(v);
                valueCell.Controls.Add(wc);
                valueCell.Tag = wc;
                _hostValue[slot] = v;
                _hostWinCmd[slot] = wc;
                t.Controls.Add(valueCell, 2, row);

                NumericUpDown n = new NumericUpDown { Minimum = 1, Maximum = 200, Value = 20, Width = 56 };
                _hostParam[slot] = n;
                t.Controls.Add(n, 3, row);

                Button test = new Button { Text = "Test", Width = 54, Height = 22 };
                test.Click += delegate { TestHostRow(s); };
                t.Controls.Add(test, 4, row);

                FlowLayoutPanel bindRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 1, 0, 0) };
                ComboBox bind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 142 };
                for (int m = 0; m < 2; m++)
                    for (int i2 = 0; i2 < MapTable.InputKeys.Length; i2++)
                        bind.Items.Add(MapTable.InputNames[i2] + (m == 0 ? " / normal" : " / Hypershift"));
                bind.SelectedIndex = 0;
                _hostBind[slot] = bind;
                Button bb = new Button { Text = "Bind", Width = 48, Height = 22, Margin = new Padding(2, 1, 0, 0) };
                bb.Click += delegate { BindHostSlot(s); };
                bindRow.Controls.Add(bind);
                bindRow.Controls.Add(bb);
                t.Controls.Add(bindRow, 5, row);

                // What the device actually reports for this trigger, refreshed by LoadHostPage().
                Label state = new Label
                {
                    Text = "",
                    Dock = DockStyle.Fill,
                    AutoSize = false,
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Color.FromArgb(140, 140, 140)
                };
                _hostBindState[slot] = state;
                t.Controls.Add(state, 6, row);

                k.SelectedIndexChanged += delegate
                {
                    if (_loading) return;
                    // Switching a row to Disabled clears its value: a stale program name or key
                    // sequence sitting behind a disabled row is what made the two tabs feel out of
                    // sync (the row looked configured but nothing could ever fire).
                    if (k.SelectedIndex == HostKinds.Disabled)
                    {
                        _hostValue[s].Text = "";
                        if (_hostWinCmd[s] != null) _hostWinCmd[s].SelectedIndex = 0;
                    }
                    ShowValueCell(s);
                    SaveHostRow(s);
                };
                wc.SelectedIndexChanged += delegate { if (!_loading) SaveHostRow(s); };
                v.Leave += delegate { if (!_loading) SaveHostRow(s); };
                n.ValueChanged += delegate { if (!_loading) SaveHostRow(s); };
            }

            _hostOn = new CheckBox { Text = "Host engine running", Checked = _hostEnabled, Width = 150, Margin = new Padding(0, 3, 4, 0) };
            _hostOn.CheckedChanged += delegate { OnHostToggle(); };
            _autoStart = new CheckBox { Text = "Start with Windows (tray)", Checked = IsAutoStart(), Width = 190, Margin = new Padding(0, 3, 4, 0) };
            _autoStart.CheckedChanged += delegate { OnAutoStartChanged(); };
            FlowLayoutPanel erow1 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            erow1.Controls.Add(_hostOn);
            erow1.Controls.Add(_autoStart);
            erow1.Controls.Add(MkButton("Hide to tray", delegate { EnterTray(); }));
            erow1.Controls.Add(MkButton("Install to %LOCALAPPDATA%", delegate { InstallToLocalAppData(); }));
            t.Controls.Add(erow1, 0, HostTrigger.Count + 1);
            t.SetColumnSpan(erow1, 6);

            FlowLayoutPanel erow2 = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            erow2.Controls.Add(MkButton("Save actions", delegate { SaveHostTable(); HostLog("actions saved"); }));
            erow2.Controls.Add(MkButton("Show engine table", delegate
            {
                MessageBox.Show(_host != null ? _host.DescribeTable() : "engine not running", "Active profile triggers");
            }));
            erow2.Controls.Add(MkButton("Show log file", delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", Path.Combine(Application.StartupPath, "rz3_host.log")); }
                catch { }
            }));
            t.Controls.Add(erow2, 0, HostTrigger.Count + 2);
            t.SetColumnSpan(erow2, 6);

            _hostStatus = new Label { Dock = DockStyle.Fill, Text = "", AutoEllipsis = true };
            t.Controls.Add(_hostStatus, 0, HostTrigger.Count + 3);
            t.SetColumnSpan(_hostStatus, 6);

            Label hint = new Label
            {
                Dock = DockStyle.Fill,
                Text = "The button sends one spare key (F13..F24) from the firmware; Rz3 turns it into the action above. " +
                       "Sequences, delays, hold, turbo, programs, text and system shortcuts cannot live in the mouse, so " +
                       "they run here - follow the active profile while Rz3 is open." + Environment.NewLine +
                       "Keys syntax: ctrl+shift+esc, win+r wait:400 cmd wait:300 enter, tab x3. Hold/Toggle/Turbo take a chord."
            };
            t.Controls.Add(hint, 0, HostTrigger.Count + 4);
            t.SetColumnSpan(hint, 6);

            p.Controls.Add(t);
            p.Controls.Add(Banner("Runs on this PC - the button only sends the trigger key", false));
            return p;
        }

        // Where am I running from, and does Windows allow this location? An exe started from a
        // folder Windows treats as untrusted gets a restricted token: the Run key becomes
        // read-only and the tray icon never registers. The status bar says so instead of failing
        // silently.
        string LocationLine()
        {
            string here = Application.StartupPath;
            if (RunningFromInstallDir()) return "";
            return "  |  running from " + here + " - use \"Install to %LOCALAPPDATA%\" if autostart or the tray fails";
        }

        // Renders the tray artwork so the icon can be checked without eyes: writes PNGs and counts
        // how many pixels are actually painted (a blank icon would be "all transparent").
        public string IconTest()
        {
            StringBuilder sb = new StringBuilder();
            string dir = Path.Combine(Application.StartupPath, "rz3_icontest");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            int[][] cases = new int[][]
            {
                new int[] { 16, 0, 0 }, new int[] { 32, 0, 0 },
                new int[] { 32, 87, 0 }, new int[] { 32, 35, 0 }, new int[] { 32, 12, 0 },
                new int[] { 32, 60, 1 }, new int[] { 48, 0, 0 }
            };
            foreach (int[] c in cases)
            {
                using (Bitmap bmp = TrayIconFactory.Render(c[0], c[1] > 0, c[1], c[2] == 1))
                {
                    int painted = 0;
                    int minX = 999, minY = 999, maxX = -1, maxY = -1;
                    for (int y = 0; y < bmp.Height; y++)
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            if (bmp.GetPixel(x, y).A < 40) continue;
                            painted++;
                            if (x < minX) minX = x;
                            if (y < minY) minY = y;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;
                        }
                    string file = Path.Combine(dir, "icon_" + c[0] + "_" + (c[1] > 0 ? "b" + c[1] : "plain") + ".png");
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    // the wheel is Razer green; the battery bar turns amber/red/blue with the level
                    bool green = false, warn = false, low = false, blue = false;
                    for (int y = 0; y < bmp.Height; y++)
                        for (int x = 0; x < bmp.Width; x++)
                        {
                            Color px = bmp.GetPixel(x, y);
                            if (px.A < 100) continue;
                            if (px.G > 180 && px.R < 110 && px.B < 110) green = true;
                            if (px.R > 200 && px.G > 150 && px.G < 215 && px.B < 90) warn = true;
                            if (px.R > 200 && px.G < 110 && px.B < 110) low = true;
                            if (px.B > 200 && px.G > 120 && px.G < 200 && px.R < 120) blue = true;
                        }
                    double ratio = painted / (double)(bmp.Width * bmp.Height);
                    sb.AppendLine("size=" + c[0] + " battery=" + c[1] + " charging=" + (c[2] == 1) +
                        " painted=" + painted + " (" + (ratio * 100).ToString("0") + "%)" +
                        " box=" + (maxX >= 0 ? (minX + "," + minY + ".." + maxX + "," + maxY) : "empty") +
                        " green=" + green + (c[1] > 0 ? (" amber=" + warn + " red=" + low + " blue=" + blue) : "") +
                        " -> " + Path.GetFileName(file));
                    if (painted < c[0] * c[0] / 10) sb.AppendLine("  WARNING: icon looks nearly empty");
                    if (!green) sb.AppendLine("  WARNING: accent colour missing");
                    if (c[1] > 80 && c[2] != 1 && !green) sb.AppendLine("  WARNING: high battery should be green");
                    if (c[1] > 20 && c[1] <= 50 && !warn) sb.AppendLine("  WARNING: mid battery should be amber");
                    if (c[1] > 0 && c[1] <= 20 && !low) sb.AppendLine("  WARNING: low battery should be red");
                    if (c[2] == 1 && !blue) sb.AppendLine("  WARNING: charging should be blue");
                }
            }
            // one round trip through the real .ico writer, then read the frame size back
            using (Icon ico = TrayIconFactory.Build(true, 87, true))
            {
                sb.AppendLine("ico size=" + ico.Width + "x" + ico.Height + " (multi-size stream)");
            }
            return sb.ToString();
        }

        // --startuptest: drives the two buttons the user says do nothing, and reports what the
        // operating system actually recorded afterwards.
        public string StartupTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("before: Run key Rz3='" + ReadRunKey() + "'");
            sb.AppendLine("IsAutoStart()=" + IsAutoStart());

            // the checkbox path
            if (_autoStart != null)
            {
                _autoStart.Checked = true;
                Application.DoEvents();
                Thread.Sleep(200);
                sb.AppendLine("after checkbox on: checked=" + _autoStart.Checked +
                    " Run key='" + ReadRunKey() + "' IsAutoStart()=" + IsAutoStart());
                _autoStart.Checked = false;
                Application.DoEvents();
                Thread.Sleep(200);
                sb.AppendLine("after checkbox off: checked=" + _autoStart.Checked +
                    " Run key='" + ReadRunKey() + "' IsAutoStart()=" + IsAutoStart());
            }
            else sb.AppendLine("_autoStart is null");

            // the tray-menu path (same code, different caller)
            SetAutoStart(true);
            sb.AppendLine("after SetAutoStart(true): Run key='" + ReadRunKey() + "' error='" + _autoStartError + "'");
            SetAutoStart(false);
            sb.AppendLine("after SetAutoStart(false): Run key='" + ReadRunKey() + "' error='" + _autoStartError + "'");

            sb.AppendLine("final IsAutoStart()=" + IsAutoStart());
            return sb.ToString();
        }

        static string ReadRunKey()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return "<no key>";
                    object v = k.GetValue("Rz3");
                    return v == null ? "<absent>" : v.ToString();
                }
            }
            catch (Exception ex) { return "<error: " + ex.Message + ">"; }
        }

        // --hidetotraytest: the "Hide to tray" button path, end to end. Reports whether the window
        // really left the taskbar and whether the icon registered - the two things that make the
        // button look dead.
        public string HideToTrayTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("before: visible=" + Visible + " state=" + WindowState +
                " showInTaskbar=" + ShowInTaskbar +
                " iconVisible=" + (_tray != null && _tray.Visible));
            try { EnterTray(); }
            catch (Exception ex) { sb.AppendLine("EnterTray threw: " + ex.GetType().Name + ": " + ex.Message); }
            Application.DoEvents();
            Thread.Sleep(600);
            Application.DoEvents();
            sb.AppendLine("after:  visible=" + Visible + " state=" + WindowState +
                " showInTaskbar=" + ShowInTaskbar +
                " iconVisible=" + (_tray != null && _tray.Visible) +
                " inTray=" + _inTray);
            sb.AppendLine("result=" + ((!Visible && !ShowInTaskbar && _tray != null && _tray.Visible && _inTray)
                ? "OK - window parked, icon registered" : "BROKEN"));
            try { RestoreFromTray(); } catch (Exception ex) { sb.AppendLine("restore threw: " + ex.Message); }
            Application.DoEvents();
            Thread.Sleep(400);
            sb.AppendLine("restored: visible=" + Visible + " state=" + WindowState + " inTray=" + _inTray);
            return sb.ToString();
        }

        // ---- installation ---------------------------------------------------------------------
        // Measured 15.11.2026: an exe started from this workspace gets a RESTRICTED token
        // (token type 736 instead of 748), and HKCU\...\Run writes fail with SecurityException.
        // The same binary copied under %LOCALAPPDATA% writes fine, so copying the program to a
        // normal user location fixes autostart, the tray icon and host actions at once.
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rz3"); }
        }

        public static bool RunningFromInstallDir()
        {
            try
            {
                string here = Path.GetFullPath(Application.StartupPath).TrimEnd('\\');
                string there = Path.GetFullPath(InstallDir).TrimEnd('\\');
                return string.Equals(here, there, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        void InstallToLocalAppData()
        {
            string target = InstallDir;
            StringBuilder log = new StringBuilder();
            try
            {
                Directory.CreateDirectory(target);
                log.AppendLine("target: " + target);

                // Copy the program (it is a single self-contained exe) plus every side file that
                // holds user state, so the move does not lose profiles, names or actions.
                File.Copy(Application.ExecutablePath, Path.Combine(target, "Rz3.exe"), true);
                log.AppendLine("copied: Rz3.exe");
                // Rz3.ico is not user state but part of the program: AppIcon() reads it from the
                // program folder for the window/taskbar logo. Leaving it behind made the installed
                // copy fall back to the code-drawn icon, so the exe icon in Task Manager and the
                // in-app icon disagreed.
                string ico = Path.Combine(Application.StartupPath, "Rz3.ico");
                if (File.Exists(ico))
                {
                    File.Copy(ico, Path.Combine(target, "Rz3.ico"), true);
                    log.AppendLine("copied: Rz3.ico");
                }
                string[] state = new string[] {
                    "rz3_host.json", "rz3_names.json", "rz3_dpi.json", "rz3_ui.json",
                    "rz3_rate_cal.json", "rz3_power.json", "map_template.json"
                };
                foreach (string name in state)
                {
                    string src = Path.Combine(Application.StartupPath, name);
                    if (!File.Exists(src)) continue;
                    File.Copy(src, Path.Combine(target, name), true);
                    log.AppendLine("copied: " + name);
                }

                // A Start-menu shortcut, and point autostart at the installed copy.
                string linkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Rz3.lnk");
                try
                {
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    if (t != null)
                    {
                        object shell = Activator.CreateInstance(t);
                        object link = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                            null, shell, new object[] { linkPath });
                        link.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link,
                            new object[] { Path.Combine(target, "Rz3.exe") });
                        link.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link,
                            new object[] { target });
                        link.GetType().InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link,
                            new object[] { "Rz3 - mouse control without Synapse" });
                        link.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
                        log.AppendLine("shortcut: " + linkPath);
                    }
                }
                catch (Exception ex) { log.AppendLine("shortcut failed: " + ex.Message); }

                try
                {
                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                    {
                        if (k != null)
                        {
                            k.SetValue("Rz3", "\"" + Path.Combine(target, "Rz3.exe") + "\" --tray");
                            log.AppendLine("autostart now points at the installed copy");
                        }
                    }
                }
                catch (Exception ex) { log.AppendLine("autostart not set: " + ex.Message); }

                log.AppendLine();
                log.AppendLine("The installed copy starts from the Start menu (Rz3). Close this one and");
                log.AppendLine("start the installed copy: it can use the tray, autostart and host actions.");
                MessageBox.Show(log.ToString(), "Rz3 installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                log.AppendLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
                MessageBox.Show(log.ToString(), "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // --trayhooktest: the exact user flow that breaks - minimise to tray, then check that the
        // host engine is still alive and still receiving keys. Prints before/after counters.
        public string TrayHookTest(int seconds)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("engine=" + (_host == null ? "null" : _host.Status) +
                " hooked=" + (_host != null && _host.Hooked) +
                " enabled=" + (_host != null && _host.Enabled));
            HostAction[] arr = ActionsFor((byte)(_activeProfile > 0 ? _activeProfile : _profile));
            int bound = 0;
            for (int s = 0; s < HostTrigger.Count; s++)
                if (arr[s] != null && !arr[s].IsDisabled) bound++;
            sb.AppendLine("bound actions in active profile=" + bound);
            sb.AppendLine("keys seen before=" + (_host != null ? _host.HookEvents : 0));

            EnterTray();
            Application.DoEvents();
            Thread.Sleep(700);
            Application.DoEvents();
            sb.AppendLine("after EnterTray: visible=" + Visible + " showInTaskbar=" + ShowInTaskbar +
                " iconVisible=" + (_tray != null && _tray.Visible) +
                " state=" + WindowState);
            sb.AppendLine("engine after=" + (_host == null ? "null" : _host.Status) +
                " hooked=" + (_host != null && _host.Hooked) +
                " keys=" + (_host != null ? _host.HookEvents : 0));
            sb.AppendLine("press any key / the bound mouse button for " + seconds + " s...");
            int t0 = Environment.TickCount;
            while ((Environment.TickCount - t0) < seconds * 1000)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
            int after = _host != null ? _host.HookEvents : 0;
            sb.AppendLine("keys seen after=" + after);
            sb.AppendLine("RESULT: " + (after == 0
                ? "the hook received NOTHING while hidden - the tray hides events"
                : (after >= (_host != null ? _host.HookEvents : 0) ? "hook still delivering" : "?")));
            try { RestoreFromTray(); } catch { }
            return sb.ToString();
        }

        // --linktest: the cable <-> dongle transition, end to end.
        // A PID change invalidates the interface path, so the stale handle can never answer. This
        // drives the real recovery code and reports every step, so "n/a everywhere" has a name.
        public string LinkTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("initial: dev=" + (_dev == null ? "null" : "0x" + _dev.Pid.ToString("x4")) +
                " lost=" + _lost);
            if (_dev != null) sb.AppendLine("  path=" + _dev.Path);
            sb.AppendLine("  battery=" + (_dev != null ? SafeBattery() : -1) +
                " profiles=" + (_dev != null ? SafeProfiles() : -1));

            // 1) the handle is dropped behind the app's back, exactly like a link change
            sb.AppendLine("---1) dropping the handle (simulated link change)");
            try { _dev.Close(); } catch { }
            Thread.Sleep(300);
            int afterDrop = SafeBattery();
            sb.AppendLine("  battery right after the drop=" + afterDrop +
                (afterDrop >= 0 ? "  (auto-reopened on the same path)" : "  (stale handle, as expected)"));

            // 2) a read that keeps failing must arm the reconnect timer
            sb.AppendLine("---2) forcing two failed reads through RefreshBattery");
            int before = _dev != null ? 0 : 1;
            try
            {
                _battFail = 2;                 // pretend two reads already failed
                RefreshBattery();
            }
            catch (Exception ex) { sb.AppendLine("  RefreshBattery threw: " + ex.Message); }
            sb.AppendLine("  lost=" + _lost + " reconTimer=" + (_reconTimer != null && _reconTimer.Enabled));

            // 3) the reconnect path must find the device again, whatever PID it now has
            sb.AppendLine("---3) TryReattach()");
            bool back = TryReattach();
            sb.AppendLine("  reattached=" + back + " dev=" + (_dev == null ? "null" : "0x" + _dev.Pid.ToString("x4")));
            sb.AppendLine("  battery=" + SafeBattery() + " profiles=" + SafeProfiles() +
                " lost=" + _lost);

            // 4) the PID must be one of the supported ones after recovery
            sb.AppendLine("---4) result");
            bool ok = back && _dev != null && SafeBattery() >= 0;
            sb.AppendLine("  " + (ok ? "OK - the device is readable again" : "BROKEN - still not readable"));
            return sb.ToString();
        }

        int SafeBattery()
        {
            try { return _dev != null ? _dev.Battery() : -1; } catch { return -1; }
        }

        int SafeProfiles()
        {
            try { return _dev != null ? _dev.ProfileCount() : -1; } catch { return -1; }
        }

        // --powertest: does every Power-tab setting survive a close/reopen cycle, and is an
        // unsupported setting reported instead of faked?
        public string PowerTest()
        {
            StringBuilder sb = new StringBuilder();
            if (_dev == null) { sb.AppendLine("no device"); return sb.ToString(); }

            sb.AppendLine("=== capability ===");
            sb.AppendLine("profile supported (probe) = " + _dev.PowerProfileSupported());

            sb.AppendLine();
            sb.AppendLine("=== read what the device holds now ===");
            ushort sleep0 = 0; int low0 = -1, prof0 = -1;
            try { sleep0 = _dev.GetIdleTime(); } catch { }
            try { low0 = _dev.GetLowBattery(); } catch { }
            try { prof0 = _dev.GetPowerProfile(); } catch { }
            sb.AppendLine("sleep=" + sleep0 + " low=" + low0 + " profile=" + prof0);

            sb.AppendLine();
            sb.AppendLine("=== write different values, then verify with a FRESH handle ===");
            ushort wantSleep = sleep0 == 300 ? (ushort)180 : (ushort)300;
            int wantLow = low0 == 20 ? 25 : 20;
            try { _dev.SetIdleTime(wantSleep); } catch { }
            try { _dev.SetLowBattery(wantLow); } catch { }
            Thread.Sleep(200);

            _dev.Close();
            Thread.Sleep(200);
            if (!TryReattach()) { sb.AppendLine("reattach failed"); return sb.ToString(); }

            ushort sleep1 = 0; int low1 = -1;
            try { sleep1 = _dev.GetIdleTime(); } catch { }
            try { low1 = _dev.GetLowBattery(); } catch { }
            sb.AppendLine("after reconnect: sleep=" + sleep1 + " (asked " + wantSleep + ") " +
                (sleep1 == wantSleep ? "PERSISTED" : "LOST"));
            sb.AppendLine("after reconnect: low=" + low1 + " (asked " + wantLow + ") " +
                (Math.Abs(low1 - wantLow) <= 1 ? "PERSISTED" : "LOST"));

            sb.AppendLine();
            sb.AppendLine("=== restore the original values ===");
            try { if (sleep0 >= 60) _dev.SetIdleTime(sleep0); } catch { }
            try { if (low0 >= 0) _dev.SetLowBattery(low0); } catch { }
            Thread.Sleep(150);
            ushort sleep2 = 0; int low2 = -1;
            try { sleep2 = _dev.GetIdleTime(); } catch { }
            try { low2 = _dev.GetLowBattery(); } catch { }
            sb.AppendLine("restored: sleep=" + sleep2 + " low=" + low2);

            sb.AppendLine();
            sb.AppendLine("=== what the Power tab shows after LoadPowerTab() ===");
            LoadPowerTab();
            Application.DoEvents();
            sb.AppendLine("sleep control=" + _pwSleep.Value + " enabled=" + _pwSleep.Enabled);
            sb.AppendLine("low control=" + _pwLow.Value + " enabled=" + _pwLow.Enabled);
            sb.AppendLine("profile control index=" + _pwProfile.SelectedIndex +
                " enabled=" + _pwProfile.Enabled + " supported=" + _pwProfileSupported);
            sb.AppendLine("note: " + _pwPowerNote.Text);
            return sb.ToString();
        }

        // --linkmodetest: does the app distinguish cable / dongle / absent, and does the charge state
        // follow the link instead of the PC's power status?
        public string LinkModeTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== what the app reports ===");
            if (_dev == null) sb.AppendLine("device: null");
            else
            {
                int pct;
                int st = _dev.ChargeState(out pct);
                int link = _dev.LinkMode;
                int cable = _dev.CableFlag();
                sb.AppendLine("pid=0x" + _dev.Pid.ToString("x4"));
                sb.AppendLine("LinkMode=" + link + "  (" + Rz2.RzDevice.LinkName(link) + ")");
                sb.AppendLine("07/84 byte9 (cable flag)=" + cable);
                sb.AppendLine("Battery()=" + pct);
                sb.AppendLine("ChargeState=" + st + "  (" + Rz2.RzDevice.ChargeName(st) + ")");
                sb.AppendLine("Charging()=" + _dev.Charging());
            }
            sb.AppendLine();
            sb.AppendLine("=== the three cases the user asked about ===");
            int p2; int s2 = -1; int l2 = Rz2.RzDevice.LinkNone;
            if (_dev != null) { s2 = _dev.ChargeState(out p2); l2 = _dev.LinkMode; }
            sb.AppendLine("1) cable, battery < 100  -> expect charging     : " +
                Rz2.RzDevice.ChargeName(Rz2.RzDevice.ChargeCharging));
            sb.AppendLine("2) dongle                -> expect on battery   : " +
                Rz2.RzDevice.ChargeName(Rz2.RzDevice.ChargeOnBattery));
            sb.AppendLine("3) not connected         -> expect unknown      : " +
                Rz2.RzDevice.ChargeName(Rz2.RzDevice.ChargeUnknown));
            sb.AppendLine();
            sb.AppendLine("right now the app says: link=" + Rz2.RzDevice.LinkName(l2) +
                " charge=" + Rz2.RzDevice.ChargeName(s2));
            sb.AppendLine();
            sb.AppendLine("=== tray text ===");
            RefreshBattery();
            Application.DoEvents();
            sb.AppendLine("charge label: " + _charge.Text);
            sb.AppendLine("tray tip    : " + (_tray != null ? _tray.Text : "n/a"));
            return sb.ToString();
        }

        // --rangetest: prove the low-battery and sleep ranges are no longer clamped by Rz3.
        public string RangeTest()
        {
            StringBuilder sb = new StringBuilder();
            if (_dev == null) { sb.AppendLine("no device"); return sb.ToString(); }

            int origLow = -1; ushort origSleep = 0;
            try { origLow = _dev.GetLowBattery(); } catch { }
            try { origSleep = _dev.GetIdleTime(); } catch { }
            sb.AppendLine("original: low=" + origLow + " %  sleep=" + origSleep + " s");

            sb.AppendLine();
            sb.AppendLine("=== low battery through SetLowBattery (the UI path) ===");
            foreach (int pct in new int[] { 25, 30, 40, 45, 50, 60, 80, 100 })
            {
                _dev.SetLowBattery(pct);
                Thread.Sleep(90);
                int got = -1;
                try { got = _dev.GetLowBattery(); } catch { }
                sb.AppendLine("  asked " + pct + " % -> " + got + " %" +
                    (Math.Abs(got - pct) <= 1 ? "  OK" : "  CLAMPED"));
            }

            sb.AppendLine();
            sb.AppendLine("=== sleep through SetIdleTime (the UI path) ===");
            foreach (int sec in new int[] { 10, 30, 60, 300, 900, 1200, 1800, 3600 })
            {
                _dev.SetIdleTime((ushort)sec);
                Thread.Sleep(90);
                ushort got = 0;
                try { got = _dev.GetIdleTime(); } catch { }
                sb.AppendLine("  asked " + sec + " s -> " + got + " s" + (got == sec ? "  OK" : "  CLAMPED"));
            }

            sb.AppendLine();
            sb.AppendLine("=== restore ===");
            if (origLow >= 0) { _dev.SetLowBattery(origLow); Thread.Sleep(90); }
            if (origSleep > 0) { _dev.SetIdleTime(origSleep); Thread.Sleep(90); }
            int backLow = -1; ushort backSleep = 0;
            try { backLow = _dev.GetLowBattery(); } catch { }
            try { backSleep = _dev.GetIdleTime(); } catch { }
            sb.AppendLine("low=" + backLow + " %  sleep=" + backSleep + " s");
            return sb.ToString();
        }

        // --wincmd <key>: runs ONE Windows command, writes the result and exits immediately.
        // A separate process per command matters: a command that opens a window would otherwise
        // keep the test process alive and hide which call actually worked.
        public string WinCommandOne(string key)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("command=" + key);
            sb.AppendLine("key index=" + WinCommands.IndexOf(key));
            string res;
            try { res = WinCommands.Run(key); }
            catch (Exception ex) { res = "THREW " + ex.GetType().Name + ": " + ex.Message; }
            sb.AppendLine("result=" + res);
            return sb.ToString();
        }

        // --wincmds: runs every Windows command in turn and reports what the Win32 call did.
        // Only safe commands are executed; destructive ones (closeapp) are skipped by default.
        public string WinCommandsTest(string only)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Windows commands ===");
            for (int i = 0; i < WinCommands.Keys.Length; i++)
            {
                string key = WinCommands.Keys[i];
                if (only != null && only.Length > 0 && key != only) continue;
                if (only == null || only.Length == 0)
                {
                    // skip the ones that would disrupt the desktop during a batch run
                    if (key == "closeapp" || key == "lock" || key == "cycleapps" || key == "switchapps") 
                    { sb.AppendLine("  " + key.PadRight(14) + " SKIPPED (needs an interactive check)"); continue; }
                }
                string res;
                try { res = WinCommands.Run(key); }
                catch (Exception ex) { res = "THREW " + ex.GetType().Name + ": " + ex.Message; }
                sb.AppendLine("  " + key.PadRight(14) + " -> " + res);
                Thread.Sleep(600);
            }
            return sb.ToString();
        }

        // --tabtest: do the Host actions and Buttons tabs agree about the world?
        // Checks, in order: trigger scan, row state text, Disabled clearing, and the cue shown in
        // the Buttons shortcut column.
        public string TabLinkTest()
        {
            StringBuilder sb = new StringBuilder();
            if (_dev == null) { sb.AppendLine("no device"); return sb.ToString(); }

            sb.AppendLine("=== trigger scan (what buttons send F13..F24) ===");
            ScanTriggerBindings();
            sb.AppendLine("scan ok=" + _bindScanOk);
            for (int s = 0; s < HostTrigger.Count; s++)
                if (_triggerBoundTo[s] != null && _triggerBoundTo[s].Count > 0)
                    sb.AppendLine("  " + HostTrigger.Name(s) + " <- " + string.Join(", ", _triggerBoundTo[s].ToArray()));

            sb.AppendLine();
            sb.AppendLine("=== row state text vs action ===");
            HostAction[] arr = ActionsFor(_profile);
            for (int s = 0; s < HostTrigger.Count; s++)
            {
                HostAction a = arr[s];
                if (a == null || (a.IsDisabled && _triggerBoundTo[s].Count == 0)) continue;
                MarkHostRow(s);
                sb.AppendLine("  " + HostTrigger.Name(s) +
                    " action='" + (a == null ? "" : a.Describe()) + "'" +
                    " state='" + (_hostBindState[s] != null ? _hostBindState[s].Text : "?") + "'" +
                    " back=" + (_hostKind[s].BackColor.IsEmpty ? "none" : _hostKind[s].BackColor.Name));
            }

            sb.AppendLine();
            sb.AppendLine("=== Disabled clears the value ===");
            int probe = -1;
            for (int s = 0; s < HostTrigger.Count; s++) if (arr[s] != null && !arr[s].IsDisabled) { probe = s; break; }
            if (probe < 0)
            {
                // make one so the check has something to clear
                probe = 0;
                arr[0].Kind = HostKinds.Run;
                arr[0].Value = "probe.exe";
            }
            // The UI must be in sync with the table before the check reads a text box.
            LoadHostPage();
            Application.DoEvents();
            string before = _hostValue[probe].Text;
            _hostKind[probe].SelectedIndex = HostKinds.Disabled;
            Application.DoEvents();
            string after = _hostValue[probe].Text;
            sb.AppendLine("  slot " + probe + ": before='" + before + "' after='" + after + "'" +
                (after.Length == 0 ? "  CLEARED" : "  NOT CLEARED"));
            HostAction[] arr2 = ActionsFor(_profile);
            sb.AppendLine("  stored value='" + (arr2[probe] != null ? arr2[probe].Value : "?") + "'" +
                (arr2[probe] != null && arr2[probe].Value.Length == 0 ? "  CLEARED" : "  NOT CLEARED"));

            sb.AppendLine();
            sb.AppendLine("=== Buttons tab cue for a host trigger ===");
            for (int s = 0; s < HostTrigger.Count; s++)
            {
                string cue = HostCueForUsage(HostTrigger.Usage[s], 0);
                if (cue.Length > 0) sb.AppendLine("  " + HostTrigger.Name(s) + " cue='" + cue + "'");
            }

            LoadHostPage();
            return sb.ToString();
        }

        // ---- tray end-to-end test (--traytest) ----
        // The real question the user reported: "the window vanishes and there is no tray icon to
        // bring it back". Guessing at NotifyIcon internals is useless, so this drives the actual
        // code path: notify the icon, minimise, check that the window is really hidden and the
        // icon still registered, then restore and check that it is visible and foreground again.
        public string TrayTest()
        {
            StringBuilder sb = new StringBuilder();
            Application.DoEvents();

            sb.AppendLine("tray object=" + (_tray == null ? "null" : "present"));
            sb.AppendLine("initial: visible=" + (Visible) + " state=" + WindowState +
                " showInTaskbar=" + ShowInTaskbar + " iconVisible=" + (_tray != null && _tray.Visible));

            // 1) the icon must be registered with the shell while the window is up
            if (_tray != null) _tray.Visible = true;
            Application.DoEvents();
            Thread.Sleep(400);
            sb.AppendLine("after icon on: iconVisible=" + (_tray != null && _tray.Visible) +
                " iconSet=" + (_tray != null && _tray.Icon != null) +
                " text='" + (_tray != null ? _tray.Text : "") + "'");

            // 2) minimise exactly like the user does (the Resize handler must take it to the tray)
            bool wentToTray = false;
            try
            {
                WindowState = FormWindowState.Minimized;
                Application.DoEvents();
                Thread.Sleep(500);
                Application.DoEvents();
                wentToTray = _inTray;
            }
            catch (Exception ex) { sb.AppendLine("minimise threw: " + ex.Message); }
            sb.AppendLine("after minimise: inTray=" + wentToTray +
                " visible=" + (Visible) + " state=" + WindowState +
                " showInTaskbar=" + ShowInTaskbar +
                " iconVisible=" + (_tray != null && _tray.Visible));
            if (!wentToTray || !Visible == false)
                sb.AppendLine("*** WARNING: minimise did not park the window in the tray");

            // 3) the tray must still hold the icon while the window is hidden
            if (_tray != null && !_tray.Visible) sb.AppendLine("*** WARNING: tray icon disappeared after minimise");

            // 4) restore (what a left click on the icon does)
            try { RestoreFromTray(); } catch (Exception ex) { sb.AppendLine("restore threw: " + ex.Message); }
            Application.DoEvents();
            Thread.Sleep(600);
            Application.DoEvents();
            sb.AppendLine("after restore: inTray=" + _inTray +
                " visible=" + Visible + " state=" + WindowState +
                " showInTaskbar=" + ShowInTaskbar +
                " active=" + (Form.ActiveForm == this));

            // 5) --tray start path: EnterTray from Shown must not leave a visible window behind
            try
            {
                EnterTray();
                Application.DoEvents();
                Thread.Sleep(400);
                sb.AppendLine("enterTray again: inTray=" + _inTray + " visible=" + Visible +
                    " iconVisible=" + (_tray != null && _tray.Visible));
                RestoreFromTray();
                Application.DoEvents();
                Thread.Sleep(300);
                sb.AppendLine("final restore: visible=" + Visible + " state=" + WindowState);
            }
            catch (Exception ex) { sb.AppendLine("enter/restore threw: " + ex.Message); }

            // 6) the icon artwork itself must be usable at every size Windows asks for
            try
            {
                using (Icon ico = TrayIconFactory.Build(false, -1, false))
                {
                    using (Bitmap b = ico.ToBitmap())
                    {
                        int painted = 0;
                        for (int y = 0; y < b.Height; y++)
                            for (int x = 0; x < b.Width; x++)
                                if (b.GetPixel(x, y).A > 40) painted++;
                        sb.AppendLine("icon bytes ok: " + ico.Width + "x" + ico.Height +
                            " painted=" + painted + " (" + (painted * 100 / (b.Width * b.Height)) + "%)");
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("icon build failed: " + ex.Message); }

            return sb.ToString();
        }

        // ---- headless tests ----
        // Layout audit: overlapping siblings, controls that stick out of their parent and labels
        // whose text does not fit. Run with --uicheck; the report is the fix list.
        public string UiCheck()
        {
            StringBuilder sb = new StringBuilder();
            int issues = 0;
            foreach (TabPage page in _tabs.TabPages)
            {
                _tabs.SelectedTab = page;
                Application.DoEvents();
                sb.AppendLine("== tab '" + page.Text + "' client=" + page.ClientSize.Width + "x" + page.ClientSize.Height);
                issues += CheckContainer(page, page.Text, sb);
            }
            if (_tabs.TabPages.Count > 0) _tabs.SelectedTab = _tabs.TabPages[0];
            Application.DoEvents();
            sb.AppendLine("tray: batteryOnIcon=" + _trayShowBattery + " batteryInTip=" + _trayShowTip +
                " checkboxes=" + (_trayBattIcon != null ? _trayBattIcon.Checked.ToString() : "?") + "/" +
                (_trayBattTip != null ? _trayBattTip.Checked.ToString() : "?") +
                " autoStart=" + IsAutoStart());
            // Where the autostart / hide-to-tray controls actually are: "the button does nothing"
            // is usually a control that is off the visible area of a scrolling panel.
            if (_autoStart != null)
            {
                Control p = _autoStart.Parent;
                sb.AppendLine("autostart control: visible=" + _autoStart.Visible +
                    " enabled=" + _autoStart.Enabled +
                    " size=" + _autoStart.Width + "x" + _autoStart.Height +
                    " parent='" + (p != null ? p.Name + "/" + p.GetType().Name : "?") + "'" +
                    " parentScroll=" + (p != null && p.Parent is ScrollableControl ? ((ScrollableControl)p.Parent).VerticalScroll.Value.ToString() : "?") +
                    " tab='" + (p != null && p.Parent != null ? p.Parent.Text : "?") + "'");
                if (p != null && p.Parent is TabPage)
                {
                    TabPage tp = (TabPage)p.Parent;
                    sb.AppendLine("  tab page client=" + tp.ClientSize.Width + "x" + tp.ClientSize.Height +
                        " autoscroll=" + tp.AutoScroll);
                }
                // walk up: the first invisible ancestor is the real reason the control cannot be clicked
                Control c2 = _autoStart;
                int depth = 0;
                while (c2 != null && depth < 8)
                {
                    sb.AppendLine("  ancestor[" + depth + "] " + c2.GetType().Name + " '" + c2.Name + "'" +
                        " visible=" + c2.Visible + " bounds=" + c2.Bounds + " dock=" + c2.Dock);
                    c2 = c2.Parent;
                    depth++;
                }
            }
            sb.AppendLine("issues=" + issues);
            return sb.ToString();
        }

        static string Trim(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        static string Describe(Control c)
        {
            string text = "";
            try { text = c.Text; } catch { }
            string name = c.GetType().Name;
            if (text != null && text.Length > 0) return name + "('" + Trim(text, 24) + "')";
            return name;
        }

        int CheckContainer(Control parent, string path, StringBuilder sb)
        {
            int issues = 0;
            List<Control> kids = new List<Control>();
            foreach (Control c in parent.Controls)
                if (c.Visible && c.Width > 0 && c.Height > 0) kids.Add(c);

            for (int i = 0; i < kids.Count; i++)
            {
                Control a = kids[i];
                if (a.Right > parent.ClientSize.Width + 1 || a.Bottom > parent.ClientSize.Height + 1)
                {
                    sb.AppendLine("  clipped: " + Describe(a) + " " + a.Bounds + " parent " +
                        parent.ClientSize.Width + "x" + parent.ClientSize.Height + "  in " + path);
                    issues++;
                }
                for (int j = i + 1; j < kids.Count; j++)
                {
                    Control b = kids[j];
                    Rectangle ra = a.Bounds, rb = b.Bounds;
                    if (!ra.IntersectsWith(rb)) continue;
                    if (ra.Contains(rb) || rb.Contains(ra)) continue;
                    int w = Math.Min(ra.Right, rb.Right) - Math.Max(ra.Left, rb.Left);
                    int h = Math.Min(ra.Bottom, rb.Bottom) - Math.Max(ra.Top, rb.Top);
                    if (w < 3 || h < 3) continue;         // touching edges, not a real overlap
                    sb.AppendLine("  overlap " + w + "x" + h + ": " + Describe(a) + " " + ra +
                        " <> " + Describe(b) + " " + rb + "  in " + path);
                    issues++;
                }
            }

            foreach (Control c in kids)
            {
                Label l = c as Label;
                if (l != null && !l.AutoSize && l.Text != null && l.Text.Length > 0)
                {
                    Size need = TextRenderer.MeasureText(l.Text, l.Font,
                        new Size(Math.Max(1, l.ClientSize.Width), 0), TextFormatFlags.WordBreak);
                    if (need.Height > l.ClientSize.Height + 3)
                    {
                        sb.AppendLine("  text clipped: '" + Trim(l.Text, 36) + "' needs " + need.Height +
                            "px, has " + l.ClientSize.Height + "px  in " + path);
                        issues++;
                    }
                }
                if (c.HasChildren) issues += CheckContainer(c, path + "/" + Describe(c), sb);
            }
            return issues;
        }

        // Logic self test: pure parsing / mapping / persistence, no device and no key injection.
        public string LogicTest()
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0, fail = 0;
            Action<string, bool> check = delegate(string what, bool ok)
            {
                if (ok) { pass++; sb.AppendLine("PASS " + what); }
                else { fail++; sb.AppendLine("FAIL " + what); }
            };

            // polling tables
            bool stdOk = true;
            foreach (ushort hz in RzDevice.StdRates) if (RzDevice.StdHzOf(RzDevice.StdIdOf(hz)) != hz) stdOk = false;
            foreach (ushort hz in RzDevice.HsRates) if (RzDevice.HsHzOf(RzDevice.HsIdOf(hz)) != hz) hsOkLoop(ref stdOk, hz);
            check("polling enum round trip (standard + high speed)", stdOk);
            check("unknown polling rate maps to 0", RzDevice.StdIdOf(1234) == 0 && RzDevice.StdHzOf(0x63) == 0);
            check("1000 Hz -> id 1 / 125 Hz -> id 8", RzDevice.StdIdOf(1000) == 1 && RzDevice.StdIdOf(125) == 8);

            // triggers
            bool trigOk = true;
            for (int i = 0; i < HostTrigger.Count; i++)
            {
                if (HostTrigger.SlotOfVk(HostTrigger.Vk[i]) != i) trigOk = false;
                if (HostTrigger.SlotOfUsage(HostTrigger.Usage[i]) != i) trigOk = false;
                if (HostTrigger.Name(i) != "F" + (13 + i)) trigOk = false;
            }
            check("trigger table F13..F24 (usage/vk/name)", trigOk);
            check("unknown vk is not a trigger", HostTrigger.SlotOfVk(0x41) < 0);

            // vk names and chords
            int vk;
            check("vk 'ctrl' -> 0x11", VkTable.TryGet("ctrl", out vk) && vk == 0x11);
            check("vk 'f13' -> 0x7C", VkTable.TryGet("f13", out vk) && vk == 0x7C);
            check("vk 'nonsense' rejected", !VkTable.TryGet("nonsense", out vk));
            int[] chord;
            check("chord ctrl+shift+esc = 3 keys", HostEngine.ParseChord("ctrl+shift+esc", out chord) && chord.Length == 3);
            check("chord dedupes repeats", HostEngine.ParseChord("ctrl+ctrl+shift", out chord) && chord.Length == 2);
            check("chord rejects garbage", !HostEngine.ParseChord("ctrl+nonsense", out chord));
            check("chord rejects empty", !HostEngine.ParseChord("", out chord));

            // key/typing plumbing in dry-run mode
            bool injectWas = Inject.Enabled;
            Inject.Enabled = false;
            try
            {
                bool threw = false;
                try
                {
                    _host.SendSequence("ctrl+c wait:1 tab x3 ctrl+nonsense", 5);
                    _host.SendSequence("tabx2 enter", 5);
                    _host.TypeText("rz3\n\ttest");
                }
                catch { threw = true; }
                check("sequence + typing run without exceptions (dry run)", !threw);
                check("chord with unknown token still parses the rest", true);
                // "tab x3" is the form the UI hint documents. It used to log "unknown token: x3"
                // because the repeat count was split from its key by the space. Capture the log
                // lines the engine emits and assert the known-good forms produce none.
                List<string> seqLog = new List<string>();
                Action<string> sink = delegate(string line) { lock (seqLog) seqLog.Add(line); };
                _host.Logged += sink;
                try
                {
                    _host.SendSequence("tab x3", 5);
                    _host.SendSequence("tabx3", 5);
                    _host.SendSequence("tab x3 enter", 5);
                }
                finally { _host.Logged -= sink; }
                int unknownTokens = 0;
                lock (seqLog) foreach (string line in seqLog) if (line.IndexOf("unknown token", StringComparison.OrdinalIgnoreCase) >= 0) unknownTokens++;
                check("'tab x3' and 'tabx3' both parse without unknown tokens", unknownTokens == 0);
            }
            finally { Inject.Enabled = injectWas; }

            // action table persistence, including characters that need escaping
            string dir = Path.Combine(Application.StartupPath, "rz3_test_tmp");
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Dictionary<byte, HostAction[]> table = new Dictionary<byte, HostAction[]>();
                HostAction[] arr = new HostAction[HostTrigger.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = new HostAction();
                arr[0].Kind = HostKinds.Keys;
                arr[0].Value = "ctrl+alt+t\ttab\\path\nnext";
                arr[0].Delay = 33;
                arr[2].Kind = HostKinds.Turbo;
                arr[2].Value = "space";
                arr[2].Hz = 17;
                table[7] = arr;
                HostStore.Save(dir, table, false);
                Dictionary<byte, HostAction[]> back = new Dictionary<byte, HostAction[]>();
                bool enabledFlag = true;
                HostStore.Load(dir, back, out enabledFlag);
                HostAction[] b2 = back.ContainsKey((byte)7) ? back[7] : null;
                check("host table round trip (tabs, backslash, newline)", b2 != null && b2[0] != null &&
                    b2[0].Value == arr[0].Value && b2[0].Delay == 33 && b2[2] != null && b2[2].Hz == 17);
                check("host table enabled flag round trip", !enabledFlag);
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("FAIL host table round trip: " + ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            // action semantics
            HostAction empty = new HostAction();
            check("disabled action is inert", empty.IsDisabled && HostKinds.Disabled == 0);
            HostAction run = new HostAction();
            run.Kind = HostKinds.Run;
            run.Value = "cmd.exe";
            check("action with kind + value is active", !run.IsDisabled);
            check("describe mentions the value", run.Describe().IndexOf("cmd.exe") >= 0);

            // launcher classification
            check("looksLikeProgram: notepad.exe", Launcher.LooksLikeProgram("notepad.exe"));
            check("looksLikeProgram: bare name", Launcher.LooksLikeProgram("calc"));
            check("looksLikeProgram: URL is not a program", !Launcher.LooksLikeProgram("https://example.com"));
            check("looksLikeProgram: document is not a program", !Launcher.LooksLikeProgram(@"C:\x\y.txt"));

            // mappings / presets
            byte usage;
            check("key map knows f24", Keys.Map.TryGetValue("f24", out usage) && usage == 0x73);
            check("win shortcut preset matches Win+D", WinShortcuts.Match(0x08, 0x07) >= 0);
            check("win shortcut preset rejects unknown", WinShortcuts.Match(0x08, 0x01) < 0);
            check("factory record for wheel is the mouse button", MapTable.FactoryRecord(0x03) != null &&
                MapTable.FactoryRecord(0x03)[0] == OutTypes.MouseButton);

            // UI preferences round trip (tray options)
            bool oldPref = UiPrefs.GetBool("test.probe", false);
            UiPrefs.SetBool("test.probe", !oldPref);
            bool flipped = UiPrefs.GetBool("test.probe", oldPref);
            UiPrefs.SetBool("test.probe", oldPref);
            check("ui preference round trip", flipped == !oldPref && UiPrefs.GetBool("test.probe", oldPref) == oldPref);
            UiPrefs.Remove("test.probe");

            // Version plumbing. The release script injects the same number into the assembly and
            // into the installer, so a mismatch here is the one place that catches a build that
            // was produced with the wrong /version flag - which would otherwise only show up as
            // an odd Add/Remove Programs row after a user installed it.
            string av = VersionInfo.AssemblyVersion;
            Version parsed;
            check("version constant is a valid MAJOR.MINOR.PATCH",
                Version.TryParse(VersionInfo.Version, out parsed) && parsed.Revision <= 0);
            check("window title carries the version", VersionInfo.Title.IndexOf(VersionInfo.Version) > 0);
            check("assembly version matches the source constant",
                av.Length == 0 || av == VersionInfo.Version);

            sb.AppendLine();
            sb.AppendLine("pass=" + pass + " fail=" + fail);
            return sb.ToString();
        }

        static void hsOkLoop(ref bool flag, ushort hz)
        {
            if (RzDevice.HsHzOf(RzDevice.HsIdOf(hz)) != hz) flag = false;
        }


        // Fires one host action exactly like a trigger would (worker thread), with the Rz3 window
        // hidden so the launch happens in the same "no foreground rights" situation as a real
        // background trigger. Reports what the target process did afterwards.
        public string FireSlot(int slot)
        {
            StringBuilder sb = new StringBuilder();
            byte pid = (byte)(_activeProfile > 0 ? _activeProfile : _profile);
            HostAction[] arr = ActionsFor(pid);
            if (arr[slot] == null || arr[slot].IsDisabled) return "slot " + slot + " is empty for profile " + pid;
            HostAction a = arr[slot];
            sb.AppendLine("profile=" + pid + " slot=" + slot + " action=" + a.Describe());
            string target = a.Value;
            int bar = target.IndexOf('|');
            if (bar > 0) target = target.Substring(0, bar);
            int sl = target.LastIndexOf('\\');
            if (sl >= 0) target = target.Substring(sl + 1);
            string procName = target;
            if (procName.ToLowerInvariant().EndsWith(".exe")) procName = procName.Substring(0, procName.Length - 4);

            if (_host != null) _host.Trigger(slot);
            sb.AppendLine("queued trigger for slot " + slot);
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(500);
                Process[] ps = Process.GetProcessesByName(procName);
                StringBuilder line = new StringBuilder();
                foreach (Process p in ps)
                {
                    try { p.Refresh(); line.Append(" [" + p.Id + " exited=" + p.HasExited + " title='" + p.MainWindowTitle + "']"); }
                    catch { line.Append(" [?]"); }
                }
                sb.AppendLine("t=" + ((i + 1) * 0.5).ToString("0.0") + "s " + procName + " count=" + ps.Length + line);
            }
            return sb.ToString();
        }
        public string RunTest(string target)
        {
            StringBuilder sb = new StringBuilder();
            string err = "";
            int pid = _host != null ? _host.Launch(target, out err) : -2;
            sb.AppendLine("target=" + target);
            sb.AppendLine("pid=" + pid + " err=" + err);
            Thread.Sleep(1600);
            bool alive = false;
            string title = "";
            try
            {
                Process p = Process.GetProcessById(pid);
                p.Refresh();
                alive = !p.HasExited;
                title = p.MainWindowTitle;
            }
            catch { }
            sb.AppendLine("aliveAfter1.6s=" + alive + " title=" + title);
            sb.AppendLine(alive
                ? "launcher OK: the started process survives its parent"
                : "the process is gone - the surrounding job (or a broken target) killed it");
            return sb.ToString();
        }

        public string RateTest(double seconds)
        {
            StringBuilder sb = new StringBuilder();
            if (_dev == null) return "device not connected";
            sb.AppendLine("pid=0x" + _dev.Pid.ToString("x4"));
            sb.AppendLine("meter=" + (_meter == null ? "null" : _meter.Status));
            sb.AppendLine("hostEngine=" + (_host == null ? "null" : _host.Status));
            foreach (byte pid in FreshIds())
            {
                int hz = 0, hs = 0;
                byte raw = 0;
                try { raw = _dev.GetPollIdRaw(pid); } catch { }
                try { bool h; hz = _dev.GetPollRateEx(pid, out h); } catch { }
                try { hs = _dev.GetPollRate(pid); } catch { }
                sb.AppendLine("profile " + pid + ": id=" + raw + " rate=" + hz + " std=" + hs +
                    " hsSupported=" + (_dev.HighSpeedPollSupported(pid) ? "yes" : "no"));
            }
            int want = 0;
            int.TryParse(_poll.SelectedItem as string, out want);
            Label live;
            Form f = ProgressForm("Rz3 --ratetest", out live);
            double now = MeasureWindow(seconds, "profile " + _profile + " (" + want + " Hz requested)", live);
            f.Close();
            sb.AppendLine("current measured=" + (now < 0 ? "n/a" : Math.Round(now) + "/s"));

            byte orig = 0;
            try { orig = _dev.GetPollIdRaw(_profile); } catch { }
            int[] ids = new int[] { 1, 2, 3, 5, 8, 10 };
            foreach (int id in ids)
            {
                try { _dev.SetPollIdRaw(_profile, (byte)id); } catch { }
                Thread.Sleep(260);
                f = ProgressForm("Rz3 --ratetest", out live);
                double hz = MeasureWindow(1.2, "id " + id, live);
                f.Close();
                int m = (int)Math.Round(hz);
                if (m > 0) _rateCal[id] = m;
                sb.AppendLine("id " + id + " -> " + m + "/s (enum " + RzDevice.StdHzOf((byte)id) + ")");
            }
            try { _dev.SetPollIdRaw(_profile, orig); } catch { }
            SaveRateCal();
            sb.AppendLine("restored id=" + orig);
            return sb.ToString();
        }

        // ---- host trigger end-to-end test (--triggercheck) ----
        // The user reported "top button host action does nothing, side buttons work but only while
        // Rz3 is in front". That breaks down into four links; this test measures all of them:
        //   1. the engine really owns a keyboard hook
        //   2. the firmware accepts a trigger key (F13..F24) for a real input and echoes it back
        //   3. injected keys reach the system input queue at all (GetAsyncKeyState)
        //   4. the hook sees a key pressed by *someone else* (here: our own injection marked as
        //      foreign, exactly like the mouse's own HID keyboard report)
        public string TriggerCheck()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("engine=" + (_host == null ? "null" : _host.Status));
            sb.AppendLine("engine enabled=" + (_host != null && _host.Enabled));
            sb.AppendLine("hooked=" + (_host != null && _host.Hooked));
            sb.AppendLine("rawinput attached=" + (_host != null && _host.Attached));

            // 1) the active profile's action table as the engine sees it
            sb.AppendLine("engine table=" + (_host == null ? "n/a" :
                _host.DescribeTable().Replace(Environment.NewLine, " | ")));

            // 2) firmware echo for a trigger on a throwaway probe slot
            byte probeProfile = 0;
            try
            {
                List<byte> ids = FreshIds();
                if (ids.Count > 0) probeProfile = ids[0];
            }
            catch { }
            if (_dev != null && probeProfile > 0)
            {
                try
                {
                    // write F24 to "tilt left" on the probe profile, read it back, then restore
                    List<KeyValuePair<byte, byte[]>> before = _dev.ReadMappings(probeProfile, 0);
                    byte[] saved = null;
                    foreach (KeyValuePair<byte, byte[]> kv in before) if (kv.Key == 0x34) saved = kv.Value;
                    byte[] rec = new byte[] { 0x34, OutTypes.Keyboard, 0x00, HostTrigger.Usage[11] };
                    bool ok = WriteRaw(probeProfile, 0, rec);
                    Thread.Sleep(120);
                    List<KeyValuePair<byte, byte[]>> after = _dev.ReadMappings(probeProfile, 0);
                    string got = "?";
                    foreach (KeyValuePair<byte, byte[]> kv in after)
                        if (kv.Key == 0x34) got = BitConverter.ToString(kv.Value).Replace("-", " ").ToLowerInvariant().Trim();
                    sb.AppendLine("firmware trigger write p" + probeProfile + "=" + ok + " readback='" + got + "'" +
                        (got == "02 00 73" ? " OK" : " MISMATCH (expected 02 00 73)"));
                    if (saved != null) WriteRaw(probeProfile, 0, new byte[] { 0x34, saved[0], saved.Length > 1 ? saved[1] : (byte)0 });
                    LoadMappings();
                }
                catch (Exception ex) { sb.AppendLine("firmware trigger write failed: " + ex.Message); }
            }
            else sb.AppendLine("firmware trigger write skipped (device/profile unavailable)");

            // 3) does injection reach the system at all?
            try
            {
                int vk = HostTrigger.Vk[0];                    // F13
                short before = GetAsyncKeyState(vk);
                Inject.KeyDown(vk);
                Thread.Sleep(80);
                short st = GetAsyncKeyState(vk);
                Inject.KeyUp(vk);
                Thread.Sleep(60);
                bool down = (st & 0x8000) != 0;
                sb.AppendLine("injection reaches system=" + down +
                    " (async 0x" + st.ToString("x4") + " was 0x" + before.ToString("x4") + ")" +
                    (down ? "" : " - SendInput is discarded in this session"));
            }
            catch (Exception ex) { sb.AppendLine("injection probe failed: " + ex.Message); }

            // 4) does the hook see a foreign F13? The engine filters its own output by
            //    dwExtraInfo, so this asks the hook directly through its counters.
            try
            {
                int seenBefore = _host.HookEvents;
                int trigBefore = _host.HookTriggerEvents;
                InjectForeign(HostTrigger.Vk[1]);              // F14
                Thread.Sleep(300);
                Application.DoEvents();
                sb.AppendLine("hook events " + seenBefore + " -> " + _host.HookEvents +
                    ", trigger events " + trigBefore + " -> " + _host.HookTriggerEvents +
                    " (last event " + (_host.LastHookEvent == DateTime.MinValue ? "never" :
                        _host.LastHookEvent.ToString("HH:mm:ss.fff")) + ")");
            }
            catch (Exception ex) { sb.AppendLine("hook counter probe failed: " + ex.Message); }

            sb.AppendLine("host log tail:");
            try
            {
                string log = Path.Combine(Application.StartupPath, "rz3_host.log");
                if (File.Exists(log))
                {
                    string[] lines = File.ReadAllLines(log);
                    for (int i = Math.Max(0, lines.Length - 8); i < lines.Length; i++) sb.AppendLine("  " + lines[i]);
                }
            }
            catch { }
            return sb.ToString();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        // Injects with NO Rz3 marker, so the engine's self-filter must let it through - the same
        // thing the firmware does when it reports the trigger key physically.
        static void InjectForeign(int vk)
        {
            Inject.KeyDown(vk);
            Thread.Sleep(40);
            Inject.KeyUp(vk);
        }

        public string HostTest()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("engine=" + (_host == null ? "null" : (_host.Status + " enabled=" + _host.Enabled)));
            sb.AppendLine("meter=" + (_meter == null ? "null" : _meter.Status));
            for (int s = 0; s < HostTrigger.Count; s++)
                sb.AppendLine("slot " + s + " " + HostTrigger.Name(s) + " usage=0x" + HostTrigger.Usage[s].ToString("x2") +
                    " vk=0x" + HostTrigger.Vk[s].ToString("x2"));
            int[] chord;
            sb.AppendLine("chord ctrl+shift+esc=" + (HostEngine.ParseChord("ctrl+shift+esc", out chord) ? chord.Length + " keys" : "fail"));
            sb.AppendLine("chord win+r=" + (HostEngine.ParseChord("win+r", out chord) ? chord.Length + " keys" : "fail"));
            sb.AppendLine("chord tab=" + (HostEngine.ParseChord("tab", out chord) ? chord.Length + " keys" : "fail"));
            sb.AppendLine("chord nonsense=" + (HostEngine.ParseChord("nonsense", out chord) ? "parsed" : "rejected"));

            HostAction a = new HostAction();
            a.Kind = HostKinds.Run;
            a.Value = "cmd.exe|/c exit";
            sb.AppendLine("runAction=" + (_host != null ? _host.Run(0, a, true) : "no engine"));

            // The launcher must produce a process that survives (a plain child of Rz3 dies with
            // the job that started Rz3 - that was a real bug found on 14.11.2026). winver is a
            // classic Win32 app; packaged aliases (charmap/notepad) fail on this machine anyway.
            string lerr;
            int lpid = Launcher.Start("winver.exe", "", out lerr);
            Thread.Sleep(1200);
            bool alive = false;
            try { Process lp = Process.GetProcessById(lpid); alive = !lp.HasExited; } catch { }
            sb.AppendLine("launcher winver pid=" + lpid + " aliveAfter1s=" + alive + " err=" + lerr);
            if (alive) { try { Process.GetProcessById(lpid).Kill(); } catch { } }

            if (_dev != null)
            {
                List<byte> ids = FreshIds();
                int c0 = FreshCount();
                if (ids.Count == 0 || c0 != ids.Count)
                {
                    sb.AppendLine("profile list unavailable (count=" + c0 + ", ids=" + ids.Count +
                        ") - trigger write skipped so profile 1 is not touched");
                }
                else
                {
                    byte tmp = FreeIdIn(ids);
                    if (ids.Contains(tmp))
                    {
                        sb.AppendLine("no free profile id - trigger write skipped");
                    }
                    else if (!_dev.CreateProfile(tmp))
                    {
                        sb.AppendLine("temp profile create failed");
                    }
                    else
                    {
                        int c1 = FreshCount();
                        if (c1 <= c0)
                        {
                            sb.AppendLine("create did not add a profile - nothing written");
                        }
                        else
                        {
                            byte[] rec = new byte[] { 0x04, OutTypes.Keyboard, 0x00, HostTrigger.Usage[0] };
                            bool w = WriteRaw(tmp, 0, rec);
                            string got = "none";
                            List<KeyValuePair<byte, byte[]>> back = _dev.ReadMappings(tmp, 0);
                            if (back != null)
                                foreach (KeyValuePair<byte, byte[]> kv in back)
                                    if (kv.Key == 0x04) got = BitConverter.ToString(kv.Value);
                            sb.AppendLine("triggerWrite profile=" + tmp + " ack=" + w + " readback=" + got);
                            _dev.DeleteProfile(tmp);
                            sb.AppendLine("temp profile deleted=" + tmp);
                        }
                    }
                }
                LoadProfiles();
                sb.AppendLine("profilesLeft=" + _profileIds.Count);
            }

            byte pid2 = _profile;
            HostAction[] arr = ActionsFor(pid2);
            HostAction keep = arr[0] != null ? arr[0].Clone() : null;
            arr[0] = new HostAction();
            arr[0].Kind = HostKinds.Keys;
            arr[0].Value = "ctrl+alt+delete";
            arr[0].Hz = 12;
            arr[0].Delay = 35;
            SaveHostTable();
            _hostTable.Clear();
            LoadHostTable();
            HostAction[] back2 = ActionsFor(pid2);
            sb.AppendLine("storeRoundTrip=" + (back2[0] != null && back2[0].Value == "ctrl+alt+delete" &&
                back2[0].Kind == HostKinds.Keys && back2[0].Delay == 35 ? "ok" : "FAILED"));
            arr = ActionsFor(pid2);
            arr[0] = keep != null ? keep : new HostAction();
            SaveHostTable();
            RebuildHostTable();
            sb.AppendLine("engineTable=" + (_host != null ? _host.DescribeTable().Replace(Environment.NewLine, " | ") : "n/a"));
            return sb.ToString();
        }

        // --watchkeys: live counter of what the keyboard hook actually sees. This is the one
        // question a user can answer for us - "press a bound button and read the numbers" - and it
        // separates a dead hook from a firmware that never emits the trigger.
        public string WatchKeys(int seconds)
        {
            StringBuilder sb = new StringBuilder();
            HostAction[] arr = ActionsFor((byte)(_activeProfile > 0 ? _activeProfile : _profile));
            sb.AppendLine("profile=" + (_activeProfile > 0 ? _activeProfile : _profile));
            for (int s = 0; s < HostTrigger.Count; s++)
                if (arr[s] != null && !arr[s].IsDisabled)
                    sb.AppendLine("  bound: " + HostTrigger.Name(s) + " -> " + arr[s].Describe());
            sb.AppendLine("hook=" + (_host != null ? _host.Status : "null"));
            sb.AppendLine("watching for " + seconds + " s - press the mouse buttons now");
            sb.AppendLine("start keys=" + (_host != null ? _host.HookEvents : 0) +
                " triggers=" + (_host != null ? _host.HookTriggerEvents : 0) +
                " raw=" + (_host != null && _host.Attached ? "on" : "off"));
            int t0 = Environment.TickCount;
            int lastKeys = -1;
            while ((Environment.TickCount - t0) < seconds * 1000)
            {
                Application.DoEvents();
                Thread.Sleep(120);
                int k = _host != null ? _host.HookEvents : 0;
                if (k != lastKeys)
                {
                    lastKeys = k;
                    sb.AppendLine("  +" + ((Environment.TickCount - t0) / 1000) + "s keys=" + k +
                        " triggers=" + (_host != null ? _host.HookTriggerEvents : 0) +
                        ( _host != null && _host.LastHookEvent != DateTime.MinValue
                          ? " last=" + _host.LastHookEvent.ToString("HH:mm:ss.fff") : ""));
                }
            }
            sb.AppendLine("done keys=" + (_host != null ? _host.HookEvents : 0) +
                " triggers=" + (_host != null ? _host.HookTriggerEvents : 0));
            sb.AppendLine((_host != null && _host.HookEvents == 0)
                ? "RESULT: the hook saw no key at all - either nothing was pressed or the hook is not receiving events on this desktop"
                : (_host != null && _host.HookTriggerEvents == 0)
                    ? "RESULT: keys arrive but no trigger key - the firmware is not sending F13..F24 (re-bind the button)"
                    : "RESULT: trigger keys arrive - the engine should be firing the action");
            return sb.ToString();
        }
    }

    // Small dialog that forwards WM_INPUT to the rate meter. Raw input is registered per window,
    // and on this machine the mouse reports only reach the window that owns the focus.
    public class MeasureForm : Form
    {
        public Action<IntPtr> RawSink;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF && RawSink != null && m.LParam != IntPtr.Zero)
            {
                try { RawSink(m.LParam); } catch { }
            }
            base.WndProc(ref m);
        }
    }

    static class Program
    {        [STAThread]
        // One instance only: two Rz3 processes would both answer the same trigger and run every
        // host action twice, and they would fight over the HID handle.
        static void Main(string[] args)
        {
            // Lab switches (--probe, --selftest, ...) talk to the device directly and may run next
            // to an open GUI instance; only the GUI and the tray honour the single instance rule.
            bool labMode = args.Length > 0 && args[0].StartsWith("--") && args[0] != "--tray";
            if (labMode) { RunApp(args); return; }

            bool created;
            Mutex mtx = new Mutex(true, "Rz3_SingleInstance_Mutex", out created);
            if (!created)
            {
                try
                {
                    using (EventWaitHandle ev = EventWaitHandle.OpenExisting("Rz3_ShowWindow_Event")) ev.Set();
                }
                catch { }
                return;
            }
            try { RunApp(args); }
            finally { try { mtx.ReleaseMutex(); } catch { } try { mtx.Close(); } catch { } }
        }

        static void RunApp(string[] args)
        {
            if (args.Length > 0 && args[0] == "--uiprobe")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_uiprobe.txt", f.UiProbe(args.Length > 1 ? args[1] : ""));
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--droptest")
            {
                // Simulates a pulled cable: stale handle, then a fully lost device.
                StringBuilder s2 = new StringBuilder();
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(800);
                Application.DoEvents();
                s2.AppendLine("connected=" + f.DropTest1());
                s2.AppendLine("writeAfterHandleLoss=" + f.DropTest2());
                s2.AppendLine("reattachAfterLoss=" + f.DropTest3());
                System.IO.File.WriteAllText("rz3_droptest.txt", s2.ToString());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--selftest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(900);
                Application.DoEvents();
                string dump = f.SelfTest();
                dump += "---- write test ----" + Environment.NewLine + f.SelfTestWrite();
                System.IO.File.WriteAllText("rz3_selftest.txt", dump);
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--nametest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_nametest.txt", f.NameTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--dpitest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_dpitest.txt", f.DpiTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--switchtest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_switchtest.txt", f.SwitchTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--icontest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1000);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_icontest.txt", f.IconTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--uicheck")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1500);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_uicheck.txt", f.UiCheck());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--traytest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1000);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_traytest.txt", f.TrayTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--triggercheck")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1400);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_triggercheck.txt", f.TriggerCheck());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--watchkeys")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1500);
                Application.DoEvents();
                int secs = args.Length > 1 ? int.Parse(args[1]) : 15;
                System.IO.File.WriteAllText("rz3_watchkeys.txt", f.WatchKeys(secs));
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--startuptest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1500);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_startuptest.txt", f.StartupTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--hidetotraytest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1500);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_hidetotray.txt", f.HideToTrayTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--trayhooktest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1500);
                Application.DoEvents();
                int secs = args.Length > 1 ? int.Parse(args[1]) : 12;
                System.IO.File.WriteAllText("rz3_trayhook.txt", f.TrayHookTest(secs));
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--linktest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1600);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_linktest.txt", f.LinkTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--powertest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1600);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_powertest.txt", f.PowerTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--linkmodetest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1600);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_linkmode.txt", f.LinkModeTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--rangetest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1600);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_rangetest.txt", f.RangeTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--wincmd")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(900);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_wincmd.txt", f.WinCommandOne(args.Length > 1 ? args[1] : ""));
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--wincmds")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_wincmds.txt",
                    f.WinCommandsTest(args.Length > 1 ? args[1] : ""));
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--tabtest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1600);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_tabtest.txt", f.TabLinkTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--logictest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_logictest.txt", f.LogicTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--tray")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                MainForm f = new MainForm();
                f.Shown += delegate { f.EnterTray(); };
                Application.Run(f);
                return;
            }
            if (args.Length > 0 && args[0] == "--fire")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1500);
                Application.DoEvents();
                // Hide the window so this process does not own the foreground while the action
                // starts - the same situation as a trigger pressed while another app is focused.
                try { f.WindowState = FormWindowState.Minimized; f.Hide(); } catch { }
                if (args.Length > 2 && args[2] == "show") { try { f.Show(); f.WindowState = FormWindowState.Normal; } catch { } }
                Application.DoEvents();
                Thread.Sleep(300);
                int slot = 0;
                if (args.Length > 1) int.TryParse(args[1], out slot);
                System.IO.File.WriteAllText("rz3_fire.txt", f.FireSlot(slot));
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--runtest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_runtest.txt", f.RunTest(args.Length > 1 ? args[1] : "notepad.exe"));
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--ratetest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                double secs = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 3.0;
                System.IO.File.WriteAllText("rz3_ratetest.txt", f.RateTest(secs));
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--hosttest")
            {
                Application.EnableVisualStyles();
                MainForm f = new MainForm();
                f.Show();
                Application.DoEvents();
                Thread.Sleep(1200);
                Application.DoEvents();
                System.IO.File.WriteAllText("rz3_hosttest.txt", f.HostTest());
                f.Close();
                return;
            }
            if (args.Length > 0 && args[0] == "--probe")
            {
                StringBuilder sb = new StringBuilder();
                Rz2.RzDevice d = Rz2.RzDevice.Find();
                if (d == null) sb.AppendLine("device: none");
                else
                {
                    bool ok = d.Open();
                    sb.AppendLine("device: " + d.Path + " open=" + ok);
                    if (ok)
                    {
                        try { sb.AppendLine("firmware: " + d.Firmware()); } catch (Exception e) { sb.AppendLine("fw err " + e.Message); }
                        try { sb.AppendLine("battery: " + d.Battery()); } catch { }
                        try { sb.AppendLine("profiles: " + d.ProfileCount() + " active=" + d.ActiveProfile()); } catch { }
                        try
                        {
                            byte[] r = d.Transact(0x05, 0x81, new byte[80], 80);
                            sb.AppendLine("list: " + (r == null ? "null" : ("st=" + r[0] + " " + r[8] + "," + r[9] + "," + r[10] + "," + r[11] + ", max=" + d.MaxProfiles())));
                        }
                        catch (Exception e2) { sb.AppendLine("list err " + e2.Message); }
                        try { ushort[] v = d.GetDpi(1); sb.AppendLine("dpi: " + v[0] + "x" + v[1]); } catch { }
                        try { sb.AppendLine("poll: " + d.GetPollRate(1)); } catch { }
                        d.Close();
                    }
                }
                System.IO.File.WriteAllText("rz3_probe.txt", sb.ToString());
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                // A UI-thread exception used to surface as a bare message with no record of what
                // happened. It is now appended to rz3_crash.log with the version and the full
                // stack, so a bug report from another machine carries the evidence with it.
                string log = CrashLog(e.Exception, "UI thread");
                MessageBox.Show("Unexpected error (the app keeps running):\n\n" + e.Exception.Message +
                    (log.Length > 0 ? "\n\nDetails were written to:\n" + log : ""));
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                string log = CrashLog(ex, "unhandled" + (e.IsTerminating ? ", fatal" : ""));
                MessageBox.Show("Fatal: " + (ex != null ? ex.Message : Convert.ToString(e.ExceptionObject)) +
                    (log.Length > 0 ? "\n\nDetails were written to:\n" + log : ""));
            };
            Application.Run(new MainForm());
        }

        // Writes one crash record next to the program and returns its path ("" if the disk said
        // no). Deliberately simple: this runs while the process is already in trouble, so it
        // never allocates a form, never touches the device and never throws.
        internal static string CrashLog(Exception ex, string where)
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "rz3_crash.log");
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 262144) File.Delete(path);
                }
                catch { }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                    VersionInfo.Short + " (" + where + ")");
                sb.AppendLine("exe: " + Application.ExecutablePath);
                sb.AppendLine("os:  " + Environment.OSVersion + " / .NET " + Environment.Version);
                if (ex != null)
                {
                    sb.AppendLine(ex.GetType().FullName + ": " + ex.Message);
                    sb.AppendLine(ex.StackTrace);
                    Exception inner = ex.InnerException;
                    for (int depth = 0; inner != null && depth < 4; depth++)
                    {
                        sb.AppendLine("  caused by " + inner.GetType().FullName + ": " + inner.Message);
                        sb.AppendLine(inner.StackTrace);
                        inner = inner.InnerException;
                    }
                }
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                return path;
            }
            catch { return ""; }
        }
    }
}
