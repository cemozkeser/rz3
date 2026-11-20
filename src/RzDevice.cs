using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rz2
{
    // Razer HID control channel: 90-byte feature reports, report id 0.
    // Framing derived from OpenRazer (GPL) driver sources, verified against this device.
    public class RzDevice
    {
        const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        const uint FILE_SHARE_R = 1, FILE_SHARE_W = 2, OPEN_EXISTING = 3;
        const uint GENERIC_WRITE = 0x40000000;
        static readonly Guid GUID_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        public static readonly ushort[] PIDS = new ushort[] { 0x00CC, 0x00CD, 0x00D6, 0x00D7 };

        [StructLayout(LayoutKind.Sequential)]
        struct SP_IFACE_DATA { public int cbSize; public Guid g; public int flags; public IntPtr res; }
        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort Vid; public ushort Pid; public ushort Ver; }
        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Res;
            public ushort a, b, c, d, e, f, gg, h, i, j;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr p, int f);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_IFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_IFACE_DATA d, IntPtr buf, int sz, out int req, IntPtr info);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string n, uint a, uint sh, IntPtr sec, uint disp, uint fl, IntPtr t);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr p);
        [DllImport("hid.dll")]
        static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")]
        static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_SetFeature(IntPtr h, byte[] b, int l);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetFeature(IntPtr h, byte[] b, int l);

        public string Path { get; private set; }
        public ushort Pid { get; private set; }
        IntPtr _handle = (IntPtr)(-1);

        public static List<RzDevice> FindAll()
        {
            List<RzDevice> found = new List<RzDevice>();
            Guid g = GUID_HID;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == (IntPtr)(-1)) return found;
            for (int i = 0; ; i++)
            {
                SP_IFACE_DATA did = new SP_IFACE_DATA();
                did.cbSize = Marshal.SizeOf(typeof(SP_IFACE_DATA));
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;
                int req = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
                if (req == 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, req, out req, IntPtr.Zero)) continue;
                    string p = Marshal.PtrToStringUni((IntPtr)(buf.ToInt64() + 4));
                    if (p == null || p.IndexOf("vid_1532", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    IntPtr h = CreateFile(p, 0, FILE_SHARE_R | FILE_SHARE_W, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == (IntPtr)(-1)) continue;
                    try
                    {
                        HIDD_ATTRIBUTES at = new HIDD_ATTRIBUTES();
                        at.Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES));
                        if (!HidD_GetAttributes(h, ref at)) continue;
                        bool ok = false;
                        foreach (ushort pid in PIDS) if (at.Pid == pid) ok = true;
                        if (!ok) continue;
                        IntPtr pp;
                        if (!HidD_GetPreparsedData(h, out pp)) continue;
                        HIDP_CAPS caps = new HIDP_CAPS();
                        caps.Res = new ushort[17];
                        int st = HidP_GetCaps(pp, ref caps);
                        HidD_FreePreparsedData(pp);
                        if (st != 0x110000 || caps.FeatLen < 90) continue;
                        RzDevice d = new RzDevice();
                        d.Path = p; d.Pid = at.Pid;
                        found.Add(d);
                    }
                    finally { CloseHandle(h); }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            SetupDiDestroyDeviceInfoList(set);
            return found;
        }

        public static RzDevice Find()
        {
            List<RzDevice> all = FindAll();
            return all.Count > 0 ? all[0] : null;
        }

        public bool Open()
        {
            Close();
            // The Razer filter driver (RzDev) permits write-only access while Synapse runs.
            _handle = CreateFile(Path, GENERIC_WRITE, FILE_SHARE_R | FILE_SHARE_W, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            return _handle != (IntPtr)(-1);
        }

        public void Close()
        {
            if (_handle != (IntPtr)(-1)) { CloseHandle(_handle); _handle = (IntPtr)(-1); }
        }

        static byte Crc(byte[] r)
        {
            byte c = 0;
            for (int i = 2; i < 88; i++) c ^= r[i];
            return c;
        }

        const int ERROR_DEVICE_NOT_CONNECTED = 1167;
        const int ERROR_INVALID_HANDLE = 6;

        // Re-opens the interface after the device dropped off the bus.
        //
        // Two different things can happen, and they need different fixes:
        //  * a wireless idle / Synapse grabbing it -> the same interface comes back, so reopening
        //    the stored Path is enough;
        //  * the user moves the mouse between the 2.4 GHz dongle and the USB cable -> the PID
        //    changes (00CD <-> 00CC), the old Path is gone forever and reopening it can never
        //    succeed. That was the "battery unknown / n/a everywhere" bug of 15.11.2026: the
        //    handle was stale and every command failed against a path that no longer existed.
        // So: try the remembered path first, then rescan the whole device list.
        public bool Reopen()
        {
            for (int i = 0; i < 2; i++)
            {
                Close();
                Thread.Sleep(120);
                if (Open()) return true;
            }

            // The interface path itself is dead: look the device up again (new PID, new instance).
            List<RzDevice> all = FindAll();
            if (all.Count == 0) return false;
            // Prefer the same PID, then the first one found.
            RzDevice pick = all[0];
            foreach (RzDevice d in all) if (d.Pid == Pid) { pick = d; break; }
            Path = pick.Path;
            Pid = pick.Pid;
            return Open();
        }

        // Returns the 90-byte response body, or null on failure.
        public byte[] Transact(byte commandClass, byte commandId, byte[] args, byte dataSize)
        {
            return Transact(commandClass, commandId, args, dataSize, 0x1F);
        }

        public byte[] Transact(byte commandClass, byte commandId, byte[] args, byte dataSize, byte tx)
        {
            byte[] body = TransactOnce(commandClass, commandId, args, dataSize, tx);
            if (body != null) return body;
            // The handle was closed because the device dropped off the bus: reopen and retry.
            if (_handle == (IntPtr)(-1) && Reopen())
                return TransactOnce(commandClass, commandId, args, dataSize, tx);
            return null;
        }

        byte[] TransactOnce(byte commandClass, byte commandId, byte[] args, byte dataSize, byte tx)
        {
            try { return TransactBody(commandClass, commandId, args, dataSize, tx); }
            catch
            {
                // Cable pulled, handle revoked, dongle gone: drop the handle, let the caller retry.
                Close();
                return null;
            }
        }

        byte[] TransactBody(byte commandClass, byte commandId, byte[] args, byte dataSize, byte tx)
        {
            if (_handle == (IntPtr)(-1) && !Open()) throw new InvalidOperationException("Device not open (permission denied?).");
            if (args == null) args = new byte[0];
            if (args.Length > 80) throw new ArgumentException("Too many argument bytes.");

            byte[] rep = new byte[90];
            rep[0] = 0x00;
            rep[1] = tx;
            rep[2] = 0x00; rep[3] = 0x00;  // remaining packets
            rep[4] = 0x00;                 // protocol type
            rep[5] = dataSize;
            rep[6] = commandClass;
            rep[7] = commandId;
            Array.Copy(args, 0, rep, 8, args.Length);
            rep[88] = Crc(rep);

            // Drain any reports left by the Razer engine (it shares this device).
            byte[] sink = new byte[91];
            for (int i = 0; i < 6; i++)
            {
                if (!HidD_GetFeature(_handle, sink, sink.Length)) break;
                Thread.Sleep(3);
            }

            byte[] send = new byte[91];
            Array.Copy(rep, 0, send, 1, 90);
            if (!HidD_SetFeature(_handle, send, send.Length))
            {
                int err = Marshal.GetLastWin32Error();
                Close();
                if (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_INVALID_HANDLE) return null;
                throw new InvalidOperationException("SetFeature failed, win32=" + err);
            }

            Thread.Sleep(31);               // settle time for the 00CC/00CD receiver group
            // The device answers with a one-command lag: discard stale acks until the
            // report matches what we sent.
            for (int attempt = 0; attempt < 120; attempt++)
            {
                byte[] resp = new byte[91];
                if (!HidD_GetFeature(_handle, resp, resp.Length))
                {
                    int err = Marshal.GetLastWin32Error();
                    Close();
                    if (err == ERROR_DEVICE_NOT_CONNECTED || err == ERROR_INVALID_HANDLE) return null;
                    throw new InvalidOperationException("GetFeature failed, win32=" + err);
                }
                byte[] body = new byte[90];
                Array.Copy(resp, 1, body, 0, 90);
                bool matches = body[6] == commandClass && body[7] == commandId;
                if (matches && body[0] == 0x01) { Thread.Sleep(20); continue; }   // busy
                if (matches) return body;
                Thread.Sleep(15);
            }
            return null;
        }

        bool Ok(byte[] r) { return r != null && r[0] == 0x02; }

        // ---- device info -------------------------------------------------
        public string Firmware()
        {
            byte[] r = Transact(0x00, 0x81, new byte[] { 0x00, 0x00 }, 0x02);
            if (!Ok(r)) return null;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            bool any = false;
            for (int i = 8; i < 30; i++) { if (r[i] != 0) any = true; }
            if (!any) return null;
            for (int i = 9; i < 15; i++) sb.Append(r[i].ToString("x2"));
            return "0x" + sb.ToString();
        }

        // ---- DPI ---------------------------------------------------------
        // DPI lives in a per-profile "class" table addressed by the profile id itself
        // (04/86 read, 04/06 write). Class index 0 is only the volatile mirror of whichever
        // profile is active, which is why a write into class 0 vanished on the next switch.
        // Record layout: [stageId, x16, y16, z16]; header: [profileId, activeStage, numStages].
        // 04/85 and 04/05 read/write the values of the active stage of one class.
        public byte[] GetDpiClass(byte profileId)
        {
            byte[] args = new byte[80];
            args[0] = profileId;
            return Transact(0x04, 0x86, args, 80);
        }

        // Reads the live DPI of a profile. Freehand wireless bodies answer 0x04 (timeout) while
        // they are waking up, so one retry turns a transient miss into a normal read instead of
        // "DPI: n/a" in the UI (seen on the dongle: the first call after idle returned null while
        // the very next one answered 1000x1000).
        public ushort[] GetDpi(byte profileId)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                byte[] r = Transact(0x04, 0x85, new byte[] { profileId, 0, 0, 0, 0, 0, 0 }, 0x07);
                if (Ok(r))
                    return new ushort[] { (ushort)((r[9] << 8) | r[10]), (ushort)((r[11] << 8) | r[12]) };
                if (attempt == 0) Thread.Sleep(150);
            }
            return null;
        }

        static ushort Word(byte[] b, int i) { return (ushort)((b[i] << 8) | b[i + 1]); }

        public bool SetDpi(byte profileId, ushort x, ushort y)
        {
            try
            {
                byte[] t = GetDpiClass(profileId);
                if (t == null || t[0] != 0x02) return false;
                int stage = t[9];
                int count = t[10];
                if (count < 1 || count > 5 || stage < 1 || stage > count) return false;
                ushort z = Word(t, 11 + (stage - 1) * 7 + 5);   // keep the stage's third axis

                // 1) rewrite the class table so the value survives profile changes
                byte[] payload = new byte[80];
                payload[0] = profileId;
                payload[1] = (byte)stage;
                payload[2] = (byte)count;
                int pos = 3;
                for (int s = 0; s < count; s++)
                {
                    int src = 11 + s * 7;
                    payload[pos++] = t[src];
                    bool mine = (s + 1) == stage;
                    ushort vx = mine ? x : Word(t, src + 1);
                    ushort vy = mine ? y : Word(t, src + 3);
                    ushort vz = mine ? z : Word(t, src + 5);
                    payload[pos++] = (byte)(vx >> 8); payload[pos++] = (byte)vx;
                    payload[pos++] = (byte)(vy >> 8); payload[pos++] = (byte)vy;
                    payload[pos++] = (byte)(vz >> 8); payload[pos++] = (byte)vz;
                }
                byte[] w = Transact(0x04, 0x06, payload, 80);
                if (w == null || w[0] != 0x02) return false;

                // 2) push it as the live setting of that same class
                byte[] r = Transact(0x04, 0x05,
                    new byte[] { profileId, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, (byte)(z >> 8), (byte)z }, 0x07);
                return Ok(r);
            }
            catch { return false; }
        }

        // Stage list of a profile (x values) plus its active stage, without changing anything.
        public ushort[] GetDpiStages(byte profileId, out int active)
        {
            active = 0;
            byte[] t = GetDpiClass(profileId);
            if (t == null || t[0] != 0x02) return null;
            int count = t[10];
            if (count < 1 || count > 5) return null;
            active = t[9];
            ushort[] v = new ushort[count];
            for (int i = 0; i < count; i++) v[i] = Word(t, 11 + i * 7 + 1);
            return v;
        }

        // Moves the active stage of a profile without touching the stored stage values: this is
        // the "DPI up / down / cycle" action of the firmware, done from the host side.
        public bool SetDpiStage(byte profileId, int stage)
        {
            try
            {
                byte[] t = GetDpiClass(profileId);
                if (t == null || t[0] != 0x02) return false;
                int count = t[10];
                if (count < 1 || count > 5) return false;
                if (stage < 1) stage = 1;
                if (stage > count) stage = count;
                byte[] payload = new byte[80];
                payload[0] = profileId;
                payload[1] = (byte)stage;
                payload[2] = (byte)count;
                int pos = 3;
                for (int s = 0; s < count; s++)
                {
                    int src = 11 + s * 7;
                    payload[pos++] = t[src];
                    payload[pos++] = t[src + 1];
                    payload[pos++] = t[src + 2];
                    payload[pos++] = t[src + 3];
                    payload[pos++] = t[src + 4];
                    payload[pos++] = t[src + 5];
                    payload[pos++] = t[src + 6];
                }
                byte[] w = Transact(0x04, 0x06, payload, 80);
                if (w == null || w[0] != 0x02) return false;
                int b = 11 + (stage - 1) * 7;
                byte[] r = Transact(0x04, 0x05,
                    new byte[] { profileId, t[b + 1], t[b + 2], t[b + 3], t[b + 4], t[b + 5], t[b + 6] }, 0x07);
                return Ok(r);
            }
            catch { return false; }
        }

        // ---- polling ----------------------------------------------------
        // Two addressings, both per profile (Razer protocol module + live probe):
        //   standard   00/8E read [profileId] -> [profileId, id];  00/0E write [profileId, id]
        //   high speed 02/C0 read [profileId] -> [profileId, id];  02/40 write [profileId, id]
        // Commands: COMMAND_{GET,SET}_DEVICE_PROFILE_POLLING_PERIOD = [2,0,142] / [2,0,14],
        // COMMAND_{GET,SET}_USB_HIGH_SPEED_POLLING_PERIOD = [2,0,192] / [2,0,64].
        // Id enums from the same module (DEVICE_POLLING_PERIOD_VALUE / USB_HIGH_SPEED_...):
        //   standard ids:   1=1000 2=500 3=333 4=250 5=200 8=125 10=100
        //   high speed ids: 1=8000 2=4000 4=2000 8=1000 16=500 32=250 64=125
        // The firmware accepts almost any id, so an accepted write proves nothing; only counting
        // real reports per second does (see Rz3 "Measure" / --ratetest).
        public static readonly ushort[] StdRates = { 1000, 500, 333, 250, 200, 125, 100 };
        static readonly byte[] StdIds = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x08, 0x0A };
        public static readonly ushort[] HsRates = { 8000, 4000, 2000, 1000, 500, 250, 125 };
        static readonly byte[] HsIds = { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40 };

        public static byte StdIdOf(ushort hz)
        {
            for (int i = 0; i < StdRates.Length; i++) if (StdRates[i] == hz) return StdIds[i];
            return 0;
        }
        public static ushort StdHzOf(byte id)
        {
            for (int i = 0; i < StdIds.Length; i++) if (StdIds[i] == id) return StdRates[i];
            return 0;
        }
        public static byte HsIdOf(ushort hz)
        {
            for (int i = 0; i < HsRates.Length; i++) if (HsRates[i] == hz) return HsIds[i];
            return 0;
        }
        public static ushort HsHzOf(byte id)
        {
            for (int i = 0; i < HsIds.Length; i++) if (HsIds[i] == id) return HsRates[i];
            return 0;
        }

        readonly Dictionary<byte, bool> _hsSupport = new Dictionary<byte, bool>();

        // True when the high-speed (HyperPolling) command answers for this profile. Only the
        // positive answer is cached: a refused probe may be a sleepy device.
        public bool HighSpeedPollSupported(byte profileId)
        {
            bool v;
            if (_hsSupport.TryGetValue(profileId, out v)) return v;
            byte[] r = Transact(0x02, 0xC0, new byte[] { profileId }, 0x01);
            v = Ok(r);
            if (v) _hsSupport[profileId] = true;
            return v;
        }

        // Rate actually stored for a profile, preferring the high-speed command when supported.
        public int GetPollRateEx(byte profileId, out bool highSpeed)
        {
            highSpeed = false;
            byte[] r = Transact(0x02, 0xC0, new byte[] { profileId }, 0x01);
            if (Ok(r) && r[9] != 0)
            {
                ushort hz = HsHzOf(r[9]);
                if (hz > 0) { highSpeed = true; return hz; }
            }
            return GetPollRate(profileId);
        }

        // Raw standard-enum id of a profile (0 when it does not answer).
        public byte GetPollIdRaw(byte profileId)
        {
            byte[] r = Transact(0x00, 0x8E, new byte[] { profileId, 0x00 }, 0x02);
            return Ok(r) ? r[9] : (byte)0;
        }

        // Raw standard-enum write; used by the calibration sweep, which walks ids the enums
        // do not list to find out what the firmware really does with them.
        public bool SetPollIdRaw(byte profileId, byte rawId)
        {
            byte[] r = Transact(0x00, 0x0E, new byte[] { profileId, rawId }, 0x02);
            return Ok(r);
        }

        public bool SetPollRate(byte profileId, ushort hz)
        {
            if (hz > 1000)
            {
                byte hid = HsIdOf(hz);
                if (hid == 0) return false;
                byte[] hr = Transact(0x02, 0x40, new byte[] { profileId, hid }, 0x02);
                return Ok(hr);
            }
            byte id = StdIdOf(hz);
            if (id == 0) return false;
            byte[] r = Transact(0x00, 0x0E, new byte[] { profileId, id }, 0x02);
            if (!Ok(r)) return false;
            // Keep the high-speed view in step when this firmware has both tables.
            if (HighSpeedPollSupported(profileId))
            {
                byte hid = HsIdOf(hz);
                if (hid != 0) Transact(0x02, 0x40, new byte[] { profileId, hid }, 0x02);
            }
            return true;
        }

        public int GetPollRate(byte profileId)
        {
            byte[] r = Transact(0x00, 0x8E, new byte[] { profileId, 0x00 }, 0x02);
            if (!Ok(r)) return 0;
            return StdHzOf(r[9]);
        }

        // ---- brightness / effects --------------------------------------
        public void SetBrightness(byte led, byte value)   // led 0=all,1=wheel,4=logo
        {
            Transact(0x0F, 0x04, new byte[] { 0x01, led, value }, 0x03);
        }
        public int GetBrightness(byte led)
        {
            byte[] r = Transact(0x0F, 0x84, new byte[] { 0x01, led, 0x00 }, 0x03);
            if (!Ok(r)) return -1;
            return r[10];
        }
        public void EffectNone(byte led)
        {
            Transact(0x0F, 0x02, new byte[] { 0x01, led, 0x00, 0x00, 0x00, 0x00 }, 0x06);
        }
        public void EffectSpectrum(byte led)
        {
            Transact(0x0F, 0x02, new byte[] { 0x01, led, 0x03, 0x00, 0x00, 0x00 }, 0x06);
        }
        public void EffectWave(byte led, byte direction)
        {
            if (direction > 2) direction = 2;
            Transact(0x0F, 0x02, new byte[] { 0x01, led, 0x04, direction, 0x28, 0x00 }, 0x06);
        }
        public void EffectStatic(byte led, byte r, byte g, byte b)
        {
            Transact(0x0F, 0x02, new byte[] { 0x01, led, 0x01, 0x00, 0x00, 0x01, r, g, b }, 0x09);
        }
        // Arm custom-frame mode, then paint all 13 columns in one report.
        public void CustomFrame(byte[] rgb39)
        {
            if (rgb39 == null || rgb39.Length != 39) throw new ArgumentException("Need 13 RGB triples.");
            Transact(0x0F, 0x02, new byte[] { 0x00, 0x00, 0x08 }, 0x0C);
            byte[] args = new byte[5 + 39];
            args[2] = 0x00;   // row
            args[3] = 0x00;   // start column
            args[4] = 0x0C;   // stop column (inclusive)
            Array.Copy(rgb39, 0, args, 5, 39);
            Transact(0x0F, 0x03, args, 0x47);
        }

        // ---- extra effects (ids measured on the device: 0/1/2/3/4/5/8 accepted) ----
        public void EffectOff(byte led)
        {
            Transact(0x0F, 0x02, new byte[] { 0x01, led, 0x00, 0x00, 0x00, 0x00 }, 0x06);
        }
        public void EffectBreathing(byte led, byte rate)
        {
            Transact(0x0F, 0x02, new byte[] { 0x01, led, 0x02, 0x00, rate, 0x00 }, 0x06);
        }
        // Effect id 5: accepted by firmware, semantics unverified (reactive/fire family).
        public void EffectReactive(byte led, byte rate)
        {
            Transact(0x0F, 0x02, new byte[] { 0x01, led, 0x05, 0x00, rate, 0x00 }, 0x06);
        }

        // ---- scroll modes: 0 = tactile, 1 = free spin, 2 = micro tactile ----
        public void SetScrollModeValue(byte mode)
        {
            Transact(0x02, 0x14, new byte[] { 0x01, mode }, 0x02);
        }

        // ---- scroll feel (global) ---------------------------------------
        // Headers from Razer's protocol module: get = cmd 0xA0+n, set = cmd 0x10+n.
        static byte Val2(bool on) { return (byte)(on ? 0x01 : 0x02); }

        // 0 = tactile, 1 = free spin, 2 = micro tactile (both directions verified on 00CC)
        public int GetScrollMode3()
        {
            byte[] r = Transact(0x02, 0x94, new byte[] { 0x01, 0x00 }, 0x02);
            return Ok(r) ? r[9] : -1;
        }
        public void SetScrollMode3(byte mode) { SetScrollModeValue(mode); }
        public bool GetScrollAccel()
        {
            byte[] r = Transact(0x02, 0x96, new byte[] { 0x01, 0x00 }, 0x02);
            return Ok(r) && r[9] == 0x01;   // 1 = on; 0 off; 2 is accepted but unverified
        }
        public void SetScrollAccel(bool on)
        {
            Transact(0x02, 0x16, new byte[] { 0x01, (byte)(on ? 1 : 0) }, 0x02);
        }
        public int GetScrollAccelLevel()
        {
            byte[] r = Transact(0x02, 0xA2, new byte[] { 0x01, 0x00 }, 0x02);
            return Ok(r) ? r[9] : -1;
        }
        public void SetScrollAccelLevel(byte level)
        {
            Transact(0x02, 0x22, new byte[] { 0x01, level }, 0x02);
        }
        // Smart reel = automatic switch to free spin while scrolling fast (Synapse calls it
        // "scroll mode auto switch" in the protocol, smartReel* in the profile model).
        public bool GetSmartReel()
        {
            byte[] r = Transact(0x02, 0x97, new byte[] { 0x01, 0x00 }, 0x02);
            return Ok(r) && r[9] == 0x01;
        }
        public void SetSmartReel(bool on)
        {
            Transact(0x02, 0x17, new byte[] { 0x01, (byte)(on ? 1 : 0) }, 0x02);
        }
        public int GetSmartReelThreshold()
        {
            byte[] r = Transact(0x02, 0xA3, new byte[] { 0x01, 0x00 }, 0x02);
            return Ok(r) ? r[9] : -1;
        }
        public void SetSmartReelThreshold(byte level)
        {
            Transact(0x02, 0x23, new byte[] { 0x01, level }, 0x02);
        }

        // ---- debounce (profile scoped) ----------------------------------
        public int GetDebounce()
        {
            byte[] r = Transact(0x02, 0x95, new byte[] { 0x01, 0x00 }, 0x02);
            return Ok(r) ? r[9] : -1;
        }
        public void SetDebounce(byte ms)
        {
            Transact(0x02, 0x15, new byte[] { 0x01, ms }, 0x02);
        }

        // ---- battery / mode / scroll -----------------------------------
        // ---- what the mouse is doing right now ---------------------------------------------
        //
        // Three states the user cares about, and they CAN be told apart - none of them needs the
        // charging command this firmware does not implement (07/85 = 0x05):
        //
        //   link     = Wired / Wireless / None   <- the PID that is enumerated
        //   charge   = Charging / Full / OnBattery / Unknown
        //
        // Measured 15.11.2026 (tools8/ConnProbe.exe):
        //   cable  00CC: 07/84 = 00 01   (byte 9 = 1)    battery 100 -> full, <100 -> charging
        //   dongle 00CD: 07/84 = 00 00   (byte 9 = 0)    never charging through the receiver
        //   00/8D is 00 on cable and 01 on the dongle, but 00/8D is unreliable (it nulls out), so
        //   the PID is the primary source and 07/84 byte 9 is the cross-check.
        public const int LinkNone = 0, LinkWired = 1, LinkWireless = 2;
        public const int ChargeUnknown = -1, ChargeOnBattery = 0, ChargeCharging = 1, ChargeFull = 2;

        public int LinkMode
        {
            get
            {
                if (_handle == (IntPtr)(-1) && !Open()) return LinkNone;
                if (Pid == 0x00CC || Pid == 0x00D6) return LinkWired;
                if (Pid == 0x00CD || Pid == 0x00D7) return LinkWireless;
                return LinkNone;
            }
        }

        // True when the cable reports a link: 07/84 byte 9 is 1 on cable and 0 through the dongle.
        // Returns -1 when the command does not answer, so callers can fall back to the PID alone.
        public int CableFlag()
        {
            byte[] r = Read2(0x07, 0x84, new byte[0], 4);
            if (!Ok(r)) return -1;
            return r[9] != 0 ? 1 : 0;
        }

        // The honest charge state:
        //  * wireless link -> the receiver never charges, so it is on battery (or unknown if the
        //    level cannot be read);
        //  * wired link -> byte 9 of 07/84 says the cable is live; then the level decides between
        //    "charging" (< 100 %) and "full" (100 %).
        public int ChargeState(out int batteryPercent)
        {
            batteryPercent = Battery();
            int link = LinkMode;
            if (link == LinkNone) return ChargeUnknown;
            if (link == LinkWireless) return ChargeOnBattery;
            int cable = CableFlag();
            if (cable < 0) cable = 1;                 // wired PID alone is good enough
            if (cable == 0) return ChargeOnBattery;
            if (batteryPercent < 0) return ChargeUnknown;
            return batteryPercent >= 100 ? ChargeFull : ChargeCharging;
        }

        // 1 = charging, 0 = not charging, -1 = unknown. Kept for source compatibility; this is the
        // HONEST answer rather than the system-wide one (GetSystemPowerStatus reports the PC).
        public int Charging()
        {
            int pct;
            int st = ChargeState(out pct);
            if (st == ChargeCharging) return 1;
            if (st == ChargeOnBattery || st == ChargeFull) return 0;
            return -1;
        }

        public static string LinkName(int link)
        {
            return link == LinkWired ? "cable" : link == LinkWireless ? "2.4 GHz dongle" : "not connected";
        }

        public static string ChargeName(int st)
        {
            switch (st)
            {
                case ChargeCharging: return "charging";
                case ChargeFull: return "full, not charging";
                case ChargeOnBattery: return "on battery";
                default: return "unknown";
            }
        }

        public int Battery()
        {
            byte[] r = Read2(0x07, 0x80, new byte[0], 0x02);
            if (!Ok(r)) return -1;
            return (int)Math.Round(r[9] / 255.0 * 100.0);
        }
        // Charging is NOT readable from this firmware: 07/84 is "time to sleep on battery" and
        // always answers 00 00, and the real charging command (07/85, Razer header [3,7,133])
        // answers 0x05 (unsupported). Measured 15.11.2026 with tools6/SleepProbe.exe and
        // PowerSweep.exe. Windows knows the truth, so this reports the system's view:
        //   PowerStatus.ACLineStatus: 0 = on battery, 1 = plugged in, 255 = unknown
        public static int SystemOnAc()
        {
            try
            {
                SYSTEM_POWER_STATUS st;
                if (!GetSystemPowerStatus(out st)) return -1;
                if (st.ACLineStatus == 255) return -1;
                return st.ACLineStatus;          // 0 = battery, 1 = AC
            }
            catch { return -1; }
        }

        public static int SystemBatteryPercent()
        {
            try
            {
                SYSTEM_POWER_STATUS st;
                if (!GetSystemPowerStatus(out st)) return -1;
                return st.BatteryLifePercent == 255 ? -1 : st.BatteryLifePercent;
            }
            catch { return -1; }
        }

        // 1 = charging, 0 = not charging, -1 = unknown.
        // Windows' BatteryFlag: 1 high, 2 low, 4 critical, 8 charging, 128 no system battery,
        // 255 unknown. A full battery on AC reports "not charging", which is the honest answer.
        public static int SystemCharging()
        {
            try
            {
                SYSTEM_POWER_STATUS st;
                if (!GetSystemPowerStatus(out st)) return -1;
                if (st.BatteryFlag == 255) return -1;
                if ((st.BatteryFlag & 128) != 0) return 0;      // no battery at all
                return (st.BatteryFlag & 8) != 0 ? 1 : 0;       // bit 3 = charging
            }
            catch { return -1; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        public void SetDeviceMode(byte mode)
        {
            Transact(0x00, 0x04, new byte[] { mode, 0x00 }, 0x02);
        }
        public void SetScrollMode(bool freeSpin)
        {
            Transact(0x02, 0x14, new byte[] { 0x01, (byte)(freeSpin ? 1 : 0) }, 0x02);
        }
        public void SetScrollAcceleration(bool on)
        {
            Transact(0x02, 0x16, new byte[] { 0x01, (byte)(on ? 1 : 0) }, 0x02);
        }

        // Sleep timeout. The firmware accepts 0..65535 s (measured: 10, 59, 300, 900, 901, 1200,
        // 1800, 3600 and 65535 s were all stored exactly). Rz3 used to clamp to 60..900 s, which
        // was its own choice, not a hardware limit - a 20 s timeout is legal.
        public void SetIdleTime(ushort seconds)
        {
            Transact(0x07, 0x03, new byte[] { (byte)(seconds >> 8), (byte)seconds }, 0x02);
        }
        // Wireless bodies answer 0x04 while waking up; every read here retries once so a transient
        // miss never surfaces as an empty/disabled control on the Power tab.
        byte[] Read2(byte cls, byte cmd, byte[] args, byte size)
        {
            for (int i = 0; i < 2; i++)
            {
                byte[] r = Transact(cls, cmd, args, size);
                if (Ok(r)) return r;
                if (i == 0) Thread.Sleep(150);
            }
            return null;
        }

        public ushort GetIdleTime()
        {
            byte[] r = Read2(0x07, 0x83, new byte[2], 0x02);
            if (!Ok(r)) return 0;
            return (ushort)((r[8] << 8) | r[9]);
        }
        // Low battery threshold. The old code clamped the raw byte to 0x0C..0x3F (5..25 %), so
        // anything above 25 % was silently rewritten to 25 % - the user could not ask for 45 %.
        // Measured (tools8/LowBattRange.exe): 07/01 stores every raw value 0x05..0xFF and reads it
        // back exactly, including 0x72 for 45 %. Only the two extreme ends (0x00..0x04) are
        // pointless, so the usable range is 5..100 %.
        public void SetLowBattery(int percent)
        {
            if (percent < 5) percent = 5;
            if (percent > 100) percent = 100;
            int b = (int)Math.Round(percent / 100.0 * 255.0);
            if (b < 0x05) b = 0x05;
            if (b > 0xFF) b = 0xFF;
            // tx 0x1F, not 0xFF: this body answers 0x04 (timeout) to every command sent with tx
            // 0xFF, so the old write never landed.
            Transact(0x07, 0x01, new byte[] { (byte)b }, 0x01);
        }
        public int GetLowBattery()
        {
            byte[] r = Read2(0x07, 0x81, new byte[0], 0x01);
            if (!Ok(r)) return -1;
            return (int)Math.Round(r[8] / 255.0 * 100.0);
        }

        // Device power profile: Razer's enum is {0 Power Save, 1 Balance, 2 High Performance}.
        //
        // Measured on this unit (15.11.2026, tools7/PowerPlanProbe.exe):
        //   Razer's own headers  05/8C (get) and 05/0D (set)  -> 0x03, not implemented here
        //   07/8D (get)                                       -> 0x02 but ALWAYS 00
        //   07/0D (set)                                       -> 0x02 but the value is not stored;
        //                                                        a fresh handle still reads 00
        // So the profile exists in the protocol but not in this firmware: a write is acknowledged
        // and immediately forgotten. -1 is returned so callers never show a value nobody set.
        public int GetPowerProfile()
        {
            // Prefer Razer's real command; fall back to the class-7 one for bodies that answer it.
            byte[] r = Read2(0x05, 0x8C, new byte[0], 4);
            if (Ok(r)) return r[8] <= 2 ? r[8] : -1;

            r = Read2(0x07, 0x8D, new byte[0], 8);
            if (!Ok(r)) return -1;
            return r[8] <= 2 ? r[8] : -1;
        }

        // Returns true only when the firmware actually stores the value (verified by read-back).
        public bool SetPowerProfile(byte profile)
        {
            if (profile > 2) return false;
            Transact(0x05, 0x0D, new byte[] { profile, 0x00 }, 2);
            Thread.Sleep(80);
            int back = GetPowerProfile();
            if (back == profile) return true;
            Transact(0x07, 0x0D, new byte[] { profile }, 1);
            Thread.Sleep(80);
            return GetPowerProfile() == profile;
        }

        // True when this firmware stores a power profile at all. Probes by writing the current
        // value back: a body that ignores the command still answers 0x02, so only a read-back can
        // tell "supported" from "acknowledged and forgotten".
        public bool PowerProfileSupported()
        {
            int cur = GetPowerProfile();
            if (cur < 0) return false;
            byte other = (byte)(cur == 0 ? 1 : 0);
            bool stored = SetPowerProfile(other);
            if (stored) SetPowerProfile((byte)cur);        // put it back
            return stored;
        }

        // ---- raw --------------------------------------------------------
        public byte[] Raw(byte cls, byte cmd, byte[] args)
        {
            return Transact(cls, cmd, args, (byte)args.Length);
        }

        // ---- profiles (class 0x05) -------------------------------------
        // Reference: Razer protocol module, COMMAND_*_PROFILE_*. Header lengths show the
        // exact payload size the firmware expects (1, 2 or 80 bytes); shorter payloads are
        // rejected with status 0x03. Verified live on 00CC: 05/80,81,82,83,84,04,88,08,8A.
        static byte[] Pad(byte[] src, int len)
        {
            byte[] a = new byte[len];
            Array.Copy(src, a, Math.Min(src.Length, len));
            return a;
        }

        public int ProfileCount()
        {
            byte[] r = Transact(0x05, 0x80, new byte[1], 0x01);
            return Ok(r) ? r[8] : -1;
        }

        public int MaxProfiles()
        {
            byte[] r = Transact(0x05, 0x8A, new byte[1], 0x01);
            return Ok(r) ? r[8] : -1;
        }

        public int ActiveProfile()
        {
            byte[] r = Transact(0x05, 0x84, new byte[1], 0x01);
            return Ok(r) ? r[8] : -1;
        }

        public bool SetActiveProfile(byte id)
        {
            byte[] r = Transact(0x05, 0x04, new byte[] { id }, 0x01);
            return Ok(r);
        }

        public bool CreateProfile(byte id)
        {
            byte[] r = Transact(0x05, 0x02, new byte[] { id }, 0x01);
            return Ok(r);
        }

        public bool DeleteProfile(byte id)
        {
            byte[] r = Transact(0x05, 0x03, new byte[] { id }, 0x01);
            return Ok(r);
        }

        // Profile name area = [guid 16][ascii name]; read returns it behind [id, offset, size].
        public string GetProfileName(byte id)
        {
            byte[] req = Pad(new byte[] { id, 0, 0, 0, 0xFA }, 80);
            byte[] r = Transact(0x05, 0x88, req, 80);
            if (!Ok(r)) return null;
            int n = 13, end = 13;
            while (end < 88 && r[end] != 0) end++;
            if (end <= 13) return "";
            byte[] raw = new byte[end - n];
            Array.Copy(r, n, raw, 0, raw.Length);
            // Strip a leading 16-byte GUID if the blob carries one.
            int start = 0;
            if (raw.Length > 16)
            {
                bool bin = false;
                for (int i = 0; i < 16; i++) if (raw[i] < 0x20 || raw[i] > 0x7E) bin = true;
                if (bin) start = 16;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < raw.Length; i++) sb.Append((char)raw[i]);
            return sb.ToString();
        }

        // Returns the 16-byte profile GUID prefix, or null when the slot has none.
        public byte[] GetProfileGuid(byte id)
        {
            byte[] req = Pad(new byte[] { id, 0, 0, 0, 0xFA }, 80);
            byte[] r = Transact(0x05, 0x88, req, 80);
            if (!Ok(r)) return null;
            bool bin = false;
            for (int i = 13; i < 29; i++) if (r[i] < 0x20 || r[i] > 0x7E) bin = true;
            if (!bin) return null;
            byte[] g = new byte[16];
            Array.Copy(r, 13, g, 0, 16);
            return g;
        }

        public bool SetProfileName(byte id, string name, byte[] guid16)        {
            byte[] txt = Encoding.ASCII.GetBytes(name ?? "");
            if (txt.Length > 60) { byte[] t = new byte[60]; Array.Copy(txt, t, 60); txt = t; }
            byte[] blob = new byte[16 + txt.Length];
            if (guid16 != null && guid16.Length == 16) Array.Copy(guid16, blob, 16);
            Array.Copy(txt, 0, blob, 16, txt.Length);
            byte[] req = new byte[5 + blob.Length];
            req[0] = id;
            req[3] = (byte)(blob.Length >> 8);
            req[4] = (byte)blob.Length;
            Array.Copy(blob, 0, req, 5, blob.Length);
            byte[] r = Transact(0x05, 0x08, Pad(req, 80), 80);
            return Ok(r);
        }

        // Reads every mapping record of one profile/mode.
        // The third request byte is a record offset; one page holds at most 9 records
        // (80-byte payload). Record format: inputId, outType, dataSize, data...
        public List<KeyValuePair<byte, byte[]>> ReadMappings(byte id, byte mode)
        {
            try { return ReadMappingsCore(id, mode); }
            catch { return null; }
        }

        List<KeyValuePair<byte, byte[]>> ReadMappingsCore(byte id, byte mode)
        {
            List<KeyValuePair<byte, byte[]>> list = new List<KeyValuePair<byte, byte[]>>();
            int offset = 0;
            while (offset < 40)
            {
                byte[] req = Pad(new byte[] { id, mode, (byte)offset }, 80);
                byte[] r = Transact(0x02, 0x8E, req, 80);
                if (!Ok(r)) return offset == 0 ? null : list;
                int pos = 11;
                int inPage = 0;
                while (pos + 3 < 88)
                {
                    byte input = r[pos];
                    if (input == 0) break;
                    byte type = r[pos + 1];
                    int size = r[pos + 2];
                    if (size > 16) size = 16;
                    byte[] data = new byte[size];
                    Array.Copy(r, pos + 3, data, 0, size);
                    byte[] rec = new byte[1 + data.Length];
                    rec[0] = type;
                    Array.Copy(data, 0, rec, 1, data.Length);
                    list.Add(new KeyValuePair<byte, byte[]>(input, rec));
                    pos += Math.Max(8, 3 + size);
                    inPage++;
                }
                if (inPage < 9) break;
                offset += inPage;
            }
            return list;
        }
    }
}



