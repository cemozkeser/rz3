using System;
using System.Collections.Generic;

namespace Rz3
{
    // Device-side (firmware) button mapping layer, one record per button and mode.
    // Command: class 0x02 / cmd 0x0C, args:
    //   slot(profile id), inputId, mode(0 normal / 1 Hypershift), outType, dataSize, data...
    // Read back: class 0x02 / cmd 0x8E, 80-byte payload [profileId, mode, 0 ...]; the reply
    // repeats that header and then lists records as [inputId, outType, dataSize, data...].
    //
    // Values recovered from Razer's own protocol module (functionId / buttonId enums) and
    // confirmed by USBPcap captures plus write+echo verification on this device.
    public class MapEntry
    {
        public string Input;
        public byte InputId;
        public byte Mode;
        public byte OutType;
        public byte[] Data = new byte[0];
        public string Label;

        public MapEntry(string input, byte id, string label)
        {
            Input = input; InputId = id; Label = label;
        }

        public byte[] Args(byte slot)
        {
            if (OutType == 0 || Data == null)
                return new byte[] { slot, InputId, Mode };
            byte[] a = new byte[5 + Data.Length];
            a[0] = slot; a[1] = InputId; a[2] = Mode; a[3] = OutType; a[4] = (byte)Data.Length;
            Array.Copy(Data, 0, a, 5, Data.Length);
            return a;
        }
    }

    public static class MapTable
    {
        public static readonly string[] InputKeys = {
            "side4", "side5", "wheel", "scrollUp", "scrollDown", "tiltL", "tiltR",
            "clutch", "top1", "top2", "profile", "left", "right"
        };
        public static readonly string[] InputNames = {
            "Side button 4", "Side button 5", "Wheel click", "Scroll up", "Scroll down",
            "Tilt left", "Tilt right", "Clutch (sniper)", "Top button 1", "Top button 2",
            "Bottom profile button", "Left click", "Right click"
        };
        public static readonly byte[] InputIds = {
            0x04, 0x05, 0x03, 0x09, 0x0A, 0x34, 0x35, 0x0F, 0x6A, 0x60, 0x0E, 0x01, 0x02
        };

        public static List<MapEntry> CreateDefaults()
        {
            List<MapEntry> l = new List<MapEntry>();
            for (int i = 0; i < InputKeys.Length; i++)
            {
                MapEntry m = new MapEntry(InputKeys[i], InputIds[i], InputNames[i]);
                m.OutType = 0;
                l.Add(m);
            }
            return l;
        }

        // Razer factory mapping, read back from a freshly created profile on this device
        // (13.11.2026). Null means "disabled".
        public static byte[] FactoryRecord(byte inputId)
        {
            switch (inputId)
            {
                case 0x01: return new byte[] { 0x01, 0x01 };
                case 0x02: return new byte[] { 0x01, 0x02 };
                case 0x03: return new byte[] { 0x01, 0x03 };
                case 0x04: return new byte[] { 0x01, 0x04 };
                case 0x05: return new byte[] { 0x01, 0x05 };
                case 0x09: return new byte[] { 0x01, 0x09 };
                case 0x0A: return new byte[] { 0x01, 0x0A };
                case 0x34: return new byte[] { 0x0E, 0x68, 0x00, 0x14 };
                case 0x35: return new byte[] { 0x0E, 0x69, 0x00, 0x14 };
                case 0x0F: return new byte[] { 0x06, 0x05, 0x01, 0x90, 0x01, 0x90 };
                case 0x60: return new byte[] { 0x06, 0x06 };
                case 0x0E: return new byte[] { 0x07, 0x04 };
                default: return null;
            }
        }
    }

    public static class OutTypes
    {
        public const byte Disabled = 0x00;
        public const byte MouseButton = 0x01;
        public const byte Keyboard = 0x02;
        public const byte MacroI = 0x03;
        public const byte Sensitivity = 0x06;
        public const byte NavigateProfile = 0x07;
        public const byte Lighting = 0x08;
        public const byte PowerKey = 0x09;
        public const byte Media = 0x0A;
        public const byte DoubleClick = 0x0B;
        public const byte ModeButton = 0x0C;
        public const byte TurboKey = 0x0D;
        public const byte SideScroll = 0x0E;
        public const byte MacroIV = 0x0F;
        public const byte RazerKey = 0x11;
        public const byte ScrollMode = 0x12;

        public static readonly string[] Names = {
            "Disabled", "Mouse button", "Keyboard key", "Macro (I)",
            "DPI action", "Switch profile", "Lighting toggle", "Razer key",
            "Media key", "Double click", "Mode button", "Turbo key",
            "Side scroll", "Macro (IV)", "Razer key (raw)", "Scroll mode toggle"
        };
        public static readonly byte[] Values = {
            Disabled, MouseButton, Keyboard, MacroI, Sensitivity, NavigateProfile,
            Lighting, PowerKey, Media, DoubleClick, ModeButton, TurboKey,
            SideScroll, MacroIV, RazerKey, ScrollMode
        };
    }

    public static class MouseActions
    {
        public static readonly string[] Names = {
            "Left click", "Right click", "Middle click", "Back (4)", "Forward (5)",
            "Double click", "Scroll up", "Scroll down", "Scroll left", "Scroll right"
        };
        public static readonly byte[] Ids = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x0B, 0x09, 0x0A, 0x34, 0x35 };
    }

    public static class Modifiers
    {
        public const byte LeftCtrl = 0x01, LeftShift = 0x02, LeftAlt = 0x04, LeftWin = 0x08;
        public static readonly string[] Labels = { "C", "S", "A", "W" };
        public static readonly byte[] Bits = { LeftCtrl, LeftShift, LeftAlt, LeftWin };
    }

    // Windows system shortcuts the firmware can emit by itself: one key + a modifier mask.
    // The mouse's keyboard interface is a real keyboard, so Windows treats these as system
    // shortcuts (Win+D, Alt+Tab, ...). Sequences with several taps or delays (macro/turbo) are
    // not possible here - those live in Synapse's host engine.
    public static class WinShortcuts
    {
        public static readonly string[] Names = {
            "Show desktop (Win+D)", "This PC (Win+E)", "Cycle apps (Alt+Tab)", "Close app (Alt+F4)",
            "Lock PC (Win+L)", "Run dialog (Win+R)", "Search (Win+S)", "Settings (Win+I)",
            "Action centre (Win+A)", "Project (Win+P)", "Emoji panel (Win+.)",
            "Snap left (Win+Left)", "Snap right (Win+Right)", "Maximise (Win+Up)", "Minimise (Win+Down)",
            "Next virtual desktop (Ctrl+Win+Right)", "Previous virtual desktop (Ctrl+Win+Left)",
            "Task manager (Ctrl+Shift+Esc)", "Screenshot snip (Win+Shift+S)",
            "File explorer new window (Ctrl+N)", "Undo (Ctrl+Z)", "Save (Ctrl+S)", "Copy (Ctrl+C)",
            "Paste (Ctrl+V)", "Cut (Ctrl+X)", "Select all (Ctrl+A)"
        };
        public static readonly byte[] Mods = {
            0x08, 0x08, 0x04, 0x04,
            0x08, 0x08, 0x08, 0x08,
            0x08, 0x08, 0x08,
            0x08, 0x08, 0x08, 0x08,
            0x09, 0x09,
            0x03, 0x0A,
            0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01
        };
        public static readonly byte[] Usages = {
            0x07, 0x08, 0x2B, 0x3D,
            0x0F, 0x15, 0x16, 0x0C,
            0x04, 0x13, 0x37,
            0x50, 0x4F, 0x52, 0x51,
            0x4F, 0x50,
            0x29, 0x16,
            0x11, 0x1D, 0x16, 0x06, 0x19, 0x1B, 0x04
        };

        // Index of the preset matching a record, or -1.
        public static int Match(byte mod, byte usage)
        {
            for (int i = 0; i < Usages.Length; i++)
                if (Mods[i] == mod && Usages[i] == usage) return i;
            return -1;
        }
    }

    // DPI actions. "DPI clutch" carries an explicit x/y pair; the rest are single-byte ids.
    public static class SensActions
    {
        public static readonly string[] Names = { "DPI clutch", "DPI up", "DPI down", "DPI cycle up", "DPI cycle down" };
        public static readonly byte[] Ids = { 0x01, 0x02, 0x03, 0x04, 0x05 };
    }

    // Profile navigation. 04/03 are measured (Synapse "profile up / down"); 05/06 are the
    // cycle (wrap-around) candidates of the same enum and still need a press test.
    public static class ProfileActions
    {
        public static readonly string[] Names = {
            "Next profile", "Previous profile",
            "Cycle up (wrap, untested)", "Cycle down (wrap, untested)",
            "Go to profile 1", "Go to profile 2", "Go to profile 3", "Go to profile 4", "Go to profile 5"
        };
        // data is [kind] for navigation, [2, profileId] for a fixed target.
        public static readonly byte[][] Data = {
            new byte[] { 0x04 }, new byte[] { 0x03 },
            new byte[] { 0x05 }, new byte[] { 0x06 },
            new byte[] { 0x02, 1 }, new byte[] { 0x02, 2 }, new byte[] { 0x02, 3 },
            new byte[] { 0x02, 4 }, new byte[] { 0x02, 5 }
        };
    }

    public static class LightingActions
    {
        public static readonly string[] Names = { "Toggle lighting", "Lighting on", "Lighting off" };
        public static readonly byte[] Ids = { 0x01, 0x02, 0x03 };
    }

    // RAZER_KEY codes for the "Razer key" / power-key outputs (subset that makes sense here).
    public static class PowerActions
    {
        public static readonly string[] Names = {
            "System sleep", "Toggle low power mode", "Display brightness up", "Display brightness down",
            "Keyboard backlight up", "Keyboard backlight down", "Game mode", "Macro record",
            "Print screen", "HyperShift"
        };
        public static readonly byte[] Ids = { 0x4C, 0x0B, 0x06, 0x07, 0x08, 0x09, 0x03, 0x04, 0x46, 0x59 };
    }

    public static class Keys
    {
        public static readonly Dictionary<string, byte> Map = new Dictionary<string, byte>();
        public static readonly List<string> Order = new List<string>();

        static void Add(string name, byte usage)
        {
            Map[name] = usage;
            Order.Add(name);
        }

        static Keys()
        {
            string letters = "abcdefghijklmnopqrstuvwxyz";
            for (int i = 0; i < letters.Length; i++) Add(letters[i].ToString(), (byte)(0x04 + i));
            string digits = "1234567890";
            for (int i = 0; i < digits.Length; i++) Add(digits[i].ToString(), (byte)(0x1E + i));
            Add("enter", 0x28); Add("esc", 0x29); Add("backspace", 0x2A); Add("tab", 0x2B);
            Add("space", 0x2C); Add("minus", 0x2D); Add("equal", 0x2E); Add("lbracket", 0x2F);
            Add("rbracket", 0x30); Add("backslash", 0x31); Add("semicolon", 0x33);
            Add("quote", 0x34); Add("grave", 0x35); Add("comma", 0x36); Add("dot", 0x37);
            Add("slash", 0x38); Add("capslock", 0x39);
            for (int i = 1; i <= 12; i++) Add("f" + i, (byte)(0x3A + i - 1));
            // F13..F24 (HID 0x68..0x73) are the spare keys Rz3 uses as host-action triggers:
            // the firmware accepts them (echo verified), Windows does not bind them, so a button
            // can carry one and Rz3's engine turns it into any action at all.
            for (int i = 13; i <= 24; i++) Add("f" + i, (byte)(0x68 + i - 13));
            Add("printscreen", 0x46); Add("scrolllock", 0x47); Add("pause", 0x48);
            Add("insert", 0x49); Add("home", 0x4A); Add("pageup", 0x4B); Add("delete", 0x4C);
            Add("end", 0x4D); Add("pagedown", 0x4E); Add("right", 0x4F); Add("left", 0x50);
            Add("down", 0x51); Add("up", 0x52);
        }
    }

    public static class Media
    {
        public static readonly string[] Names = {
            "Volume up", "Volume down", "Mute", "Play / Pause", "Next track", "Previous track",
            "Stop", "Eject"
        };
        public static readonly byte[] Usages = { 0xE9, 0xEA, 0xE2, 0xCD, 0xB5, 0xB6, 0xB7, 0xB8 };
    }
}
