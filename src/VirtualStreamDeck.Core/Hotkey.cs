using System.Globalization;

namespace VirtualStreamDeck.Core;

/// <summary>Modifier flags. Values match the Win32 MOD_* constants used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A key combination such as "Ctrl+Alt+1" or "F13".
/// The key is stored as a Windows virtual-key code so Core does not depend on WPF.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public bool HasModifiers => Modifiers != HotkeyModifiers.None;

    /// <summary>True when the main key itself is a modifier (Shift, Ctrl, ...). Such a hotkey is invalid.</summary>
    public bool IsModifierOnly => KeyNames.IsModifier(VirtualKey);

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyNames.GetName(VirtualKey));
        return string.Join("+", parts);
    }

    public static Hotkey Parse(string text) =>
        TryParse(text, out var hk) ? hk : throw new FormatException($"Neplatná skratka: '{text}'");

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0)) return false;

        var mods = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var m = ParseModifier(parts[i]);
            if (m == null) return false;
            mods |= m.Value;
        }

        if (!KeyNames.TryGetVirtualKey(parts[^1], out int vk)) return false;
        if (KeyNames.IsModifier(vk)) return false;

        hotkey = new Hotkey(mods, vk);
        return true;
    }

    private static HotkeyModifiers? ParseModifier(string s) => s.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "ctl" => HotkeyModifiers.Ctrl,
        "alt" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" or "meta" => HotkeyModifiers.Win,
        _ => null,
    };
}

/// <summary>Human readable names for Windows virtual-key codes.</summary>
public static class KeyNames
{
    private static readonly Dictionary<int, string> VkToName = new();
    private static readonly Dictionary<string, int> NameToVk = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<int> Modifiers = new()
    {
        0x10, 0x11, 0x12,       // Shift, Control, Menu (Alt)
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, // L/R Shift, Ctrl, Alt
        0x5B, 0x5C,             // L/R Win
    };

    static KeyNames()
    {
        Add(0x08, "Backspace", "Back");
        Add(0x09, "Tab");
        Add(0x0D, "Enter", "Return");
        Add(0x13, "Pause", "Break");
        Add(0x14, "CapsLock", "Capital");
        Add(0x1B, "Escape", "Esc");
        Add(0x20, "Space");
        Add(0x21, "PageUp", "PgUp", "Prior");
        Add(0x22, "PageDown", "PgDn", "Next");
        Add(0x23, "End");
        Add(0x24, "Home");
        Add(0x25, "Left");
        Add(0x26, "Up");
        Add(0x27, "Right");
        Add(0x28, "Down");
        Add(0x2C, "PrintScreen", "PrtSc", "Snapshot");
        Add(0x2D, "Insert", "Ins");
        Add(0x2E, "Delete", "Del");
        for (int i = 0; i <= 9; i++) Add(0x30 + i, i.ToString(CultureInfo.InvariantCulture), "D" + i);
        for (char c = 'A'; c <= 'Z'; c++) Add(c, c.ToString());
        Add(0x5D, "Apps", "ContextMenu");
        for (int i = 0; i <= 9; i++) Add(0x60 + i, "NumPad" + i, "Num" + i);
        Add(0x6A, "Multiply", "NumPadMultiply");
        Add(0x6B, "Add", "NumPadAdd");
        Add(0x6D, "Subtract", "NumPadSubtract");
        Add(0x6E, "Decimal", "NumPadDecimal");
        Add(0x6F, "Divide", "NumPadDivide");
        for (int i = 1; i <= 24; i++) Add(0x6F + i, "F" + i);
        Add(0x90, "NumLock");
        Add(0x91, "ScrollLock", "Scroll");
        Add(0xAD, "VolumeMute");
        Add(0xAE, "VolumeDown");
        Add(0xAF, "VolumeUp");
        Add(0xB0, "MediaNext");
        Add(0xB1, "MediaPrev", "MediaPrevious");
        Add(0xB2, "MediaStop");
        Add(0xB3, "MediaPlayPause");
        Add(0xBA, "Semicolon", "Oem1");
        Add(0xBB, "Plus", "OemPlus", "Equals");
        Add(0xBC, "Comma", "OemComma");
        Add(0xBD, "Minus", "OemMinus");
        Add(0xBE, "Period", "OemPeriod");
        Add(0xBF, "Slash", "Oem2");
        Add(0xC0, "Backtick", "Oem3", "Tilde");
        Add(0xDB, "OpenBracket", "Oem4");
        Add(0xDC, "Backslash", "Oem5");
        Add(0xDD, "CloseBracket", "Oem6");
        Add(0xDE, "Quote", "Oem7");
        Add(0xE2, "Oem102");
        Add(0x10, "Shift");
        Add(0x11, "Control");
        Add(0x12, "Menu");
        Add(0xA0, "LeftShift");
        Add(0xA1, "RightShift");
        Add(0xA2, "LeftCtrl");
        Add(0xA3, "RightCtrl");
        Add(0xA4, "LeftAlt");
        Add(0xA5, "RightAlt");
        Add(0x5B, "LWin");
        Add(0x5C, "RWin");
    }

    private static void Add(int vk, string name, params string[] aliases)
    {
        VkToName[vk] = name;
        NameToVk[name] = vk;
        foreach (var a in aliases) NameToVk[a] = vk;
    }

    public static bool IsModifier(int vk) => Modifiers.Contains(vk);

    public static string GetName(int vk) =>
        VkToName.TryGetValue(vk, out var name) ? name : $"VK_0x{vk:X2}";

    public static bool TryGetVirtualKey(string name, out int vk)
    {
        if (NameToVk.TryGetValue(name, out vk)) return true;

        // Fallback for keys without a friendly name: "VK_0x1F" or "0x1F".
        var hex = name.StartsWith("VK_", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk) &&
            vk is > 0 and < 0xFF)
        {
            return true;
        }

        vk = 0;
        return false;
    }
}
