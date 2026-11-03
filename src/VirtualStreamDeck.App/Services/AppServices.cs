using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace VirtualStreamDeck.App.Services;

internal static class AppInfo
{
    public const string ProductName = "Virtual Stream Deck";

    /// <summary>e.g. "0.2.14" (CI sets it from the build number; local builds are "0.2.0").</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";
        int plus = v.IndexOf('+'); // strip "+<git sha>"
        return plus >= 0 ? v[..plus] : v;
    }
}

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

/// <summary>
/// Only one instance runs.
/// - Starting the same exe again just shows the running window.
/// - Starting a different build (newer download, other path) replaces the running one,
///   so "I downloaded the new version but still see the old one" can't happen.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\VirtualStreamDeck.Instance";
    private const string ShowEventName = @"Local\VirtualStreamDeck.Show";
    private const string ExitEventName = @"Local\VirtualStreamDeck.Exit";

    private static readonly string InfoPath = Path.Combine(Path.GetTempPath(), "VirtualStreamDeck.instance");

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static EventWaitHandle? _exitEvent;

    /// <summary>Returns false if another instance of the same build is already running (it was asked to show itself).</summary>
    public static bool TryAcquire(Action onShowRequest, Action onExitRequest)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            var running = ReadInfo();
            bool sameBuild = running != null &&
                             running.Value.Version == AppInfo.Version &&
                             string.Equals(running.Value.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);

            if (sameBuild || !TakeOver())
            {
                Signal(ShowEventName);
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }

        WriteInfo();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        Listen(_showEvent, onShowRequest, "ShowListener");
        Listen(_exitEvent, onExitRequest, "ExitListener");
        return true;
    }

    public static void Release()
    {
        try
        {
            if (ReadInfo()?.Pid == Environment.ProcessId) File.Delete(InfoPath);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Asks the running instance to quit (or kills an old build that can't be asked) and takes the mutex.</summary>
    private static bool TakeOver()
    {
        Signal(ExitEventName);
        if (WaitForMutex(TimeSpan.FromSeconds(4))) return true;

        // Builds before 0.2 don't listen for the exit request.
        foreach (var p in FindOtherInstances())
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch
            {
                // already gone or not ours
            }
        }
        return WaitForMutex(TimeSpan.FromSeconds(3));
    }

    private static bool WaitForMutex(TimeSpan timeout)
    {
        try
        {
            return _mutex!.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // previous owner exited without releasing: we own it now
        }
    }

    private static Process[] FindOtherInstances() =>
        Process.GetProcesses()
            .Where(p => p.Id != Environment.ProcessId &&
                        p.ProcessName.StartsWith("VirtualStreamDeck", StringComparison.OrdinalIgnoreCase) &&
                        IsOurProduct(p))
            .ToArray();

    private static bool IsOurProduct(Process p)
    {
        try
        {
            return p.MainModule?.FileVersionInfo.ProductName == AppInfo.ProductName;
        }
        catch
        {
            return false;
        }
    }

    private static void Signal(string name)
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(name);
            e.Set();
        }
        catch
        {
            // the other instance is not listening (yet / anymore)
        }
    }

    private static void Listen(EventWaitHandle handle, Action callback, string name)
    {
        var thread = new Thread(() =>
        {
            while (handle.WaitOne()) callback();
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
    }

    private static void WriteInfo()
    {
        try
        {
            File.WriteAllLines(InfoPath, new[]
            {
                Environment.ProcessId.ToString(),
                AppInfo.Version,
                Environment.ProcessPath ?? "",
            });
        }
        catch
        {
            // not critical: a later launch will simply take over
        }
    }

    private static (int Pid, string Version, string Path)? ReadInfo()
    {
        try
        {
            var lines = File.ReadAllLines(InfoPath);
            if (lines.Length >= 3 && int.TryParse(lines[0], out int pid)) return (pid, lines[1], lines[2]);
        }
        catch
        {
            // missing or unreadable
        }
        return null;
    }
}

internal static class ShellHelper
{
    public static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* ignore */ }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
