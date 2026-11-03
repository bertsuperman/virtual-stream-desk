namespace VirtualStreamDeck.Core.Mac;

/// <summary>
/// Hotkeys are stored with Windows virtual-key codes on every platform (config.json stays the same).
/// On a Mac they are translated to macOS key codes (kVK_*, Carbon/HIToolbox Events.h);
/// the Win modifier is ⌘ Command and Alt is ⌥ Option.
/// </summary>
public static class MacKeys
{
    // Modifier masks for Carbon RegisterEventHotKey.
    public const uint CarbonCmd = 0x0100, CarbonShift = 0x0200, CarbonOption = 0x0800, CarbonControl = 0x1000;

    // Modifier flags for CGEventSetFlags.
    public const ulong CgShift = 0x20000, CgControl = 0x40000, CgOption = 0x80000, CgCommand = 0x100000;

    private static readonly Dictionary<int, ushort> VkToMac = new()
    {
        [0x41] = 0x00, [0x53] = 0x01, [0x44] = 0x02, [0x46] = 0x03, [0x48] = 0x04, [0x47] = 0x05, // A S D F H G
        [0x5A] = 0x06, [0x58] = 0x07, [0x43] = 0x08, [0x56] = 0x09, [0x42] = 0x0B, [0x51] = 0x0C, // Z X C V B Q
        [0x57] = 0x0D, [0x45] = 0x0E, [0x52] = 0x0F, [0x59] = 0x10, [0x54] = 0x11,                 // W E R Y T
        [0x4F] = 0x1F, [0x55] = 0x20, [0x49] = 0x22, [0x50] = 0x23, [0x4C] = 0x25, [0x4A] = 0x26, // O U I P L J
        [0x4B] = 0x28, [0x4E] = 0x2D, [0x4D] = 0x2E,                                               // K N M
        [0x31] = 0x12, [0x32] = 0x13, [0x33] = 0x14, [0x34] = 0x15, [0x36] = 0x16, [0x35] = 0x17, // 1 2 3 4 6 5
        [0x39] = 0x19, [0x37] = 0x1A, [0x38] = 0x1C, [0x30] = 0x1D,                                // 9 7 8 0
        [0xBB] = 0x18, [0xBD] = 0x1B, [0xDD] = 0x1E, [0xDB] = 0x21, [0xDE] = 0x27, [0xBA] = 0x29, // = - ] [ ' ;
        [0xDC] = 0x2A, [0xBC] = 0x2B, [0xBF] = 0x2C, [0xBE] = 0x2F, [0xC0] = 0x32, [0xE2] = 0x0A, // \ , / . ` §
        [0x0D] = 0x24, [0x09] = 0x30, [0x20] = 0x31, [0x08] = 0x33, [0x1B] = 0x35, [0x14] = 0x39, // Return Tab Space ⌫ Esc Caps
        [0x2E] = 0x75, [0x2D] = 0x72, [0x24] = 0x73, [0x23] = 0x77, [0x21] = 0x74, [0x22] = 0x79, // ⌦ Help/Ins Home End PgUp PgDn
        [0x25] = 0x7B, [0x27] = 0x7C, [0x28] = 0x7D, [0x26] = 0x7E,                                // arrows
        [0x70] = 0x7A, [0x71] = 0x78, [0x72] = 0x63, [0x73] = 0x76, [0x74] = 0x60, [0x75] = 0x61, // F1–F6
        [0x76] = 0x62, [0x77] = 0x64, [0x78] = 0x65, [0x79] = 0x6D, [0x7A] = 0x67, [0x7B] = 0x6F, // F7–F12
        [0x7C] = 0x69, [0x7D] = 0x6B, [0x7E] = 0x71, [0x7F] = 0x6A, [0x80] = 0x40, [0x81] = 0x4F, // F13–F18
        [0x82] = 0x50, [0x83] = 0x5A,                                                              // F19 F20
        [0x60] = 0x52, [0x61] = 0x53, [0x62] = 0x54, [0x63] = 0x55, [0x64] = 0x56,                 // keypad 0–4
        [0x65] = 0x57, [0x66] = 0x58, [0x67] = 0x59, [0x68] = 0x5B, [0x69] = 0x5C,                 // keypad 5–9
        [0x6A] = 0x43, [0x6B] = 0x45, [0x6D] = 0x4E, [0x6E] = 0x41, [0x6F] = 0x4B, [0x90] = 0x47, // keypad * + - . / Clear
    };

    private static readonly Dictionary<ushort, int> MacToVk = VkToMac.ToDictionary(p => p.Value, p => p.Key);

    /// <summary>The macOS key code, or null for keys a Mac keyboard doesn't have (F21–F24, PrintScreen, …).</summary>
    public static ushort? ToMac(int virtualKey) => VkToMac.TryGetValue(virtualKey, out var code) ? code : null;

    public static int? FromMac(ushort macKeyCode) => MacToVk.TryGetValue(macKeyCode, out var vk) ? vk : null;

    public static uint CarbonModifiers(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Ctrl) ? CarbonControl : 0) |
        (m.HasFlag(HotkeyModifiers.Alt) ? CarbonOption : 0) |
        (m.HasFlag(HotkeyModifiers.Shift) ? CarbonShift : 0) |
        (m.HasFlag(HotkeyModifiers.Win) ? CarbonCmd : 0);

    public static ulong CgFlags(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Ctrl) ? CgControl : 0) |
        (m.HasFlag(HotkeyModifiers.Alt) ? CgOption : 0) |
        (m.HasFlag(HotkeyModifiers.Shift) ? CgShift : 0) |
        (m.HasFlag(HotkeyModifiers.Win) ? CgCommand : 0);

    /// <summary>How a hotkey reads on a Mac: "⌃⌥⇧⌘K" order, e.g. Ctrl+Win+K → "⌃⌘K".</summary>
    public static string Display(Hotkey hotkey)
    {
        var s = "";
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) s += "⌃";
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) s += "⌥";
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) s += "⇧";
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) s += "⌘";
        return s + hotkey.VirtualKey switch
        {
            0x0D => "↩",
            0x08 => "⌫",
            0x2E => "⌦",
            0x1B => "⎋",
            0x09 => "⇥",
            0x20 => L.T("Medzera", "Space"),
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            _ => KeyNames.GetName(hotkey.VirtualKey),
        };
    }
}
