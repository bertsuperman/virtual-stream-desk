using System;
using System.Windows.Controls;
using System.Windows.Input;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App.Controls;

/// <summary>Text box that records a key combination when the user presses it.</summary>
public sealed class HotkeyBox : TextBox
{
    private const string Placeholder = "Klikni sem a stlač skratku…";
    private Hotkey? _hotkey;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        var mods = CurrentModifiers();

        // Plain Tab still moves focus so the dialog stays usable from the keyboard.
        if (key == Key.Tab && mods == HotkeyModifiers.None)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        if (IsModifierKey(key))
        {
            Text = Describe(mods) + "…";
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        Hotkey = new Hotkey(mods, vk);
        UpdateText();
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Only modifiers were pressed and released: restore the previous value.
        if (CurrentModifiers() == HotkeyModifiers.None) UpdateText();
        base.OnPreviewKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        UpdateText();
        base.OnLostKeyboardFocus(e);
    }

    private void UpdateText() => Text = _hotkey?.ToString() ?? Placeholder;

    private static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        var k = Keyboard.Modifiers;
        if (k.HasFlag(ModifierKeys.Control)) m |= HotkeyModifiers.Ctrl;
        if (k.HasFlag(ModifierKeys.Alt)) m |= HotkeyModifiers.Alt;
        if (k.HasFlag(ModifierKeys.Shift)) m |= HotkeyModifiers.Shift;
        if (k.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            m |= HotkeyModifiers.Win;
        return m;
    }

    private static string Describe(HotkeyModifiers m)
    {
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
}
