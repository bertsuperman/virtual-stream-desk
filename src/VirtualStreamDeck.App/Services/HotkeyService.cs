using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Mac;

namespace VirtualStreamDeck.App.Services;

/// <summary>Global hotkeys of the visible buttons of the active profile.</summary>
internal abstract class HotkeyService : IDisposable
{
    private List<(string ButtonId, Hotkey Hotkey)> _desired = new();
    private readonly HashSet<string> _failed = new();
    private int _suspendCount;

    public static HotkeyService Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsHotkeys();
        if (OperatingSystem.IsMacOS()) return new MacHotkeys();
        return new NoHotkeys();
    }

    /// <summary>Raised on the UI thread with the id of the button whose hotkey was pressed.</summary>
    public event Action<string>? Pressed;

    /// <summary>Buttons whose hotkey is already taken by another application.</summary>
    public IReadOnlySet<string> Failed => _failed;

    public void Apply(DeckConfig config)
    {
        _desired = new List<(string ButtonId, Hotkey Hotkey)>();
        foreach (var b in config.VisibleButtons)
            if (Hotkey.TryParse(b.Hotkey, out var hk)) _desired.Add((b.Id, hk));

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
            if (!Register(buttonId, hotkey)) _failed.Add(buttonId);
    }

    protected abstract bool Register(string buttonId, Hotkey hotkey);
    protected abstract void UnregisterAll();

    protected void Raise(string buttonId) => Dispatcher.UIThread.Post(() => Pressed?.Invoke(buttonId));

    public void Dispose() => UnregisterAll();

    private sealed class NoHotkeys : HotkeyService
    {
        protected override bool Register(string buttonId, Hotkey hotkey) => true; // nothing to collide with
        protected override void UnregisterAll() { }
    }
}

/// <summary>RegisterHotKey on a hidden message-only window (the UI framework's message loop delivers WM_HOTKEY).</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsHotkeys : HotkeyService
{
    private static WindowsHotkeys? _instance;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, string> _registered = new();
    private int _nextId = 1;

    public WindowsHotkeys()
    {
        _instance = this;
        var hInstance = Win32.GetModuleHandle(null);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = hInstance,
            lpszClassName = "VirtualStreamDeck.Hotkeys",
        };
        Win32.RegisterClassEx(ref wc);
        _hwnd = Win32.CreateWindowEx(0, wc.lpszClassName, "", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    protected override bool Register(string buttonId, Hotkey hotkey)
    {
        if (_hwnd == IntPtr.Zero) return false;
        int id = _nextId++;
        if (_nextId > 0xBFFF) _nextId = 1; // valid range for application hotkey ids
        if (!Win32.RegisterHotKey(_hwnd, id, (uint)hotkey.Modifiers | Win32.MOD_NOREPEAT, (uint)hotkey.VirtualKey)) return false;
        _registered[id] = buttonId;
        return true;
    }

    protected override void UnregisterAll()
    {
        foreach (var id in _registered.Keys) Win32.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_HOTKEY && _instance != null && _instance._registered.TryGetValue(wParam.ToInt32(), out var buttonId))
        {
            _instance.Raise(buttonId);
            return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}

/// <summary>Carbon RegisterEventHotKey: still the standard way for global hotkeys on macOS, no permission needed.</summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacHotkeys : HotkeyService
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint kEventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint kEventHotKeyPressed = 5;
    private const uint kEventParamDirectObject = 0x2D2D2D2D; // '----'
    private const uint typeEventHotKeyID = 0x686B6964; // 'hkid'
    private const uint Signature = 0x56534468; // 'VSDh'

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint numTypes, EventTypeSpec[] list, IntPtr userData, out IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKeyRef);

    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(IntPtr evt, uint name, uint desiredType, IntPtr actualType, nuint bufferSize,
        IntPtr actualSize, out EventHotKeyID data);

    private static MacHotkeys? _instance;
    private readonly Dictionary<uint, (string ButtonId, IntPtr Ref)> _registered = new();
    private uint _nextId = 1;
    private bool _handlerInstalled;

    public MacHotkeys() => _instance = this;

    private void EnsureHandler()
    {
        if (_handlerInstalled) return;
        var spec = new[] { new EventTypeSpec { EventClass = kEventClassKeyboard, EventKind = kEventHotKeyPressed } };
        var fn = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&OnHotKey;
        _handlerInstalled = InstallEventHandler(GetApplicationEventTarget(), fn, 1, spec, IntPtr.Zero, out _) == 0;
    }

    protected override bool Register(string buttonId, Hotkey hotkey)
    {
        if (MacKeys.ToMac(hotkey.VirtualKey) is not { } keyCode) return false;
        EnsureHandler();
        uint id = _nextId++;
        int status = RegisterEventHotKey(keyCode, MacKeys.CarbonModifiers(hotkey.Modifiers),
            new EventHotKeyID { Signature = Signature, Id = id }, GetApplicationEventTarget(), 0, out var hotKeyRef);
        if (status != 0) return false;
        _registered[id] = (buttonId, hotKeyRef);
        return true;
    }

    protected override void UnregisterAll()
    {
        foreach (var (_, r) in _registered.Values) UnregisterEventHotKey(r);
        _registered.Clear();
    }

    [UnmanagedCallersOnly]
    private static int OnHotKey(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        if (GetEventParameter(theEvent, kEventParamDirectObject, typeEventHotKeyID, IntPtr.Zero,
                (nuint)sizeof(EventHotKeyID), IntPtr.Zero, out var hk) == 0 &&
            hk.Signature == Signature && _instance != null && _instance._registered.TryGetValue(hk.Id, out var entry))
        {
            _instance.Raise(entry.ButtonId);
            return 0; // noErr
        }
        return -9874; // eventNotHandledErr
    }
}
