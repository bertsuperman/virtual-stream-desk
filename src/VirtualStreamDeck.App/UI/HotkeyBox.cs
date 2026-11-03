using System;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls;
using VirtualStreamDeck.App.Services;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Mac;

namespace VirtualStreamDeck.App.UI;

/// <summary>Text box that records a key combination when the user presses it.</summary>
internal sealed class HotkeyBox : TextBox
{
    private Hotkey? _hotkey;

    protected override Type StyleKeyOverride => typeof(TextBox); // keep the normal text box look

    public HotkeyBox()
    {
        IsReadOnly = true;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Hand);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, _) => UpdateText(), RoutingStrategies.Tunnel);
        LostFocus += (_, _) => UpdateText();
        UpdateText();
    }

    public event EventHandler? HotkeyChanged;

    public Hotkey? Hotkey
    {
        get => _hotkey;
        set
        {
            if (_hotkey == value) return;
            _hotkey = value;
            UpdateText();
            HotkeyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>How a hotkey reads on this computer (⌃⌥⇧⌘ on a Mac).</summary>
    public static string Describe(Hotkey hotkey) => AppInfo.IsMac ? MacKeys.Display(hotkey) : hotkey.ToString();

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var mods = Modifiers(e.KeyModifiers);

        // Plain Tab still moves focus so the dialog stays usable from the keyboard.
        if (e.Key == Key.Tab && mods == HotkeyModifiers.None) return;
        e.Handled = true;

        if (IsModifierKey(e.Key))
        {
            Text = DescribeModifiers(mods) + "…";
            return;
        }

        if (ToVirtualKey(e.Key) is not { } vk) return;
        Hotkey = new Hotkey(mods, vk);
        UpdateText();
    }

    private void UpdateText() =>
        Text = _hotkey is { } hk ? Describe(hk) : L.T("Klikni sem a stlač skratku…", "Click here and press a hotkey…");

    private static HotkeyModifiers Modifiers(KeyModifiers k)
    {
        var m = HotkeyModifiers.None;
        if (k.HasFlag(KeyModifiers.Control)) m |= HotkeyModifiers.Ctrl;
        if (k.HasFlag(KeyModifiers.Alt)) m |= HotkeyModifiers.Alt;
        if (k.HasFlag(KeyModifiers.Shift)) m |= HotkeyModifiers.Shift;
        if (k.HasFlag(KeyModifiers.Meta)) m |= HotkeyModifiers.Win; // Windows key / ⌘
        return m;
    }

    private static string DescribeModifiers(HotkeyModifiers m)
    {
        if (AppInfo.IsMac)
            return (m.HasFlag(HotkeyModifiers.Ctrl) ? "⌃" : "") + (m.HasFlag(HotkeyModifiers.Alt) ? "⌥" : "") +
                   (m.HasFlag(HotkeyModifiers.Shift) ? "⇧" : "") + (m.HasFlag(HotkeyModifiers.Win) ? "⌘" : "");
        var s = "";
        if (m.HasFlag(HotkeyModifiers.Ctrl)) s += "Ctrl+";
        if (m.HasFlag(HotkeyModifiers.Alt)) s += "Alt+";
        if (m.HasFlag(HotkeyModifiers.Shift)) s += "Shift+";
        if (m.HasFlag(HotkeyModifiers.Win)) s += "Win+";
        return s;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>Avalonia key → Windows virtual-key code (what config.json stores on every platform).</summary>
    public static int? ToVirtualKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return 0x41 + (key - Key.A);
        if (key >= Key.D0 && key <= Key.D9) return 0x30 + (key - Key.D0);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return 0x60 + (key - Key.NumPad0);
        if (key >= Key.F1 && key <= Key.F24) return 0x70 + (key - Key.F1);
        return key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Enter => 0x0D,
            Key.Pause => 0x13,
            Key.CapsLock => 0x14,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.PrintScreen => 0x2C,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Apps => 0x5D,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.NumLock => 0x90,
            Key.Scroll => 0x91,
            Key.VolumeMute => 0xAD,
            Key.VolumeDown => 0xAE,
            Key.VolumeUp => 0xAF,
            Key.MediaNextTrack => 0xB0,
            Key.MediaPreviousTrack => 0xB1,
            Key.MediaStop => 0xB2,
            Key.MediaPlayPause => 0xB3,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            Key.OemBackslash => 0xE2,
            _ => null,
        };
    }
}
