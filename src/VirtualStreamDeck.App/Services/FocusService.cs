using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using VirtualStreamDeck.Core;
using VirtualStreamDeck.Core.Mac;

namespace VirtualStreamDeck.App.Services;

/// <summary>
/// Key buttons must type into the app the user works in, not into the deck.
/// Windows: the deck window never activates (WS_EX_NOACTIVATE).
/// macOS: windows can't refuse activation, so the app the user was in is remembered and
/// re-activated right before the keys are sent.
/// </summary>
internal sealed class FocusService
{
    private int _previousPid;
    private DispatcherTimer? _macTimer;

    public void Attach(Window window)
    {
        if (OperatingSystem.IsMacOS())
        {
            _macTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _macTimer.Tick += (_, _) => RememberFrontApp();
            _macTimer.Start();
        }
    }

    /// <summary>Windows: on = clicks don't take the focus (off while the side panel with text boxes is open).</summary>
    public static void SetNoActivate(Window window, bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex = on ? ex | Win32.WS_EX_NOACTIVATE : ex & ~Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Before sending keys: give the focus back to the app the user was working in.</summary>
    public async Task PrepareForKeysAsync()
    {
        if (!OperatingSystem.IsMacOS() || _previousPid == 0) return;
        bool reactivated = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsOurAppActive()) return false;
            return ActivatePid(_previousPid);
        });
        if (reactivated) await Task.Delay(150); // let the other app become key before the keys arrive
    }

    private void RememberFrontApp()
    {
        try
        {
            var pool = MacNative.objc_autoreleasePoolPush();
            try
            {
                var ws = MacNative.Send(MacNative.Class("NSWorkspace"), MacNative.Sel("sharedWorkspace"));
                var front = MacNative.Send(ws, MacNative.Sel("frontmostApplication"));
                if (front == IntPtr.Zero) return;
                int pid = MacNative.SendInt(front, MacNative.Sel("processIdentifier"));
                if (pid != Environment.ProcessId && pid > 0) _previousPid = pid;
            }
            finally
            {
                MacNative.objc_autoreleasePoolPop(pool);
            }
        }
        catch
        {
            _macTimer?.Stop(); // bindings not available: give up quietly
        }
    }

    private static bool IsOurAppActive()
    {
        var app = MacNative.Send(MacNative.Class("NSApplication"), MacNative.Sel("sharedApplication"));
        return app != IntPtr.Zero && MacNative.SendBool(app, MacNative.Sel("isActive"));
    }

    private static bool ActivatePid(int pid)
    {
        var pool = MacNative.objc_autoreleasePoolPush();
        try
        {
            var running = MacNative.Send(MacNative.Class("NSRunningApplication"),
                MacNative.Sel("runningApplicationWithProcessIdentifier:"), (nuint)pid);
            if (running == IntPtr.Zero) return false;
            // NSApplicationActivateIgnoringOtherApps = 1 << 1
            MacNative.Send(running, MacNative.Sel("activateWithOptions:"), (nuint)2);
            return true;
        }
        finally
        {
            MacNative.objc_autoreleasePoolPop(pool);
        }
    }
}

/// <summary>Runs actions; on a Mac key and media buttons first hand the focus back to the user's app.</summary>
internal sealed class FocusAwareExecutor(IActionExecutor inner, FocusService focus) : IActionExecutor
{
    public async Task ExecuteAsync(DeckAction action)
    {
        if (action is KeysAction) await focus.PrepareForKeysAsync();
        await inner.ExecuteAsync(action);
    }
}
