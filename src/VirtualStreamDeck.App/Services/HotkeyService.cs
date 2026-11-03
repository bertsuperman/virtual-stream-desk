using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.App.Services;

/// <summary>Registers global hotkeys (RegisterHotKey) on the main window and reports which button was triggered.</summary>
internal sealed class HotkeyService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _registered = new();
    private List<(string ButtonId, Hotkey Hotkey)> _desired = new();
    private readonly HashSet<string> _failed = new();
    private int _suspendCount;
    private int _nextId = 1;

    public HotkeyService(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
    }

    /// <summary>Raised on the UI thread with the id of the button whose hotkey was pressed.</summary>
    public event Action<string>? Pressed;

    /// <summary>Buttons whose hotkey is already taken by another application.</summary>
    public IReadOnlySet<string> Failed => _failed;

    public void Apply(DeckConfig config)
    {
        _desired = new List<(string ButtonId, Hotkey Hotkey)>();
        foreach (var b in config.VisibleButtons)
        {
            if (Hotkey.TryParse(b.Hotkey, out var hk)) _desired.Add((b.Id, hk));
        }

        if (_suspendCount == 0)
        {
            UnregisterAll();
            RegisterAll();
        }
    }

    /// <summary>Temporarily releases all hotkeys, e.g. while the user records a new one in the editor.</summary>
    public void Suspend()
    {
        if (_suspendCount++ == 0) UnregisterAll();
    }

    public void Resume()
    {
        if (_suspendCount == 0) return;
        if (--_suspendCount == 0) RegisterAll();
    }

    private void RegisterAll()
    {
        _failed.Clear();
        foreach (var (buttonId, hotkey) in _desired)
        {
            int id = _nextId++;
            if (_nextId > 0xBFFF) _nextId = 1; // valid range for application hotkey ids
            uint mods = (uint)hotkey.Modifiers | Win32.MOD_NOREPEAT;
            if (Win32.RegisterHotKey(_hwnd, id, mods, (uint)hotkey.VirtualKey))
                _registered[id] = buttonId;
            else
                _failed.Add(buttonId);
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys) Win32.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var buttonId))
        {
            handled = true;
            Pressed?.Invoke(buttonId);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
