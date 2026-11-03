using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VirtualStreamDeck.Core.Windows;

/// <summary>Synthesizes keyboard input with SendInput.</summary>
[SupportedOSPlatform("windows")]
internal static class NativeInput
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;

    // An unassigned virtual key. Tapping it before releasing Alt/Win stops the
    // Start menu or an app's menu bar from opening on the synthetic key-up.
    private const ushort VK_DUMMY = 0xE8;

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    private static readonly int[] PhysicalModifiers =
    {
        0xA0, 0xA1, // L/R Shift
        0xA2, 0xA3, // L/R Ctrl
        0xA4, 0xA5, // L/R Alt
        0x5B, 0x5C, // L/R Win
    };

    private static readonly HashSet<int> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home arrows
        0x2C, 0x2D, 0x2E,                               // PrintScreen Insert Delete
        0x5B, 0x5C, 0x5D,                               // Win keys, Apps
        0x6F, 0x90,                                     // NumPad Divide, NumLock
        0xA3, 0xA5,                                     // Right Ctrl, Right Alt
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,       // Volume + media
    };

    /// <summary>Must be 40 on x64 (28 on x86), otherwise SendInput rejects the call.</summary>
    internal static int InputStructSize => Marshal.SizeOf<INPUT>();

    /// <summary>Presses modifiers, taps the key and releases everything in reverse order.</summary>
    public static void SendChord(Hotkey hotkey)
    {
        ReleaseHeldModifiers();

        var mods = new List<int>(4);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods.Add(VK_CONTROL);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods.Add(VK_MENU);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods.Add(VK_SHIFT);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) mods.Add(VK_LWIN);

        var inputs = new List<INPUT>();
        foreach (var m in mods) inputs.Add(Key(m, up: false));
        inputs.Add(Key(hotkey.VirtualKey, up: false));
        inputs.Add(Key(hotkey.VirtualKey, up: true));
        if (mods.Contains(VK_MENU) || mods.Contains(VK_LWIN))
        {
            inputs.Add(Key(VK_DUMMY, up: false));
            inputs.Add(Key(VK_DUMMY, up: true));
        }
        for (int i = mods.Count - 1; i >= 0; i--) inputs.Add(Key(mods[i], up: true));

        Send(inputs);
    }

    public static void TapKey(int vk) => Send(new List<INPUT> { Key(vk, false), Key(vk, true) });

    /// <summary>
    /// When an action is triggered by a global hotkey (e.g. Ctrl+Alt+1), the user is still
    /// holding Ctrl+Alt. Wait briefly for them to let go; if they don't, release them synthetically
    /// so they don't get mixed into the combo we are about to send.
    /// </summary>
    private static void ReleaseHeldModifiers()
    {
        var sw = Stopwatch.StartNew();
        while (HeldModifiers().Count > 0 && sw.ElapsedMilliseconds < 400) Thread.Sleep(10);

        var held = HeldModifiers();
        if (held.Count == 0) return;

        var inputs = new List<INPUT>();
        if (held.Any(vk => vk is 0xA4 or 0xA5 or 0x5B or 0x5C))
        {
            inputs.Add(Key(VK_DUMMY, false));
            inputs.Add(Key(VK_DUMMY, true));
        }
        foreach (var vk in held) inputs.Add(Key(vk, up: true));
        Send(inputs);
    }

    private static List<int> HeldModifiers() =>
        PhysicalModifiers.Where(vk => (GetAsyncKeyState(vk) & 0x8000) != 0).ToList();

    private static INPUT Key(int vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (ExtendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC),
                    dwFlags = flags,
                },
            },
        };
    }

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length)
        {
            var err = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            throw new InvalidOperationException(
                L.T("Klávesy sa nepodarilo odoslať. Ak cieľová aplikácia beží ako správca, spusti aj Stream Deck ako správca. (",
                    "Could not send the keys. If the target app runs as administrator, run Stream Deck as administrator too. (") + err + ")");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
