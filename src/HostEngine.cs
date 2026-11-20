using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rz3
{
    // ---------------------------------------------------------------------------------------
    // Host-side action layer ("anything the firmware cannot do").
    //
    // The mouse firmware can only emit one HID report per button press: one key plus a modifier
    // mask. Everything else Synapse offers (multi step sequences, delays, "hold", turbo, launch a
    // program, type text, chained window commands) runs in a host process, not in the mouse.
    //
    // Rz3 does the same thing, but openly: a button is written into the firmware as a *trigger*
    // key out of the spare F13..F24 range, and this engine turns that trigger into whatever the
    // user defined. Nothing is hooked globally, the trigger keys are unused by Windows, and the
    // engine lives inside Rz3, so a running Rz3.exe is all that is needed.
    //
    // 12 triggers x (13 buttons x 2 layers) per profile = plenty, and because the action table is
    // kept per profile, the same physical button does different things in different profiles -
    // exactly like the firmware mappings.
    // ---------------------------------------------------------------------------------------
    public static class HostTrigger
    {
        public const int Count = 12;
        // HID usages 0x68..0x73 = F13..F24 (the firmware took them, echo verified 14.11.2026)
        public static readonly byte[] Usage = { 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F, 0x70, 0x71, 0x72, 0x73 };
        // Windows virtual keys F13..F24
        public static readonly int[] Vk = { 0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87 };

        public static string Name(int slot) { return "F" + (13 + slot); }

        public static int SlotOfVk(int vk)
        {
            for (int i = 0; i < Vk.Length; i++) if (Vk[i] == vk) return i;
            return -1;
        }

        public static int SlotOfUsage(byte usage)
        {
            for (int i = 0; i < Usage.Length; i++) if (Usage[i] == usage) return i;
            return -1;
        }
    }

    public class HostAction
    {
        public int Kind;            // index into HostKinds.Names
        public string Value = "";   // program / key sequence / text / keyword
        public int Hz = 20;         // turbo repeat rate
        public int Delay = 40;      // key hold time inside a sequence

        public bool IsDisabled { get { return Kind <= 0 || Value.Trim().Length == 0; } }

        // Why an action that has a kind will still not fire. The UI shows this instead of letting
        // the row look configured while the engine silently refuses it.
        public string Problem()
        {
            if (Kind <= 0) return "no action chosen";
            if (Value.Trim().Length == 0) return "value is empty";
            return "";
        }

        public HostAction Clone()
        {
            HostAction a = new HostAction();
            a.Kind = Kind; a.Value = Value; a.Hz = Hz; a.Delay = Delay;
            return a;
        }

        public string Describe()
        {
            if (Kind <= 0) return "disabled";
            // A hand-edited or corrupted table can carry a kind outside the enum; indexing the
            // name table with it used to throw IndexOutOfRangeException and take the UI down.
            if (Kind >= HostKinds.Names.Length) return "unknown action " + Kind + (Value.Length > 0 ? ": " + Value : "");
            string k = HostKinds.Names[Kind];
            switch (Kind)
            {
                case HostKinds.Turbo: return k + ": " + Value + " @ " + Hz + " Hz";
                case HostKinds.Keys: return k + ": " + Value;
                case HostKinds.Text: return k + ": " + (Value.Length > 40 ? Value.Substring(0, 40) + "..." : Value);
                default: return k + ": " + Value;
            }
        }
    }

    public static class HostKinds
    {
        public const int Disabled = 0;
        public const int Run = 1;
        public const int Keys = 2;
        public const int Text = 3;
        public const int Hold = 4;
        public const int Turbo = 5;
        public const int Toggle = 6;
        public const int Media = 7;
        public const int Power = 8;
        public const int Device = 9;
        // The Windows-command layer. These are NOT single hotkeys: each one drives the real
        // Win32 call, which is what makes them behave like Synapse's "Windows shortcuts".
        //   ThisPC      -> Shell:MyComputerFolder via launchFileExplorer  (Win+E opens "Home")
        //   CycleApps   -> the real Alt+Tab switcher, held long enough to travel
        //   SwitchApps  -> Alt+Tab and stop on the next window (what Alt+Tab normally does)
        //   CloseApp    -> WM_CLOSE to the foreground window
        public const int WindowsCmd = 10;

        public static readonly string[] Names = {
            "Disabled",
            "Run program / file / URL",
            "Send keys (sequence)",
            "Type text",
            "Hold keys while pressed",
            "Turbo (repeat while held)",
            "Toggle keys on / off",
            "Media key",
            "Power / system",
            "Device action (mouse)",
            "Windows command"
        };

        public static readonly string[] Help = {
            "",
            "Value = command line, document path or URL. Prefix with admin: to elevate.",
            "Value = sequence, e.g. \"ctrl+shift+esc\" or \"win+r wait:400 cmd wait:200 enter x2\".",
            "Value = literal text to type (Unicode is fine).",
            "Value = chord held down while the button is held (e.g. \"ctrl\" or \"shift\").",
            "Value = chord, Hz = repeats per second while held.",
            "Value = chord; first press holds it down, second press releases it.",
            "Value = volup, voldown, mute, play, next, prev, stop.",
            "Value = lock, sleep, monitoroff, showdesktop, taskmgr, snipping.",
            "Value = dpiup, dpidown, dpicycle, profnext, profprev, lighttoggle.",
            "Value = thispc, fileexplorer, userdir, settings, calculator, notepad, mspaint, " +
                "snipping, taskmgr, copilot, sysmenu, showdesktop, cycleapps, switchapps, " +
                "closeapp, cut, copy, paste, mail, refresh, lock, explorernew."
        };
    }

    // ---------------------------------------------------------------------------------------
    // Windows commands - the real Win32 call behind each of Synapse's "Windows shortcuts".
    //
    // Synapse does not press hotkeys for these: its native layer (sysUtilsNative.dll) calls
    // launchFileExplorer("shell:MyComputerFolder"), LaunchMicrosoftApp, msSettings(page),
    // LockWorkStation and so on. That is why Synapse's "This PC" really opens This PC, while a
    // plain Win+E opens Explorer's Start page ("Home"), and why its "Cycle apps" walks through
    // the windows while a single Alt+Tab tap only flashes the switcher.
    //
    // Rz3 now does the same thing here, in the host engine.
    // ---------------------------------------------------------------------------------------
    public static class WinCommands
    {
        public static readonly string[] Keys = {
            "taskview", "thispc", "fileexplorer", "userdir", "explorernew", "settings", "calculator",
            "notepad", "mspaint", "snipping", "taskmgr", "copilot", "sysmenu",
            "showdesktop", "cycleapps", "switchapps", "closeapp", "cut", "copy", "paste",
            "mail", "refresh", "lock"
        };

        public static readonly string[] Names = {
            "Task View (windows overview)", "This PC", "File Explorer", "User folder", "New Explorer window",
            "Windows Settings", "Calculator", "Notepad", "Paint", "Snipping Tool", "Task Manager",
            "Copilot", "Power user menu (Win+X)", "Show desktop", "Cycle apps", "Switch apps",
            "Close app", "Cut", "Copy", "Paste", "Mail", "Refresh", "Lock computer"
        };

        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool LockWorkStation();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string win);

        public static int IndexOf(string key)
        {
            if (key == null) return -1;
            string k = key.Trim().ToLowerInvariant();
            for (int i = 0; i < Keys.Length; i++) if (Keys[i] == k) return i;
            return -1;
        }

        // Runs one Windows command by key. Returns a short result line.
        public static string Run(string key)
        {
            string k = (key ?? "").Trim().ToLowerInvariant();
            try
            {
                switch (k)
                {
                    // ---- Windows' own overview ----
                    // Task View is a real, full-screen, interactive window owned by explorer.exe
                    // (class XamlExplorerHostIslandWindow, title "Task Switching"). It lives hidden
                    // between uses, so showing it is the same thing the taskbar button does.
                    // Measured 16.11.2026 (tools9/TaskViewClean.exe): ShowWindow is the one route
                    // that works from a background process here; Win+Tab and the tray WM_COMMANDs
                    // are ignored by the injected-input path, and the ShellExperienceHost app id
                    // does nothing for this window.
                    case "taskview": return TaskViewToggle();

                    // ---- folders: the path is handed to Explorer, never a bare hotkey ----
                    case "thispc": return OpenShell("shell:MyComputerFolder");
                    case "fileexplorer": return OpenShell("shell:MyComputerFolder");
                    case "userdir": return OpenShell("shell:Profile");
                    case "explorernew": return OpenShell("explorer.exe");
                    case "mail": return OpenShell("shell:AppsFolder\\microsoft.windowscommunicationsapps_8wekyb3d8bbwe!App");

                    // ---- settings: the real page, not Win+I (which opens the home page) ----
                    case "settings": return OpenShell("ms-settings:");
                    case "taskmgr": return OpenShell("taskmgr.exe");

                    // ---- packaged / classic apps: launched by path so they really start ----
                    case "calculator": return LaunchApp("calc.exe");
                    case "notepad": return LaunchApp("notepad.exe");
                    case "mspaint": return LaunchApp("mspaint.exe");
                    case "snipping": return LaunchApp("ms-screenclip:");
                    case "copilot": return OpenShell("ms-copilot:");

                    // ---- window management ----
                    case "showdesktop": return ShowDesktopReal();
                    case "cycleapps": return CycleApps(true);
                    case "switchapps": return CycleApps(false);
                    case "closeapp": return CloseForeground();
                    case "sysmenu":
                        // Win+X cannot be injected reliably; the menu itself can be posted.
                        keybd_event(0x5B, 0, 0, IntPtr.Zero);
                        keybd_event(0x58, 0, 0, IntPtr.Zero);
                        keybd_event(0x58, 0, 2, IntPtr.Zero);
                        keybd_event(0x5B, 0, 2, IntPtr.Zero);
                        return "power user menu";

                    // ---- clipboard / refresh target the foreground window ----
                    case "cut": return SendChord(0x11, 0x58);
                    case "copy": return SendChord(0x11, 0x43);
                    case "paste": return SendChord(0x11, 0x56);
                    case "refresh": return SendKey(0x74);                 // F5
                    case "lock": LockWorkStation(); return "workstation locked";
                }
            }
            catch (Exception ex) { return "failed: " + ex.Message; }
            return "unknown Windows command: " + k;
        }

        // Routes a target through the shell. Two distinct cases:
        //  * a shell namespace like shell:MyComputerFolder, or a protocol like ms-settings: - the
        //    reliable route is explorer.exe <target>, which never blocks;
        //  * a plain program name - CreateProcess with breakaway.
        // ---- Task View ----------------------------------------------------------------------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string win);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        // Finds the ShellExperienceHost "Task Switching" window. It is created once by explorer and
        // kept hidden, so the handle survives across calls.
        static IntPtr TaskViewWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                StringBuilder cn = new StringBuilder(128);
                GetClassName(h, cn, 128);
                if (cn.ToString() != "XamlExplorerHostIslandWindow") return true;
                StringBuilder tt = new StringBuilder(128);
                GetWindowText(h, tt, 128);
                if (tt.ToString().IndexOf("Task Switching", StringComparison.OrdinalIgnoreCase) < 0) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static bool TaskViewIsOpen()
        {
            IntPtr h = TaskViewWindow();
            return h != IntPtr.Zero && IsWindowVisible(h);
        }

        // Opens (or closes) Task View.
        static string TaskView(bool open)
        {
            IntPtr h = TaskViewWindow();
            if (h == IntPtr.Zero)
            {
                // No cached window: fall back to the hotkey, which works when this process is the
                // foreground one.
                SendChord(0x5B, 0x09);      // Win+Tab
                return "task view (hotkey fallback)";
            }
            if (open)
            {
                if (IsWindowVisible(h)) { ShowWindow(h, 0); return "task view closed"; }
                // Borrow foreground rights first: Windows only lets the foreground process take
                // activation, and Rz3 usually sits in the tray. Without this the overview appears
                // but the keyboard belongs to the previous window.
                try { AllowSetForegroundWindow(-1); } catch { }
                ShowWindow(h, 5);                                  // SW_SHOW
                SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002);   // TOPMOST
                SetWindowPos(h, (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002);   // NOTOPMOST
                SetForegroundWindow(h);
                return "task view opened";
            }
            ShowWindow(h, 0);
            return "task view closed";
        }

        // Toggles Task View: a second press closes it, which is what a "Task View" button should do.
        public static string TaskViewToggle()
        {
            return TaskView(!TaskViewIsOpen());
        }

        static string OpenShell(string what)
        {
            if (what.IndexOf("://", StringComparison.Ordinal) > 0 || what.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                string err2;
                int pidUrl = Launcher.Start(what, "", out err2);
                if (pidUrl > 0) return "opened " + what;
                // explorer hands the target to the registered handler without blocking
                int pidEx = Launcher.Start("explorer.exe", "\"" + what + "\"", out err2);
                if (pidEx > 0) return "opened " + what + " (explorer)";
                return "open failed: " + err2;
            }
            if (what.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (what.IndexOf('\\') < 0 && what.IndexOf('/') < 0)
                {
                    // bare program name: let the shell's App Paths resolve it, then CreateProcess
                    string resolved = ResolveAppPath(what);
                    string err;
                    int pid = Launcher.Start(resolved, "", out err);
                    if (pid > 0) return "launched " + what;
                    return "launch failed: " + err;
                }
            }
            // anything else (a folder, a document) goes through explorer so the handler decides
            string e3;
            int p3 = Launcher.Start("explorer.exe", "\"" + what + "\"", out e3);
            return p3 > 0 ? "opened " + what : "open failed: " + e3;
        }

        // Looks a bare program name up the way the shell does (App Paths, then PATH).
        static string ResolveAppPath(string exe)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(null);
                        if (v != null && File.Exists(v.ToString())) return v.ToString();
                    }
                }
            }
            catch { }
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(null);
                        if (v != null && File.Exists(v.ToString())) return v.ToString();
                    }
                }
            }
            catch { }
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string c = Path.Combine(sys, exe);
            if (File.Exists(c)) return c;
            return exe;
        }

        static string LaunchApp(string what)
        {
            return OpenShell(what);
        }

        // Win+D toggles the desktop in Explorer itself; posting the shell command is what makes
        // "Show desktop" work even when a full-screen window would swallow the hotkey.
        static string ShowDesktopReal()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                PostMessage(tray, 0x0111, (IntPtr)419, IntPtr.Zero);   // WM_COMMAND 419 = toggle desktop
                return "desktop toggled";
            }
            SendChord(0x5B, 0x44);
            return "desktop toggled (hotkey)";
        }

        // "Cycle apps" / "Switch apps".
        //
        // A single Alt+Tab tap only flashes the switcher and commits on release, so a rapid
        // press-release looks like "it does nothing" or, worse, like it walks every window. With
        // Task View available that is the better target: it is the same overview Synapse shows,
        // and it stays open so the user can pick a window with the mouse.
        static string CycleApps(bool multi)
        {
            // Prefer Task View when the window exists - it is stable and does not depend on
            // injected Alt/Tab timing.
            if (TaskViewWindow() != IntPtr.Zero)
                return TaskViewToggle();

            // Fallback: a real held Alt+Tab.
            keybd_event(0x12, 0, 0, IntPtr.Zero);            // Alt down
            Thread.Sleep(40);
            int taps = multi ? 2 : 1;
            for (int i = 0; i < taps; i++)
            {
                keybd_event(0x09, 0, 0, IntPtr.Zero);        // Tab down
                Thread.Sleep(60);
                keybd_event(0x09, 0, 2, IntPtr.Zero);        // Tab up
                Thread.Sleep(140);
            }
            Thread.Sleep(140);
            keybd_event(0x12, 0, 2, IntPtr.Zero);            // Alt up -> commits
            return multi ? "cycled apps (alt+tab)" : "switched app (alt+tab)";
        }

        static string CloseForeground()
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "no foreground window";
            PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
            return "close requested";
        }

        static string SendChord(byte mod, byte key)
        {
            keybd_event(mod, 0, 0, IntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(key, 0, 0, IntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(key, 0, 2, IntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(mod, 0, 2, IntPtr.Zero);
            return "sent 0x" + mod.ToString("x2") + "+0x" + key.ToString("x2");
        }

        static string SendKey(byte key)
        {
            keybd_event(key, 0, 0, IntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(key, 0, 2, IntPtr.Zero);
            return "sent key 0x" + key.ToString("x2");
        }
    }

    public static class HostStore
    {
        public static string Path_(string folder) { return Path.Combine(folder, "rz3_host.json"); }

        static string Esc(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static string Unesc(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 't') sb.Append('\t');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 'n') sb.Append('\n');
                    else if (n == '\\') sb.Append('\\');
                    else sb.Append(n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // Table: profile id -> 12 actions.
        public static bool Load(string folder, Dictionary<byte, HostAction[]> table, out bool enabled)
        {
            enabled = true;
            table.Clear();
            string path = Path_(folder);
            if (!File.Exists(path)) return false;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw;
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.StartsWith("ENABLED=")) { enabled = line.Substring(8).Trim() != "0"; continue; }
                    if (line.StartsWith("RZ3HOST")) continue;
                    string[] p = line.Split(new char[] { '\t' });
                    if (p.Length < 6) continue;
                    byte pid; int slot; int kind; int hz; int delay;
                    if (!byte.TryParse(p[0], out pid)) continue;
                    if (!int.TryParse(p[1], out slot)) continue;
                    if (!int.TryParse(p[2], out kind)) continue;
                    if (!int.TryParse(p[3], out hz)) continue;
                    if (!int.TryParse(p[4], out delay)) continue;
                    if (slot < 0 || slot >= HostTrigger.Count) continue;
                    // Reject a kind outside the enum instead of keeping it: the engine cannot run
                    // it and every reader of the table would have to defend against it.
                    if (kind < 0 || kind >= HostKinds.Names.Length) continue;
                    HostAction[] arr;
                    if (!table.TryGetValue(pid, out arr)) { arr = new HostAction[HostTrigger.Count]; table[pid] = arr; }
                    HostAction a = new HostAction();
                    a.Kind = kind; a.Hz = hz; a.Delay = delay; a.Value = Unesc(p[5]);
                    arr[slot] = a;
                }
                return true;
            }
            catch { return false; }
        }

        public static void Save(string folder, Dictionary<byte, HostAction[]> table, bool enabled)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Rz3 host action table (profile, slot, kind, hz, delay, value)");
                sb.AppendLine("RZ3HOST 1");
                sb.AppendLine("ENABLED=" + (enabled ? "1" : "0"));
                List<byte> ids = new List<byte>(table.Keys);
                ids.Sort();
                foreach (byte pid in ids)
                {
                    HostAction[] arr = table[pid];
                    if (arr == null) continue;
                    for (int s = 0; s < HostTrigger.Count && s < arr.Length; s++)
                    {
                        HostAction a = arr[s];
                        if (a == null || a.Kind <= 0) continue;
                        sb.AppendLine(pid + "\t" + s + "\t" + a.Kind + "\t" + a.Hz + "\t" + a.Delay + "\t" + Esc(a.Value));
                    }
                }
                File.WriteAllText(Path_(folder), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // Injects keys the way a keyboard would. Magic = our own events, so the engine (and the
    // optional hook a future revision may add) never reacts to its own output.
    public static class Inject
    {
        public const uint Magic = 0x525A3301;
        // Dry run switch for --logictest: everything is parsed, nothing is sent.
        public static bool Enabled = true;

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx; public int dy; public uint mouseData; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_UNICODE = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        static bool Send(INPUT[] input)
        {
            INPUT[] one = input;
            uint sent = SendInput((uint)one.Length, one, Marshal.SizeOf(typeof(INPUT)));
            return sent == one.Length;
        }

        public static void KeyDown(int vk) { Key(vk, true); }
        public static void KeyUp(int vk) { Key(vk, false); }

        public static void Key(int vk, bool down)
        {
            if (!Enabled) return;
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = (ushort)vk;
            i.u.ki.wScan = 0;
            i.u.ki.dwFlags = down ? 0u : KEYEVENTF_KEYUP;
            i.u.ki.time = 0;
            i.u.ki.dwExtraInfo = (IntPtr)Magic;
            INPUT[] arr = new INPUT[1];
            arr[0] = i;
            if (!Send(arr))
            {
                // UIPI refused SendInput (elevated foreground window): the old API still works.
                keybd_event((byte)vk, 0, down ? 0u : KEYEVENTF_KEYUP, (IntPtr)Magic);
            }
        }

        public static void Tap(int vk, int holdMs)
        {
            Key(vk, true);
            Thread.Sleep(Math.Max(6, holdMs));
            Key(vk, false);
        }

        public static void UnicodeChar(char c)
        {
            if (!Enabled) return;
            INPUT down = new INPUT();
            down.type = INPUT_KEYBOARD;
            down.u.ki.wVk = 0;
            down.u.ki.wScan = c;
            down.u.ki.dwFlags = KEYEVENTF_UNICODE;
            down.u.ki.dwExtraInfo = (IntPtr)Magic;
            INPUT up = down;
            up.u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            INPUT[] arr = new INPUT[2];
            arr[0] = down; arr[1] = up;
            if (!Send(arr)) keybd_event(0, (byte)(c & 0xFF), KEYEVENTF_UNICODE, (IntPtr)Magic);
            Thread.Sleep(2);
        }
    }

    public static class VkTable
    {
        static readonly Dictionary<string, int> _map = new Dictionary<string, int>();

        static void Add(string name, int vk) { _map[name] = vk; }

        static VkTable()
        {
            for (int c = 'a'; c <= 'z'; c++) Add(((char)c).ToString(), 0x41 + (c - 'a'));
            for (int d = 0; d <= 9; d++) Add(d.ToString(), 0x30 + d);
            for (int f = 1; f <= 24; f++) Add("f" + f, 0x6F + f);
            Add("ctrl", 0x11); Add("control", 0x11);
            Add("shift", 0x10); Add("alt", 0x12); Add("win", 0x5B); Add("lwin", 0x5B);
            Add("enter", 0x0D); Add("return", 0x0D); Add("esc", 0x1B); Add("escape", 0x1B);
            Add("tab", 0x09); Add("space", 0x20); Add("backspace", 0x08); Add("bksp", 0x08);
            Add("delete", 0x2E); Add("del", 0x2E); Add("insert", 0x2D); Add("ins", 0x2D);
            Add("home", 0x24); Add("end", 0x23); Add("pageup", 0x21); Add("pagedown", 0x22);
            Add("left", 0x25); Add("up", 0x26); Add("right", 0x27); Add("down", 0x28);
            Add("capslock", 0x14); Add("numlock", 0x90); Add("scrolllock", 0x91);
            Add("printscreen", 0x2C); Add("prtsc", 0x2C); Add("pause", 0x13);
            Add("apps", 0x5D); Add("menu", 0x5D);
            Add("volup", 0xAF); Add("voldown", 0xAE); Add("mute", 0xAD);
            Add("play", 0xB3); Add("next", 0xB0); Add("prev", 0xB1); Add("stop", 0xB2);
            Add("-", 0xBD); Add("=", 0xBB); Add("[", 0xDB); Add("]", 0xDD); Add("\\", 0xDC);
            Add(";", 0xBA); Add("'", 0xDE); Add(",", 0xBC); Add(".", 0xBE); Add("/", 0xBF);
            Add("`", 0xC0); Add("plus", 0xBB);
        }

        public static bool TryGet(string name, out int vk)
        {
            vk = 0;
            string k = (name ?? "").Trim().ToLowerInvariant();
            if (k.Length == 0) return false;
            return _map.TryGetValue(k, out vk);
        }

        public static string NameOf(int vk)
        {
            foreach (KeyValuePair<string, int> kv in _map) if (kv.Value == vk) return kv.Key;
            return "vk" + vk.ToString("x2");
        }
    }

    // Starts programs. Plain Process.Start (ShellExecute) inherits the job object of whatever
    // started Rz3, and a job that closes takes the children with it - the symptom is a program
    // that "launches" and vanishes. CreateProcess with CREATE_BREAKAWAY_FROM_JOB escapes that,
    // which is also what the shell's own "start" does.
    public static class Launcher
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
            public int dwX; public int dwY; public int dwXSize; public int dwYSize;
            public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags;
            public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2;
            public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
            uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("user32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

        [DllImport("user32.dll")]
        static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc cb, IntPtr l);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        delegate bool EnumProc(IntPtr hwnd, IntPtr l);

        // A program started from a background process is created *behind* the current window:
        // the user presses a button and "nothing happens". Pull the new window forward.
        public static void BringToFront(int pid)
        {
            if (pid <= 0) return;
            Thread t = new Thread(delegate ()
            {
                for (int i = 0; i < 25; i++)
                {
                    Thread.Sleep(80);
                    IntPtr hwnd = IntPtr.Zero;
                    try
                    {
                        Process p = Process.GetProcessById(pid);
                        p.Refresh();
                        hwnd = p.MainWindowHandle;
                    }
                    catch { return; }
                    if (hwnd == IntPtr.Zero) hwnd = FindTopWindow(pid);
                    if (hwnd == IntPtr.Zero) continue;
                    try
                    {
                        ShowWindow(hwnd, 9);       // SW_RESTORE
                        uint dummy;
                        uint other = GetWindowThreadProcessId(hwnd, out dummy);
                        // Foreground lock: borrowing the target's input queue makes the switch legal.
                        bool attached = AttachThreadInput(GetCurrentThreadId(), other, true);
                        bool ok = SetForegroundWindow(hwnd);
                        if (attached) AttachThreadInput(GetCurrentThreadId(), other, false);
                        if (!ok && GetForegroundWindow() != hwnd)
                        {
                            SwitchToThisWindow(hwnd, true);
                        }
                        // Raise it either way: even without activation rights the window must be
                        // visible above the current one, otherwise the button looks dead.
                        SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);  // TOPMOST
                        SetWindowPos(hwnd, (IntPtr)(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);  // NOTOPMOST
                    }
                    catch { }
                    return;
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static IntPtr FindTopWindow(int pid)
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint p;
                    GetWindowThreadProcessId(h, out p);
                    if (p != (uint)pid) return true;
                    // Skip the invisible IME/cloak windows a process always owns: activating those
                    // makes the new program look like "nothing happened".
                    if (!IsWindowVisible(h)) return true;
                    StringBuilder sb = new StringBuilder(8);
                    GetWindowText(h, sb, 8);
                    if (sb.Length == 0) return true;
                    found = h;
                    return false;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        // A child that inherits our console dies with it (Rz3 may itself have been started from a
        // console that is long gone), so give every target its own, detached console.
        const uint DETACHED_PROCESS = 0x00000008;

        static STARTUPINFO NewStartInfo()
        {
            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.dwFlags = 1;         // STARTF_USESHOWWINDOW
            si.wShowWindow = 1;     // SW_SHOWNORMAL
            return si;
        }

        // Returns the new process id, or -1 with a reason.
        public static int Start(string file, string args, out string error)
        {
            error = "";
            string cmdline = "\"" + file + "\"" + (args.Length > 0 ? " " + args : "");
            // Let the child come to the front: a background process normally cannot take focus.
            try { AllowSetForegroundWindow(-1); } catch { }
            STARTUPINFO si = NewStartInfo();
            PROCESS_INFORMATION pi;
            uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS;
            bool ok = CreateProcess(null, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, null, ref si, out pi);
            if (!ok)
            {
                // The surrounding job may forbid breakaway; a plain child is still better than none.
                si = NewStartInfo();
                ok = CreateProcess(null, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | DETACHED_PROCESS, IntPtr.Zero, null, ref si, out pi);
            }
            if (!ok)
            {
                si = NewStartInfo();
                ok = CreateProcess(null, new StringBuilder(cmdline), IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, null, ref si, out pi);
            }
            if (!ok)
            {
                error = "CreateProcess failed, win32=" + Marshal.GetLastWin32Error();
                return -1;
            }
            int pid = pi.dwProcessId;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pid;
        }

        public static bool LooksLikeProgram(string file)
        {
            if (file == null) return false;
            if (file.IndexOf("://", StringComparison.Ordinal) > 0) return false;   // URL
            string ext = Path.GetExtension(file);
            if (ext == null) return false;
            ext = ext.ToLowerInvariant();
            if (ext == ".exe" || ext == ".com") return true;
            if (ext == ".bat" || ext == ".cmd") return true;
            if (ext.Length == 0 && !File.Exists(file)) return true;    // bare name, PATH lookup
            return false;
        }
    }

    public class HostEngine : IDisposable
    {
        const int WM_INPUT = 0x00FF;
        const uint RID_INPUT = 0x10000003;
        const uint RIDEV_INPUTSINK = 0x00000100;
        const int WH_KEYBOARD_LL = 13;

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER { public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam; }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWKEYBOARD
        {
            public ushort MakeCode; public ushort Flags; public ushort Reserved;
            public ushort VKey; public uint Message; public uint ExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        [DllImport("powrprof.dll", SetLastError = true)]
        static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

        public bool Enabled = true;
        public bool Attached { get; private set; }
        public string Status = "not started";
        public event Action<string> Logged;

        public Func<string, string> DeviceAction;   // set by the form; runs on the UI thread

        readonly Dictionary<int, HostAction> _table = new Dictionary<int, HostAction>();
        readonly Dictionary<int, Thread> _turbo = new Dictionary<int, Thread>();
        readonly Dictionary<int, bool> _turboRun = new Dictionary<int, bool>();
        readonly Dictionary<int, bool> _latched = new Dictionary<int, bool>();
        readonly Dictionary<int, long> _lastDown = new Dictionary<int, long>();
        readonly Queue<int[]> _queue = new Queue<int[]>();
        readonly object _lock = new object();
        readonly AutoResetEvent _signal = new AutoResetEvent(false);
        Thread _worker;
        volatile bool _stop;

        // Low level keyboard hook: the working input path on this machine. Raw Input with
        // RIDEV_INPUTSINK does not deliver the triggers while another window is focused here,
        // while WH_KEYBOARD_LL sees them (and can swallow them so the app never gets F13).
        IntPtr _hook = IntPtr.Zero;
        HookProc _proc;
        int _lastHookTick;
        int _lastHookAnyKey;      // any key the hook delivered, trigger or not (raw-input stand-down)
        public bool Hooked { get { return _hook != IntPtr.Zero; } }

        // Diagnostics: every key event the callback is handed, including the ones it ignores.
        // The UI uses this to tell "the hook is alive but the firmware never sends F13" apart from
        // "the hook is dead", which is the difference between a mapping bug and an input bug.
        public int HookEvents { get { return _hookEvents; } }
        public int HookTriggerEvents { get { return _hookTriggers; } }
        volatile int _hookEvents, _hookTriggers;
        public DateTime LastHookEvent = DateTime.MinValue;
        public DateTime LastTriggerEvent = DateTime.MinValue;

        public HostEngine()
        {
            _worker = new Thread(Worker);
            _worker.IsBackground = true;
            _worker.Name = "rz3-host-actions";
            _worker.Start();
        }

        public bool Attach(IntPtr hwnd)
        {
            _hwnd = hwnd;
            RaiseHookTimeout();
            // The keyboard hook lives on a thread of its own with its own message loop. A low level
            // hook is delivered to the thread that installed it, and that thread only receives
            // events while it is pumping. Installing it on the UI thread tied it to the form: as
            // soon as the window was minimised into the tray the buttons went dead (measured
            // 15.11.2026 in rz3_host.log: "F13 down" while visible, silence after EnterTray).
            StartHookThread();
            // 2) raw input (fallback when the hook cannot be installed or is dropped by Windows)
            RAWINPUTDEVICE[] d = new RAWINPUTDEVICE[1];
            d[0].UsagePage = 1;           // generic desktop
            d[0].Usage = 6;               // keyboard
            d[0].Flags = RIDEV_INPUTSINK; // receive events even when another window has focus
            d[0].Target = hwnd;
            Attached = RegisterRawInputDevices(d, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            if (_hook != IntPtr.Zero) Status = "running (keyboard hook, own thread)";
            else if (Attached) Status = "running (raw input only)";
            else Status = "failed: no hook, raw input err " + Marshal.GetLastWin32Error();
            return _hook != IntPtr.Zero || Attached;
        }

        // Own thread + own message loop for the hook. Signals _hookReady once the hook handle is
        // installed so Attach can report the real state synchronously.
        Thread _hookThread;
        readonly ManualResetEvent _hookReady = new ManualResetEvent(false);

        void StartHookThread()
        {
            if (_hookThread != null) return;
            _hookThread = new Thread(HookThreadProc);
            _hookThread.IsBackground = true;
            _hookThread.Name = "rz3-keyboard-hook";
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
            _hookReady.WaitOne(2000);
        }

        void HookThreadProc()
        {
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            _hookReady.Set();
            if (_hook == IntPtr.Zero) return;

            // A message loop is what keeps the hook fed. UnhookWindowsHookEx/PostThreadMessage on
            // shutdown breaks it out; the loop itself never blocks the callback path.
            MSG msg;
            while (!_stop)
            {
                if (PeekMessage(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                {
                    if (msg.message == WM_QUIT) break;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                else Thread.Sleep(2);
            }
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }

        const uint PM_REMOVE = 0x0001;
        const uint WM_QUIT = 0x0012;

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
            public uint time; public int ptX, ptY;
        }

        [DllImport("user32.dll")]
        static extern bool PeekMessage(out MSG m, IntPtr hWnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG m);

        IntPtr _hwnd;

        // Windows removes a low level hook whose callback takes longer than LowLevelHooksTimeout
        // (300 ms by default). The engine's callback only parses and enqueues, but one garbage
        // collection pause under load is enough to lose every button silently, so the timeout is
        // raised once per process (HKCU, no administrator needed).
        static bool _timeoutRaised;
        static void RaiseHookTimeout()
        {
            if (_timeoutRaised) return;
            _timeoutRaised = true;
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\Desktop", true))
                {
                    if (k == null) return;
                    object v = k.GetValue("LowLevelHooksTimeout");
                    int cur = 0;
                    if (v is int) cur = (int)v;
                    else if (v != null) int.TryParse(v.ToString(), out cur);
                    if (cur < 5000) k.SetValue("LowLevelHooksTimeout", 5000, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        // Windows silently drops a low level hook whose callback misses LowLevelHooksTimeout, and
        // an Explorer restart can lose the icon/hook pairing. Re-installing is cheap and harmless
        // (an existing valid hook is left alone), so the UI calls this from a timer.
        public bool EnsureAttached()
        {
            if (_hook == IntPtr.Zero)
            {
                // the hook thread died or lost the handle: start a fresh one
                _hookThread = null;
                _hookReady.Reset();
                StartHookThread();
                if (_hook != IntPtr.Zero) Status = "running (keyboard hook, own thread)";
                return _hook != IntPtr.Zero;
            }
            return true;
        }

        // True when the hook has been silent for longer than any plausible idle period while the
        // user is clearly active is NOT something we can know; instead this reports whether the
        // hook handle still exists, which is what Windows drops first.
        public bool HookAlive { get { return _hook != IntPtr.Zero; } }

        // Runs on the UI thread: parse, enqueue, swallow. Never block here - Windows drops a hook
        // whose callback takes too long (LowLevelHooksTimeout).
        IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && Enabled)
                {
                    int msg = (int)wParam;
                    bool down = msg == 0x0100 || msg == 0x0104;
                    bool up = msg == 0x0101 || msg == 0x0105;
                    if (down || up)
                    {
                        KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        _hookEvents++;
                        LastHookEvent = DateTime.Now;
                        _lastHookAnyKey = Environment.TickCount;
                        if (k.dwExtraInfo != (IntPtr)Inject.Magic)
                        {
                            int slot = HostTrigger.SlotOfVk((int)k.vkCode);
                            if (slot >= 0)
                            {
                                _hookTriggers++;
                                LastTriggerEvent = DateTime.Now;
                                bool known;
                                lock (_lock) { known = _table.ContainsKey(slot); }
                                if (known)
                                {
                                    _lastHookTick = Environment.TickCount;
                                    Enqueue(slot, down);
                                    return (IntPtr)1;      // swallow: the trigger never reaches the app
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void SetTable(Dictionary<int, HostAction> table)
        {
            lock (_lock)
            {
                _table.Clear();
                if (table != null)
                    foreach (KeyValuePair<int, HostAction> kv in table)
                        if (kv.Value != null && !kv.Value.IsDisabled) _table[kv.Key] = kv.Value.Clone();
            }
        }

        public string DescribeTable()
        {
            StringBuilder sb = new StringBuilder();
            lock (_lock)
            {
                foreach (KeyValuePair<int, HostAction> kv in _table)
                    sb.AppendLine(HostTrigger.Name(kv.Key) + " -> " + kv.Value.Describe());
            }
            return sb.Length == 0 ? "(no host actions defined for the active profile)" : sb.ToString();
        }

        public void HandleRawInput(IntPtr lParam)
        {
            if (!Attached) return;
            // The hook is the authoritative path *while it is delivering key events at all*.
            // Watching _lastHookTick (trigger-only) was wrong: a hook that sees ordinary typing but
            // never the trigger still suppressed raw input, so the fallback could not work. The
            // fallback now stands down only when the hook itself is clearly alive and has just
            // delivered a key event, and the 35 ms dedupe below keeps a double path harmless.
            if (_hook != IntPtr.Zero && Environment.TickCount - _lastHookAnyKey < 5000) return;
            uint size = 0;
            uint header = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header);
            if (size == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint got = size;
                if (GetRawInputData(lParam, RID_INPUT, buf, ref got, header) != got) return;
                RAWINPUTHEADER h = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                if (h.Type != 1) return;    // 1 = keyboard
                RAWKEYBOARD k = (RAWKEYBOARD)Marshal.PtrToStructure(
                    new IntPtr(buf.ToInt64() + Marshal.SizeOf(typeof(RAWINPUTHEADER))), typeof(RAWKEYBOARD));
                if (k.ExtraInformation == Inject.Magic) return;   // our own output
                int slot = HostTrigger.SlotOfVk(k.VKey);
                if (slot < 0) return;
                bool down = (k.Flags & 1) == 0;                   // bit 0 = RI_KEY_BREAK (key up)
                lock (_lock)
                {
                    if (!_table.ContainsKey(slot)) return;
                    if (down)
                    {
                        long now = DateTime.Now.Ticks / 10000;
                        long prev;
                        if (_lastDown.TryGetValue(slot, out prev) && now - prev < 35) return;
                        _lastDown[slot] = now;
                    }
                }
                Enqueue(slot, down);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Fires a trigger through the same queue the hook uses (lab tool: --fire).
        public void Trigger(int slot)
        {
            Enqueue(slot, true);
            Enqueue(slot, false);
        }

        void Enqueue(int slot, bool down)
        {
            int[] item = new int[2];
            item[0] = slot;
            item[1] = down ? 1 : 0;
            lock (_lock) { _queue.Enqueue(item); }
            _signal.Set();
        }

        void Worker()
        {
            while (!_stop)
            {
                int[] item = null;
                lock (_lock) { if (_queue.Count > 0) item = _queue.Dequeue(); }
                if (item == null) { _signal.WaitOne(250); continue; }
                try
                {
                    HostAction a;
                    lock (_lock) { _table.TryGetValue(item[0], out a); }
                    if (a == null || !Enabled) continue;

                    // Hold-to-repeat guard. A key held down produces a stream of "down" events, and
                    // for a one-shot action (Windows command, launch, task view) that meant the
                    // action fired again and again - which is exactly how "cycle apps" ended up
                    // flicking through every window. Only actions that are meant to be held (Hold,
                    // Turbo) see the repeats; everything else fires once until the key goes up.
                    bool isDown = item[1] == 1;
                    bool repeatable = a.Kind == HostKinds.Hold || a.Kind == HostKinds.Turbo;
                    if (isDown && !repeatable)
                    {
                        bool alreadyDown;
                        lock (_lock) { alreadyDown = _down.Contains(item[0]); if (!alreadyDown) _down.Add(item[0]); }
                        if (alreadyDown) continue;          // auto-repeat: ignore
                    }
                    else if (!isDown)
                    {
                        lock (_lock) { _down.Remove(item[0]); }
                    }

                    if (isDown) LogLine(HostTrigger.Name(item[0]) + " down -> " + a.Describe());
                    Run(item[0], a, isDown);
                }
                catch (Exception ex) { LogLine("action failed: " + ex.Message); }
            }
        }

        // Slots whose key is currently held down (auto-repeat suppression).
        readonly HashSet<int> _down = new HashSet<int>();

        void LogLine(string s)
        {
            Action<string> h = Logged;
            if (h != null) { try { h(s); } catch { } }
        }

        // Runs one action. Exposed so the UI "Test" button exercises the same path.
        public string Run(int slot, HostAction a, bool down)        {
            int[] chord;
            switch (a.Kind)
            {
                case HostKinds.Run:
                    if (!down) return "";
                    try
                    {
                        int lpid = StartTarget(a.Value);
                        if (lpid > 0) WatchLiveness(lpid, a.Value);
                        LogLine("launched " + a.Value);
                        return "launched " + a.Value;
                    }
                    catch (Exception ex) { LogLine("launch failed: " + ex.Message); return "launch failed: " + ex.Message; }

                case HostKinds.Keys:
                    if (!down) return "";
                    ThreadPool.QueueUserWorkItem(delegate { SendSequence(a.Value, a.Delay); });
                    return "sequence sent";

                case HostKinds.Text:
                    if (!down) return "";
                    ThreadPool.QueueUserWorkItem(delegate { TypeText(a.Value); });
                    return "text sent";

                case HostKinds.Hold:
                    if (!ParseChord(a.Value, out chord)) return "bad chord: " + a.Value;
                    // Down presses and stays pressed; the release of the trigger releases it.
                    if (down)
                    {
                        for (int i = 0; i < chord.Length; i++) { Inject.KeyDown(chord[i]); Thread.Sleep(8); }
                    }
                    else
                    {
                        for (int i = chord.Length - 1; i >= 0; i--) { Inject.KeyUp(chord[i]); Thread.Sleep(8); }
                    }
                    return (down ? "holding " : "released ") + a.Value;

                case HostKinds.Turbo:
                    if (!ParseChord(a.Value, out chord)) return "bad chord: " + a.Value;
                    if (down) StartTurbo(slot, chord, a); else StopTurbo(slot);
                    return (down ? "turbo on" : "turbo off");

                case HostKinds.Toggle:
                    if (!down) return "";
                    if (!ParseChord(a.Value, out chord)) return "bad chord: " + a.Value;
                    ToggleChord(slot, chord);
                    return "toggle " + a.Value;

                case HostKinds.Media:
                    if (!down) return "";
                    {
                        int vk;
                        if (!VkTable.TryGet(a.Value, out vk)) return "unknown media key: " + a.Value;
                        Inject.Tap(vk, 20);
                        return "media " + a.Value;
                    }

                case HostKinds.Power:
                    if (!down) return "";
                    return DoPower(a.Value);

                case HostKinds.Device:
                    if (!down) return "";
                    {
                        Func<string, string> f = DeviceAction;
                        if (f == null) return "no device hook";
                        return f(a.Value);
                    }

                case HostKinds.WindowsCmd:
                    if (!down) return "";
                    {
                        string res = WinCommands.Run(a.Value);
                        LogLine("win " + a.Value + ": " + res);
                        return res;
                    }
            }
            return "";
        }

        // ---- sequence / chord helpers ----------------------------------------

        public static bool ParseChord(string text, out int[] vks)
        {
            List<int> list = new List<int>();
            vks = null;
            if (text == null) return false;
            string[] parts = text.Split(new char[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            foreach (string p in parts)
            {
                int vk;
                if (!VkTable.TryGet(p, out vk)) return false;
                if (!list.Contains(vk)) list.Add(vk);
            }
            vks = list.ToArray();
            return vks.Length > 0;
        }

        static void Chord(int[] vks, bool down, int holdMs)
        {
            if (down)
            {
                for (int i = 0; i < vks.Length; i++) { Inject.KeyDown(vks[i]); Thread.Sleep(8); }
                Thread.Sleep(Math.Max(10, holdMs));
                for (int i = vks.Length - 1; i >= 0; i--) { Inject.KeyUp(vks[i]); Thread.Sleep(8); }
            }
            else
            {
                for (int i = vks.Length - 1; i >= 0; i--) { Inject.KeyUp(vks[i]); Thread.Sleep(8); }
            }
        }

        // Sequence grammar:  chord | wait:ms (or w:ms) | chord xN | xN
        //
        // The documented, user-facing form is "tab x3" - a key and a repeat count separated by a
        // space. Splitting on spaces produced the tokens "tab" and "x3", and a standalone "x3"
        // was then reported as an unknown key, so the promise in the UI hint ("tab x3") did not
        // work. Both spellings are now accepted:
        //      "tab x3"  -> tab, then x3 = repeat the previous chord 3 more times
        //      "tabx3"   -> tab repeated 3 times (the old glued form, still supported)
        // Example: "win+r wait:400 cmd wait:300 enter", "tab x3 esc"
        public void SendSequence(string spec, int holdMs)
        {
            if (spec == null) return;
            string[] tokens = spec.Split(new char[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            int[] lastChord = null;
            foreach (string raw in tokens)
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                string lower = t.ToLowerInvariant();

                if (lower.StartsWith("wait:") || lower.StartsWith("w:"))
                {
                    int ms = 200;
                    int.TryParse(lower.Substring(lower.IndexOf(':') + 1), out ms);
                    Thread.Sleep(Math.Max(0, Math.Min(60000, ms)));
                    continue;
                }

                // A standalone "xN" repeats the chord that was just sent.
                if (lower.Length > 1 && lower[0] == 'x')
                {
                    int extra;
                    if (int.TryParse(lower.Substring(1), out extra) && extra > 0 && extra <= 50)
                    {
                        if (lastChord == null) { LogLine("repeat without a preceding key: " + t); continue; }
                        for (int r = 0; r < extra; r++) Chord(lastChord, true, holdMs);
                        Thread.Sleep(30);
                        continue;
                    }
                }

                // A suffix "xN" glued to the chord repeats that chord N times in total.
                int repeat = 1;
                int x = lower.LastIndexOf('x');
                if (x > 0 && x < lower.Length - 1)
                {
                    int n;
                    if (int.TryParse(lower.Substring(x + 1), out n) && n > 0 && n <= 50)
                    {
                        repeat = n;
                        t = t.Substring(0, x);
                    }
                }

                int[] chord;
                if (!ParseChord(t, out chord)) { LogLine("unknown token: " + t); continue; }
                for (int r = 0; r < repeat; r++) Chord(chord, true, holdMs);
                Thread.Sleep(30);
                lastChord = chord;
            }
        }

        public void TypeText(string text)
        {
            if (text == null) return;
            foreach (char c in text)
            {
                if (c == '\n') { Inject.Tap(0x0D, 20); continue; }
                if (c == '\t') { Inject.Tap(0x09, 20); continue; }
                Inject.UnicodeChar(c);
            }
        }

        // Starts a target and returns the new process id (0 when the shell owns it).
        int StartTarget(string target)
        {
            string cmd = target.Trim();
            bool admin = false;
            if (cmd.StartsWith("admin:", StringComparison.OrdinalIgnoreCase)) { admin = true; cmd = cmd.Substring(6).Trim(); }
            if (cmd.Length == 0) throw new ArgumentException("empty target");

            // "program.exe|arguments" keeps a plain URL/path working without a second field.
            string file = cmd;
            string args = "";
            int bar = cmd.IndexOf('|');
            if (bar > 0) { file = cmd.Substring(0, bar).Trim(); args = cmd.Substring(bar + 1).Trim(); }

            if (admin)
            {
                ProcessStartInfo up = new ProcessStartInfo();
                up.FileName = file;
                up.Arguments = args;
                up.UseShellExecute = true;
                up.Verb = "runas";
                Process.Start(up);
                return 0;
            }
            if (Launcher.LooksLikeProgram(file))
            {
                string runFile = file;
                string runArgs = args;
                string ext = (Path.GetExtension(file) ?? "").ToLowerInvariant();
                if (ext == ".bat" || ext == ".cmd")
                {
                    runArgs = "/c \"" + file + "\"" + (args.Length > 0 ? " " + args : "");
                    runFile = "cmd.exe";
                }
                string err;
                int pid = Launcher.Start(runFile, runArgs, out err);
                if (pid <= 0)
                {
                    StartViaShell(file, args);
                    return 0;
                }
                // Some targets (notably packaged apps such as Notepad or Calculator) die at once
                // when started with CreateProcess and only come up through the shell. One quick
                // liveness check decides which way this target needed.
                Thread.Sleep(400);
                bool dead = false;
                try { Process p = Process.GetProcessById(pid); p.Refresh(); dead = p.HasExited; }
                catch { dead = true; }
                if (dead)
                {
                    StartViaShell(file, args);
                    return 0;
                }
                Launcher.BringToFront(pid);
                return pid;
            }
            // Documents and URLs go through the shell so the registered handler opens them.
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = file;
            psi.Arguments = args;
            psi.UseShellExecute = true;
            Process psiP = Process.Start(psi);
            return psiP != null ? psiP.Id : 0;
        }

        // Also used by --runtest to check the launcher without a physical press.
        // Last resort: let the shell's own "start" resolve and activate the target.
        static void StartViaShell(string file, string args)
        {
            string cmdArgs = "/c start \"\" \"" + file + "\"" + (args.Length > 0 ? " " + args : "");
            string err;
            if (Launcher.Start("cmd.exe", cmdArgs, out err) <= 0)
                throw new InvalidOperationException(err);
        }

        // Logs whether a launched program is still there two seconds later: that is what tells a
        // working launch apart from one that a job object or a shell quirk swallowed.
        void WatchLiveness(int pid, string what)
        {
            Thread t = new Thread(delegate()
            {
                Thread.Sleep(2000);
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
                LogLine("2 s later: " + what + " pid " + pid + (alive ? " alive" : " GONE") +
                    (title.Length > 0 ? " title='" + title + "'" : ""));
            });
            t.IsBackground = true;
            t.Start();
        }

        public int Launch(string target, out string error)
        {
            error = "";
            try { return StartTarget(target); }
            catch (Exception ex) { error = ex.Message; return -1; }
        }

        void StartTurbo(int slot, int[] chord, HostAction a)
        {
            StopTurbo(slot);
            int interval = Math.Max(2, 1000 / Math.Max(1, a.Hz));
            lock (_lock) { _turboRun[slot] = true; }
            Thread t = new Thread(delegate ()
            {
                bool run;
                lock (_lock) { _turboRun.TryGetValue(slot, out run); }
                while (run && !_stop)
                {
                    Chord(chord, true, Math.Max(10, Math.Min(interval / 2, 40)));
                    int slept = 0;
                    while (slept < interval)
                    {
                        Thread.Sleep(5); slept += 5;
                        lock (_lock) { _turboRun.TryGetValue(slot, out run); }
                        if (!run) break;
                    }
                }
                Chord(chord, false, 10);
            });
            t.IsBackground = true;
            lock (_lock) { _turbo[slot] = t; }
            t.Start();
        }

        void StopTurbo(int slot)
        {
            lock (_lock) { if (_turboRun.ContainsKey(slot)) _turboRun[slot] = false; }
        }

        void ToggleChord(int slot, int[] chord)
        {
            bool on = false;
            lock (_lock) { _latched.TryGetValue(slot, out on); }
            if (on)
            {
                for (int i = chord.Length - 1; i >= 0; i--) { Inject.KeyUp(chord[i]); Thread.Sleep(8); }
                lock (_lock) { _latched[slot] = false; }
                LogLine("toggle off " + string.Join("+", Names(chord)));
            }
            else
            {
                for (int i = 0; i < chord.Length; i++) { Inject.KeyDown(chord[i]); Thread.Sleep(8); }
                lock (_lock) { _latched[slot] = true; }
                LogLine("toggle on " + string.Join("+", Names(chord)));
            }
        }

        static string[] Names(int[] vks)
        {
            string[] s = new string[vks.Length];
            for (int i = 0; i < vks.Length; i++) s[i] = VkTable.NameOf(vks[i]);
            return s;
        }

        string DoPower(string what)
        {
            string w = (what ?? "").Trim().ToLowerInvariant();
            try
            {
                switch (w)
                {
                    case "lock": LockWorkStation(); return "workstation locked";
                    case "sleep": SetSuspendState(false, false, false); return "sleeping";
                    case "monitoroff":
                        {
                            IntPtr res;
                            SendMessageTimeout((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2, 2, 500, out res);
                            return "monitor off";
                        }
                    case "showdesktop": SendSequence("win+d", 30); return "show desktop";
                    case "taskmgr": SendSequence("ctrl+shift+esc", 30); return "task manager";
                    case "snipping": SendSequence("win+shift+s", 30); return "snipping tool";
                    default: return "unknown power action: " + w;
                }
            }
            catch (Exception ex) { return "power failed: " + ex.Message; }
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
            if (_hook != IntPtr.Zero) { try { UnhookWindowsHookEx(_hook); } catch { } _hook = IntPtr.Zero; }
            List<int> slots = new List<int>();
            lock (_lock) { foreach (KeyValuePair<int, bool> kv in _turboRun) slots.Add(kv.Key); }
            foreach (int s in slots) StopTurbo(s);
        }
    }
}
