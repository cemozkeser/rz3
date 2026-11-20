using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Rz3
{
    // Counts real HID reports of the mouse. "The firmware accepted id 8" means nothing; the only
    // honest answer to "what is my polling rate" is how many reports per second arrive at Windows.
    // The count comes from Raw Input (WM_INPUT), so the mouse has to be moved while a measurement
    // runs - a still mouse sends no reports at all, whatever the polling rate is.
    //
    // Reports only arrive when the sensor sees movement, so a slow drag averages far below the
    // polling rate. The shortest gap between two consecutive reports is what the device can really
    // do (peak = 1 / shortest gap), so both numbers are reported.
    public class RateMeter
    {
        const int WM_INPUT = 0x00FF;
        const uint RID_INPUT = 0x10000003;
        const uint RIDI_DEVICENAME = 0x20000007;
        const uint RIDEV_INPUTSINK = 0x00000100;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER
        {
            public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        public bool Attached { get; private set; }
        public string Status = "not attached";
        public long Reports { get { return Interlocked.Read(ref _count); } }

        long _count;
        long _lastTick;
        long _minGapUs = long.MaxValue;

        public void ResetWindow()
        {
            Interlocked.Exchange(ref _lastTick, 0);
            Interlocked.Exchange(ref _minGapUs, long.MaxValue);
        }

        public double PeakHz
        {
            get
            {
                long g = Interlocked.Read(ref _minGapUs);
                if (g == long.MaxValue || g <= 0) return 0;
                return 1000000.0 / g;
            }
        }

        public double PeakIntervalMs
        {
            get
            {
                long g = Interlocked.Read(ref _minGapUs);
                return (g == long.MaxValue || g <= 0) ? 0 : g / 1000.0;
            }
        }

        public bool Attach(IntPtr hwnd)
        {
            RAWINPUTDEVICE[] d = new RAWINPUTDEVICE[1];
            d[0].UsagePage = 1;             // generic desktop
            d[0].Usage = 2;                 // mouse
            d[0].Flags = RIDEV_INPUTSINK;   // asked for background delivery; this machine only
            d[0].Target = hwnd;             // delivers to the focused window, so the UI re-targets
            Attached = RegisterRawInputDevices(d, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            Status = Attached ? "counting" : ("register failed, win32=" + Marshal.GetLastWin32Error());
            return Attached;
        }

        public void HandleRawInput(IntPtr lParam)
        {
            if (!Attached) return;
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
                if (h.Type != 0) return;             // 0 = mouse
                long now = Stopwatch.GetTimestamp();
                long prev = Interlocked.Exchange(ref _lastTick, now);
                if (prev != 0)
                {
                    double gapUs = (now - prev) * 1000000.0 / Stopwatch.Frequency;
                    if (gapUs > 20 && gapUs < 200000)   // ignore batched/coalesced and idle gaps
                    {
                        long b = (long)gapUs;
                        long cur = Interlocked.Read(ref _minGapUs);
                        while (b < cur)
                        {
                            long was = Interlocked.CompareExchange(ref _minGapUs, b, cur);
                            if (was == cur) break;
                            cur = was;
                        }
                    }
                }
                Interlocked.Increment(ref _count);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
