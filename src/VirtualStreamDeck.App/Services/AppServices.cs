using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace VirtualStreamDeck.App.Services;

/// <summary>"Start with Windows" via HKCU\...\Run.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VirtualStreamDeck";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry may be locked down by policy; the checkbox simply has no effect then.
        }
    }
}

/// <summary>Only one instance runs. Starting it again just shows the existing window.</summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\VirtualStreamDeck.Instance";
    private const string EventName = @"Local\VirtualStreamDeck.Show";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    public static bool TryAcquire(Action onActivateRequest)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(EventName).Set(); } catch { /* first instance is closing */ }
            return false;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var thread = new Thread(() =>
        {
            while (_showEvent.WaitOne()) onActivateRequest();
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };
        thread.Start();
        return true;
    }
}

internal static class ShellHelper
{
    public static void OpenFolder(string path)
    {
        try { System.IO.Directory.CreateDirectory(path); } catch { /* ignore */ }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
